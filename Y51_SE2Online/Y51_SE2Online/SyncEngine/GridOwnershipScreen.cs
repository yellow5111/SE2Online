using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Threading;
using Keen.Game2.Client.UI.TerminalScreen.ControlPanel;
using Keen.VRage.UI.Screens;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

/// <summary>The grid ownership controls, shown in the terminal's tab and in the pause menu's screen.</summary>
internal sealed class GridOwnershipView
{
    private readonly ListBox _grids = OnlineUi.List("OwnershipGridList"), _players = OnlineUi.List("OwnershipPlayerList");
    private readonly TextBox _search = OnlineUi.Input("Search grids", 100);
    private readonly TextBlock _status = OnlineUi.Text(""), _details = OnlineUi.Text("Select a grid.", "SubTitle", 28), _page = OnlineUi.Text(""), _empty = OnlineUi.Text("No grids available.");
    private readonly Button _private, _faction, _everyone, _claim, _transfer, _release, _previous, _next;
    private SocialSnapshot _snapshot = new();
    private string _signature, _playersSignature;
    private bool _closed;
    private readonly Func<SocialGrid> _focus;

    internal Control Navigation, Body, Actions, EmbeddedActions, Focused;
    internal TextBlock Status => _status;
    internal bool Closed { set => _closed = value; }

    /// <param name="focus">One fixed grid (the control panel's selection) instead of the searchable list.</param>
    public GridOwnershipView(Func<SocialGrid> focus = null)
    {
        _focus = focus;
        _grids.ItemTemplate = new FuncDataTemplate<SocialGrid>((g, _) => g == null ? null : OnlineUi.ListCells("*,220,150", g.Name, PlayerName(g.Owner), g.Sharing.ToString()));
        _players.ItemTemplate = new FuncDataTemplate<SocialPlayer>((p, _) => p == null ? null : OnlineUi.ListCells("*", p.Name));
        _search.Width = 480; _search.TextChanged += (_, _) => RefreshPage(0);
        _grids.SelectionChanged += (_, _) => RefreshActions(); _players.SelectionChanged += (_, _) => RefreshActions();
        _private = OnlineUi.Button("Private", () => Share(GridSharing.Private), "GridSharePrivate");
        _faction = OnlineUi.Button("Faction", () => Share(GridSharing.Faction), "GridShareFaction");
        _everyone = OnlineUi.Button("Everyone", () => Share(GridSharing.Everyone), "GridShareEveryone");
        _claim = OnlineUi.Button("Claim grid", () => Send(SocialAction.ClaimGrid), "ClaimGrid");
        _transfer = OnlineUi.Button("Transfer ownership", () => Confirm("Transfer ownership", "Transfer this grid to the selected player? Its sharing resets to Private.", SocialAction.TransferGrid), "TransferGrid");
        _release = OnlineUi.Button("Release ownership", () => Confirm("Release ownership", "Release this grid? Everyone can use it and any player can claim it.", SocialAction.ReleaseGrid), "ReleaseGrid");
        _previous = OnlineUi.Button("Previous", () => RefreshPage(_snapshot.Offset - 100), "PreviousOwnershipPage");
        _next = OnlineUi.Button("Next", () => RefreshPage(_snapshot.Offset + 100), "NextOwnershipPage");
        foreach (var button in new[] { _private, _faction, _everyone, _claim, _transfer, _release }) { button.FontSize = 20; button.Padding = new Thickness(12, 8); }
        var left = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        left.Children.Add(OnlineUi.Cells("*,220,150", "GRID", "OWNER", "SHARING")); Grid.SetRow(_grids, 1); left.Children.Add(_grids);
        Grid.SetRow(_empty, 1); _empty.IsHitTestVisible = false; left.Children.Add(_empty);
        var pages = OnlineUi.Row(_previous, _page, _next); Grid.SetRow(pages, 2); pages.Margin = new Thickness(0, 12, 0, 0); left.Children.Add(pages);
        var right = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,*,Auto,Auto") };
        right.Children.Add(_details);
        var share = OnlineUi.Row(_private, _faction, _everyone); share.Margin = new Thickness(0, 16, 0, 14); Grid.SetRow(share, 1); right.Children.Add(share);
        var help = OnlineUi.Text("Private: only you. Faction: your faction members. Everyone: all players. Only the owner changes sharing or transfers ownership.", "SubtleNotice", 21); Grid.SetRow(help, 2); right.Children.Add(help);
        var label = OnlineUi.Text("TRANSFER TO PLAYER", "SubTitle"); label.Margin = new Thickness(0, 20, 0, 10); Grid.SetRow(label, 3); right.Children.Add(label);
        Grid.SetRow(_players, 4); right.Children.Add(_players);
        var transfer = OnlineUi.Row(_transfer, _claim); transfer.Margin = new Thickness(0, 14, 0, 10); Grid.SetRow(transfer, 5); right.Children.Add(transfer);
        Grid.SetRow(_release, 6); right.Children.Add(_release);
        // The focused panel owns the right-hand column itself; a control can have only one parent.
        if (focus != null) { Focused = OnlineUi.Panel(right, new Thickness(20)); RefreshControls(); return; }
        Navigation = OnlineUi.Row(_search, OnlineUi.Button("Refresh", () => RefreshPage(_snapshot.Offset), "RefreshOwnership"));
        Body = OnlineUi.Columns(OnlineUi.Panel(left), OnlineUi.Panel(right), "*,24,640");
        Actions = OnlineUi.Row(OnlineUi.Button("Factions", SocialUi.OpenFactions, "OwnershipFactions"));
        EmbeddedActions = OnlineUi.Row();
        RefreshControls(); RefreshPage(0);
    }
    private SocialGrid SelectedGrid => _focus != null ? _focus() : _grids.SelectedItem as SocialGrid;
    internal void QueueRefresh() => Dispatcher.UIThread.Post(() => { if (!_closed) RefreshControls(); });
    private void RefreshPage(int offset) => SocialUi.Send(new(SocialAction.Refresh, Name: _search.Text ?? "", Offset: Math.Max(0, offset)));
    private string PlayerName(long id) => id == 0 ? "Unowned" : PlayerIdentities.IsNpc(id) ? "NPC / Scenario" : _snapshot.Players.FirstOrDefault(p => p.Identity == id)?.Name ?? "Player " + id;
    private void Share(GridSharing sharing) => Send(SocialAction.ShareGrid, sharing);
    private SocialRequest Request(SocialAction action, GridSharing sharing = GridSharing.Private) => new(action, Player: (_players.SelectedItem as SocialPlayer)?.Identity ?? 0, Grid: SelectedGrid?.NetId ?? 0, Sharing: sharing);
    private void Send(SocialAction action, GridSharing sharing = GridSharing.Private) => SocialUi.Send(Request(action, sharing));
    private void Confirm(string title, string text, SocialAction action)
    {
        var request = Request(action); SocialUi.Confirm(title, text, () => SocialUi.Send(request));
    }
    private void RefreshControls()
    {
        _snapshot = SocialUi.Snapshot;
        _status.Text = _snapshot.Actor == 0 ? "Start or join a multiplayer world to manage grid ownership." : _snapshot.Message;
        string signature = _snapshot.Revision + ":" + string.Join("|", _snapshot.Grids.Select(g => g.NetId + ":" + g.Name + ":" + g.Owner + ":" + g.Sharing));
        if (_signature != signature)
        {
            uint selected = (_grids.SelectedItem as SocialGrid)?.NetId ?? 0;
            _signature = signature; _grids.ItemsSource = _snapshot.Grids; _grids.SelectedItem = _snapshot.Grids.FirstOrDefault(g => g.NetId == selected);
        }
        string players = _snapshot.Revision.ToString();
        if (_playersSignature != players)
        {
            long selected = (_players.SelectedItem as SocialPlayer)?.Identity ?? 0;
            _playersSignature = players; var rows = _snapshot.Players.Where(p => p.Identity != _snapshot.Actor).OrderBy(p => p.Name).ToArray();
            _players.ItemsSource = rows; _players.SelectedItem = rows.FirstOrDefault(p => p.Identity == selected);
        }
        _page.Text = _snapshot.TotalGrids == 0 ? "0 grids" : (_snapshot.Offset + 1) + " - " + (_snapshot.Offset + _snapshot.Grids.Length) + " / " + _snapshot.TotalGrids;
        _previous.IsEnabled = _snapshot.Actor != 0 && _snapshot.Offset > 0; _next.IsEnabled = _snapshot.Actor != 0 && _snapshot.Offset + _snapshot.Grids.Length < _snapshot.TotalGrids;
        _empty.IsVisible = _snapshot.Grids.Length == 0; RefreshActions();
    }
    private void RefreshActions()
    {
        if (_claim == null) return;
        var grid = SelectedGrid;
        bool owner = grid != null && _snapshot.Actor != 0 && grid.Owner == _snapshot.Actor;
        _private.IsEnabled = _everyone.IsEnabled = owner;
        _faction.IsEnabled = owner && _snapshot.Factions.Any(f => f.Members.Contains(_snapshot.Actor));
        _claim.IsEnabled = grid != null && _snapshot.Actor != 0 && grid.Owner == 0;
        _transfer.IsEnabled = owner && _players.SelectedItem is SocialPlayer;
        _release.IsEnabled = owner;
        _details.Text = grid == null ? "Select a grid to view ownership and sharing." : grid.Name + "\nOwner: " + PlayerName(grid.Owner) + "\nSharing: " + grid.Sharing;
    }
}

public sealed class GridOwnershipScreen : ScreenView
{
    private readonly GridOwnershipView _view = new();
    private SocialScreenViewModel _model;

    public GridOwnershipScreen()
    {
        Content = OnlineUi.Shell(this, "GRID OWNERSHIP", _view.Navigation, _view.Status, _view.Body, _view.Actions);
        OnDisposing += _ => { _view.Closed = true; if (_model != null) _model.Refresh -= _view.QueueRefresh; };
    }
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_model != null) _model.Refresh -= _view.QueueRefresh;
        _model = DataContext as SocialScreenViewModel; if (_model != null) _model.Refresh += _view.QueueRefresh;
    }
}

/// <summary>
/// The terminal control panel: selecting the grid's own row shows its ownership controls, selecting one block shows
/// that block's whitelist and blacklist.
/// </summary>
internal static class ControlPanelOwnership
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ControlPanelScreen, object> Built = new();

    // A class handler, not a constructor patch: the screen's constructor is a one-line InitializeComponent call that
    // the precompiled caller inlines, so a Harmony patch on it never runs.
    internal static void Install() => Control.LoadedEvent.AddClassHandler<ControlPanelScreen>((screen, _e) =>
    {
        if (Built.TryGetValue(screen, out object _)) return;
        Built.Add(screen, null);
        Build(screen);
    });

    private static void Build(ControlPanelScreen __instance)
    {
        try
        {
            if (__instance.Content is not Grid root || root.Children.OfType<Grid>().FirstOrDefault(g => g.ColumnDefinitions.Count == 3) is not { } columns)
            {
                GameLog.Instance.Warn("grid ownership: the control panel layout was not recognised; ownership and block access controls are unavailable");
                return;
            }
            string gridName = null;
            Guid gridId = Guid.Empty;
            long askedMs = 0;
            var grids = new GridOwnershipView(() => gridName != null && SocialUi.Snapshot.Focus is { } focus && focus.Name == gridName ? focus : null);
            var blocks = new BlockAccessView();
            var gridPanel = grids.Focused;
            var blockPanel = blocks.Panel;
            foreach (var panel in new[] { gridPanel, blockPanel })
            {
                panel.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom;
                panel.MaxHeight = 600; panel.Margin = new Thickness(16); panel.IsVisible = false;
                Grid.SetColumn(panel, 1);
                columns.Children.Add(panel);
            }
            // The block panel sits over the block's own controls, so it opens on demand.
            var toggle = OnlineUi.Button("Block access", () => { blockPanel.IsVisible = !blockPanel.IsVisible; blocks.Refresh(); }, "BlockAccessToggle");
            toggle.FontSize = 20; toggle.MinHeight = 44; toggle.Padding = new Thickness(14, 6);
            toggle.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right; toggle.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom;
            toggle.Margin = new Thickness(16); toggle.IsVisible = false;
            Grid.SetColumn(toggle, 1);
            columns.Children.Add(toggle);
            blockPanel.Margin = new Thickness(16, 16, 16, 76);

            ControlPanelViewModel model = null;
            void Select()
            {
                bool online = SocialUi.Ready;
                var grid = online ? model?.SelectedGridModel : null;
                var selection = model?.BlockTerminalSelection;
                var block = online && grid == null && selection is { Count: 1 } && selection[0] is ControlPanelEntityViewModel entity ? entity : null;
                gridPanel.IsVisible = grid != null;
                gridName = grid == null ? null : SocialState.Clean(grid.DisplayName, 100);
                var id = grid?.EntityId.EntityGuid ?? Guid.Empty;
                if (id != gridId) askedMs = 0;
                gridId = id;
                toggle.IsVisible = block != null;
                if (block == null) blockPanel.IsVisible = false;
                blocks.Select(block?.Model.EntityId.EntityGuid ?? Guid.Empty);
                grids.QueueRefresh();
            }
            void Changed(object sender, System.ComponentModel.PropertyChangedEventArgs e)
            {
                if (e.PropertyName is nameof(ControlPanelViewModel.SelectedGridModel) or nameof(ControlPanelViewModel.BlockTerminalSelection)) Select();
            }
            // Polled as well: clearing the selection raises no property change.
            var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) =>
            {
                Select();
                if (model != null) NameSync.Follow(model);
                if (gridPanel.IsVisible)
                {
                    long now = Core.MpClock.NowMs();
                    // The host drops requests closer than 200 ms apart; ask again until this grid is the focus.
                    if ((SocialUi.Snapshot.Focus?.Name != gridName) && now - askedMs > 1000) { askedMs = now; SocialUi.Send(new SocialRequest(SocialAction.Refresh, Secure: gridId)); }
                    grids.QueueRefresh();
                }
                if (toggle.IsVisible) blocks.Refresh();
            });
            timer.Stop();
            void Bind()
            {
                if (model != null) model.PropertyChanged -= Changed;
                model = __instance.DataContext as ControlPanelViewModel;
                if (model != null) model.PropertyChanged += Changed;
                Select();
            }
            __instance.DataContextChanged += (_, _) => Bind();
            __instance.AttachedToVisualTree += (_, _) => timer.Start();
            __instance.DetachedFromVisualTree += (_, _) => timer.Stop();
            Bind();
            timer.Start();
            GameLog.Instance.Info("grid ownership: control panel ownership and block access controls added");
        }
        catch (Exception ex) { GameLog.Instance.Warn("grid ownership: control panel hook failed: " + ex.Message); }
    }
}
