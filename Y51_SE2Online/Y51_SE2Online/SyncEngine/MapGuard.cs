using System.Collections.Generic;
using HarmonyLib;
using Keen.Game2.Simulation.GameSystems.Discoveries.Discoverables;
using Keen.VRage.DCS.Components;
using Keen.VRage.Voxels.Components;
using Keen.VRage.Voxels.Definitions;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// Joiners get discoveries through the session snapshot, whose serializer skips a planet's biome map,
/// so opening the map (M) threw in <c>PlanetDiscoverable.CollectRawMaterials</c> and crashed the game.
/// </summary>
internal static class MapGuard
{
    public static long Resolved, Missing;

    public static void Install(Harmony harmony)
        => harmony.Patch(AccessTools.Method(typeof(PlanetDiscoverable), "CollectRawMaterials"), prefix: new HarmonyMethod(typeof(MapGuard), nameof(Prefix)));

    private static bool Prefix(PlanetDiscoverable __instance, ref HashSet<VoxelMaterialDefinition> __result)
    {
        if (__instance._biomeDefinition != null) return true;
        var session = MpPlugin.App?.ClientSession;
        if (session != null)
            foreach (Entity planet in session.GetEntitiesOfType<VoxelPlanetComponent>())
                if (planet.Get<VoxelPlanetComponent>().Name == __instance.Details.Name && planet.TryGet<PlanetBiomeMapComponent>() is { } biomes)
                {
                    __instance._biomeDefinition = biomes.Definition;
                    Resolved++;
                    return true;
                }
        Missing++;
        __result = new HashSet<VoxelMaterialDefinition>();
        return false;
    }
}
