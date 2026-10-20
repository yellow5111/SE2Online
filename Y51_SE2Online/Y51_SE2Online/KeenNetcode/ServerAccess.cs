using System;
using HarmonyLib;
using Keen.Game2.Simulation.GameSystems.Identities;
using Keen.Game2.Simulation.GameSystems.Ownership;
using Keen.Game2.Simulation.GameSystems.Permissions;
using Keen.Game2.Simulation.GameSystems.Respawn;
using Keen.Game2.Simulation.RuntimeSystems.Validators;
using Keen.Game2.Simulation.StreamedUI;
using Keen.Game2.Simulation.StreamedUI.Respawn;
using Keen.Game2.Simulation.StreamedUI.Terminal.ControlPanel;
using Keen.Game2.Simulation.WorldObjects.Characters;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Definitions;
using Keen.VRage.Library.Utils;
using Keen.VRage.Multiplayer;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;
using Y51_SE2Online.SyncEngine;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>
/// SE2 4.0's server never checks access for its terminal screens: opening a block's control panel, its inventory or its
/// production queue, moving items between inventories and binding to a medbay all validate only that the caller is a
/// player (an empty check), so SE2 Online's "access denied" showed in the UI while the action still went through. The
/// server now refuses them with the same rules the UI shows: grid sharing, factions, block allow and deny lists.
/// </summary>
internal static class ServerAccess
{
	public static long Refused;

	public static void Install(Harmony harmony)
	{
		// Each check on its own, so a game update that renames one target only turns that check off.
		Patch(harmony, "terminal screens", AccessTools.Method(typeof(StreamedBaseSessionComponent), "AsEntity"), postfix: nameof(AsEntityPostfix));
		Patch(harmony, "item transfers", AccessTools.Method(typeof(CommonValidators), nameof(CommonValidators.ValidateInteractedEntities)), prefix: nameof(InteractedPrefix));
		Patch(harmony, "control panel", AccessTools.Method(typeof(StreamedControlPanelSessionComponent), nameof(StreamedControlPanelSessionComponent.Attach),
			new[] { typeof(Entity), typeof(ControlPanelListModel), typeof(ReplicationEventContext) }), prefix: nameof(ControlPanelPrefix));
		Patch(harmony, "medbays", AccessTools.Method(typeof(RespawnSessionComponent), nameof(RespawnSessionComponent.ActivateRespawnPoint)), prefix: nameof(ActivateRespawnPrefix));
		Patch(harmony, "story caller", AccessTools.Method(typeof(Keen.VRage.Multiplayer.Transport.OutOfProcess.OutOfProcessPeerTransport), "ProcessStory"),
			prefix: nameof(StoryPrefix), finalizer: nameof(StoryFinalizer));
		var toolbar = typeof(Keen.Game2.Simulation.WorldObjects.CubeBlocks.Toolbar.BlockToolbarServerComponent);
		Patch(harmony, "button panels", System.Linq.Enumerable.FirstOrDefault(AccessTools.GetDeclaredMethods(toolbar), m => m.Name == "ActivateTile" && m.GetParameters().Length == 3), prefix: nameof(ToolbarPrefix));
		Patch(harmony, "button panel edits", AccessTools.Method(toolbar, "ClearTile"), prefix: nameof(ToolbarPrefix));
		Patch(harmony, "admin tools", AccessTools.Method(typeof(Keen.VRage.Game.Utilities.CoreValidators), nameof(Keen.VRage.Game.Utilities.CoreValidators.HasAdminAccess)), prefix: nameof(AdminPrefix));
		var inventories = typeof(Keen.Game2.Simulation.StreamedUI.Terminal.Inventories.StreamedInventorySessionComponent);
		var inventoryList = typeof(Keen.Game2.Simulation.StreamedUI.Terminal.Inventories.InventoryListModelStreamed);
		Patch(harmony, "inventory lists", AccessTools.Method(inventories, "Attach", new[] { typeof(Entity), inventoryList, typeof(ReplicationEventContext) }), prefix: nameof(InventoryListPrefix));
		Patch(harmony, "inventory list entries", AccessTools.Method(inventories, "AddEntity"), prefix: nameof(InventoryEntryPrefix));
	}

	private static void Patch(Harmony harmony, string what, System.Reflection.MethodBase? target, string? prefix = null, string? postfix = null, string? finalizer = null)
	{
		try
		{
			if (target == null) throw new MissingMethodException(what);
			harmony.Patch(target, prefix: prefix == null ? null : new HarmonyMethod(typeof(ServerAccess), prefix), postfix: postfix == null ? null : new HarmonyMethod(typeof(ServerAccess), postfix),
				finalizer: finalizer == null ? null : new HarmonyMethod(typeof(ServerAccess), finalizer));
		}
		catch (Exception ex) { GameLog.Instance.Warn($"access: {what} check not installed ({ex.GetType().Name}: {ex.Message})"); }
	}

	// Keen runs a client's story on the server inside that client's transport; stories declared [Server(null)] get no
	// validator and cannot see their caller, so the caller is kept here while one runs.
	[ThreadStatic] private static Keen.VRage.Multiplayer.ClientId? _storyClient;

	private static void StoryPrefix(Keen.VRage.Multiplayer.Transport.OutOfProcess.OutOfProcessPeerTransport __instance, out Keen.VRage.Multiplayer.ClientId? __state)
	{
		__state = _storyClient;
		_storyClient = __instance.PeerClientId;
	}

	private static Exception? StoryFinalizer(Exception? __exception, Keen.VRage.Multiplayer.ClientId? __state)
	{
		_storyClient = __state;
		return __exception;
	}

	// Button panels and every other block toolbar: pressing or clearing a tile needs the block's use access (2.4 checked
	// grid sharing on the host; 4.0's RequestActivateTile has no validator).
	private static bool ToolbarPrefix(Component __instance)
	{
		if (!Enforcing || _storyClient is not { } client || client == Keen.VRage.Multiplayer.ClientId.Server) return true;
		var identity = MpPlugin.App?.ServerSession?.SessionComponents.TryGet<IdentitiesSessionComponent>()?.GetIdentityFromClient(client);
		if (identity is not { } who || MayUse(null, who, __instance.Entity)) return true;
		Refused++;
		GameLog.Instance.Info($"access: refused identity {(long)who} on {__instance.Entity.GetComposition()?.DebugNameShort} toolbar (no access)");
		return false;
	}

	// SE2 4.0's admin check returns true for everyone, so any player could teleport, spawn ships, edit voxels with the
	// voxel hand, make safe zones and use every other admin tool. On an SE2 Online server it means SE2 Online's own admin
	// role: the host's own game, and players with the Admin role (access list).
	private static bool AdminPrefix(ReplicationEventContext context, ref bool __result)
	{
		if (!Enforcing) return true;
		__result = context.IsLocal || context.IsFromServer || IsAdmin(context);
		return false;
	}

	// Opening a container lists every inventory on its ship, and the client then asks for each one at once; one refused
	// container failed that whole request (the server refused it as "not there"), and on the listen host the failure
	// reached the crash handler. A container the player may not use is left out of the list instead, so it is never asked
	// for. Blocks added to the ship later are checked against the player the list was made for.
	private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Keen.Game2.Simulation.StreamedUI.Terminal.Inventories.InventoryListModelStreamed, System.Runtime.CompilerServices.StrongBox<IdentityId>> _listOwners = new();

	private static void InventoryListPrefix(Keen.Game2.Simulation.StreamedUI.Terminal.Inventories.InventoryListModelStreamed model, ReplicationEventContext? context)
	{
		if (context != null) _listOwners.AddOrUpdate(model, new System.Runtime.CompilerServices.StrongBox<IdentityId>(IdentityOf(context)));
	}

	private static bool InventoryEntryPrefix(Entity entity, Keen.Game2.Simulation.StreamedUI.Terminal.Inventories.InventoryListModelStreamed model, ReplicationEventContext? context)
	{
		if (!Enforcing || entity == null) return true;
		IdentityId who;
		if (context != null) who = IdentityOf(context);
		else if (_listOwners.TryGetValue(model, out var owner)) who = owner.Value;
		else return true;
		if (MayUse(context, who, entity)) return true;
		Refused++;
		return false;
	}

	private static bool IsAdmin(ReplicationEventContext context)
	{
		var host = AdminAccess.Host;
		if (host == null) return false;
		long identity = (long)IdentityOf(context);
		var player = System.Linq.Enumerable.FirstOrDefault(host.Players(), p => p.IdentityId == identity);
		return player != null && host.IsAdminClient(player.ClientId);
	}

	private static bool Enforcing => AdminAccess.Hosting;

	/// <summary>Whether this identity may use the entity a terminal screen or request names. Never throws: a check that
	/// cannot decide allows, because an exception here faults the player's request and their game.</summary>
	internal static bool MayUse(ReplicationEventContext? context, IdentityId identity, Entity entity)
	{
		try
		{
			// No admin or host exemption: only a player's own access bypass (Admin Panel) lifts the rules. A request with no
			// player behind it (the game's own server stories) is not a player's use.
			if ((long)identity == 0 || AccessBypass.IsOn((long)identity)) return true;
			var permissions = MpPlugin.App?.ServerSession?.SessionComponents.TryGet<PermissionsSessionComponent>();
			if (permissions == null) return true;
			if (entity.TryGet<CubeBlockComponent>() is { } block)
			{
				var grid = block.Grid.Entity;
				// Armor and other blocks without their own access settings follow their grid.
				if (entity.TryGet<BlockPermissionsComponent>() == null || grid.TryGet<OwnershipComponent>() == null) return MayUseGrid(permissions, identity, grid);
				var use = Singleton<DefinitionManager>.Instance.GetConfiguration<PermissionsConfiguration>().AdvancedUse;
				return permissions.CanUseBlockAction(entity, grid, identity, use);
			}
			if (entity.TryGet<CubeGridComponent>() != null) return MayUseGrid(permissions, identity, entity);
			// Another player's living character (their suit inventory); bodies and backpacks stay lootable.
			if (entity.TryGet<CharacterLifeComponent>() is { IsAlive: true } && TeamPermissions.OwnerOf(entity) is long owner && owner != 0
				&& owner != (long)identity && PlayerIdentities.IsHuman(owner)) return false;
			return true;
		}
		catch (Exception ex)
		{
			GameLog.Instance.WarnOnce("access-check-failed", $"access: could not check {entity.GetComposition()?.DebugNameShort ?? entity.ToString()} ({ex.GetType().Name}: {ex.Message}); allowed");
			return true;
		}
	}

	private static bool MayUseGrid(PermissionsSessionComponent permissions, IdentityId identity, Entity grid)
		=> grid.TryGet<OwnershipComponent>() == null || permissions.IsOwner(grid, identity) || permissions.CanPlaceBlock(grid, identity);

	private static IdentityId IdentityOf(ReplicationEventContext context)
		=> context.Session.SessionComponents.Get<IdentitiesSessionComponent>().GetIdentityFromClient(context.ClientId);

	private static void Note(ReplicationEventContext context, Entity entity)
	{
		Refused++;
		GameLog.Instance.Info($"access: refused identity {(long)IdentityOf(context)} on {entity.GetComposition()?.DebugNameShort ?? entity.ToString()} (no access)");
	}

	private static Exception Refuse(ReplicationEventContext context, Entity entity)
	{
		Note(context, entity);
		return context.Throw("Access denied");
	}

	// A refused block reads as "not there": the screen skips it instead of the request failing.
	private static void AsEntityPostfix(StreamedBaseSessionComponent __instance, ReplicationEventContext? replicationContext, ref Entity? __result)
	{
		if (!Enforcing || replicationContext == null || __result == null || __instance is StreamedRespawnScreenSessionComponent) return;
		if (MayUse(replicationContext, IdentityOf(replicationContext), __result)) return;
		Note(replicationContext, __result);
		__result = null;
	}

	private static void InteractedPrefix(ReplicationEventContext eventContext, Entity? interacted)
	{
		if (!Enforcing || interacted == null) return;
		if (!MayUse(eventContext, IdentityOf(eventContext), interacted)) throw Refuse(eventContext, interacted);
	}

	private static bool ControlPanelPrefix(Entity root, ReplicationEventContext? context)
	{
		if (!Enforcing || context == null || root == null || MayUse(context, IdentityOf(context), root)) return true;
		Note(context, root);
		return false;
	}

	private static bool ActivateRespawnPrefix(IdentityId identity, Entity respawnPoint, ref StringId __result)
	{
		if (!Enforcing || respawnPoint == null || MayUse(null, identity, respawnPoint)) return true;
		Refused++;
		__result = default;
		GameLog.Instance.Info($"access: identity {(long)identity} may not bind to the medbay on {respawnPoint.GetComposition()?.DebugNameShort}");
		return false;
	}
}
