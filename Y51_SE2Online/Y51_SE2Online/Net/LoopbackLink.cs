using System;
using System.Collections.Generic;

namespace Y51_SE2Online.Net;

public sealed class LoopbackLink : IPacketLink, IDisposable
{
	private readonly object _sync;

	private readonly Random _rng;

	private readonly Queue<LinkEvent> _events = new();

	private readonly List<(uint Conn, byte[] Data, bool Reliable)> _inbound = new();

	private readonly Dictionary<uint, (LoopbackLink Peer, uint PeerConn, ulong RemoteId)> _conns = new();

	private readonly Dictionary<uint, int> _pendingReliable = new();

	private static uint _nextConn = 100u;

	public string Name { get; }

	public ulong LocalId { get; }

	public int MaxMessageSize { get; set; } = 524288;

	public bool AuthenticatesPeers => true;

	public double UnreliableDropRate { get; set; }

	public double UnreliableReorderRate { get; set; }

	public int SendBufferBytes { get; set; } = 4194304;

	/// <summary>Test knob: what <see cref="IsSessionAlive"/> answers.</summary>
	public bool SessionAlive { get; set; }

	public bool IsSessionAlive(uint conn) => SessionAlive;

	public long SentMessages { get; private set; }

	public long DroppedUnreliable { get; private set; }

	public int MaxPendingReliableSeen { get; private set; }

	public LoopbackLink(string name, ulong localId, int seed = 1, object? sync = null)
	{
		Name = name;
		LocalId = localId;
		_rng = new Random(seed);
		_sync = sync ?? new object();
	}

	public static (uint ConnA, uint ConnB) Connect(LoopbackLink a, LoopbackLink b)
	{
		lock (a._sync)
		{
			uint connA = _nextConn++;
			uint connB = _nextConn++;
			a._conns[connA] = (b, connB, b.LocalId);
			b._conns[connB] = (a, connA, a.LocalId);
			a._events.Enqueue(new LinkEvent(LinkEventKind.Connected, connA, b.LocalId));
			b._events.Enqueue(new LinkEvent(LinkEventKind.Connected, connB, a.LocalId));
			return (connA, connB);
		}
	}

	public void Pump(List<LinkEvent> events, List<LinkMessage> messages)
	{
		lock (_sync)
		{
			while (_events.Count > 0)
			{
				events.Add(_events.Dequeue());
			}
			foreach (var (conn, data, reliable) in _inbound)
			{
				messages.Add(new LinkMessage(conn, data));
				if (reliable && _conns.TryGetValue(conn, out var link))
				{
					link.Peer.AddPending(link.PeerConn, -data.Length);
				}
			}
			_inbound.Clear();
		}
	}

	public LinkSendResult Send(uint conn, Channel channel, bool reliable, ReadOnlySpan<byte> frame)
	{
		lock (_sync)
		{
			if (!_conns.TryGetValue(conn, out var link))
			{
				return LinkSendResult.Failed;
			}
			if (frame.Length > MaxMessageSize)
			{
				return LinkSendResult.Failed;
			}
			if (reliable && GetPendingReliableBytes(conn) + frame.Length > SendBufferBytes)
			{
				return LinkSendResult.Busy;
			}
			SentMessages++;
			if (!reliable && UnreliableDropRate > 0.0 && _rng.NextDouble() < UnreliableDropRate)
			{
				DroppedUnreliable++;
				return LinkSendResult.Ok;
			}
			byte[] data = frame.ToArray();
			var inbound = link.Peer._inbound;
			if (!reliable && UnreliableReorderRate > 0.0 && inbound.Count > 0 && _rng.NextDouble() < UnreliableReorderRate)
			{
				// Only jump ahead of an unreliable message; reliable ones must keep their order.
				int last = inbound.Count - 1;
				if (!inbound[last].Reliable)
				{
					inbound.Insert(last, (link.PeerConn, data, false));
					return LinkSendResult.Ok;
				}
			}
			inbound.Add((link.PeerConn, data, reliable));
			if (reliable)
			{
				AddPending(conn, data.Length);
			}
			return LinkSendResult.Ok;
		}
	}

	private void AddPending(uint conn, int delta)
	{
		_pendingReliable.TryGetValue(conn, out var pending);
		pending += delta;
		_pendingReliable[conn] = pending;
		if (pending > MaxPendingReliableSeen)
		{
			MaxPendingReliableSeen = pending;
		}
	}

	public int GetPendingReliableBytes(uint conn)
	{
		lock (_sync)
		{
			return _pendingReliable.TryGetValue(conn, out int pending) ? pending : 0;
		}
	}

	public void Close(uint conn, int code, string reason, bool linger)
	{
		lock (_sync)
		{
			if (!_conns.Remove(conn, out var link))
			{
				return;
			}
			_pendingReliable.Remove(conn);
			if (!linger)
			{
				_inbound.RemoveAll(m => m.Conn == conn);
			}
			link.Peer._conns.Remove(link.PeerConn);
			link.Peer._pendingReliable.Remove(link.PeerConn);
			link.Peer._events.Enqueue(new LinkEvent(LinkEventKind.Disconnected, link.PeerConn, LocalId, code, reason, byPeer: true));
		}
	}

	public void SimulateConnectionLost(uint conn, int code, string reason)
	{
		lock (_sync)
		{
			if (_conns.Remove(conn, out var link))
			{
				_events.Enqueue(new LinkEvent(LinkEventKind.Disconnected, conn, link.RemoteId, code, reason));
				link.Peer._conns.Remove(link.PeerConn);
				link.Peer._events.Enqueue(new LinkEvent(LinkEventKind.Disconnected, link.PeerConn, LocalId, code, reason));
			}
		}
	}

	public void Dispose()
	{
		lock (_sync)
		{
			foreach (uint conn in new List<uint>(_conns.Keys))
			{
				Close(conn, DisconnectCodes.Shutdown, "dispose", linger: false);
			}
		}
	}
}
