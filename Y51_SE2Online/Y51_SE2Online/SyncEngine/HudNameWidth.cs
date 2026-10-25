using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Keen.Game2.Client.GameSystems.GPS;
using Keen.Game2.Simulation.GameSystems.Antennas;
using Keen.Game2.Simulation.GameSystems.GPS;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Render.Contracts;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// The game cuts every HUD marker name at 7 times its line height with "…" ("Long antenna na…", "[TAG] player (off…"),
/// about 20 Latin characters, in focus too. Antenna markers (ships and suits), SE2 Online's player nametags and pings are
/// drawn whole instead, wrapped onto further lines of up to <see cref="LineHeights"/> line heights; GPS, ore and every
/// other marker stay as the game draws them. -y51online:hudnames:off keeps the game's cut.
/// </summary>
/// <remarks>
/// 0.16.6 to 0.18.0 lifted the width limit in prefixes on DrawSingleMarker and DrawBunchedMarkers, which never covered
/// pings (a pinger's name plus a label) and drew a long antenna name as one unbounded line. The decision is now taken
/// where the renderer enters (DisplayMarker) and applied where the cut is made (DrawName).
/// </remarks>
internal static class HudNameWidth
{
	internal const float LineHeights = 2 * GPSMarkerHelpers.MAX_NAME_WIDTH;

	// Render thread: the names (by reference) of the bunch being drawn whose markers are drawn whole.
	[ThreadStatic] private static List<string> _wide;
	[ThreadStatic] private static Dictionary<string, (float Size, string[] Lines, float Width)> _lines;
	private static int _logged;

	public static void Install(Harmony harmony)
	{
		if (MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "hudnames:off")) { GameLog.Instance.Info("hud names: the game's own name cut (launch option)"); return; }
		harmony.Patch(AccessTools.Method(typeof(GPSMarkerHelpers), nameof(GPSMarkerHelpers.DisplayMarker)), prefix: new HarmonyMethod(typeof(HudNameWidth), nameof(DisplayPrefix)));
		harmony.Patch(AccessTools.Method(typeof(GPSMarkerHelpers), nameof(GPSMarkerHelpers.DrawName)), prefix: new HarmonyMethod(typeof(HudNameWidth), nameof(NamePrefix)));
	}

	private static bool Wide(GPSMarker marker) => marker is AntennaMarkersSessionComponent.AntennaHUDMarker
		|| marker.Identifier is NametagOverlay.MarkerIdentifier or PingMarkers.MarkerIdentifier;

	private static void DisplayPrefix(ref MarkerBunch bunch)
	{
		var wide = _wide ??= new List<string>(4);
		wide.Clear();
		foreach (var (marker, _) in bunch.Markers.Data)
			if (marker?.Name is { } name && Wide(marker)) wide.Add(name);
	}

	private static bool IsWide(string name)
	{
		if (_wide is not { Count: > 0 } wide) return false;
		foreach (string w in wide) if (ReferenceEquals(w, name)) return true;
		return false;
	}

	private static bool NamePrefix(GPSMarkerHelpers.MarkerDrawSettings settings, ImmediateDrawBatch drawBatch, Font font, string name, ColorSRGB color, float fontSize,
		Vector2 nextLinePosition, ref BoundingBox2 __result)
	{
		if (settings.MaxNameWidth <= 0f || name == null || font == null || !IsWide(name)) return true;
		Vector2 size = font.MeasureString(name) * fontSize;
		if (size.X <= settings.MaxNameWidth * size.Y) return true;
		var cache = _lines ??= new Dictionary<string, (float, string[], float)>(ReferenceEqualityComparer.Instance);
		if (!cache.TryGetValue(name, out var cached) || cached.Size != fontSize)
		{
			if (cache.Count > 256) cache.Clear();
			var lines = NameWrap.Lines(name, LineHeights * size.Y, s => font.MeasureString(s).X * fontSize).ToArray();
			cache[name] = cached = (fontSize, lines, lines.Max(l => font.MeasureString(l).X * fontSize));
			if (System.Threading.Interlocked.Exchange(ref _logged, 1) == 0) GameLog.Instance.Info($"hud names: a marker name drawn whole on {lines.Length} line(s) instead of the game's cut");
		}
		ColorSRGB shadow = GPSMarkerHelpers.ApplyOpacity(ColorSRGB.Black, settings.Opacity), ink = GPSMarkerHelpers.ApplyOpacity(color, settings.Opacity);
		Vector2 at = nextLinePosition;
		foreach (string line in cached.Lines)
		{
			drawBatch.DrawString(font, at + settings.TextShadowOffset * Vector2.One, shadow, line, fontSize, ignoreBounds: false);
			drawBatch.DrawString(font, at, ink, line, fontSize, ignoreBounds: false);
			at = new Vector2(at.X, at.Y + size.Y);
		}
		__result = new BoundingBox2(nextLinePosition, new Vector2(nextLinePosition.X + cached.Width, at.Y));
		return false;
	}
}
