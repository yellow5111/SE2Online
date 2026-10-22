using HarmonyLib;
using Keen.Game2.Simulation.GameSystems.BlockPlacement;
using Keen.Game2.Simulation.GameSystems.BlockPlacement.Projections;
using Keen.Game2.Simulation.GameSystems.Permissions;
using Keen.Game2.Simulation.Utils;
using Keen.VRage.DCS.Components;
using Keen.VRage.Multiplayer;
using Y51_SE2Online.Core;
using Y51_SE2Online.SyncEngine;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>
/// Deleting ghost (planned, not yet welded) blocks by volume (the block placer's dragged delete): SE2 4.0 checks the
/// projection's ownership when one ghost block is deleted (DeleteBlockImpl, CanRemoveBlocks) but not here, so any player
/// could delete any other player's ghost blocks. The server checks it the same way.
/// </summary>
internal static class GhostBlocks
{
	public static long Refused;

	public static void Install(Harmony harmony)
	{
		harmony.Patch(AccessTools.Method(typeof(BlockPlacerNetworkStories), nameof(BlockPlacerNetworkStories.DeleteBlocksInVolumeImpl)),
			prefix: new HarmonyMethod(typeof(GhostBlocks), nameof(VolumePrefix)));
	}

	private static bool VolumePrefix(ReplicationEventContext context, Entity targetEntity, ref BlockPlacerNetworkStories.DeletionResult? __result)
	{
		if (!AdminAccess.InSession || targetEntity?.TryGet<ProjectionSourceComponent>() == null) return true;
		var identity = context.GetIdentity();
		if (context.Session.SessionComponents.Get<PermissionsSessionComponent>().CanPlaceBlock(targetEntity, identity)) return true;
		Refused++;
		if (ServerAccess.LogDue(context.ClientId, "ghost-delete")) GameLog.Instance.Info($"access: refused identity {(long)identity} deleting ghost blocks of identity {TeamPermissions.OwnerOf(targetEntity)}");
		__result = null;
		return false;
	}
}
