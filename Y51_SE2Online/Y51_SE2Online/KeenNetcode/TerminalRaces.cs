using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;
using Keen.Game2.Client.UI.TerminalScreen.ControlPanel;
using Keen.Game2.Simulation.StreamedUI.Terminal.ControlPanel;
using Keen.Game2.Simulation.StreamedUI.Terminal.Inventories;
using Keen.VRage.Multiplayer.SecureIds;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>Two races in the game's terminal screens that crashed players, each installed on its own. An inventory
/// screen fills its list after an await, so an entity that streams in while the first fill waits is filled twice and the
/// second Dictionary.Add threw: the first inventory stays and the second is detached. A grid can leave the control panel
/// before the removal of its blocks is resolved, and the missing grid view threw: those blocks are left out.</summary>
internal static class TerminalRaces
{
	public static long Refills, GoneGridBlocks;

	private static readonly MethodInfo MapAdd = AccessTools.Method(typeof(Dictionary<EntityId, InventoryContainerModel>), "Add");
	private static FieldInfo _fillOwner;

	public static void Install(Harmony harmony)
	{
		Half("duplicate inventory", () => InstallFill(harmony));
		Half("removed grid blocks", () => harmony.Patch(AccessTools.Method(typeof(ControlPanelTreeViewModel), nameof(ControlPanelTreeViewModel.ResolveBlockViewModels))
			?? throw new MissingMethodException("ControlPanelTreeViewModel.ResolveBlockViewModels"),
			prefix: new HarmonyMethod(typeof(TerminalRaces), nameof(SkipGoneGrids))));
	}

	private static void Half(string name, Action install)
	{
		try { install(); }
		catch (Exception ex) { GameLog.Instance.Warn($"terminal races: '{name}' not installed for this game build ({ex.GetType().Name}: {ex.Message.Split('\n')[0]})"); }
	}

	// Checked before patching: a game build that moved or doubled the Add leaves the fill unpatched instead of guessing.
	private static void InstallFill(Harmony harmony)
	{
		var moveNext = AccessTools.AsyncMoveNext(AccessTools.Method(typeof(InventoryListModel), "FillInventories")
			?? throw new MissingMethodException("InventoryListModel.FillInventories")) ?? throw new MissingMethodException("FillInventories state machine");
		_fillOwner = AccessTools.Field(moveNext.DeclaringType, "<>4__this") ?? throw new MissingFieldException("FillInventories state machine owner");
		int adds = PatchProcessor.GetOriginalInstructions(moveNext).Count(i => i.Calls(MapAdd));
		if (adds != 1) throw new InvalidOperationException($"expected one inventory map Add in FillInventories, found {adds}");
		harmony.Patch(moveNext, transpiler: new HarmonyMethod(typeof(TerminalRaces), nameof(FillIl)));
	}

	private static IEnumerable<CodeInstruction> FillIl(IEnumerable<CodeInstruction> instructions)
	{
		foreach (var instruction in instructions)
		{
			if (!instruction.Calls(MapAdd)) { yield return instruction; continue; }
			yield return new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(instruction);
			yield return new CodeInstruction(OpCodes.Ldfld, _fillOwner);
			yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(TerminalRaces), nameof(KeepFirst)));
		}
	}

	private static void KeepFirst(Dictionary<EntityId, InventoryContainerModel> map, EntityId entity, InventoryContainerModel container, InventoryListModel owner)
	{
		if (TerminalRaceRule.Spare(map, entity, container) is not { } spare) return;
		Interlocked.Increment(ref Refills);
		GameLog.Instance.WarnOnce("terminal-refill", $"terminal races: an inventory screen filled entity {entity} twice while it opened; the first inventory stays, the second is let go (further ones not logged)");
		try { owner._inventories.Detach(spare, owner.StreamedModel); }
		catch (Exception ex) { GameLog.Instance.WarnOnce("terminal-refill-detach", $"terminal races: letting the second inventory go failed ({ex.GetType().Name}: {ex.Message.Split('\n')[0]})"); }
	}

	private static void SkipGoneGrids(ControlPanelTreeViewModel __instance, ref IEnumerable<ControlPanelEntityModel> blockModels)
	{
		var grids = __instance._gridModelToViewModel;
		blockModels = TerminalRaceRule.Shown(blockModels, b => b.GridModel != null && grids.ContainsKey(b.GridModel), out int gone);
		if (gone == 0) return;
		Interlocked.Add(ref GoneGridBlocks, gone);
		GameLog.Instance.WarnOnce("terminal-gone-grid", $"terminal races: the control panel was asked to remove {gone} block(s) of a grid it no longer shows; those are skipped (further ones not logged)");
	}
}
