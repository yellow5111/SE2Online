using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Keen.Game2.Client.UI.TerminalScreen.ControlPanel;
using Keen.Game2.Simulation.StreamedUI.Terminal.ControlPanel;
using Keen.Game2.Simulation.WorldObjects.CubeGrids.ResourceDistribution.Conveyors;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// Game code that assumes a related entity exists, which in multiplayer can still be in flight or already gone. Each
/// of these crashed players' games outright.
/// </summary>
internal static class ReplicationRaceGuards
{
	private static bool _panelLogged, _conveyorLogged;

	internal static void Install(Harmony harmony)
	{
		harmony.Patch(AccessTools.Method(typeof(ControlPanelViewModel), "RemoveBlockVisualModels"), prefix: new HarmonyMethod(typeof(ReplicationRaceGuards), nameof(RemoveBlocksPrefix)));
		harmony.Patch(AccessTools.Method(typeof(ConveyorSystemComponent), "UpdateGroupConnections"), finalizer: new HarmonyMethod(typeof(ReplicationRaceGuards), nameof(ConveyorFinalizer)));
	}

	// A block-removed event for a grid whose view model is already gone (mass destruction): the game asserts, then
	// dereferences the missing grid inside an async void handler.
	private static void RemoveBlocksPrefix(ControlPanelViewModel __instance, ref IEnumerable<ControlPanelEntityModel> removedBlockModels)
	{
		var grids = __instance._gridModelToViewModel;
		var kept = removedBlockModels.Where(m => m.GridModel != null && grids.ContainsKey(m.GridModel)).ToList();
		if (!_panelLogged && kept.Count != removedBlockModels.Count())
		{
			_panelLogged = true;
			GameLog.Instance.Warn("control panel: dropped removed blocks of a grid that is no longer listed");
		}
		removedBlockModels = kept;
	}

	// A conveyor link to a partner grid whose conveyor groups are not built yet (connector events arriving before the
	// partner): the traversal indexes a missing node. The connections stay dirty and are recomputed on the next change.
	private static Exception ConveyorFinalizer(Exception __exception, ConveyorSystemComponent __instance)
	{
		if (__exception is not KeyNotFoundException) return __exception;
		__instance.MarkConnectionsDirty();
		if (!_conveyorLogged)
		{
			_conveyorLogged = true;
			GameLog.Instance.Warn("conveyors: a conveyor link points at a grid that is not built yet; its connections are recomputed later");
		}
		return null;
	}
}
