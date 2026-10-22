#if !PULSAR
using System;
using System.Linq;
using Keen.Game2.Client.GameSystems.PlayerControl;
using Keen.Game2.Simulation.StreamedUI.Terminal;
using Keen.Game2.Simulation.StreamedUI.Terminal.ControlPanel;
using Keen.Game2.Simulation.StreamedUI.Terminal.Inventories;
using Keen.Game2.Simulation.WorldObjects.Characters;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Mathematics;
using Y51_SE2Online.Core;
using Y51_SE2Online.SyncEngine;

namespace Y51_SE2Online.Engine;

/// <summary>
/// Test rig only (-y51online:selftest-systems, a player's game, with -y51online:selftest-npc on both sides and
/// -y51online:selftest-npc-own on the server so the probe's blocks stay the player's): 90 s into play the player moves one
/// item from their own inventory into their own cargo container and switches their own trade terminal off and on again,
/// through the requests the inventory and control panel screens send. "selftest-systems: ..." lines; stateprobe "inv" and
/// "on" lines show what the server and every player then hold.
/// </summary>
internal static class SystemsProbe
{
	private static bool Requested => MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "selftest-systems");
	private static long _atMs;

	public static void Tick(Session session)
	{
		if (!Requested || _atMs < 0 || Me(session) == null) return;
		long now = MpClock.NowMs();
		if (_atMs == 0) { _atMs = now + 90_000; return; }
		if (now < _atMs) return;
		_atMs = -1;
		_ = Run(session);
	}

	private static Entity? Me(Session session) => session.SessionComponents.Get<ClientPlayersSessionComponent>().LocalPlayerController?.ControlledEntities
		.Select(c => (c.Controllable as Component)?.Entity).FirstOrDefault(e => e?.TryGet<CharacterLifeComponent>() != null);

	private static Entity? OwnBlock(Session session, Entity me, Func<Entity, bool> match)
	{
		long mine = TeamPermissions.OwnerOf(me);
		var at = me.Data.GetWorldTransform().Position;
		Entity? found = null;
		double best = double.MaxValue;
		foreach (var grid in session.GetEntitiesOfType<CubeGridComponent>())
		{
			if (TeamPermissions.OwnerOf(grid) != mine) continue;
			double d = Vector3D.Distance(grid.Data.GetWorldTransform().Position, at);
			if (d > 60 || d >= best) continue;
			Entity? block = null;
			grid.Get<CubeGridComponent>().VisitAllBlocksWithComponent((CubeBlockComponent b) => { if (block == null && match(b.Entity)) block = b.Entity; });
			if (block != null) { found = block; best = d; }
		}
		return found;
	}

	private static async System.Threading.Tasks.Task Run(Session session)
	{
		var log = GameLog.Instance;
		try
		{
			var me = Me(session)!;
			var inventories = session.SessionComponents.Get<StreamedInventorySessionComponent>();
			if (OwnBlock(session, me, e => e.GetComposition()?.DebugNameShort?.Contains("CargoContainer", StringComparison.Ordinal) == true) is { } cargo)
			{
				var info = new InteractionInfo { InteractorEntity = me, InteractedEntity = cargo };
				var theirs = await inventories.Attach(cargo, populateMainEntity: true, info);
				var mine = await inventories.Attach(me, populateMainEntity: true, info);
				var target = theirs.InventoryContainers.Where(c => c.EntityId == theirs.MainEntity).SelectMany(c => c.Inventories).FirstOrDefault()
					?? theirs.InventoryContainers.SelectMany(c => c.Inventories).FirstOrDefault();
				var item = mine.InventoryContainers.SelectMany(c => c.Inventories).SelectMany(v => v.Items).FirstOrDefault();
				if (target != null && item != null)
				{
					var (result, moved, left) = await inventories.TransferAsync(item, target, info, null, null);
					log.Info($"selftest-systems: moved {item.Definition} into own cargo {cargo}: {result}, moved {moved}, left {left}");
				}
				else log.Warn($"selftest-systems: nothing to move (item {item != null}, cargo inventory {target != null})");
				inventories.Detach(mine);
				inventories.Detach(theirs);
			}
			else log.Warn("selftest-systems: no own cargo container within 60 m");
			if ((OwnBlock(session, me, e => e.TryGet<PowerableBlockComponent>() != null && e.GetComposition()?.DebugNameShort?.Contains("Seat", StringComparison.Ordinal) != true)
				?? OwnBlock(session, me, e => e.TryGet<PowerableBlockComponent>() != null)) is { } powered)
			{
				var panels = session.SessionComponents.Get<StreamedControlPanelSessionComponent>();
				var list = await panels.Attach(powered);
				var ids = list.Blocks.Select(b => b.EntityId).ToList();
				// The block's own grid holds only this block: its detail page's on/off switch is the one the player flips.
				var details = await panels.Attach(new System.Collections.Generic.HashSet<Keen.VRage.Library.Utils.SubclassOf<Keen.Game2.Simulation.StreamedUI.Terminal.ControlPanel.BlockDetails.BlockDetailModel>> { typeof(Keen.Game2.Simulation.StreamedUI.Terminal.ControlPanel.BlockDetails.PowerableBlockDetailModel) }, ids, list);
				var switches = details.Values.SelectMany(d => d).OfType<Keen.Game2.Simulation.StreamedUI.Terminal.ControlPanel.BlockDetails.PowerableBlockDetailModel>().ToList();
				log.Info($"selftest-systems: control panel of {powered.GetComposition()?.DebugNameShort}: {ids.Count} blocks, detail pages {string.Join(", ", details.Values.SelectMany(d => d).Select(d => d.GetType().Name))}");
				var block = powered.Get<PowerableBlockComponent>();
				bool was = block.Enabled;
				foreach (var page in switches) page.Enabled = !was;
				log.Info($"selftest-systems: switched own {powered.GetComposition()?.DebugNameShort} {(was ? "off" : "on")} on its control panel page ({switches.Count} switch)");
				await Keen.VRage.Library.Threading.Task.Delay(TimeSpan.FromSeconds(10));
				log.Info($"selftest-systems: own {powered.GetComposition()?.DebugNameShort} is {(block.Enabled ? "on" : "off")} here 10 s later");
				foreach (var page in switches) page.Enabled = was;
				log.Info($"selftest-systems: switched it back {(was ? "on" : "off")}");
				await Keen.VRage.Library.Threading.Task.Delay(TimeSpan.FromSeconds(10));
				foreach (var detail in details.Values.SelectMany(d => d)) panels.Detach(detail);
				panels.Detach(list);
			}
			else log.Warn("selftest-systems: no own powered block within 60 m");
			log.Info("selftest-systems: done");
		}
		catch (Exception ex) { log.Error($"selftest-systems: FAIL {ex.GetType().Name}: {ex.Message.Split('\n')[0]}"); }
	}
}
#endif
