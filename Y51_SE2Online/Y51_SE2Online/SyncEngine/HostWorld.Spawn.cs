using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Keen.Game2.Simulation.GameSystems.Encounters;
using Keen.Game2.Simulation.GameSystems.Identities;
using Keen.Game2.Simulation.GameSystems.Ownership;
using Keen.Game2.Simulation.GameSystems.Player;
using Keen.Game2.Simulation.GameSystems.Respawn;
using Keen.Game2.Simulation.WorldObjects.Characters;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.Pilotable;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.Core.Game.Definitions;
using Keen.VRage.Core.Game.GameSystems.ProceduralGeneration;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Definitions;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Library.Utils;
using Y51_SE2Online.Core;
using Y51_SE2Online.KeenNetcode;
using Y51_SE2Online.Net;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// SE2 Online's spawn menu on SE2 4.0: a player without a character (first join or after death) asks the host, which
/// answers with its medbays, faction and shared medbays, the suit, respawn ships, planet landers and "next to the host",
/// then creates the chosen character with Keen's respawn system. The player's client possesses it (<see cref="KeenRespawn"/>).
/// </summary>
internal sealed partial class HostWorld
{
    private sealed record SpawnTarget(SpawnOption Option, Entity? Point, RespawnShip? Ship = null, Guid Planet = default);

    private readonly record struct SpawnRequest(ushort ClientId, PeerId? Peer, int Choice, bool Respawn, long AtMs);

    // One per client, the latest wins: a client can send requests faster than identities arrive.
    private readonly ConcurrentDictionary<ushort, SpawnRequest> _spawnRequests = new();
    private const long SpawnRequestTimeoutMs = 30_000;
    private readonly ConcurrentQueue<ushort> _possessed = new();
    private Joiner? _localJoiner;

    private const long ShipSpawnTimeoutMs = 30_000;
    // A client that never reports its possession (it crashed or left) still gets its pilot seated after this.
    private const long SeatAckTimeoutMs = 20_000;
    private readonly ConcurrentDictionary<long, long> _shipReadyAt = new();
    private readonly ConcurrentDictionary<ushort, RespawnShip> _shipChosen = new();

    private static OnlineSettings? ShipSettings => Dedicated.DedicatedRuntime.Config?.Network ?? (Dedicated.DedicatedRuntime.Headless ? null : MpMenu.Settings);

    private static long ShipCooldownMs => (ShipSettings?.RespawnShipCooldownMinutes ?? 10) * 60_000L;

    // Protected virtual, so not publicized; the delegate still calls the seat's override.
    private static readonly Action<SeatComponent, Entity> SetPilotInternal = HarmonyLib.AccessTools.MethodDelegate<Action<SeatComponent, Entity>>(HarmonyLib.AccessTools.Method(typeof(SeatComponent), "SetPilotInternal"));

    /// <summary>Net thread: a player asks for its spawn menu (choice -1) or picks an entry of the last one.</summary>
    public void RequestCharacter(PeerId client, ushort clientId, string name, int choice, bool respawn) => _spawnRequests[clientId] = new(clientId, client, choice, respawn, MpClock.NowMs());

    public void ReleaseCharacter(PeerId client, ushort clientId) => _spawnRequests.TryRemove(clientId, out _);

    /// <summary>The listen host's own client, the same request without the network.</summary>
    internal void RequestLocalCharacter(int choice, bool respawn) => _spawnRequests[SyncProtocol.HostClientId] = new(SyncProtocol.HostClientId, null, choice, respawn, MpClock.NowMs());

    /// <summary>The player's client possesses the character it was given; a respawn ship's pilot can sit down now.</summary>
    public void OnPossessed(ushort clientId) => _possessed.Enqueue(clientId);

    internal void OnLocalPossessed() => _possessed.Enqueue(0);

    private Joiner LocalJoiner => _localJoiner ??= new Joiner { ClientId = SyncProtocol.HostClientId, Name = "host", Local = true };

    private long SpawnIdentity(Joiner j, IdentitiesSessionComponent? identities) => j.Local ? identities != null ? (long)identities.LocalIdentity : 0 : IdentityOf(j.ClientId);

    /// <summary>Server thread, every tick: menu requests and choices, respawn ships on their way, pilots to seat.</summary>
    private void UpdateSpawns(Session session, HostSession host, IdentitiesSessionComponent? identities)
    {
#if !PULSAR
        RigShipProbe(session);
#endif
        foreach (var (clientId, r) in _spawnRequests)
        {
            var j = r.Peer is PeerId peer ? _joiners.GetOrAdd(r.ClientId, id => new Joiner { ClientId = id, Name = "?" }) : LocalJoiner;
            if (r.Peer is PeerId p) j.Peer = p;
            long identity = SpawnIdentity(j, identities);
            // Keen names the player's identity when its client asks for the world; a request can overtake that by a tick.
            if (identity == 0 && MpClock.NowMs() - r.AtMs < SpawnRequestTimeoutMs) continue;
            // A newer request replaced this one since the loop started: it is handled next tick.
            if (!_spawnRequests.TryRemove(new KeyValuePair<ushort, SpawnRequest>(clientId, r))) continue;
            if (identity == 0) { _log.Warn($"host spawn: ClientId {j.ClientId} has no identity after {SpawnRequestTimeoutMs / 1000} s; its spawn request is dropped"); continue; }
            j.Respawn = r.Respawn;
            try { ServerSpawn(session, host, j, identity, r.Choice); }
            catch (Exception ex)
            {
                _log.Error($"host spawn: spawning a character for ClientId {j.ClientId} '{j.Name}' failed: {ex}");
                SpawnFailed(host, j, ex.GetType().Name + ": " + ex.Message);
            }
        }
        DeleteLeftovers(session);
        while (_possessed.TryDequeue(out ushort clientId))
        {
            var j = clientId == 0 ? _localJoiner : _joiners.TryGetValue(clientId, out var found) ? found : null;
            if (j != null) j.Possessed = true;
        }
        foreach (var j in _joiners.Values) ServerShip(session, host, j, identities);
        if (_localJoiner != null) ServerShip(session, host, _localJoiner, identities);
    }

    private static Entity? LivingCharacterOf(Session session, long identity)
    {
        foreach (Entity character in session.GetEntitiesOfType<CharacterLifeComponent>())
            if (character.Get<CharacterLifeComponent>().IsAlive && TeamPermissions.OwnerOf(character) == identity) return character;
        return null;
    }

    private void ServerSpawn(Session session, HostSession host, Joiner j, long identity, int choice)
    {
        if (j.ShipPending) return;
        if (LivingCharacterOf(session, identity) is { } existing)
        {
            SpawnAssigned(host, j, existing, "its existing character");
            return;
        }
        if (session.TryGet<IRespawn>() is not RespawnSessionComponent respawn)
        {
            SpawnFailed(host, j, "the world has no respawn service (IRespawn)");
            return;
        }
        if (ChosenSpawn(session, host, j, respawn, new IdentityId(identity), choice, out var character, out string how)) SpawnAssigned(host, j, character!, how);
    }

    private void SpawnAssigned(HostSession host, Joiner j, Entity character, string how)
    {
        var at = character.Data.GetWorldTransform().Position;
        string where = $"at {at.X:0.0}, {at.Y:0.0}, {at.Z:0.0}";
        _log.Info($"host spawn: SPAWNED character for ClientId {j.ClientId} '{j.Name}'{(j.Local ? " (the listen host)" : "")}, owner IdentityId {TeamPermissions.OwnerOf(character)}, via {how}, {where}");
        // SE2 4.0 streams each client only what is around its observer; Keen's own respawn moves the observer to the new
        // body before the hand-off. A respawn ship or planet lander is made here instead, far from where the player died,
        // and the client never received the body it was given.
        try
        {
            MpPlugin.App?.ServerSession?.SessionComponents.TryGet<Keen.Game2.Simulation.GameSystems.SceneSubset.SceneSubsetObserverSessionComponent>()
                ?.OnPlayerBodyCreated(new IdentityId(TeamPermissions.OwnerOf(character)), character);
        }
        catch (Exception ex) { _log.WarnOnce("spawn-observer", $"host spawn: moving the player's streaming area to the new body failed ({ex.GetType().Name}: {ex.Message})"); }
        if (j.Local) KeenRespawn.ShowLocal(null);
        else host.CharacterAssigned(j.Peer, _ids?.GetOrAssign(character) ?? 0, "spawned via " + how + ", " + where);
    }

    private void SpawnFailed(HostSession host, Joiner j, string why)
    {
        if (j.Local) KeenRespawn.LocalFailed(why);
        else host.CharacterFailed(j.Peer, why);
    }

    /// <summary>
    /// Spawns the player where it chose, or sends it the choices first. False when nothing spawned yet
    /// (menu sent, ship on its way, or the choice failed and the menu went out again).
    /// </summary>
    private bool ChosenSpawn(Session session, HostSession host, Joiner j, RespawnSessionComponent respawn, IdentityId identity, int choice, out Entity? character, out string how)
    {
        character = null; how = "";
        var menu = j.Menu;
        var hostCharacter = j.Local ? null : _hostCharacterServer;
        if (menu == null || choice < 0 || choice >= menu.Count || !menu[choice].Option.Available)
        {
            SendSpawnMenu(session, host, j, identity, hostCharacter, "");
            return false;
        }
        var target = menu[choice];
        j.Menu = null;
        switch (target.Option.Kind)
        {
        case SpawnKind.NearHost when hostCharacter != null && !EngineCodec.IsDead(hostCharacter):
        {
            var near = hostCharacter.Data.GetWorldTransform();
            Vector3 right = near.Orientation.GetRight();
            character = respawn.CreateCharacterAtPoint(identity, new WorldTransform(near.Position + new Vector3D(right.X, right.Y, right.Z) * 1.5, near.Orientation));
            how = "next to the host's character";
            break;
        }
        case SpawnKind.Suit:
            character = respawn.CreateCharacterAtWorldSpawn(identity, null);
            how = "the world spawn in a suit";
            break;
        case SpawnKind.Ship when target.Ship != null:
            if (!StartShipOrLander(session, j, respawn, target)) { SendSpawnMenu(session, host, j, identity, hostCharacter, $"The {target.Ship.Name} could not be spawned. Choose another spawn."); return false; }
            _log.Info($"host spawn: spawning a respawn ship ({target.Option.Name}) for ClientId {j.ClientId} '{j.Name}'");
            return false;
        default:
            var point = target.Point;
            if (point != null && !EngineCodec.IsDead(point) && RespawnSessionComponent.IsFunctional(point))
            {
                var known = session.Get<IPerPlayerData>().GetPerPlayerData<RespawnPerPlayerData>(identity).KnownPoints;
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

    private void SendSpawnMenu(Session session, HostSession host, Joiner j, IdentityId identity, Entity? hostCharacter, string message)
    {
        var targets = new List<SpawnTarget>();
        if (SpawnNearHost && hostCharacter != null) targets.Add(new(new SpawnOption(SpawnKind.NearHost, "Next to the host", "Spawn beside the host's character", true), null));
        var perPlayer = session.Get<IPerPlayerData>();
        var data = perPlayer.GetPerPlayerData<RespawnPerPlayerData>(identity);
        Vector3D? death = j.Respawn ? perPlayer.GetPerPlayerData<LastDeathPerPlayerData>(identity).LastDeathPoint.Position : null;
        var social = Social;
        long me = identity;
#if !PULSAR
        RigSocial(session, social, me);
#endif
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
            medbays.Add((new(new SpawnOption(kind, PointName(point), ownerText + (distance >= 0 ? $", {Distance(distance)} from where you died" : ""), true), point), distance, home));
        }
        targets.AddRange(medbays.OrderByDescending(m => m.Home).ThenBy(m => m.Target.Option.Kind).ThenBy(m => m.Distance).Take(40).Select(m => m.Target));
        targets.Add(new(new SpawnOption(SpawnKind.Suit, "Space suit", "Spawn in your suit at the world spawn", true), null));
        long wait = _shipReadyAt.TryGetValue(me, out long ready) ? ready - MpClock.NowMs() : 0;
        foreach (var ship in RespawnShips.Offered(ShipSettings?.RespawnShips).Where(s => s.Space))
        {
            bool hasShip = Singleton<DefinitionManager>.Instance.TryGetDefinition(ship.Prefab, out PrefabDefinition _);
            string detail = !hasShip ? "Not available in this game version" : wait > 0 ? $"Available again in {(wait + 59_999) / 60_000} min" : ship.Detail + ", fuelled and yours to keep";
            targets.Add(new(new SpawnOption(SpawnKind.Ship, "Respawn ship: " + ship.Name, detail, hasShip && wait <= 0), null, ship));
        }
        AddPlanetShips(session, targets, wait);
        j.Menu = targets;
        var menu = new SpawnMenu(targets.Select(t => t.Option).ToArray(), j.Respawn, message);
        if (j.Local) KeenRespawn.ShowLocal(menu);
        else host.SendSpawnMenu(j.Peer, menu);
        _log.Info($"host spawn: sent ClientId {j.ClientId} '{j.Name}' {targets.Count} spawn choices ({medbays.Count} medbays){(message.Length > 0 ? ": " + message : "")}");
    }

    private static string Distance(double metres) => metres >= 10_000 ? $"{metres / 1000:0} km" : metres >= 1000 ? $"{metres / 1000:0.0} km" : $"{metres:0} m";

    private static string PointName(Entity point)
    {
        string? name = null;
        try { name = point.Get<RespawnPointComponent>().DisplayName.EvaluateText(); } catch (Exception) { }
        if (string.IsNullOrWhiteSpace(name) || name.Contains('{')) name = "Medbay";
        string? grid = WorldSocial.GridOf(point)?.TryGet<CubeGridComponent>()?.DisplayName.ToString();
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
        SpawnShip(session, encounters, prefab, new WorldTransform(OpenSpace(session, spawn.Position + up * 150 + side * 150), spawn.Orientation), j);
        return true;
    }

    private async void SpawnShip(Session session, EncounterSessionComponent encounters, PrefabDefinition prefab, WorldTransform at, Joiner j)
    {
        try { j.ShipRoots = await SpawnEncounter(session, encounters, prefab, at); }
        catch (Exception ex)
        {
            _log.Warn("host spawn: spawning the respawn ship failed: " + ex.Message);
            j.ShipRoots = Array.Empty<Entity>();
        }
    }

    // The game's own encounter spawn (EncounterSessionComponent.DebugSpawnEncounter without the admin story).
    internal static async Keen.VRage.Library.Threading.Task<Entity[]> SpawnEncounter(Session session, EncounterSessionComponent encounters, PrefabDefinition prefab, WorldTransform at, bool missions = true)
    {
        await Keen.VRage.Library.Threading.Task.SetLifetime(encounters);
        // MissionEnabled off: a contract prefab (the planet landers) spawns its grids only, never a contract or mission triggers.
        var context = new EncounterBuildContext(prefab) { Random = new Random(), PostProcessor = encounters._encounters, Anchor = at, MissionEnabled = missions };
        long t0 = MpClock.NowMs();
        await encounters.GenerateEncounter(context);
        var generated = context.Build();
        long t1 = MpClock.NowMs();
        await session.Scene.MoveToDCS<EncounterUpdate.SpawnEncounters>();
        long t2 = MpClock.NowMs();
        // A planet lander comes out of a bounty contract prefab; the game's SpawnEncounter also waits for the contract's
        // spawners (its enemies, ~11 s) although only the ship is kept and the rest is deleted, so the bundle is spawned
        // on its own and the player gets the ship as soon as it exists.
        var roots = missions ? await encounters.SpawnEncounter(generated) : await session.Get<IEntitySpawner>().SpawnBundleAsync(generated.Bundle);
        GameLog.Instance.Info($"host spawn: encounter '{prefab}' generated in {t1 - t0} ms, spawn slot after {t2 - t1} ms, spawned in {MpClock.NowMs() - t2} ms");
        return roots;
    }

    /// <summary>
    /// What the game does to keep an encounter (EncounterHelper.TurnManual): the spawners let go of the grids and they stop
    /// being procedural; deleting the encounter root then removes only the cargo route, not the ship.
    /// </summary>
    internal static List<Entity> ReleaseEncounter(Session session, Entity[] roots)
    {
        var ship = new List<Entity>();
        foreach (var spawner in EncounterHelper.EnumerateEntities<EncounterSpawnerComponent>(roots, null).ToList())
            foreach (var e in spawner.SpawnedEntities.ToList()) { spawner.SpawnedEntities.Remove(e); e.Data.TryRemove<ProcedurallyGeneratedTag>(); ship.Add(e); }
        foreach (var root in roots) DeleteLater(root);
        return ship;
    }

    // Deleting an entity a frame after it spawned made Keen's replication skip the transport units that still named it, so a
    // client missed what else they held (an antenna's map marker) and failed on its later removal, killing the dedicated
    // server with the faulted story. Leftovers of a spawn are deleted once their creation has gone out.
    private const long DeleteDelayMs = 10_000, SettleMs = 3_000;
    private static readonly ConcurrentQueue<(Entity Entity, long DueMs)> Leftovers = new();

    /// <summary>
    /// The contract's spawners have made the ship's grids and every connected client has confirmed the batch that created
    /// them. A lander comes out of a bounty contract whose spawner makes the grids after the bundle; the game's own spawn
    /// waited ~11 s for every spawner (the contract's enemies too). Deleting the leftovers before a client confirmed them
    /// left it logging its grid error every frame until it crashed, so the hand-over waits for exactly this.
    /// </summary>
    private static bool CreationConfirmed(Session session, Entity[] roots)
    {
        var grids = EncounterHelper.EnumerateEntities<EncounterSpawnerComponent>(roots, null)
            .SelectMany(spawner => spawner.SpawnedEntities).Where(e => !EngineCodec.IsDead(e) && e.TryGet<CubeGridComponent>() != null).ToList();
        if (grids.Count == 0) return false;
        if (session.SessionComponents.TryGet<Keen.VRage.Multiplayer.Replications.ReplicationServerSessionComponent>() is not { } replication) return true;
        // With streaming on, a client outside a grid's range is never sent it: it has nothing to confirm or to build.
        var subset = session.SessionComponents.TryGet<Keen.VRage.Multiplayer.Replications.SceneSubset.SceneSubsetServerSessionComponent>() is { EnforcementEnabled: true } enforced ? enforced : null;
        foreach (var (client, transport) in replication.ClientPeers)
        {
            var tracker = (transport as Keen.VRage.Multiplayer.Transport.OutOfProcess.Server.OutOfProcessServerPeerTransport)?._tracker;
            foreach (var grid in grids)
            {
                if (subset != null && !subset.IsInSubset(client, grid.Data.GetTopLevelParent().Entity)) continue;
                // Confirmed: sent to that client and its batch acknowledged (before the first send it is neither).
                if (!transport.IsReplicated(grid) || tracker != null && tracker.IsPendingReplication(grid)) return false;
            }
        }
        return true;
    }

    private static void DeleteLater(Entity entity) => Leftovers.Enqueue((entity, MpClock.NowMs() + DeleteDelayMs));

    private static void DeleteLeftovers(Session session)
    {
        while (Leftovers.TryPeek(out var next) && MpClock.NowMs() >= next.DueMs && Leftovers.TryDequeue(out next))
            if (!EngineCodec.IsDead(next.Entity)) session.Get<IEntitySpawner>().DeleteEntity(next.Entity);
    }

    /// <summary>A respawn ship's free working cockpit, on its first (main) grid first.</summary>
    private static SeatComponent? RespawnCockpit(IEnumerable<Entity> grids)
    {
        foreach (var grid in grids)
        {
            if (EngineCodec.IsDead(grid) || grid.TryGet<CubeGridComponent>() is not { } cube) continue;
            SeatComponent? found = null;
            cube.VisitAllBlocksWithComponent((SeatComponent s) => { if (found == null && s.IsFunctional && s.Pilot == null && s.Entity.TryGet<CockpitComponent>() != null) found = s; }, includeSubgrids: false);
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>Server thread, every tick for every player: its respawn ship spawning, settling, and its pilot sitting down.</summary>
    private void ServerShip(Session session, HostSession host, Joiner j, IdentitiesSessionComponent? identities)
    {
        long now = MpClock.NowMs();
        if (j.ShipSettle is { } settling)
        {
            Settle(settling);
            bool done = now > j.ShipSettleUntil;
            HoldPins(j, done);
            if (done) j.ShipSettle = null;
        }
        if (j.SeatTarget != null && (j.Possessed || now > j.SeatDeadline)) SeatRespawnPilot(j);
        if (!j.ShipPending) return;
        var roots = j.ShipRoots;
        if (roots == null && now < j.ShipDeadline) return;
        // A lander's contract stays where it spawned until its creation has reached every player (see DeleteLater).
        if (roots != null && _planetSites.TryGetValue(j, out var site))
        {
            if (site.SpawnedMs == 0) site.SpawnedMs = now;
            // Confirmed by every client, and settled: moving the lander or switching its motion within a second of spawning
            // raced the physics on both ends (the host's shared-motion link to the contract pad, a client's fresh body).
            if (now < site.SpawnedMs + DeleteDelayMs && (now < site.SpawnedMs + SettleMs || !CreationConfirmed(session, roots))) return;
            _log.Info($"host spawn: lander handed over {now - site.SpawnedMs} ms after spawning ({(CreationConfirmed(session, roots) ? "its grids reached every client" : "deadline: its grids had not reached every client")})");
        }
        j.ShipPending = false;
        var identity = new IdentityId(SpawnIdentity(j, identities));
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
        catch (Exception ex) { _log.Warn("host spawn: taking over the respawn ship failed: " + ex); }
        ship = PlaceOnPlanet(session, j, ship, out var beside);
        var main = ship.Where(e => e.TryGet<CubeGridComponent>() != null && e.Data.HasTransform()).FirstOrDefault();
        if (main == null || session.TryGet<IRespawn>() is not RespawnSessionComponent respawn)
        {
            SendSpawnMenu(session, host, j, identity, j.Local ? null : _hostCharacterServer, "The respawn ship could not be spawned. Choose another spawn.");
            return;
        }
        var grids = ship.Where(e => e.TryGet<CubeGridComponent>() != null).ToList();
        Settle(grids, force: true);
        _shipChosen.TryRemove(j.ClientId, out var chosen);
        try
        {
            var topped = ShipCheck.TopUp(grids);
            var failed = ShipCheck.Inspect(grids).Where(c => !c.Pass).Select(c => c.Name + " (" + c.Detail + ")").ToList();
            _log.Info($"host spawn: respawn ship {chosen?.Name} for ClientId {j.ClientId} owned by IdentityId {(long)identity}: {grids.Count} grids, {topped.Count} refuels{(failed.Count == 0 ? ", survival checklist passed" : "; checklist FAILED: " + string.Join("; ", failed))}");
        }
        catch (Exception ex) { _log.Warn("host spawn: checking the respawn ship failed: " + ex.Message); }
        j.ShipSettle = grids;
        j.ShipSettleUntil = now + 8000;
        var at = main.Data.GetWorldTransform();
        var up = WorldTransform.TransformDirection(new Vector3D(0, 1, 0), at);
        var character = respawn.CreateCharacterAtPoint(identity, beside ?? new WorldTransform(at.Position + up * 12, at.Orientation));
        if (character == null)
        {
            SpawnFailed(host, j, "creating the respawn ship's pilot returned nothing");
            return;
        }
        _shipReadyAt[(long)identity] = now + ShipCooldownMs;
        if (RespawnCockpit(grids.OrderBy(g => g != main)) is { } cockpit)
        {
            j.SeatTarget = cockpit.Entity;
            j.SeatBody = character;
            j.SeatDeadline = now + SeatAckTimeoutMs;
            j.Possessed = false;
        }
        SpawnAssigned(host, j, character, $"a respawn ship ({chosen?.Name}, {ship.Count} entities)");
    }

    /// <summary>
    /// SeatComponent.SetPilotStory's server half; every client is told. Runs once the player's client possesses the new
    /// character, so its cockpit takes the control over (CockpitControllableComponent.PossessPilot). The seat keeps the
    /// pilot's offset from it as the spot it stands up at, so the pilot is put in front of the seat first.
    /// </summary>
    private void SeatRespawnPilot(Joiner j)
    {
        var seatEntity = j.SeatTarget!;
        var body = j.SeatBody;
        j.SeatTarget = j.SeatBody = null;
        var seat = EngineCodec.IsDead(seatEntity) ? null : seatEntity.TryGet<SeatComponent>();
        if (body == null || EngineCodec.IsDead(body) || seat == null || !seat.CanSeat(body) || body.AsInterface<ISeatable>()?.CurrentSeat != null)
        {
            _log.Info($"host spawn: ClientId {j.ClientId} '{j.Name}' stays beside its respawn ship (its cockpit is taken, broken or gone)");
            return;
        }
        var block = seatEntity.Data.GetWorldTransform();
        body.Data.Teleport(new WorldTransform(block.Position + (Vector3D)block.Orientation.GetForward() * 1.5, block.Orientation));
        SetPilotInternal(seat, body);
        seat.BroadcastSetPilot(body);
        _log.Info($"host spawn: ClientId {j.ClientId} '{j.Name}' seated in its respawn ship's {seatEntity.GetComposition()?.DebugNameShort} ({(j.Possessed ? "its client possesses the pilot" : "no possession report in time")})");
    }

#if !PULSAR
    private bool _rigProbeStarted, _rigSpawnMoved;

    /// <summary>
    /// Rig (-y51online:spawnsocial:faction|share): before a player's spawn menu, every other player who owns a medbay
    /// shares its grids with that player, through one faction (faction) or with everyone (share).
    /// </summary>
    private void RigSocial(Session session, WorldSocial? social, long me)
    {
        string? mode = MpPlugin.LaunchArgs.FirstOrDefault(a => a.StartsWith(MpOptions.Prefix + "spawnsocial:", StringComparison.OrdinalIgnoreCase))?.Substring(MpOptions.Prefix.Length + "spawnsocial:".Length);
        if (mode == null || social == null) return;
        var owners = session.GetEntitiesOfType<RespawnPointComponent>().Select(TeamPermissions.OwnerOf).Where(o => o != me && PlayerIdentities.IsHuman(o)).Distinct().ToList();
        foreach (long other in owners)
        {
            var results = new List<string>();
            bool faction = mode.Equals("faction", StringComparison.OrdinalIgnoreCase);
            lock (social.Sync)
            {
                if (faction && social.State.FactionOf(me) == null)
                {
                    if (social.State.FactionOf(other) == null) results.Add(social.State.Apply(other, new SocialRequest(SocialAction.CreateFaction, Name: "Rig Faction", Tag: "RIG")));
                    string id = social.State.FactionOf(other)?.Id ?? "";
                    results.Add(social.State.Apply(other, new SocialRequest(SocialAction.Invite, id, me)));
                    results.Add(social.State.Apply(me, new SocialRequest(SocialAction.AcceptInvitation, id)));
                }
                foreach (Entity grid in session.GetEntitiesOfType<CubeGridComponent>())
                    if (TeamPermissions.OwnerOf(grid) == other && WorldSocial.GridOf(grid) == grid) WorldSocial.SetSharing(grid, faction ? GridSharing.Faction : GridSharing.Everyone);
                social.State.Revision++;
            }
            _log.Info($"rig: {mode}: IdentityId {other} shares its grids with {me}{(results.Count > 0 ? " (" + string.Join("; ", results) + ")" : "")}");
        }
    }
    private long _rigReportAt;

    /// <summary>
    /// Rig (-y51online:shipprobe:&lt;file&gt;): spawns every candidate ship 30 km from the world spawn, then writes the survival
    /// checklist after the spawn top-up and the world's planets with their landers to the file.
    /// </summary>
    private void RigShipProbe(Session session)
    {
        // Rig: -y51online:worldspawn:x,y,z moves the world spawn (hidden test clients crash on one Verdure flora sector).
        if (!_rigSpawnMoved && MpPlugin.LaunchArgs.FirstOrDefault(a => a.StartsWith(MpOptions.Prefix + "worldspawn:", StringComparison.OrdinalIgnoreCase)) is { } spawnArg
            && session.TryGet<IRespawn>() is RespawnSessionComponent respawn)
        {
            _rigSpawnMoved = true;
            var xyz = spawnArg.Substring(MpOptions.Prefix.Length + "worldspawn:".Length).Split(',').Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            respawn.WorldSpawn = new WorldTransform(new Vector3D(xyz[0], xyz[1], xyz[2]), respawn.WorldSpawn.Orientation);
            _log.Info($"rig: world spawn moved to {xyz[0]:0}, {xyz[1]:0}, {xyz[2]:0}");
        }
        string? arg = MpPlugin.LaunchArgs.FirstOrDefault(a => a.StartsWith(MpOptions.Prefix + "shipprobe:", StringComparison.OrdinalIgnoreCase));
        if (arg == null) return;
        if (!_rigProbeStarted)
        {
            _rigProbeStarted = true;
            _rigReportAt = MpClock.NowMs() + 20_000;
            foreach (var ship in RespawnShips.Candidates)
                _log.Info($"ship probe: {ship.Key} prefab {ship.Prefab} {(Singleton<DefinitionManager>.Instance.TryGetDefinition(ship.Prefab, out PrefabDefinition _) ? "exists" : "MISSING")} in this game version");
            ProbeShips(session);
            return;
        }
        if (_rigReportAt == 0 || MpClock.NowMs() < _rigReportAt) return;
        _rigReportAt = 0;
        string path = arg.Substring(MpOptions.Prefix.Length + "shipprobe:".Length);
        ShipCheck.Report(topUp: true, path);
        System.IO.File.AppendAllText(path, "## Planets\n\n" + PlanetReport(session) + "\n\nPROBE DONE\n");
        _log.Info("ship probe: report written to " + path);
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
                var roots = await SpawnEncounter(session, encounters, prefab, new WorldTransform(OpenSpace(session, origin + new Vector3D(i * 3000, 0, 0)), Quaternion.Identity), missions: ship.Space);
                var grids = ReleaseEncounter(session, roots).Where(e => e.TryGet<CubeGridComponent>() != null).ToList();
                if (ship.Grid != null) grids = grids.Where(g => g.Get<CubeGridComponent>().DisplayName.ToString() == ship.Grid).ToList();
                Settle(grids, force: true);
                ShipCheck.Probed[ship.Key] = grids;
            }
            catch (Exception ex)
            {
                GameLog.Instance.Warn("ship probe: " + ship.Key + " failed: " + ex.Message);
                ShipCheck.Probed[ship.Key] = new();
            }
        }
    }
#endif
}
