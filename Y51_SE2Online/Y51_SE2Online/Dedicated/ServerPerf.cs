using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using HarmonyLib;
using Keen.Game2;
using Keen.Game2.Game.SessionComponents;
using Keen.Game2.Simulation.GameSystems.GameTimes;
using Keen.VRage.Library.Utils;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.Dedicated;

/// <summary>
/// Headless server speed: missed ticks are caught up (see <see cref="TickPacer"/>), Keen's worker pool uses every core,
/// the process runs above normal priority, and a 30 s line reports world time against the wall clock.
/// </summary>
internal static class ServerPerf
{
	private static WaitForTargetFrameRate? _waiter;
	private static long _backlogFrames;
	private static long _droppedTicks, _caughtUpTicks, _lastRun;

	private static readonly AccessTools.FieldRef<WaitForTargetFrameRate, long> Target = AccessTools.FieldRefAccess<WaitForTargetFrameRate, long>("_targetTicks");
	private static readonly AccessTools.FieldRef<WaitForTargetFrameRate, AppTimer> Timer = AccessTools.FieldRefAccess<WaitForTargetFrameRate, AppTimer>("_timer");
	private static readonly AccessTools.FieldRef<WaitForTargetFrameRate, TimeSpan> Delay = AccessTools.FieldRefAccess<WaitForTargetFrameRate, TimeSpan>("_delta");

	private static int _stallMs, _stallEveryMs;
	private static long _nextStallMs;

	public static void Install(Harmony harmony, string[] args)
	{
		var config = DedicatedRuntime.Config;
		if (config.CatchUpMs > 0)
			harmony.Patch(AccessTools.Method(typeof(WaitForTargetFrameRate), nameof(WaitForTargetFrameRate.Wait)),
				prefix: new HarmonyMethod(typeof(ServerPerf), nameof(WaitPrefix)));
		try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.AboveNormal; }
		catch (Exception ex) { GameLog.Instance.Warn("dedicated: could not raise the process priority: " + ex.Message); }
		// Test rig: -y51online:perf-stall:500/5000 blocks the server frame 500 ms every 5 s.
		string stall = args.FirstOrDefault(a => a.StartsWith(MpOptions.Prefix + "perf-stall:", StringComparison.Ordinal));
		if (stall != null && stall.Substring((MpOptions.Prefix + "perf-stall:").Length).Split('/') is [var ms, var every]
			&& int.TryParse(ms, out _stallMs) && int.TryParse(every, out _stallEveryMs) && _stallMs > 0 && _stallEveryMs > 0)
			GameLog.Instance.Warn($"dedicated: test stall: the server frame blocks {_stallMs} ms every {_stallEveryMs} ms");
	}

	/// <summary>From the headless engine composition: the main loop's pacer and Keen's worker pool.</summary>
	public static void Configure(AppLoop loop, Keen.VRage.Core.EngineComponents.EngineBuilder engine)
	{
		var config = DedicatedRuntime.Config;
		_waiter = loop.Waiter;
		_backlogFrames = TickPacer.BacklogFrames(config.CatchUpMs, loop.TargetFrequency);
		if (config.WorkerThreads > 0) engine.Configure<Keen.VRage.Core.CoreConfigurations.ThreadingConfigurationObjectBuilder>().WorkerCount = config.WorkerThreads;
		GameLog.Instance.Info($"dedicated: {(config.WorkerThreads > 0 ? config.WorkerThreads.ToString() : "the game's own count of")} worker threads on {Environment.ProcessorCount} logical processors; "
			+ (config.CatchUpMs > 0 ? $"missed ticks are caught up to {config.CatchUpMs} ms behind" : "Keen's own pacing (no catch-up beyond 83 ms)"));
	}

	private static bool WaitPrefix(WaitForTargetFrameRate __instance)
	{
		if (!ReferenceEquals(__instance, _waiter)) return true;
		AppTimer timer = Timer(__instance);
		TimeSpan delay = Delay(__instance);
		if (delay != TimeSpan.Zero)
		{
			Delay(__instance) = TimeSpan.Zero;
			timer.AddElapsed(-delay);
		}
		long frame = __instance.TicksPerFrame, now = timer.ElapsedTicks;
		long target = TickPacer.NextTarget(Target(__instance), now, frame, _backlogFrames, out long dropped);
		if (dropped > 0) _droppedTicks += dropped;
		if (!__instance.WaitForInterval) target = now;
		Target(__instance) = target;
		if (target < now) _caughtUpTicks++;
		long runAt = __instance.WaitForInterval ? TickPacer.RunAt(target, _lastRun, frame) : now;
		long ahead = runAt - now;
		if (ahead > 0)
		{
			int sleepMs = (int)(ahead * 1000 / Stopwatch.Frequency) - 2;
			if (sleepMs > 0) Thread.Sleep(sleepMs);
			while (timer.ElapsedTicks < runAt) Thread.SpinWait(20);
		}
		_lastRun = Math.Max(now, runAt);
		return false;
	}

	private static long _windowStartMs, _windowTicks;
	private static double _windowWorldS, _worstMs;
	private static TimeSpan _windowCpu;

	public static void Tick(GameAppComponent app, ServerFrame? frame)
	{
		long now = MpClock.NowMs();
		if (_stallMs > 0 && app.Server != null)
		{
			if (_nextStallMs == 0) _nextStallMs = now + _stallEveryMs;
			else if (now >= _nextStallMs) { _nextStallMs += _stallEveryMs; Thread.Sleep(_stallMs); }
		}
		if (frame != null) { _windowTicks++; _worstMs = Math.Max(_worstMs, frame.FrameMs); }
		var clock = app.Server?.Entity.Get<WorldSessionComponent>()?.OwnedSession?.SessionComponents.TryGet<GameTimeSessionComponent>();
		if (clock == null) { _windowStartMs = 0; return; }
		double world = clock.FrameTime.TotalSeconds;
		if (_windowStartMs == 0) { Reset(now, world); return; }
		if (now - _windowStartMs < 30_000) return;
		double wall = (now - _windowStartMs) / 1000.0, advanced = world - _windowWorldS;
		TimeSpan cpu = Process.GetCurrentProcess().TotalProcessorTime;
		GameLog.Instance.Info(FormattableString.Invariant(
			$"perf: world time +{advanced:0.0} s over {wall:0.0} s wall (sim speed {advanced / wall:0.00}); {_windowTicks} ticks ({_windowTicks / wall:0.0}/s), {_caughtUpTicks} run late to catch up, {ServerProfiler.Ms(_droppedTicks) / 1000:0.0} s of backlog dropped; worst frame {_worstMs:0} ms; CPU {(cpu - _windowCpu).TotalSeconds / wall:0.0} of {Environment.ProcessorCount} logical processors"));
		Reset(now, world);
	}

	private static void Reset(long now, double world)
	{
		_windowStartMs = now;
		_windowWorldS = world;
		_windowTicks = _caughtUpTicks = _droppedTicks = 0;
		_worstMs = 0;
		_windowCpu = Process.GetCurrentProcess().TotalProcessorTime;
	}
}
