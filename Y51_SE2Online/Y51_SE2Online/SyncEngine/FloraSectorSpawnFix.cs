using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Keen.VRage.Library.Memory;
using Keen.VRage.Render.Contracts;
using Keen.VRage.Voxels.Storage.Planet.PlanetEnvironment;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// Hands LogicalFloraClientSector.Spawn only the removed ids that are still render instances (see <see cref="FloraRemovals"/>),
/// so a sector with many cut or cleared trees still draws the ones left standing.
/// </summary>
internal static class FloraSectorSpawnFix
{
	private static FieldInfo? _renderInstances;

	internal static int Fixed;

	internal static void Install(Harmony harmony)
	{
		var sector = Type.GetType("Keen.VRage.Voxels.Client.Storage.Planet.PlanetEnvironment.LogicalFloraClientSector, VRage.Voxels.Client");
		var builder = Type.GetType("Keen.VRage.Voxels.Client.Storage.Planet.PlanetEnvironment.LogicalFloraClientSectorObjectBuilder, VRage.Voxels.Client");
		_renderInstances = builder == null ? null : AccessTools.Field(builder, "_renderInstances");
		var spawn = sector == null ? null : AccessTools.DeclaredMethod(sector, "Spawn", new[] { typeof(HashSet<short>) });
		if (spawn == null || _renderInstances?.FieldType != typeof(Buffer<FloraSectorEntity.FloraInstance>))
		{
			GameLog.Instance.Info("flora: LogicalFloraClientSector.Spawn not found; sectors with many removed trees may not render");
			return;
		}
		harmony.Patch(spawn, prefix: new HarmonyMethod(typeof(FloraSectorSpawnFix), nameof(Prefix)));
		GameLog.Instance.Info("flora: sector render fix on (trees cut before a sector loads no longer hide the rest of it)");
	}

	private static void Prefix(LogicalFloraSector __instance, ref HashSet<short> removedItems)
	{
		try
		{
			if (removedItems == null || removedItems.Count == 0 || _renderInstances!.GetValue(__instance.Builder) is not Buffer<FloraSectorEntity.FloraInstance> instances) return;
			var render = instances.ReadOnlyData;
			var ids = new short[render.Length];
			for (int i = 0; i < ids.Length; i++) ids[i] = render[i].InstanceId;
			if (FloraRemovals.StillRendered(ids, removedItems) is not { } still) return;
			if (ids.Length - removedItems.Count <= 0 && Interlocked.Increment(ref Fixed) == 1)
			{
				GameLog.Instance.Info($"flora: sector {__instance.Builder.SectorId} drawn with {ids.Length - still.Count} of {ids.Length} trees ({removedItems.Count} removed in it in all); SE2 would have hidden it (0.15.2 fix, further ones counted)");
			}
			removedItems = still;
		}
		catch (Exception ex)
		{
			GameLog.Instance.Info("flora: sector spawn check failed: " + ex.Message);
		}
	}
}
