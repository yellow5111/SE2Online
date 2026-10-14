using System;
using System.Collections.Generic;
using System.Linq;

namespace Y51_SE2Online.Sync;

public enum TradeVerb : byte { Accept, Decline, Set, Ready, Unready, Cancel }

/// <summary>Client to host: ask <paramref name="Target"/> to trade.</summary>
public sealed record TradeRequest(ushort Target);

/// <summary>Client to host. Set: offer <paramref name="Raw"/> (FixedPoint raw units) of <paramref name="Item"/>, Guid.Empty is credits. Ready: <paramref name="Raw"/> is the version the player accepted.</summary>
public sealed record TradeAction(long Trade, TradeVerb Verb, Guid Item = default, long Raw = 0);

/// <summary>Host to client: someone wants to trade.</summary>
public sealed record TradeInvite(long Trade, string From);

public sealed record TradeLine(Guid Item, long Raw);

/// <summary>Trade invites and states for this machine's player, from the host connection or, on a listen host, in-process.</summary>
public static class TradeInbox
{
	public static event Action<SyncMsg, byte[]> Received;

	public static void Raise(SyncMsg kind, byte[] data) => Received?.Invoke(kind, data);
}

/// <summary>Host to client: the whole trade as that participant sees it. <paramref name="Have"/> is what they own and can offer.</summary>
public sealed record TradeState(long Trade, int Version, int You, string[] Names, TradeLine[][] Offers, bool[] Ready, TradeLine[] Have, string Message, bool Closed, bool Done);

/// <summary>
/// Host-side trade sessions without any game types: who trades with whom, both offers, acceptance and limits. The
/// world validates amounts against real inventories and performs the swap; this only ever holds intents.
/// </summary>
public sealed class TradeBook
{
	public const int MaxLines = 16;
	public const long InviteMs = 30_000, IdleMs = 300_000, InviteGapMs = 3_000;
	public const int MaxActionsPerSecond = 20;

	public sealed class Session
	{
		public long Id;
		public ushort[] Players = new ushort[2];
		public bool Open;
		public readonly Dictionary<Guid, long>[] Offers = { new(), new() };
		public readonly bool[] Ready = new bool[2];
		public int Version;
		public long StartedMs, LastMs;
		public string Message = "";

		public int Side(ushort player) => Players[0] == player ? 0 : Players[1] == player ? 1 : -1;

		public TradeLine[] Lines(int side) => Offers[side].Where(kv => kv.Value > 0).Select(kv => new TradeLine(kv.Key, kv.Value)).ToArray();

		internal void Changed(long now)
		{
			Version++;
			Ready[0] = Ready[1] = false;
			LastMs = now;
		}
	}

	private readonly Dictionary<long, Session> _byId = new();
	private readonly Dictionary<ushort, long> _active = new();
	private readonly Dictionary<ushort, long> _lastInvite = new();
	private readonly Dictionary<ushort, (long Second, int Count)> _rate = new();
	private long _next = 1;

	public IEnumerable<Session> Sessions => _byId.Values;

	public Session Get(long id) => _byId.TryGetValue(id, out var s) ? s : null;

	public Session ActiveOf(ushort player) => _active.TryGetValue(player, out long id) ? Get(id) : null;

	/// <summary>False when a player sends more actions than any hand on a mouse could.</summary>
	public bool Allow(ushort player, long now)
	{
		long second = now / 1000;
		var (at, count) = _rate.TryGetValue(player, out var r) ? r : (second, 0);
		if (at != second) (at, count) = (second, 0);
		_rate[player] = (at, count + 1);
		return count < MaxActionsPerSecond;
	}

	public string Invite(ushort from, ushort to, long now, out Session session)
	{
		session = null;
		if (from == to) return "You cannot trade with yourself.";
		if (_lastInvite.TryGetValue(from, out long last) && now - last < InviteGapMs) return "Wait a moment before asking again.";
		if (_active.ContainsKey(from)) return "Finish or cancel your current trade first.";
		if (_active.ContainsKey(to)) return "That player is already trading.";
		_lastInvite[from] = now;
		session = new Session { Id = _next++, StartedMs = now, LastMs = now };
		session.Players[0] = from;
		session.Players[1] = to;
		_byId[session.Id] = session;
		_active[from] = _active[to] = session.Id;
		return null;
	}

	public string Respond(ushort player, long id, bool accept, long now, out Session session)
	{
		session = Get(id);
		if (session == null || session.Open || session.Side(player) != 1) return "That trade request is no longer open.";
		if (!accept) { Close(session); return null; }
		session.Open = true;
		session.LastMs = now;
		return null;
	}

	/// <summary>Sets one line of the player's own offer, clamped to what they own; any change clears both acceptances.</summary>
	public string Set(ushort player, long id, Guid item, long raw, long owned, long now, out Session session)
	{
		session = Get(id);
		int side = session?.Side(player) ?? -1;
		if (session == null || !session.Open || side < 0) return "That trade is not open.";
		if (raw < 0) return "Invalid amount.";
		long amount = Math.Min(raw, Math.Max(0, owned));
		var offer = session.Offers[side];
		long current = offer.TryGetValue(item, out long c) ? c : 0;
		if (amount == current) return null;
		if (amount > 0 && current == 0 && offer.Count(kv => kv.Value > 0) >= MaxLines) return $"An offer holds at most {MaxLines} different items.";
		if (amount == 0) offer.Remove(item); else offer[item] = amount;
		session.Changed(now);
		return null;
	}

	/// <summary>Accepting is tied to the version the player saw, so a last-moment change is never accepted blind.</summary>
	public string Ready(ushort player, long id, bool ready, int version, long now, out Session session, out bool both)
	{
		session = Get(id);
		both = false;
		int side = session?.Side(player) ?? -1;
		if (session == null || !session.Open || side < 0) return "That trade is not open.";
		if (ready && version != session.Version) return "The offer changed; check it again before accepting.";
		if (ready && session.Offers[0].Values.All(v => v <= 0) && session.Offers[1].Values.All(v => v <= 0)) return "Both offers are empty.";
		session.Ready[side] = ready;
		session.LastMs = now;
		both = session.Ready[0] && session.Ready[1];
		return null;
	}

	/// <summary>Re-clamps an offer when the owner no longer has that much; true if anything changed.</summary>
	public bool Clamp(Session session, int side, Func<Guid, long> owned, long now)
	{
		bool changed = false;
		foreach (var (item, amount) in session.Offers[side].ToList())
		{
			long have = Math.Max(0, owned(item));
			if (have >= amount) continue;
			if (have == 0) session.Offers[side].Remove(item); else session.Offers[side][item] = have;
			changed = true;
		}
		if (changed) session.Changed(now);
		return changed;
	}

	public void Close(Session session)
	{
		_byId.Remove(session.Id);
		foreach (ushort p in session.Players)
			if (_active.TryGetValue(p, out long id) && id == session.Id) _active.Remove(p);
	}

	/// <summary>Unanswered invites and abandoned trades.</summary>
	public List<Session> Expired(long now)
		=> _byId.Values.Where(s => (!s.Open && now - s.StartedMs > InviteMs) || now - s.LastMs > IdleMs).ToList();
}
