using System;
using System.Collections.Generic;
using System.Linq;
using Keen.Game2.Simulation.GameSystems.Encounters;
using Keen.Game2.Simulation.GameSystems.RangedAffectGenerators.Atmosphere;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.Movement;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.Game2.Simulation.WorldObjects.CubeGrids.Physicss;
using Keen.Game2.Simulation.WorldObjects.Movement;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.Core.Game.Definitions;
using Keen.VRage.Core.Game.GameSystems.Gravity;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Definitions;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Library.Utils;
using Keen.VRage.Physics.Data;
using Keen.VRage.Voxels.Components;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

internal sealed partial class HostWorld
{
    /// <summary>Surface gravity in m/s^2 of a planet entity (its gravity at the zero-ground radius).</summary>
    private static double SurfaceGravity(Session session, Entity planet)
    {
        var component = planet.Get<VoxelPlanetComponent>();
        var at = planet.Data.GetWorldTransform().Position + new Vector3D(0, component.ZeroGroundRadius, 0);
        try { return session.TryGet<IGravitySystem>()?.GetGravityForGeneratorAtPoint(planet, at).Length() ?? 0; }
        catch (Exception) { return 0; }
    }

    /// <summary>Rig: every planet in the world with radius, gravity and atmosphere.</summary>
    internal static string PlanetReport(Session session)
    {
        var rows = new List<string>();
        foreach (Entity planet in session.GetEntitiesOfType<VoxelPlanetComponent>())
        {
            var p = planet.Get<VoxelPlanetComponent>();
            var at = planet.Data.GetWorldTransform().Position;
            rows.Add($"{p.Name} id {p.Id} generator {p.Generator.Guid} moon {p.Generator.IsMoon} radius {p.Radius:0} zero-ground {p.ZeroGroundRadius:0} atmosphere-radius {p.AtmosphereRadius:0} gravity {SurfaceGravity(session, planet):0.00} m/s2 air {AirDensity(planet):0.00} offers {string.Join("/", RespawnShips.ForPlanet(null, SurfaceGravity(session, planet), AirDensity(planet)).Select(s => s.Key))} at ({at.X:0},{at.Y:0},{at.Z:0})");
        }
        return rows.Count == 0 ? "no planets" : string.Join("\n", rows);
    }

    /// <summary>Surface air density of a planet (its atmosphere generator's density; 0 without one).</summary>
    private static double AirDensity(Entity planet) => planet.TryGet<AtmosphereGeneratorComponent>()?._definition?.Density ?? 0;

    /// <summary>Where a planet lander goes: the ground point and its up (away from the planet centre), chosen before the spawn.</summary>
    private sealed class PlanetSite
    {
        public required Entity Planet;
        public required RespawnShip Ship;
        public required Vector3D Ground;
        public required Vector3D Up;
        public long SpawnedMs;
    }

    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Joiner, PlanetSite> _planetSites = new();

    private const double SiteClearMeters = 400, SiteSlopeMeters = 6;

    /// <summary>A lander option per planet in the world for every offered ship that lifts off there.</summary>
    private void AddPlanetShips(Session session, List<SpawnTarget> targets, long wait)
    {
        foreach (Entity planet in session.GetEntitiesOfType<VoxelPlanetComponent>().OrderBy(p => p.Get<VoxelPlanetComponent>().Name, StringComparer.OrdinalIgnoreCase))
        {
            if (EngineCodec.IsDead(planet)) continue;
            var p = planet.Get<VoxelPlanetComponent>();
            double gravity = SurfaceGravity(session, planet);
            foreach (var ship in RespawnShips.ForPlanet(ShipSettings?.RespawnShips, gravity, AirDensity(planet)))
            {
                bool hasShip = Singleton<DefinitionManager>.Instance.TryGetDefinition(ship.Prefab, out PrefabDefinition _);
                string detail = !hasShip ? "Not available in this game version" : wait > 0 ? $"Available again in {(wait + 59_999) / 60_000} min"
                    : $"{ship.Detail}; lands on {p.Name} ({gravity / 9.81:0.0#} g), fuelled and yours to keep";
                targets.Add(new(new SpawnOption(SpawnKind.Ship, $"{p.Name} surface: {ship.Name}", detail, hasShip && wait <= 0), null, ship, p.Id));
            }
        }
    }

    /// <summary>
    /// The point itself in open space; inside a planet's gravity, the nearest point 30 km past it. A cargo encounter
    /// finds no route inside a gravity well ("Could not find valid cargo ship path"), so a world spawn on a planet
    /// (Verdure in the scenarios) failed every space respawn ship.
    /// </summary>
    internal static Vector3D OpenSpace(Session session, Vector3D at)
    {
        foreach (Entity planet in session.GetEntitiesOfType<VoxelPlanetComponent>())
        {
            if (EngineCodec.IsDead(planet)) continue;
            var sphere = planet.Get<VoxelPlanetComponent>().GetSphereOfInfluence();
            var offset = at - sphere.Center;
            if (offset.Length() < sphere.Radius + 30_000)
                return sphere.Center + (offset.LengthSquared() > 1 ? Vector3D.Normalize(offset) : new Vector3D(0, 1, 0)) * (sphere.Radius + 30_000);
        }
        return at;
    }

    /// <summary>A respawn ship near the world spawn, or a lander on the chosen planet's surface.</summary>
    private bool StartShipOrLander(Session session, Joiner j, Keen.Game2.Simulation.GameSystems.Respawn.IRespawn respawn, SpawnTarget target)
    {
        _planetSites.Remove(j);
        if (target.Planet == default) return StartShip(session, j, respawn, target.Ship!);
        var planet = session.GetEntitiesOfType<VoxelPlanetComponent>().FirstOrDefault(e => !EngineCodec.IsDead(e) && e.Get<VoxelPlanetComponent>().Id == target.Planet);
        var encounters = session.SessionComponents.TryGet<EncounterSessionComponent>();
        if (planet == null || encounters == null || !Singleton<DefinitionManager>.Instance.TryGetDefinition(target.Ship!.Prefab, out PrefabDefinition prefab)) return false;
        var site = FindSite(session, planet, respawn.WorldSpawn.Position);
        if (site == null)
        {
            _log.Warn($"host spawn: no free landing site on {planet.Get<VoxelPlanetComponent>().Name} for ClientId {j.ClientId}");
            return false;
        }
        site.Ship = target.Ship;
        _planetSites.AddOrUpdate(j, site);
        _shipChosen[j.ClientId] = target.Ship;
        j.ShipRoots = null;
        j.ShipDeadline = MpClock.NowMs() + ShipSpawnTimeoutMs;
        j.ShipPending = true;
        // A cargo encounter needs open space for its route (it fails inside a gravity well); the lander is moved to the site anyway.
        var anchor = new WorldTransform(OpenSpace(session, site.Ground), respawn.WorldSpawn.Orientation);
        SpawnLander(session, encounters, prefab, anchor, j);
        _log.Info($"host spawn: landing site for ClientId {j.ClientId} on {planet.Get<VoxelPlanetComponent>().Name} at ({site.Ground.X:0},{site.Ground.Y:0},{site.Ground.Z:0})");
        return true;
    }

    private async void SpawnLander(Session session, EncounterSessionComponent encounters, PrefabDefinition prefab, WorldTransform at, Joiner j)
    {
        try { j.ShipRoots = await SpawnEncounter(session, encounters, prefab, at, missions: false); }
        catch (Exception ex)
        {
            _log.Warn("host spawn: spawning the planet lander failed: " + ex.Message);
            j.ShipRoots = Array.Empty<Entity>();
        }
    }

    /// <summary>Surface radius (distance from the planet centre) under a planet-local direction.</summary>
    private static double SurfaceRadius(VoxelPlanetComponent planet, Vector3 direction)
        => planet.QuerySurfacePosition(direction * planet.Radius, out Keen.VRage.Voxels.Storage.Planet.SampledPlanetPosition s) ? s.SurfaceDistance : -1;

    private static Vector3 Tangent(Vector3 up)
    {
        var t = Vector3.Cross(up, Math.Abs(up.Y) < 0.9f ? Vector3.Up : Vector3.Right);
        return Vector3.Normalize(t);
    }

    /// <summary>
    /// A dry, fairly level ground point 2-14 km from the world spawn when that lies on this planet (anywhere otherwise),
    /// at least <see cref="SiteClearMeters"/> from every grid and outside every safe zone.
    /// </summary>
    private static PlanetSite? FindSite(Session session, Entity planet, Vector3D worldSpawn)
    {
        var p = planet.Get<VoxelPlanetComponent>();
        var frame = planet.Data.GetWorldTransform();
        var water = planet.TryGet<Keen.VRage.Water.Components.PlanetaryWaterComponent>();
        var spawnLocal = (Vector3)WorldTransform.TransformInv(worldSpawn, frame);
        bool spawnHere = spawnLocal.Length() < p.Radius * 1.5f;
        var anchor = spawnHere ? Vector3.Normalize(spawnLocal) : Vector3.Normalize(new Vector3(Random.Shared.NextSingle() - 0.5f, Random.Shared.NextSingle() - 0.5f, Random.Shared.NextSingle() - 0.5f));
        var grids = session.GetEntitiesOfType<CubeGridComponent>().Where(g => !EngineCodec.IsDead(g)).Select(g => g.Data.GetWorldTransform().Position).ToList();
        var zones = session.GetEntitiesOfType<Keen.Game2.Simulation.WorldObjects.SafeZones.SafeZoneComponent>().Where(z => !EngineCodec.IsDead(z))
            .Select(z => (At: z.Data.GetWorldTransform().Position, Radius: (double)z.Get<Keen.Game2.Simulation.WorldObjects.SafeZones.SafeZoneComponent>().Radius)).ToList();
        var tiles = new List<Keen.VRage.Water.OceanTile>();
        for (int attempt = 0; attempt < 48; attempt++)
        {
            // ponytail: random sampling around the anchor, widening each attempt; a scan of the planet's flat biomes if it misses often.
            double distance = spawnHere ? 2000 + attempt * 250 : Random.Shared.NextDouble() * p.Radius * 3;
            var t1 = Tangent(anchor);
            var t2 = Vector3.Cross(anchor, t1);
            double angle = Random.Shared.NextDouble() * Math.PI * 2;
            var dir = Vector3.Normalize(anchor + (t1 * (float)Math.Cos(angle) + t2 * (float)Math.Sin(angle)) * (float)(distance / p.Radius));
            double radius = SurfaceRadius(p, dir);
            if (radius <= 0) continue;
            // Level: the ground 25 m around must be within a few metres of the centre.
            var a = Tangent(dir);
            var b = Vector3.Cross(dir, a);
            bool level = true;
            for (int k = 0; k < 6 && level; k++)
            {
                double s = k * Math.PI / 3;
                double r = SurfaceRadius(p, Vector3.Normalize(dir + (a * (float)Math.Cos(s) + b * (float)Math.Sin(s)) * (float)(25 / radius)));
                level = r > 0 && Math.Abs(r - radius) <= SiteSlopeMeters;
            }
            if (!level) continue;
            var ground = WorldTransform.Transform((Vector3D)(dir * (float)radius), frame);
            if (water != null)
            {
                tiles.Clear();
                var local = water.WorldToLocal(ground);
                water.QueryOceanTiles(new BoundingBox(local - new Vector3(1), local + new Vector3(1)), tiles);
                double fromCentre = Vector3D.Distance(in ground, water.PlanetCenter);
                if (tiles.Any(t => fromCentre < t.WaterLevelUnscaled + 3)) continue;
            }
            if (grids.Any(g => Vector3D.Distance(in g, in ground) < SiteClearMeters)) continue;
            if (zones.Any(z => Vector3D.Distance(in z.At, in ground) < z.Radius + SiteClearMeters)) continue;
            return new PlanetSite { Planet = planet, Ship = null!, Ground = ground, Up = Vector3D.Normalize(WorldTransform.TransformDirection((Vector3D)dir, frame)) };
        }
        return null;
    }

    /// <summary>
    /// After a lander spawned (wherever its prefab put it): keeps only the ship's grids, stands it upright over the chosen site
    /// with its bottom <see cref="RespawnShips.LanderHoverMeters"/> above the highest ground under it (just above it when it
    /// has no cockpit for the player), and returns where the player stands beside it if it cannot sit in it.
    /// </summary>
    private List<Entity> PlaceOnPlanet(Session session, Joiner j, List<Entity> released, out WorldTransform? beside)
    {
        beside = null;
        if (!_planetSites.TryGetValue(j, out var site)) return released;
        _planetSites.Remove(j);
        var grids = released.Where(e => !EngineCodec.IsDead(e) && e.TryGet<CubeGridComponent>() != null).ToList();
        var keep = site.Ship.Grid == null ? grids : grids.Where(g => g.Get<CubeGridComponent>().DisplayName.ToString() == site.Ship.Grid).ToList();
        // The rest of the contract shares a physics motion group with the lander (it stands on the contract's pad): it goes
        // before the lander moves, and HoldPins moves it a tick later, once Havok has dropped the constraint.
        foreach (var e in released)
            if (!keep.Contains(e) && !EngineCodec.IsDead(e)) session.Get<IEntitySpawner>().DeleteEntity(e);
        if (keep.Count == 0 || EngineCodec.IsDead(site.Planet)) return new List<Entity>();
        int Blocks(Entity g) { int n = 0; g.Get<CubeGridComponent>().VisitAllBlocksWithComponent((Keen.Game2.Simulation.WorldObjects.CubeBlocks.CubeBlockComponent _) => n++, includeSubgrids: false); return n; }
        var main = keep.OrderByDescending(Blocks).First();
        keep.Remove(main);
        keep.Insert(0, main);
        var box = BoundingBoxData.Get(main);
        var old = main.Data.GetWorldTransform();
        var up = (Vector3)site.Up;
        var forward = old.Orientation.GetForward();
        forward -= up * Vector3.Dot(forward, up);
        if (forward.LengthSquared() < 1e-4f) forward = Tangent(up);
        var orientation = Quaternion.CreateFromForwardUp(Vector3.Normalize(forward), up);
        // Highest ground under the ship's footprint, measured along the site's up.
        var p = site.Planet.Get<VoxelPlanetComponent>();
        var frame = site.Planet.Data.GetWorldTransform();
        double halfX = (box.Max.X - box.Min.X) / 2.0, halfZ = (box.Max.Z - box.Min.Z) / 2.0, footprint = Math.Sqrt(halfX * halfX + halfZ * halfZ);
        var centre = WorldTransform.TransformInv(site.Ground, frame);
        double groundRadius = centre.Length(), highest = groundRadius;
        var dir = (Vector3)Vector3D.Normalize(centre);
        var a = Tangent(dir);
        var b = Vector3.Cross(dir, a);
        for (int k = 0; k < 12; k++)
        {
            double s = k * Math.PI / 6, reach = footprint * (k % 2 == 0 ? 1 : 0.5);
            highest = Math.Max(highest, SurfaceRadius(p, Vector3.Normalize(dir + (a * (float)Math.Cos(s) + b * (float)Math.Sin(s)) * (float)(reach / groundRadius))));
        }
        // The box centre (not the grid origin, which can sit far off the hull) goes over the site.
        bool piloted = FindCockpit(keep) != null;
        var position = site.Ground - (Vector3D)Vector3.Transform(new Vector3(box.Center.X, 0, box.Center.Z), orientation) + site.Up * RespawnShips.LanderOriginHeight(highest - groundRadius, box.Min.Y, piloted);
        var rotation = orientation * Quaternion.Conjugate(old.Orientation);
        var pins = new List<(Entity Grid, WorldTransform At)>();
        foreach (var grid in keep)
        {
            var t = grid.Data.GetWorldTransform();
            var offset = t.Position - old.Position;
            var at = new WorldTransform(position + Vector3D.Transform(in offset, rotation), rotation * t.Orientation);
            pins.Add((grid, at));
        }
        // The contract prefab spawns static. Moving it and switching it to dynamic in the same frame went wrong about one
        // time in four: the dynamic body was built at the contract's spot while the grid entity moved, so the lander
        // either snapped back there (km away, ~95 m up) or, put back on the site, turned about that far-away body (the
        // mouse swung the ship across the world and it hit terrain it was nowhere near). HoldPins moves it while still
        // static and switches it to dynamic a few ticks later, from where it stands.
        j.ShipPins = pins;
        j.ShipPinHeld = j.ShipPinFixes = 0;
        j.ShipPinsDynamic = false;
        // The player stands on the ground beside the ship, facing it.
        var side = Vector3.Transform(Vector3.Right, orientation);
        double sideReach = halfX + 6;
        var standDir = Vector3.Normalize(dir + (Vector3)WorldTransform.TransformDirectionInv((Vector3D)side, frame) * (float)(sideReach / groundRadius));
        double standRadius = SurfaceRadius(p, standDir);
        var stand = WorldTransform.Transform((Vector3D)(standDir * (float)(standRadius > 0 ? standRadius + 1.2 : groundRadius + 1.2)), frame);
        beside = new WorldTransform(stand, Quaternion.CreateFromForwardUp(-side, up));
        _log.Info($"host spawn: lander {site.Ship.Name} for ClientId {j.ClientId} placed on {p.Name}: {keep.Count} grids, footprint {footprint:0} m, ground rise {highest - groundRadius:0.0} m, {(piloted ? $"hovering {RespawnShips.LanderHoverMeters:0} m up for its pilot" : "no cockpit, resting on its gear")}, box {box.Min} to {box.Max}");
        return keep;
    }

    // Keen's own teleport (EntityAdmin.TeleportEntity): setting the transform moved the lander across physics clusters in a
    // way Havok's shared-motion groups could not follow and the dedicated server crashed on a migrated constraint.
    private static void Pin(Entity grid, WorldTransform at)
    {
        grid.Data.Teleport(in at);
        if (grid.Data.TryGet<RigidBodyData>(out _)) grid.Data.Set(new RigidBodyData(Vector3.Zero, Vector3.Zero));
    }

    /// <summary>
    /// Server thread while a lander settles: keeps its grids static on the site until they have been there for
    /// <see cref="PinStaticTicks"/> ticks, then makes them dynamic and watches <see cref="PinHeldTicks"/> more ticks;
    /// a grid that jumps away after the switch is made static and placed again.
    /// </summary>
    private void HoldPins(Joiner j, bool last)
    {
        if (j.ShipPins is not { } pins) return;
        bool held = true;
        foreach (var (grid, at) in pins)
        {
            if (EngineCodec.IsDead(grid) || Vector3D.Distance(grid.Data.GetWorldTransform().Position, at.Position) <= (j.ShipPinsDynamic ? PinSlackMeters : 0.5)) continue;
            held = false;
            j.ShipPinFixes++;
            if (j.ShipPinsDynamic) grid.TryGet<GridPhysicsComponent>()?.SetGridMotion(true);
            Pin(grid, at);
        }
        if (!held) j.ShipPinsDynamic = false;
        j.ShipPinHeld = held ? j.ShipPinHeld + 1 : 0;
        if (!j.ShipPinsDynamic && (j.ShipPinHeld >= PinStaticTicks || last))
        {
            foreach (var (grid, _) in pins)
                if (!EngineCodec.IsDead(grid)) grid.TryGet<GridPhysicsComponent>()?.SetGridMotion(false);
            j.ShipPinsDynamic = true;
            j.ShipPinHeld = 0;
        }
        if (!j.ShipPinsDynamic || (j.ShipPinHeld < PinHeldTicks && !last)) return;
        j.ShipPins = null;
        if (j.ShipPinFixes > 0 || !held)
            _log.Info($"host spawn: lander for ClientId {j.ClientId} {(held ? "stays on its site" : "did NOT stay on its site")} after {j.ShipPinFixes} correction(s)");
    }

    private const int PinStaticTicks = 5, PinHeldTicks = 30;

    // Once dynamic, a lander sags a little before its dampeners hold it (or settles onto its gear when it has no pilot);
    // that is left to the physics: pulling it back every tick shook it into the ground and broke blocks off. A lost
    // move is km.
    private const double PinSlackMeters = 50;

#if !PULSAR
    /// <summary>Rig probe: spawns each encounter prefab (comma-separated GUIDs) the game's way and writes every grid it holds to <paramref name="path"/>.</summary>
    internal static async void ProbeEncounterGrids(Session session, string guids, string path)
    {
        var encounters = session.SessionComponents.TryGet<EncounterSessionComponent>();
        var anchor = session.TryGet<Keen.Game2.Simulation.GameSystems.Respawn.IRespawn>()?.WorldSpawn ?? WorldTransform.Identity;
        foreach (string text in guids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var report = new System.Text.StringBuilder().AppendLine($"## {text}").AppendLine();
            try
            {
                if (encounters == null || !Singleton<DefinitionManager>.Instance.TryGetDefinition(new Guid(text), out PrefabDefinition prefab)) { report.AppendLine("no such prefab"); continue; }
                var roots = await SpawnEncounter(session, encounters, prefab, anchor);
                var grids = new List<Entity>();
                foreach (var spawner in EncounterHelper.EnumerateEntities<EncounterSpawnerComponent>(roots, null).ToList())
                    grids.AddRange(spawner.SpawnedEntities.Where(e => e.TryGet<CubeGridComponent>() != null));
                foreach (var root in roots) if (root.TryGet<CubeGridComponent>() != null && !grids.Contains(root)) grids.Add(root);
                report.AppendLine($"{roots.Length} roots, {grids.Count} grids").AppendLine();
                foreach (var grid in grids) report.AppendLine(DescribeGrid(session, grid));
            }
            catch (Exception ex) { report.AppendLine("failed: " + ex.Message); }
            finally { System.IO.File.AppendAllText(path, report.AppendLine().ToString()); }
        }
        System.IO.File.AppendAllText(path, "PROBE DONE\n");
    }
#endif

    private static string DescribeGrid(Session session, Entity grid)
    {
        var cube = grid.Get<CubeGridComponent>();
        var at = grid.Data.GetWorldTransform().Position;
        double g = session.GetEntitiesOfType<VoxelPlanetComponent>().Max(p => (double?)(session.TryGet<IGravitySystem>()?.GetGravityForGeneratorAtPoint(p, at).Length() ?? 0)) ?? 0;
        bool isStatic = grid.TryGet<GridPhysicsComponent>()?.GridMotionType == BodyArgs.Motion.Static;
        var checks = ShipCheck.Inspect(new[] { grid });
        int wheels = 0;
        cube.VisitAllBlocksWithComponent((Keen.Game2.Simulation.WorldObjects.CubeBlocks.MechanicalBlocks.Wheels.WheelCornerModuleComponent _) => wheels++);
        var thrusters = new List<string>();
        cube.VisitAllBlocksWithComponent((ThrusterComponent t) => thrusters.Add(t._definition.DebugNameShort + "@" + t.Data.Get<ThrustData>().Direction));
        float air = grid.Data.TryGet<AirData>(out var a) ? a.Density : -1;
        return $"### {cube.DisplayName} (static {isStatic}, gravity here {g:0.00}, air {air:0.00}, wheels {wheels})\n\nthrusters: {string.Join(", ", thrusters.GroupBy(x => x).Select(x => x.Key + " x" + x.Count()))}\n\n"
            + string.Join("\n", checks.Select(c => $"- {c.Name}: {(c.Pass ? "yes" : "NO")} - {c.Detail}")) + "\n";
    }

    /// <summary>Rig commands: planetdyn &lt;name&gt; makes matching grids dynamic (dampeners on); planetdesc &lt;name&gt; describes them.</summary>
    internal string PlanetDiag(Session session, string command)
    {
        string[] parts = command.Split(' ', 2);
        string name = parts.Length > 1 ? parts[1] : "";
        var grids = session.GetEntitiesOfType<CubeGridComponent>().Where(g => g.Get<CubeGridComponent>().DisplayName.ToString().Contains(name, StringComparison.OrdinalIgnoreCase)).ToList();
        switch (parts[0])
        {
        case "planetdyn":
            foreach (var g in grids) g.TryGet<GridPhysicsComponent>()?.SetGridMotion(false);
            Settle(grids, force: true);
            return grids.Count + " grids made dynamic";
        case "planetgrids":
            return string.Join("\n", grids.Select(g =>
            {
                var cube = g.Get<CubeGridComponent>();
                int blocks = 0, thrusters = 0, wheels = 0;
                cube.VisitAllBlocksWithComponent((Keen.Game2.Simulation.WorldObjects.CubeBlocks.CubeBlockComponent _) => blocks++);
                cube.VisitAllBlocksWithComponent((ThrusterComponent _) => thrusters++);
                cube.VisitAllBlocksWithComponent((Keen.Game2.Simulation.WorldObjects.CubeBlocks.MechanicalBlocks.Wheels.WheelCornerModuleComponent _) => wheels++);
                var at = g.Data.GetWorldTransform().Position;
                return $"{cube.DisplayName}: {blocks} blocks, {thrusters} thrusters, {wheels} wheels, static {g.TryGet<GridPhysicsComponent>()?.GridMotionType == BodyArgs.Motion.Static}, air {(g.Data.TryGet<AirData>(out var air) ? air.Density : -1):0.00}, owner {TeamPermissions.OwnerOf(g)} at ({at.X:0},{at.Y:0},{at.Z:0})";
            }));
        case "planetface":
        {
            // planetface <clientId> <sign> <grid>: turn that player's character to look at the grid's box centre (sign -1 flips).
            var words = name.Split(' ', 3);
            var body = CharacterOf(ushort.Parse(words[0]));
            if (body == null) return "no character";
            var me = body.Data.GetWorldTransform();
            var grid = session.GetEntitiesOfType<CubeGridComponent>().Where(g => g.Get<CubeGridComponent>().DisplayName.ToString().Contains(words[2], StringComparison.OrdinalIgnoreCase))
                .OrderBy(g => Vector3D.Distance(g.Data.GetWorldTransform().Position, me.Position)).FirstOrDefault();
            if (grid == null) return "no grid";
            var centre = WorldTransform.Transform((Vector3D)BoundingBoxData.Get(grid).Center, grid.Data.GetWorldTransform());
            var up = (Vector3)me.Orientation.GetUp();
            var look = (Vector3)(centre - me.Position);
            look -= up * Vector3.Dot(look, up);
            body.Data.Set(new WorldTransform(me.Position, Quaternion.CreateFromForwardUp(Vector3.Normalize(look) * float.Parse(words[1], System.Globalization.CultureInfo.InvariantCulture), up)));
            return $"turned; ship centre {Vector3D.Distance(in centre, in me.Position):0.0} m away";
        }
        case "planetalt":
            return string.Join("\n", grids.Select(g =>
            {
                var t = g.Data.GetWorldTransform();
                var planet = session.GetEntitiesOfType<VoxelPlanetComponent>().OrderBy(p => Vector3D.Distance(p.Data.GetWorldTransform().Position, t.Position)).FirstOrDefault();
                if (planet == null) return g.Get<CubeGridComponent>().DisplayName + ": no planet";
                var p = planet.Get<VoxelPlanetComponent>();
                var frame = planet.Data.GetWorldTransform();
                var local = WorldTransform.TransformInv(t.Position, frame);
                double ground = SurfaceRadius(p, (Vector3)Vector3D.Normalize(local));
                var radial = Vector3D.Normalize(t.Position - frame.Position);
                double tilt = Math.Acos(Math.Clamp(Vector3D.Dot(radial, (Vector3D)t.Orientation.GetUp()), -1, 1)) * 180 / Math.PI;
                var v = g.Data.TryGet<RigidBodyData>(out var body) ? body.LinearVelocity.Length() : -1;
                return $"{g.Get<CubeGridComponent>().DisplayName} on {p.Name}: origin {local.Length() - ground:0.0} m above ground, tilt {tilt:0.0} deg, speed {v:0.00} m/s, static {g.TryGet<GridPhysicsComponent>()?.GridMotionType == BodyArgs.Motion.Static}, owner {TeamPermissions.OwnerOf(g)}, dampeners {Keen.Game2.Simulation.WorldObjects.Movement.EntityMovementExtensions.GetDampenerState(g.Data)}";
            }));
        case "planetdesc":
            return string.Join("\n", grids.Select(g => DescribeGrid(session, g)));
        default:
            return "unknown: " + command;
        }
    }
}
