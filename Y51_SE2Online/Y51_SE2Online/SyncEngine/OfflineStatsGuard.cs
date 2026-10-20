using HarmonyLib;
using Keen.Game2.Simulation.WorldObjects.Characters;
using Keen.VRage.DCS.Components;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// Host side of <see cref="OfflineStats"/>. SE2 2.4 drains suit energy and oxygen in
/// CharacterLifeSupportComponent.ConsumeLifeSupport (which also applies the suffocation / no-energy damage and the
/// low-health regen) and suit energy / hydrogen in JetpackFuelComponent.ConsumeJetpackFuel; both are server-only
/// per-frame jobs. Skipping them for an offline player's character leaves every value where it was.
/// </summary>
internal static class OfflineStatsGuard
{
    public static void Install(Harmony harmony)
    {
        var skip = new HarmonyMethod(typeof(OfflineStatsGuard), nameof(SkipPrefix));
        harmony.Patch(AccessTools.Method(typeof(CharacterLifeSupportComponent), "ConsumeLifeSupport"), prefix: skip);
        harmony.Patch(AccessTools.Method(typeof(JetpackFuelComponent), "ConsumeJetpackFuel"), prefix: skip);
    }

    private static bool SkipPrefix(Component __instance)
    {
        if (!PvpRules.Active || !OfflineStats.Frozen(TeamPermissions.OwnerOf(__instance.Entity))) return true;
        System.Threading.Interlocked.Increment(ref OfflineStats.Paused);
        return false;
    }
}
