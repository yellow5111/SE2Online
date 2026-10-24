using System;
using System.Collections.Generic;
using System.Linq;

namespace Y51_SE2Online.Sync;

/// <param name="Lift">Upward acceleration in m/s^2 from the thrusters that lift the ship in its upright pose (rig probe, after the top-up).</param>
/// <param name="Grid">For a prefab that holds more than the ship (a planet contract): the one grid to keep.</param>
/// <param name="Space">Offered as a space respawn ship near the world spawn.</param>
/// <param name="Ion">Ion thrust: full only in thin air, so landers need a planet with an air density of at most <see cref="RespawnShips.IonAirDensity"/>.</param>
/// <param name="Blueprint">A blueprint ship: the folder holding its grid.json.vrb (no encounter prefab).</param>
/// <param name="Preview">The blueprint's folder name under Content\Ships, for the spawn screen's picture ("" for none).</param>
public sealed record RespawnShip(Guid Prefab, string Key, string Name, string Detail, double Lift = 0, string? Grid = null, bool Space = true, bool Ion = true,
    string? Blueprint = null, string Preview = "", SpawnPlace[]? Places = null);

/// <summary>Where a respawn ship can be offered.</summary>
public enum SpawnPlace : byte { Space, Moon, Atmosphere, Airless }

/// <summary>A respawn ship from a blueprint the host ships (server.json Network.RespawnBlueprints).</summary>
public sealed class RespawnBlueprint
{
    public string Name { get; set; } = "";
    /// <summary>The blueprint folder (grid.json.vrb inside): a folder name under the plugin's Content\Ships, or a full path.</summary>
    public string Path { get; set; } = "";
    /// <summary>Where it is offered: "space", "moon", "atmosphere" (planets with air), "airless" (planets without air, not moons).</summary>
    public List<string> Offer { get; set; } = new();
    public string Detail { get; set; } = "";
}

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

    public const int MaxBlueprints = 16;

    /// <summary>"none" in the vanilla list turns every respawn ship off, the blueprint ships too (as before 0.18).</summary>
    public static bool AllOff(IReadOnlyCollection<string>? vanilla) => vanilla?.Contains(None, StringComparer.OrdinalIgnoreCase) == true;

    /// <summary>The vanilla encounter ships are offered when the switch is on, or when a host named them before 0.18.</summary>
    public static bool OfferVanilla(bool vanillaSwitch, IReadOnlyCollection<string>? vanilla) => !AllOff(vanilla) && (vanillaSwitch || vanilla is { Count: > 0 });

    /// <summary>The spawn card tag for where a ship is offered.</summary>
    public static string Tag(RespawnShip ship)
    {
        var places = ship.Places ?? (ship.Space ? new[] { SpawnPlace.Space } : new[] { SpawnPlace.Atmosphere });
        bool space = places.Contains(SpawnPlace.Space) || places.Contains(SpawnPlace.Moon), planets = places.Contains(SpawnPlace.Atmosphere) || places.Contains(SpawnPlace.Airless);
        return space && planets ? "Anywhere" : planets ? "Planets" : places.Contains(SpawnPlace.Moon) ? "Space & moons" : "Space";
    }

    public static List<RespawnBlueprint> DefaultBlueprints() => new()
    {
        new() { Name = "Banana Boat", Path = "SE1 Banana Boat", Offer = new() { "space", "moon" }, Detail = "Ion ship with a survival kit, reactor, refinery, assembler, O2/H2 generator and gravity generator" },
        new() { Name = "Atmospheric Lander", Path = "SE1 Atmospheric Lander", Offer = new() { "atmosphere" }, Detail = "Atmospheric lander with a survival kit, reactor, refinery, assembler, O2/H2 generator, ore detector and landing gear" },
    };

    public static bool TryPlace(string? word, out SpawnPlace place) => Enum.TryParse(word, true, out place) && Enum.IsDefined(place) && !int.TryParse(word, out _);

    /// <summary>A planet's kind for the respawn ships: air first (Caligo is a moon with full air, where ion thrust is
    /// zero), then the generator's moon flag.</summary>
    public static SpawnPlace Classify(bool moon, double airDensity) => airDensity > MinAirDensity ? SpawnPlace.Atmosphere : moon ? SpawnPlace.Moon : SpawnPlace.Airless;

    /// <summary>At or below this air density atmospheric thrust is zero and ion thrust full (ThrustClassesConfiguration).</summary>
    public const double MinAirDensity = 0.2;

    /// <summary>The blueprint ships offered at a place, in the host's order.</summary>
    public static RespawnShip[] ForPlace(IEnumerable<RespawnBlueprint>? blueprints, SpawnPlace place)
        => (blueprints ?? Enumerable.Empty<RespawnBlueprint>()).Select(Ship).Where(s => s?.Places?.Contains(place) == true).ToArray()!;

    /// <summary>A blueprint as a respawn ship; null for an entry without a name, path or place.</summary>
    public static RespawnShip? Ship(RespawnBlueprint? b)
    {
        if (b == null || string.IsNullOrWhiteSpace(b.Name) || string.IsNullOrWhiteSpace(b.Path)) return null;
        var places = (b.Offer ?? new()).Select(w => TryPlace(w, out var p) ? p : (SpawnPlace?)null).Where(p => p != null).Select(p => p!.Value).Distinct().ToArray();
        if (places.Length == 0) return null;
        string folder = b.Path.TrimEnd('/', '\\');
        string preview = System.IO.Path.GetFileName(folder);
        return new RespawnShip(Guid.Empty, "bp:" + b.Name.Trim(), b.Name.Trim(), string.IsNullOrWhiteSpace(b.Detail) ? "Respawn ship with a medbay" : b.Detail.Trim(),
            Space: places.Contains(SpawnPlace.Space), Ion: false, Blueprint: b.Path, Preview: SafePreview(preview), Places: places);
    }

    /// <summary>A preview name a client may look up in its own Content\Ships: one plain folder name, never a path.</summary>
    public static string SafePreview(string? name)
        => name is { Length: > 0 and <= 64 } && name.Trim() == name && name != "." && name != ".." && name.All(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.') ? name : "";

    public static string? ValidateBlueprints(IReadOnlyCollection<RespawnBlueprint>? blueprints)
    {
        if (blueprints == null) return "RespawnBlueprints must be a list (empty for none).";
        if (blueprints.Count > MaxBlueprints) return $"At most {MaxBlueprints} respawn blueprints.";
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in blueprints)
        {
            if (b == null || string.IsNullOrWhiteSpace(b.Name) || b.Name.Length > 48) return "Each respawn blueprint needs a Name of at most 48 characters.";
            if (!names.Add(b.Name.Trim())) return $"Respawn blueprint '{b.Name}' is listed twice.";
            if (Candidates.Any(s => string.Equals(s.Name, b.Name.Trim(), StringComparison.OrdinalIgnoreCase) || string.Equals(s.Key, b.Name.Trim(), StringComparison.OrdinalIgnoreCase)))
                return $"Respawn blueprint '{b.Name}' has the name of a vanilla respawn ship; choose another name.";
            if (string.IsNullOrWhiteSpace(b.Path)) return $"Respawn blueprint '{b.Name}' needs a Path (a folder under Content\\Ships or a full path).";
            if (b.Offer == null || b.Offer.Count == 0) return $"Respawn blueprint '{b.Name}' needs Offer: any of space, moon, atmosphere, airless.";
            foreach (string w in b.Offer)
                if (!TryPlace(w, out _)) return $"Respawn blueprint '{b.Name}': unknown place '{w}'. Use space, moon, atmosphere or airless.";
        }
        return null;
    }

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
