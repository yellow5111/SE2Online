using System;
using System.Collections.Generic;
using System.Linq;

namespace Y51_SE2Online.Sync;

/// <param name="Lift">Upward acceleration in m/s^2 from the thrusters that lift the ship in its upright pose (rig probe, after the top-up).</param>
/// <param name="Grid">For a prefab that holds more than the ship (a planet contract): the one grid to keep.</param>
/// <param name="Space">Offered as a space respawn ship near the world spawn.</param>
/// <param name="Ion">Ion thrust: full only in thin air, so landers need a planet with an air density of at most <see cref="RespawnShips.IonAirDensity"/>.</param>
public sealed record RespawnShip(Guid Prefab, string Key, string Name, string Detail, double Lift = 0, string? Grid = null, bool Space = true, bool Ion = true);

/// <summary>The ships players can take on spawn or respawn: vanilla 2.4 cargo encounters that pass the survival checklist.</summary>
public static class RespawnShips
{
    /// <summary>Every flyable cargo encounter in SE2 2.4; the rig probe checks all of them.</summary>
    public static readonly RespawnShip[] Candidates =
    {
        new(new("57c9cf6b-d639-4352-820e-63cbb574eb0e"), "Pathfinder", "Pathfinder prospector", "Space miner and explorer: survival kit, refinery, assembler, ore detector", 7.2),
        new(new("553ffc79-7ec5-43d4-84bb-01889d78c7f5"), "Nova35", "Nova 35 freighter", "Unarmed space hauler with the biggest cargo hold", 9.6),
        new(new("f8c0082d-750b-4e9d-8886-c4d6dc511c4e"), "RenegadePathfinder", "Armed Pathfinder", "Pathfinder with gatling cannons and turrets", 6.9),
        new(new("dd8285f9-8841-454a-bbd8-8997f0090b10"), "CentreLineC", "CentreLine C", "Armed space hauler", 7.2),
        new(new("9c95bcf3-fe59-4ebc-843d-4ecb6fa61d37"), "CentreLineH2", "CentreLine H2", "Armed hydrogen hauler", 6.5),
        new(new("3a9d2387-86f4-405c-90dd-63feae963cbf"), "Gladius", "Gladius gunship", "Armed space gunship with gatling turrets", 9.2),
        // The only vanilla 2.4 prefab with a survival kit that lifts off at 1 g: the hydrogen dropship of the Verdure bounty contract
        // (every planet encounter is a static base; the other atmospheric craft have no survival kit or recharging power).
        new(new("0581d25e-1e6b-4801-a2d6-cfed4af6cabd"), "Dropship", "Renegade Dropship", "Hydrogen lander: survival kit, refinery, assembler, O2/H2 generator, turret", 34.2, "Renegade Dropship", Space: false, Ion: false),
    };

    // The candidates that pass the survival checklist after the spawn top-up (E:\SE2Online\artifacts\ships\ship-checklist.md).
    // Both CentreLines have no oxygen source at all. ponytail: re-run the rig probe (shipprobe/shipreport) after a game update.
    public static readonly RespawnShip[] Catalog = Candidates.Where(s => s.Key is "Pathfinder" or "Nova35" or "RenegadePathfinder" or "Gladius" or "Dropship").ToArray();

    /// <summary>A lander needs this much more lift than the planet's surface gravity (cargo, climbing).</summary>
    public const double LiftMargin = 1.5;

    /// <summary>Ion thrust is full up to this air density and falls off above it (ThrustClassesConfiguration "Ion").</summary>
    public const double IonAirDensity = 0.2;

    /// <summary>The landers: the dropship for every planet, the Pathfinder for airless low-gravity moons (one survival ship per planet type).</summary>
    public static readonly string[] LanderKeys = { "Pathfinder", "Dropship" };

    /// <summary>The offered landers that can lift off from a planet with this surface gravity (m/s^2) and air density.</summary>
    public static RespawnShip[] ForPlanet(IReadOnlyCollection<string>? allowed, double gravity, double airDensity)
        => Offered(allowed).Where(s => LanderKeys.Contains(s.Key) && s.Lift >= gravity * LiftMargin && (!s.Ion || airDensity <= IonAirDensity)).ToArray();

    /// <summary>
    /// Gap under a piloted lander's hull at the spawn: clear of the terrain bumps a site may still have, and the dampeners
    /// (kept on by the spawn) hold it there under its pilot: an offered lander lifts at least <see cref="LiftMargin"/> times the planet's gravity.
    /// </summary>
    // Well up in the atmosphere, not just over the ground: the pilot starts with room to fly and no terrain to catch.
    public const double LanderHoverMeters = 2000;

    /// <summary>Gap for a lander nobody sits in: it settles onto its gear, so the player standing beside it can board.</summary>
    public const double LanderRestMeters = 1.5;

    /// <summary>How far the lander's grid origin goes above the site ground: the ground rise under its footprint, the gap, and the hull below the origin (<paramref name="boxMinY"/>, the grid box bottom in grid space).</summary>
    public static double LanderOriginHeight(double groundRise, double boxMinY, bool piloted)
        => groundRise + (piloted ? LanderHoverMeters : LanderRestMeters) - boxMinY;

    public const int MaxCooldownMinutes = 1440;

    /// <summary>The ships a host offers: every catalog ship, or only the ones its settings name.</summary>
    /// <summary>The list entry that turns respawn ships off; an empty list offers every ship.</summary>
    public const string None = "none";

    public static RespawnShip[] Offered(IReadOnlyCollection<string>? allowed)
        => allowed is { Count: > 0 } ? Catalog.Where(s => allowed.Contains(s.Key, StringComparer.OrdinalIgnoreCase)).ToArray() : Catalog;

    public static string? Validate(IReadOnlyCollection<string>? allowed, int cooldownMinutes)
    {
        if (cooldownMinutes < 0 || cooldownMinutes > MaxCooldownMinutes) return $"Respawn ship cooldown must be between 0 and {MaxCooldownMinutes} minutes.";
        if (allowed == null || allowed.Count > Catalog.Length) return "Too many respawn ships listed.";
        if (allowed.Contains(None, StringComparer.OrdinalIgnoreCase)) return allowed.Count == 1 ? null : $"\"{None}\" turns respawn ships off and cannot be combined with ship names.";
        foreach (string key in allowed)
            if (!Catalog.Any(s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase)))
                return $"Unknown respawn ship '{key}'. Use any of: {string.Join(", ", Catalog.Select(s => s.Key))}, or \"{None}\" for no respawn ships.";
        return null;
    }
}
