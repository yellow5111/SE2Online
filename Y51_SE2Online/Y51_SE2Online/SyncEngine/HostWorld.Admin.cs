using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Keen.Game2.Simulation.GameSystems.Ownership;
using Keen.Game2.Simulation.GameSystems.Player;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.Game2.Simulation.WorldObjects.Characters;
using Keen.Game2.Simulation.WorldObjects.Items;
using Keen.Game2.Simulation.WorldObjects.SafeZones;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.Library.Serialization;
using Keen.VRage.Core.Game.Definitions;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.Components;
using Keen.VRage.DCS.ObjectBuilders;
using Keen.VRage.Library.Definitions;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Library.Utils;
using Y51_SE2Online.Core;
using Y51_SE2Online.Net;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

internal sealed partial class HostWorld
{
    private readonly ConcurrentQueue<(PeerId? Peer, ushort ClientId, AdminRequest Request)> _adminRequests = new();
    private long _nextSafeZoneScan;

    /// <summary>Personally creative identities that may weld without components right now (CreativeWeld); refreshed every 2 s on the server thread.</summary>
    public volatile HashSet<long> FreeWelders = new();

    /// <summary>Identities in the game's personal creative and invulnerable sets, for the panel's own toggles.</summary>
    private volatile HashSet<long> _creativeSet = new(), _godSet = new();

    internal bool IdentitiesOn = true;

    /// <summary>Safe zones for the admin panel, refreshed on the server thread.</summary>
    public SafeZoneInfo[] SafeZones { get; private set; } = Array.Empty<SafeZoneInfo>();

    public (bool Creative, bool Invulnerable) ModeOf(long identity) => (_creativeSet.Contains(identity), _godSet.Contains(identity));

    public void OnAdminRequest(PeerId? peer, ushort clientId, AdminRequest request)
    {
        if (_adminRequests.Count < 64) _adminRequests.Enqueue((peer, clientId, request));
    }

    private volatile Session _adminSession;
    private int _adminThread = -1;

    /// <summary>A world admin action run at once (game thread: console, RCON and chat commands), its answer returned.</summary>
    public string RunAdminNow(PeerId? peer, ushort clientId, AdminRequest request)
    {
        // Only on the server thread (co-op chat commands): the dedicated console, RCON and dedicated chat run on the app
        // thread while the server simulates, so their world actions go through the queue (null).
        if (_adminSession == null || Environment.CurrentManagedThreadId != System.Threading.Volatile.Read(ref _adminThread)) return null;
        try { return Admin(_adminSession, peer, clientId, request) ?? ""; }
        catch (Exception ex) { _log.Warn("admin: " + request.Action + " failed: " + ex); return "That admin action failed: " + ex.Message; }
    }

    private void UpdateAdmin(Session session)
    {
        _adminSession = session;
        System.Threading.Volatile.Write(ref _adminThread, Environment.CurrentManagedThreadId);
        while (_adminRequests.TryDequeue(out var command))
        {
            string message;
            try { message = Admin(session, command.Peer, command.ClientId, command.Request); }
            catch (Exception ex) { message = "That admin action failed: " + ex.Message; _log.Warn("admin: " + command.Request.Action + " failed: " + ex); }
            _log.Info($"admin: ClientId {command.ClientId} {command.Request.Action}: {message}");
            if (message != null) _host?.SendAdminMessage(command.Peer, message);
        }
        // Co-op host: players' /commands run here, on the game thread, as their player.
        if (_host != null && !Y51_SE2Online.Dedicated.DedicatedRuntime.Requested)
            for (int i = 0; i < 4 && _host.ChatCommands.TryDequeue(out var chat); i++)
                _host.RunChatCommand(chat, line => CoopCommand(_host, line));
        UpdateCleanup(session);
        long now = MpClock.NowMs();
        if (now < _nextSafeZoneScan) return;
        _nextSafeZoneScan = now + 2000;
        try { ReconcileCreative(session); }
        catch (Exception ex) { FreeWelders = new(); _log.WarnOnce("creative-reconcile", "admin: checking who has creative tools failed: " + ex.Message); }
        SafeZones = session.GetEntitiesOfType<SafeZoneComponent>().Where(e => !EngineCodec.IsDead(e))
            .Select(e => { var zone = e.Get<SafeZoneComponent>(); return new SafeZoneInfo(_ids.GetOrAssign(e), SocialState.Clean(zone.ZoneName, 64), zone.Radius, 0); }).ToArray();
    }

    private static string CoopCommand(HostSession host, string line)
    {
        string verb = RconAccess.Verb(line);
        if (verb == "status") return $"{host.Players().Count(p => p.Peer.HasValue) + 1} players online, SE2 Online {MpPlugin.Version}.";
        if (verb == "players") return "Online: " + string.Join(", ", host.AccessPlayers().Where(p => p.Connected).Select(p => p.Name));
        if (verb == "motd") return host.LocalAdmin(new AdminRequest(AdminAction.Motd, Name: line.Length > 4 ? line[4..].Trim() : ""));
        return HostCommands.Run(host, line) ?? "That command runs on dedicated servers only.";
    }

    // Toggles that need a permission end when it is lost (demoted, perms reload): creative tools, god mode, access bypass.
    // Only players this server knows (they joined since it started) are judged, so an admin's toggles survive a restart
    // and a demoted player's end as soon as they are back. The world's own creative mode is the game's business.
    private void ReconcileCreative(Session session)
    {
        var mode = session.SessionComponents.TryGet<GameModeSessionComponent>();
        if (mode == null || mode.CreativeOwners == null) { FreeWelders = new(); return; }
        long hostIdentity = Y51_SE2Online.Dedicated.DedicatedRuntime.Requested ? 0 : _hostIdentity;
        var players = _host?.Players() ?? Array.Empty<HostSession.PlayerRecord>();
        bool Known(long identity) => players.Any(p => p.IdentityId == identity);
        bool Has(long identity, string key)
        {
            if (identity == 0) return false;
            if (identity == hostIdentity) return true;
            if (!Known(identity)) return true;
            ulong steam = _host!.SteamOfIdentity(identity);
            return steam != 0 && _host.Allowed(steam, key);
        }
        bool Connected(long identity) => identity != 0 && (identity == hostIdentity || players.Any(p => p.IdentityId == identity && p.Peer.HasValue));
        var creative = mode.CreativeOwners.Select(i => (long)i).ToList();
        var god = mode.InvulnerableOwners?.Select(i => (long)i).ToList() ?? new List<long>();
        _creativeSet = new HashSet<long>(creative);
        _godSet = new HashSet<long>(god);
        if (!mode.CreativeGlobal)
            foreach (long identity in CreativeTools.Revoke(creative, i => Has(i, "admin.creative.tools")))
            {
                mode.CreativeChanged(new IdentityId(identity), false);
                _log.Info($"admin: creative tools off for identity {identity} (no admin.creative.tools on this server)");
            }
        foreach (long identity in CreativeTools.Revoke(god, i => Has(i, "admin.creative.godmode")))
        {
            mode.InvulnerableChanged(new IdentityId(identity), false);
            _log.Info($"admin: god mode off for identity {identity} (no admin.creative.godmode on this server)");
        }
        foreach (long identity in AccessBypass.All().Where(i => !Has(i, "admin.access_bypass")).ToList())
        {
            AccessBypass.Set(identity, false);
            _log.Info($"admin: access bypass off for identity {identity} (no admin.access_bypass on this server)");
        }
        // With identities off every player plays as the host's identity, so a free welder would be everyone.
        FreeWelders = IdentitiesOn && !mode.CreativeGlobal ? CreativeTools.FreeWelders(creative, i => Connected(i) && Has(i, "admin.creative.tools")) : new();
    }

    private Entity CharacterOf(ushort clientId)
    {
        if (clientId == SyncProtocol.HostClientId) return _hostCharacterServer;
        return _joiners.Values.FirstOrDefault(j => j.ClientId == clientId)?.Character;
    }

    private ushort ClientOfSteam(ulong steamId)
    {
        if (steamId == 0) return 0;
        var record = _host?.Players().FirstOrDefault(p => p.Peer?.Value == steamId);
        return record?.ClientId ?? (steamId == NetHost.NetSession.Current?.LocalIdentity ? SyncProtocol.HostClientId : (ushort)0);
    }

    // The checks (permission key, rank) were made by HostSession.Act before the request came here; null sends no reply.
    private string Admin(Session session, PeerId? peer, ushort clientId, AdminRequest r)
    {
        long actor = clientId == SyncProtocol.HostClientId ? _hostIdentity : IdentityOf(clientId);
        Entity body = CharacterOf(clientId);
        WorldTransform? here = body != null && !EngineCodec.IsDead(body) ? body.Data.GetWorldTransform() : null;
        switch (r.Action)
        {
        case AdminAction.Creative:
        case AdminAction.Invulnerable:
        {
            if (actor == 0) return "Your player identity is not ready.";
            var mode = session.SessionComponents.TryGet<GameModeSessionComponent>();
            if (mode == null) return "This world has no game mode component.";
            var identity = new IdentityId(actor);
            if (r.Action == AdminAction.Creative)
            {
                if (mode.CreativeGlobal) return "This world is in creative mode for everyone already.";
                if (mode.IsPersonalCreative(identity) != r.Value) mode.CreativeChanged(identity, r.Value);
                _nextSafeZoneScan = 0;
                return r.Value ? "Creative tools on: build, paste blueprints and spawn without resources. You still take damage and need oxygen (god mode is separate)." : "Creative tools off.";
            }
            if (mode.IsInvulnerable(identity) != r.Value) mode.InvulnerableChanged(identity, r.Value);
            _nextSafeZoneScan = 0;
            return r.Value ? "God mode on: no damage, and health, energy and oxygen stay as they are." : "God mode off.";
        }
        case AdminAction.SpawnPrefab:
        {
            if (here == null) return "You need a character in the world to spawn in front of.";
            if (!Guid.TryParse(r.Name, out var guid) || !Singleton<DefinitionManager>.Instance.TryGetDefinition(guid, out PrefabDefinition prefab)) return "Unknown prefab.";
            var ob = prefab.Get();
            ob.OB<WorldTransformComponent, WorldTransformComponentObjectBuilder>().Transform = Ahead(here.Value, Math.Clamp(r.Size <= 0 ? 25 : r.Size, 5, 500));
            if (actor != 0) OwnershipSessionComponent.ChangeOwnership(ob, new IdentityId(actor));
            session.Get<IEntitySpawner>().SpawnEntity(ob);
            return "Spawned " + prefab.DebugNameShort + ".";
        }
        case AdminAction.SpawnItem:
        {
            // Target 0 is the admin; a 0.15.3 host ignores Target and gives to the admin.
            Entity receiver = r.Target == 0 ? body : CharacterOf(ClientOfSteam(r.Target));
            string name = r.Target == 0 ? null : _host?.AccessPlayers().FirstOrDefault(p => p.SteamId == r.Target)?.Name ?? "that player";
            if (receiver == null || EngineCodec.IsDead(receiver)) return r.Target == 0 ? "You need a character to receive items." : "That player has no character in the world.";
            if (!Guid.TryParse(r.Name, out var guid) || !Singleton<DefinitionManager>.Instance.TryGetDefinition(guid, out ItemDefinition item)) return "Unknown item.";
            int amount = (int)Math.Clamp(r.Size <= 0 ? 1 : r.Size, 1, 100000);
            foreach (var inventory in receiver.Components.OfType<InventoryComponent>())
            {
                var added = inventory.AddItem(new ItemStack(item, amount));
                if (added > 0) return name == null ? $"Added {added} x {item.DebugNameShort}." : $"Gave {added} x {item.DebugNameShort} to {name}.";
            }
            return name == null ? "Your inventory has no room for that." : $"The inventory of {name} has no room for that.";
        }
        case AdminAction.CreateSafeZone:
        {
            if (here == null) return "You need a character in the world to place a safe zone.";
            var prefab = SafeZonesAdmin.GetSafeZonePrefab();
            if (prefab == null) return "This game has no safe zone prefab.";
            var ob = prefab.Get();
            ob.OB<WorldTransformComponent, WorldTransformComponentObjectBuilder>().Transform = new WorldTransform(here.Value.Position, Quaternion.Identity);
            var zone = ob.OB<SafeZoneComponent, SafeZoneObjectBuilder>();
            zone.ZoneName = string.IsNullOrWhiteSpace(r.Name) ? "Safe zone" : SocialState.Clean(r.Name, 64);
            zone.Radius = (float)Math.Clamp(r.Size <= 0 ? 100 : r.Size, 10, 2000);
            session.Get<IEntitySpawner>().SpawnEntity(ob);
            _nextSafeZoneScan = 0;
            return $"Safe zone '{zone.ZoneName}' created with a {zone.Radius:0} m radius.";
        }
        case AdminAction.DeleteSafeZone:
        {
            if (!_ids.TryGet((uint)r.Target, out Entity entity) || entity.TryGet<SafeZoneComponent>() == null) return "That safe zone no longer exists.";
            if (r.Size > 0)
            {
                entity.Get<SafeZoneComponent>().Radius = (float)Math.Clamp(r.Size, 10, 2000);
                _nextSafeZoneScan = 0;
                return "Safe zone resized.";
            }
            session.Get<IEntitySpawner>().DeleteEntity(entity);
            _nextSafeZoneScan = 0;
            return "Safe zone removed.";
        }
        case AdminAction.TeleportTo:
        case AdminAction.Bring:
        {
            Entity other = CharacterOf(ClientOfSteam(r.Target));
            if (body == null || other == null || EngineCodec.IsDead(other)) return "Both players need a character in the world.";
            var (from, to) = r.Action == AdminAction.TeleportTo ? (body, other) : (other, body);
            var target = to.Data.GetWorldTransform();
            Teleport(from, new WorldTransform(target.Position + WorldTransform.TransformDirection(new Vector3D(0, 0, 2), target), target.Orientation));
            return r.Action == AdminAction.TeleportTo ? "Teleported to the player." : "Brought the player to you.";
        }
        case AdminAction.Kill:
        {
            Entity victim = CharacterOf(ClientOfSteam(r.Target));
            if (victim == null || EngineCodec.IsDead(victim) || victim.TryGet<CharacterLifeComponent>() is not { IsAlive: true } life) return "That player has no living character.";
            life.RequestDeath();
            return "Killed " + (_host?.NameOrId(r.Target) ?? "the player") + "'s character.";
        }
        case AdminAction.EntityQuery:
            _host?.SendAdminQuery(peer, EntityQuery(session, here?.Position, r.Value, r.Name));
            return null;
        case AdminAction.EntityAction:
            return EntityAction(session, body, here, (EntityVerb)r.Minutes, (uint)r.Target, r.Name);
        case AdminAction.CleanupPreview:
        case AdminAction.CleanupRun:
        case AdminAction.CleanupSettings:
            return CleanupAdmin(session, peer, r);
        case AdminAction.SetHome:
            return SetHome(session, clientId, actor, r);
        default:
            return "Unknown admin action.";
        }
    }

    // Teleport, not a plain transform write: SE2 4.0's physics and replication treat it as a jump to snap to, and the moved
    // player's own game takes the new place instead of freezing against its old one (Keen's admin teleport).
    private static void Teleport(Entity entity, WorldTransform to)
    {
        var data = entity.Data;
        data.Teleport(to);
        if (data.Has<Keen.VRage.Physics.Data.RigidBodyData>()) data.Set<Keen.VRage.Physics.Data.RigidBodyData>(default);
    }

    internal string GridName(Entity grid)
    {
        string name = null;
        try { name = grid.TryGet<CubeGridComponent>()?.DisplayName.EvaluateText(); } catch (Exception) { }
        return SocialState.Clean(string.IsNullOrWhiteSpace(name) ? grid.GetComposition()?.DebugNameShort ?? "Grid" : name, 64);
    }

    /// <summary>A player's name for an owner identity: the session's saved players, then "NPC" or "nobody".</summary>
    internal string OwnerName(long owner)
    {
        if (owner == 0) return "nobody";
        if (!PlayerIdentities.IsHuman(owner)) return "NPC";
        var social = Social;
        if (social != null) lock (social.Sync) if (social.State.Players.FirstOrDefault(p => p.Identity == owner) is { } player) return player.Name;
        return "player " + owner;
    }

    /// <summary>Each grid group once (its biggest grid named), near the admin or everywhere, nearest first, at most 100.</summary>
    private AdminQuery EntityQuery(Session session, Vector3D? at, bool everywhere, string filter)
    {
        const double Near = 20_000;
        var seen = new HashSet<Entity>();
        var rows = new List<(QueryRow Row, double Distance)>();
        foreach (var grid in session.GetEntitiesOfType<CubeGridComponent>())
        {
            if (EngineCodec.IsDead(grid) || seen.Contains(grid)) continue;
            var group = grid.Get<CubeGridComponent>().GridGraphHandler.GetAllConnectedEntities(grid).Where(e => e.TryGet<CubeGridComponent>() != null).Append(grid).Distinct().ToList();
            foreach (var g in group) seen.Add(g);
            var main = group.OrderByDescending(g => g.Get<CubeGridComponent>().CubeBlockStorage.CubeBlockCount).First();
            var position = main.Data.GetWorldTransform().Position;
            double distance = at is Vector3D me ? Vector3D.Distance(position, me) : -1;
            if (!everywhere && at != null && distance > Near) continue;
            string name = GridName(main);
            long owner = TeamPermissions.OwnerOf(main);
            string ownerName = OwnerName(owner);
            if (!string.IsNullOrWhiteSpace(filter) && !name.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase) && !ownerName.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
            int blocks = group.Sum(g => g.Get<CubeGridComponent>().CubeBlockStorage.CubeBlockCount);
            int pcu = 0;
            try { pcu = group.Sum(g => Keen.Game2.Simulation.GameSystems.PCU.PCUExtensions.GetPCU(g.Get<CubeGridComponent>())); } catch (Exception) { }
            string note = $"{pcu:N0} PCU" + (group.Count > 1 ? $", {group.Count} grids" : "") + (Moving(main) ? ", moving" : "");
            rows.Add((new QueryRow(_ids.GetOrAssign(main), name, ownerName, blocks, Math.Round(distance), note), distance < 0 ? double.MaxValue : distance));
        }
        var shown = rows.OrderBy(r => r.Distance).Take(100).Select(r => r.Row).ToArray();
        return new AdminQuery
        {
            Kind = "entities", Rows = shown,
            Text = $"{rows.Count} grid group{(rows.Count == 1 ? "" : "s")} {(everywhere || at == null ? "in the world" : "within 20 km")}{(string.IsNullOrWhiteSpace(filter) ? "" : " matching '" + SocialState.Clean(filter, 40) + "'")}" + (rows.Count > shown.Length ? $"; nearest {shown.Length} shown" : ".")
        };
    }

    private static bool Moving(Entity grid)
        => grid.Data.Has<Keen.VRage.Physics.Data.RigidBodyData>() && grid.Data.Get<Keen.VRage.Physics.Data.RigidBodyData>().LinearVelocity.LengthSquared() > 0.25f;

    private string EntityAction(Session session, Entity body, WorldTransform? here, EntityVerb verb, uint netId, string argument)
    {
        if (!_ids.TryGet(netId, out Entity grid) || EngineCodec.IsDead(grid) || grid.TryGet<CubeGridComponent>() is not { } cube) return "That grid no longer exists.";
        var group = cube.GridGraphHandler.GetAllConnectedEntities(grid).Where(e => e.TryGet<CubeGridComponent>() != null).Append(grid).Distinct().ToList();
        string name = GridName(grid), owner = OwnerName(TeamPermissions.OwnerOf(grid));
        var position = grid.Data.GetWorldTransform().Position;
        switch (verb)
        {
        case EntityVerb.TeleportTo:
        {
            if (body == null || here == null) return "You need a character in the world to teleport.";
            // ponytail: a sphere guess from the block count instead of the grid's bounds; fine for getting close to it.
            int blocks = group.Sum(g => g.Get<CubeGridComponent>().CubeBlockStorage.CubeBlockCount);
            double reach = 15 + 2.5 * Math.Cbrt(Math.Max(1, blocks)) * 2;
            var away = here.Value.Position - position;
            var direction = away.LengthSquared() > 1 ? Vector3D.Normalize(away) : new Vector3D(0, 0, 1);
            Teleport(body, new WorldTransform(position + direction * reach, here.Value.Orientation));
            return $"Teleported next to '{name}'.";
        }
        case EntityVerb.Stop:
            foreach (var g in group)
                if (g.Data.Has<Keen.VRage.Physics.Data.RigidBodyData>()) g.Data.Set<Keen.VRage.Physics.Data.RigidBodyData>(default);
            return $"Stopped '{name}'.";
        case EntityVerb.Delete:
        {
            if (group.Any(g => Occupied(g))) return $"Someone is sitting in '{name}'; it was not deleted.";
            int blocks = group.Sum(g => g.Get<CubeGridComponent>().CubeBlockStorage.CubeBlockCount);
            AuditLog.Write("admin", "panel", "delete grid", $"'{name}' of {owner}, {blocks} blocks, {group.Count} grids at {position.X:0},{position.Y:0},{position.Z:0}");
            KeenNetcode.PlayerGridGuard.Allow(() => { foreach (var g in group) session.Get<IEntitySpawner>().DeleteEntity(g); });
            return $"Deleted '{name}' ({owner}, {group.Count} grid{(group.Count == 1 ? "" : "s")}, {blocks} blocks).";
        }
        case EntityVerb.Transfer:
        {
            ulong steam = _host?.FindPlayer(argument) ?? 0;
            long to = steam == 0 ? 0 : _host!.Players().FirstOrDefault(p => PeerAlias.SteamOf(p.Peer?.Value ?? 0) == steam && p.IdentityId != 0)?.IdentityId ?? 0;
            if (to == 0 && steam != 0) to = _host!.Access.SeenCopy().FirstOrDefault(s => s.SteamId == steam)?.Identity ?? 0;
            if (to == 0) return $"No player '{argument}' with an identity in this world (they need to have joined once).";
            var ownership = session.SessionComponents.TryGet<OwnershipSessionComponent>();
            if (ownership == null) return "This world has no ownership component.";
            int moved = group.Count(g => g.TryGet<OwnershipComponent>() != null && ownership.TryTransferOwnership(g, new IdentityId(to)));
            return $"'{name}' now belongs to {OwnerName(to)} ({moved} grid{(moved == 1 ? "" : "s")}).";
        }
        case EntityVerb.PowerOff:
        {
            int off = 0;
            foreach (var g in group)
                g.Get<CubeGridComponent>().VisitAllBlocksWithComponent<PowerableBlockComponent>(p => { if (p.Enabled) { p.Enabled = false; off++; } }, includeSubgrids: false);
            return $"Turned off {off} block{(off == 1 ? "" : "s")} on '{name}'.";
        }
        default: return "Unknown grid action.";
        }
    }

    /// <summary>A player sits in one of the grid's seats.</summary>
    internal static bool Occupied(Entity grid)
    {
        bool seated = false;
        try { grid.Get<CubeGridComponent>().VisitAllBlocksWithComponent<Keen.Game2.Simulation.WorldObjects.CubeBlocks.Pilotable.SeatComponent>(s => { if (s.HasPilot) seated = true; }, includeSubgrids: false); }
        catch (Exception) { return true; }
        return seated;
    }

    private static WorldTransform Ahead(WorldTransform from, double distance)
        => new(from.Position + WorldTransform.TransformDirection(new Vector3D(0, 0, -distance), from), from.Orientation);
}
