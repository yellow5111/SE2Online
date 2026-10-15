using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Keen.Game2.Client.UI.HUD.Notification;
using Keen.Game2.Client.UI.InGame;
using Keen.Game2.Client.UI.Library;
using Keen.Game2.Client.UI.Library.Dialogs.TwoOptionsDialog;
using Keen.Game2.Client.UI.TerminalScreen;
using Keen.VRage.Library.Utils;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// Client side of hacking: the Hack key, the HUD progress panel, the owner's alert (with Lockdown), the counter-hack
/// glitch and the terminal boot. The host decides everything; this only shows the host's <see cref="HackView"/>s.
/// </summary>
internal static class HackUi
{
    private static readonly Dictionary<uint, (int Glitch, int Boot, HackPhase Phase)> Seen = new();
    private static readonly Queue<HackView> Alerts = new();
    private static SocialSnapshot _snapshot;
    private static bool _keyDown;
    private static object _engagedTerminal;
    private static IObservableDisposable _alert;
    private static long _alertUntilMs;
    internal static long SnapshotMs, GlitchUntilMs;

    internal static HackView[] Views => SocialUi.Snapshot.Hacks ?? Array.Empty<HackView>();

    /// <summary>The Hack key: cancels your own hack, otherwise asks the host to hack (or counter-hack) the nearest terminal.</summary>
    internal static void Request() => SocialUi.Send(new SocialRequest(SocialAction.Hack, Offset: Views.Any(v => v.Role == HackRole.Hacker) ? 0 : 1));

    internal static void Lockdown(uint grid) => SocialUi.Send(new SocialRequest(SocialAction.Lockdown, Grid: grid));

    /// <summary>"/lockdown" freezes every hack on your side's grids; never sent as chat.</summary>
    internal static bool Command(string text)
    {
        if (!text.Equals("/lockdown", StringComparison.OrdinalIgnoreCase)) return false;
        Lockdown(0);
        return true;
    }

    /// <summary>Game thread, every frame in a multiplayer world.</summary>
    internal static void Tick(Keen.Game2.GameAppComponent app, SharedUIComponent shared, bool free)
    {
        try
        {
            long now = MpClock.NowMs();
            var snapshot = SocialUi.Snapshot;
            if (!ReferenceEquals(snapshot, _snapshot)) { _snapshot = snapshot; SnapshotMs = now; OnSnapshot(app, shared, now); }
            bool down = KeyBinding.TryParse(MpMenu.Settings.HackKey, out var key) && key.IsDown(GameKeys.IsDown) && GameKeys.OwnWindowInFront();
            if (down && !_keyDown && free && !ChatUi.InputOpen) Request();
            _keyDown = down;
            // A defender opening a terminal while their grid is hacked counter-hacks (the host checks it is that grid's).
            var top = shared.TryGetTopScreenNeedingInput();
            if (top is TerminalScreenViewModel terminal && !ReferenceEquals(terminal, _engagedTerminal) && Views.Any(v => v.Role == HackRole.Defender))
            {
                _engagedTerminal = terminal;
                SocialUi.Send(new SocialRequest(SocialAction.Hack, Offset: 2));
            }
            // The alert is modal and hides the HUD, so it never outstays its hack or a few seconds.
            if (_alert != null && (now > _alertUntilMs || Views.All(v => v.Role != HackRole.Defender) || now < GlitchUntilMs)) { _alert.Dispose(); _alert = null; }
            if (free && _alert == null && Alerts.Count > 0) ShowAlert(shared, Alerts.Dequeue(), now);
        }
        catch (Exception ex) { GameLog.Instance.WarnOnce("hack-ui", "hacking: client update failed: " + ex.Message); }
    }

    private static void OnSnapshot(Keen.Game2.GameAppComponent app, SharedUIComponent shared, long now)
    {
        var views = Views;
        foreach (var v in views)
        {
            bool known = Seen.TryGetValue(v.Grid, out var seen);
            if (!known && v.Role == HackRole.Defender) { Alerts.Enqueue(v); Notify(app, $"ALERT: {v.Hacker} is hacking {v.GridName}!"); }
            if (v.Glitch > seen.Glitch) GlitchUntilMs = now + HackRules.GlitchMs;
            if (v.Boot > seen.Boot && shared.TryGetTopScreenNeedingInput() is TerminalScreenViewModel terminal)
            {
                terminal.Close();
                Notify(app, "Counter-hack feedback threw you out of the terminal!");
            }
            if (known && v.Phase != seen.Phase && v.Role == HackRole.Hacker) Notify(app, PhaseText(v));
            Seen[v.Grid] = (v.Glitch, v.Boot, v.Phase);
        }
        foreach (uint gone in Seen.Keys.Where(g => views.All(v => v.Grid != g)).ToList()) Seen.Remove(gone);
        if (views.All(v => v.Role != HackRole.Defender)) { Alerts.Clear(); _engagedTerminal = null; }
    }

    private static void Notify(Keen.Game2.GameAppComponent app, string text)
    {
        try { app.ClientSession?.SessionComponents.TryGet<SessionInGameUISessionComponent>()?.ShowNotification(HudNotification.CreateTextNotification(default, OnlineUi.Raw(text))); }
        catch (Exception ex) { GameLog.Instance.WarnOnce("hack-toast", "hacking: HUD notice failed: " + ex.Message); }
    }

    private static void ShowAlert(SharedUIComponent shared, HackView v, long now)
    {
        if (Views.All(x => x.Grid != v.Grid)) return;
        _alertUntilMs = now + 12_000;
        _alert = shared.ShowDialog(new TwoOptionsDialogViewModel(new TwoOptionsDialogDefinition
        {
            Title = OnlineUi.Raw("Grid under attack"),
            Content = OnlineUi.Raw($"{v.Hacker} is hacking your grid {v.GridName}.\n\nLock it down from here to freeze the hack, go counter-hack at one of its terminals, or stop the hacker. Typing /lockdown in chat works too."),
            ConfirmOption = OnlineUi.Raw("Lock down"), CancelOption = OnlineUi.Raw("Dismiss")
        }) { ConfirmAction = () => MpMenu.Queue(() => Lockdown(v.Grid)) });
        _alert.OnDisposed += _ => _alert = null;
    }

    internal static string PhaseText(HackView v) => v.Phase switch
    {
        HackPhase.Slowed => "Defender nearby - slowed",
        HackPhase.Lockdown => $"Remote lockdown - frozen {v.LockdownLeft} s",
        HackPhase.Halted => "Counter-hack! Signal lost...",
        HackPhase.Contested => $"Contested - {HackRules.ContestSpeed:P0} speed",
        _ => v.Role == HackRole.Hacker ? "Hacking" : "Being hacked"
    };

    /// <summary>For the grid ownership panel: locks down the selected grid (or all of yours), shown while one is hacked.</summary>
    internal static Button LockdownButton(Func<uint> grid)
    {
        var button = OnlineUi.Button("Lock down hack", () => Lockdown(grid()), "HackLockdown");
        button.IsVisible = false;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) => button.IsVisible = Views.Any(v => v.Role == HackRole.Defender);
        button.AttachedToVisualTree += (_, _) => timer.Start();
        button.DetachedFromVisualTree += (_, _) => timer.Stop();
        return button;
    }

    /// <summary>A 1920x1080 HUD layer: progress panel and the counter-hack glitch.</summary>
    internal static Control Layer() => new Viewbox { Stretch = Stretch.Uniform, IsHitTestVisible = false, Child = new HackHud() };
}

internal sealed class HackHud : Grid
{
    private static readonly IBrush Amber = new SolidColorBrush(Color.FromRgb(255, 190, 40)), Red = new SolidColorBrush(Color.FromRgb(255, 70, 60)),
        Violet = new SolidColorBrush(Color.FromRgb(210, 90, 255)), Blue = new SolidColorBrush(Color.FromRgb(120, 170, 255)), Track = new SolidColorBrush(Color.FromArgb(160, 60, 60, 60));
    // Modest alpha, no white: the glitch reads as interference without flashing the screen.
    private static readonly IBrush[] Bands =
    {
        new SolidColorBrush(Color.FromArgb(70, 0, 255, 255)), new SolidColorBrush(Color.FromArgb(70, 255, 0, 200)),
        new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)), new SolidColorBrush(Color.FromArgb(55, 120, 255, 120))
    };
    private readonly StackPanel _panels = new() { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 120, 0, 0), Width = 560 };
    private readonly Canvas _glitch = new() { Width = 1920, Height = 1080, IsHitTestVisible = false, IsVisible = false };
    private readonly Rectangle[] _bands = Enumerable.Range(0, 14).Select(_ => new Rectangle()).ToArray();
    private readonly TextBlock[] _split = { Split(Color.FromArgb(150, 255, 40, 40)), Split(Color.FromArgb(150, 40, 230, 255)), Split(Color.FromArgb(220, 230, 230, 230)) };
    private readonly TranslateTransform _shake = new();
    private readonly Random _random = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private string _signature = "";

    public HackHud()
    {
        Width = 1920; Height = 1080; IsHitTestVisible = false;
        _panels.RenderTransform = _shake;
        var scanlines = new Rectangle
        {
            Width = 1920, Height = 1080,
            Fill = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Absolute), EndPoint = new RelativePoint(0, 4, RelativeUnit.Absolute), SpreadMethod = GradientSpreadMethod.Repeat,
                GradientStops = { new GradientStop(Colors.Transparent, 0), new GradientStop(Colors.Transparent, 0.5), new GradientStop(Color.FromArgb(55, 0, 0, 0), 0.5), new GradientStop(Color.FromArgb(55, 0, 0, 0), 1) }
            }
        };
        _glitch.Children.Add(scanlines);
        foreach (var band in _bands) _glitch.Children.Add(band);
        foreach (var text in _split) { Canvas.SetTop(text, 470); _glitch.Children.Add(text); }
        Children.Add(_panels);
        Children.Add(_glitch);
        _timer.Tick += (_, _) => Update();
        AttachedToVisualTree += (_, _) => _timer.Start();
        DetachedFromVisualTree += (_, _) => _timer.Stop();
    }

    private static TextBlock Split(Color color) => new() { Text = "SIGNAL INTERFERENCE", FontSize = 64, FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(color), Width = 1920, TextAlignment = TextAlignment.Center };

    private void Update()
    {
        long now = MpClock.NowMs();
        var views = AdminAccess.InSession ? HackUi.Views : Array.Empty<HackView>();
        double elapsed = (now - HackUi.SnapshotMs) / 1000.0;
        string signature = string.Join("|", views.Select(v => Row(v, elapsed).Signature));
        if (signature != _signature)
        {
            _signature = signature;
            _panels.Children.Clear();
            foreach (var v in views.OrderBy(v => v.Role)) _panels.Children.Add(Panel(v, elapsed));
        }
        long glitchLeft = HackUi.GlitchUntilMs - now;
        _glitch.IsVisible = glitchLeft > 0;
        if (glitchLeft <= 0) { _shake.X = _shake.Y = 0; return; }
        _glitch.Opacity = Math.Clamp(glitchLeft / 600.0, 0, 1);
        foreach (var band in _bands)
        {
            band.Width = 1920; band.Height = _random.Next(3, 34);
            band.Fill = Bands[_random.Next(Bands.Length)];
            Canvas.SetLeft(band, _random.Next(-60, 60)); Canvas.SetTop(band, _random.Next(0, 1080));
        }
        for (int i = 0; i < _split.Length; i++) Canvas.SetLeft(_split[i], (i - 1) * _random.Next(4, 12) + _random.Next(-3, 4));
        _shake.X = _random.Next(-8, 9); _shake.Y = _random.Next(-4, 5);
    }

    private static (double Progress, string Signature) Row(HackView v, double elapsed)
    {
        double progress = Math.Clamp(v.Progress + v.Rate * elapsed, 0, 0.999);
        string left = v.Rate > 0 ? HostWorld.Clock((1 - progress) / v.Rate) : "--";
        return (progress, $"{v.Grid}:{v.Phase}:{(int)(progress * 400)}:{left}:{v.LockdownLeft}:{v.LockdownReadyIn}");
    }

    private static Control Panel(HackView v, double elapsed)
    {
        var (progress, _) = Row(v, elapsed);
        bool hacker = v.Role == HackRole.Hacker;
        IBrush accent = v.Phase switch { HackPhase.Contested or HackPhase.Halted => Violet, HackPhase.Slowed or HackPhase.Lockdown => Blue, _ => hacker ? Amber : Red };
        var lines = new StackPanel { Spacing = 6, Margin = new Thickness(18, 12) };
        lines.Children.Add(Text(hacker ? "HACKING  " + v.GridName : "UNDER ATTACK  " + v.GridName, 24, true, accent));
        var bar = new Grid { Height = 14, Width = 524, HorizontalAlignment = HorizontalAlignment.Left };
        bar.Children.Add(new Rectangle { Fill = Track });
        bar.Children.Add(new Rectangle { Fill = accent, Width = 524 * progress, HorizontalAlignment = HorizontalAlignment.Left });
        lines.Children.Add(bar);
        string left = v.Rate > 0 ? "about " + HostWorld.Clock((1 - progress) / v.Rate) + " left" : "no progress";
        lines.Children.Add(Text($"{progress:P0}   {left}   {HackUi.PhaseText(v)}", 19, false, Brushes.White));
        lines.Children.Add(Text(hacker
            ? "Stay within 3 m of the terminal. Damage or moving away cancels it." + (v.Powered ? " Grid powered: 1.5x slower." : "")
            : $"Hacker: {v.Hacker}.  " + (v.LockdownReadyIn > 0 ? $"Lockdown ready in {HostWorld.Clock(v.LockdownReadyIn)}." : "Type /lockdown to freeze it.") + " Counter-hack at a terminal or stop them.", 17, false, Brushes.Gainsboro));
        // Built like PlayerAimView (background, accent strip, text): a Border with a BorderBrush drew solid in the accent colour.
        var panel = new Grid();
        panel.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(0xC0, 0, 0, 0)) });
        panel.Children.Add(new Rectangle { Width = 5, Fill = accent, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Stretch });
        panel.Children.Add(lines);
        return panel;
    }

    private static TextBlock Text(string text, double size, bool bold, IBrush brush)
        => new() { Text = text, FontSize = size, FontWeight = bold ? FontWeight.Bold : FontWeight.Normal, Foreground = brush, TextWrapping = TextWrapping.Wrap };
}
