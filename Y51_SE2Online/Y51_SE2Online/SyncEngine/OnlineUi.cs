using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Keen.VRage.Library.Localization;
using Keen.VRage.Library.Utils;
using Keen.VRage.UI.AvaloniaInterface.Utilities;
using Keen.VRage.UI.Screens;
using Keen.VRage.UI.Shared.Controls.BeveledBorder;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;
using Y51_SE2Online.NetHost;

namespace Y51_SE2Online.SyncEngine;

internal static class OnlineUi
{
    public static string SessionStatus()
    {
        var controller = MpPlugin.Controller;
        if (controller?.State == OnlineSessionState.Hosting && NetSession.Current is { } network)
        {
            if (!string.IsNullOrEmpty(network.Failure) || network.HasStopped) return "Hosting connection failed / Stop hosting and try again";
            if (!network.TransportReady) return "Starting the hosting connection...";
        }
        return controller?.State switch
        {
            OnlineSessionState.Stopping => "Closing the previous multiplayer session...",
            OnlineSessionState.ReturningToMenu => "Returning to the main menu...",
            OnlineSessionState.Failed => "Multiplayer connection failed",
            OnlineSessionState.Hosting => MpMenu.HasWorld ? "Hosting your world" : "Hosting enabled / Load or create a world to begin",
            OnlineSessionState.Joining => ClientWorld.Progress.Stage switch
            {
                JoinStage.Playing => "Connected to multiplayer",
                JoinStage.WaitingForSteam => "Waiting for Steam...",
                JoinStage.WaitingForHostWorld => "Waiting for the host to open a world...",
                JoinStage.Receiving => "Downloading the world...",
                JoinStage.Loading or JoinStage.Building or JoinStage.WaitingForCharacter => "Loading the multiplayer world...",
                JoinStage.Lost or JoinStage.GaveUp or JoinStage.Failed or JoinStage.Rejected => "Connection interrupted / Join again from the server list",
                _ => "Connecting to multiplayer..."
            },
            _ => MpPlugin.SteamAvailable ? "Online / Browse servers or join a session" : "Steam unavailable / Launch SE2 through Steam to connect"
        };
    }

    public static TextBlock Text(string value, string style = null, double size = 24, bool inheritForeground = false)
    {
        var text = new TextBlock { Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None, VerticalAlignment = VerticalAlignment.Center };
        if (style != null) text.Classes.Add(style);
        if (!inheritForeground) text.Bind(TextBlock.ForegroundProperty, new VRageDynamicResource("HighContrast"));
        return text;
    }

    public static string Localize(string key, string fallback)
    {
        try { var value = Raw(key).EvaluateText(); return string.IsNullOrWhiteSpace(value) || value == key ? fallback : value; }
        catch { return fallback; }
    }

    // SE2 logs every missing localization key as a warning, and at exit turns the first warning into a crash report.
    // The plugin's texts are not keys, so they are registered here and looked up without that warning.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<StringId, bool> RawTexts = new();

    /// <summary>A plugin text shown through SE2's localized UI as it is.</summary>
    public static LocKey Raw(string text)
    {
        // ponytail: progress texts change every frame; dropping all at the cap only risks one warning per text shown again.
        if (RawTexts.Count > 20000) RawTexts.Clear();
        RawTexts.TryAdd(text, true);
        return LocKey.FromString(text);
    }

    internal static void InstallRawTexts(HarmonyLib.Harmony harmony)
    {
        harmony.Patch(HarmonyLib.AccessTools.Method(typeof(LocalizationPackage), nameof(LocalizationPackage.GetText), new[] { typeof(StringId) }),
            prefix: new HarmonyLib.HarmonyMethod(typeof(OnlineUi), nameof(RawTextPrefix)));
        harmony.Patch(HarmonyLib.AccessTools.Method(typeof(LocKey), nameof(LocKey.ToString)), prefix: new HarmonyLib.HarmonyMethod(typeof(OnlineUi), nameof(RawToStringPrefix)));
    }

    // LocKey.ToString logs a warning for every {...} in the text before evaluating it; a plugin text needs no evaluation.
    private static bool RawToStringPrefix(ref LocKey __instance, ref string __result)
    {
        if (!RawTexts.ContainsKey(__instance.TextId) || Singleton<TextManager>.Instance.HasText(__instance)) return true;
        __result = __instance.TextId.ToString();
        return false;
    }

    private static bool RawTextPrefix(LocalizationPackage __instance, StringId id, ref string __result)
    {
        if (!RawTexts.ContainsKey(id) || __instance.HasText(id)) return true;
        // The text source's result goes through TextManager's {filter:...} parser; host and player names are not filters.
        __result = TextManager.EscapeInputString(id.ToString());
        return false;
    }

    public static StackPanel Stack(params Control[] controls)
    {
        var stack = new StackPanel { Spacing = 20 };
        foreach (var control in controls) stack.Children.Add(control);
        return stack;
    }

    public static WrapPanel Row(params Control[] controls)
    {
        var row = new WrapPanel();
        foreach (var control in controls)
        {
            var margin = control.Margin;
            control.Margin = new Thickness(margin.Left, margin.Top, margin.Right + 20, margin.Bottom + 10);
            row.Children.Add(control);
        }
        return row;
    }

    public static Button Button(string label, Action action, string name = null, bool menu = false)
    {
        var button = new Button { Content = label, Name = name, MinHeight = menu ? 62 : 48, MinWidth = menu ? 232 : 128, Padding = new Thickness(22, 10), FontSize = 24, HorizontalContentAlignment = HorizontalAlignment.Center };
        if (menu) button.Classes.Add("Menu");
        button.Click += (_, _) => { try { action(); } catch (Exception e) { MpMenu.Report(e.Message); } };
        return button;
    }

    /// <summary>A button the size of the control panel's own buttons, for panels laid over SE2's screens.</summary>
    public static Button SmallButton(string label, Action action, string name = null)
    {
        var button = Button(label, action, name);
        button.FontSize = 20; button.MinHeight = 40; button.MinWidth = 96; button.Padding = new Thickness(14, 4);
        return button;
    }

    /// <summary>A compact player picker (a native drop-down instead of a tall list).</summary>
    public static ComboBox PlayerPicker(string placeholder) => new()
    {
        MinHeight = 40, MinWidth = 240, FontSize = 20, PlaceholderText = placeholder, VerticalAlignment = VerticalAlignment.Center,
        ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<SocialPlayer>((p, _) => p == null ? null : Text(p.Name, size: 20, inheritForeground: true))
    };

    public static Button Back(ScreenView screen, Action action)
    {
        var button = Button(Localize("GenericBack", "Back"), action, "OnlineBack", true);
        var content = new Grid { ColumnDefinitions = new ColumnDefinitions("42,*"), Width = 250 };
        var icon = Image(screen, "MenuAvaloniaConfiguration_BackIcon");
        icon.Height = 40; icon.Width = 34;
        content.Children.Add(icon);
        var title = new AccessText { Text = Localize("GenericBack", "Back"), Name = "PART_Text", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(title, 1); content.Children.Add(title);
        button.Content = content;
        return button;
    }

    public static Image Image(ScreenView screen, string resource)
    {
        screen.PushRequiredAvaloniaResource(resource);
        var image = new Image { IsHitTestVisible = false, Stretch = Stretch.Fill };
        image.Bind(Avalonia.Controls.Image.SourceProperty, new VRageDynamicResource(resource));
        return image;
    }

    public static Grid Panel(Control content, Thickness? padding = null)
    {
        var grid = new Grid();
        var surface = new BeveledBorder { IsHitTestVisible = false }; surface.Classes.Add("PrimaryPanel");
        var border = new BeveledBorder { Child = content, Padding = padding ?? new Thickness(28), ClampDirection = ClampDirection.None }; border.Classes.Add("PrimaryBorder");
        grid.Children.Add(surface); grid.Children.Add(border);
        return grid;
    }

    // Test diagnostics: our full-screen views, for the in-game layout audit.
    internal static readonly System.Collections.Generic.List<WeakReference<ScreenView>> Screens = new();

    public static Control Shell(ScreenView screen, string title, Control navigation, Control status, Control body, Control actions)
    {
        lock (Screens) { Screens.RemoveAll(w => !w.TryGetTarget(out _)); Screens.Add(new WeakReference<ScreenView>(screen)); }
        var layout = new Grid { Width = 1920, Height = 1080, RowDefinitions = new RowDefinitions("112,Auto,Auto,*,Auto"), Margin = new Thickness(0) };
        var ornament = Image(screen, "MenuAvaloniaConfiguration_ScreenOrnament");
        ornament.HorizontalAlignment = HorizontalAlignment.Right; ornament.VerticalAlignment = VerticalAlignment.Top; ornament.Width = 1200; ornament.Height = 132; ornament.Opacity = .45;
        layout.Children.Add(ornament);
        var heading = Text(title, "Header", 58); heading.Margin = new Thickness(80, 15, 0, 0);
        layout.Children.Add(heading);
        navigation.MinHeight = 58; navigation.Margin = new Thickness(80, 0, 80, 14); Grid.SetRow(navigation, 1); layout.Children.Add(navigation);
        status.Margin = new Thickness(80, 0, 80, 20); Grid.SetRow(status, 2); layout.Children.Add(status);
        body.Margin = new Thickness(72, 0, 72, 18); Grid.SetRow(body, 3); layout.Children.Add(body);
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), MinHeight = 68, Margin = new Thickness(72, 0, 72, 20) };
        var back = Back(screen, () => (screen.DataContext as OnlineScreenViewModel)?.Close());
        if (screen is PlayersScreen) back = Back(screen, () => (screen.DataContext as PlayersScreenViewModel)?.Close());
        if (screen is FactionsScreen || screen is GridOwnershipScreen || screen is GpsShareScreen || screen is TradeScreen || screen is PlayerOptionsScreen) back = Back(screen, () => (screen.DataContext as SocialScreenViewModel)?.Close());
        if (screen is AdminScreen) back = Back(screen, () => (screen.DataContext as AdminScreenViewModel)?.Close());
        if (screen is RespawnScreen) back.IsVisible = false;
        footer.Children.Add(back); Grid.SetColumn(actions, 2); footer.Children.Add(actions);
        Grid.SetRow(footer, 4); layout.Children.Add(footer);
        return new Viewbox { Name = "OnlineSafeFrame", Stretch = Stretch.Uniform, Child = layout };
    }

    /// <summary>A tab body inside SE2's terminal: Shell's parts without the title and back button, refreshed while open.</summary>
    public static Control Embedded(Control navigation, Control status, Control body, Control actions, Action refresh)
    {
        var layout = new Grid { Width = 1840, Height = 860, RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto") };
        navigation.MinHeight = 58; navigation.Margin = new Thickness(8, 0, 8, 14); layout.Children.Add(navigation);
        status.Margin = new Thickness(8, 0, 8, 20); Grid.SetRow(status, 1); layout.Children.Add(status);
        body.Margin = new Thickness(0, 0, 0, 18); Grid.SetRow(body, 2); layout.Children.Add(body);
        actions.Margin = new Thickness(0, 0, 0, 8); Grid.SetRow(actions, 3); layout.Children.Add(actions);
        var frame = new Viewbox { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Child = layout };
        var timer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) => refresh();
        frame.AttachedToVisualTree += (_, _) => timer.Start();
        frame.DetachedFromVisualTree += (_, _) => timer.Stop();
        return frame;
    }

    public static Grid Columns(Control left, Control right, string widths = "*,24,560")
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(widths) };
        grid.Children.Add(left); Grid.SetColumn(right, 2); grid.Children.Add(right);
        return grid;
    }

    /// <summary>A red bold hint line under a setting's name.</summary>
    public static TextBlock Warning(string text)
    {
        // Skips the theme's foreground binding, which would override the red.
        var hint = Text(text, "SubtleNotice", 17, inheritForeground: true);
        hint.HorizontalAlignment = HorizontalAlignment.Left; hint.TextAlignment = TextAlignment.Left;
        hint.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x4D, 0x4D)); hint.FontWeight = FontWeight.Bold;
        return hint;
    }

    public static Control Field(string label, Control input, string help = null, Control warning = null, double inputWidth = 480)
    {
        input.MinHeight = 48;
        // The hint is a small line right under the setting's name, not a separate block below it.
        var caption = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        caption.Children.Add(Text(label, "SubTitle"));
        // SE2's SubtleNotice style centres the text; a hint sits left-aligned under its setting's name.
        if (help != null) { var hint = Text(help, "SubtleNotice", 17); hint.HorizontalAlignment = HorizontalAlignment.Left; hint.TextAlignment = TextAlignment.Left; caption.Children.Add(hint); }
        if (warning != null) caption.Children.Add(warning);
        var field = new Grid { ColumnDefinitions = new ColumnDefinitions("*,24," + inputWidth.ToString(System.Globalization.CultureInfo.InvariantCulture)), Margin = new Thickness(0, 6) };
        input.VerticalAlignment = VerticalAlignment.Center;
        field.Children.Add(caption); Grid.SetColumn(input, 2); field.Children.Add(input);
        return field;
    }

    /// <summary>A snapping slider with its current value shown beside it.</summary>
    public static (Slider Slider, Control Row) Slider(double min, double max, double step, Func<int, string> format, double width = 340)
    {
        var slider = new Slider { Minimum = min, Maximum = max, TickFrequency = step, IsSnapToTickEnabled = true, Width = width, VerticalAlignment = VerticalAlignment.Center };
        var value = Text(format((int)Math.Round(min)), size: 22);
        value.MinWidth = 100; value.Margin = new Thickness(16, 0, 0, 0);
        slider.PropertyChanged += (_, e) => { if (e.Property == Avalonia.Controls.Primitives.RangeBase.ValueProperty) value.Text = format((int)Math.Round(slider.Value)); };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(slider); row.Children.Add(value);
        return (slider, row);
    }

    public static Control Section(string title, params Control[] fields)
    {
        var stack = Stack(Text(title, "SubTitle", 30));
        foreach (var field in fields) stack.Children.Add(field);
        return Panel(stack);
    }

    public static TextBox Input(string watermark = "", int length = 256) => new() { Watermark = watermark, MaxLength = length, FontSize = 24, MinHeight = 50 };
    public static CheckBox Check(string label) => new() { Content = label, FontSize = 24, MinHeight = 48 };
    public static ListBox List(string name)
    {
        var list = new ListBox { Name = name };
        list.Classes.Add("LoadMenuList");
        OnlineListStyles.Apply(list);
        list.Styles.Add(new Style(s => s.OfType<ListBoxItem>()) { Setters = { new Setter(ContentControl.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch) } });
        return list;
    }
    /// <summary>SE2's vertical scroll bar (10 px) is drawn over the right edge of the content; its own lists keep 20 px clear.</summary>
    public const double ScrollGutter = 20;
    public static Control Scroll(Control content) => new ScrollViewer
    {
        Content = new Border { Child = content, Padding = new Thickness(0, 0, ScrollGutter, 0) },
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, AllowAutoHide = false
    };

    /// <summary>A list's column headers. Rows sit inside the list template's scroll gutter, so headers leave it too.</summary>
    public static Grid Cells(string widths, params string[] values)
    {
        var header = Cells(widths, values, false);
        header.Margin = new Thickness(0, 0, ScrollGutter, 0);
        return header;
    }

    public static Grid ListCells(string widths, params string[] values)
        => Cells(widths, values, true);

    private static Grid Cells(string widths, string[] values, bool inheritForeground)
    {
        var cells = new Control[values.Length];
        // List rows carry names: they wrap like table names instead of shrinking to one unreadable line.
        for (int i = 0; i < values.Length; i++) cells[i] = inheritForeground ? Cell(values[i], true, i == 0) : Fit(Text(values[i], i == 0 ? "Bright" : null, 24, inheritForeground));
        return CellRow(widths, cells);
    }

    public static Grid CellRow(string widths, params Control[] cells)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(widths), MinHeight = 64, Margin = new Thickness(12, 4) };
        for (int i = 0; i < cells.Length; i++) { cells[i].Margin = new Thickness(6, 0); Grid.SetColumn(cells[i], i); grid.Children.Add(cells[i]); }
        return grid;
    }

    /// <summary>One line of text that shrinks its font to fit its space instead of being cut off, centred in it when <paramref name="center"/>.</summary>
    public static Control Fit(TextBlock text, bool center = false)
    {
        text.TextWrapping = TextWrapping.NoWrap;
        text.TextTrimming = TextTrimming.None;
        return new FitText { Child = text, Center = center, VerticalAlignment = VerticalAlignment.Center };
    }

    /// <summary>
    /// Shrinks one line of text to its space. It asks for no width of its own, so a long name never widens
    /// its grid column or row (a Viewbox does, and Avalonia centres a stretched control that renders narrower than its slot).
    /// </summary>
    internal sealed class FitText : Decorator
    {
        public bool Center;

        protected override Size MeasureOverride(Size availableSize)
        {
            if (Child == null) return default;
            Child.Measure(Size.Infinity);
            return new Size(0, Child.DesiredSize.Height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            if (Child == null) return finalSize;
            var natural = Child.DesiredSize;
            double scale = natural.Width > finalSize.Width && natural.Width > 0 ? finalSize.Width / natural.Width : 1;
            Child.RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Relative);
            Child.RenderTransform = scale < 1 ? new ScaleTransform(scale, scale) : null;
            double x = Center ? Math.Max(0, (finalSize.Width - natural.Width * scale) / 2) : 0;
            Child.Arrange(new Rect(x, Math.Max(0, (finalSize.Height - natural.Height * scale) / 2), natural.Width, natural.Height));
            return finalSize;
        }
    }

    // SE2's LoadMenuList row: content padded 4 px left and right inside a 4 px right margin, left of the list's scroll gutter.
    private const double RowInsetLeft = 4 + 8, RowInsetRight = 8 + 8;

    /// <summary>A LoadMenuList for a table: no border or padding, so <see cref="TableHeader"/> can sit exactly over its rows.</summary>
    public static ListBox TableList(string name)
    {
        var list = List(name);
        list.Padding = new Thickness(0); list.BorderThickness = new Thickness(0);
        return list;
    }

    /// <summary>One banner of a <see cref="TableList"/>. Every cell is centred in its column unless it says otherwise.</summary>
    public static Grid TableRow(string widths, params Control[] cells)
    {
        // Tall enough for a name on three 17 px lines (SE2's font spaces lines about 1.75 times its size apart), so every row is one height.
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(widths), MinHeight = 90, Margin = new Thickness(8, 4) };
        for (int i = 0; i < cells.Length; i++) { cells[i].Margin = new Thickness(4, 0); Grid.SetColumn(cells[i], i); grid.Children.Add(cells[i]); }
        return grid;
    }

    /// <summary>The column headers over a <see cref="TableList"/>: same columns, same insets, so each label sits over its cells.</summary>
    public static Grid TableHeader(string widths, params Control[] labels)
    {
        var header = TableRow(widths, labels);
        header.MinHeight = 36;
        header.Margin = new Thickness(RowInsetLeft, 0, RowInsetRight + ScrollGutter, 4);
        return header;
    }

    public static Control HeaderLabel(string text, bool left = false) => Fit(Text(text, "SubTitle", 18), !left);

    /// <summary>A table cell's text: centred on one line, or for names left-aligned on up to two lines (<see cref="NameText"/>).</summary>
    public static Control Cell(string text, bool left = false, bool bright = false, double size = 24)
    {
        var block = Text(text, bright ? "Bright" : null, size, true);
        if (!left) return Fit(block, true);
        if (!string.IsNullOrEmpty(text)) ToolTip.SetTip(block, text);
        return new NameText { Child = block, Full = text ?? "", Size = size, VerticalAlignment = VerticalAlignment.Center };
    }

    /// <summary>
    /// A name on one line at full size, else wrapped a little smaller: two lines at 80 %, three at 70 % (down to 58 %
    /// if needed); every row is tall enough for three. Long words break too, so the directory's longest names
    /// (100 characters) show in full; a longer one keeps wrapping at the smallest size and the row grows. Nothing is ever
    /// cut. Like FitText it asks for no width, so a long name never widens its column.
    /// </summary>
    internal sealed class NameText : Decorator
    {
        public string Full = "";
        public double Size = 24;
        // Font scale and the lines allowed at it.
        private static readonly (double Scale, int Lines)[] Steps = { (1, 1), (0.8, 2), (0.7, 3), (0.66, 3), (0.62, 3), (0.58, 3) };

        // The width the cell was last arranged at. A grid can measure a star column wider than it arranges it; text fitted
        // to the measured width then wraps once more and its "..." line falls out of the row.
        private double _arranged = double.PositiveInfinity;

        protected override Size MeasureOverride(Size availableSize) => new(0, Fit(Math.Min(availableSize.Width, _arranged)));

        protected override Size ArrangeOverride(Size finalSize)
        {
            if (Math.Abs(finalSize.Width - _arranged) > 0.5) { _arranged = finalSize.Width; Fit(finalSize.Width); }
            return base.ArrangeOverride(finalSize);
        }

        // Text and size are set while measuring: the text is measured again right here, and the next pass ends in the same state.
        private double Fit(double width)
        {
            if (Child is not TextBlock text) return 0;
            text.TextWrapping = TextWrapping.Wrap; text.TextTrimming = TextTrimming.None;
            Show(text, Full, Size); text.Measure(new Size(width, double.PositiveInfinity));
            // Unbounded, or a column squeezed to almost nothing: nothing sensible to wrap to.
            if (double.IsInfinity(width) || width < Size * 2) return text.DesiredSize.Height;
            var space = new Size(width, double.PositiveInfinity);
            foreach (var (scale, allowed) in Steps)
            {
                Show(text, Full, Math.Round(Size * scale)); text.Measure(space);
                if (text.TextLayout.TextLines.Count <= allowed) break;
            }
            return text.DesiredSize.Height;
        }

        private static void Show(TextBlock text, string value, double size)
        {
            if (text.Text != value) text.Text = value;
            if (text.FontSize != size) text.FontSize = size;
        }
    }

    /// <summary>A small vector icon in the theme's text colour.</summary>
    public static Control Icon(Geometry shape, double size, string tip)
    {
        var icon = new Avalonia.Controls.Shapes.Path { Data = shape, Stretch = Stretch.Uniform, Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        icon.Bind(Avalonia.Controls.Shapes.Shape.FillProperty, new VRageDynamicResource("HighContrast"));
        // A transparent holder takes the pointer for the tooltip over the whole cell, not only the icon's strokes.
        var holder = new Border { Background = Brushes.Transparent, Child = icon };
        ToolTip.SetTip(holder, tip);
        return holder;
    }

    // Lazy: Geometry.Parse needs Avalonia's render platform, which a headless dedicated server never starts, and a
    // static field would make every OnlineUi use there (Raw texts in host notifications) throw TypeInitializationException.
    private static Geometry _lockShape, _shieldShape;
    public static Geometry LockShape => _lockShape ??= Geometry.Parse("M6,10 V7.5 A6,6 0 0 1 18,7.5 V10 H15.6 V7.5 A3.6,3.6 0 0 0 8.4,7.5 V10 Z M4,10 H20 V22 H4 Z");
    public static Geometry ShieldShape => _shieldShape ??= Geometry.Parse("F0 M12,2 L20,5 V11 C20,16.5 16.6,20.7 12,22 C7.4,20.7 4,16.5 4,11 V5 Z M10.6,15.6 L7.2,12.2 L8.6,10.8 L10.6,12.8 L15.4,8 L16.8,9.4 Z");

    /// <summary>
    /// A join button for one row. The list's item style recolours every TextBlock in a row, so the label follows the
    /// button's own presenter instead; disabled controls show no tooltip, so the reason sits on a holder around it.
    /// </summary>
    public static (Button Button, Border Holder) RowButton(string label, Action action, string name)
    {
        var text = new TextBlock { Text = label, FontSize = 20, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        text.Bind(TextBlock.ForegroundProperty, new Avalonia.Data.Binding("Foreground") { RelativeSource = new Avalonia.Data.RelativeSource(Avalonia.Data.RelativeSourceMode.FindAncestor) { AncestorType = typeof(Avalonia.Controls.Presenters.ContentPresenter) } });
        var button = new Button { Name = name, Content = text, MinHeight = 44, MinWidth = 0, Padding = new Thickness(6, 2), FontSize = 20, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        button.Click += (_, args) => { args.Handled = true; try { action(); } catch (Exception e) { MpMenu.Report(e.Message); } };
        return (button, new Border { Background = Brushes.Transparent, Child = button, VerticalAlignment = VerticalAlignment.Center });
    }

    /// <summary>The details panel's frame: less padding on the right, where the scroll gutter already keeps text clear of the bar.</summary>
    public static readonly Thickness DetailPadding = new(24, 24, 12, 24);

    /// <summary>The details panel beside a list: scrolling facts on top, its buttons always visible at the bottom.</summary>
    public static Grid DetailPanel(Control body, params Button[] actions)
    {
        var grid = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        grid.Children.Add(Scroll(body));
        var buttons = new StackPanel { Spacing = 12, Margin = new Thickness(0, 20, ScrollGutter, 0) };
        foreach (var action in actions) { action.HorizontalAlignment = HorizontalAlignment.Stretch; buttons.Children.Add(action); }
        Grid.SetRow(buttons, 1); grid.Children.Add(buttons);
        return grid;
    }

    /// <summary>What the details panel shows with nothing selected.</summary>
    public static Control EmptyState(string title, string hint)
    {
        var stack = new StackPanel { Spacing = 12, Margin = new Thickness(0, 120, 0, 0) };
        var heading = Text(title, "SubTitle", 28); heading.HorizontalAlignment = HorizontalAlignment.Center; heading.TextAlignment = TextAlignment.Center;
        var text = Text(hint, "SubtleNotice", 20); text.HorizontalAlignment = HorizontalAlignment.Center; text.TextAlignment = TextAlignment.Center;
        stack.Children.Add(heading); stack.Children.Add(text);
        return stack;
    }

    /// <summary>A label/value grid for the details panel. Values wrap; a null value skips its row.</summary>
    public static Grid Facts(params (string Label, Control Value)[] rows)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("112,*") };
        int row = 0;
        foreach (var (label, value) in rows)
        {
            if (value == null) continue;
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var name = Text(label, size: 19); name.Opacity = 0.65; name.VerticalAlignment = VerticalAlignment.Top; name.Margin = new Thickness(0, 7, 12, 7);
            value.VerticalAlignment = VerticalAlignment.Top; value.Margin = new Thickness(0, 5, 0, 5);
            Grid.SetRow(name, row); Grid.SetRow(value, row); Grid.SetColumn(value, 1);
            grid.Children.Add(name); grid.Children.Add(value);
            row++;
        }
        return grid;
    }

    /// <summary>A fact's value: wrapping text the player can select and copy (an address, a Workshop ID).</summary>
    public static TextBlock Value(string text, double size = 21)
    {
        var value = new SelectableTextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
        value.Bind(TextBlock.ForegroundProperty, new VRageDynamicResource("HighContrast"));
        return value;
    }

    /// <summary>A small capitalised heading between groups in the details panel.</summary>
    public static TextBlock Heading(string text)
    {
        var heading = Text(text, "SubTitle", 19); heading.Margin = new Thickness(0, 10, 0, 2); heading.Opacity = 0.8;
        return heading;
    }

}



