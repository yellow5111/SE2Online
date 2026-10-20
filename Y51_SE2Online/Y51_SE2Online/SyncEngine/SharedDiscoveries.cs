using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Y51_SE2Online.Core;
using Keen.Game2.Simulation.GameSystems.Discoveries;
using Keen.Game2.Simulation.GameSystems.Ownership;
using Keen.Game2.Simulation.GameSystems.Player;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Collections;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// SE2 keeps discoveries per identity: a station is on your map once your own character entered it, or once a sector
/// stage your identity reached revealed it. With research shared, colonization progress is the host's record for
/// everyone, but the stage change and the visit only raised the discovery of the identity that caused them, so other
/// players saw 100 % colonization and none of the stations. While research is shared, any player's discovery is every
/// player's: raised for the connected ones as it happens, and for the rest when they register.
/// </summary>
internal static class SharedDiscoveries
{
    [ThreadStatic] private static bool _mirroring;

    public static long Mirrored, CaughtUp;

    private static bool Active => HostWorld.ResearchShared;

    public static void Install(Harmony harmony)
        => harmony.Patch(AccessTools.Method(typeof(DiscoveriesServerSessionComponent), nameof(DiscoveriesServerSessionComponent.TryIncreaseDiscoveryState)),
            postfix: new HarmonyMethod(typeof(SharedDiscoveries), nameof(Postfix)));

    private static void Postfix(DiscoveriesServerSessionComponent __instance, IdentityId owner, IDiscoverableProvider provider, DiscoveryState state, bool __result)
    {
        if (!__result || _mirroring || !Active) return;
        _mirroring = true;
        try
        {
            // Offline identities are not registered for the discoverable (vanilla only registers players with a client); they catch up at join.
            foreach (IdentityId other in __instance._perPlayerData.GetIdentityIds().ToList())
                if ((long)other != (long)owner && __instance._identities.TryGetClientFromIdentity(other).HasValue && __instance.TryIncreaseDiscoveryState(other, provider, state))
                    Mirrored++;
        }
        finally
        {
            _mirroring = false;
        }
    }

    /// <summary>Raises each of the identity's discoveries to the furthest state any identity in the world has reached.</summary>
    public static int CatchUp(Session server, InProcessPerPlayerDataSessionComponent ppd, IdentityId identity)
    {
        if (server.SessionComponents.TryGet<DiscoveriesServerSessionComponent>() == null) return 0;
        var target = ppd.GetPerPlayerData<DiscoveriesPlayerDataServer>(identity);
        var others = new List<DiscoveriesPlayerDataServer>();
        foreach (var (_, records) in ppd._perPlayerData)
            others.AddRange(records.OfType<DiscoveriesPlayerDataServer>().Where(d => d != target));
        int raised = 0;
        foreach (Entity entity in target._discoveries.Keys.ToList())
        {
            DiscoveryState mine = target.GetDiscoveryState(entity), best = mine;
            foreach (var other in others)
                if (other._discoveryStates.TryGetValue(entity, out DiscoveryState s) && s > best) best = s;
            if (best > mine)
            {
                target.SetDiscoveryState(entity, best);
                raised++;
            }
        }
        CaughtUp += raised;
        return raised;
    }
}
