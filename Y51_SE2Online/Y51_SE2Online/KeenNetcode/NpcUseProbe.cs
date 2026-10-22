#if !PULSAR
using System;
using System.Collections.Generic;
using System.Linq;
using Keen.Game2.Client.GameSystems.BlockPlacement.BlockPlacer;
using Keen.Game2.Client.GameSystems.PlayerControl;
using Keen.Game2.Simulation.GameSystems.BlockPlacement;
using Keen.Game2.Simulation.GameSystems.Economy;
using Keen.Game2.Simulation.GameSystems.Ownership;
using Keen.Game2.Simulation.GameSystems.Player;
using Keen.Game2.Simulation.StreamedUI.Terminal;
using Keen.Game2.Simulation.StreamedUI.Terminal.Inventories;
using Keen.Game2.Simulation.WorldObjects.Characters;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.Pilotable;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.Game2.Simulation.WorldObjects.Items;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.Core.Game.Definitions;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.Components;
using Keen.VRage.DCS.ObjectBuilders;
using Keen.VRage.Library.Definitions;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Library.Utils;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;
using Y51_SE2Online.SyncEngine;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>
/// Test rig only (`-y51online:selftest-npc`): what a player does at an NPC station. The server makes every player
/// personally creative; each client (the co-op host's own game too, with `-y51online:selftest-npc-host`) places a cargo
/// container, a control seat and a trade terminal, the server hands those three to the NPC identity, and the client then
/// opens the container's inventory, sits in the seat and sells one item at the terminal. "selftest-npc: ..." lines.
/// </summary>
internal static class NpcUseProbe
{
	private static bool Requested => MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "selftest-npc");
	private static bool Foreign => MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "selftest-npc-foreign");
	private const long ForeignOwner = 900000002;
	// -own: the player keeps the three blocks (a player's own trade terminal).
	private static bool Own => MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "selftest-npc-own");
	private static long Expected => Foreign ? ForeignOwner : PlayerIdentities.NpcIdentity;
	private static bool OnHost => MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "selftest-npc-host");

	private static readonly Guid Cargo = new("51e6d0e7-6f67-4d22-8204-7b45453dbd1b"), Seat = new("a029b001-36b5-4508-b05b-0223662d91a5"), Trade = new("82218f59-3722-4804-a26c-014220246084");

	private static readonly HashSet<long> _creative = new();
	private static readonly HashSet<Entity> _handed = new();
	private static long _atMs;

	public static void Tick(Session session)
	{
		if (!Requested) return;
		try
		{
			if (ReferenceEquals(session, MpPlugin.App?.ServerSession)) Server(session);
			else if (ReferenceEquals(session, MpPlugin.App?.ClientSession)) Client(session);
		}
		catch (Exception ex) { GameLog.Instance.WarnOnce("npc-probe", "selftest-npc: tick failed: " + ex); }
	}

	private static void Server(Session session)
	{
		var mode = session.SessionComponents.TryGet<GameModeSessionComponent>();
		foreach (var character in session.GetEntitiesOfType<CharacterLifeComponent>())
		{
			long owner = TeamPermissions.OwnerOf(character);
			if (mode == null || !PlayerIdentities.IsHuman(owner) || !_creative.Add(owner)) continue;
			mode.CreativeChanged(new IdentityId(owner), true);
			GameLog.Instance.Info($"selftest-npc: identity {owner} is now personally creative");
		}
		var ownership = session.SessionComponents.TryGet<OwnershipSessionComponent>();
		foreach (var grid in session.GetEntitiesOfType<CubeGridComponent>())
		{
			if (Own || _handed.Contains(grid) || !PlayerIdentities.IsHuman(TeamPermissions.OwnerOf(grid)) || Kind(grid) is not { } kind) continue;
			_handed.Add(grid);
			// -foreign: an absent player's private grid instead, which the access rules must refuse without crashing anyone.
			bool done = Foreign ? ownership?.TryTransferOwnership(grid, new IdentityId(ForeignOwner)) ?? false : ownership?.TryTransferToNPC(grid) ?? false;
			GameLog.Instance.Info($"selftest-npc: handed the {kind} grid to {(Foreign ? "another player" : "the NPC identity")}: {done}, owner now {TeamPermissions.OwnerOf(grid)}");
		}
	}

	private static string? Kind(Entity grid)
	{
		int blocks = 0;
		string? kind = null;
		grid.Get<CubeGridComponent>().VisitAllBlocksWithComponent((CubeBlockComponent b) =>
		{
			blocks++;
			if (b.Entity.TryGet<TraderComponent>() != null || b.Entity.GetComposition()?.DebugNameShort?.Contains("TradeTerminal", StringComparison.Ordinal) == true) kind = "trade terminal";
			else if (b.Entity.FirstOrDefault<SeatComponent>() != null) kind = "seat";
			else if (b.Entity.GetComposition()?.DebugNameShort?.Contains("CargoContainer", StringComparison.Ordinal) == true) kind = "cargo";
		});
		return blocks == 1 ? kind : null;
	}

	private static Entity? Me(Session session) => session.SessionComponents.Get<ClientPlayersSessionComponent>().LocalPlayerController?.ControlledEntities
		.Select(c => (c.Controllable as Component)?.Entity).FirstOrDefault(e => e?.TryGet<CharacterLifeComponent>() != null);

	private static void Client(Session session)
	{
		bool host = MpPlugin.App?.ServerSession != null;
		if (_atMs < 0 || (host && !OnHost) || MpPlugin.App!._isTransitioning.IsSet || Me(session) == null) return;
		long now = MpClock.NowMs();
		if (_atMs == 0) { _atMs = now + (host ? 45000 : 25000); return; }
		if (now < _atMs) return;
		_atMs = -1;
		_ = Run(session, host ? "host" : "player");
	}

	private static async System.Threading.Tasks.Task Run(Session session, string who)
	{
		var log = GameLog.Instance;
		try
		{
			var me = Me(session)!;
			var placerDef = Singleton<DefinitionManager>.Instance.GetDefinitionsOfType<BlockPlacerEntityComponentDefinition>().First();
			var at = me.Data.GetWorldTransform();
			var placed = new Dictionary<string, Entity?>();
			int i = 0;
			foreach (var (name, prefab) in new[] { ("cargo", Cargo), ("seat", Seat), ("trade terminal", Trade) })
			{
				Singleton<DefinitionManager>.Instance.TryGetDefinition(prefab, out PrefabDefinition? block);
				var grid = placerDef.DefaultPrefabServerGrid.Get();
				var ob = block!.Get();
				grid.OB<HierarchyComponent, HierarchyComponentObjectBuilder>().AddChild(ob);
				var bundle = new BlockPlacerEntityBundle(new EntityBundle(), ob.Definition<CubeBlockComponent, CubeBlockDefinition>().BoundingBox);
				bundle.Add(grid, null, isRoot: true);
				bundle.Add(ob, null);
				var spot = new WorldTransform(at.Position + at.Orientation.GetForward() * 10 + at.Orientation.GetRight() * (i++ - 1) * 6 + at.Orientation.GetUp() * 1, at.Orientation);
				var result = await BlockPlacerNetworkStories.PlaceStandalone(session, new BlockPlacerNetworkStories.NewGridArgs { Transform = spot, Static = true }, bundle.Bundle.CloneBundle(),
					Vector3I.Zero, Vector3I.Zero, new MatrixI(IntegerOrientation.Identity), null, default).Task;
				var target = result?.Grid.Value;
				placed[name] = target;
				log.Info($"selftest-npc: {who} placed the {name}: {(target == null ? "refused" : target.TryGet<CubeGridComponent>() != null ? "a grid" : "a projection (not creative)")}");
			}
			for (int wait = 0; wait < 30 && !Own && placed.Values.Any(g => g != null && TeamPermissions.OwnerOf(g) != Expected); wait++)
				await Keen.VRage.Library.Threading.Task.Delay(TimeSpan.FromMilliseconds(500));
			log.Info($"selftest-npc: {who} sees owners {string.Join(", ", placed.Select(p => $"{p.Key}={(p.Value == null ? "-" : TeamPermissions.OwnerOf(p.Value).ToString())}"))}");
			Entity? BlockOf(Entity? grid)
			{
				Entity? found = null;
				grid?.TryGet<CubeGridComponent>()?.VisitAllBlocksWithComponent((CubeBlockComponent b) => found ??= b.Entity);
				return found;
			}
			var inventories = session.SessionComponents.Get<StreamedInventorySessionComponent>();
			if (BlockOf(placed["cargo"]) is { } cargo)
			{
				try
				{
					var info = new InteractionInfo { InteractorEntity = me, InteractedEntity = cargo };
					var list = await inventories.Attach(cargo, populateMainEntity: true, info);
					bool listed = list.InventoryContainers.Any(c => c.EntityId == list.MainEntity) || list.InventoryContainers.Count > 1;
					log.Info($"selftest-npc: {(listed ? "PASS" : "FAIL")} {who}: NPC loot container opened, {list.InventoryContainers.Count} inventories listed");
					inventories.Detach(list);
				}
				catch (Exception ex) { log.Error($"selftest-npc: FAIL {who}: NPC loot container: {ex.GetType().Name}: {ex.Message.Split('\n')[0]}"); }
			}
			if (BlockOf(placed["seat"])?.FirstOrDefault<SeatComponent>() is { } seat)
			{
				try
				{
					bool sat = await seat.TrySetPilotAsync(me);
					log.Info($"selftest-npc: {(sat ? "PASS" : "FAIL")} {who}: {(sat ? "sat in" : "refused")} the NPC seat");
					if (sat) { await Keen.VRage.Library.Threading.Task.Delay(TimeSpan.FromSeconds(2)); await seat.TryClearPilotAsync(); }
				}
				catch (Exception ex) { log.Error($"selftest-npc: FAIL {who}: NPC seat: {ex.GetType().Name}: {ex.Message.Split('\n')[0]}"); }
			}
			if (BlockOf(placed["trade terminal"]) is { } terminal)
			{
				if (MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "selftest-npc-unawaited")) OpenLikeTradeScreen(session, me, terminal, who);
				try
				{
					var model = await TraderComponent.StartInteraction(terminal).Task;
					log.Info($"selftest-npc: {who}: trade terminal interaction started, model {(model == null ? "null" : "received")}");
					var info = new InteractionInfo { InteractorEntity = me, InteractedEntity = terminal };
					var theirs = await inventories.Attach(terminal, populateMainEntity: true, info);
					var mine = await inventories.Attach(me, populateMainEntity: true, info);
					var target = theirs.InventoryContainers.SelectMany(c => c.Inventories).FirstOrDefault();
					var item = mine.InventoryContainers.SelectMany(c => c.Inventories).SelectMany(v => v.Items).FirstOrDefault();
					log.Info($"selftest-npc: {who}: trader inventories {theirs.InventoryContainers.Count}, own item {item?.Definition?.ToString() ?? "none"}");
					if (target != null && item != null)
					{
						var traded = await TraderComponent.TryTradeItem(item, target, info, 1, TradeTransferDirection.PlayerToTrader).Task;
						log.Info($"selftest-npc: PASS {who}: sold one item at the NPC trade terminal: successful {traded.Successful}, balance {traded.BalanceAfterTransaction}");
					}
					else log.Warn($"selftest-npc: {who}: nothing to sell or no trader inventory listed");
					inventories.Detach(mine);
					inventories.Detach(theirs);
					await TraderComponent.EndInteraction(terminal).Task;

				}
				catch (Exception ex) { log.Error($"selftest-npc: FAIL {who}: NPC trade terminal: {ex.GetType().Name}: {ex.Message.Split('\n')[0]}"); }
			}
			log.Info($"selftest-npc: {who} done");
		}
		catch (Exception ex) { log.Error($"selftest-npc: FAIL {who}: {ex.GetType().Name}: {ex.Message.Split('\n')[0]}"); }
	}

	// The trade screen's handlers are async void methods built on SE2's own AsyncVoidMethodBuilder, which hands a fault to
	// Parallel.ExceptionHandler (the crash handler); this does the same with what such a handler awaits.
	private static async void OpenLikeTradeScreen(Session session, Entity me, Entity terminal, string who)
	{
		GameLog.Instance.Info($"selftest-npc: {who}: opening the NPC trade terminal the way the trade screen does (fault to the game's handler)");
		try
		{
			var inventories = session.SessionComponents.Get<StreamedInventorySessionComponent>();
			var list = await inventories.Attach(terminal, populateMainEntity: true, new InteractionInfo { InteractorEntity = me, InteractedEntity = terminal });
			GameLog.Instance.Info($"selftest-npc: {who}: trade-screen open listed {list.InventoryContainers.Count} inventories");
			inventories.Detach(list);
		}
		catch (Exception ex)
		{
			Keen.VRage.Library.Threading.Parallel.ExceptionHandler.HandleException(ex);
			GameLog.Instance.Info($"selftest-npc: {who}: the game's handler took the trade-screen fault and the game goes on");
		}
	}
}
#endif
