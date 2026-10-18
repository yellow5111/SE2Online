using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Keen.Game2.Client.UI.TerminalScreen.Inventory;
using Keen.Game2.Simulation.StreamedUI.Terminal.Inventories;
using Keen.Game2.Simulation.WorldObjects.Items;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.UI.AvaloniaInterface.Utilities;
using Keen.VRage.UI.Screens;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// The trade window in the terminal inventory's look: SE2's own item tiles for your inventory, what you give, what you get
/// and the partner's inventory. Drag your items into YOU GIVE, the partner's into YOU GET (a request), and either back out.
/// Any change asks both players to accept again; the host checks every amount when the trade runs.
/// </summary>
public sealed class TradeScreen : ScreenView
{
    private const int VkShift = 0x10, VkControl = 0x11;
    private const double DragStart = 5;

    private readonly Dictionary<TradePane, ItemsControl> _grids = new();
    private readonly Dictionary<TradePane, Control> _areas = new();
    private readonly Dictionary<TradePane, TextBlock> _empty = new();
    private readonly Grid _body = new();
    private readonly Canvas _overlay = new() { IsHitTestVisible = false };
    private readonly InventoryItem _ghost = new() { IsDragging = true, IsHitTestVisible = false, IsVisible = false };
    private readonly TextBox _amount = OnlineUi.Input("Amount (blank: whole stack)", 16), _credits = OnlineUi.Input("Credits", 16);
    private readonly TextBlock _status = OnlineUi.Text(""), _mineCredits = OnlineUi.Text("", size: 20), _giveCredits = OnlineUi.Text("", size: 20), _getCredits = OnlineUi.Text("", size: 20);
    private readonly TextBlock _giveTitle = OnlineUi.Text("YOU GIVE", "SubTitle", 26), _getTitle = OnlineUi.Text("YOU GET", "SubTitle", 26), _theirsTitle = OnlineUi.Text("THEIR INVENTORY", "SubTitle", 26);
    private readonly TextBlock _giveHint = Hint(""), _getHint = Hint("");
    private readonly Button _accept, _withdraw, _cancel;
    private readonly bool _native;
    private readonly double _tileWidth = 100, _tileHeight = 100;
    private SocialScreenViewModel _model;
    private long _shown = -1;
    private bool _closing, _dragging;
    private TradeSlot _pressed;
    private Point _pressAt;

    public TradeScreen()
    {
        _accept = OnlineUi.Button("Accept trade", Accept, "TradeAccept", true);
        _withdraw = OnlineUi.Button("Withdraw acceptance", () => Verb(TradeVerb.Unready), "TradeWithdraw");
        _cancel = OnlineUi.Button("Cancel trade", () => { _closing = true; Verb(TradeVerb.Cancel); _model?.Close(); }, "TradeCancel");
        var setCredits = OnlineUi.SmallButton("Offer credits", OfferCredits, "TradeOfferCredits");
        _amount.Width = 300; _credits.Width = 180;

        var middle = new Grid { RowDefinitions = new RowDefinitions("*,20,*") };
        var give = PaneView(TradePane.Give, _giveTitle, _giveHint, _giveCredits);
        var get = PaneView(TradePane.Get, _getTitle, _getHint, _getCredits);
        middle.Children.Add(give); Grid.SetRow(get, 2); middle.Children.Add(get);
        var mine = PaneView(TradePane.Mine, OnlineUi.Text("YOUR INVENTORY", "SubTitle", 26), Hint("Drag items into YOU GIVE. Double-click moves a whole stack."),
            new StackPanel { Spacing = 8, Children = { _mineCredits, OnlineUi.Row(_credits, setCredits) } });
        var theirs = PaneView(TradePane.Theirs, _theirsTitle, Hint("Drag items into YOU GET to ask for them."), null);
        var columns = new Grid { ColumnDefinitions = new ColumnDefinitions("*,20,*,20,*") };
        columns.Children.Add(mine);
        Grid.SetColumn(middle, 2); columns.Children.Add(middle);
        Grid.SetColumn(theirs, 4); columns.Children.Add(theirs);
        _body.Children.Add(columns);
        _overlay.Children.Add(_ghost);
        _body.Children.Add(_overlay);
        try
        {
            OnlineListStyles.ApplyTerminal(_body);
            _native = true;
            if (_body.TryFindResource("InventoryItemWidth", out object w) && w is double width and > 0) _tileWidth = width;
            if (_body.TryFindResource("InventoryItemHeight", out object h) && h is double height and > 0) _tileHeight = height;
        }
        catch (Exception e) { GameLog.Instance.Warn("trade: SE2's inventory styles did not load, item tiles show text instead: " + e.Message); }
        _ghost.Width = _tileWidth; _ghost.Height = _tileHeight;

        _body.AddHandler(PointerPressedEvent, Pressed, RoutingStrategies.Bubble, true);
        _body.AddHandler(PointerMovedEvent, Moved, RoutingStrategies.Bubble, true);
        _body.AddHandler(PointerReleasedEvent, Released, RoutingStrategies.Bubble, true);
        _body.AddHandler(PointerCaptureLostEvent, (_, _) => EndDrag(null), RoutingStrategies.Bubble, true);
        _body.AddHandler(InventoryItem.ActionEventProperty, ItemAction, RoutingStrategies.Bubble, true);

        var help = Hint("Shift: 10, Ctrl: 100, both: 1000, or type an amount; otherwise whole stacks move. Outlined items were asked for.");
        Content = OnlineUi.Shell(this, "TRADE", OnlineUi.Row(_amount, help), _status, _body, OnlineUi.Row(_accept, _withdraw, _cancel));
        OnDisposing += _ => { if (!_closing && TradeUi.State is { Closed: false } s) TradeUi.Act(new TradeAction(s.Trade, TradeVerb.Cancel)); };
        Refresh();
    }

    private static TextBlock Hint(string text)
    {
        var hint = OnlineUi.Text(text, "SubtleNotice", 18);
        hint.HorizontalAlignment = HorizontalAlignment.Left; hint.TextAlignment = TextAlignment.Left;
        return hint;
    }

    private Control PaneView(TradePane pane, TextBlock title, TextBlock hint, Control footer)
    {
        var items = new ItemsControl
        {
            Name = "Trade" + pane,
            ItemsPanel = new FuncTemplate<Panel>(() => new WrapPanel()),
            ItemTemplate = new FuncDataTemplate<TradeSlot>((slot, _) => slot == null ? null : Tile(slot))
        };
        _grids[pane] = items;
        var empty = OnlineUi.Text("", "SubtleNotice", 20);
        empty.HorizontalAlignment = HorizontalAlignment.Center; empty.TextAlignment = TextAlignment.Center; empty.IsHitTestVisible = false;
        _empty[pane] = empty;
        var area = new Grid { Background = Brushes.Transparent, Margin = new Thickness(0, 8) };
        area.Children.Add(empty);
        area.Children.Add(OnlineUi.Scroll(items));
        _areas[pane] = area;
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto") };
        grid.Children.Add(title);
        Grid.SetRow(hint, 1); grid.Children.Add(hint);
        Grid.SetRow(area, 2); grid.Children.Add(area);
        if (footer != null) { Grid.SetRow(footer, 3); grid.Children.Add(footer); }
        return OnlineUi.Panel(grid, new Thickness(18));
    }

    internal static InventoryItemModel Model(Guid item, long raw)
        => TradeUi.Definition(item) is ItemDefinition def ? new InventoryItemModel { Definition = def, Amount = new FixedPoint { RawValue = raw }, Stackable = true } : null;

    private Control Tile(TradeSlot slot)
    {
        var model = _native ? Model(slot.Item, slot.Raw) : null;
        Control content;
        if (model != null) content = new InventoryItem { Item = model, DataContext = model, Width = _tileWidth, Height = _tileHeight };
        else
        {
            var text = OnlineUi.Text(TradeUi.ItemName(slot.Item) + "\n" + TradeUi.Amount(slot.Raw), size: 16);
            text.TextAlignment = TextAlignment.Center; text.HorizontalAlignment = HorizontalAlignment.Center;
            content = new Border { Child = text, Width = _tileWidth, Height = _tileHeight, Background = new SolidColorBrush(Color.FromArgb(0x60, 0, 0, 0)) };
            ToolTip.SetTip(content, TradeUi.ItemName(slot.Item) + ": " + TradeUi.Amount(slot.Raw));
        }
        var frame = new Border { Child = content, BorderThickness = new Thickness(2), Margin = new Thickness(2), Tag = slot, BorderBrush = Brushes.Transparent };
        if (slot.Asked) frame.Bind(Border.BorderBrushProperty, new VRageDynamicResource("Highlight"));
        return frame;
    }

    private void Drop(TradeSlot slot, TradePane? target, long amount)
    {
        if (TradeUi.State is not { Closed: false } state) return;
        var (verb, raw, error) = TradeDrag.Plan(state, slot, target, amount);
        if (error != null) { _status.Text = error; return; }
        if (verb is TradeVerb.Set or TradeVerb.Request) TradeUi.Act(new TradeAction(state.Trade, verb, slot.Item, raw));
    }

    /// <summary>SE2's own transfer amounts: Shift 10, Ctrl 100, both 1000, the amount box when filled, otherwise the whole stack (0 = invalid box).</summary>
    private long Amount(long stack, KeyModifiers modifiers)
    {
        bool shift = modifiers.HasFlag(KeyModifiers.Shift) || GameKeys.IsDown(VkShift), ctrl = modifiers.HasFlag(KeyModifiers.Control) || GameKeys.IsDown(VkControl);
        if (shift || ctrl) return Math.Min(stack, TradeUi.Raw((shift ? 10 : 1) * (ctrl ? 100 : 1)));
        string text = _amount.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return stack;
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double units) && units > 0 && units < 1e12 ? Math.Min(stack, TradeUi.Raw(units)) : 0;
    }

    private static TradeSlot SlotAt(object source)
    {
        for (var visual = source as Visual; visual != null; visual = visual.GetVisualParent())
            if (visual is Control { Tag: TradeSlot slot }) return slot;
        return null;
    }

    private TradePane? PaneAt(PointerEventArgs e)
    {
        foreach (var (pane, area) in _areas)
            if (area.IsEffectivelyVisible && new Rect(area.Bounds.Size).Contains(e.GetPosition(area))) return pane;
        return null;
    }

    private void Pressed(object sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_body).Properties.IsLeftButtonPressed) return;
        _pressed = SlotAt(e.Source);
        _pressAt = e.GetPosition(_body);
        _dragging = false;
    }

    private void Moved(object sender, PointerEventArgs e)
    {
        if (_pressed == null) return;
        if (!_dragging)
        {
            if (Point.Distance(_pressAt, e.GetPosition(_body)) <= DragStart) return;
            _dragging = true;
            e.Pointer.Capture(_body);
            var ghost = _native ? Model(_pressed.Item, Math.Max(1, Amount(_pressed.Raw, e.KeyModifiers))) : null;
            _ghost.Item = ghost; _ghost.DataContext = ghost; _ghost.IsVisible = ghost != null;
        }
        var at = e.GetPosition(_overlay);
        Canvas.SetLeft(_ghost, at.X - _tileWidth / 2);
        Canvas.SetTop(_ghost, at.Y - _tileHeight / 2);
    }

    private void Released(object sender, PointerReleasedEventArgs e)
    {
        var slot = _pressed;
        bool dragged = _dragging;
        EndDrag(e.Pointer);
        if (slot != null && dragged) Drop(slot, PaneAt(e), Amount(slot.Raw, e.KeyModifiers));
    }

    private void EndDrag(IPointer pointer)
    {
        _pressed = null;
        _dragging = false;
        _ghost.IsVisible = false; _ghost.Item = null; _ghost.DataContext = null;
        if (pointer?.Captured == _body) pointer.Capture(null);
    }

    private void ItemAction(object sender, InventoryItemEventArgs e)
    {
        if (e.Type != EventType.DoubleClick || SlotAt(e.Source) is not TradeSlot slot) return;
        Drop(slot, slot.Pane switch { TradePane.Mine => TradePane.Give, TradePane.Give => TradePane.Mine, TradePane.Theirs => TradePane.Get, _ => TradePane.Theirs }, slot.Raw);
    }

    private void OfferCredits()
    {
        if (TradeUi.State is not { Closed: false } s) return;
        string text = _credits.Text?.Trim();
        if (!double.TryParse(string.IsNullOrEmpty(text) ? "0" : text, NumberStyles.Float, CultureInfo.InvariantCulture, out double credits) || credits < 0 || credits >= 1e12) { _status.Text = "Enter a credit amount (0 takes credits out of your offer)."; return; }
        TradeUi.Act(new TradeAction(s.Trade, TradeVerb.Set, Guid.Empty, TradeUi.Raw(credits)));
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
        if (version == _shown || _dragging) return;
        _shown = version;
        var state = TradeUi.State;
        if (state == null || state.Closed) return;
        int you = state.You, them = 1 - you;
        string other = state.Names[them];
        foreach (TradePane pane in Enum.GetValues<TradePane>())
        {
            var slots = TradeDrag.Slots(state, pane, TradeUi.ItemName);
            _grids[pane].ItemsSource = slots;
            _empty[pane].Text = slots.Length > 0 ? "" : pane switch
            {
                TradePane.Theirs when state.TheirHave == null => other + "'s inventory is not shown: their game needs Space Engineers 2 Online 0.15.4 or newer.",
                TradePane.Theirs or TradePane.Mine => "No items",
                _ => "Drag items here"
            };
        }
        static long Credits(TradeLine[] lines) => lines.FirstOrDefault(l => l.Item == Guid.Empty)?.Raw ?? 0;
        _mineCredits.Text = "Your credits: " + TradeUi.Amount(Credits(state.Have));
        _giveCredits.Text = Credits(state.Offers[you]) is long give and > 0 ? "+ " + TradeUi.Amount(give) + " credits" : "";
        _getCredits.Text = Credits(state.Offers[them]) is long get and > 0 ? "+ " + TradeUi.Amount(get) + " credits" : "";
        _giveTitle.Text = "YOU GIVE" + (state.Ready[you] ? "  - YOU ACCEPTED" : "");
        _getTitle.Text = "YOU GET FROM " + other.ToUpperInvariant() + (state.Ready[them] ? "  - " + other.ToUpperInvariant() + " ACCEPTED" : "");
        _theirsTitle.Text = other.ToUpperInvariant() + "'S INVENTORY";
        _giveHint.Text = state.Asked == null ? "Drag items out to take them back." : "Drag items out to take them back or refuse a request.";
        _getHint.Text = state.Asked == null ? "What " + other + " offers." : "Drag out what you asked for to take the request back.";
        string waiting = state.Ready[you] ? "Waiting for " + other + " to accept." : state.Ready[them] ? other + " accepted. Accept to complete the trade." : "Change the offers or accept them. Any change asks both of you to accept again.";
        _status.Text = string.IsNullOrEmpty(state.Message) ? waiting : state.Message + "  " + waiting;
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
