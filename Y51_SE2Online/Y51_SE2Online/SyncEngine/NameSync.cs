using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Keen.Game2.Client.UI.TerminalScreen.ControlPanel;
using Keen.Game2.Simulation.StreamedUI.Terminal.ControlPanel;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.VRage.DCS.Components;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// Names in the control panel for every viewer. SE2's server keeps one control panel model per player who has it
/// open, but its group handlers only ever updated the first viewer's model (once per viewer subscription), and a group
/// renamed through one model was pushed back to the old name by every other viewer's model. On the client, an open
/// panel copied block and grid names once, so someone else's rename never showed until the panel was reopened.
/// </summary>
internal static class NameSync
{
    private static readonly Type Panel = typeof(StreamedControlPanelSessionComponent);

    internal static void Install(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(ControlPanelGroupModelServer), "GroupOnPropertyChanged"), new HarmonyMethod(typeof(NameSync), nameof(GroupChangedPrefix)));
        harmony.Patch(AccessTools.Method(Panel, "OnGroupAdded"), new HarmonyMethod(typeof(NameSync), nameof(GroupAddedPrefix)));
        harmony.Patch(AccessTools.Method(Panel, "OnGroupRemoved"), new HarmonyMethod(typeof(NameSync), nameof(GroupRemovedPrefix)));
        harmony.Patch(AccessTools.Method(Panel, "OnBlockAddedToGroup"), new HarmonyMethod(typeof(NameSync), nameof(BlockAddedPrefix)));
        harmony.Patch(AccessTools.Method(Panel, "OnBlockRemovedFromGroup"), new HarmonyMethod(typeof(NameSync), nameof(BlockRemovedPrefix)));
    }

    // The model whose own name already matches made the change and tells the grid once; the others follow it.
    private static bool GroupChangedPrefix(ControlPanelGroupModelServer __instance)
    {
        if (__instance.DisplayName == __instance._group.DisplayName) return true;
        __instance.DisplayName = __instance._group.DisplayName;
        return false;
    }

    // The panel subscribes these handlers once per viewer, so one change calls them once per viewer: the first call
    // updates every viewer's model and the remaining calls of the same dispatch are skipped.
    // ponytail: duplicates are told apart by arguments within 50 ms; an identical second change inside that window is lost.
    [ThreadStatic] private static (object Key, int Left, long At) _repeats;

    private static bool Repeat(object key)
    {
        var last = _repeats;
        if (last.Left <= 0 || !Equals(last.Key, key) || Stopwatch.GetTimestamp() - last.At > Stopwatch.Frequency / 20) return false;
        _repeats.Left--;
        return true;
    }

    private static void ForEachViewer(StreamedControlPanelSessionComponent panel, Entity grid, object key, Action<ControlPanelListModel, ControlPanelGridModel> apply)
    {
        if (Repeat(key)) return;
        int viewers = 0;
        foreach (var (list, grids) in panel._models)
        {
            if (!grids.TryGetValue(grid, out var gridModel)) continue;
            viewers++;
            apply(list, gridModel);
        }
        _repeats = (key, viewers - 1, Stopwatch.GetTimestamp());
    }

    private static bool GroupAddedPrefix(StreamedControlPanelSessionComponent __instance, Entity grid, GridGroup group)
    {
        ForEachViewer(__instance, grid, ("group+", grid, group), (list, gridModel) =>
        {
            var model = StreamedControlPanelSessionComponent.CreateEmptyGroupModel(group, gridModel);
            if (group.ParentGroup == null) gridModel.RootGroups.Add(model);
            else if (StreamedControlPanelSessionComponent.TryGetGroupModel(gridModel, m => m.Group == group.ParentGroup) is { } parent) parent.SubGroups.Add(model);
            else return;
            list.Groups.Add(model);
        });
        return false;
    }

    private static bool GroupRemovedPrefix(StreamedControlPanelSessionComponent __instance, Entity grid, GridGroup group)
    {
        ForEachViewer(__instance, grid, ("group-", grid, group), (list, gridModel) => __instance.TryRemoveGroupModel(gridModel, list, group));
        return false;
    }

    private static bool BlockAddedPrefix(StreamedControlPanelSessionComponent __instance, Entity grid, Entity block, GridGroup group)
    {
        ForEachViewer(__instance, grid, ("block+", grid, block, group), (list, gridModel) =>
        {
            var blockModel = StreamedControlPanelSessionComponent.TryGetBlockModel(block, list);
            var groupModel = StreamedControlPanelSessionComponent.TryGetGroupModel(gridModel, m => m.Group == group);
            if (blockModel == null || groupModel == null) return;
            blockModel.GroupModel = groupModel;
            groupModel.BlockIds.Add(blockModel.EntityId);
        });
        return false;
    }

    private static bool BlockRemovedPrefix(StreamedControlPanelSessionComponent __instance, Entity grid, Entity block, GridGroup group)
    {
        ForEachViewer(__instance, grid, ("block-", grid, block, group), (list, gridModel) =>
        {
            var blockModel = StreamedControlPanelSessionComponent.TryGetBlockModel(block, list);
            var groupModel = StreamedControlPanelSessionComponent.TryGetGroupModel(gridModel, m => m.Group == group);
            if (blockModel == null || groupModel == null) return;
            blockModel.GroupModel = group.ParentGroup == null ? null : StreamedControlPanelSessionComponent.TryGetGroupModel(gridModel, m => m.Group == group.ParentGroup);
            groupModel.BlockIds.Remove(blockModel.EntityId);
        });
        return false;
    }

}
