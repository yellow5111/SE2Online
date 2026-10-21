using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Keen.Game2.Simulation.WorldObjects.Characters;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.Components;
using Y51_SE2Online.Core;
using Y51_SE2Online.NetHost;
using Y51_SE2Online.Sync;
using Y51_SE2Online.SyncEngine;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>
/// SE2 4.0 takes a player's suit colour and suit skin from their client and stores them on the server, but never sends them
/// on: the server applies a client's property change with only the field setter and its [Server] handlers, not the handler
/// that replicates the value. Other players saw the change only after the character was rebuilt for them (rejoin, respawn).
/// The server now re-raises each change on its next tick through the property's own signal, so Keen's writer sends it out.
/// The suit menu applies every entry the pointer hovers: past <see cref="SuitRate{T}"/>'s budget only the latest goes out.
/// </summary>
internal static class SuitSync
{
	private static readonly ConcurrentQueue<(Entity Character, bool Skin)> Changed = new();

	public static long Rebroadcast;

	private static readonly SuitRate<Entity> Rate = new();

	// Server thread only: the (client, kind) pairs with a change waiting for budget.
	private static readonly HashSet<(ushort Client, byte Kind)> Waiting = new();

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
		long now = MpClock.NowMs();
		while (Changed.TryDequeue(out var change))
		{
			var key = (ClientOf(change.Character), change.Skin ? (byte)1 : (byte)0);
			if (Rate.Offer(key.Item1, key.Item2, change.Character, now)) { Waiting.Remove(key); Send(change.Character, change.Skin); }
			else Waiting.Add(key);
		}
		if (Waiting.Count == 0) return;
		foreach (var key in Waiting.ToArray())
		{
			if (Rate.Due(key.Client, key.Kind, now) is not { } character) continue;
			Waiting.Remove(key);
			Send(character, key.Kind == 1);
		}
	}

	private static ushort ClientOf(Entity character)
	{
		long owner = TeamPermissions.OwnerOf(character);
		return owner == 0 ? SyncProtocol.HostClientId : NetSession.Current?.Host?.Players().FirstOrDefault(p => p.IdentityId == owner)?.ClientId ?? SyncProtocol.HostClientId;
	}

	// The current value is sent, so a change that waited goes out as the look the player settled on.
	private static void Send(Entity character, bool skinChanged)
	{
		if (EngineCodec.IsDead(character) || character.TryGet<SkinComponent>() is not { } skin) return;
		if (skinChanged) SkinComponent.__InternalSuitSkinProperty.TriggerLocally(character, skin.__signalTableOffset + 2, skin.SuitSkin);
		else SkinComponent.__InternalSuitColorProperty.TriggerLocally(character, skin.__signalTableOffset, skin.SuitColor);
		Rebroadcast++;
	}
}
