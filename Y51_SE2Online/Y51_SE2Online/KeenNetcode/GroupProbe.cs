#if !PULSAR
using System;
using System.Linq;
using Keen.Game2.Simulation.StreamedUI.Terminal.ControlPanel;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.Characters;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Mathematics;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>
/// Test rig (-y51online:selftest-group, player): 40 s after joining, opens the control panel of the nearest functional
/// block (as the terminal does) and asks for a new group of its grid's first two blocks, then logs what came back (F16).
/// </summary>
internal static class GroupProbe
{
	private static long _atMs;
	private static bool _done;

	public static void ClientTick(Session session)
	{
		if (_done || !MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "selftest-group")) return;
		long now = MpClock.NowMs();
		if (_atMs == 0) { _atMs = now + 40_000; return; }
		if (now < _atMs) return;
		_done = true;
		Run(session);
	}

	private static async void Run(Session session)
	{
		try
		{
			var me = session.GetEntitiesOfType<CharacterLifeComponent>().FirstOrDefault(c => c.Get<CharacterLifeComponent>().IsAlive && c.TryGet<Keen.Game2.Client.GameSystems.PlayerControl.PlayerControllerComponent>() != null)
				?? session.GetEntitiesOfType<CharacterLifeComponent>().FirstOrDefault();
			var at = me?.Data.GetWorldTransform().Position ?? default;
			var block = session.GetEntitiesOfType<FunctionalBlockComponent>().OrderBy(b => Vector3D.Distance(b.Data.GetWorldTransform().Position, at)).FirstOrDefault();
			if (block == null) { GameLog.Instance.Info("selftest: group: no functional block in this game"); return; }
			var panels = session.SessionComponents.Get<StreamedControlPanelSessionComponent>();
			var list = await panels.Attach(block);
			for (int i = 0; i < 50 && list.Blocks.Count < 2; i++) await Keen.VRage.Library.Threading.Task.Delay(TimeSpan.FromMilliseconds(200));
			var blocks = list.Blocks.Take(2).ToList();
			var grid = blocks.FirstOrDefault()?.GridModel ?? list.Grids.FirstOrDefault();
			int before = list.Groups.Count;
			GameLog.Instance.Info($"selftest: group: panel of {block} ({Vector3D.Distance(block.Data.GetWorldTransform().Position, at):0} m): {list.Blocks.Count} blocks, {list.Grids.Count} grids, {before} groups; creating a group of {blocks.Count} blocks on grid model {(grid == null ? "none" : "set")}");
			bool ok = await panels.RequestCreateNewGroup(blocks, grid!, list);
			await Keen.VRage.Library.Threading.Task.Delay(TimeSpan.FromSeconds(2));
			GameLog.Instance.Info($"selftest: group: {(ok ? "PASS" : "FAIL")} create returned {ok}; groups now {list.Groups.Count} (was {before})");
			// The group action into a timer / button panel slot (F16 part 2).
			var toolbar = list.Blocks.FirstOrDefault(b => b.Composite.ToString().Contains("Timer", StringComparison.OrdinalIgnoreCase) || b.Composite.ToString().Contains("ButtonPanel", StringComparison.OrdinalIgnoreCase));
			var group = list.Groups.LastOrDefault();
			if (toolbar == null || group == null) { GameLog.Instance.Info($"selftest: group: no toolbar block ({toolbar != null}) or group ({group != null}) for the slot test"); return; }
			var tools = Keen.VRage.DCS.ObjectBuilders.EntityObjectBuilderFunctions.TryGetDefinition<Keen.Game2.Simulation.WorldObjects.CubeBlocks.Tools.BlockToolsComponent, Keen.Game2.Simulation.WorldObjects.CubeBlocks.Tools.BlockToolsDefinition>(toolbar.Composite);
			var actions = panels.GetAvailableActions(tools, list, group);
			var action = actions.FirstOrDefault();
			if (action == null) { GameLog.Instance.Info("selftest: group: no group action for the slot test"); return; }
			GameLog.Instance.Info($"selftest: group: setting slot 0/1 of {toolbar.Composite} to {action} of '{group.DisplayName}' ({actions.Count} actions)");
			var set = panels.RequestSetTileGroup(toolbar.EntityId, 0, group.Group, list, new Keen.Game2.Simulation.GameSystems.Toolbars.ToolbarSelection(0, 1), Keen.VRage.Multiplayer.Utils.Detached.From<Keen.Game2.Simulation.GameSystems.Toolbars.Actions.IToolbarAction>(action));
			var waiting = set.GetAwaiter();
			for (int i = 0; i < 50 && !waiting.IsCompleted; i++) await Keen.VRage.Library.Threading.Task.Delay(TimeSpan.FromMilliseconds(200));
			string outcome = "FAIL no answer within 10 s";
			if (waiting.IsCompleted)
			{
				try { waiting.GetResult(); outcome = "PASS completed"; }
				catch (Exception ex) { outcome = "FAIL faulted: " + ex.GetBaseException().Message; }
			}
			GameLog.Instance.Info("selftest: group: slot request " + outcome);
		}
		catch (Exception ex) { GameLog.Instance.Info("selftest: group: FAIL " + ex.GetType().Name + ": " + ex.Message); }
	}
}
#endif
