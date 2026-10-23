using System;
using HarmonyLib;
using Keen.Game2.Simulation.GameSystems.GameTimes;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.Library.Threading;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>
/// Keeps a player's game clock on the server's. SE2 4.0 copies the server's game time once at join, then each side
/// counts its own ticks: a server below 60 ticks/s falls behind the player, and whatever runs on game time disagrees
/// (the jetpack boost charge: the player's bar is full, the server is still charging, the boost never fires). The
/// server's time is sampled every 2 s; a player's clock that is behind jumps forward, one that is ahead is eased back
/// (at most 5% slower), never backwards.
/// </summary>
internal static class ClientClock
{
	private const long SampleMs = 2000;

	private const long SampleTimeoutMs = 10000;

	// Characters animate on game time while their bodies play back on frame time: a clock eased at up to 50% played every
	// animation half again as fast for a few frames after most samples (feet sliding). 5% is not visible.
	private const double MaxSlew = 0.05;

	// Far ahead (a server that stalled 40 s does not catch up; its players' clocks are 35 s ahead): 5% would take 12 min.
	// More than 5 s ahead, the clock runs at half speed until it is back within 5 s: about a minute for 35 s.
	private const double FarSlew = 0.5;
	private static readonly TimeSpan Far = TimeSpan.FromSeconds(5);

	private static GameTimeSessionComponent? _clock;

	private static bool _asking;

	// Our requests still unanswered: a reply that comes after its timeout (a host stall, a big autosave) is ours and
	// must not reach Keen's Init.
	private static int _pending;

	private static long _askedMs, _nextMs, _loggedMs;

	private static TimeSpan _sentRaw, _lastRaw;

	// The server's time at a local raw tick time, and how fast it runs against ours (ticks per tick).
	private static TimeSpan? _serverAt, _rawAt;

	private static double _rate = 1;

	/// <summary>The player's clock minus the server's at the last sample, in ms (positive: the player runs ahead).</summary>
	public static double LastErrorMs;

	/// <summary>Main menu: let go of the last world's clock component.</summary>
	internal static void Reset()
	{
		_clock = null;
		_asking = false;
		_pending = 0;
		_serverAt = _rawAt = null;
		_rate = 1;
	}

	public static void Install(Harmony harmony) => harmony.Patch(
		AccessTools.Method(typeof(GameTimeSessionComponent), "Init", new[] { typeof(GameTimeSessionComponentObjectBuilder) }),
		prefix: new HarmonyMethod(typeof(ClientClock), nameof(InitPrefix)));

	// Keen's own sync at join goes through untouched; our samples set the target instead of jumping the clock.
	private static bool InitPrefix(GameTimeSessionComponent __instance, GameTimeSessionComponentObjectBuilder ob)
	{
		// The wire copy carries no time provider; Keen's Init would null ours and every later game-time read would throw.
		if (ob.Valid && ob.TimeProvider == null) ob.TimeProvider = __instance._timeProvider;
		if (__instance != _clock || _pending == 0 || !ob.Valid || __instance._timeProvider == null) return true;
		_pending--;
		if (!_asking) return false;
		_asking = false;
		TimeSpan raw = __instance._timeProvider.FrameTime;
		// The server read its time about halfway through the round trip.
		TimeSpan mid = _sentRaw + (raw - _sentRaw) / 2;
		if (_serverAt is { } before && _rawAt is { } beforeRaw && mid - beforeRaw > TimeSpan.FromSeconds(1))
			_rate = Math.Clamp((ob.ElapsedTime - before) / (mid - beforeRaw), 0.25, 2.0);
		LastErrorMs = (__instance.FrameTime - (ob.ElapsedTime + (raw - mid) * _rate)).TotalMilliseconds;
		_serverAt = ob.ElapsedTime;
		_rawAt = mid;
		long now = Core.MpClock.NowMs();
		if (Math.Abs(LastErrorMs) > 250 && now - _loggedMs > 60000)
		{
			_loggedMs = now;
			GameLog.Instance.Info($"client clock: {LastErrorMs:0} ms off the server's (the server runs {_rate * 60:0.#} ticks/s against our 60); {(LastErrorMs < 0 ? "catching up" : "easing it back")}");
		}
		return false;
	}

	/// <summary>Main thread, while the player is in a server's world.</summary>
	public static void Tick(Session? session, long now)
	{
		var clock = session?.SessionComponents.TryGet<GameTimeSessionComponent>();
		if (clock != _clock)
		{
			_clock = clock;
			_asking = false;
			_pending = 0;
			_serverAt = _rawAt = null;
			_rate = 1;
			_lastRaw = default;
			_nextMs = now + SampleMs;
		}
		if (clock?._timeProvider == null) return;
		TimeSpan raw = clock._timeProvider.FrameTime;
		if (_asking && now - _askedMs > SampleTimeoutMs) _asking = false;
		if (!_asking && now >= _nextMs)
		{
			_nextMs = now + SampleMs;
			_askedMs = now;
			_asking = true;
			_pending++;
			_sentRaw = raw;
			clock.RequestDataFromServer().SkipWait();
		}
		TimeSpan step = raw - _lastRaw;
		_lastRaw = raw;
		if (_serverAt is not { } server || _rawAt is not { } at || step <= TimeSpan.Zero || step > TimeSpan.FromMilliseconds(250)) return;
		TimeSpan wanted = server + (raw - at) * _rate - raw;
		TimeSpan error = wanted - clock._elapsedTimeOffset;
		TimeSpan max = step * (error < -Far ? FarSlew : MaxSlew);
		// Behind (the clock stood still while the world loaded, 40 s on a big one): catch up at once, timers only end
		// sooner. Ahead: ease back, game time never runs backwards.
		if (error > TimeSpan.FromSeconds(1)) clock._elapsedTimeOffset = wanted;
		else clock._elapsedTimeOffset += error > max ? max : error < -max ? -max : error;
	}
}
