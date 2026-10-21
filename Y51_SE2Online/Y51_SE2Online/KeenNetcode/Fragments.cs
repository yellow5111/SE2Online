using System;
using System.Runtime.InteropServices;
using Keen.VRage.Core.Services.Data;
using Keen.VRage.Core.Services.GameSessions;
using Keen.VRage.Core.Services.Networking;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>Keen's own Steam connection with SE2 Online's framing, on both ends (see <see cref="FragmentLink{TPeer}"/>).</summary>
internal sealed class FramedSession : IPeerToPeerSession
{
	private readonly FragmentLink<GameServiceUser> _link;

	public IPeerToPeerSession Inner { get; }

	public FramedSession(IPeerToPeerSession inner, IMpLog log, Func<GameServiceUser, bool>? admitted = null)
	{
		Inner = inner;
		_link = new FragmentLink<GameServiceUser>((user, channel, reliability, frame) => inner.SendMessage(user, frame, channel, reliability), log, CloseConnection, admitted);
	}

	public event Action<GameServiceUser> ConnectionRequested
	{
		add => Inner.ConnectionRequested += value;
		remove => Inner.ConnectionRequested -= value;
	}

	public event Action<GameServiceUser, ConnectionFailureReason> PeerConnectionFailed
	{
		add => Inner.PeerConnectionFailed += value;
		remove => Inner.PeerConnectionFailed -= value;
	}

	public void Flush() => _link.Flush();

	private static bool _bufferRaised;

	/// <summary>Steam's default 512 KiB send buffer turns a world's first batches into a long retry queue; 32 MiB takes them at once.</summary>
	public static unsafe void RaiseSendBuffer(IMpLog log)
	{
		if (_bufferRaised) return;
		_bufferRaised = true;
		int bytes = 32 * 1024 * 1024;
		var key = Steamworks.ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendBufferSize;
		bool set = SetSteamInt(key, bytes);
		log.Info($"large messages: Steam send buffer {(set ? "raised to 32 MiB" : "left at Steam's default (refused)")}");
		// Steam paces each connection to its own default rate (a few hundred KiB/s): a world, a respawn ship or a busy base
		// queued behind it. SE2 Online's send rate settings (2.4's sendrate/sendratemin) apply to SE2 4.0's connection too.
		var options = Y51_SE2Online.NetHost.NetSession.Current?.Options;
		int min = options?.SendRateMinBytes ?? 1024 * 1024, max = Math.Max(min, options?.SendRateMaxBytes ?? 8 * 1024 * 1024);
		bool rate = SetSteamInt(Steamworks.ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMin, min)
			& SetSteamInt(Steamworks.ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax, max);
		log.Info(rate ? $"large messages: Steam send rate {min / 1024}..{max / 1024} KiB/s per connection" : "large messages: Steam refused the send rate; its default pacing applies");
	}

	private static unsafe bool SetSteamInt(Steamworks.ESteamNetworkingConfigValue key, int value)
	{
		return Y51_SE2Online.Dedicated.DedicatedRuntime.Requested
			? Steamworks.SteamGameServerNetworkingUtils.SetConfigValue(key, Steamworks.ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Global, IntPtr.Zero, Steamworks.ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32, (nint)(&value))
			: Steamworks.SteamNetworkingUtils.SetConfigValue(key, Steamworks.ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Global, IntPtr.Zero, Steamworks.ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32, (nint)(&value));
	}

	public bool AcceptConnection(GameServiceUser user) => Inner.AcceptConnection(user);

	public void CloseConnection(GameServiceUser user)
	{
		_link.Forget(user);
		Inner.CloseConnection(user);
	}

	public bool SendMessage(GameServiceUser user, ReadOnlySpan<byte> data, byte channel = 0, MessageReliability reliability = MessageReliability.Reliable)
		=> _link.Send(user, data, channel, reliability);

	private readonly object _readSync = new();

	// Keen's Steam session receives through one shared one-message buffer; the game reads channels 0-1 on its network job
	// and SE2 Online reads 120-122 on its own thread, so unserialized reads handed one thread the other's (or a freed)
	// message.
	public bool TryReadNextMessage(byte channel, out IPeerToPeerSession.Message message)
	{
		lock (_readSync)
		{
			while (Inner.TryReadNextMessage(channel, out var received))
			{
				byte[]? whole;
				GameServiceUser from = received.RemotePeer;
				MessageReliability reliability = received.Reliability;
				if (channel < Y51_SE2Online.Net.KeenLink.FirstChannel) JoinBytes.Add(received.GetContents().Length);
				try { whole = _link.Receive(from, channel, received.GetContents()); }
				finally { received.Dispose(); }
				if (whole == null) continue;
				nint buffer = Marshal.AllocHGlobal(Math.Max(1, whole.Length));
				Marshal.Copy(whole, 0, buffer, whole.Length);
				message = IPeerToPeerSession.CreateMessage(from, buffer, whole.Length, buffer, reliability, this);
				return true;
			}
		}
		message = default;
		return false;
	}

	void IPeerToPeerSession.DisposeMessage(nint messageHandle) => Marshal.FreeHGlobal(messageHandle);

	public void Dispose() => Inner.Dispose();
}

/// <summary>Channels something reads: the game's 0 (handshake) and 1 (replication), SE2 Online's three lanes.</summary>
internal static class GameChannels
{
	public static bool Read(byte channel) => channel <= 1 || channel >= Y51_SE2Online.Net.KeenLink.FirstChannel && channel < Y51_SE2Online.Net.KeenLink.FirstChannel + 3;
}

/// <summary>Bytes of the game's own traffic (the world, then its updates) this game has received, for the join progress bar.</summary>
internal static class JoinBytes
{
	public static long Received;

	public static void Add(int bytes) => System.Threading.Interlocked.Add(ref Received, bytes);
}

/// <summary>A game session joined through Keen's service, with its connection framed by <see cref="FramedSession"/>.</summary>
internal sealed class FramedGameSession(IGameSession inner, IMpLog log) : IGameSession
{
	public IGameSession Inner { get; } = inner;

	public IPeerToPeerSession Communication { get; } = new FramedSession(inner.Communication, log);

	public GameServiceUser Server => Inner.Server;

	public bool IsDedicated => Inner.IsDedicated;

	public SessionJoinCode? JoinCode => Inner.JoinCode;
}