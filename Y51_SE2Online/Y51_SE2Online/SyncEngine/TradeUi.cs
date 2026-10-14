using System;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Threading;
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
                var state = SocialMessage.Decode<TradeState>(data);
                if (state.Names?.Length != 2 || state.Offers?.Length != 2 || state.Ready?.Length != 2 || state.You is < 0 or > 1) return;
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
        if (!Singleton<DefinitionManager>.Instance.TryGetDefinition(item, out ItemDefinition def)) return "Unknown item";
        string text = null;
        try { text = def.DisplayName.EvaluateText(); } catch (Exception) { }
        return string.IsNullOrWhiteSpace(text) || text.Contains('{') ? def.DebugNameShort : text;
    }

    internal static string Amount(long raw) => (raw / 1_000_000.0).ToString("0.##", CultureInfo.InvariantCulture);

    internal static long Raw(double amount) => (long)Math.Round(amount * 1_000_000);
}

/// <summary>Both offers side by side; editing your offer clears both acceptances, so a change is a counter offer.</summary>
public sealed class TradeScreen : ScreenView
{
    private sealed record Row(Guid Item, long Raw, string Name) { public override string ToString() => Name; }

    private readonly ListBox _have = OnlineUi.List("TradeInventory"), _mine = OnlineUi.List("TradeMyOffer"), _theirs = OnlineUi.List("TradeTheirOffer");
    private readonly TextBox _amount = OnlineUi.Input("Amount", 16);
    private readonly TextBlock _status = OnlineUi.Text(""), _mineTitle = OnlineUi.Text("YOUR OFFER", "SubTitle", 26), _theirsTitle = OnlineUi.Text("THEIR OFFER", "SubTitle", 26);
    private readonly Button _offer, _all, _remove, _accept, _withdraw, _cancel;
    private SocialScreenViewModel _model;
    private long _shown = -1;
    private bool _closing;

    public TradeScreen()
    {
        var template = new FuncDataTemplate<Row>((r, _) => r == null ? null : OnlineUi.ListCells("*,140", r.Name, TradeUi.Amount(r.Raw)));
        _have.ItemTemplate = _mine.ItemTemplate = _theirs.ItemTemplate = template;
        _amount.Text = "1";
        _offer = OnlineUi.Button("Offer amount", () => Offer(false), "TradeOffer");
        _all = OnlineUi.Button("Offer all", () => Offer(true), "TradeOfferAll");
        _remove = OnlineUi.Button("Remove from offer", () => { if (_mine.SelectedItem is Row r) Set(r.Item, 0); }, "TradeRemove");
        _accept = OnlineUi.Button("Accept trade", Accept, "TradeAccept", true);
        _withdraw = OnlineUi.Button("Withdraw acceptance", () => Verb(TradeVerb.Unready), "TradeWithdraw");
        _cancel = OnlineUi.Button("Cancel trade", () => { _closing = true; Verb(TradeVerb.Cancel); _model?.Close(); }, "TradeCancel");
        var columns = new Grid { ColumnDefinitions = new ColumnDefinitions("*,24,*,24,*") };
        void Column(int at, Control title, Control list, params Control[] actions)
        {
            var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
            grid.Children.Add(title);
            Grid.SetRow(list, 1); grid.Children.Add(list);
            var row = OnlineUi.Row(actions); Grid.SetRow(row, 2); grid.Children.Add(row);
            var panel = OnlineUi.Panel(grid, new Thickness(18));
            Grid.SetColumn(panel, at); columns.Children.Add(panel);
        }
        Column(0, OnlineUi.Text("YOUR INVENTORY", "SubTitle", 26), _have, _amount, _offer, _all);
        Column(2, _mineTitle, _mine, _remove);
        Column(4, _theirsTitle, _theirs);
        Content = OnlineUi.Shell(this, "TRADE", OnlineUi.Row(), _status, columns, OnlineUi.Row(_accept, _withdraw, _cancel));
        OnDisposing += _ => { if (!_closing && TradeUi.State is { Closed: false } s) TradeUi.Act(new TradeAction(s.Trade, TradeVerb.Cancel)); };
        Refresh();
    }

    private void Offer(bool all)
    {
        if (_have.SelectedItem is not Row row) { _status.Text = "Select something in your inventory first."; return; }
        long raw = row.Raw;
        if (!all)
        {
            if (!double.TryParse(_amount.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double amount) || amount <= 0) { _status.Text = "Enter an amount above zero."; return; }
            raw = Math.Min(row.Raw, TradeUi.Raw(amount));
        }
        Set(row.Item, raw);
    }

    private static void Set(Guid item, long raw)
    {
        if (TradeUi.State is { Closed: false } s) TradeUi.Act(new TradeAction(s.Trade, TradeVerb.Set, item, raw));
    }

    private static void Verb(TradeVerb verb)
    {
        if (TradeUi.State is { Closed: false } s) TradeUi.Act(new TradeAction(s.Trade, verb));
    }

    private static void Accept()
    {
        if (TradeUi.State is { Closed: false } s) TradeUi.Act(new TradeAction(s.Trade, TradeVerb.Ready, Raw: s.Version));
    }

    private void Refresh()
    {
        long version = TradeUi.StateVersion;
        if (version == _shown) return;
        _shown = version;
        var state = TradeUi.State;
        if (state == null || state.Closed) return;
        int you = state.You, them = 1 - you;
        static Row[] Rows(TradeLine[] lines) => (lines ?? Array.Empty<TradeLine>()).Where(l => l != null && l.Raw > 0).Take(200)
            .Select(l => new Row(l.Item, l.Raw, TradeUi.ItemName(l.Item))).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        Guid? keep = (_have.SelectedItem as Row)?.Item;
        var have = Rows(state.Have);
        _have.ItemsSource = have;
        _have.SelectedItem = have.FirstOrDefault(r => r.Item == keep);
        _mine.ItemsSource = Rows(state.Offers[you]);
        _theirs.ItemsSource = Rows(state.Offers[them]);
        string other = SocialState.Clean(state.Names[them] ?? "", 80);
        _mineTitle.Text = "YOUR OFFER" + (state.Ready[you] ? "  - ACCEPTED" : "");
        _theirsTitle.Text = other.ToUpperInvariant() + "'S OFFER" + (state.Ready[them] ? "  - ACCEPTED" : "");
        string waiting = state.Ready[you] ? "Waiting for " + other + " to accept." : state.Ready[them] ? other + " accepted. Accept to complete the trade." : "Change your offer or accept the current one. Any change asks both of you to accept again.";
        _status.Text = string.IsNullOrEmpty(state.Message) ? waiting : SocialState.Clean(state.Message, 200) + "  " + waiting;
        _accept.IsEnabled = !state.Ready[you];
        _withdraw.IsEnabled = state.Ready[you];
    }

    private void QueueRefresh() => Dispatcher.UIThread.Post(Refresh);

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_model != null) _model.Refresh -= QueueRefresh;
        _model = DataContext as SocialScreenViewModel;
        if (_model != null) _model.Refresh += QueueRefresh;
    }
}
