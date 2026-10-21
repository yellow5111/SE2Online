using System.Linq;
using HarmonyLib;
using Keen.Game2.Client.GameSystems.PlayerControl;
using Keen.Game2.Simulation.GameSystems.Physicss;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.Game2.Simulation.WorldObjects.Tools;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.Core.Game.GameSystems.Queries;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Library.Memory;
using Keen.VRage.Physics;
using Keen.VRage.Physics.Queries;
using Y51_SE2Online.SyncEngine;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>
/// Server-side fix that loosens one check (2.4's StandingToolReach). InteractedEntityProviderServerComponent.Validate only
/// casts from the tool origin to the hit point; for a remote player the server's copy of the character lags its client,
/// so the ray grazes the deck the player stands on and welding or grinding is refused. A refusal becomes an accept only
/// when: the actor is a remote player's character, the target is a block whose cells are within <see cref="Reach"/> of the
/// tool origin, the client's point lies on that block, the player stands on that block's grid, and what stops the ray is
/// the top of that deck at the player's feet with the target not below it. <see cref="Reach"/> is the whole bound, so this
/// is no reach extension beyond a hand tool, and armor or a wall in the way still refuses.
/// </summary>
internal static class ToolReach
{
	public const double Reach = 4.0;

	// The client's hit point is block-local, so it lands on the block's cells unless the client made it up.
	private const double PointSlack = 0.25;

	// How far the deck hit may lie from the server's copy of the feet, which lags the client.
	private const double DeckSlack = 0.5;

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
		if (actor.TryGet<CharacterSupportTrackerComponent>() is { } support && support.SupportingEntities.Any(s => s.GetTopLevelParent() == gridRoot)
			&& StoppedByDeck(__instance, actor, gridRoot, origin.WorldTransform.Position, intersection))
		{
			__result = true;
			Accepted++;
		}
	}

	private static bool StoppedByDeck(InteractedEntityProviderServerComponent provider, Entity actor, Entity gridRoot, Vector3D from, Vector3D to)
	{
		var feet = actor.Data.GetWorldTransform();
		Vector3 up = feet.Orientation.GetUp();
		using Buffer<SweepQueryHit> hits = default;
		var ray = RayCastArgs.CreateFromTo(from, to);
		try
		{
			ray.IgnoredEntities.Add(provider.Entity.DEntity);
			provider._physics.CastRay(hits, in ray, CollisionPreset.Closest);
		}
		finally { ray.Dispose(); }
		if (hits.Count == 0) return false;
		SweepQueryHit hit = hits[0];
		if (Entity.TryGetFromDataEntity((Scene: actor.Scene, Entity: hit.Body)) is not { } body || body.GetTopLevelParent() != gridRoot || Vector3.Dot(hit.Normal, up) < 0.7f) return false;
		Vector3D upD = up;
		double deck = Vector3D.Dot(hit.Position - feet.Position, upD), target = Vector3D.Dot(to - hit.Position, upD);
		return deck > -DeckSlack && deck < DeckSlack && target > -PointSlack;
	}
}
