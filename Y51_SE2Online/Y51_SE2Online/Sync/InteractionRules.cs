using System;
using System.Collections.Generic;
using System.Linq;

namespace Y51_SE2Online.Sync;

/// <summary>
/// The block interactions SE2 4.0 runs on the server for any player: the validators its attributes name
/// ([Server("ValidateInteractedEntities")], [SyncFromClients(...)], [Server("ValidateUseBasic")]) are never called, and
/// stories that move with MoveTo.Server(null) have none. The server holds each to the block's use access (owner, grid
/// sharing, faction, block lists), as SE2 Online's terminal and inventory checks do.
/// </summary>
public static class InteractionRules
{
    private const string Blocks = "Keen_Game2_Simulation_WorldObjects_CubeBlocks_";

    // A player's signal by its replay key (OwnSignal.Name), and whether the player's game already set the value itself
    // (on/off, which it flips before sending: a refusal sends the server's value back). The game's own client sends only
    // the door request and on/off; the rest are values only a modified client writes, refused without an answer.
    private static readonly Dictionary<string, bool> Signals = new (string Key, bool Predicted)[]
    {
        (Blocks + "Doors_DoorServerComponent_RequestDoorStateToggleSignal", false),
        (Blocks + "PowerableBlockComponent_EnabledProperty", true),
        (Blocks + "MechanicalBlocks_MagneticBlockComponent_RequestLockSignal", false),
        (Blocks + "MechanicalBlocks_MagneticBlockComponent_RequestUnlockSignal", false),
        (Blocks + "MechanicalBlocks_MagneticBlockComponent_AutoLockProperty", false),
        ("Keen_Game2_Simulation_WorldObjects_Movement_IParkingBrake_OnParkingControlEnabledProperty", false),
        (Blocks + "FunctionalBlockComponent_FunctionalProperty", false),
        (Blocks + "FunctionalBlockComponent_WorkingProperty", false),
        ("Keen_Game2_Simulation_WorldObjects_Shared_Visuals_Lights_LightComponent_LightColorProperty", false),
        ("Keen_Game2_Simulation_WorldObjects_Shared_Visuals_Lights_LightComponent_LightBrightnessProperty", false),
        (Blocks + "MechanicalBlocks_MechanicalBlockCounterpartAlignmentComponent_MaxAlignmentSpeedProperty", false),
        (Blocks + "Detectors_SensorFieldDrawerControllerComponent_DrawFieldProperty", false),
    }.ToDictionary(s => "Replay_" + s.Key + "_Trampoline", s => s.Predicted, StringComparer.Ordinal);

    /// <summary>A player's signal (its full replay key) that uses the block it is raised on: doors, on/off, landing gear, lights.</summary>
    public static bool IsBlockUse(string signal, out bool predicted) => Signals.TryGetValue(signal ?? "", out predicted);

    /// <summary>
    /// A player's story that uses a block: the state machine field naming the block ("&lt;&gt;4__this" for the component the
    /// story runs on) and the permission key the use also needs (null for none). Null for every other story.
    /// </summary>
    public static (string Field, string Key)? StoryUse(string type, string story) => (type, story) switch
    {
        // Pulling an item (a bottle, a battery) out of a block's item slot.
        ("InventorySlotsServerComponent", "Pull") => ("interactedEntity", "use.inventory"),
        // Filling or emptying a bottle at a tank's slot.
        ("ResourceContainerInventoryComponent", "TransferBest") => ("resourceContainer", "use.inventory"),
        // Medical room healing, survival kit, tank refills of the suit.
        ("InteractionStatModifierComponent", "RequestActivate") => ("<>4__this", null),
        // Clearing a whole block toolbar (its [Server("ValidateAdminAccess")] is never called either).
        ("BlockToolbarServerComponent", "RequestClearToolbar") => ("toolbarBlock", "use.toolbar"),
        _ => null,
    };

    /// <summary>
    /// A player's story that reads or edits a block toolbar the server names by id, none of which SE2 4.0 checks: the field
    /// naming the toolbar (an EntityId, or the toolbar model whose selection the player writes). Null for every other story.
    /// </summary>
    public static string ToolbarStory(string type, string story) => (type, story) switch
    {
        // A cockpit's or button panel's toolbar, by the id RequestToolbarIdFromEntity handed out.
        ("BlockToolbarReplicationServerSessionComponent", "RequestToolbarFromId") => "id",
        // The control panel's toolbar page: reading it, setting tiles, and the tile's own panel and group.
        ("StreamedControlPanelSessionComponent", "RequestToolbar" or "RequestSetTile" or "RequestSetTileKind" or "RequestSetTileGroup"
            or "TryGetTileControlPanelModel" or "TryGetTileGroupPath") => "toolbarId",
        // A write to a replicated model ([SyncFromClients] Selection and ExplicitSelection on a toolbar model).
        ("ObservableObjectReplicationClientSessionComponent", "SyncMemberChanged") => "replicatedObject",
        _ => null,
    };

    /// <summary>The toast for inventories the server leaves out of a player's list: the first one's name, and how many more.</summary>
    public static string DeniedNotice(IReadOnlyList<string> names)
        => names == null || names.Count == 0 ? null
            : "Access denied: " + (string.IsNullOrWhiteSpace(names[0]) ? "a container" : names[0].Trim()) + (names.Count > 1 ? $" and {names.Count - 1} more" : "");
}
