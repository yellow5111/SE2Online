using System;
using System.Globalization;
using System.Linq;
using Keen.Game2.Client.UI.Library.Dialogs.TwoOptionsDialog;
using Keen.Game2.Simulation.WorldObjects.Items;
using Keen.VRage.Library.Definitions;
using Keen.VRage.Library.Utils;
using Keen.VRage.UI.Screens;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

/// <summary>This player's side of trading: the request popup, the trade window and "/trade name".</summary>
internal static class TradeUi
{
    private static readonly object Sync = new();
    private static TradeInvite _invite;
    private static TradeState _state;
    private static long _stateVersion;
    private static IObservableDisposable _open;

    internal static TradeState State { get { lock (Sync) return _state; } }
    internal static long StateVersion { get { lock (Sync) return _stateVersion; } }

    internal static void Install()
    {
        TradeInbox.Received += Received;
        PlayerOptions.Add(t => "Trade with " + t.Name, _ => AdminAccess.InSession, t => Request(t.ClientId));
    }

    private static void Received(SyncMsg kind, byte[] data)
    {
        try
        {
            if (kind == SyncMsg.TradeInvite)
            {
                var invite = SocialMessage.Decode<TradeInvite>(data);
                lock (Sync) _invite = invite with { From = SocialState.Clean(invite.From ?? "", 80) };
            }
            else if (kind == SyncMsg.TradeState)
            {
                var state = SocialMessage.Decode<TradeState>(data).Bounded();
                if (state == null) return;
                lock (Sync) { _state = state; _stateVersion++; }
            }
        }
        catch (ArgumentException) { }
    }

    /// <summary>Every UI frame: shows a pending request, opens the window for an open trade and closes it when the trade ends.</summary>
    internal static void Tick()
    {
        TradeInvite invite;
        TradeState state;
        lock (Sync) { invite = _invite; _invite = null; state = _state; }
        if (invite != null) Ask(invite);
        if (state == null) return;
        if (state.Closed)
        {
            lock (Sync) if (ReferenceEquals(_state, state)) _state = null;
            _open?.Dispose();
            _open = null;
            if (!string.IsNullOrEmpty(state.Message)) Notice(state.Message);
            return;
        }
        if (_open != null || !AdminAccess.InSession) return;
        var shared = JoinStatusUi.FindShared(MpPlugin.App);
        if (shared == null) return;
        _open = shared.CreateScreen<TradeScreen>(new SocialScreenViewModel(), true, ScreenLayer.Top);
        _open.OnDisposed += _ => _open = null;
    }

    private static void Ask(TradeInvite invite)
    {
        var shared = JoinStatusUi.FindShared(MpPlugin.App);
        if (shared == null) { Act(new TradeAction(invite.Trade, TradeVerb.Decline)); return; }
        shared.ShowDialog(new TwoOptionsDialogViewModel(new TwoOptionsDialogDefinition
        {
            Title = OnlineUi.Raw("Trade request"), Content = OnlineUi.Raw(invite.From + " wants to trade with you."),
            ConfirmOption = OnlineUi.Raw("Trade"), CancelOption = OnlineUi.Raw("Decline")
        })
        {
            ConfirmAction = () => Act(new TradeAction(invite.Trade, TradeVerb.Accept)),
            CancelAction = () => Act(new TradeAction(invite.Trade, TradeVerb.Decline)),
            EscapeAction = () => Act(new TradeAction(invite.Trade, TradeVerb.Decline))
        });
    }

    internal static void Request(ushort target) => Send(SocialMessage.Encode(SyncMsg.TradeRequest, new TradeRequest(target)));

    internal static void Act(TradeAction action) => Send(SocialMessage.Encode(SyncMsg.TradeAction, action));

    private static void Send(byte[] data)
    {
        if (AdminAccess.Hosting) AdminAccess.Host.LocalTrade(data);
        else AdminAccess.Client?.SendTrade(data);
    }

    internal static void Notice(string text) => ChatLog.Add(new ChatLine("Trade", 0, text, ChatKind.System, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), MpClock.NowMs());

    /// <summary>"/trade name" asks that player to trade; never sent as chat.</summary>
    internal static bool Command(string text)
    {
        if (!text.StartsWith("/trade", StringComparison.OrdinalIgnoreCase)) return false;
        string name = text[(text.IndexOf(' ') is int space and > 0 ? space + 1 : text.Length)..].Trim();
        if (name.Length == 0) { Notice("Usage: /trade <player name>"); return true; }
        var players = AdminAccess.Hosting ? AdminAccess.Host.PlayerTable() : AdminAccess.Client?.Players.ToList();
        var target = players?.Where(p => p.Connected).FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (target is not { ClientId: not 0 } found) { Notice("No player named '" + name + "' is online."); return true; }
        Request(found.ClientId);
        return true;
    }

    internal static string ItemName(Guid item)
    {
        if (item == Guid.Empty) return "Credits";
        if (Definition(item) is not ItemDefinition def) return "Unknown item";
        string text = null;
        try { text = def.DisplayName.EvaluateText(); } catch (Exception) { }
        return string.IsNullOrWhiteSpace(text) || text.Contains('{') ? def.DebugNameShort : text;
    }

    internal static ItemDefinition Definition(Guid item)
    {
        try { return item != Guid.Empty && Singleton<DefinitionManager>.Instance.TryGetDefinition(item, out ItemDefinition def) ? def : null; }
        catch (Exception) { return null; }
    }

    internal static string Amount(long raw) => (raw / 1_000_000.0).ToString("0.##", CultureInfo.InvariantCulture);

    internal static long Raw(double amount) => (long)Math.Round(amount * 1_000_000);
}

