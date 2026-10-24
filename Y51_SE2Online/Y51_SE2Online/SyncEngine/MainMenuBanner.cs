using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using HarmonyLib;
using Keen.Game2.Client.UI.Menu;
using Keen.Game2.Client.UI.Menu.MainMenu;
using Y51_SE2Online.Core;
using L = Y51_SE2Online.Sync.LoadScreenLayout;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// The SE2 Online logo, wordmark and version under the game's logo on the main menu. It lives in the same Canvas of the
/// GameMenu template as PART_GameLogo, so it follows the menu's own resolution and UI scaling.
/// </summary>
internal static class MainMenuBanner
{
	private const double Gap = 12, Height = 56;
	private static WriteableBitmap _logo;
	private static bool _logoFailed;

	public static void Install(Harmony harmony)
		=> harmony.Patch(AccessTools.Method(typeof(GameMenu), "OnApplyTemplate"), postfix: new HarmonyMethod(typeof(MainMenuBanner), nameof(ApplyTemplatePostfix)));

	// The template is shared with the in-game menu; the banner only shows while the DataContext is the main menu.
	private static void ApplyTemplatePostfix(GameMenu __instance, TemplateAppliedEventArgs e)
	{
		try
		{
			if (e.NameScope.Find<Image>("PART_GameLogo") is not { Parent: Canvas canvas } logo)
			{
				GameLog.Instance.WarnOnce("menu-banner", "main menu banner: the game's logo was not found in the menu; no SE2 Online banner");
				return;
			}
			if (canvas.Children.Any(c => c.Name == "OnlineMenuBanner")) return;
			double top = Canvas.GetTop(logo) + (double.IsNaN(logo.Height) ? 140 : logo.Height) + Gap;
			var banner = Build();
			Canvas.SetLeft(banner, Canvas.GetLeft(logo));
			Canvas.SetTop(banner, top);
			canvas.Children.Add(banner);

			// The buttons are bottom-anchored and grow upwards (MpMenu caps them just under the logo): never draw over them.
			Control buttons = e.NameScope.Find<Control>("PART_ButtonsPanel");
			while (buttons != null && buttons.Parent != canvas) buttons = buttons.Parent as Control;
			void Refresh() => banner.IsVisible = __instance.DataContext is MainMenuScreenViewModel
				&& (buttons == null || buttons.Bounds.Height == 0 || buttons.Bounds.Top >= top + Height);
			__instance.DataContextChanged += (_, _) => Refresh();
			if (buttons != null) buttons.PropertyChanged += (_, args) => { if (args.Property == Visual.BoundsProperty) Refresh(); };
			Refresh();
		}
		catch (Exception ex) { GameLog.Instance.WarnOnce("menu-banner", $"main menu banner: not shown ({ex.GetType().Name}: {ex.Message})"); }
	}

	private static Control Build()
	{
		var row = new StackPanel { Name = "OnlineMenuBanner", Orientation = Orientation.Horizontal, Height = Height, IsHitTestVisible = false };
		if (Logo() is { } bitmap) row.Children.Add(new Image { Source = bitmap, Width = Height, Height = Height, Stretch = Stretch.Uniform });
		var wordmark = new TextBlock { FontSize = 26, FontWeight = FontWeight.Bold, Foreground = Brush(L.White) };
		wordmark.Inlines!.Add(new Run("SE2") { Foreground = Brush(L.Amber) });
		wordmark.Inlines.Add(new Run(" ONLINE"));
		var version = new TextBlock { FontSize = 15, Foreground = Brush(L.Muted) };
		version.Inlines!.Add(new Run("SE2 Online " + MpPlugin.Version));
		var update = new Run { Foreground = Brush(L.Amber), FontWeight = FontWeight.Bold };
		version.Inlines.Add(update);
		row.Children.Add(new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Children = { wordmark, version } });

		// The release check answers some seconds after the menu opens.
		int ticks = 0;
		DispatcherTimer.Run(() =>
		{
			update.Text = UpdateText();
			return update.Text.Length == 0 && UpdatePrompt.Latest == null && MpMenu.Settings.CheckForUpdates && ++ticks < 120;
		}, TimeSpan.FromSeconds(1));
		return row;
	}

	private static string UpdateText()
	{
		string latest = UpdatePrompt.Latest;
		if (latest == null || !VersionMismatch.IsNewer(latest, MpPlugin.Version)) return "";
		return AutoUpdateClient.Installed != null ? "   Update " + AutoUpdateClient.Installed + " installed: restart the game" : "   Update available: " + latest;
	}

	private static IBrush Brush(uint argb) => new SolidColorBrush(Color.FromUInt32(argb));

	// Same decode as the loading screen: SE2's Avalonia backend only shows bitmaps written at runtime.
	private static WriteableBitmap Logo()
	{
		if (_logo != null || _logoFailed) return _logo;
		try
		{
			var (width, height, rgba) = Countries.DecodePng(File.ReadAllBytes(Path.Combine(PluginPaths.ContentDir ?? "", "Textures/GUI/Loading", "Logo.png")));
			var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Unpremul);
			using (var frame = bitmap.Lock()) Marshal.Copy(rgba, 0, frame.Address, rgba.Length);
			return _logo = bitmap;
		}
		catch (Exception ex)
		{
			_logoFailed = true;
			GameLog.Instance.WarnOnce("menu-banner-logo", $"main menu banner: the logo could not be read ({ex.GetType().Name}: {ex.Message}); text only");
			return null;
		}
	}
}
