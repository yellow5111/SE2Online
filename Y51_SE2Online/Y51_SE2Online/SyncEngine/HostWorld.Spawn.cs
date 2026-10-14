using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Keen.Game2.Game.SessionComponents;
using Keen.Game2.Simulation.GameSystems.Encounters;
using Keen.Game2.Simulation.GameSystems.Ownership;
using Keen.Game2.Simulation.GameSystems.Player;
using Keen.Game2.Simulation.GameSystems.Respawn;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.Core.Game.Definitions;
using Keen.VRage.Core.Game.GameSystems.ProceduralGeneration;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.Components;
using Keen.VRage.DCS.ObjectBuilders;
using Keen.VRage.Library.Definitions;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Library.Utils;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

internal sealed partial class HostWorld
{
    private sealed record SpawnTarget(SpawnOption Option, Entity? Point, RespawnShip? Ship = null);

    private const long ShipSpawnTimeoutMs = 30_000;
    private readonly ConcurrentDictionary<long, long> _shipReadyAt = new();
    private readonly ConcurrentDictionary<ushort, RespawnShip> _shipChosen = new();

    private static OnlineSettings? ShipSettings => Dedicated.DedicatedRuntime.Config?.Network ?? (Dedicated.DedicatedRuntime.Headless ? null : MpMenu.Settings);

    private static long ShipCooldownMs => (ShipSettings?.RespawnShipCooldownMinutes ?? 10) * 60_000L;

    /// <summary>
    /// Spawns the joiner where they chose, or sends them the choices first. False when nothing spawned yet
    /// (menu sent, ship on its way, or the choice failed and the menu went out again).
    /// </summary>
    private bool ChosenSpawn(Session session, HostSession host, Joiner j, IRespawn respawn, IdentityId identity, Entity? hostCharacter, out Entity? character, out string how)
    {
        character = null; how = "";
        var menu = j.Menu;
        int choice = j.Choice;
        j.Choice = -1;
        if (menu == null || choice < 0 || choice >= menu.Count || !menu[choice].Option.Available)
        {
            SendSpawnMenu(session, host, j, identity, hostCharacter, "");
            return false;
        }
        var target = menu[choice];
        j.Menu = null;
        switch (target.Option.Kind)
        {
        case SpawnKind.NearHost when hostCharacter != null && respawn is RespawnSessionComponent component && hostCharacter.Data.TryGet<WorldTransform>(out var near):
        {
            Vector3 right = near.Orientation.GetRight();
            character = CreateAt(component, identity, new WorldTransform(near.Position + new Vector3D(right.X, right.Y, right.Z) * 1.5, near.Orientation));
            how = "next to the host's character";
            break;
        }
        case SpawnKind.Suit when respawn is RespawnSessionComponent component:
            character = CreateAt(component, identity, respawn.WorldSpawn);
            how = "the world spawn in a suit";
            break;
        case SpawnKind.Ship when target.Ship != null:
            if (!StartShip(session, j, respawn, target.Ship)) { SendSpawnMenu(session, host, j, identity, hostCharacter, $"The {target.Ship.Name} could not be spawned. Choose another spawn."); return false; }
            _log.Info($"host world sync: spawning a respawn ship ({target.Ship.Name}) for ClientId {j.ClientId} '{j.Name}'");
            return false;
        default:
            var point = target.Point;
            if (point != null && !EngineCodec.IsDead(point) && RespawnSessionComponent.IsFunctional(point))
            {
                var known = PerPlayer(session).GetPerPlayerData<RespawnPerPlayerData>(identity).KnownPoints;
                bool wasKnown = known.Contains(point);
                // The game only respawns at points the player claimed; a faction member's shared medbay is claimed for this spawn only.
                if (!wasKnown) known.Add(point);
                try { character = respawn.Respawn(identity, point); }
                finally { if (!wasKnown) known.Remove(point); }
            }
            how = target.Option.Name;
            break;
        }
        if (character == null)
        {
            SendSpawnMenu(session, host, j, identity, hostCharacter, target.Option.Name + " is blocked or gone. Choose another spawn.");
            return false;
        }
        return true;
    }

    private static InProcessPerPlayerDataSessionComponent PerPlayer(Session session) => session.SessionComponents.Get<InProcessPerPlayerDataSessionComponent>();

    private static Entity? CreateAt(RespawnSessionComponent component, IdentityId identity, WorldTransform at)
        => (Entity?)CreateCharacterMethod?.Invoke(component, new object?[] { identity, at, default(Vector3), null, null });

    private void SendSpawnMenu(Session session, HostSession host, Joiner j, IdentityId identity, Entity? hostCharacter, string message)
    {
        var targets = new List<SpawnTarget>();
        if (SpawnNearHost && hostCharacter != null) targets.Add(new(new SpawnOption(SpawnKind.NearHost, "Next to the host", "Spawn beside the host's character", true), null));
        var data = PerPlayer(session).GetPerPlayerData<RespawnPerPlayerData>(identity);
        Vector3D? death = j.Respawn ? PerPlayer(session).GetPerPlayerData<LastDeathPerPlayerData>(identity).LastDeathPoint.Position : null;
        var social = Social;
        long me = identity;
        var medbays = new List<(SpawnTarget Target, double Distance, bool Home)>();
        foreach (Entity point in session.GetEntitiesOfType<RespawnPointComponent>())
        {
            if (EngineCodec.IsDead(point) || !RespawnSessionComponent.IsFunctional(point)) continue;
            long owner = TeamPermissions.OwnerOf(point);
            bool home = data.HomePoint == point;
            SpawnKind kind;
            string ownerText;
            if (owner == me || data.KnownPoints.Contains(point)) { kind = SpawnKind.Medbay; ownerText = home ? "Home medbay" : "Your medbay"; }
            else if (owner == 0 || PlayerIdentities.IsNpc(owner) || social == null || WorldSocial.Access(point, me) != true) continue;
            else
            {
                bool faction;
                string name;
                lock (social.Sync)
                {
                    faction = social.State.FactionOf(me)?.Members.Contains(owner) == true;
                    name = social.State.Players.FirstOrDefault(p => p.Identity == owner)?.Name ?? "another player";
                }
                kind = faction ? SpawnKind.FactionMedbay : SpawnKind.SharedMedbay;
                ownerText = (faction ? "Faction medbay of " : "Shared by ") + name;
            }
            var transform = point.Data.GetWorldTransform();
            double distance = death is Vector3D at ? Vector3D.Distance(in at, in transform.Position) : -1;
            string label = PointName(point);
            medbays.Add((new(new SpawnOption(kind, label, ownerText + (distance >= 0 ? $", {Distance(distance)} from where you died" : ""), true), point), distance, home));
        }
        targets.AddRange(medbays.OrderByDescending(m => m.Home).ThenBy(m => m.Target.Option.Kind).ThenBy(m => m.Distance).Take(40).Select(m => m.Target));
        targets.Add(new(new SpawnOption(SpawnKind.Suit, "Space suit", "Spawn in your suit at the world spawn", true), null));
        long wait = _shipReadyAt.TryGetValue(me, out long ready) ? ready - MpClock.NowMs() : 0;
        foreach (var ship in RespawnShips.Offered(ShipSettings?.RespawnShips))
        {
            bool hasShip = Singleton<DefinitionManager>.Instance.TryGetDefinition(ship.Prefab, out PrefabDefinition _);
            string detail = !hasShip ? "Not available in this game version" : wait > 0 ? $"Available again in {(wait + 59_999) / 60_000} min" : ship.Detail + ", fuelled and yours to keep";
            targets.Add(new(new SpawnOption(SpawnKind.Ship, "Respawn ship: " + ship.Name, detail, hasShip && wait <= 0), null, ship));
        }
        j.Menu = targets;
        host.SendSpawnMenu(j.Peer, new SpawnMenu(targets.Select(t => t.Option).ToArray(), j.Respawn, message));
        _log.Info($"host world sync: sent ClientId {j.ClientId} '{j.Name}' {targets.Count} spawn choices ({medbays.Count} medbays){(message.Length > 0 ? ": " + message : "")}");
    }

    private static string Distance(double metres) => metres >= 10_000 ? $"{metres / 1000:0} km" : metres >= 1000 ? $"{metres / 1000:0.0} km" : $"{metres:0} m";

    private static string PointName(Entity point)
    {
        string name = null;
        try { name = point.Get<RespawnPointComponent>().DisplayName.EvaluateText(); } catch (Exception) { }
        if (string.IsNullOrWhiteSpace(name) || name.Contains('{')) name = "Medbay";
        string grid = WorldSocial.GridOf(point)?.TryGet<CubeGridComponent>()?.DisplayName.ToString();
        return string.IsNullOrWhiteSpace(grid) ? name : name + " on " + grid;
    }

    private bool StartShip(Session session, Joiner j, IRespawn respawn, RespawnShip ship)
    {
        var encounters = session.SessionComponents.TryGet<EncounterSessionComponent>();
        if (encounters == null || !Singleton<DefinitionManager>.Instance.TryGetDefinition(ship.Prefab, out PrefabDefinition prefab)) return false;
        _shipChosen[j.ClientId] = ship;
        var spawn = respawn.WorldSpawn;
        var up = WorldTransform.TransformDirection(new Vector3D(0, 1, 0), spawn);
        var side = WorldTransform.TransformDirection(new Vector3D(Random.Shared.NextDouble() - 0.5, 0, Random.Shared.NextDouble() - 0.5), spawn);
        if (side.LengthSquared() < 1e-6) side = WorldTransform.TransformDirection(new Vector3D(1, 0, 0), spawn);
        side.Normalize();
        j.ShipRoots = null;
        j.ShipDeadline = MpClock.NowMs() + ShipSpawnTimeoutMs;
        j.ShipPending = true;
        SpawnShip(session, encounters, prefab, new WorldTransform(spawn.Position + up * 150 + side * 150, spawn.Orientation), j);
        return true;
    }

    private async void SpawnShip(Session session, EncounterSessionComponent encounters, PrefabDefinition prefab, WorldTransform at, Joiner j)
    {
        try { j.ShipRoots = await SpawnEncounter(session, encounters, prefab, at); }
        catch (Exception ex)
        {
            _log.Warn("host world sync: spawning the respawn ship failed: " + ex.Message);
            j.ShipRoots = Array.Empty<Entity>();
        }
    }

    // The game's own encounter spawn (EncounterSessionComponent.DebugSpawnEncounter without the admin story).
    private static async Keen.VRage.Library.Threading.Task<Entity[]> SpawnEncounter(Session session, EncounterSessionComponent encounters, PrefabDefinition prefab, WorldTransform at)
    {
        await Keen.VRage.Library.Threading.Task.SetLifetime(encounters);
        var context = new EncounterBuildContext(prefab) { Random = new Random(), PostProcessor = encounters._encounters, Anchor = at };
        await encounters.GenerateEncounter(context);
        var generated = context.Build();
        await session.Scene.MoveToDCS<EncounterUpdate.SpawnEncounters>();
        return await encounters.SpawnEncounter(generated);
    }

    /// <summary>
    /// What the game does to keep an encounter (EncounterHelper.TurnManual): the spawners let go of the grids and they stop
    /// being procedural; deleting the encounter root then removes only the cargo route, not the ship.
    /// </summary>
    private static List<Entity> ReleaseEncounter(Session session, Entity[] roots)
    {
        var ship = new List<Entity>();
        foreach (var spawner in EncounterHelper.EnumerateEntities<EncounterSpawnerComponent>(roots, null).ToList())
            foreach (var e in spawner.SpawnedEntities.ToList()) { spawner.SpawnedEntities.Remove(e); e.Data.TryRemove<ProcedurallyGeneratedTag>(); ship.Add(e); }
        foreach (var root in roots) if (!EngineCodec.IsDead(root)) session.Get<IEntitySpawner>().DeleteEntity(root);
        return ship;
    }

    /// <summary>Rig probe: spawns every candidate ship in a row 30 km from the world spawn for <see cref="ShipCheck"/> to inspect.</summary>
    internal static async void ProbeShips(Session session)
    {
        var encounters = session.SessionComponents.TryGet<EncounterSessionComponent>();
        var origin = (session.TryGet<IRespawn>()?.WorldSpawn.Position ?? default) + new Vector3D(30000, 0, 0);
        ShipCheck.Probed.Clear();
        for (int i = 0; i < RespawnShips.Candidates.Length; i++)
        {
            var ship = RespawnShips.Candidates[i];
            try
            {
                if (encounters == null || !Singleton<DefinitionManager>.Instance.TryGetDefinition(ship.Prefab, out PrefabDefinition prefab)) { ShipCheck.Probed[ship.Key] = new(); continue; }
                var roots = await SpawnEncounter(session, encounters, prefab, new WorldTransform(origin + new Vector3D(i * 3000, 0, 0), Quaternion.Identity));
                var grids = ReleaseEncounter(session, roots).Where(e => e.TryGet<CubeGridComponent>() != null).ToList();
                Settle(grids);
                ShipCheck.Probed[ship.Key] = grids;
            }
            catch (Exception ex)
            {
                GameLog.Instance.Warn("ship probe: " + ship.Key + " failed: " + ex.Message);
                ShipCheck.Probed[ship.Key] = new();
            }
        }
    }

    private static readonly Type? CargoSpeed = typeof(Keen.Game2.Simulation.GameSystems.Encounters.CargoShips.CargoShipEncounterComponent).GetNestedType("TargetSpeedData", System.Reflection.BindingFlags.NonPublic);

    private static readonly Lazy<System.Reflection.MethodInfo?> RemoveCargoSpeed = new(() =>
    {
        try { return CargoSpeed == null ? null : typeof(Keen.VRage.DCS.Accessors.DEntityContext).GetMethods().FirstOrDefault(m => m.Name == "TryRemove" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0)?.MakeGenericMethod(CargoSpeed); }
        catch (Exception) { return null; }
    });

    /// <summary>
    /// Undoes the cargo route (dampeners off, thrusters held at a target speed) so the ship holds still for its new owner.
    /// The route starts asynchronously after the spawn, so this runs for a few seconds after the takeover.
    /// </summary>
    internal static void Settle(IEnumerable<Entity> grids)
    {
        foreach (var grid in grids)
        {
            if (EngineCodec.IsDead(grid) || grid.TryGet<CubeGridComponent>() is not { } cube) continue;
            cube.VisitAllBlocksWithComponent((Keen.Game2.Simulation.WorldObjects.CubeBlocks.Movement.ThrusterComponent t) =>
            {
                object data = t.Data;
                try { RemoveCargoSpeed.Value?.Invoke(data, null); } catch (Exception) { }
            });
            if (Keen.Game2.Simulation.WorldObjects.Movement.EntityMovementExtensions.GetDampenerState(grid.Data) == Keen.Game2.Simulation.WorldObjects.Movement.DampenerState.Off)
                Keen.Game2.Simulation.WorldObjects.Movement.EntityMovementExtensions.ToggleDampeners(grid.Data);
        }
    }

    /// <summary>Server thread, every tick while a joiner's respawn ship is spawning or settling.</summary>
    private void ServerShip(Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent server, Joiner j)
    {
        if (j.ShipSettle is { } settling)
        {
            Settle(settling);
            if (MpClock.NowMs() > j.ShipSettleUntil) j.ShipSettle = null;
            return;
        }
        var host = _host;
        if (host == null || !j.ShipPending || !_joiners.TryGetValue(j.ClientId, out var current) || current != j) return;
        var roots = j.ShipRoots;
        if (roots == null && MpClock.NowMs() < j.ShipDeadline) return;
        j.ShipPending = false;
        var session = server.Session;
        IdentityId identity = IdentityFor(j.ClientId);
        var ship = new List<Entity>();
        try
        {
            if (roots != null)
            {
                ship = ReleaseEncounter(session, roots);
                foreach (var grid in ship.Where(e => e.TryGet<CubeGridComponent>() != null && e.TryGet<OwnershipComponent>() != null))
                {
                    WorldSocial.SetOwner(grid, (long)identity);
                    WorldSocial.SetSharing(grid, GridSharing.Private);
                }
            }
        }
        catch (Exception ex) { _log.Warn("host world sync: taking over the respawn ship failed: " + ex); }
        var main = ship.Where(e => e.TryGet<CubeGridComponent>() != null && e.Data.HasTransform()).FirstOrDefault();
        var respawn = session.TryGet<IRespawn>() as RespawnSessionComponent;
        if (main == null || respawn == null)
        {
            SendSpawnMenu(session, host, j, identity, null, "The respawn ship could not be spawned. Choose another spawn.");
            return;
        }
        var grids = ship.Where(e => e.TryGet<CubeGridComponent>() != null).ToList();
        Settle(grids);
        _shipChosen.TryRemove(j.ClientId, out var chosen);
        try
        {
            var topped = ShipCheck.TopUp(grids);
            var failed = ShipCheck.Inspect(grids).Where(c => !c.Pass).Select(c => c.Name + " (" + c.Detail + ")").ToList();
            _log.Info($"host world sync: respawn ship {chosen?.Name} for ClientId {j.ClientId}: {topped.Count} refuels{(failed.Count == 0 ? ", survival checklist passed" : "; checklist FAILED: " + string.Join("; ", failed))}");
        }
        catch (Exception ex) { _log.Warn("host world sync: checking the respawn ship failed: " + ex.Message); }
        j.ShipSettle = grids;
        j.ShipSettleUntil = MpClock.NowMs() + 8000;
        var at = main.Data.GetWorldTransform();
        var up = WorldTransform.TransformDirection(new Vector3D(0, 1, 0), at);
        var character = CreateAt(respawn, identity, new WorldTransform(at.Position + up * 12, at.Orientation));
        if (character != null) _shipReadyAt[identity] = MpClock.NowMs() + ShipCooldownMs;
        Assign(host, j, character, identity, null, "a respawn ship (" + ship.Count + " entities)");
    }
}
