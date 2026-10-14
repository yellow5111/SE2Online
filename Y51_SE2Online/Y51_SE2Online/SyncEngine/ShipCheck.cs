using System;
using System.Collections.Generic;
using System.Linq;
using Keen.Game2.Simulation.GameSystems.AI;
using Keen.Game2.Simulation.GameSystems.Antennas;
using Keen.Game2.Simulation.GameSystems.Encounters;
using Keen.Game2.Simulation.GameSystems.Respawn;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.Detectors;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.MechanicalBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.Movement;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.Production;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.Production.ItemConverters;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.ResourceDistribution.Resources;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.Tools;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.Water;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.Game2.Simulation.WorldObjects.Items;
using Keen.Game2.Simulation.WorldObjects.Movement;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Physics.Data;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// The survival checklist every respawn ship (<see cref="Sync.RespawnShips"/>) must pass: power with fuel,
/// oxygen, fuel for its thrusters, a medbay or survival kit, a cockpit, thrust on all six axes, gyroscopes, an antenna,
/// cargo, production, completely built blocks and no NPC AI. <see cref="TopUp"/> refuels a freshly spawned ship.
/// </summary>
internal static class ShipCheck
{
    private static readonly Guid Electricity = new("bcded093-f5c0-4997-af3a-a6fbd853ad66");
    private static readonly Guid Hydrogen = new("a0e12811-7f81-4735-aef4-b74ef0c2d395");
    private static readonly Guid Oxygen = new("d129d38e-116d-4424-a1a8-bbb298ac9dba");

    internal sealed record Check(string Name, bool Pass, string Detail);

    private const int MinFuel = 500;

    private static IEnumerable<T> Blocks<T>(IEnumerable<Entity> grids, bool subgrids = true) where T : Component
    {
        var found = new List<T>();
        foreach (var grid in grids)
            if (!EngineCodec.IsDead(grid) && grid.TryGet<CubeGridComponent>() is { } cube)
                cube.VisitAllBlocksWithComponent<T>(found.Add, includeSubgrids: false);
        return found;
    }

    private static bool Is(Keen.VRage.Library.Definitions.Definition? type, Guid id) => type != null && type.Guid == id;

    private static string Ratio(double value) => $"{value * 100:0}%";

    /// <summary>The checklist for one ship (all of its grids). Every check must pass for the ship to be offered.</summary>
    internal static List<Check> Inspect(IReadOnlyCollection<Entity> grids)
    {
        var checks = new List<Check>();
        var blocks = Blocks<CubeBlockComponent>(grids).ToList();
        var incomplete = blocks.Count(b => b.BuildProgress < 0.999f || b.HealthIntegrity < 0.999f);
        checks.Add(new("Fully built", incomplete == 0, $"{blocks.Count} blocks, {incomplete} damaged or unfinished"));

        var main = grids.Where(g => !EngineCodec.IsDead(g) && g.TryGet<CubeGridComponent>() != null).OrderByDescending(g => Blocks<CubeBlockComponent>(new[] { g }).Count()).FirstOrDefault();
        double mass = 0;
        foreach (var grid in grids)
            if (!EngineCodec.IsDead(grid) && grid.Data.TryGet<RigidBodyMassProperties>(out var props)) mass += props.Mass;
        var thrust = new Dictionary<Base6Directions.Direction, (double Nominal, double Supplied)>();
        foreach (var t in main == null ? Enumerable.Empty<ThrusterComponent>() : Blocks<ThrusterComponent>(new[] { main }))
        {
            var data = t.Data.Get<ThrustData>();
            thrust.TryGetValue(data.Direction, out var sum);
            thrust[data.Direction] = (sum.Nominal + t._definition.ThrustPower, sum.Supplied + data.MaxThrustPower);
        }
        var axes = Enum.GetValues<Base6Directions.Direction>().Where(d => (int)d < 6).ToArray();
        double minAccel = axes.Min(d => thrust.TryGetValue(d, out var v) && mass > 0 ? v.Nominal / mass : 0);
        string perAxis = string.Join(", ", axes.Select(d => $"{d} {(thrust.TryGetValue(d, out var v) && mass > 0 ? v.Nominal / mass : 0):0.0}"));
        checks.Add(new("Thrust on all six axes", minAccel > 0, $"mass {mass / 1000:0.0} t; m/s^2 {perAxis}; 1 g on every axis: {(minAccel >= 9.81 ? "yes" : "no")}"));
        double supplied = thrust.Values.Sum(v => v.Supplied), nominal = thrust.Values.Sum(v => v.Nominal);
        checks.Add(new("Thrusters have fuel or power", nominal > 0 && supplied >= nominal * 0.999, $"{Ratio(nominal > 0 ? supplied / nominal : 0)} of rated thrust available"));
        checks.Add(new("Gyroscope", Blocks<GyroscopeComponent>(grids).Any(), Blocks<GyroscopeComponent>(grids).Count() + " gyroscopes"));
        checks.Add(new("Cockpit", Blocks<CockpitComponent>(grids).Any(), Blocks<CockpitComponent>(grids).Count() + " cockpits"));

        var converters = Blocks<ItemToResourceConverterComponent>(grids).ToList();
        var containers = Blocks<ResourceContainerComponent>(grids).ToList();
        bool Fuelled(ItemToResourceConverterComponent c) => c.Definition.ItemConsumed is { } item && c._inputInventory?.CountItem(item) > 0;
        bool Produces(ItemToResourceConverterComponent c, Guid type) => Is(c.Definition.ResourceProduced, type) || !c.Definition.ResourcesProduced.IsDefault && c.Definition.ResourcesProduced.Any(r => Is(r.Resource, type));
        string Stored(Guid type)
        {
            var list = containers.Where(c => Is(c.ResourceType, type)).ToList();
            return list.Count == 0 ? "none" : $"{list.Count} at {Ratio(list.Average(c => (double)c.CurrentChargeLevel))}";
        }
        bool HasStored(Guid type) => containers.Any(c => Is(c.ResourceType, type) && c.CurrentChargeLevel > 0);
        bool HasMade(Guid type) => converters.Any(c => Produces(c, type) && Fuelled(c));
        int renewable = Blocks<SolarGeneratorComponent>(grids).Count() + Blocks<WindTurbineComponent>(grids).Count() + Blocks<WaterTurbineComponent>(grids).Count();
        // Ion thrusters, O2/H2 generators and production all run on power, so a battery alone is not enough: it must recharge.
        checks.Add(new("Sustained power", HasMade(Electricity) || renewable > 0,
            $"fuelled reactors {converters.Count(c => Produces(c, Electricity) && Fuelled(c))}/{converters.Count(c => Produces(c, Electricity))}, solar/wind/water {renewable}, batteries {Stored(Electricity)}"));
        checks.Add(new("Oxygen", HasMade(Oxygen) || HasStored(Oxygen),
            $"fuelled O2 generators {converters.Count(c => Produces(c, Oxygen) && Fuelled(c))}/{converters.Count(c => Produces(c, Oxygen))}, O2 tanks {Stored(Oxygen)}"));
        checks.Add(new("Hydrogen (if used)", !converters.Any(c => Produces(c, Hydrogen)) && !containers.Any(c => Is(c.ResourceType, Hydrogen)) || HasMade(Hydrogen) || HasStored(Hydrogen),
            $"H2 generators {converters.Count(c => Produces(c, Hydrogen))}, H2 tanks {Stored(Hydrogen)}"));
        checks.Add(new("Medbay or survival kit", Blocks<RespawnPointComponent>(grids).Any(), Blocks<RespawnPointComponent>(grids).Count() + " respawn points"));
        checks.Add(new("Production", Blocks<ItemConverterComponent>(grids).Any(), Blocks<ItemConverterComponent>(grids).Count() + " refineries, assemblers or kits"));
        checks.Add(new("Antenna", Blocks<AntennaComponent>(grids).Any(), Blocks<AntennaComponent>(grids).Count() + " antennas"));
        int inventories = blocks.Sum(b => b.Entity.Components.OfType<InventoryComponent>().Count());
        checks.Add(new("Cargo", inventories > 0, inventories + " inventories"));
        int ai = grids.Count(g => !EngineCodec.IsDead(g) && g.TryGet<AIControllerComponent>() != null);
        checks.Add(new("No NPC AI", ai == 0, ai + " grids with an AI controller"));
        // Nice to have; reported, never required.
        checks.Add(new("Connector (optional)", true, Blocks<ConnectorComponent>(grids).Count() + " connectors"));
        checks.Add(new("Ore detector (optional)", true, Blocks<OreDetectorBlockComponent>(grids).Count() + " ore detectors"));
        var named = blocks.Select(b => b.Definition.DebugNameShort ?? "?").Where(n => !n.Contains("Armor", StringComparison.OrdinalIgnoreCase))
            .GroupBy(n => n).OrderBy(g => g.Key).Select(g => g.Count() == 1 ? g.Key : g.Key + " x" + g.Count());
        checks.Add(new("Functional blocks (info)", true, string.Join(", ", named)));
        return checks;
    }

    /// <summary>Fills generator fuel and every battery and tank of a freshly spawned ship; returns what changed.</summary>
    internal static List<string> TopUp(IReadOnlyCollection<Entity> grids)
    {
        var changes = new List<string>();
        foreach (var converter in Blocks<ItemToResourceConverterComponent>(grids))
        {
            if (converter.Definition.ItemConsumed is not { } item || converter._inputInventory is not { } inventory) continue;
            // Cargo encounters carry random loot, often an empty O2/H2 generator; keep every generator stocked.
            int have = (int)inventory.CountItem(item);
            if (have >= MinFuel) continue;
            var added = inventory.AddItem(new ItemStack(item, MinFuel - have));
            if (added > 0) changes.Add($"added {added} {item.DebugNameShort} to {converter.Entity}");
        }
        foreach (var container in Blocks<ResourceContainerComponent>(grids))
        {
            if (container.CurrentChargeLevel >= 1) continue;
            ref var data = ref container.ResourceEntity.GetWritePtr<ResourceContainerComponent.ResourceContainerData>();
            data.CurrentChargeValue = data.MaxCapacity;
            changes.Add($"filled {container.ResourceType?.DebugNameShort} container {container.Entity}");
        }
        return changes;
    }

    /// <summary>The rig probe's ships by candidate key (see <see cref="HostWorld.ProbeShips"/>).</summary>
    internal static readonly Dictionary<string, List<Entity>> Probed = new();

    /// <summary>Rig: the checklist of every probed ship as markdown, optionally after a top-up; written to <paramref name="path"/> when given.</summary>
    internal static string Report(bool topUp, string? path)
    {
        var text = new System.Text.StringBuilder();
        text.AppendLine(topUp ? "## After top-up" : "## As spawned").AppendLine();
        foreach (var ship in Sync.RespawnShips.Candidates)
        {
            if (!Probed.TryGetValue(ship.Key, out var grids)) continue;
            text.AppendLine($"### {ship.Name} ({ship.Key}, {ship.Prefab})").AppendLine();
            if (grids.Count == 0) { text.AppendLine("Did not spawn.").AppendLine(); continue; }
            if (topUp) foreach (string change in TopUp(grids)) text.AppendLine("- top-up: " + change);
            var checks = Inspect(grids);
            text.AppendLine($"Result: **{(checks.All(c => c.Pass) ? "PASS" : "FAIL")}**").AppendLine();
            text.AppendLine("| Check | Pass | Detail |").AppendLine("|---|---|---|");
            foreach (var c in checks) text.AppendLine($"| {c.Name} | {(c.Pass ? "yes" : "NO")} | {c.Detail} |");
            text.AppendLine();
        }
        if (path != null) System.IO.File.AppendAllText(path, text.ToString());
        return text.ToString();
    }
}
