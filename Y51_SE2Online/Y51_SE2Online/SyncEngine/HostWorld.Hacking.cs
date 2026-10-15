using System;
using System.Collections.Generic;
using System.Linq;
using Keen.Game2.Simulation.GameSystems.Encounters;
using Keen.Game2.Simulation.GameSystems.GPS;
using Keen.Game2.Simulation.GameSystems.Ownership;
using Keen.Game2.Simulation.GameSystems.Permissions;
using Keen.Game2.Simulation.GameSystems.Player;
using Keen.Game2.Simulation.WorldObjects.Characters;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.Game2.Simulation.WorldObjects.SafeZones;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.Core.Game.GameSystems.ProceduralGeneration;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Definitions;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Library.Utils;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// Server side of hacking: a player at a terminal or control block of a grid takes it over after a while. The rules
/// (who, how long, defences) are <see cref="HackRules"/>/<see cref="HackJob"/>; this file applies them to the world.
/// Everything runs on the server thread (UpdateSocial and UpdateHacks).
/// </summary>
internal sealed partial class HostWorld
{
    private sealed class Hack
    {
        public uint Id;
        public Entity Grid, Block;
        public long Owner, Hacker;
        public ushort HackerClient;
        public string HackerName = "", GridName = "";
        public readonly HackJob Job = new();
        public float Health;
        public long LastMs, NextScanMs;
        public double Reach, Seconds = HackRules.BaseSeconds;
        public int Blocks;
        public bool Powered, DefenderNear;
        public HackPhase LastPhase;
        public List<Entity> Ship = new(), Terminals = new();
        /// <summary>Defenders who counter-hacked, with the clash count when they joined.</summary>
        public readonly Dictionary<long, int> Contesters = new();
        public readonly List<(long Identity, GPSMarker Marker)> Markers = new();
    }

    private readonly Dictionary<uint, Hack> _hacks = new();
    private readonly HackCooldowns _hackCooldowns = new(), _lockdownCooldowns = new();
    private readonly List<(List<Entity> Grids, long Until)> _hackSettles = new();
    private long _nextHackTick;

    /// <summary>Rig diagnostics: one line per running hack, refreshed on the server thread.</summary>
    internal static volatile string HackStatus = "";

    private static OnlineSettings HackSettings => (Dedicated.DedicatedRuntime.Requested ? Dedicated.DedicatedRuntime.Config?.Network : null) ?? MpMenu.Settings;

    private void Tell(ushort clientId, string text) => _host?.GpsNotice(clientId, text);

    private static bool Alive(Entity e) => e != null && !EngineCodec.IsDead(e);
    private static Vector3D At(Entity e) => e.Data.GetWorldTransform().Position;

    private static double Radius(Entity block)
    {
        var b = block.TryGet<CubeBlockComponent>()?.LocalBounds;
        if (b is not { } box) return 1;
        var d = box.Max - box.Min;
        return Math.Sqrt(d.X * d.X + d.Y * d.Y + d.Z * d.Z) / 2;
    }

    private static bool Working(Entity block)
        => Alive(block) && block.TryGet<BreakableBlockComponent>()?.IsBroken() != true && block.TryGet<FunctionalBlockComponent>()?.Functional != false;

    private static double Gap(Entity block, Vector3D at) => Math.Max(0, (At(block) - at).Length() - Radius(block));

    /// <summary>The grid and every grid connected to it (rotors, pistons, connectors) with the same owner.</summary>
    private static List<Entity> ShipOf(Entity grid, long owner)
    {
        var ship = new List<Entity> { grid };
        foreach (var other in grid.Get<CubeGridComponent>().GetAllGridsInShip())
            if (other != grid && Alive(other) && other.TryGet<CubeGridComponent>() != null && TeamPermissions.OwnerOf(other) == owner) ship.Add(other);
        return ship;
    }

    private static List<Entity> TerminalsOf(List<Entity> ship)
    {
        var found = new List<Entity>();
        foreach (var grid in ship)
            grid.Get<CubeGridComponent>().VisitAllBlocksWithComponent<BlockPermissionsComponent>(p => { if (Working(p.Entity)) found.Add(p.Entity); }, includeSubgrids: false);
        return found;
    }

    private static bool PoweredShip(List<Entity> ship, List<Entity> terminals)
        => ship.Any(g => g.Get<CubeGridComponent>().GridIsTurnedOn) && terminals.Any(t => t.TryGet<PowerableBlockComponent>() is { Supplied: true });

    private static int BlockCount(List<Entity> ship) => ship.Sum(g => g.Get<CubeGridComponent>().CubeBlockStorage.CubeBlockCount);

    private static bool InSafeZone(Session session, Vector3D at)
        => session.GetEntitiesOfType<SafeZoneComponent>().Any(z => Alive(z) && z.Get<SafeZoneComponent>().IsInSafeZone(in at));

    private static PlayerRelation Relation(long self, long other) => TurretRelations.Table.Of(self, other);
    private static bool Defends(long player, long owner) => HackRules.Defends(player, owner, Relation(owner, player));

    /// <summary>Server, inside the social lock: Hack (start, counter, cancel) and Lockdown requests.</summary>
    private void HackCommand(Session session, long actor, ushort clientId, SocialRequest request)
    {
        var host = _host;
        if (host == null) return;
        long now = MpClock.NowMs();
        if (request.Action == SocialAction.Lockdown) { Lockdown(actor, clientId, request.Grid, now); return; }
        var mine = _hacks.Values.FirstOrDefault(h => h.Hacker == actor);
        if (request.Offset == 0)
        {
            if (mine != null) EndHack(mine, "you stopped it");
            return;
        }
        var body = CharacterOf(clientId);
        if (!Alive(body) || body.TryGet<CharacterLifeComponent>() is not { IsAlive: true })
        {
            if (request.Offset == 1) Tell(clientId, "You can only hack while you are alive and on foot or seated.");
            return;
        }
        var at = At(body);
        // A defender at a terminal of a grid under attack counter-hacks instead.
        foreach (var hack in _hacks.Values)
            if (Defends(actor, hack.Owner) && hack.Terminals.Any(t => Alive(t) && Gap(t, at) <= HackRules.RangeMeters + 2))
            {
                Contest(hack, actor, now);
                return;
            }
        if (request.Offset != 1) return;
        if (mine != null) { Tell(clientId, $"You are already hacking {mine.GridName}."); return; }
        var settings = HackSettings;
        var online = host.PlayerTable().Where(p => p.Connected).Select(p => p.Identity).ToHashSet();
        string refusal = null;
        foreach (var block in session.GetEntitiesOfType<BlockPermissionsComponent>().Where(b => Working(b) && Gap(b, at) <= HackRules.RangeMeters).OrderBy(b => Gap(b, at)))
        {
            var grid = WorldSocial.GridOf(block);
            if (grid?.TryGet<OwnershipComponent>() == null) continue;
            long owner = TeamPermissions.OwnerOf(grid);
            if (owner == actor) continue;
            uint id = _ids.GetOrAssign(grid);
            var ship = ShipOf(grid, owner);
            var terminals = TerminalsOf(ship);
            refusal = HackRules.Refusal(actor, owner, Relation(actor, owner), new HackRules.Check(settings.Hacking, settings.AllowHackingPlayerGrids, host.Pvp,
                host.OfflineRaidProtection, online.Any(p => p == owner || Relation(owner, p) == PlayerRelation.Faction), InSafeZone(session, At(block)),
                terminals.Count > 0, _hackCooldowns.LeftMs(id, now), _hacks.ContainsKey(id)));
            if (refusal != null) continue;
            StartHack(session, id, grid, block, owner, actor, clientId, ship, terminals, now);
            return;
        }
        Tell(clientId, refusal ?? "Stand within 3 m of a terminal or control block (cockpit, seat, remote control, cargo, ...) of the grid you want to hack.");
    }

    private void StartHack(Session session, uint id, Entity grid, Entity block, long owner, long actor, ushort clientId, List<Entity> ship, List<Entity> terminals, long now)
    {
        var host = _host;
        var roster = host.PlayerTable();
        var h = new Hack
        {
            Id = id, Grid = grid, Block = block, Owner = owner, Hacker = actor, HackerClient = clientId,
            HackerName = roster.FirstOrDefault(p => p.Identity == actor).Name ?? "A player",
            GridName = SocialState.Clean(grid.Get<CubeGridComponent>().DisplayName.ToString(), 60),
            Health = CharacterOf(clientId)?.TryGet<CharacterLifeComponent>()?.Health ?? 0, LastMs = now, NextScanMs = now + 2000,
            Reach = HackRules.RangeMeters + Radius(block), Ship = ship, Terminals = terminals, Blocks = BlockCount(ship), Powered = PoweredShip(ship, terminals)
        };
        if (string.IsNullOrWhiteSpace(h.GridName)) h.GridName = "a grid";
        h.Seconds = HackRules.Seconds(h.Blocks, h.Powered, HackSettings.HackTimeMultiplier);
        _hacks[id] = h;
        string owned = !PlayerIdentities.IsHuman(owner) ? (owner == 0 ? "unowned" : "NPC") : "owned by " + (roster.FirstOrDefault(p => p.Identity == owner).Name ?? "an offline player");
        Tell(clientId, $"Hacking {h.GridName} ({owned}): about {Clock(h.Seconds)} at full speed{(h.Powered ? ", slowed because the grid has power" : "")}. Stay within 3 m; damage or moving away cancels it.");
        foreach (var p in roster.Where(p => p.Connected && Defends(p.Identity, owner)))
        {
            Tell(p.ClientId, $"ALERT: {h.HackerName} is hacking your grid {h.GridName}! Stop them, counter-hack at one of its terminals, or type /lockdown.");
            AddHackMarker(session, h, p.Identity);
        }
        _log.Info($"hacking: '{h.HackerName}' (identity {actor}) started hacking '{h.GridName}' (NetId {id}, {owned}, {h.Blocks} blocks, {h.Terminals.Count} terminals, powered {h.Powered}, {h.Seconds:0} s)");
        _socialNextPublish = 0;
    }

    private void AddHackMarker(Session session, Hack h, long identity)
    {
        try
        {
            var config = Singleton<DefinitionManager>.Instance.GetConfiguration<GPSMarkersConfiguration>();
            var data = session.Get<IPerPlayerData>().GetPerPlayerData<GPSMarkersServerData>(new IdentityId(identity));
            string name = "HACK: " + h.GridName;
            if (name.Length > config.NameMaxLength) name = name[..config.NameMaxLength];
            var marker = new GPSMarker(config.PlayerMadeMarkerType, name, "Being hacked by " + h.HackerName, At(h.Block), config.PlayerCreatedMarkerIcon, new ColorSRGB((byte)255, (byte)40, (byte)40, (byte)255), alwaysShow: true, hidden: false);
            data.GPSMarkers.Add(marker);
            h.Markers.Add((identity, marker));
        }
        catch (Exception ex) { _log.Warn("hacking: alert marker for identity " + identity + " failed: " + ex.Message); }
    }

    private void RemoveHackMarkers(Hack h)
    {
        var session = _worldSession?.OwnedSession;
        foreach (var (identity, marker) in h.Markers)
        {
            try { session?.Get<IPerPlayerData>().GetPerPlayerData<GPSMarkersServerData>(new IdentityId(identity)).GPSMarkers.Remove(marker); }
            catch (Exception ex) { _log.Warn("hacking: removing an alert marker failed: " + ex.Message); }
        }
        h.Markers.Clear();
    }

    private void Contest(Hack h, long actor, long now)
    {
        if (h.Contesters.ContainsKey(actor)) return;
        bool first = h.Job.Contest(now);
        h.Contesters[actor] = h.Job.Clashes;
        string name = _host.PlayerTable().FirstOrDefault(p => p.Identity == actor).Name ?? "A defender";
        Tell(h.HackerClient, $"COUNTER-HACK: {name} is fighting you for {h.GridName}! {(first ? "Your signal drops for a moment, then crawls on at a quarter speed." : "")}");
        foreach (var p in _host.PlayerTable().Where(p => p.Connected && Defends(p.Identity, h.Owner)))
            Tell(p.ClientId, p.Identity == actor ? $"You are counter-hacking {h.HackerName} on {h.GridName}. The hack is slowed, but the fight keeps throwing you out of the terminal: go stop them." : $"{name} is counter-hacking {h.HackerName} on {h.GridName}.");
        _log.Info($"hacking: '{name}' (identity {actor}) counter-hacks '{h.HackerName}' on '{h.GridName}' (NetId {h.Id})");
        _socialNextPublish = 0;
    }

    private void Lockdown(long actor, ushort clientId, uint grid, long now)
    {
        var settings = HackSettings;
        if (settings.HackLockdownSeconds <= 0) { Tell(clientId, "Remote lockdown is turned off on this server."); return; }
        var targets = _hacks.Values.Where(h => (grid == 0 || h.Id == grid) && Defends(actor, h.Owner)).ToList();
        if (targets.Count == 0) { Tell(clientId, grid == 0 ? "None of your grids is being hacked." : "That grid is not being hacked, or it is not yours to defend."); return; }
        string name = _host.PlayerTable().FirstOrDefault(p => p.Identity == actor).Name ?? "The owner";
        foreach (var h in targets)
        {
            long left = _lockdownCooldowns.LeftMs(h.Id, now);
            if (left > 0) { Tell(clientId, $"Lockdown on {h.GridName} is recharging: ready in {Clock(left / 1000.0)}."); continue; }
            h.Job.Lockdown(now, settings.HackLockdownSeconds);
            _lockdownCooldowns.Start(h.Id, now, settings.HackLockdownCooldownSeconds * 1000L);
            Tell(h.HackerClient, $"LOCKDOWN: {h.GridName}'s systems are locked; your hack is frozen for {settings.HackLockdownSeconds} s.");
            foreach (var p in _host.PlayerTable().Where(p => p.Connected && Defends(p.Identity, h.Owner)))
                Tell(p.ClientId, $"{name} locked down {h.GridName}: the hack is frozen for {settings.HackLockdownSeconds} s (next lockdown in {Clock(settings.HackLockdownCooldownSeconds)}).");
            _log.Info($"hacking: '{name}' (identity {actor}) locked down '{h.GridName}' (NetId {h.Id}) for {settings.HackLockdownSeconds} s");
        }
        _socialNextPublish = 0;
    }

    private void EndHack(Hack h, string why)
    {
        _hacks.Remove(h.Id);
        RemoveHackMarkers(h);
        _hackCooldowns.Start(h.Id, MpClock.NowMs(), HackRules.CooldownMs);
        Tell(h.HackerClient, $"Hack on {h.GridName} cancelled: {why}.");
        foreach (var p in _host?.PlayerTable().Where(p => p.Connected && Defends(p.Identity, h.Owner)) ?? Enumerable.Empty<PlayerInfo>())
            Tell(p.ClientId, $"The hack on {h.GridName} by {h.HackerName} stopped: {why}.");
        _log.Info($"hacking: '{h.HackerName}' stopped hacking '{h.GridName}' (NetId {h.Id}) at {h.Job.Progress:P0}: {why}");
        _socialNextPublish = 0;
    }

    private void CompleteHack(Session session, Hack h)
    {
        _hacks.Remove(h.Id);
        RemoveHackMarkers(h);
        long now = MpClock.NowMs();
        _hackCooldowns.Start(h.Id, now, HackRules.CooldownMs);
        var permissions = Singleton<DefinitionManager>.Instance.GetConfiguration<PermissionsConfiguration>();
        var taken = h.Ship.Where(g => Alive(g) && TeamPermissions.OwnerOf(g) == h.Owner).ToList();
        foreach (var grid in taken)
        {
            WorldSocial.SetOwner(grid, h.Hacker);
            WorldSocial.SetSharing(grid, GridSharing.Private);
            grid.Get<CubeGridComponent>().VisitAllBlocksWithComponent<BlockPermissionsComponent>(p =>
            {
                if (BlockAcl.IsAcl(p.AccessGroup.String) || p.AccessGroup == permissions.EveryoneAccess) p.AccessGroup = permissions.OwnerAccess;
            }, includeSubgrids: false);
        }
        if (!PlayerIdentities.IsHuman(h.Owner))
        {
            // An NPC ship stays a procedural encounter (despawned when nobody is near, still on its cargo route) until its spawner lets go.
            foreach (var spawner in session.GetEntitiesOfType<EncounterSpawnerComponent>().Select(e => e.Get<EncounterSpawnerComponent>()))
                foreach (var grid in taken) spawner.SpawnedEntities.Remove(grid);
            foreach (var grid in taken) grid.Data.TryRemove<ProcedurallyGeneratedTag>();
            _hackSettles.Add((taken, now + 8000));
        }
        var social = Social;
        if (social != null) lock (social.Sync) social.State.Revision++;
        Tell(h.HackerClient, $"Hack complete: {h.GridName} is yours ({taken.Count} grid{(taken.Count == 1 ? "" : "s")}, sharing Private, block lists cleared).");
        foreach (var p in _host?.PlayerTable().Where(p => p.Connected && Defends(p.Identity, h.Owner)) ?? Enumerable.Empty<PlayerInfo>())
            Tell(p.ClientId, $"{h.HackerName} hacked {h.GridName}: you lost it.");
        _log.Info($"hacking: '{h.HackerName}' (identity {h.Hacker}) took over '{h.GridName}' (NetId {h.Id}) and {taken.Count - 1} connected grid(s) from owner {h.Owner}");
        _socialNextPublish = 0;
    }

    /// <summary>Server thread, every frame: progress, cancellation and defences for each running hack.</summary>
    private void UpdateHacks(Session session)
    {
        long now = MpClock.NowMs();
        for (int i = _hackSettles.Count - 1; i >= 0; i--)
        {
            Settle(_hackSettles[i].Grids);
            if (now > _hackSettles[i].Until) _hackSettles.RemoveAt(i);
        }
        if (now < _nextHackTick) return;
        _nextHackTick = now + 200;
        if (_hacks.Count == 0) { HackStatus = ""; return; }
        var host = _host;
        if (host == null) return;
        var settings = HackSettings;
        var roster = host.PlayerTable().Where(p => p.Connected).ToList();
        foreach (var h in _hacks.Values.ToList())
        {
            try { TickHack(session, h, roster, settings, now); }
            catch (Exception ex)
            {
                _log.Warn($"hacking: '{h.GridName}' failed: {ex}");
                EndHack(h, "the hack failed");
            }
        }
        HackStatus = string.Join(" | ", _hacks.Values.Select(h => $"{h.Id} '{h.GridName}' by {h.Hacker} {h.Job.Progress:P0} {h.Job.PhaseAt(now, h.DefenderNear)} {h.Seconds:0}s powered {h.Powered} near {h.DefenderNear} contesters {h.Contesters.Count} clashes {h.Job.Clashes}"));
    }

    private void TickHack(Session session, Hack h, List<PlayerInfo> roster, OnlineSettings settings, long now)
    {
        bool connected = roster.Any(p => p.Identity == h.Hacker && p.ClientId == h.HackerClient);
        var body = connected ? CharacterOf(h.HackerClient) : null;
        var life = Alive(body) ? body.TryGet<CharacterLifeComponent>() : null;
        string why = !settings.Hacking ? "hacking was turned off on this server"
            : !connected ? "the hacker left"
            : life is not { IsAlive: true } ? "the hacker died"
            : life.Health < h.Health - 0.01f ? "the hacker took damage"
            : !Alive(h.Grid) || !Working(h.Block) ? "the terminal was destroyed"
            : TeamPermissions.OwnerOf(h.Grid) != h.Owner ? "the grid changed owner"
            : (At(h.Block) - At(body)).Length() > h.Reach + HackRules.CancelSlackMeters ? "the hacker moved away from the terminal"
            : null;
        if (why != null) { EndHack(h, why); return; }
        h.Health = life.Health;
        if (now >= h.NextScanMs)
        {
            h.NextScanMs = now + 2000;
            h.Ship = ShipOf(h.Grid, h.Owner);
            h.Terminals = TerminalsOf(h.Ship);
            h.Powered = PoweredShip(h.Ship, h.Terminals);
            h.Blocks = BlockCount(h.Ship);
        }
        var defenders = roster.Where(p => Defends(p.Identity, h.Owner) && !h.Contesters.ContainsKey(p.Identity)).Select(p => CharacterOf(p.ClientId)).Where(Alive).Select(At).ToList();
        h.DefenderNear = defenders.Count > 0 && defenders.Any(d => (At(h.Grid) - d).Length() <= HackRules.DefenderRadiusMeters || h.Terminals.Any(t => Alive(t) && (At(t) - d).Length() <= HackRules.DefenderRadiusMeters));
        if (h.Job.ClashDue(now))
        {
            _log.Info($"hacking: clash {h.Job.Clashes} on '{h.GridName}' (NetId {h.Id}): glitch and terminal boot");
            _socialNextPublish = 0;
        }
        h.Seconds = HackRules.Seconds(h.Blocks, h.Powered, settings.HackTimeMultiplier);
        var phase = h.Job.PhaseAt(now, h.DefenderNear);
        if (phase != h.LastPhase)
        {
            _log.Info($"hacking: '{h.GridName}' (NetId {h.Id}) {h.LastPhase} -> {phase} at {h.Job.Progress:P0}");
            h.LastPhase = phase;
            _socialNextPublish = 0;
        }
        foreach (var (_, marker) in h.Markers) marker.Position = At(h.Block);
        bool done = h.Job.Advance(now, (now - h.LastMs) / 1000.0, h.DefenderNear, h.Seconds);
        h.LastMs = now;
        if (done) CompleteHack(session, h);
    }

    /// <summary>The hacks a player runs or defends, for their social snapshot.</summary>
    private HackView[] HackViewsFor(long identity)
    {
        if (_hacks.Count == 0) return Array.Empty<HackView>();
        long now = MpClock.NowMs();
        var views = new List<HackView>();
        foreach (var h in _hacks.Values)
        {
            bool hacker = h.Hacker == identity;
            if (!hacker && !Defends(identity, h.Owner)) continue;
            var phase = h.Job.PhaseAt(now, h.DefenderNear);
            int since = h.Contesters.TryGetValue(identity, out int joined) ? joined : -1;
            views.Add(new HackView(h.Id, h.GridName, h.HackerName, hacker ? HackRole.Hacker : HackRole.Defender, phase, h.Job.Progress,
                HackRules.Speed(phase) / Math.Max(1, h.Seconds), (int)Math.Ceiling(h.Job.LockdownUntilMs > now ? (h.Job.LockdownUntilMs - now) / 1000.0 : 0),
                (int)Math.Ceiling(_lockdownCooldowns.LeftMs(h.Id, now) / 1000.0),
                hacker ? h.Job.Clashes : since < 0 ? 0 : h.Job.Clashes - since + 1, since < 0 ? 0 : h.Job.Clashes - since, h.Powered));
        }
        return views.ToArray();
    }

    internal static string Clock(double seconds)
    {
        int s = (int)Math.Ceiling(Math.Max(0, seconds));
        return s >= 60 ? $"{s / 60}:{s % 60:00}" : s + " s";
    }
}
