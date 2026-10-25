using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Keen.Game2.Client.UI.Library;
using Keen.VRage.UI.Screens;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;
using L = Y51_SE2Online.Sync.LoadScreenLayout;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// SE2 Online's own look on the game's loading screen while a join holds it: the gameplay screenshot, a dark band with the
/// SE2 ONLINE wordmark, the server, the step being waited on with a progress bar, and a rotating tip. It is a child of
/// the game's LoadingTransitionScreen (that screen is exclusive on the Top layer: a screen of the plugin's own would be
/// suspended under it), so it closes with it. If it cannot be made, the game's own screen shows the same progress as text.
/// </summary>
internal static class LoadScreenOverlay
{
	private const string Folder = "Textures/GUI/Loading";

	// Bisect switch: -y51online:loadscreen:off keeps the game's own loading screen (with SE2 Online's progress text).
	private static readonly bool Off = MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "loadscreen:off");

	private static Grid? _root;
	private static TextBlock? _server, _stage, _percent, _detail, _tip;
	private static Border? _fill;
	private static Image? _background;
	private static L.View? _pending;
	private static volatile bool _posted;
	private static bool _failed;
	private static WriteableBitmap? _backgroundBitmap, _logoBitmap;
	private static System.Threading.Tasks.Task<(int Width, int Height, byte[] Rgba)>? _decoding;

	public static void Install(HarmonyLib.Harmony harmony)
	{
		if (Off) { GameLog.Instance.Info("loading screen: SE2 Online's look is off (launch option); the game's own screen shows the join"); return; }
		var open = HarmonyLib.AccessTools.Method(typeof(SharedUIComponent), nameof(SharedUIComponent.OpenLoadingTransitionScreenIfNeeded));
		harmony.Patch(open, prefix: new HarmonyLib.HarmonyMethod(typeof(LoadScreenOverlay), nameof(OpenPrefix)), postfix: new HarmonyLib.HarmonyMethod(typeof(LoadScreenOverlay), nameof(OpenPostfix)));
	}

	private static void OpenPrefix(SharedUIComponent __instance, out bool __state) => __state = __instance._loadingTransitionScreen != null;

	// Main thread, right after the game opened its loading screen (OnTransitionToGameBegin): only a screen this call made,
	// only for loading into an SE2 Online join (not the unloading screen when leaving).
	private static void OpenPostfix(SharedUIComponent __instance, Keen.Game2.Client.UI.Overlays.TransitionKind transitionKind, bool __state)
	{
		try
		{
			if (_failed || __state || transitionKind != Keen.Game2.Client.UI.Overlays.TransitionKind.Loading || MpPlugin.Controller?.Current?.Join != true
				|| __instance._loadingTransitionScreen is not ScreenHandle handle) return;
			_decoding ??= System.Threading.Tasks.Task.Run(DecodeBackground);
			_pending = null;
			var view = handle.ScreenView;
			Dispatcher.UIThread.Post(() => Attach(view, 0));
		}
		catch (Exception ex) { Fail(ex); }
	}

	private static void Attach(ScreenView view, int tries)
	{
		try
		{
			if (view.State == ScreenLifetimeState.Closed) return;
			if (view.Content is not Grid grid)
			{
				// Its content is made with the screen; retried for 2 s on a timer (posts can all run in one dispatcher pass).
				if (tries < 40) DispatcherTimer.RunOnce(() => Attach(view, tries + 1), TimeSpan.FromMilliseconds(50));
				else GameLog.Instance.WarnOnce("loading-attach", "loading screen: the game's loading screen had no layout to draw into; it shows the join itself this time");
				return;
			}
			var root = Build();
			grid.Children.Add(root);
			_root = root;
			view.OnDisposed += _ => Dispatcher.UIThread.Post(() => { if (ReferenceEquals(_root, root)) Detach(); });
			GameLog.Instance.Info("loading screen: SE2 Online's look is up");
			if (_pending is { } first) Apply(first);
			SetBackground();
		}
		catch (Exception ex) { Fail(ex); }
	}

	private static void Detach()
	{
		_root = null;
		_pending = null;
		_server = _stage = _percent = _detail = _tip = null;
		_fill = null;
		_background = null;
	}

	private static void Fail(Exception ex)
	{
		_failed = true;
		try { if (_root?.Parent is Panel parent) parent.Children.Remove(_root); } catch (Exception) { }
		Detach();
		GameLog.Instance.Warn($"loading screen: SE2 Online's look could not be shown ({ex.GetType().Name}: {ex.Message}); the game's own screen shows the join");
	}

	/// <summary>Any thread: what the screen shows now (applied on the UI thread, at most once per post).</summary>
	public static void Update(L.View view)
	{
		if (Off || _failed) return;
		_pending = view;
		if (_posted) return;
		_posted = true;
		Dispatcher.UIThread.Post(() =>
		{
			_posted = false;
			if (_pending is { } latest && _root != null)
			{
				try { Apply(latest); }
				catch (Exception ex) { Fail(ex); }
			}
		});
	}

	private static void Apply(L.View v)
	{
		if (_root == null) return;
		_server!.Text = "Joining " + v.Server + (string.IsNullOrEmpty(v.World) || v.World == v.Server ? "" : "  /  " + v.World);
		_stage!.Text = v.Stage;
		_percent!.Text = $"{(int)Math.Clamp(v.Percent, 0, 100)}%";
		_detail!.Text = v.Detail;
		_fill!.Width = (L.Right - L.Column) * Math.Clamp(v.Percent, 0, 100) / 100;
		_tip!.Inlines!.Clear();
		_tip.Inlines.Add(new Run(v.TipKey) { Foreground = Brush(L.Amber), FontWeight = FontWeight.Bold });
		_tip.Inlines.Add(new Run("   " + v.TipText) { Foreground = Brush(L.Muted) });
	}

	private static IBrush Brush(uint argb) => new SolidColorBrush(Color.FromUInt32(argb));

	private static TextBlock Text(double size, uint color, FontWeight weight = FontWeight.Normal)
		=> new() { FontSize = size, Foreground = Brush(color), FontWeight = weight, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis };

	private static T At<T>(T control, double left, double top) where T : Control
	{
		Canvas.SetLeft(control, left);
		Canvas.SetTop(control, top);
		return control;
	}

	private static Grid Build()
	{
		var root = new Grid { IsHitTestVisible = false, Background = Brushes.Black };
		_background = new Image { Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
		root.Children.Add(_background);
		root.Children.Add(new Border { Background = Brush(L.ScreenDim) });

		// The band reaches both screen edges (ultrawide too); its height follows the screen like the content does.
		var band = new Grid { RowDefinitions = new RowDefinitions($"{L.BandTop - 120}*,120*,{L.Height - L.BandTop}*") };
		var fade = new Border
		{
			Background = new LinearGradientBrush
			{
				StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
				GradientStops = { new GradientStop(Color.FromArgb(0, 8, 12, 18), 0), new GradientStop(Color.FromUInt32(L.Band), 1) }
			}
		};
		Grid.SetRow(fade, 1);
		band.Children.Add(fade);
		var bottom = new Border { Background = Brush(L.Band), BorderBrush = Brush(L.BandEdge), BorderThickness = new Thickness(0, 2, 0, 0) };
		Grid.SetRow(bottom, 2);
		band.Children.Add(bottom);
		root.Children.Add(band);

		var canvas = new Canvas { Width = L.Width, Height = L.Height };
		if (Logo() is { } logo) canvas.Children.Add(At(new Image { Source = logo, Width = L.LogoSize, Height = L.LogoSize, Stretch = Stretch.Uniform }, L.Left, L.LogoTop));
		var wordmark = Text(L.WordmarkSize, L.White, FontWeight.Bold);
		wordmark.Inlines!.Add(new Run("SE2") { Foreground = Brush(L.Amber) });
		wordmark.Inlines.Add(new Run(" ONLINE"));
		canvas.Children.Add(At(wordmark, L.WordmarkLeft, L.WordmarkTop));
		canvas.Children.Add(At(_server = Text(L.ServerSize, L.Muted), L.WordmarkLeft, L.ServerTop));
		_server.Width = L.Column - 40 - L.WordmarkLeft;
		_server.TextWrapping = TextWrapping.Wrap; _server.TextTrimming = TextTrimming.None;
		canvas.Children.Add(At(_stage = Text(L.StageSize, L.White, FontWeight.Bold), L.Column, L.StageTop));
		_stage.Width = L.Right - L.Column - 120;
		canvas.Children.Add(At(_percent = Text(L.StageSize, L.Amber, FontWeight.Bold), L.Column, L.StageTop));
		_percent.Width = L.Right - L.Column;
		_percent.TextAlignment = TextAlignment.Right;
		canvas.Children.Add(At(new Border { Width = L.Right - L.Column, Height = L.BarHeight, Background = Brush(L.Track) }, L.Column, L.BarTop));
		canvas.Children.Add(At(_fill = new Border { Width = 0, Height = L.BarHeight, Background = Brush(L.Amber) }, L.Column, L.BarTop));
		canvas.Children.Add(At(_detail = Text(L.DetailSize, L.Dim), L.Column, L.DetailTop));
		_detail.Width = L.Right - L.Column;
		canvas.Children.Add(At(_tip = Text(L.TipSize, L.Muted), L.Left, L.TipTop));
		_tip.Width = L.Right - L.Left;
		_tip.TextWrapping = TextWrapping.Wrap;
		_tip.MaxHeight = L.Height - L.TipTop - 8;
		// Uniform: 1080p to 4K alike; on a wider screen the content keeps its size and sits over the band, bottom aligned.
		root.Children.Add(new Viewbox { Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Bottom, Child = canvas });
		return root;
	}

	// The screenshot is a JPEG decoded off the UI thread with the game's own ImageSharp; the game's Avalonia backend only
	// shows bitmaps written at runtime (as CountryFlags does).
	private static (int, int, byte[]) DecodeBackground()
	{
		string file = Path.Combine(PluginPaths.ContentDir ?? throw new FileNotFoundException("no Content folder"), Folder, "Background.jpg");
		using var image = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(File.ReadAllBytes(file));
		var rgba = new byte[image.Width * image.Height * 4];
		image.CopyPixelDataTo(rgba);
		return (image.Width, image.Height, rgba);
	}

	private static void SetBackground()
	{
		if (_background == null) return;
		if (_backgroundBitmap != null) { _background.Source = _backgroundBitmap; return; }
		var decoding = _decoding;
		if (decoding == null) return;
		if (!decoding.IsCompleted)
		{
			decoding.ContinueWith(_ => Dispatcher.UIThread.Post(SetBackground), System.Threading.Tasks.TaskScheduler.Default);
			return;
		}
		if (!decoding.IsCompletedSuccessfully)
		{
			GameLog.Instance.WarnOnce("loading-background", $"loading screen: the background picture could not be read ({decoding.Exception?.GetBaseException().Message}); a plain background instead");
			return;
		}
		try
		{
			var (width, height, rgba) = decoding.Result;
			_backgroundBitmap = Bitmap(width, height, rgba);
			_decoding = null;
			_background.Source = _backgroundBitmap;
		}
		catch (Exception ex) { GameLog.Instance.WarnOnce("loading-background", $"loading screen: the background picture could not be shown ({ex.GetType().Name}: {ex.Message})"); }
	}

	private static WriteableBitmap? Logo()
	{
		if (_logoBitmap != null) return _logoBitmap;
		try
		{
			var (width, height, rgba) = Countries.DecodePng(File.ReadAllBytes(Path.Combine(PluginPaths.ContentDir ?? "", Folder, "Logo.png")));
			return _logoBitmap = Bitmap(width, height, rgba);
		}
		catch (Exception ex)
		{
			GameLog.Instance.WarnOnce("loading-logo", $"loading screen: the logo could not be shown ({ex.GetType().Name}: {ex.Message})");
			return null;
		}
	}

	// SE2's RuntimeBitmapsImpl reads the locked buffer back tightly packed as straight-alpha RGBA (see CountryFlags).
	private static WriteableBitmap Bitmap(int width, int height, byte[] rgba)
	{
		var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Unpremul);
		using var frame = bitmap.Lock();
		Marshal.Copy(rgba, 0, frame.Address, rgba.Length);
		return bitmap;
	}
}
