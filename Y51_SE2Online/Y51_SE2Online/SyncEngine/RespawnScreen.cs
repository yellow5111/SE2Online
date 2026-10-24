using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Keen.VRage.Library.Utils;
using Keen.VRage.UI.Screens;
using Y51_SE2Online.Core;
using Y51_SE2Online.KeenNetcode;
using Y51_SE2Online.Sync;
using C = Y51_SE2Online.SyncEngine.RespawnCardsUi;
using L = Y51_SE2Online.Sync.LoadScreenLayout;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// The fallback spawn screen, when the game's own respawn screen cannot show the menu (<see cref="InGameRespawn"/>).
/// Where a player spawns: home base, their medbays, faction and shared medbays, the suit, respawn ships and planet landers,
/// as cards in the server's order (choosing card i sends i back). A selected card is confirmed with the big button,
/// a double click or Enter (the gamepad's A is Enter, its D-pad the arrow keys). If the cards cannot be built, a plain list.
/// </summary>
public sealed class RespawnScreen : ScreenView
{
    private static IObservableDisposable _open;
    private static SpawnMenu _shown;

    private SocialScreenViewModel _model;
    private SpawnMenu _menu;
    private bool _plain, _updating;

    private ListBox _cards;
    private Grid _column;
    private C.ShipPanel _panel;
    private C.Card _hover;
    // The ship whose destinations are open under it (its group name), "" for none.
    private string _expanded = "";
    private bool _building;
    private TextBlock _title, _death, _message, _summary, _hint;
    private Button _respawn;

    private ListBox _options;
    private TextBlock _status;
    private Button _spawn;

    /// <summary>Opens the screen while the host waits for our choice, and closes it once we have chosen. Only when the
    /// game's own respawn screen cannot show the menu (<see cref="InGameRespawn.Fallback"/>).</summary>
    internal static void Tick()
    {
        var menu = InGameRespawn.Fallback ? KeenRespawn.Menu : null;
        if (menu == null)
        {
            if (_open != null) { _open.Dispose(); _open = null; }
            _shown = null;
            return;
        }
        if (_open != null && ReferenceEquals(menu, _shown)) return;
        _shown = menu;
        if (_open != null) return;
        var shared = JoinStatusUi.FindShared(MpPlugin.App); if (shared == null) return;
        _open = shared.CreateScreen<RespawnScreen>(new SocialScreenViewModel(), true, ScreenLayer.Top);
        _open.OnDisposed += _ => _open = null;
    }

    public RespawnScreen()
    {
        try { BuildCards(); }
        catch (Exception ex) { Plain(ex); return; }
        Refresh();
    }

    private void Plain(Exception why)
    {
        GameLog.Instance.WarnOnce("respawn-cards", $"spawn screen: the cards could not be shown ({why.GetType().Name}: {why.Message}); a plain list instead");
        _plain = true;
        _menu = null;
        Styles.Clear();
        BuildPlain();
        Refresh();
    }

    private void BuildCards()
    {
        lock (OnlineUi.Screens) { OnlineUi.Screens.RemoveAll(w => !w.TryGetTarget(out _)); OnlineUi.Screens.Add(new WeakReference<ScreenView>(this)); }
        C.Restyle(Styles);
        var column = _column = new Grid { Width = C.Column, Height = C.Height, HorizontalAlignment = HorizontalAlignment.Center, RowDefinitions = new RowDefinitions(C.Rows) };
        var brand = C.Wordmark(24);
        Grid.SetRow(brand, 1); column.Children.Add(brand);
        _title = C.Text("", 60, L.White, FontWeight.Bold);
        Grid.SetRow(_title, 2); column.Children.Add(_title);
        var accent = new Border { Width = 96, Height = 4, Background = C.Brush(L.Amber), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        Grid.SetRow(accent, 3); column.Children.Add(accent);
        _death = C.Wrap("", 24, L.Muted);
        _death.MinHeight = 40;
        Grid.SetRow(_death, 4); column.Children.Add(_death);
        _message = C.Wrap("", 20, L.Amber);
        _message.MinHeight = 34;
        Grid.SetRow(_message, 5); column.Children.Add(_message);

        _cards = new ListBox { Name = "SpawnCards", Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0, 0, OnlineUi.ScrollGutter, 0), SelectionMode = SelectionMode.Single };
        ScrollViewer.SetHorizontalScrollBarVisibility(_cards, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        _cards.SelectionChanged += (_, _) => Selected();
        _cards.DoubleTapped += (_, _) => Choose();
        // Tunnel: the list would take Right and the arrows for itself before a bubbling handler sees them.
        _cards.AddHandler(KeyDownEvent, CardKey, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Grid.SetRow(_cards, 7); column.Children.Add(_cards);

        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,24,Auto"), MinHeight = 88 };
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 4 };
        info.Children.Add(_summary = C.Wrap("", 26, L.White, FontWeight.Bold));
        info.Children.Add(_hint = C.Wrap("", 18, L.Muted));
        footer.Children.Add(info);
        _respawn = new Button { Name = "SpawnChoose", Width = 380, Height = 76, FontSize = 28, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        _respawn.Classes.Add("Respawn");
        _respawn.Click += (_, _) => Choose();
        Grid.SetColumn(_respawn, 2); footer.Children.Add(_respawn);
        Grid.SetRow(footer, 9); column.Children.Add(footer);

        // Beside the column, top-aligned with the cards; the column moves left while the menu has ships with pictures.
        _panel = new C.ShipPanel(C.PanelWidth) { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, -(C.PanelWidth + C.PanelGap), 0) };
        Grid.SetRow(_panel, 7); column.Children.Add(_panel);
        var frame = new Grid { Width = C.Width, Height = C.Height, Children = { column } };
        // The shade fills the whole screen (ultrawide too); the column scales uniformly from 1080p to 4K and stays centred.
        Content = new Grid { Background = C.Shade(), Children = { new Viewbox { Name = "OnlineSafeFrame", Stretch = Stretch.Uniform, Child = frame } } };
        AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(FocusSelected);
    }

    private void BuildPlain()
    {
        _options = OnlineUi.List("SpawnOptions");
        _status = OnlineUi.Text("");
        _options.ItemTemplate = new FuncDataTemplate<SpawnOption>((o, _) => o == null ? null : OnlineUi.ListCells("*,720", o.Name, o.Detail));
        _options.SelectionChanged += (_, _) => _spawn.IsEnabled = _options.SelectedItem is SpawnOption { Available: true };
        _options.DoubleTapped += (_, _) => Choose();
        _options.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Choose(); } };
        _spawn = OnlineUi.Button("Spawn", Choose, "SpawnChoose", true);
        var body = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        body.Children.Add(OnlineUi.Cells("*,720", "SPAWN POINT", "DETAILS"));
        Grid.SetRow(_options, 1); body.Children.Add(_options);
        Content = OnlineUi.Shell(this, "CHOOSE SPAWN", OnlineUi.Row(), _status, OnlineUi.Panel(body), OnlineUi.Row(_spawn));
    }

    private C.Card SelectedCard => _cards?.SelectedItem as C.Card;

    private void CardKey(object sender, KeyEventArgs e)
    {
        var card = SelectedCard;
        bool onButton = card != null && card.HomeButton.IsFocused;
        // Esc (the gamepad's B) closes a ship's destinations and goes back to the ship.
        if (e.Key == Key.Escape && _expanded.Length > 0) { e.Handled = true; Collapse(); return; }
        if (e.Key is Key.Enter or Key.Right && !onButton && card?.Group != null) { e.Handled = true; Expand(card.Group, true); return; }
        if (e.Key == Key.Enter && !onButton) { e.Handled = true; Choose(); }
        else if (e.Key == Key.H && card is { CanToggleHome: true }) { e.Handled = true; ToggleHome(card); }
        // The home button sits on the card; right reaches it from the list, the other directions come back.
        else if (e.Key == Key.Right && !onButton && card is { HomeButton.IsVisible: true }) { e.Handled = true; card.HomeButton.Focus(NavigationMethod.Directional); }
        else if (e.Key is Key.Left or Key.Up or Key.Down && onButton) { e.Handled = true; FocusSelected(); }
    }

    private void FocusSelected()
    {
        if (_cards == null) return;
        if (_cards.SelectedIndex >= 0 && _cards.ContainerFromIndex(_cards.SelectedIndex) is Control row) row.Focus(NavigationMethod.Directional);
        else _cards.Focus();
    }

    private void Choose()
    {
        if (_menu == null) return;
        int index = _plain
            ? _options.SelectedItem is SpawnOption { Available: true } option ? Array.IndexOf(_menu.Options, option) : -1
            : SelectedCard is { Option.Available: true, Group: null } card ? card.Index : -1;
        if (index < 0 || C.Updating || !ReferenceEquals(_menu, KeenRespawn.Menu)) return;
        KeenRespawn.Choose(index);
        _model?.Close();
    }

    // The server answers with a new menu (the home card first), which Refresh shows.
    private void ToggleHome(C.Card card)
    {
        C.SendHome(_menu, card.Option, card.Index);
        Selected();
    }

    private void Selected()
    {
        var card = SelectedCard;
        foreach (var item in _cards.Items.OfType<C.Card>()) item.Selected = ReferenceEquals(item, card) || card != null && card.IsDestinationCard && item.Group?.Name == card.Option.Group;
        _updating = C.Updating;
        // A ship alone is not a spawn point: only one of its destinations is.
        _respawn.IsEnabled = card is { Option.Available: true, Group: null } && !_updating;
        ShowPanel();
        if (card == null) { _summary.Text = ""; _hint.Text = ""; return; }
        _summary.Text = SpawnGroups.Label(card.Option);
        if (card.Group != null && !_building && card.Group.Name != _expanded) Dispatcher.UIThread.Post(() => Expand(card.Group, false));
        if (_updating) { _hint.Foreground = C.Brush(L.Amber); _hint.Text = "Updating..."; }
        else if (card.Group != null)
        {
            _hint.Foreground = C.Brush(L.Muted);
            _hint.Text = "Choose a destination below: Down or Enter (A) to move into the list, Esc (B) to close it";
        }
        else if (card.Option.Available)
        {
            _hint.Foreground = C.Brush(L.Muted);
            _hint.Text = "Enter or A to " + (_menu?.Respawn == false ? "spawn" : "respawn") + " here, Up and Down to choose" + (card.CanToggleHome ? ", H for " + (card.Option.Home ? "clear home" : "set as home") : "")
                + (card.IsDestinationCard ? ", Esc or B to go back to the ship" : "");
        }
        else
        {
            var status = card.Option.Status == SpawnStatus.Available ? SpawnStatus.Unavailable : card.Option.Status;
            _hint.Foreground = C.Brush(C.StatusColor(status));
            _hint.Text = SpawnCards.StatusText(status) + (string.IsNullOrEmpty(card.Option.Detail) ? "" : ": " + card.Option.Detail);
        }
    }

    // The card under the pointer, else the selected one (keyboard and controller move the selection).
    private void ShowPanel() => _panel?.Show((_hover ?? SelectedCard)?.Option);

    private void Refresh()
    {
        var menu = KeenRespawn.Menu;
        if (!_plain && _cards != null && C.Updating != _updating) Selected();
        if (menu == null || ReferenceEquals(menu, _menu)) return;
        if (_plain) { RefreshPlain(menu); return; }
        try { RefreshCards(menu); }
        catch (Exception ex) { Plain(ex); }
    }

    private void RefreshPlain(SpawnMenu menu)
    {
        _menu = menu;
        _status.Text = menu.Message.Length > 0 ? menu.Message : menu.Respawn ? "You died. Choose where to respawn." : "Choose where to spawn.";
        _options.ItemsSource = menu.Options;
        _options.SelectedItem = menu.Options.FirstOrDefault(o => o.Available);
        _spawn.IsEnabled = _options.SelectedItem != null;
    }

    private void RefreshCards(SpawnMenu menu)
    {
        var before = SelectedCard?.Option;
        _menu = menu;
        _title.Text = menu.Respawn ? "RESPAWN" : "CHOOSE SPAWN";
        _death.Text = menu.Death.Length > 0 ? menu.Death : menu.Respawn ? "You died. Choose where to respawn." : "Welcome. Choose where to start.";
        _message.Text = menu.Message;
        _respawn.Content = menu.Respawn ? "RESPAWN HERE" : "SPAWN HERE";
        bool ships = menu.Options.Any(C.IsShip);
        _column.HorizontalAlignment = ships ? HorizontalAlignment.Left : HorizontalAlignment.Center;
        _column.Margin = new Thickness(ships ? C.SideMargin : 0, 0, 0, 0);
        if (!menu.Options.Any(o => o.Kind == SpawnKind.Ship && o.Group == _expanded)) _expanded = "";
        var cards = Build(menu);
        // Keep the player's pick across a new menu (a home base set or cleared moves it), else the first usable card.
        var keep = before == null ? null : cards.FirstOrDefault(c => c.Option.Name == before.Name && c.Option.Grid == before.Grid && c.Option.Place == before.Place && c.Option.Group == before.Group);
        Select(keep ?? cards.FirstOrDefault(c => c.Option.Available && c.Group == null) ?? cards.FirstOrDefault());
    }

    // The list: one card per medbay, suit and so on, one per ship, and under the open ship its destinations (flat menu indices).
    private List<C.Card> Build(SpawnMenu menu)
    {
        var cards = new List<C.Card>(menu.Options.Length);
        foreach (var (option, index, group) in SpawnGroups.Top(menu.Options))
        {
            cards.Add(Make(option, index, group));
            if (group != null && group.Name == _expanded)
                foreach (var (destination, at) in group.Destinations) cards.Add(Make(destination, at, null));
        }
        _hover = null;
        _building = true;
        try { _cards.ItemsSource = cards; }
        finally { _building = false; }
        return cards;
    }

    private C.Card Make(SpawnOption option, int index, ShipGroup group)
    {
        C.Card card = null;
        card = new C.Card(option, index, () => { if (card.Group == null) ToggleHome(card); }, group);
        card.PointerEntered += (_, _) => { _hover = card; ShowPanel(); };
        card.PointerExited += (_, _) => { if (ReferenceEquals(_hover, card)) { _hover = null; ShowPanel(); } };
        return card;
    }

    /// <param name="open">False when closing a ship: selecting its card must not open it again.</param>
    private void Select(C.Card card, bool open = true)
    {
        _building = true;
        try
        {
            _cards.SelectedItem = card;
            if (!open) Selected();
        }
        finally { _building = false; }
        if (open) Selected();
        if (card != null) _cards.ScrollIntoView(card);
        Dispatcher.UIThread.Post(FocusSelected);
    }

    /// <summary>Opens a ship's destinations under it; a ship with one destination picks it, <paramref name="enter"/> moves into the list.</summary>
    private void Expand(ShipGroup group, bool enter)
    {
        if (_menu == null) return;
        _expanded = group.Name;
        var cards = Build(_menu);
        var ship = cards.FirstOrDefault(c => c.Group?.Name == group.Name);
        var destinations = cards.Where(c => c.IsDestinationCard && c.Option.Group == group.Name).ToList();
        Select(destinations.Count == 1 || enter && destinations.Count > 0 ? destinations.FirstOrDefault(c => c.Option.Available) ?? destinations[0] : ship);
    }

    private void Collapse()
    {
        if (_menu == null) return;
        string closing = _expanded;
        _expanded = "";
        var cards = Build(_menu);
        Select(cards.FirstOrDefault(c => c.Group?.Name == closing) ?? cards.FirstOrDefault(), false);
    }

    private void QueueRefresh() => Dispatcher.UIThread.Post(Refresh);

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_model != null) _model.Refresh -= QueueRefresh;
        _model = DataContext as SocialScreenViewModel; if (_model != null) _model.Refresh += QueueRefresh;
    }
}
