using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Keen.VRage.Core.Game.GameSystems.ProceduralGeneration;
using Keen.VRage.DCS.Components;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// A joiner's game generates its own copies of the asteroids around it, and drops each one when the server's copy of
/// the same asteroid has rendered its first cell (ProceduralVoxelEntityComponent.RegisterExternally). SE2 only arms that
/// hand-over for a copy that shares the server's storage in-process (VoxelRenderComponent.OnAddedToScene), which the
/// host's copy here never does. Both copies stayed: the local one kept showing untouched rock and blocking the player
/// over every hole mined, blasted or crashed into the host's copy.
/// </summary>
internal static class ProceduralVoxelHandoff
{
	private static FieldInfo? _notify;

	internal static int Armed;

	internal static void Install(Harmony harmony)
	{
		var render = Type.GetType("Keen.VRage.Voxels.Client.Components.VoxelRenderComponent, VRage.Voxels.Client");
		_notify = render == null ? null : AccessTools.Field(render, "NotifyFirstCellRemoved");
		var added = render == null ? null : AccessTools.Method(render, "Keen.VRage.Core.Game.Systems.IInSceneListener.OnAddedToScene");
		if (_notify == null || added == null)
		{
			GameLog.Instance.Info("procedural asteroids: VoxelRenderComponent hand-over not found; joiners may see their own copy of mined asteroids");
			return;
		}
		harmony.Patch(added, postfix: new HarmonyMethod(typeof(ProceduralVoxelHandoff), nameof(Postfix)));
	}

	private static void Postfix(object __instance)
	{
		try
		{
			if (__instance is not Component component || ClientWorld.Current is not { } world) return;
			var entity = component.Entity;
			if (entity.Components.OfType<ProceduralEntityComponentBase>().FirstOrDefault()?.ID is null || world.NetIdOf(entity) is null) return;
			_notify!.SetValue(__instance, true);
			Armed++;
		}
		catch (Exception ex)
		{
			GameLog.Instance.Info("procedural asteroids: hand-over check failed: " + ex.Message);
		}
	}
}
