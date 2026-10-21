using System;
using System.Collections.Generic;
using System.IO;
using Keen.VRage.Core.Services.Data;
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

	public const int MaxMessageBytes = 128 * 1024 * 1024;

	/// <summary>Bytes of unfinished messages one peer may hold here across all channels.</summary>
	public const long MaxPartialBytes = MaxMessageBytes + 64L * 1024 * 1024;

	private const long StuckMs = 120000;

	private readonly Func<TPeer, byte, MessageReliability, byte[], bool> _send;

	private readonly IMpLog _log;

	private readonly Action<TPeer>? _close;

	private readonly Func<TPeer, bool>? _admitted;

	private readonly List<TPeer> _toClose = new();

	private readonly object _sync = new();

	private readonly Dictionary<TPeer, (Queue<(byte Channel, byte[] Frame)> Frames, long SinceMs)> _backlog = new();

	private sealed class Assembly
	{
		public uint Id;
		public int Count, Next;
		public long LastMs;
		public readonly MemoryStream Data = new();
	}

	private readonly Dictionary<(TPeer, byte), Assembly> _assemblies = new();

	private uint _nextId;

	/// <param name="admitted">Whether a peer may send messages in pieces; null trusts every peer (a client's server).</param>
	public FragmentLink(Func<TPeer, byte, MessageReliability, byte[], bool> send, IMpLog log, Action<TPeer>? close = null, Func<TPeer, bool>? admitted = null)
	{
		_send = send;
		_log = log;
		_close = close;
		_admitted = admitted;
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
			if (data.Length > MaxMessageBytes) _log.WarnOnce("fragment-huge-" + channel, $"large messages: a {data.Length / (1024 * 1024)} MiB message on channel {channel} is over the {MaxMessageBytes / (1024 * 1024)} MiB the other end takes; it will be dropped there");
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

	/// <summary>Sends what waited for Steam's send buffer and drops stalled assemblies; call every network poll.</summary>
	public void Flush()
	{
		List<TPeer>? close = null;
		lock (_sync)
		{
			foreach (var peer in new List<TPeer>(_backlog.Keys)) Flush(peer);
			if (_toClose.Count > 0) { close = new List<TPeer>(_toClose); _toClose.Clear(); }
		}
		Expire(MpClock.NowMs());
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

	/// <summary>Drops messages whose next piece has not come for <see cref="StuckMs"/> (a peer that left or stalled).</summary>
	internal void Expire(long nowMs)
	{
		lock (_sync)
		{
			List<(TPeer, byte)>? stale = null;
			foreach (var (key, assembly) in _assemblies) if (nowMs - assembly.LastMs > StuckMs) (stale ??= new()).Add(key);
			if (stale != null) foreach (var key in stale) _assemblies.Remove(key);
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

	/// <summary>Bytes of unfinished messages held for <paramref name="peer"/>.</summary>
	internal long PartialBytes(TPeer peer)
	{
		lock (_sync)
		{
			long total = 0;
			foreach (var (key, assembly) in _assemblies) if (EqualityComparer<TPeer>.Default.Equals(key.Item1, peer)) total += assembly.Data.Length;
			return total;
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
		// Before SE2 Online's join check passed, a peer only sends small messages (hellos, the join request).
		if (_admitted != null && !_admitted(peer))
		{
			_log.WarnOnce("fragment-early-" + peer, $"large messages: {peer} sent pieces of a large message before it was admitted; dropped");
			return null;
		}
		uint id = BitConverter.ToUInt32(frame[1..]);
		int index = BitConverter.ToUInt16(frame[5..]), count = BitConverter.ToUInt16(frame[7..]);
		int size = frame.Length - 9;
		lock (_sync)
		{
			var key = (peer, channel);
			// Every piece but the last is full size, so the count bounds the message and nothing is held for bytes not sent.
			if (size < 1 || count == 0 || index >= count || (long)count * PieceBytes > MaxMessageBytes || size > PieceBytes || index + 1 < count && size != PieceBytes)
			{
				_assemblies.Remove(key);
				_log.WarnOnce("fragment-size-" + peer, $"large messages: {peer} sent a malformed piece ({index}/{count}, {size} bytes); dropped");
				return null;
			}
			if (index == 0)
			{
				_assemblies.Remove(key);
				_assemblies[key] = new Assembly { Id = id, Count = count };
			}
			if (!_assemblies.TryGetValue(key, out var assembly) || assembly.Id != id || assembly.Next != index || assembly.Count != count)
			{
				_assemblies.Remove(key);
				_log.WarnOnce("fragment-order-" + peer, $"large messages: piece {index}/{count} of message {id} from {peer} arrived out of order; dropped");
				return null;
			}
			if (PartialBytes(peer) + size > MaxPartialBytes)
			{
				_assemblies.Remove(key);
				_log.WarnOnce("fragment-budget-" + peer, $"large messages: {peer} holds over {MaxPartialBytes / (1024 * 1024)} MiB of unfinished messages; dropped one");
				return null;
			}
			assembly.Data.Write(frame[9..]);
			assembly.LastMs = MpClock.NowMs();
			if (++assembly.Next < count) return null;
			_assemblies.Remove(key);
			return assembly.Data.ToArray();
		}
	}
}
