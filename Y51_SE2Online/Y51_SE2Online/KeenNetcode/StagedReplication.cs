using System.Collections.Generic;
using HarmonyLib;
using Keen.VRage.DCS.Components;
using Keen.VRage.Multiplayer.Replications;
using Y51_SE2Online.Core;
using Y51_SE2Online.SyncEngine;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>
/// SE2 4.0's server keeps the set of entities it sends to every player who joins later
/// (ReplicationServerSessionComponent._replicatedEntities, sent by ReplicateInitialEntitySetToPeer). Entities that arrive in
/// a staged bundle (IEntitySpawner.SpawnBundleAsync: encounters, respawn ships, planet landers, pastes) are sent to the
/// players connected at that moment (ServerSceneContext.ReplicateStagedEntitiesAsync) but never enter that set, so a
/// player who joined or rejoined afterwards never got them, and the server's updates and signals for them failed on that
/// player forever ("Object NetworkId(...) is not registered"). The finished staged batch is added to the set, as a
/// broadcast batch is (ServerSceneContext.CommitBroadcastBundle).
/// </summary>
internal static class StagedReplication
{
	private static readonly AccessTools.FieldRef<ReplicationServerSessionComponent, HashSet<Entity>> Replicated =
		AccessTools.FieldRefAccess<ReplicationServerSessionComponent, HashSet<Entity>>("_replicatedEntities");

	public static void Install(Harmony harmony)
	{
		harmony.Patch(AccessTools.Method(typeof(ReplicationServerSessionComponent), nameof(ReplicationServerSessionComponent.CompleteStagedReplication)),
			postfix: new HarmonyMethod(typeof(StagedReplication), nameof(CompletePostfix)));
		harmony.Patch(AccessTools.Method(typeof(ReplicationServerSessionComponent), "ReplicateInitialEntitySetToPeer"),
			prefix: new HarmonyMethod(typeof(StagedReplication), nameof(InitialSetPrefix)));
	}

	private static void CompletePostfix(ReplicationServerSessionComponent __instance, Entity[] entities)
	{
		if (!MpPlugin.Online) return;
		var set = Replicated(__instance);
		foreach (var entity in entities)
			if (!EngineCodec.IsDead(entity)) set.Add(entity);
	}

	// Keen removes an entity from the set when its removal is replicated; a staged entity removed by a path that never
	// paired it would stay listed, and a joining player must not be sent a dead entity.
	private static void InitialSetPrefix(ReplicationServerSessionComponent __instance)
	{
		if (!MpPlugin.Online) return;
		int removed = Replicated(__instance).RemoveWhere(EngineCodec.IsDead);
		if (removed > 0) GameLog.Instance.WarnOnce("staged-dead", $"replication: {removed} removed entities were still listed for joining players; left out");
	}
}
