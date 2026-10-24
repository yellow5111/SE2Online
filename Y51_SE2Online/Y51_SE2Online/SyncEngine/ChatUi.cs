using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Keen.Game2.Client.UI.Library;
using Keen.VRage.UI.Screens;
using Keen.VRage.Library.Utils;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

internal static class ChatUi
{
    private const long HudLineMs = 15000;
    // In 1920x1080 units: left of SE2's speed readout (x 500) and above the suit status icons (y 835).
    internal const double LeftEdge = 24, BottomEdge = 262;
    private static IObservableDisposable _input, _hud;

    public static bool InputOpen => _input != null;

    public static void OpenInput(SharedUIComponent shared)
    {
        if (_input != null || shared == null) return;
        _input = shared.CreateScreen<ChatScreen>(new ChatScreenViewModel(), showCursor: false, ScreenLayer.Top);
        _input.OnDisposed += _ => _input = null;
    }

    /// <summary>Keeps the passive chat HUD open while a multiplayer world is on screen.</summary>
    public static void TickHud(SharedUIComponent shared, bool wanted)
    {
        if (!wanted || shared == null) { _hud?.Dispose(); _hud = null; return; }
        if (_hud != null) return;
        _hud = shared.CreateScreen<ChatHudScreen>(new ChatHudViewModel(), showCursor: false, ScreenLayer.Top);
        _hud.OnDisposed += _ => _hud = null;
    }

    public static void Close() { _input?.Dispose(); _input = null; _hud?.Dispose(); _hud = null; }

    internal static Control Line(ChatLine line, double opacity)
    {
        var text = new TextBlock { FontSize = 18, TextWrapping = TextWrapping.Wrap, Opacity = opacity, MaxWidth = 440 };
        IBrush nameBrush = line.Kind switch { ChatKind.System => Brushes.Gold, ChatKind.Admin => new SolidColorBrush(Color.FromRgb(255, 170, 60)), _ => new SolidColorBrush(Color.FromRgb(120, 200, 255)) };
        // Role decorations come from the group's style table, drawn as their own runs: a name can never carry them.
        var style = line.Kind == ChatKind.System ? null : AdminAccess.StyleOf(line.Group);
        IBrush Paint(string hex, IBrush fallback) => GroupStyle.Readable(hex) is var (r, g, b) ? new SolidColorBrush(Color.FromRgb(r, g, b)) : fallback;
        void Add(string part, IBrush brush, bool bold) { if (!string.IsNullOrEmpty(part)) text.Inlines.Add(new Avalonia.Controls.Documents.Run(part) { Foreground = brush, FontWeight = bold ? FontWeight.Bold : FontWeight.Normal }); }
        if (line.Kind != ChatKind.System)
        {
            var name = Paint(style?.NameColor, nameBrush);
            Add(style?.NamePrefix, Paint(style?.NamePrefixColor, name), true);
            Add(line.From, name, true);
            Add(style?.NameSuffix, Paint(style?.NameSuffixColor, name), true);
            Add(": ", name, true);
        }
        var body = line.Kind == ChatKind.System ? Brushes.Gold : Paint(style?.TextColor, Brushes.White);
        Add(style?.ChatPrefix, Paint(style?.ChatPrefixColor, body), false);
        Add(line.Text, body, false);
        Add(style?.ChatSuffix, Paint(style?.ChatSuffixColor, body), false);
        return new Border { Child = text, Background = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), Padding = new Thickness(8, 3), Margin = new Thickness(0, 1), HorizontalAlignment = HorizontalAlignment.Left };
    }

    internal static Viewbox Frame(Control content)
    {
        var layout = new Grid { Width = 1920, Height = 1080 };
        content.HorizontalAlignment = HorizontalAlignment.Left; content.VerticalAlignment = VerticalAlignment.Bottom;
        content.Margin = new Thickness(LeftEdge, 0, 0, BottomEdge);
        layout.Children.Add(content);
        return new Viewbox { Stretch = Stretch.Uniform, Child = layout, IsHitTestVisible = false };
    }

    internal static bool HudExpired(long receivedMs, long now) => now - receivedMs > HudLineMs;
    internal static double HudOpacity(long receivedMs, long now) => Math.Clamp((HudLineMs - (now - receivedMs)) / 2000.0, 0, 1);
}

public sealed class ChatHudViewModel : ScreenViewModel
{
    public event Action Refresh;
    private long _next;
    public ChatHudViewModel()
    {
        NeedsGeneralUIInput = false; NeedsBackground = false; KeepsOtherScreensVisible = true; AllowsInputFromLowerScreens = true;
        AllowsInputBelowUI = true; CanHaveFocus = false;
        OnTick += () => { long now = MpClock.NowMs(); if (now < _next) return; _next = now + 250; Refresh?.Invoke(); };
    }
}

public sealed class ChatHudScreen : ScreenView
{
    private readonly StackPanel _lines = new() { Spacing = 0 };
    private ChatHudViewModel _model;
    private bool _closed;
    private string _signature = "";

    public ChatHudScreen()
    {
        IsHitTestVisible = false;
        var hud = new Grid { IsHitTestVisible = false };
        hud.Children.Add(ChatUi.Frame(_lines));
        hud.Children.Add(PlayerAimView.Layer());
        hud.Children.Add(HackUi.Layer());
        Content = hud;
        OnDisposing += _ => { _closed = true; if (_model != null) _model.Refresh -= QueueRefresh; };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_model != null) _model.Refresh -= QueueRefresh;
        _model = DataContext as ChatHudViewModel;
        if (_model != null) _model.Refresh += QueueRefresh;
    }

    private void QueueRefresh() => Dispatcher.UIThread.Post(() => { if (!_closed) Update(); });

    private Avalonia.Controls.Control _antennaToggle;
    private long _nextAntennaScan;

    /// <summary>SE2 ships the suit HUD's antenna toggle permanently disabled; light it while the suit broadcasts.</summary>
    private void ShowBroadcast(long now)
    {
        if ((_antennaToggle == null || !Avalonia.VisualTree.VisualExtensions.IsAttachedToVisualTree(_antennaToggle)) && now >= _nextAntennaScan)
        {
            _nextAntennaScan = now + 2000;
            _antennaToggle = Avalonia.Controls.TopLevel.GetTopLevel(this) is { } top
                ? Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(top).OfType<Avalonia.Controls.Control>().FirstOrDefault(c => c.Name == "Antenna" && c.Classes.Contains("HUDToggle") && Avalonia.VisualTree.VisualExtensions.GetVisualAncestors(c).Any(a => a.GetType().Name == "CharacterHUDScreen"))
                : null;
        }
        if (_antennaToggle != null && _antennaToggle.IsEnabled != MpMenu.Settings.SuitBroadcast) _antennaToggle.IsEnabled = MpMenu.Settings.SuitBroadcast;
    }

    private void Update()
    {
        long now = MpClock.NowMs();
        ShowBroadcast(now);
        var visible = ChatUi.InputOpen ? Array.Empty<(ChatLine Line, long ReceivedMs)>() : ChatLog.Recent(6).Where(l => !ChatUi.HudExpired(l.ReceivedMs, now)).ToArray();
        string talking = VoiceChat.Talking.Count == 0 ? "" : "Talking: " + string.Join(", ", VoiceChat.Talking);
        string signature = ChatLog.Version + ":" + talking + ":" + string.Join(",", visible.Select(l => (int)(ChatUi.HudOpacity(l.ReceivedMs, now) * 8)));
        if (signature == _signature) return;
        _signature = signature;
        _lines.Children.Clear();
        foreach (var (line, received) in visible) _lines.Children.Add(ChatUi.Line(line, ChatUi.HudOpacity(received, now)));
        if (talking.Length > 0) _lines.Children.Add(ChatUi.Line(new ChatLine("Voice", 0, talking, ChatKind.System, 0), 1));
    }
}

public sealed class ChatScreenViewModel : ScreenViewModel
{
    public event Action Refresh;
    private long _next;
    public ChatScreenViewModel()
    {
        NeedsBackground = false; KeepsOtherScreensVisible = true; AllowsInputFromLowerScreens = false;
        InitializeInputContext();
        OnTick += () => { long now = MpClock.NowMs(); if (now < _next) return; _next = now + 250; Refresh?.Invoke(); };
    }
    public void Close() => SignalWantsClose();
}

public sealed class ChatScreen : ScreenView
{
    private readonly StackPanel _lines = new();
    private readonly TextBox _input = new() { MaxLength = ChatLog.MaxText, FontSize = 18, MinHeight = 36, Width = 456, Watermark = "Say something (Enter sends, Esc closes)" };
    private ChatScreenViewModel _model;
    private bool _closed;
    private long _version = -1;

    public ChatScreen()
    {
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(_lines);
        stack.Children.Add(_input);
        var layout = new Grid { Width = 1920, Height = 1080 };
        stack.HorizontalAlignment = HorizontalAlignment.Left; stack.VerticalAlignment = VerticalAlignment.Bottom; stack.Margin = new Thickness(ChatUi.LeftEdge, 0, 0, ChatUi.BottomEdge);
        layout.Children.Add(stack);
        Content = new Viewbox { Stretch = Stretch.Uniform, Child = layout };
        _input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                string text = _input.Text;
                _input.Text = "";
                MpMenu.Queue(() => AdminAccess.Chat(text));
                _model?.Close();
            }
            else if (e.Key == Key.Escape) { e.Handled = true; _model?.Close(); }
        };
        AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() => _input.Focus());
        OnDisposing += _ => { _closed = true; if (_model != null) _model.Refresh -= QueueRefresh; };
        Update();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_model != null) _model.Refresh -= QueueRefresh;
        _model = DataContext as ChatScreenViewModel;
        if (_model != null) _model.Refresh += QueueRefresh;
    }

    private void QueueRefresh() => Dispatcher.UIThread.Post(() => { if (!_closed) Update(); });

    private void Update()
    {
        if (_version == ChatLog.Version) return;
        _version = ChatLog.Version;
        _lines.Children.Clear();
        foreach (var (line, _) in ChatLog.Recent(10)) _lines.Children.Add(ChatUi.Line(line, 1));
        if (!_input.IsFocused) _input.Focus();
    }
}
