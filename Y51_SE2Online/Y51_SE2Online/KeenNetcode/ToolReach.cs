using System.Linq;
using HarmonyLib;
using Keen.Game2.Client.GameSystems.PlayerControl;
using Keen.Game2.Simulation.GameSystems.Physicss;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.Game2.Simulation.WorldObjects.Tools;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Mathematics;
using Y51_SE2Online.SyncEngine;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>
/// Server-side fix that loosens one check (2.4's StandingToolReach). InteractedEntityProviderServerComponent.Validate only
/// casts from the tool origin to the hit point; for a remote player the server's copy of the character lags its client,
/// so the ray grazes the deck the player stands on and welding or grinding is refused. A refusal becomes an accept only
/// when: the actor is a remote player's character, the target is a block whose cells are within <see cref="Reach"/> of the
/// tool origin, the client's point lies on that block, and the player stands on that block's grid. <see cref="Reach"/> is
/// the whole bound, so this is no reach extension beyond a hand tool.
/// </summary>
internal static class ToolReach
{
	public const double Reach = 4.0;

	// The client's hit point is block-local, so it lands on the block's cells unless the client made it up.
	private const double PointSlack = 0.25;

	public static long Accepted;

	public static void Install(Harmony harmony)
	{
		harmony.Patch(AccessTools.Method(typeof(InteractedEntityProviderServerComponent), nameof(InteractedEntityProviderServerComponent.Validate)),
			postfix: new HarmonyMethod(typeof(ToolReach), nameof(ValidatePostfix)));
	}

	private static void ValidatePostfix(InteractedEntityProviderServerComponent __instance, Entity entity, Vector3D intersection, ref bool __result)
	{
		if (__result || !AdminAccess.Hosting || entity.TryGet<CubeBlockComponent>() is not { } block || block.Grid?.Entity is not { } grid || __instance._origin is not { } origin) return;
		var cells = CubeGridCoords.GridToWorldOriented(block.AABB, grid.Data.GetWorldTransform());
		if (cells.Distance(origin.WorldTransform.Position) > Reach || cells.Distance(intersection) > PointSlack) return;
		Entity actor = __instance.Entity.GetTopLevelParent();
		long owner = TeamPermissions.OwnerOf(actor);
		if (owner == 0 || !Sync.PlayerIdentities.IsHuman(owner)) return;
		// The co-op host's own character is not lagging: it runs in the same process as the server.
		if (MpPlugin.App?.ClientSession?.SessionComponents.TryGet<ClientPlayersSessionComponent>() is { } local && (long)local.LocalPlayerIdentity == owner) return;
		Entity gridRoot = grid.GetTopLevelParent();
		if (actor.TryGet<CharacterSupportTrackerComponent>() is { } support && support.SupportingEntities.Any(s => s.GetTopLevelParent() == gridRoot))
		{
			__result = true;
			Accepted++;
		}
	}
}
