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
        var text = new TextBlock { Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
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

    internal static void InstallRawTexts(HarmonyLib.Harmony harmony) => harmony.Patch(
        HarmonyLib.AccessTools.Method(typeof(LocalizationPackage), nameof(LocalizationPackage.GetText), new[] { typeof(StringId) }),
        prefix: new HarmonyLib.HarmonyMethod(typeof(OnlineUi), nameof(RawTextPrefix)));

    private static bool RawTextPrefix(LocalizationPackage __instance, StringId id, ref string __result)
    {
        if (!RawTexts.ContainsKey(id) || __instance.HasText(id)) return true;
        __result = id.ToString();
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

    public static Control Field(string label, Control input, string help = null)
    {
        input.MinHeight = 48;
        // The hint is a small line right under the setting's name, not a separate block below it.
        var caption = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        caption.Children.Add(Text(label, "SubTitle"));
        // SE2's SubtleNotice style centres the text; a hint sits left-aligned under its setting's name.
        if (help != null) { var hint = Text(help, "SubtleNotice", 17); hint.HorizontalAlignment = HorizontalAlignment.Left; hint.TextAlignment = TextAlignment.Left; caption.Children.Add(hint); }
        var field = new Grid { ColumnDefinitions = new ColumnDefinitions("*,24,480"), Margin = new Thickness(0, 6) };
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
    public static Control Scroll(Control content) => new ScrollViewer { Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

    public static Grid Cells(string widths, params string[] values)
        => Cells(widths, values, false);

    public static Grid ListCells(string widths, params string[] values)
        => Cells(widths, values, true);

    private static Grid Cells(string widths, string[] values, bool inheritForeground)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(widths), MinHeight = 64, Margin = new Thickness(12, 4) };
        for (int i = 0; i < values.Length; i++) { var cell = Text(values[i], i == 0 ? "Bright" : null, 24, inheritForeground); cell.TextWrapping = TextWrapping.NoWrap; cell.TextTrimming = TextTrimming.CharacterEllipsis; cell.Margin = new Thickness(10, 0); Grid.SetColumn(cell, i); grid.Children.Add(cell); }
        return grid;
    }

}



