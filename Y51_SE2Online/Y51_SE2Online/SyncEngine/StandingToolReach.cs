using System;
using System.Linq;
using System.Threading;
using Keen.Game2.Simulation.GameSystems.Physicss;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.Game2.Simulation.WorldObjects.Tools;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Mathematics;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// Host-side gameplay fix that LOOSENS a server check. The game's InteractedEntityProviderServerComponent.Validate only
/// tests line of sight from the tool origin to the hit point. For a joiner the server's copy of the character lags its
/// client, so that ray often grazes the deck the joiner stands on and the weld/grind target is refused. This postfix turns
/// such a refusal into an accept only when all of these hold: the actor is a joiner's character (never the host's own),
/// the target is a block, the block itself (its cells on the host's grid, not the point the client sent) is within
/// <see cref="Reach"/> of the tool origin, the client's point lies on that block, and the joiner is standing on the same
/// grid. Nothing else about the interaction changes; in particular the game applies no extra distance limit here, so
/// <see cref="Reach"/> is the whole bound.
/// </summary>
internal static class StandingToolReach
{
	public const double Reach = 4.0;
	/// <summary>The client's hit point is block-local, so it lands on the block's cells unless the client made it up.</summary>
	private const double PointSlack = 0.25;

	public static long Accepted;

	public static void ValidatePostfix(InteractedEntityProviderServerComponent __instance, Entity entity, Vector3D intersection, ref bool __result)
	{
		if (__result)
		{
			return;
		}
		try
		{
			CubeBlockComponent? block = entity.TryGet<CubeBlockComponent>();
			Entity? grid = block?.Grid?.Entity;
			IToolOriginProvider? origin = __instance._origin;
			if (block == null || grid == null || origin == null)
			{
				return;
			}
			OrientedBoundingBoxD cells = CubeGridCoords.GridToWorldOriented(block.AABB, grid.Data.GetWorldTransform());
			if (cells.Distance(origin.WorldTransform.Position) > Reach || cells.Distance(intersection) > PointSlack)
			{
				return;
			}
			Entity actor = __instance.Entity.GetTopLevelParent();
			if (HostWorld.Current?.IsJoinerCharacter(actor) != true)
			{
				return;
			}
			Entity gridRoot = grid.GetTopLevelParent();
			CharacterSupportTrackerComponent? support = actor.TryGet<CharacterSupportTrackerComponent>();
			if (support != null && support.SupportingEntities.Any(s => s.GetTopLevelParent() == gridRoot))
			{
				__result = true;
				Interlocked.Increment(ref Accepted);
			}
		}
		catch (Exception)
		{
		}
	}
}
