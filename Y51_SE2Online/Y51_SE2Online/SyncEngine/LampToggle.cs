using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Keen.VRage.UI.Shared.Controls.BeveledBorder;

namespace Y51_SE2Online.SyncEngine;

/// <summary>An on/off tile with a lamp on the left and the label centred. Unlit it is a dark tile with a hollow lamp; lit, the
/// lamp glows Highlight and washes the tile amber. Still a CheckBox: IsChecked, IsCheckedChanged, keys and automation as before.</summary>
internal sealed class LampToggle : CheckBox
{
    private readonly string _text;
    private readonly Control[] _below;
    private BeveledBorder _tile;
    private Border _lamp, _halo;
    private TextBlock _label;

    /// <param name="below">Lines under the label (help, warning), centred with it.</param>
    public LampToggle(string label, params Control[] below)
    {
        _text = label;
        _below = below;
        Content = label;
        AutomationProperties.SetName(this, label);
        FontSize = 17;
        MinHeight = 32;
		MaxWidth = 460;
		HorizontalAlignment = HorizontalAlignment.Left;
        Template = new FuncControlTemplate<LampToggle>((t, _) => t.Build());
    }

    private Control Build()
    {
        // Keen's renderer ignores BoxShadow, so the glow is a radial gradient behind the lamp.
        _halo = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(13), IsHitTestVisible = false };
        _lamp = new Border { Width = 9, Height = 9, CornerRadius = new CornerRadius(4.5), BorderThickness = new Thickness(1.5) };
        var lamp = new Panel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        lamp.Children.Add(_halo);
        lamp.Children.Add(_lamp);
        _label = new TextBlock { Text = _text, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        _label.Bind(TextBlock.FontSizeProperty, this.GetObservable(FontSizeProperty));
        var text = new StackPanel { Spacing = 0, Margin = new Thickness(0, 3), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(_label);
        foreach (var line in _below) text.Children.Add(line);
        // The empty right column mirrors the lamp's, so the label sits in the middle of the tile.
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("32,*,32") };
        body.Children.Add(lamp);
        Grid.SetColumn(text, 1);
        body.Children.Add(text);
        _tile = new BeveledBorder { Child = body, Bevel = new CornerRadius(5, 0, 5, 0), BorderThickness = new Thickness(1), ClampDirection = ClampDirection.None };
        Update();
        return _tile;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        var p = change.Property;
        if (p == IsCheckedProperty || p == IsPointerOverProperty || p == IsPressedProperty || p == IsFocusedProperty || p == IsEffectivelyEnabledProperty)
            Update();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Update();
    }

    private void Update()
    {
        if (_tile == null || _lamp == null || _halo == null || _label == null) return;
        bool on = IsChecked == true, hot = IsPointerOver, focus = Classes.Contains(":focus-visible");
        Color accent = Palette("Highlight", 0xFFE08A0D), edge = Palette("Secondary", 0xFF627C82), dim = Palette("Primary", 0xFF94AFB6),
            bright = Palette("HighContrast", 0xFFFFFFFF), dark = Palette("BackgroundDark", 0xFF000E0D);
        _tile.Background = on ? Linear(accent, IsPressed ? 0.3 : 0.5, IsPressed ? 0.06 : 0.12) : new SolidColorBrush(dark, IsPressed ? 0.95 : hot ? 0.8 : 0.6);
        _tile.BorderBrush = new SolidColorBrush(focus ? bright : on || hot ? accent : edge, on || hot || focus ? 1 : 0.7);
        _label.Foreground = new SolidColorBrush(on || hot ? bright : dim);
        _lamp.Background = on ? Radial(Color.FromRgb(0xFF, 0xF3, 0xD6), accent) : new SolidColorBrush(dark);
        _lamp.BorderBrush = new SolidColorBrush(on ? accent : edge);
        _halo.Background = on ? Radial(Color.FromArgb(0x99, accent.R, accent.G, accent.B), Color.FromArgb(0, accent.R, accent.G, accent.B)) : null;
        Opacity = IsEffectivelyEnabled ? 1 : 0.4;
    }

    private Color Palette(string key, uint fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is Color color ? color : Color.FromUInt32(fallback);

    private static IBrush Linear(Color c, double from, double to) => new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.FromArgb((byte)(from * 255), c.R, c.G, c.B), 0), new GradientStop(Color.FromArgb((byte)(to * 255), c.R, c.G, c.B), 1) },
    };

    private static IBrush Radial(Color centre, Color edge) => new RadialGradientBrush
    {
        GradientStops = { new GradientStop(centre, 0), new GradientStop(edge, 1) },
    };
}
