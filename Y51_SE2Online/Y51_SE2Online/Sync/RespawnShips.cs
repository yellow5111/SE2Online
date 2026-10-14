using System;
using System.Collections.Generic;
using System.Linq;

namespace Y51_SE2Online.Sync;

public sealed record RespawnShip(Guid Prefab, string Key, string Name, string Detail);

/// <summary>The ships players can take on spawn or respawn: vanilla 2.4 cargo encounters that pass the survival checklist.</summary>
public static class RespawnShips
{
    /// <summary>Every flyable cargo encounter in SE2 2.4; the rig probe checks all of them.</summary>
    public static readonly RespawnShip[] Candidates =
    {
        new(new("57c9cf6b-d639-4352-820e-63cbb574eb0e"), "Pathfinder", "Pathfinder prospector", "Space miner and explorer: survival kit, refinery, assembler, ore detector"),
        new(new("553ffc79-7ec5-43d4-84bb-01889d78c7f5"), "Nova35", "Nova 35 freighter", "Unarmed space hauler with the biggest cargo hold"),
        new(new("f8c0082d-750b-4e9d-8886-c4d6dc511c4e"), "RenegadePathfinder", "Armed Pathfinder", "Pathfinder with gatling cannons and turrets"),
        new(new("dd8285f9-8841-454a-bbd8-8997f0090b10"), "CentreLineC", "CentreLine C", "Armed space hauler"),
        new(new("9c95bcf3-fe59-4ebc-843d-4ecb6fa61d37"), "CentreLineH2", "CentreLine H2", "Armed hydrogen hauler"),
        new(new("3a9d2387-86f4-405c-90dd-63feae963cbf"), "Gladius", "Gladius gunship", "Armed space gunship with gatling turrets"),
    };

    // The candidates that pass the survival checklist after the spawn top-up (E:\SE2MP\artifacts\ships\ship-checklist.md).
    // Both CentreLines have no oxygen source at all. ponytail: re-run the rig probe (shipprobe/shipreport) after a game update.
    public static readonly RespawnShip[] Catalog = Candidates.Where(s => s.Key is "Pathfinder" or "Nova35" or "RenegadePathfinder" or "Gladius").ToArray();

    public const int MaxCooldownMinutes = 1440;

    /// <summary>The ships a host offers: every catalog ship, or only the ones its settings name.</summary>
    public static RespawnShip[] Offered(IReadOnlyCollection<string>? allowed)
        => allowed is { Count: > 0 } ? Catalog.Where(s => allowed.Contains(s.Key, StringComparer.OrdinalIgnoreCase)).ToArray() : Catalog;

    public static string? Validate(IReadOnlyCollection<string>? allowed, int cooldownMinutes)
    {
        if (cooldownMinutes < 0 || cooldownMinutes > MaxCooldownMinutes) return $"Respawn ship cooldown must be between 0 and {MaxCooldownMinutes} minutes.";
        if (allowed == null || allowed.Count > Catalog.Length) return "Too many respawn ships listed.";
        foreach (string key in allowed)
            if (!Catalog.Any(s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase)))
                return $"Unknown respawn ship '{key}'. Use any of: {string.Join(", ", Catalog.Select(s => s.Key))}.";
        return null;
    }
}
