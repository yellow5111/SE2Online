using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Keen.Game2.Simulation.GameSystems.Antennas;
using Keen.Game2.Simulation.GameSystems.GPS;
using Keen.Game2.Simulation.GameSystems.OreDetection;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Collections;
using Keen.VRage.Voxels.Components;
using Y51_SE2Online.Core;
using Y51_SE2Online.Net;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// SE2 keeps one ore marker list for the whole session and fills it once any character is antenna-connected to the
/// detector, so in multiplayer every player saw every detector's ores. Each player gets its own list instead, and the
/// session list becomes the local player's. A player sees a detector's markers when Keen's own rule holds for their
/// character (antenna-connected: their own drill, a ship they sit in, a grid whose antennas reach them) or when they
/// stand within the detector's range.
/// </summary>
internal sealed partial class HostWorld
{
	// ponytail: one list per peer that joined this hosting session, emptied (not dropped) when they leave.
	private static readonly ConcurrentDictionary<PeerId, OreDetectionHUDSessionComponent.OreDetectorMarkers> OrePerPeer = new();

	public static void InstallOreMarkers(Harmony harmony)
	{
		OrePerPeer.Clear();
		harmony.Patch(AccessTools.Method(typeof(OreDetectionHUDSessionComponent), nameof(OreDetectionHUDSessionComponent.UpdateMarkers)), new HarmonyMethod(typeof(HostWorld), nameof(OreUpdatePrefix)));
		harmony.Patch(AccessTools.Method(typeof(OreDetectionHUDSessionComponent), nameof(OreDetectionHUDSessionComponent.RemoveOreDetection)), postfix: new HarmonyMethod(typeof(HostWorld), nameof(OreRemovePostfix)));
	}

	internal static bool IsOwnOreList(object model) => model is OreDetectionHUDSessionComponent.OreDetectorMarkers list && OrePerPeer.Values.Contains(list);

	/// <summary>The session's ore list asked for by a player becomes that player's own list.</summary>
	internal static object? OreListFor(PeerId peer, object? result) =>
		result is OreDetectionHUDSessionComponent.OreDetectorMarkers ? OrePerPeer.GetOrAdd(peer, _ => new OreDetectionHUDSessionComponent.OreDetectorMarkers()) : result;

	private static bool OreUpdatePrefix(OreDetectionHUDSessionComponent __instance)
	{
		var world = Current;
		if (world == null) return true;
		try
		{
			var all = __instance._markers.DetectorToMarker;
			var links = __instance._connections;
			ShowInRange(__instance._markers.ActiveMarkers, all, world._hostCharacterServer, links);
			foreach (var (peer, own) in OrePerPeer)
			{
				ShowInRange(own.ActiveMarkers, all, world._joiners.Values.FirstOrDefault(j => j.Peer.Equals(peer))?.Character, links);
			}
			return false;
		}
		catch (Exception ex)
		{
			world._log.WarnOnce("ore-markers", $"host world sync: per-player ore markers failed ({ex.GetType().Name}: {ex.Message}); Keen's shared list is used instead");
			return true;
		}
	}

	private static void OreRemovePostfix(OreDetectionHUDSessionComponent __instance)
	{
		var all = __instance._markers.DetectorToMarker;
		foreach (var own in OrePerPeer.Values)
		{
			for (int i = own.ActiveMarkers.Count - 1; i >= 0; i--)
			{
				if (!all.ContainsValue(own.ActiveMarkers[i])) own.ActiveMarkers.RemoveAt(i);
			}
		}
	}

	private static void ShowInRange(ObservableList<GPSMarker> shown, Dictionary<OreDetectionHUDSessionComponent.OreDetectionOrigin, GPSMarker> all, Entity? body, AntennaConnectionsSessionComponent links)
	{
		foreach (var (origin, marker) in all)
		{
			if (Sees(origin.Owner.Entity, body, links))
			{
				if (!shown.Contains(marker)) shown.Add(marker);
			}
			else
			{
				shown.Remove(marker);
			}
		}
	}

	private static bool Sees(Entity detector, Entity? body, AntennaConnectionsSessionComponent links)
	{
		if (body == null || EngineCodec.IsDead(body) || EngineCodec.IsDead(detector)) return false;
		if (links.IsConnected(detector, body)) return true;
		float range = detector.TryGet<OreDetectorComponent>()?.Range ?? 0f;
		return (detector.Data.GetWorldTransform().Position - body.Data.GetWorldTransform().Position).LengthSquared() <= (double)range * range;
	}
}
