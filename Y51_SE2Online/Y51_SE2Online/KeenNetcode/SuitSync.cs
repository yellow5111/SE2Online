using System.Collections.Concurrent;
using HarmonyLib;
using Keen.Game2.Simulation.WorldObjects.Characters;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.Components;
using Y51_SE2Online.Core;
using Y51_SE2Online.SyncEngine;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>
/// SE2 4.0 takes a player's suit colour and suit skin from their client and stores them on the server, but never sends them
/// on: the server applies a client's property change with only the field setter and its [Server] handlers, not the handler
/// that replicates the value. Other players saw the change only after the character was rebuilt for them (rejoin, respawn).
/// The server now re-raises each change on its next tick through the property's own signal, so Keen's writer sends it out.
/// </summary>
internal static class SuitSync
{
	private static readonly ConcurrentQueue<(Entity Character, bool Skin)> Changed = new();

	public static long Rebroadcast;

	public static void Install(Harmony harmony)
	{
		var callbacks = AccessTools.TypeByName("Game2.Simulation.Replication.CB.InProcessReplicationReaderCallbacks");
		harmony.Patch(AccessTools.Method(callbacks, "Keen_Game2_Simulation_WorldObjects_Characters_SkinComponent_SuitColorProperty_Apply"),
			postfix: new HarmonyMethod(typeof(SuitSync), nameof(ColorApplied)));
		harmony.Patch(AccessTools.Method(callbacks, "Keen_Game2_Simulation_WorldObjects_Characters_SkinComponent_SuitSkinProperty_Apply"),
			postfix: new HarmonyMethod(typeof(SuitSync), nameof(SkinApplied)));
		harmony.Patch(AccessTools.Method(typeof(SkinComponent), "Init"), prefix: new HarmonyMethod(typeof(SuitSync), nameof(InitPrefix)));
	}

	// The game resets a saved DLC suit skin to the default wherever this game does not own that DLC: a dedicated server (no
	// Steam client) reset every DLC skin on load and respawn, and players without the DLC saw the owner in the default suit.
	// In a multiplayer session the saved skin stays; picking a skin is still checked against ownership in the owner's menu.
	private static bool InitPrefix(SkinComponent __instance, SkinComponentObjectBuilder ob)
	{
		if (!AdminAccess.InSession && !Y51_SE2Online.Dedicated.DedicatedRuntime.Requested) return true;
		if (ob.SuitSkin?.DLC == null || !__instance._definition.PersistentSkin) return true;
		__instance._suitColor = ob.SuitColor;
		__instance._suitSecondaryColor = ob.SuitSecondaryColor;
		__instance._suitSkin = ob.SuitSkin;
		return false;
	}

	private static bool OnServer(Entity entity) => MpPlugin.App?.ServerSession is { } server && ReferenceEquals(entity.Scene, server.Scene);

	private static void ColorApplied(Entity entity)
	{
		if (OnServer(entity)) Changed.Enqueue((entity, false));
	}

	private static void SkinApplied(Entity entity)
	{
		if (OnServer(entity)) Changed.Enqueue((entity, true));
	}

	/// <summary>Server tick, outside the replication job that applied the client's change.</summary>
	public static void Flush()
	{
		while (Changed.TryDequeue(out var change))
		{
			if (change.Character.TryGet<SkinComponent>() is not { } skin) continue;
			if (change.Skin) SkinComponent.__InternalSuitSkinProperty.TriggerLocally(change.Character, skin.__signalTableOffset + 2, skin.SuitSkin);
			else SkinComponent.__InternalSuitColorProperty.TriggerLocally(change.Character, skin.__signalTableOffset, skin.SuitColor);
			Rebroadcast++;
		}
	}
}
