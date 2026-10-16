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
    private readonly ListBox _grids = OnlineUi.List("OwnershipGridList");
    private readonly Avalonia.Controls.Primitives.SelectingItemsControl _players;
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
        _players = focus != null ? OnlineUi.PlayerPicker("Transfer to player") : OnlineUi.List("OwnershipPlayerList");
        _players.Name = "OwnershipPlayerList";
        _grids.ItemTemplate = new FuncDataTemplate<SocialGrid>((g, _) => g == null ? null : OnlineUi.ListCells("*,220,150", g.Name, PlayerName(g.Owner), g.Sharing.ToString()));
        if (focus == null) _players.ItemTemplate = new FuncDataTemplate<SocialPlayer>((p, _) => p == null ? null : OnlineUi.ListCells("*", p.Name));
        _search.Width = 480; _search.TextChanged += (_, _) => RefreshPage(0);
        _grids.SelectionChanged += (_, _) => RefreshActions(); _players.SelectionChanged += (_, _) => RefreshActions();
        _private = OnlineUi.Button("Private", () => Share(GridSharing.Private), "GridSharePrivate");
        _faction = OnlineUi.Button("Faction", () => Share(GridSharing.Faction), "GridShareFaction");
        _everyone = OnlineUi.Button("Everyone", () => Share(GridSharing.Everyone), "GridShareEveryone");
        _claim = OnlineUi.Button("Claim grid", () => Send(SocialAction.ClaimGrid), "ClaimGrid");
        _transfer = OnlineUi.Button("Transfer ownership", () => { if (_focus != null && DialogHost != null) OpenTransferDialog(); else Confirm("Transfer ownership", "Transfer this grid to the selected player? Its sharing resets to Private.", SocialAction.TransferGrid); }, "TransferGrid");
        _release = OnlineUi.Button("Release ownership", () => Confirm("Release ownership", "Release this grid? Everyone can use it and any player can claim it.", SocialAction.ReleaseGrid), "ReleaseGrid");
        _previous = OnlineUi.Button("Previous", () => RefreshPage(_snapshot.Offset - 100), "PreviousOwnershipPage");
        _next = OnlineUi.Button("Next", () => RefreshPage(_snapshot.Offset + 100), "NextOwnershipPage");
        foreach (var button in new[] { _private, _faction, _everyone, _claim, _transfer, _release }) { button.FontSize = 20; button.Padding = new Thickness(12, 8); }
        if (focus != null) { Focused = BuildCompact(); RefreshControls(); return; }
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
        Navigation = OnlineUi.Row(_search, OnlineUi.Button("Refresh", () => RefreshPage(_snapshot.Offset), "RefreshOwnership"));
        Body = OnlineUi.Columns(OnlineUi.Panel(left), OnlineUi.Panel(right), "*,24,640");
        Actions = OnlineUi.Row(OnlineUi.Button("Factions", SocialUi.OpenFactions, "OwnershipFactions"), HackUi.LockdownButton(() => SelectedGrid?.NetId ?? 0));
        EmbeddedActions = OnlineUi.Row(HackUi.LockdownButton(() => SelectedGrid?.NetId ?? 0));
        RefreshControls(); RefreshPage(0);
    }
    /// <summary>
    /// The control panel's version: it sits under SE2's own grid controls, so it is a few native-sized rows (owner and
    /// sharing on one line, only the actions that apply, a drop-down for the transfer target) instead of the full column.
    /// </summary>
    private Control BuildCompact()
    {
        foreach (var button in new[] { _private, _faction, _everyone, _claim, _transfer, _release }) { button.MinHeight = 40; button.MinWidth = 96; button.Padding = new Thickness(14, 4); }
        _transfer.Content = "Transfer ownership"; _claim.Content = "Claim"; _release.Content = "Release";
        ToolTip.SetTip(_private, "Only you can use this grid.");
        ToolTip.SetTip(_faction, "Your faction members can use this grid.");
        ToolTip.SetTip(_everyone, "All players can use this grid.");
        _details.Classes.Remove("SubTitle"); _details.FontSize = 20;
        _details.TextWrapping = Avalonia.Media.TextWrapping.NoWrap; _details.TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis;
        _transferRow = _transfer;
        var stack = new StackPanel { Spacing = 6 };
        stack.Children.Add(_details);
        stack.Children.Add(OnlineUi.Row(_private, _faction, _everyone, _claim, _release, _transfer));
        _confirmTransfer = OnlineUi.SmallButton("Transfer", ConfirmTransfer, "TransferGridConfirm");
        _players.SelectionChanged += (_, _) => _confirmTransfer.IsEnabled = _players.SelectedItem is SocialPlayer;
        return OnlineUi.Panel(stack, new Thickness(16, 12, 16, 2));
    }
    private Control _transferRow, _dialog;
    private Button _confirmTransfer;
    private TextBlock _dialogGrid;

    /// <summary>The control panel root the transfer dialog covers; set by the control panel hook.</summary>
    internal Panel DialogHost;

    private void OpenTransferDialog()
    {
        if (_dialog == null)
        {
            _dialogGrid = OnlineUi.Text("", size: 20);
            _dialogGrid.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
            var note = OnlineUi.Text("The new owner gets the whole grid and its sharing resets to Private. You can't undo this yourself.", "SubtleNotice", 17);
            note.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
            _players.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
            var buttons = OnlineUi.Row(_confirmTransfer, OnlineUi.SmallButton("Cancel", CloseTransferDialog, "TransferGridCancel"));
            buttons.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right;
            var body = new StackPanel { Spacing = 12, Width = 520 };
            body.Children.Add(OnlineUi.Text("TRANSFER OWNERSHIP", "SubTitle", 24));
            body.Children.Add(_dialogGrid);
            body.Children.Add(_players);
            body.Children.Add(note);
            body.Children.Add(buttons);
            // SE2's panel surface is see-through; the dialog sits over live controls, so it gets a solid backing.
            var card = new Border
            {
                Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(10, 18, 26)), Child = OnlineUi.Panel(body, new Thickness(24)),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            var dim = new Border { Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(220, 0, 0, 0)), Child = card };
            Grid.SetColumnSpan(dim, 64); Grid.SetRowSpan(dim, 64); dim.ZIndex = 1000;
            _dialog = dim;
            // Posted: removing a child while Avalonia walks the detaching tree threw out of range and crashed the game.
            Focused.DetachedFromVisualTree += (_, _) => Dispatcher.UIThread.Post(CloseTransferDialog);
        }
        var grid = SelectedGrid;
        _dialogGrid.Text = "Transfer \"" + (grid?.Name ?? "this grid") + "\" to:";
        _players.SelectedItem = null;
        _confirmTransfer.IsEnabled = false;
        if (!DialogHost.Children.Contains(_dialog)) DialogHost.Children.Add(_dialog);
    }

    private void CloseTransferDialog()
    {
        if (_dialog != null && DialogHost?.Children.Contains(_dialog) == true) DialogHost.Children.Remove(_dialog);
    }

    private void ConfirmTransfer()
    {
        if (_players.SelectedItem is not SocialPlayer || SelectedGrid == null) return;
        Send(SocialAction.TransferGrid);
        CloseTransferDialog();
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
        _transfer.IsEnabled = owner && (_focus != null ? _players.ItemCount > 0 : _players.SelectedItem is SocialPlayer);
        _release.IsEnabled = owner;
        if (_focus != null)
        {
            _details.Text = grid == null ? "Loading ownership..." : "Owner: " + PlayerName(grid.Owner) + "     Sharing: " + grid.Sharing;
            _private.IsVisible = _faction.IsVisible = _everyone.IsVisible = _release.IsVisible = owner;
            _claim.IsVisible = _claim.IsEnabled;
            _transferRow.IsVisible = owner && _players.ItemCount > 0;
            if (!owner) CloseTransferDialog();
            return;
        }
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
            // SE2's middle column: title, the on/off row with the copy/paste actions, the controls area (a fixed-height
            // scroll viewer centred in a star row), then a toolbar row. Our controls go into that grid so they follow
            // its layout at every resolution.
            if (__instance.BlockControls?.Content is not Grid controls || controls.RowDefinitions.Count != 7
                || controls.Children.OfType<StackPanel>().FirstOrDefault(s => s.Classes.Contains("BlockControlsActions")) is not { } actions
                || controls.Children.OfType<ScrollViewer>().FirstOrDefault(s => Grid.GetRow(s) == 4) is not { } nativeScroll)
            {
                GameLog.Instance.Warn("grid ownership: the control panel layout was not recognised; ownership and block access controls are unavailable");
                return;
            }
            Guid gridId = Guid.Empty;
            long askedMs = 0;
            var grids = new GridOwnershipView(() => gridId != Guid.Empty && SocialUi.Snapshot.Focus is { } focus && focus.Secure == gridId ? focus : null);
            var blocks = new BlockAccessView();
            grids.DialogHost = (__instance.Content as Panel) ?? controls;
            var gridPanel = grids.Focused;
            var blockPanel = blocks.Panel;
            foreach (var panel in new[] { gridPanel, blockPanel })
            {
                panel.Margin = new Thickness(16, 0); panel.IsVisible = false; panel.ZIndex = 10;
                Grid.SetRow(panel, 4); Grid.SetColumnSpan(panel, 2);
                controls.Children.Add(panel);
            }
            // A grid's own controls are two rows at the top of the controls area; the free space is at its bottom.
            gridPanel.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom;
            // A block's controls fill the area, so its access lists open on demand from a button beside copy/paste.
            blockPanel.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
            var toggle = OnlineUi.SmallButton("Block access", () => { blockPanel.IsVisible = !blockPanel.IsVisible; blocks.Refresh(); }, "BlockAccessToggle");
            toggle.MinWidth = 0; toggle.IsVisible = false; toggle.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
            actions.Children.Insert(0, toggle);

            // Our panel takes its height out of SE2's controls area instead of covering it: the native scroll viewer
            // (fixed height, centred in its row) moves below the block panel or ends above the grid panel, and scrolls.
            double nativeHeight = nativeScroll.Height;
            var nativeMargin = nativeScroll.Margin;
            var nativeAlignment = nativeScroll.VerticalAlignment;
            void Reserve()
            {
                var open = blockPanel.IsVisible ? blockPanel : gridPanel.IsVisible ? gridPanel : null;
                double row = controls.RowDefinitions[4].ActualHeight, used = open == null ? 0 : open.Bounds.Height + 8;
                if (open == null || used <= 8 || row <= 0 || double.IsNaN(nativeHeight))
                {
                    nativeScroll.Height = nativeHeight; nativeScroll.Margin = nativeMargin; nativeScroll.VerticalAlignment = nativeAlignment;
                    return;
                }
                double top = Math.Max(0, (row - nativeHeight) / 2);
                if (open == blockPanel) top = Math.Max(top, used);
                double height = Math.Max(80, Math.Min(nativeHeight, row - top - (open == gridPanel ? used : 0)));
                nativeScroll.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
                nativeScroll.Margin = new Thickness(nativeMargin.Left, top, nativeMargin.Right, 0);
                nativeScroll.Height = height;
            }
            foreach (var panel in new[] { gridPanel, blockPanel })
                panel.PropertyChanged += (_, e) => { if (e.Property == Visual.BoundsProperty || e.Property == Visual.IsVisibleProperty) Dispatcher.UIThread.Post(Reserve, DispatcherPriority.Background); };

            ControlPanelViewModel model = null;
            void Select()
            {
                bool online = SocialUi.Ready;
                var grid = online ? model?.SelectedGridModel : null;
                var selection = model?.BlockTerminalSelection;
                var block = online && grid == null && selection is { Count: 1 } && selection[0] is ControlPanelEntityViewModel entity ? entity : null;
                gridPanel.IsVisible = grid != null;
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
                    // The host drops requests over five a second; ask again until this grid is the focus.
                    if (SocialUi.Snapshot.Focus?.Secure != gridId && now - askedMs > 1000) { askedMs = now; SocialUi.Send(new SocialRequest(SocialAction.Refresh, Secure: gridId)); }
                    grids.QueueRefresh();
                }
                if (toggle.IsVisible) blocks.Refresh();
                Reserve();
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
