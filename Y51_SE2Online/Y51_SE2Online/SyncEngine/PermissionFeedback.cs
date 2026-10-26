using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Keen.Game2.Client.GameSystems.Interaction;
using Keen.Game2.Client.GameSystems.Render;
using Keen.Game2.Client.UI.InGame;
using Keen.Game2.Client.UI.Shared.SystemNotification;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Library.Utils;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// Trying to use a block the host would refuse turns the game's own hover highlight red (same outline, section and
/// strobe as the yellow one, for as long as the player keeps aiming at it) and shows a "Permission Denied" toast.
/// </summary>
internal static class PermissionFeedback
{
    private static readonly MethodInfo Clone = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly ConditionalWeakTable<UseActionDefinition, UseActionDefinition> RedDefinitions = new();
    private static readonly ConditionalWeakTable<UseAction, object> Reddened = new();
    private static IObservableDisposable _toast;

    internal static void Install(Harmony harmony)
    {
        var prefix = new HarmonyMethod(typeof(PermissionFeedback), nameof(InteractPrefix));
        harmony.Patch(AccessTools.Method(typeof(UseAction), "InteractPrimary"), prefix);
        harmony.Patch(AccessTools.Method(typeof(UseAction), "InteractSecondary"), prefix);
        var drill = new HarmonyMethod(typeof(PermissionFeedback), nameof(DrillPostfix));
        harmony.Patch(AccessTools.Method(typeof(Keen.Game2.Client.WorldObjects.Tools.DrillToolControllableComponent), nameof(Keen.Game2.Client.WorldObjects.Tools.DrillToolControllableComponent.RequestActivatePrimaryAction)), postfix: drill);
        harmony.Patch(AccessTools.Method(typeof(Keen.Game2.Client.WorldObjects.Tools.DrillToolControllableComponent), nameof(Keen.Game2.Client.WorldObjects.Tools.DrillToolControllableComponent.RequestActivateSecondaryAction)), postfix: drill);
        try
        {
            harmony.Patch(AccessTools.Method(typeof(Keen.Game2.Simulation.StreamedUI.Terminal.Inventories.InventoryListModel), nameof(Keen.Game2.Simulation.StreamedUI.Terminal.Inventories.InventoryListModel.CreateAsync)),
                new HarmonyMethod(typeof(PermissionFeedback), nameof(InventoryListPrefix)));
        }
        catch (Exception ex) { GameLog.Instance.Warn("permissions: the denied inventory notice is not installed: " + ex.Message); }
    }

    // SE2 sends its own "no voxel edits in a safe zone" notice from the server with MoveTo.Client, which never reaches
    // a joiner; the host still refuses the edit, so this only says why the drill does nothing.
    private static void DrillPostfix(Keen.Game2.Client.WorldObjects.Tools.DrillToolControllableComponent __instance, bool enabled)
    {
        try
        {
            if (!enabled || !AdminAccess.Joined || __instance.Entity == null || MpPlugin.App?.ClientSession is not { } session) return;
            var at = __instance.Entity.Data.GetWorldTransform();
            var tip = at.Position + (Vector3D)at.Orientation.GetForward() * 2;
            foreach (var zone in session.GetEntitiesOfType<Keen.Game2.Simulation.WorldObjects.SafeZones.SafeZoneComponent>())
            {
                if (!zone.Get<Keen.Game2.Simulation.WorldObjects.SafeZones.SafeZoneComponent>().IsInSafeZone(in tip)) continue;
                Notify("Safe Zone", "Voxel modification in safe zone is not allowed.");
                GameLog.Instance.Info("permissions: drilling inside a safe zone; told the player the host refuses it");
                return;
            }
        }
        catch (Exception ex) { GameLog.Instance.WarnOnce("safezone-feedback", "permissions: safe zone drill feedback failed: " + ex.Message); }
    }

    /// <summary>The local player's identity in a hosted or joined world, else 0.</summary>
    internal static long LocalActor => AdminAccess.Hosting ? PlayerIdentities.HotfixIdentity : AdminAccess.Joined ? AdminAccess.Client.IdentityId : 0;

    // Feedback only, apart from the uses the game carries out locally before the server answers: on/off flips the block
    // and pulling from an item slot plays the pickup sound. A joined player's game skips those when the server would refuse
    // them (the host's own uses are never refused there). A prediction that is unsure (null) lets the use go to the server.
    private static bool InteractPrefix(UseAction __instance, bool value)
    {
        try
        {
            long actor = LocalActor;
            if (!value || actor == 0 || __instance.Entity == null || AccessBypass.IsOn(actor) || WorldSocial.Access(__instance.Entity, actor) != false) return true;
            Deny(__instance);
            return !(AdminAccess.Joined && __instance is UsePowerableAction or UseItemSlotAction);
        }
        catch (Exception ex) { GameLog.Instance.WarnOnce("permission-feedback", "permissions: denied feedback failed: " + ex.Message); }
        return true;
    }

    // Opening a container's terminal lists every inventory on its ship; the server leaves out the ones the player may not
    // use (ServerAccess.InventoryEntryPrefix), so the player is told which, once per list.
    private static void InventoryListPrefix(Entity targetEntity)
    {
        try
        {
            long actor = LocalActor;
            if (actor == 0 || AccessBypass.IsOn(actor) || WorldSocial.GridOf(targetEntity) is not { } grid) return;
            var refused = new System.Collections.Generic.List<string>();
            foreach (var ship in grid.Get<Keen.Game2.Simulation.WorldObjects.CubeGrids.CubeGridComponent>().GetAllGridsInShip())
                // Blocks with several inventories (input and output) have no default InventoryComponent tag: Entity.TryGet of it
                // asserts ("No default tag found"), so blocks are visited and their inventories enumerated as the game does.
                ship.TryGet<Keen.Game2.Simulation.WorldObjects.CubeGrids.CubeGridComponent>()?.VisitAllBlocksWithComponent<Keen.Game2.Simulation.WorldObjects.CubeBlocks.CubeBlockComponent>(block =>
                {
                    if (refused.Count >= 100) return;
                    foreach (var component in block.Entity.Components)
                    {
                        if (component is not Keen.Game2.Simulation.WorldObjects.Items.InventoryComponent) continue;
                        if (WorldSocial.Access(block.Entity, actor) == false) refused.Add(BlockName(block.Entity));
                        break;
                    }
                }, includeSubgrids: false);
            if (InteractionRules.DeniedNotice(refused) is { } text) Notify("Inventory", text);
        }
        catch (Exception ex) { GameLog.Instance.WarnOnce("inventory-denied-feedback", "permissions: denied inventory feedback failed: " + ex.Message); }
    }

    private static string BlockName(Entity block)
    {
        try { return block.TryGet<Keen.Game2.Simulation.WorldObjects.CubeBlocks.FunctionalBlockComponent>()?.DisplayName.EvaluateText()
            ?? block.TryGet<Keen.Game2.Simulation.WorldObjects.CubeBlocks.CubeBlockComponent>()?.Definition.LocalizableBlockTypeDisplayName.EvaluateText(); }
        catch (Exception) { return null; }
    }

    internal static void Deny(UseAction action)
    {
        RedHover(action);
        Notify("Permission Denied", "You don't have access to this block!");
    }

    /// <summary>Shows the notice unless one of these is still on screen; true when it showed.</summary>
    internal static bool Notify(string title, string text)
    {
        if (_toast != null) return false;
        var ui = MpPlugin.App?.ClientSession?.SessionComponents.TryGet<SessionInGameUISessionComponent>();
        _toast = ui?.DisplayNotification(new NotificationViewModel(title, text, TimeSpan.FromSeconds(3)));
        if (_toast != null) _toast.OnDisposed += _ => _toast = null;
        return _toast != null;
    }

    // The interactor owns the hover handle and disposes it when the player looks away or the block leaves the scene,
    // so the red version lives exactly as long as the yellow one would have.
    private static void RedHover(UseAction action)
    {
        var interactor = action.Interactor?.TryGet<InteractorEntityComponent>();
        var native = action._definition;
        if (interactor == null || interactor._useAction != action || native?.HighlightEffect == null || Reddened.TryGetValue(action, out _)) return;
        var red = RedDefinitions.GetValue(native, Redden);
        interactor._hoverAction?.Dispose();
        interactor._hoverAction = null;
        Reddened.Add(action, null);
        action._definition = red;
        try { interactor._hoverAction = action.OnHover(); }
        finally { action._definition = native; }
    }

    private static UseActionDefinition Redden(UseActionDefinition native)
    {
        var effect = (HighlightEffectDefinition)Clone.Invoke(native.HighlightEffect, null);
        effect.Color = new ColorSRGB((byte)255, (byte)24, (byte)24, native.HighlightEffect.Color.A);
        var definition = (UseActionDefinition)Clone.Invoke(native, null);
        definition.HighlightEffect = effect;
        return definition;
    }
}
