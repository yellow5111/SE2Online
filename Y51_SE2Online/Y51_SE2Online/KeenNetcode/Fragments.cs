using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Keen.VRage.Core.Services.Data;
using Keen.VRage.Core.Services.GameSessions;
using Keen.VRage.Core.Services.Networking;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>
/// Carries messages of any size over links that take at most one Steam message (512 KiB) at a time. SE2 4.0 sends a
/// world's first replication batch as one message (2.9 MiB for a 266 MB save), which Steam refuses outright, so the
/// player never gets those objects. Every message gets a 1-byte header: 0 = whole message, 1 = piece
/// [id u32][index u16][count u16]. Pieces of one message go out back to back, reliably, on the message's own channel,
/// so the channel's order is kept. What Steam's send buffer cannot take yet waits here, in order, and is retried.
/// </summary>
internal sealed class FragmentLink<TPeer> where TPeer : notnull
{
	public const int PieceBytes = 256 * 1024;

	private const int MaxMessageBytes = 512 * 1024 * 1024;

	private const long StuckMs = 120000;

	private readonly Func<TPeer, byte, MessageReliability, byte[], bool> _send;

	private readonly IMpLog _log;

	private readonly Action<TPeer>? _close;

	private readonly List<TPeer> _toClose = new();

	private readonly object _sync = new();

	private readonly Dictionary<TPeer, (Queue<(byte Channel, byte[] Frame)> Frames, long SinceMs)> _backlog = new();

	private readonly Dictionary<(TPeer, byte), (uint Id, int Count, int Next, MemoryStream Data)> _assemblies = new();

	private uint _nextId;

	public FragmentLink(Func<TPeer, byte, MessageReliability, byte[], bool> send, IMpLog log, Action<TPeer>? close = null)
	{
		_send = send;
		_log = log;
		_close = close;
	}

	public bool Send(TPeer peer, ReadOnlySpan<byte> data, byte channel, MessageReliability reliability)
	{
		lock (_sync)
		{
			Flush(peer);
			bool waiting = _backlog.ContainsKey(peer);
			if (data.Length < PieceBytes)
			{
				// Unreliable messages are state samples; one behind a backlog is stale before it could leave.
				if (waiting && reliability == MessageReliability.Unreliable) return false;
				var frame = new byte[data.Length + 1];
				data.CopyTo(frame.AsSpan(1));
				if (!waiting && _send(peer, channel, reliability, frame)) return true;
				if (reliability == MessageReliability.Unreliable) return false;
				Wait(peer, channel, frame);
				return true;
			}
			uint id = ++_nextId;
			int count = (data.Length + PieceBytes - 1) / PieceBytes;
			for (int i = 0; i < count; i++)
			{
				var piece = data.Slice(i * PieceBytes, Math.Min(PieceBytes, data.Length - i * PieceBytes));
				var frame = new byte[piece.Length + 9];
				frame[0] = 1;
				BitConverter.TryWriteBytes(frame.AsSpan(1), id);
				BitConverter.TryWriteBytes(frame.AsSpan(5), (ushort)i);
				BitConverter.TryWriteBytes(frame.AsSpan(7), (ushort)count);
				piece.CopyTo(frame.AsSpan(9));
				if (!waiting && _send(peer, channel, MessageReliability.Reliable, frame)) continue;
				waiting = true;
				Wait(peer, channel, frame);
			}
			return true;
		}
	}

	private void Wait(TPeer peer, byte channel, byte[] frame)
	{
		if (!_backlog.TryGetValue(peer, out var backlog)) _backlog[peer] = backlog = (new(), MpClock.NowMs());
		backlog.Frames.Enqueue((channel, frame));
	}

	/// <summary>Sends what waited for Steam's send buffer; call every network poll.</summary>
	public void Flush()
	{
		List<TPeer>? close = null;
		lock (_sync)
		{
			foreach (var peer in new List<TPeer>(_backlog.Keys)) Flush(peer);
			if (_toClose.Count > 0) { close = new List<TPeer>(_toClose); _toClose.Clear(); }
		}
		// Outside the lock: closing goes back through the session, which may send or forget on this link.
		if (close != null && _close != null) foreach (var peer in close) _close(peer);
	}

	private void Flush(TPeer peer)
	{
		if (!_backlog.TryGetValue(peer, out var backlog)) return;
		bool moved = false;
		while (backlog.Frames.TryPeek(out var next) && _send(peer, next.Channel, MessageReliability.Reliable, next.Frame))
		{
			backlog.Frames.Dequeue();
			moved = true;
		}
		if (backlog.Frames.Count == 0) _backlog.Remove(peer);
		else if (moved) _backlog[peer] = (backlog.Frames, MpClock.NowMs());
		else if (MpClock.NowMs() - backlog.SinceMs > StuckMs)
		{
			// Dropping reliable pieces under a live connection left that player silently out of step for good; it is
			// closed instead, so the player rejoins from a clean state.
			_log.Warn($"large messages: nothing could be sent to {peer} for {StuckMs / 1000} s ({backlog.Frames.Count} piece(s) waiting); closing its connection (connection too slow; rejoin)");
			_backlog.Remove(peer);
			_toClose.Add(peer);
		}
	}

	public void Forget(TPeer peer)
	{
		lock (_sync)
		{
			_backlog.Remove(peer);
			foreach (var key in new List<(TPeer, byte)>(_assemblies.Keys)) if (EqualityComparer<TPeer>.Default.Equals(key.Item1, peer)) _assemblies.Remove(key);
		}
	}

	/// <summary>The message a received frame completes, or null while pieces are missing (or the frame is not ours).</summary>
	public byte[]? Receive(TPeer peer, byte channel, ReadOnlySpan<byte> frame)
	{
		if (frame.Length == 0) return null;
		if (frame[0] == 0) return frame[1..].ToArray();
		if (frame[0] != 1 || frame.Length < 9)
		{
			_log.WarnOnce("fragment-bad-" + peer, $"large messages: {peer} sent a message without SE2 Online's framing (different plugin version?)");
			return null;
		}
		uint id = BitConverter.ToUInt32(frame[1..]);
		int index = BitConverter.ToUInt16(frame[5..]), count = BitConverter.ToUInt16(frame[7..]);
		lock (_sync)
		{
			var key = (peer, channel);
			if (index == 0)
			{
				if ((long)count * PieceBytes > MaxMessageBytes) return null;
				_assemblies[key] = (id, count, 0, new MemoryStream(Math.Min(count * PieceBytes, 64 * 1024 * 1024)));
			}
			if (!_assemblies.TryGetValue(key, out var assembly) || assembly.Id != id || assembly.Next != index || assembly.Count != count)
			{
				_assemblies.Remove(key);
				_log.WarnOnce("fragment-order-" + peer, $"large messages: piece {index}/{count} of message {id} from {peer} arrived out of order; dropped");
				return null;
			}
			assembly.Data.Write(frame[9..]);
			if (index + 1 < count)
			{
				_assemblies[key] = (assembly.Id, assembly.Count, index + 1, assembly.Data);
				return null;
			}
			_assemblies.Remove(key);
			return assembly.Data.ToArray();
		}
	}
}

/// <summary>Keen's own Steam connection with SE2 Online's framing, on both ends (see <see cref="FragmentLink{TPeer}"/>).</summary>
internal sealed class FramedSession : IPeerToPeerSession
{
	private readonly FragmentLink<GameServiceUser> _link;

	public IPeerToPeerSession Inner { get; }

	public FramedSession(IPeerToPeerSession inner, IMpLog log)
	{
		Inner = inner;
		_link = new FragmentLink<GameServiceUser>((user, channel, reliability, frame) => inner.SendMessage(user, frame, channel, reliability), log, CloseConnection);
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
