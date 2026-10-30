using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using HarmonyLib;
using Keen.VRage.Core.Render;
using Keen.VRage.Library.Localization;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>With VSync off, a joined game shows split-second jitter of its own character and ship (ship pulled in or the
/// cockpit missing for a frame); with VSync on it is smooth. Until that is fixed at its source, VSync is held on while
/// SE2 Online is loaded: the display settings the game creates at start and every change from the options menu are
/// given VSync before they reach the render. The options menu shows VSync only as "On (Forced ON by SE2 Online)" with
/// its selector disabled; the saved setting is left as it is.
/// -y51online:vsync:free leaves VSync to the options menu.</summary>
internal static class ForceVSync
{
	private const string Label = "On (Forced ON by SE2 Online)";
	private static readonly bool Free = MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "vsync:free");
	private static int _logged;

	public static void Install(Harmony harmony)
	{
		if (Free || Dedicated.DedicatedRuntime.Headless) return;
		var system = typeof(Keen.VRage.Render.Contracts.RenderSystem);
		harmony.Patch(AccessTools.Method(system, nameof(Keen.VRage.Render.Contracts.RenderSystem.SetDisplaySettings)), prefix: new HarmonyMethod(typeof(ForceVSync), nameof(SettingsPrefix)));
		harmony.Patch(AccessTools.Method(system, nameof(Keen.VRage.Render.Contracts.RenderSystem.SetFacadeDisplaySettings)), prefix: new HarmonyMethod(typeof(ForceVSync), nameof(SettingsPrefix)));
		if (AccessTools.Method(AccessTools.TypeByName("Keen.VRage.Render12.EngineComponents.Render12EngineComponent"), "CreateRenderDisplaySettings") is { } create)
			harmony.Patch(create, postfix: new HarmonyMethod(typeof(ForceVSync), nameof(CreatePostfix)));
		// Options menu: every VSync entry reads as forced (the selector's index is the enum value, so the entries stay as
		// the game builds them) and the selector is disabled once the view is up, which greys its arrows out.
		harmony.Patch(AccessTools.PropertyGetter(typeof(LocalizedEnum<VSyncMode>), nameof(LocalizedEnum<VSyncMode>.Localized)), postfix: new HarmonyMethod(typeof(ForceVSync), nameof(LabelPostfix)));
		harmony.Patch(AccessTools.Constructor(AccessTools.TypeByName("Keen.Game2.Client.UI.Menu.Options.DisplayView")), postfix: new HarmonyMethod(typeof(ForceVSync), nameof(ViewPostfix)));
	}

	private static void LabelPostfix(ref string __result) => __result = Label;

	private static void ViewPostfix(UserControl __instance) => __instance.Loaded += (_, _) => Dispatcher.UIThread.Post(() => Lock(__instance));

	private static void Lock(UserControl view)
	{
		foreach (var selector in view.GetLogicalDescendants().OfType<SelectingItemsControl>())
			if (selector.ItemsSource is IEnumerable<LocalizedEnum<VSyncMode>>) selector.IsEnabled = false;
	}

	private static void SettingsPrefix(ref RenderDisplaySettings settings) => Hold(ref settings);

	private static void CreatePostfix(ref RenderDisplaySettings __result) => Hold(ref __result);

	private static void Hold(ref RenderDisplaySettings settings)
	{
		if (settings.VSync == VSyncMode.VSync) return;
		settings.VSync = VSyncMode.VSync;
		if (System.Threading.Interlocked.Exchange(ref _logged, 1) == 0)
			GameLog.Instance.Info("display: VSync held on while SE2 Online is loaded (multiplayer jitter with VSync off); -y51online:vsync:free turns this off");
	}
}
