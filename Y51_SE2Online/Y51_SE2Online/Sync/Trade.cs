using System;
using System.Collections.Generic;
using System.Linq;

namespace Y51_SE2Online.Sync;

/// <summary>Appended only: 0.15.3 peers send the first six by number.</summary>
public enum TradeVerb : byte { Accept, Decline, Set, Ready, Unready, Cancel, Request }

/// <summary>Client to host: ask <paramref name="Target"/> to trade.</summary>
public sealed record TradeRequest(ushort Target);

/// <summary>Client to host. Set: offer <paramref name="Raw"/> (FixedPoint raw units) of <paramref name="Item"/>, Guid.Empty is credits. Request: ask the partner to give <paramref name="Raw"/> of an item they own. Ready: <paramref name="Raw"/> is the version the player accepted.</summary>
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

/// <summary>
/// Host to client: the whole trade as that participant sees it. <paramref name="Have"/> is what they own and can offer.
/// <paramref name="TheirHave"/> (the partner's items, no credits) and <paramref name="Asked"/> (per side, the offered items the
/// other side requested) are null unless both players' games take them (<see cref="JoinRequest.TradeView"/>); 0.15.3 games ignore them.
/// </summary>
public sealed record TradeState(long Trade, int Version, int You, string[] Names, TradeLine[][] Offers, bool[] Ready, TradeLine[] Have, string Message, bool Closed, bool Done,
	TradeLine[] TheirHave = null, Guid[][] Asked = null)
{
	public const int MaxHave = 256;

	/// <summary>The state as received, cut to the sizes the host ever sends; null when it is malformed.</summary>
	public TradeState Bounded()
	{
		if (Names?.Length != 2 || Offers?.Length != 2 || Ready?.Length != 2 || You is < 0 or > 1) return null;
		static TradeLine[] Cut(TradeLine[] lines, int max) => lines?.Where(l => l != null && l.Raw > 0).Take(max).ToArray();
		var asked = Asked?.Length == 2 ? new[] { Asked[0]?.Take(TradeBook.MaxLines).ToArray() ?? Array.Empty<Guid>(), Asked[1]?.Take(TradeBook.MaxLines).ToArray() ?? Array.Empty<Guid>() } : null;
		return this with
		{
			Names = Names.Select(n => SocialState.Clean(n ?? "", 80)).ToArray(),
			Offers = new[] { Cut(Offers[0], TradeBook.MaxLines) ?? Array.Empty<TradeLine>(), Cut(Offers[1], TradeBook.MaxLines) ?? Array.Empty<TradeLine>() },
			Have = Cut(Have, MaxHave) ?? Array.Empty<TradeLine>(),
			TheirHave = Cut(TheirHave, MaxHave),
			Asked = asked,
			Message = SocialState.Clean(Message ?? "", 200)
		};
	}
}

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
		/// <summary>Per side: lines of that side's offer the other side asked for and the owner has not edited since.</summary>
		public readonly HashSet<Guid>[] Asked = { new(), new() };
		public readonly bool[] Ready = new bool[2];
		public int Version;
		public long StartedMs, LastMs, RefreshMs;
		public string Message = "";

		public int Side(ushort player) => Players[0] == player ? 0 : Players[1] == player ? 1 : -1;

		public TradeLine[] Lines(int side) => Offers[side].Where(kv => kv.Value > 0).Select(kv => new TradeLine(kv.Key, kv.Value)).ToArray();

		public Guid[] AskedLines(int side) => Asked[side].Where(Offers[side].ContainsKey).ToArray();

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
		session.Asked[side].Remove(item);
		session.Changed(now);
		return null;
	}

	/// <summary>
	/// Asks the partner for <paramref name="raw"/> of an item, clamped to what they own: the line goes into the partner's
	/// offer, marked as asked, so accepting the trade gives it. Only lines the partner has not set themselves can be asked
	/// for or changed; the owner can still change or remove an asked line with <see cref="Set"/>.
	/// </summary>
	public string Request(ushort player, long id, Guid item, long raw, long partnerOwned, long now, out Session session)
	{
		session = Get(id);
		int side = session?.Side(player) ?? -1;
		if (session == null || !session.Open || side < 0) return "That trade is not open.";
		if (raw < 0) return "Invalid amount.";
		if (item == Guid.Empty) return "Credits cannot be requested; ask your partner to offer them.";
		int other = 1 - side;
		var offer = session.Offers[other];
		long current = offer.TryGetValue(item, out long c) ? c : 0;
		if (current > 0 && !session.Asked[other].Contains(item)) return "Your partner already offers that; only they can change it.";
		long amount = Math.Min(raw, Math.Max(0, partnerOwned));
		if (raw > 0 && amount == 0) return "Your partner does not have that item.";
		if (amount == current) return null;
		if (amount > 0 && current == 0 && offer.Count(kv => kv.Value > 0) >= MaxLines) return $"An offer holds at most {MaxLines} different items.";
		if (amount == 0) { offer.Remove(item); session.Asked[other].Remove(item); }
		else { offer[item] = amount; session.Asked[other].Add(item); }
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
			if (have == 0) { session.Offers[side].Remove(item); session.Asked[side].Remove(item); } else session.Offers[side][item] = have;
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

public enum TradePane { Mine, Give, Get, Theirs }

public sealed record TradeSlot(TradePane Pane, Guid Item, long Raw, bool Asked);

/// <summary>The trade window's item tiles and what dragging one asks the host for; game-free so it is testable offline.</summary>
public static class TradeDrag
{
	/// <summary>The tiles each pane shows: your and their inventories without what is already offered, both offers; credits are shown apart.</summary>
	public static TradeSlot[] Slots(TradeState state, TradePane pane, Func<Guid, string> name)
	{
		int you = state.You, them = 1 - you;
		var offers = state.Offers;
		static long Offered(TradeLine[] offer, Guid item) => offer.FirstOrDefault(l => l.Item == item)?.Raw ?? 0;
		IEnumerable<TradeSlot> slots = pane switch
		{
			TradePane.Mine => state.Have.Select(l => new TradeSlot(pane, l.Item, l.Raw - Offered(offers[you], l.Item), false)),
			TradePane.Theirs => (state.TheirHave ?? Array.Empty<TradeLine>()).Select(l => new TradeSlot(pane, l.Item, l.Raw - Offered(offers[them], l.Item), false)),
			TradePane.Give => offers[you].Select(l => new TradeSlot(pane, l.Item, l.Raw, state.Asked?[you].Contains(l.Item) == true)),
			_ => offers[them].Select(l => new TradeSlot(pane, l.Item, l.Raw, state.Asked?[them].Contains(l.Item) == true))
		};
		return slots.Where(s => s.Item != Guid.Empty && s.Raw > 0).OrderBy(s => name(s.Item), StringComparer.OrdinalIgnoreCase).ToArray();
	}

	/// <summary>What one drop of <paramref name="amount"/> asks the host for (verb and the line's new total), or why it does nothing. Null target: dropped outside every pane.</summary>
	public static (TradeVerb Verb, long Raw, string Error) Plan(TradeState state, TradeSlot slot, TradePane? target, long amount)
	{
		int you = state.You, them = 1 - you;
		string other = state.Names[them];
		long mine = state.Offers[you].FirstOrDefault(l => l.Item == slot.Item)?.Raw ?? 0;
		long theirs = state.Offers[them].FirstOrDefault(l => l.Item == slot.Item)?.Raw ?? 0;
		bool asked = state.Asked?[them].Contains(slot.Item) == true;
		if (target == slot.Pane || (target == null && slot.Pane is TradePane.Mine or TradePane.Theirs)) return (TradeVerb.Cancel, 0, null);
		if (amount <= 0) return (TradeVerb.Cancel, 0, "Enter an amount above zero, or clear the amount box to move whole stacks.");
		return slot.Pane switch
		{
			TradePane.Mine when target == TradePane.Give => (TradeVerb.Set, mine + Math.Min(amount, slot.Raw), null),
			TradePane.Mine => (TradeVerb.Cancel, 0, "Drag your items into YOU GIVE."),
			TradePane.Theirs when target != TradePane.Get => (TradeVerb.Cancel, 0, "Drag " + other + "'s items into YOU GET to ask for them."),
			TradePane.Theirs when state.Asked == null => (TradeVerb.Cancel, 0, other + "'s game is too old for item requests; ask them to offer it."),
			TradePane.Theirs when theirs > 0 && !asked => (TradeVerb.Cancel, 0, other + " already offers that; only they can change it."),
			TradePane.Theirs => (TradeVerb.Request, theirs + Math.Min(amount, slot.Raw), null),
			TradePane.Give => (TradeVerb.Set, Math.Max(0, slot.Raw - amount), null),
			_ when slot.Asked => (TradeVerb.Request, Math.Max(0, slot.Raw - amount), null),
			_ => (TradeVerb.Cancel, 0, "Only " + other + " can change their offer.")
		};
	}
}
