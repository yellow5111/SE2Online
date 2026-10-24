using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;
using Avalonia.Threading;
using Keen.VRage.UI.AvaloniaInterface.Utilities;
using Keen.VRage.UI.Screens;
using Keen.VRage.UI.Shared.Controls.BeveledBorder;
using Y51_SE2Online.Core;
using Y51_SE2Online.NetHost;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

public sealed class AdminScreenViewModel : ScreenViewModel
{
    public event Action Refresh;
    private long _next;
    public AdminScreenViewModel()
    {
        // The world keeps rendering behind the docked column. Esc closes through the screen's default closing action.
        KeepsOtherScreensVisible = true; AllowsInputFromLowerScreens = false; InitializeInputContext();
        OnTick += () => { if (MpClock.NowMs() < _next) return; _next = MpClock.NowMs() + 500; Refresh?.Invoke(); };
    }
    public void Close() => SignalWantsClose();
}

/// <summary>The admin panel: a column docked on the right of the screen, one page per area, each shown only with its keys.</summary>
public sealed partial class AdminScreen : ScreenView
{
    private const double ColumnWidth = 640, FrameHeight = 1080, MinBackdropOpacity = 0.8;
    private static readonly IBrush LiveBrush = new SolidColorBrush(Color.FromRgb(0x3D, 0xD6, 0x8C));
    private static readonly IBrush IdleBrush = new SolidColorBrush(Color.FromArgb(0x99, 0xC8, 0xCC, 0xD4));

    private sealed record PageDef(string Name, Control Body, Func<AdminState, bool> Show, Button Tab);

    private readonly Border _pill = new() { CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 2), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _pillText = new() { FontSize = 16, FontWeight = FontWeight.Bold, LetterSpacing = 2, Foreground = Brushes.Black };
    private readonly TextBlock _server = OnlineUi.Text("", "SubTitle", 22);
    private readonly TextBlock _who = Hint("");
    private readonly TextBlock _message = Hint(""), _note = Hint("");
    private readonly ContentControl _page = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    private readonly Control _nothing = OnlineUi.EmptyState("Nothing to show", "Your group has no admin panel pages here.");
    private readonly List<PageDef> _pages = new();
    private readonly List<Action<AdminState>> _gates = new();
    private readonly BeveledBorder _surface = new() { IsHitTestVisible = false, Opacity = 0.92 }, _frame = new() { IsHitTestVisible = false, IsVisible = false };
    private readonly Border _edge = new() { BorderThickness = new Thickness(2, 0, 0, 0), IsHitTestVisible = false }, _rule = new() { Height = 1, IsVisible = false, Margin = new Thickness(0, 12, 0, 0) };
    private readonly Grid _dock = new();
    private bool _native;
    private AdminState _state = new();
    private AdminScreenViewModel _model;
    private int _current;
    private bool _closed, _built;
    private string _lastMessage;

    public AdminScreen()
    {
        AddPage("Players", PlayersPage(), s => CanAny(s, "admin.players.", "admin.teleport.", "admin.spawn.item_others"));
        AddPage("Entities", EntitiesPage(), s => s.Can("admin.entities.list"));
        AddPage("Cleanup", CleanupPage(), s => s.Can("admin.cleanup") || s.Can("cleanup.run") || s.Can("cleanup.configure"));
        AddPage("Spawn", SpawnPage(), s => CanAny(s, "admin.spawn."));
        AddPage("Zones", ZonesPage(), s => CanAny(s, "admin.safezone."));
        AddPage("World", WorldPage(), s => CanAny(s, "admin.world."));
        AddPage("Me", MePage(), s => s.Can("admin.creative.tools") || s.Can("admin.creative.godmode") || s.Can("admin.access_bypass"));
        AddPage("Server", ServerPage(), s => s.Can("admin.players.view_bans") || s.Can("admin.players.unban") || s.Can("admin.whitelist") || s.Dedicated);
        AddPage("Perms", PermsPage(), s => s.Can("admin.players.set_group") || s.Can("command.perms"));
        Styles.Add(new Style(s => s.OfType<Button>().Class("OnlineNavigation").Class("Selected")) { Setters = { new Setter(Button.BorderThicknessProperty, new Thickness(3)), new Setter(Button.BorderBrushProperty, new VRageDynamicResource("Primary")), new Setter(Button.FontWeightProperty, FontWeight.Bold) } });
        // SE2's TextBlock theme is one line with "..."; Is<> also reaches the AccessText inside buttons and check boxes.
        Styles.Add(new Style(s => s.Is<TextBlock>()) { Setters = { new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap), new Setter(TextBlock.TextTrimmingProperty, TextTrimming.None) } });

        var titleRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        titleRow.Children.Add(OnlineUi.Text("ADMIN", "Header", 46));
        var close = OnlineUi.SmallButton("Close (Esc)", () => _model?.Close(), "AdminClose");
        close.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(close, 1); titleRow.Children.Add(close);
        _pill.Child = _pillText; _pill.VerticalAlignment = VerticalAlignment.Top;
        var serverRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,12,*") };
        serverRow.Children.Add(_pill); Grid.SetColumn(_server, 2); serverRow.Children.Add(_server);
        var header = new StackPanel { Spacing = 6 };
        header.Children.Add(titleRow); header.Children.Add(serverRow); header.Children.Add(_who); header.Children.Add(_rule);

        var nav = new WrapPanel { Margin = new Thickness(0, 14, 0, 8) };
        foreach (var page in _pages) nav.Children.Add(page.Tab);
        var footer = new StackPanel { Spacing = 4 };
        footer.Children.Add(_note); footer.Children.Add(_message);
        var footerScroll = new ScrollViewer { Content = footer, MaxHeight = 160, Margin = new Thickness(0, 10, 0, 0), HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };

        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto") };
        layout.Children.Add(header);
        Grid.SetRow(nav, 1); layout.Children.Add(nav);
        Grid.SetRow(_page, 2); layout.Children.Add(_page);
        Grid.SetRow(footerScroll, 3); layout.Children.Add(footerScroll);

        // Fallback look until ApplyNativeLook finds the game's panel resources: PrimaryPanel black, denser than its usual 60 %.
        _surface.Classes.Add("PrimaryPanel");
        _edge.Bind(Border.BorderBrushProperty, new VRageDynamicResource("Primary"));
        _dock.Children.Add(_surface); _dock.Children.Add(_frame); _dock.Children.Add(_edge);
        _dock.Children.Add(new Border { Padding = new Thickness(24, 18, 16, 18), Child = layout });
        var column = new Grid { Width = ColumnWidth, Height = FrameHeight };
        column.Children.Add(_dock);
        Loaded += (_, _) => ApplyNativeLook();
        // Only the column scales with the screen height and sits on the right edge, at any aspect ratio; the rest of the
        // screen has no control at all, so it takes no clicks.
        Content = new Viewbox { Name = "AdminDock", Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Stretch, Child = column };
        OnDisposing += _ => { _closed = true; if (_model != null) _model.Refresh -= QueueRefresh; };
        _built = true;
        Select(0);
    }

    /// <summary>
    /// The look of SE2's terminal dialogs (PreciseTransferScreen, TradeTransferScreen): Border-Background under a 2 px
    /// Decorative-Border-Brush bevel, the terminal header's Gray-906 rule, Tab-Stroke-Active on the open tab, and the
    /// player's own UI background opacity. Read once the column is in the window, where the game's global styles are.
    /// </summary>
    private void ApplyNativeLook()
    {
        if (_native) return;
        try
        {
            if (Find("Border-Background") is not { } panel) return;
            _native = true;
            var bevel = this.TryFindResource("Radius-Medium", out object r) && r is CornerRadius radius ? radius : new CornerRadius(5);
            _surface.Classes.Remove("PrimaryPanel");
            _surface.Background = panel; _surface.BorderThickness = default; _surface.Bevel = bevel;
            // Never lighter than 80 %: unlike the terminal, the column is read over the live world.
            _surface.Opacity = this.TryFindResource("DynamicUIBackgroundOpacity", out object o) && o is IConvertible value ? Math.Clamp(value.ToDouble(CultureInfo.InvariantCulture), MinBackdropOpacity, 1) : _surface.Opacity;
            _dock.Margin = new Thickness(0, 12, 12, 12);
            if (Find("Decorative-Border-Brush") is { } border)
            {
                _frame.BorderBrush = border; _frame.BorderThickness = new Thickness(2); _frame.Bevel = bevel;
                _frame.IsVisible = true; _edge.IsVisible = false;
            }
            if (Find("Gray-906") is { } rule) { _rule.Background = rule; _rule.IsVisible = true; }
            if (Find("Tab-Stroke-Active") is { } tab)
                Styles.Add(new Style(s => s.OfType<Button>().Class("OnlineNavigation").Class("Selected")) { Setters = { new Setter(Button.BorderBrushProperty, tab) } });
        }
        catch (Exception e) { GameLog.Instance.Warn("admin panel: the game's panel look is not available: " + e.Message); }
    }

    private IBrush Find(string key)
        => !this.TryFindResource(key, out object value) ? null : value as IBrush ?? (value is Color color ? new ImmutableSolidColorBrush(color) : null);

    private void AddPage(string name, Control body, Func<AdminState, bool> show)
    {
        int index = _pages.Count;
        _pages.Add(new PageDef(name, body, show, Nav(name, () => Select(index), "Admin" + name)));
    }

    private static Button Nav(string label, Action action, string name)
    {
        var button = OnlineUi.SmallButton(label, action, name);
        button.FontSize = 18; button.MinWidth = 0; button.MinHeight = 38; button.Padding = new Thickness(12, 4); button.Margin = new Thickness(0, 0, 6, 6);
        button.Classes.Add("OnlineNavigation");
        return button;
    }

    private static void Mark(Button button, bool selected) { if (selected) button.Classes.Add("Selected"); else button.Classes.Remove("Selected"); }

    private void Select(int index)
    {
        _current = index;
        for (int i = 0; i < _pages.Count; i++) Mark(_pages[i].Tab, i == index);
        RefreshControls();
    }

    /// <summary>Any key, or for an entry ending in '.', any key under it.</summary>
    private static bool CanAny(AdminState s, params string[] keys)
        => keys.Any(k => k.EndsWith('.') ? s.Role == OnlineRole.Host || s.Grants.Any(g => g == "*" || g.StartsWith(k, StringComparison.Ordinal)) : s.Can(k));

    private static string Key(AdminAction action, ulong target = 0, int minutes = 0, double size = 0)
        => AdminRules.KeyFor(new AdminRequest(action, target, Minutes: minutes, Size: size));

    private bool Owner => _state.Role == OnlineRole.Host || Array.IndexOf(_state.Grants, "*") >= 0;
    private static ulong MySteam => NetSession.Current?.LocalIdentity ?? 0;

    /// <summary>Shows <paramref name="control"/> only with what <paramref name="show"/> needs; enables it while <paramref name="enabled"/> holds.</summary>
    private T Gate<T>(T control, Func<AdminState, bool> show, Func<bool> enabled = null) where T : Control
    {
        _gates.Add(s => { control.IsVisible = show(s); if (enabled != null) control.IsEnabled = AdminAccess.InSession && enabled(); });
        return control;
    }

    private T Gate<T>(T control, string key, Func<bool> enabled = null) where T : Control => Gate(control, s => s.Can(key), enabled);

    /// <summary>A button that sends what <paramref name="make"/> returns (null sends nothing), shown only with <paramref name="key"/>.</summary>
    private Button Act(string label, string key, Func<AdminRequest> make, Func<bool> enabled = null)
        => Gate(OnlineUi.SmallButton(label, () => { if (make() is { } request) Send(request); }, "Admin" + label.Replace(" ", "")), key, enabled);

    private static void Send(AdminRequest request) => MpMenu.Admin(request);

    private AdminRequest Note(string text)
    {
        _note.Text = text; _note.IsVisible = !string.IsNullOrWhiteSpace(text);
        return null;
    }

    private void Copy(string text) => TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(text);

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_model != null) _model.Refresh -= QueueRefresh;
        _model = DataContext as AdminScreenViewModel;
        if (_model != null) _model.Refresh += QueueRefresh;
    }

    private void QueueRefresh() => Dispatcher.UIThread.Post(() => { if (!_closed) RefreshControls(); });

    private void RefreshControls()
    {
        if (!_built) return;
        _state = AdminAccess.State;
        var s = _state;
        bool live = AdminAccess.InSession;
        _pillText.Text = live ? "LIVE" : "OFFLINE"; _pill.Background = live ? LiveBrush : IdleBrush;
        string name = string.IsNullOrWhiteSpace(s.ServerName) ? AdminAccess.ServerName : s.ServerName;
        _server.Text = !live ? "Not in a multiplayer session" : AdminAccess.Hosting ? "Hosting " + name : name;
        _who.Text = live ? $"{s.Group} / rank {s.Rank} / {s.Players.Count(p => p.Connected)} online" : "";
        if (s.Message != _lastMessage) { _lastMessage = s.Message; Note(""); }
        _message.Text = s.Message; _message.IsVisible = !string.IsNullOrWhiteSpace(s.Message);

        foreach (var page in _pages) page.Tab.IsVisible = page.Show(s);
        if (!_pages[_current].Tab.IsVisible && _pages.FindIndex(p => p.Tab.IsVisible) is int first and >= 0) { _current = first; for (int i = 0; i < _pages.Count; i++) Mark(_pages[i].Tab, i == first); }
        var body = _pages[_current].Tab.IsVisible ? _pages[_current].Body : _nothing;
        if (_page.Content != body) _page.Content = body;
        RefreshPages();
        RefreshActions();
    }

    private void RefreshActions()
    {
        if (!_built) return;
        foreach (var gate in _gates) gate(_state);
        RefreshPageActions();
    }

    // Compact building blocks for the 640 px column.
    private static TextBlock Info(string text)
    {
        var block = OnlineUi.Text(text, null, 18);
        block.HorizontalAlignment = HorizontalAlignment.Left; block.TextAlignment = TextAlignment.Left;
        return block;
    }

    private static TextBlock Hint(string text)
    {
        var block = OnlineUi.Text(text, "SubtleNotice", 17);
        block.HorizontalAlignment = HorizontalAlignment.Left; block.TextAlignment = TextAlignment.Left;
        return block;
    }

    private static TextBlock Label(string text) => OnlineUi.Text(text, "SubTitle", 19);

    private static StackPanel Stack(double spacing, params Control[] controls)
    {
        var stack = new StackPanel { Spacing = spacing };
        foreach (var control in controls) stack.Children.Add(control);
        return stack;
    }

    private static WrapPanel Flow(params Control[] controls)
    {
        var row = new WrapPanel();
        foreach (var control in controls) { control.Margin = new Thickness(0, 0, 10, 8); control.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(control); }
        return row;
    }

    private static Control Section(string title, params Control[] parts)
    {
        var stack = Stack(10, OnlineUi.Text(title, "SubTitle", 22));
        foreach (var part in parts) stack.Children.Add(part);
        return OnlineUi.Panel(stack, new Thickness(18));
    }

    private static Control Page(params Control[] parts) => OnlineUi.Scroll(Stack(14, parts));

    /// <summary>A text box at least <paramref name="width"/> wide; text and watermark wrap instead of being cut.</summary>
    private static TextBox Box(string watermark, int length, double width = 0)
    {
        var box = new TextBox { Watermark = watermark, MaxLength = length, FontSize = 20, MinHeight = 42, MinWidth = width, TextWrapping = TextWrapping.Wrap };
        ScrollViewer.SetHorizontalScrollBarVisibility(box, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        return box;
    }

    /// <summary>A list as tall as its rows, scrolling past <paramref name="maxHeight"/>.</summary>
    private static ListBox List(string name, double maxHeight)
    {
        var list = OnlineUi.List(name);
        list.MinHeight = 48; list.MaxHeight = maxHeight;
        return list;
    }

    /// <summary>A list row: the name in full on top, the other values wrapped on a dimmer line under it.</summary>
    private static StackPanel Row(string name, params string[] details)
    {
        var row = new StackPanel { Spacing = 2, MinHeight = 40, Margin = new Thickness(6, 4) };
        row.Children.Add(RowText(name, "Bright", 19));
        string rest = string.Join(" / ", details.Where(d => !string.IsNullOrWhiteSpace(d)));
        if (rest.Length > 0) row.Children.Add(RowText(rest, null, 16, .8));
        return row;
    }

    private static TextBlock RowText(string text, string style, double size, double opacity = 1)
    {
        var block = OnlineUi.Text(text ?? "", style, size, true);
        block.HorizontalAlignment = HorizontalAlignment.Left; block.TextAlignment = TextAlignment.Left; block.Opacity = opacity;
        return block;
    }

    private static ComboBox Combo<T>(Func<T, string> text, double width = 240) where T : class => new()
    {
        MinHeight = 40, MinWidth = width, FontSize = 19, VerticalAlignment = VerticalAlignment.Center,
        ItemTemplate = new FuncDataTemplate<T>((x, _) => x == null ? null : OnlineUi.Text(text(x), size: 19, inheritForeground: true))
    };

    private static double Number(TextBox box, double fallback)
        => double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value) ? value : fallback;

    /// <summary>A switch mirroring a server-side state; a flip is not undone by state sent before the server applied it.</summary>
    private sealed class StateToggle
    {
        public readonly OnlineToggle Toggle;
        private bool _syncing;
        private long _touchedMs;

        public StateToggle(string label, string help, Action<bool> send)
        {
            Toggle = new OnlineToggle(label, help) { ColumnDefinitions = new ColumnDefinitions("*,12,Auto") };
            Toggle.IsCheckedChanged += (_, _) => { if (_syncing) return; _touchedMs = MpClock.NowMs(); send(Toggle.IsChecked); };
        }

        public void Sync(bool value)
        {
            if (Toggle.IsChecked == value || MpClock.NowMs() - _touchedMs < 2500) return;
            _syncing = true; Toggle.IsChecked = value; _syncing = false;
        }
    }
}
