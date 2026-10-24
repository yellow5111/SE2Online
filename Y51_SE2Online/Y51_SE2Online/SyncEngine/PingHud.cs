using System;
using System.Collections.Generic;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Keen.Game2.Client.UI.Library;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Library.Utils;
using Keen.VRage.UI.Screens;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.SyncEngine;

/// <summary>The compass strip (top) and the recent pings feed (above chat), a click-through HUD screen of their own.</summary>
internal static class PingHud
{
	private static IObservableDisposable _hud;

	internal static void Tick(SharedUIComponent shared, bool wanted)
	{
		if (!wanted || shared == null) { _hud?.Dispose(); _hud = null; return; }
		if (_hud != null) return;
		try
		{
			_hud = shared.CreateScreen<PingHudScreen>(new PingHudViewModel(), showCursor: false, ScreenLayer.Top);
			if (_hud != null) _hud.OnDisposed += _ => _hud = null;
		}
		catch (Exception ex) { GameLog.Instance.WarnOnce("ping-hud", "pings: the compass strip and feed are unavailable: " + ex.Message); }
	}

	private static readonly Dictionary<string, Bitmap> Glyphs = new();

	private static Bitmap Load(string name)
	{
		if (!Glyphs.TryGetValue(name, out var image))
		{
			try { image = PingIcons.HudPath(name) is { } path && File.Exists(path) ? new Bitmap(path) : null; }
			catch (Exception) { image = null; }
			Glyphs[name] = image;
		}
		return image;
	}

	/// <summary>A pre-coloured screen-edge pip (12x20 at 1080p), tip at the bottom centre, rotated about its centre.</summary>
	internal static Control Pip(string variant, double angle)
	{
		var image = Load("pip_" + variant);
		Control pip = image != null ? new Image { Source = image, Width = 12, Height = 20 } : new Rectangle { Width = 12, Height = 20, Fill = Brushes.White };
		pip.RenderTransform = new RotateTransform(angle);
		pip.RenderTransformOrigin = RelativePoint.Center;
		pip.IsHitTestVisible = false;
		return pip;
	}

	/// <summary>A glyph tinted with a colour: the white PNG is the mask, the colour the fill.</summary>
	internal static Control Glyph(string glyph, ColorSRGB colour, double size)
	{
		var image = Load(glyph);
		var brush = new SolidColorBrush(Color.FromRgb(colour.R, colour.G, colour.B));
		if (image == null) return new Ellipse { Width = size * 0.5, Height = size * 0.5, Fill = brush, Margin = new Thickness(size * 0.25) };
		return new Rectangle { Width = size, Height = size, Fill = brush, OpacityMask = new ImageBrush(image) { Stretch = Stretch.Uniform } };
	}
}

public sealed class PingHudViewModel : ScreenViewModel
{
	public event Action Refresh;
	private long _next;

	public PingHudViewModel()
	{
		NeedsGeneralUIInput = false; NeedsBackground = false; KeepsOtherScreensVisible = true; AllowsInputFromLowerScreens = true;
		AllowsInputBelowUI = true; CanHaveFocus = false;
		OnTick += () => { long now = MpClock.NowMs(); if (now < _next) return; _next = now + 100; Refresh?.Invoke(); };
	}
}

public sealed class PingHudScreen : ScreenView
{
	private const double FeedBottom = ChatUi.BottomEdge + 190;
	private const double StripHeight = 50;
	private static readonly (double Heading, string Letter)[] Cardinals = { (0, "N"), (90, "E"), (180, "S"), (270, "W") };
	private readonly Canvas _compass = new() { Width = 1920, Height = StripHeight, IsHitTestVisible = false };
	private readonly Canvas _pips = new() { Width = 1920, Height = 1080, IsHitTestVisible = false };
	private readonly Grid _strip = new() { Width = 1920, Height = StripHeight, IsHitTestVisible = false };
	private readonly StackPanel _feed = new() { Spacing = 3, IsHitTestVisible = false };
	private PingHudViewModel _model;
	private PingHudState _shown;
	private bool _closed;

	public PingHudScreen()
	{
		IsHitTestVisible = false;
		var strip = _strip;
		strip.Children.Add(_compass);
		var frame = new Grid { Width = 1920, Height = 1080, IsHitTestVisible = false };
		strip.VerticalAlignment = VerticalAlignment.Top;
		strip.Margin = new Thickness(0, 4, 0, 0);
		_feed.HorizontalAlignment = HorizontalAlignment.Left; _feed.VerticalAlignment = VerticalAlignment.Bottom;
		_feed.Margin = new Thickness(ChatUi.LeftEdge, 0, 0, FeedBottom);
		frame.Children.Add(strip);
		frame.Children.Add(_pips);
		frame.Children.Add(_feed);
		Content = new Viewbox { Stretch = Stretch.Uniform, IsHitTestVisible = false, Child = frame };
		OnDisposing += _ => { _closed = true; if (_model != null) _model.Refresh -= QueueRefresh; };
	}

	protected override void OnDataContextChanged(EventArgs e)
	{
		base.OnDataContextChanged(e);
		if (_model != null) _model.Refresh -= QueueRefresh;
		_model = DataContext as PingHudViewModel;
		if (_model != null) _model.Refresh += QueueRefresh;
	}

	private void QueueRefresh() => Dispatcher.UIThread.Post(() => { if (!_closed) Update(); });

	private void Update()
	{
		var state = PingMarkers.Hud;
		if (ReferenceEquals(state, _shown)) return;
		_shown = state;
		_compass.Children.Clear();
		_strip.IsVisible = state.CompassFade > 0;
		_strip.Opacity = state.CompassFade;
		if (state.CompassFade > 0) Scale(state.Facing);
		foreach (var tick in state.Ticks)
		{
			var colour = new SolidColorBrush(Color.FromRgb(tick.Colour.R, tick.Colour.G, tick.Colour.B));
			var mark = new Rectangle { Width = 3, Height = 9, Fill = colour };
			Canvas.SetLeft(mark, tick.X - 1.5); Canvas.SetTop(mark, 20);
			var glyph = PingHud.Glyph(tick.Glyph, tick.Colour, 18);
			Canvas.SetLeft(glyph, tick.X - 9); Canvas.SetTop(glyph, 29);
			var distance = new TextBlock { Text = tick.Distance, FontSize = 13, FontWeight = FontWeight.Bold, Foreground = colour };
			Canvas.SetLeft(distance, tick.X + 11); Canvas.SetTop(distance, 30);
			_compass.Children.Add(mark);
			_compass.Children.Add(glyph);
			_compass.Children.Add(distance);
		}
		_pips.Children.Clear();
		foreach (var pip in state.Pips)
		{
			// The tip sits on the edge point: the pip's centre is 10 units back along its direction.
			double radians = (pip.Angle + 90) * Math.PI / 180;
			var control = PingHud.Pip(pip.Variant, pip.Angle);
			Canvas.SetLeft(control, pip.X - Math.Cos(radians) * 10 - 6); Canvas.SetTop(control, pip.Y - Math.Sin(radians) * 10 - 10);
			_pips.Children.Add(control);
		}
		_feed.Children.Clear();
		// Newest at the bottom, nearest the chat.
		for (int i = state.Feed.Length - 1; i >= 0; i--) _feed.Children.Add(Row(state.Feed[i], i == 0));
	}

	// The heading scale: a faint line with a mark every 15 degrees and N, E, S, W, centred on where the camera faces.
	private void Scale(double facing)
	{
		var faint = new SolidColorBrush(Color.FromArgb(0x90, 255, 255, 255));
		var line = new Rectangle { Width = 1860, Height = 1, Fill = faint };
		Canvas.SetLeft(line, 30); Canvas.SetTop(line, 19);
		_compass.Children.Add(line);
		for (int heading = 0; heading < 360; heading += 15)
		{
			double relative = PingLogic.Relative(heading, facing);
			if (Math.Abs(relative) > 120) continue;
			double x = PingLogic.CompassX(relative);
			var cardinal = Array.Find(Cardinals, c => c.Heading == heading).Letter;
			if (cardinal != null)
			{
				var letter = new TextBlock { Text = cardinal, FontSize = 15, FontWeight = FontWeight.Bold, Width = 24, TextAlignment = TextAlignment.Center,
					Foreground = cardinal == "N" ? new SolidColorBrush(Color.FromRgb(240, 160, 30)) : Brushes.White };
				Canvas.SetLeft(letter, x - 12); Canvas.SetTop(letter, -2);
				_compass.Children.Add(letter);
				continue;
			}
			var mark = new Rectangle { Width = 1, Height = heading % 45 == 0 ? 7 : 4, Fill = faint };
			Canvas.SetLeft(mark, x); Canvas.SetTop(mark, 19 - mark.Height);
			_compass.Children.Add(mark);
		}
	}

	private static Control Row(PingHudState.Row row, bool newest)
	{
		var colour = new SolidColorBrush(Color.FromRgb(row.Colour.R, row.Colour.G, row.Colour.B));
		var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
		line.Children.Add(new Rectangle { Width = 3, Height = 22, Fill = colour });
		line.Children.Add(PingHud.Glyph(row.Glyph, row.Colour, 20));
		int colon = row.Text.IndexOf(": ", StringComparison.Ordinal);
		var text = new TextBlock { FontSize = 18, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 460, TextTrimming = TextTrimming.CharacterEllipsis };
		text.Inlines.Add(new Avalonia.Controls.Documents.Run(colon > 0 ? row.Text[..colon] : "") { Foreground = colour, FontWeight = FontWeight.Bold });
		text.Inlines.Add(new Avalonia.Controls.Documents.Run(colon > 0 ? row.Text[colon..] : row.Text) { Foreground = Brushes.White });
		line.Children.Add(text);
		// The HUD is click-through: the hint names the command that keeps the newest ping.
		if (newest) line.Children.Add(new TextBlock { Text = "/keep saves it", FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.FromRgb(255, 210, 60)) });
		return new Border { Child = line, Background = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), Padding = new Thickness(0, 1, 10, 1), HorizontalAlignment = HorizontalAlignment.Left, Opacity = row.Alpha };
	}
}
