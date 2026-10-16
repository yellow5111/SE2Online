using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace SE2OnlineInstaller;

/// <summary>
/// The backdrop: deep space with a warm nebula and a cold one, like SE2's main menu. Drawn, not shipped, so the
/// installer carries none of Keen's artwork.
/// </summary>
public sealed class Starfield : Control
{
    private readonly (double X, double Y, double R, byte A)[] _stars;

    public Starfield()
    {
        var random = new Random(51);
        _stars = new (double, double, double, byte)[420];
        for (int i = 0; i < _stars.Length; i++)
        {
            double size = random.NextDouble();
            _stars[i] = (random.NextDouble(), random.NextDouble(), size < 0.92 ? 0.6 + size * 0.6 : 1.3 + size, (byte)(70 + random.Next(185)));
        }
    }

    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        context.FillRectangle(new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.Parse("#0A1220"), 0), new GradientStop(Color.Parse("#05080E"), 0.55), new GradientStop(Color.Parse("#0B0F18"), 1) }
        }, new Rect(size));
        Glow(context, size, 0.40, 0.10, 0.55, "#55C8822A");
        Glow(context, size, 0.47, 0.17, 0.16, "#88FFD58A");
        Glow(context, size, 0.86, 0.30, 0.45, "#3346708C");
        Glow(context, size, 0.12, 0.78, 0.40, "#2236486A");
        foreach (var (x, y, r, a) in _stars)
            context.DrawEllipse(new SolidColorBrush(Color.FromArgb(a, 225, 235, 255)), null, new Point(x * size.Width, y * size.Height), r, r);
    }

    private static void Glow(DrawingContext context, Size size, double x, double y, double radius, string color)
    {
        var c = Color.Parse(color);
        var brush = new RadialGradientBrush
        {
            Center = new RelativePoint(x, y, RelativeUnit.Relative), GradientOrigin = new RelativePoint(x, y, RelativeUnit.Relative),
            RadiusX = new RelativeScalar(radius, RelativeUnit.Relative), RadiusY = new RelativeScalar(radius * 0.8, RelativeUnit.Relative),
            GradientStops = { new GradientStop(c, 0), new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1) }
        };
        context.FillRectangle(brush, new Rect(size));
    }
}
