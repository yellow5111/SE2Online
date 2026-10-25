using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using HarmonyLib;
using Keen.Game2.Client.UI.HUD.Notification;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// The game's HUD notices are one 48 px line cut with "…" at 480 px, so a join or leave notice could end mid-name. In an
/// SE2 Online session their text wraps inside that width and the notice grows to fit. The text lines come from data
/// templates, so they are fixed after layout passes (the game keeps and reloads its notice views: one handler each).
/// </summary>
internal static class NotificationWrap
{
	// The notice's text column is at most 480 px with 20 px margins on each side of the text.
	private const double TextWidth = 440;

	private static readonly ConditionalWeakTable<HUDNotificationView, object> Hooked = new();

	public static void Install(Harmony harmony) =>
		harmony.Patch(AccessTools.Method(typeof(HUDNotificationView), "OnLoaded"), postfix: new HarmonyMethod(typeof(NotificationWrap), nameof(LoadedPostfix)));

	private static void LoadedPostfix(HUDNotificationView __instance)
	{
		if (!MpPlugin.Online || Hooked.TryGetValue(__instance, out _)) return;
		Hooked.Add(__instance, true);
		Wrap(__instance);
		__instance.LayoutUpdated += (_, _) => Wrap(__instance);
	}

	private static void Wrap(HUDNotificationView view)
	{
		if (!MpPlugin.Online) return;
		bool changed = false;
		foreach (var visual in view.GetVisualDescendants())
		{
			if (visual is not TextBlock text || text.TextTrimming == TextTrimming.None) continue;
			text.TextTrimming = TextTrimming.None;
			text.TextWrapping = TextWrapping.Wrap;
			text.MaxWidth = TextWidth;
			changed = true;
		}
		if (changed && view.PART_Grid is { } grid && grid.MinHeight < grid.Height)
		{
			grid.MinHeight = grid.Height;
			grid.Height = double.NaN;
		}
	}
}
