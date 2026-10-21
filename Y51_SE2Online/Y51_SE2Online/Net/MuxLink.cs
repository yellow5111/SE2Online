using System;
using System.Collections.Generic;

namespace Y51_SE2Online.Net;

/// <summary>
/// Several links behind one transport, each connection renumbered so their ids never collide. Used only to add the
/// loopback test listener (-y51testtcp) next to a dedicated server's Steam link; normal hosting never builds one.
/// </summary>
public sealed class MuxLink : IPacketLink
{
	private readonly IPacketLink[] _links;

	private readonly Dictionary<(int Link, uint Conn), uint> _outer = new();

	private readonly Dictionary<uint, (int Link, uint Conn)> _inner = new();

	private readonly List<LinkEvent> _events = new();

	private readonly List<LinkMessage> _messages = new();

	private readonly List<uint> _closed = new();

	private uint _next = 1;

	public MuxLink(params IPacketLink[] links)
	{
		if (links.Length == 0) throw new ArgumentException("MuxLink needs at least one link.");
		_links = links;
	}

	public string Name => string.Join("+", Array.ConvertAll(_links, l => l.Name));

	public int MaxMessageSize
	{
		get
		{
			int max = int.MaxValue;
			foreach (var link in _links) max = Math.Min(max, link.MaxMessageSize);
			return max;
		}
	}

	// Only consulted to warn about a Hello that claims someone else's Steam id; the Steam link does authenticate.
	public bool AuthenticatesPeers => _links[0].AuthenticatesPeers;

	public void Pump(List<LinkEvent> events, List<LinkMessage> messages)
	{
		foreach (uint gone in _closed)
		{
			if (_inner.Remove(gone, out var key)) _outer.Remove(key);
		}
		_closed.Clear();
		for (int i = 0; i < _links.Length; i++)
		{
			_events.Clear();
			_messages.Clear();
			_links[i].Pump(_events, _messages);
			foreach (var e in _events)
			{
				uint conn = Outer(i, e.Conn);
				events.Add(new LinkEvent(e.Kind, conn, e.RemoteId, e.Code, e.Reason, e.ByPeer));
				// Kept until the next pump: this pump's messages for it are still delivered after the event.
				if (e.Kind == LinkEventKind.Disconnected) _closed.Add(conn);
			}
			foreach (var m in _messages) messages.Add(new LinkMessage(Outer(i, m.Conn), m.Data));
		}
	}

	private uint Outer(int link, uint conn)
	{
		if (_outer.TryGetValue((link, conn), out uint outer)) return outer;
		do { outer = _next++; } while (outer == 0 || _inner.ContainsKey(outer));
		_outer[(link, conn)] = outer;
		_inner[outer] = (link, conn);
		return outer;
	}

	public LinkSendResult Send(uint conn, Channel channel, bool reliable, ReadOnlySpan<byte> frame)
		=> _inner.TryGetValue(conn, out var at) ? _links[at.Link].Send(at.Conn, channel, reliable, frame) : LinkSendResult.Failed;

	public int GetPendingReliableBytes(uint conn) => _inner.TryGetValue(conn, out var at) ? _links[at.Link].GetPendingReliableBytes(at.Conn) : 0;

	public bool HasLanePriorities(uint conn) => _inner.TryGetValue(conn, out var at) && _links[at.Link].HasLanePriorities(at.Conn);

	public bool IsSessionAlive(uint conn) => _inner.TryGetValue(conn, out var at) && _links[at.Link].IsSessionAlive(at.Conn);

	public void Close(uint conn, int code, string reason, bool linger)
	{
		if (!_inner.Remove(conn, out var at)) return;
		_outer.Remove(at);
		_links[at.Link].Close(at.Conn, code, reason, linger);
	}

	public void Dispose()
	{
		foreach (var link in _links) link.Dispose();
	}
}
