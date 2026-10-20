using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Keen.Game2.Simulation.GameSystems.Ownership;
using Keen.Game2.Simulation.GameSystems.Relations;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.Weapons;
using Keen.Game2.Simulation.WorldObjects.CubeGrids.Weapons;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.Components;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// Player-to-player relations for turrets and sensors in multiplayer (<see cref="RelationTable"/>): with PvP on,
/// players whose factions have no treaty or are at war are hostile; own faction, allies and factions at peace are not.
/// With PvP off, no player is hostile to another.
/// </summary>
internal static class TurretRelations
{
    /// <summary>Set per multiplayer session from the host's "turrets attack players outside your faction" setting.</summary>
    public static volatile bool PlayersHostile;

    // Read on the simulation thread, replaced whole when membership or treaties change.
    private static volatile RelationTable _table = RelationTable.Empty;
    private static string _signature;

    public static RelationTable Table => _table;

    public static void Install(Harmony harmony)
        => harmony.Patch(AccessTools.Method(typeof(RelationsSessionComponent), nameof(RelationsSessionComponent.GetRelationFromOwners)),
            postfix: new HarmonyMethod(typeof(TurretRelations), nameof(RelationPostfix)));

    private static void RelationPostfix(RelationsSessionComponent __instance, IdentityId ownerA, IdentityId ownerB, ref RelationState __result)
    {
        long a = (long)ownerA, b = (long)ownerB;
        if (!PvpRules.Active || a == 0 || b == 0 || __instance._npcIdentity.IsNPC(ownerA) || __instance._npcIdentity.IsNPC(ownerB)) return;
        __result = RelationTable.MayFight(_table.Of(a, b), PlayersHostile) ? RelationState.Hostile : RelationState.NonHostile;
    }

    /// <summary>Publishes faction membership and treaties; returns true when they changed so turrets need re-evaluating.</summary>
    public static bool SetRelations(IEnumerable<Sync.OnlineFaction> factions, IEnumerable<Sync.FactionPact> pacts)
    {
        string signature = string.Join(";", factions.OrderBy(f => f.Id).Select(f => f.Id + "=" + string.Join(",", f.Members.OrderBy(m => m))))
            + "|" + string.Join(";", pacts.Select(p => p.A + "-" + p.B + "=" + p.State).OrderBy(x => x, StringComparer.Ordinal));
        if (signature == _signature) return false;
        _signature = signature;
        _table = RelationTable.Build(factions, pacts);
        return true;
    }

    /// <summary>Makes every turret grid rebuild its target list with the current relations (server thread).</summary>
    public static int Refresh(Session session)
    {
        int count = 0;
        foreach (Entity grid in session.GetEntitiesOfType<TargetingGridComponent>().ToList())
        {
            var targeting = grid.Get<TargetingGridComponent>();
            var owner = grid.TryGet<OwnershipComponent>()?.Owner ?? default;
            targeting.OnOwnerChanged(owner);
            count++;
        }
        // A turret keeps shooting its current target until it leaves range or sight; drop targets that are no longer hostile.
        foreach (Entity turret in session.GetEntitiesOfType<AutoTargetingComponent>().ToList())
        {
            var auto = turret.Get<AutoTargetingComponent>();
            Entity target = auto._target.Entity;
            if (target != null && auto._gridTargeting != null && !auto._gridTargeting.PotentialTargets.Contains(target)) auto.Untarget(setIdle: true);
        }
        return count;
    }

    private static volatile bool _refresh;

    /// <summary>Applies a PvP change to turrets, sensors and damage rules; turret target lists rebuild on the server thread.</summary>
    public static void SetPvp(bool on)
    {
        PvpRules.Pvp = PlayersHostile = on;
        _refresh = true;
    }

    public static bool TakeRefresh()
    {
        bool refresh = _refresh;
        _refresh = false;
        return refresh;
    }

    public static void Reset()
    {
        PlayersHostile = false;
        _table = RelationTable.Empty;
        _signature = null;
    }
}
