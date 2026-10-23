using System.Linq;
using HarmonyLib;
using Keen.Game2.Client.GameSystems.GPS;
using Keen.Game2.Simulation.GameSystems.Antennas;
using Keen.Game2.Simulation.GameSystems.GPS;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// The game cuts every HUD marker name at 7 times its line height ("Long antenna na…", "[TAG] player (off…"), about 20
/// Latin characters, in focus too. Antenna markers and SE2 Online's player nametags are drawn whole (the terminal puts no
/// length limit on an antenna name); GPS, ore and every other marker stay as the game draws them.
/// -y51online:hudnames:off keeps the game's cut.
/// </summary>
internal static class HudNameWidth
{
	// The game's own switch for no cut (DrawName only cuts while MaxNameWidth > 0).
	private const float Width = 0f;

	public static void Install(Harmony harmony)
	{
		if (MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "hudnames:off")) { GameLog.Instance.Info("hud names: the game's own name cut (launch option)"); return; }
		harmony.Patch(AccessTools.Method(typeof(GPSMarkerHelpers), nameof(GPSMarkerHelpers.DrawSingleMarker)), prefix: new HarmonyMethod(typeof(HudNameWidth), nameof(SinglePrefix)));
		harmony.Patch(AccessTools.Method(typeof(GPSMarkerHelpers), nameof(GPSMarkerHelpers.DrawBunchedMarkers)), prefix: new HarmonyMethod(typeof(HudNameWidth), nameof(BunchPrefix)));
	}

	private static bool Wide(GPSMarker marker) => marker is AntennaMarkersSessionComponent.AntennaHUDMarker || marker.Identifier == NametagOverlay.MarkerIdentifier;

	private static void SinglePrefix(ref GPSMarkerHelpers.MarkerDrawSettings settings, GPSMarker marker)
	{
		if (settings.MaxNameWidth > 0f && Wide(marker)) settings.MaxNameWidth = Width;
	}

	// A bunch draws all its names with one setting: wider only when every marker in it is one of those.
	private static void BunchPrefix(ref GPSMarkerHelpers.MarkerDrawSettings settings, ref MarkerBunch bunch)
	{
		if (settings.MaxNameWidth <= 0f) return;
		foreach (var (marker, _) in bunch.Markers.Data) if (!Wide(marker)) return;
		settings.MaxNameWidth = Width;
	}
}
