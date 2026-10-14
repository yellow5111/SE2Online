using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Keen.Game2.Simulation.GameSystems.Economy;
using Keen.Game2.Simulation.GameSystems.Ownership;
using Keen.Game2.Simulation.GameSystems.Player;
using Keen.Game2.Simulation.WorldObjects.Items;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Definitions;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Library.Utils;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// Player-to-player trades, run on the server thread. Clients only send intents; every amount is checked against the
/// live inventories here, and the swap moves items between the two characters' inventories in one step, moving
/// everything back if any part does not fit.
/// </summary>
internal sealed partial class HostWorld
{
    private const double TradeRangeMeters = 30;
    private readonly ConcurrentQueue<(ushort ClientId, byte[] Data)> _tradeInbox = new();
    private readonly TradeBook _trades = new();
    private readonly Dictionary<(long Trade, int Side), string> _tradeHaveSent = new();
    private long _nextTradeScan;

    public void OnTrade(ushort clientId, byte[] data)
    {
        if (_tradeInbox.Count < 256) _tradeInbox.Enqueue((clientId, data));
    }

    private void UpdateTrades(Session session)
    {
        var host = _host;
        if (host == null) return;
        long now = MpClock.NowMs();
        while (_tradeInbox.TryDequeue(out var item))
        {
            try { HandleTrade(session, host, item.ClientId, item.Data, now); }
            catch (Exception ex) { _log.WarnOnce("trade-failed-" + ex.GetType().Name, "trade: request from ClientId " + item.ClientId + " failed: " + ex.Message); }
        }
        if (now < _nextTradeScan) return;
        _nextTradeScan = now + 500;
        foreach (var expired in _trades.Expired(now)) EndTrade(host, expired, expired.Open ? "The trade timed out." : "The trade request expired.");
        foreach (var trade in _trades.Sessions.Where(s => s.Open).ToList())
        {
            string gone = trade.Players.Select(p => TradeAbsent(host, p)).FirstOrDefault(r => r != null);
            if (gone != null) { EndTrade(host, trade, gone); continue; }
            bool changed = false;
            for (int side = 0; side < 2; side++)
            {
                ushort player = trade.Players[side];
                if (_trades.Clamp(trade, side, item => Owned(session, player, item), now))
                {
                    trade.Message = TradeName(host, player) + " no longer has everything offered; the offer was reduced.";
                    changed = true;
                }
            }
            if (changed) SendTradeState(session, host, trade);
            else for (int side = 0; side < 2; side++)
            {
                // The inventory list follows item pickups and use; only resent when it differs.
                var have = Have(session, trade.Players[side]);
                if (!_tradeHaveSent.TryGetValue((trade.Id, side), out string sent) || sent != Signature(have)) SendTradeState(session, host, trade);
            }
        }
    }

    private void HandleTrade(Session session, HostSession host, ushort player, byte[] data, long now)
    {
        if (data.Length == 0 || !_trades.Allow(player, now)) return;
        if ((SyncMsg)data[0] == SyncMsg.TradeRequest)
        {
            var request = SocialMessage.Decode<TradeRequest>(data);
            string why = TradeAbsent(host, request.Target) ?? TradeAbsent(host, player)
                ?? (TradeDistance(player, request.Target) > TradeRangeMeters ? $"Move within {TradeRangeMeters:0} m of {TradeName(host, request.Target)} to trade." : null);
            TradeBook.Session created = null;
            why ??= _trades.Invite(player, request.Target, now, out created);
            if (why != null) { host.GpsNotice(player, why); return; }
            host.SendTrade(request.Target, SyncMsg.TradeInvite, new TradeInvite(created.Id, TradeName(host, player)));
            host.GpsNotice(player, "Trade request sent to " + TradeName(host, request.Target) + ".");
            return;
        }
        if ((SyncMsg)data[0] != SyncMsg.TradeAction) return;
        var action = SocialMessage.Decode<TradeAction>(data);
        var trade = _trades.Get(action.Trade);
        if (trade == null || trade.Side(player) < 0) return;
        string error = null;
        switch (action.Verb)
        {
        case TradeVerb.Accept:
        case TradeVerb.Decline:
            error = _trades.Respond(player, action.Trade, action.Verb == TradeVerb.Accept, now, out _);
            if (error == null && action.Verb == TradeVerb.Decline) { EndTrade(host, trade, TradeName(host, player) + " declined the trade."); return; }
            break;
        case TradeVerb.Set:
            error = _trades.Set(player, action.Trade, action.Item, action.Raw, Owned(session, player, action.Item), now, out _);
            if (error == null) trade.Message = "";
            break;
        case TradeVerb.Ready:
        case TradeVerb.Unready:
            error = _trades.Ready(player, action.Trade, action.Verb == TradeVerb.Ready, (int)action.Raw, now, out _, out bool both);
            if (error == null && both)
            {
                string failed = ExecuteTrade(session, host, trade);
                if (failed == null) { EndTrade(host, trade, "Trade complete.", done: true); return; }
                trade.Changed(now);
                trade.Message = failed;
            }
            break;
        case TradeVerb.Cancel:
            EndTrade(host, trade, TradeName(host, player) + " cancelled the trade.");
            return;
        }
        if (error != null) trade.Message = error;
        SendTradeState(session, host, trade);
    }

    private string ExecuteTrade(Session session, HostSession host, TradeBook.Session trade)
    {
        foreach (ushort p in trade.Players)
            if (TradeAbsent(host, p) is string gone) return gone;
        if (TradeDistance(trade.Players[0], trade.Players[1]) > TradeRangeMeters) return $"Both players must stay within {TradeRangeMeters:0} m to trade.";
        var definitions = Singleton<DefinitionManager>.Instance;
        for (int side = 0; side < 2; side++)
            foreach (var (item, raw) in trade.Offers[side])
                if (Owned(session, trade.Players[side], item) < raw) return TradeName(host, trade.Players[side]) + " no longer has everything offered.";
        var moves = new List<(InventoryComponent From, InventoryComponent To, ItemDefinition Item, FixedPoint Amount)>();
        string failure = null;
        for (int side = 0; side < 2 && failure == null; side++)
        {
            var from = Inventories(trade.Players[side]);
            var to = Inventories(trade.Players[1 - side]);
            foreach (var (item, raw) in trade.Offers[side])
            {
                if (item == Guid.Empty) continue;
                if (!definitions.TryGetDefinition(item, out ItemDefinition def)) { failure = "An offered item is unknown."; break; }
                if (!Move(from, to, def, new FixedPoint { RawValue = raw }, moves)) { failure = TradeName(host, trade.Players[1 - side]) + " has no room for everything offered."; break; }
            }
        }
        if (failure != null)
        {
            Rollback(moves);
            return failure;
        }
        // Credits last: the balances were checked above and nothing on this thread changed them since.
        for (int side = 0; side < 2; side++)
        {
            if (!trade.Offers[side].TryGetValue(Guid.Empty, out long credits) || credits <= 0) continue;
            var amount = new FixedPoint { RawValue = credits };
            Credits(session, trade.Players[side])?.ModifyCredits(-amount);
            Credits(session, trade.Players[1 - side])?.ModifyCredits(amount);
        }
        _log.Info($"trade: {TradeName(host, trade.Players[0])} gave [{Describe(trade.Offers[0])}] for {TradeName(host, trade.Players[1])}'s [{Describe(trade.Offers[1])}]");
        return null;
    }

    private static bool Move(List<InventoryComponent> from, List<InventoryComponent> to, ItemDefinition def, FixedPoint amount, List<(InventoryComponent, InventoryComponent, ItemDefinition, FixedPoint)> moves)
    {
        FixedPoint left = amount;
        foreach (var source in from)
        {
            foreach (var target in to)
            {
                if (left <= FixedPoint.Zero) return true;
                FixedPoint here = FixedPoint.Min(left, source.CountItem(def));
                if (here <= FixedPoint.Zero) break;
                FixedPoint moved = source.TransferByDef(target, def, null, here, allowPartial: true);
                if (moved <= FixedPoint.Zero) continue;
                moves.Add((source, target, def, moved));
                left -= moved;
            }
        }
        return left <= FixedPoint.Zero;
    }

    private void Rollback(List<(InventoryComponent From, InventoryComponent To, ItemDefinition Item, FixedPoint Amount)> moves)
    {
        for (int i = moves.Count - 1; i >= 0; i--)
        {
            var (from, to, item, amount) = moves[i];
            FixedPoint back = to.TransferByDef(from, item, null, amount, allowPartial: true);
            if (back != amount) _log.Error($"trade: rollback returned {(double)back} of {(double)amount} {item.DebugNameShort}; the rest stays with the receiver");
        }
    }

    private void EndTrade(HostSession host, TradeBook.Session trade, string message, bool done = false)
    {
        _trades.Close(trade);
        _tradeHaveSent.Remove((trade.Id, 0));
        _tradeHaveSent.Remove((trade.Id, 1));
        var names = trade.Players.Select(p => TradeName(host, p)).ToArray();
        for (int side = 0; side < 2; side++)
            host.SendTrade(trade.Players[side], SyncMsg.TradeState, new TradeState(trade.Id, trade.Version, side, names, new[] { trade.Lines(0), trade.Lines(1) }, trade.Ready.ToArray(), Array.Empty<TradeLine>(), message, Closed: true, Done: done));
    }

    private void SendTradeState(Session session, HostSession host, TradeBook.Session trade)
    {
        var names = trade.Players.Select(p => TradeName(host, p)).ToArray();
        for (int side = 0; side < 2; side++)
        {
            var have = Have(session, trade.Players[side]);
            _tradeHaveSent[(trade.Id, side)] = Signature(have);
            host.SendTrade(trade.Players[side], SyncMsg.TradeState, new TradeState(trade.Id, trade.Version, side, names, new[] { trade.Lines(0), trade.Lines(1) }, trade.Ready.ToArray(), have, trade.Message, Closed: false, Done: false));
        }
    }

    private List<InventoryComponent> Inventories(ushort player)
    {
        Entity body = CharacterOf(player);
        return body == null || EngineCodec.IsDead(body) ? new List<InventoryComponent>() : body.Components.OfType<InventoryComponent>().ToList();
    }

    private PlayerCreditsData Credits(Session session, ushort player)
    {
        long identity = player == SyncProtocol.HostClientId ? PlayerIdentities.HotfixIdentity : IdentityOf(player);
        return identity == 0 ? null : session.Get<IPerPlayerData>().GetPerPlayerData<PlayerCreditsData>(new IdentityId(identity));
    }

    private long Owned(Session session, ushort player, Guid item)
    {
        if (item == Guid.Empty) return Math.Max(0, Credits(session, player)?.Credits.RawValue ?? 0);
        if (!Singleton<DefinitionManager>.Instance.TryGetDefinition(item, out ItemDefinition def)) return 0;
        long total = 0;
        foreach (var inventory in Inventories(player)) total += inventory.CountItem(def).RawValue;
        return total;
    }

    private TradeLine[] Have(Session session, ushort player)
    {
        var totals = new Dictionary<Guid, long>();
        foreach (var inventory in Inventories(player))
            foreach (var stack in inventory.Items)
                if (stack.Definition != null) totals[stack.Definition.Guid] = (totals.TryGetValue(stack.Definition.Guid, out long t) ? t : 0) + stack.Amount.RawValue;
        long credits = Owned(session, player, Guid.Empty);
        if (credits > 0) totals[Guid.Empty] = credits;
        return totals.Select(kv => new TradeLine(kv.Key, kv.Value)).OrderBy(l => l.Item).ToArray();
    }

    private static string Signature(TradeLine[] lines) => string.Join(";", lines.Select(l => l.Item + "=" + l.Raw));

    private static string Describe(Dictionary<Guid, long> offer)
    {
        var definitions = Singleton<DefinitionManager>.Instance;
        return string.Join(", ", offer.Where(kv => kv.Value > 0).Select(kv => (double)new FixedPoint { RawValue = kv.Value } + " x "
            + (kv.Key == Guid.Empty ? "credits" : definitions.TryGetDefinition(kv.Key, out ItemDefinition d) ? d.DebugNameShort : kv.Key.ToString())));
    }

    private string TradeAbsent(HostSession host, ushort player)
    {
        var info = host.PlayerTable().FirstOrDefault(p => p.ClientId == player);
        if (info.ClientId == 0 || !info.Connected) return "Your trade partner is no longer online.";
        Entity body = CharacterOf(player);
        return body == null || EngineCodec.IsDead(body) ? TradeName(host, player) + " has no character in the world." : null;
    }

    private double TradeDistance(ushort a, ushort b)
    {
        Entity x = CharacterOf(a), y = CharacterOf(b);
        return x == null || y == null ? double.MaxValue : Vector3D.Distance(x.Data.GetWorldTransform().Position, y.Data.GetWorldTransform().Position);
    }

    private static string TradeName(HostSession host, ushort player)
        => host.PlayerTable().FirstOrDefault(p => p.ClientId == player).Name ?? "Player " + player;
}
