using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Keen.Game2.Game.SessionComponents;
using Keen.Game2.Game.GameComponents;
using Keen.Game2.Simulation.RuntimeSystems.Saves;
using Keen.Game2.Simulation.RuntimeSystems.Serialization;
using Keen.Game2.Simulation.GameSystems.Antennas;
using Keen.Game2.Simulation.GameSystems.Identities;
using Keen.Game2.Simulation.GameSystems.Ownership;
using Keen.Game2.Simulation.GameSystems.Permissions;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.MechanicalBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.Game2.Simulation.WorldObjects.Characters;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.Core.Worlds;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.DCS.Components;
using Keen.VRage.DCS.Builders;
using Keen.VRage.Library.Definitions;
using Keen.VRage.Library.Filesystem.StorageManagers;
using Keen.VRage.Library.Utils;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;
using Y51_SE2Online.Dedicated;
using Y51_SE2Online.Net;

namespace Y51_SE2Online.SyncEngine;

internal sealed class WorldSocial
{
    internal const string SaveFile = "y51-online-factions.json";
    private static readonly ConditionalWeakTable<WorldSessionComponent, WorldSocial> Worlds = new();
    private static readonly StringId Private = StringId.Get("Y51.Private");
    private static readonly StringId Faction = StringId.Get("Y51.Faction");
    private static readonly StringId Everyone = StringId.Get("Y51.Everyone");
    internal readonly object Sync = new();
    internal SocialState State = new();
    private bool _initialized;
    // The world's save had SE2 Online's factions file: SE2 Online hosted it before.
    private bool _hostedBefore;
    private byte[] _unreadable;
    internal SocialSnapshot Local { get; private set; } = new();
    internal static WorldSocial For(WorldSessionComponent world) => Worlds.GetValue(world, _ => new());

    public static void Install(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(GameWorldBuilder), nameof(GameWorldBuilder.CreateBaseGame)), postfix: new HarmonyMethod(typeof(WorldSocial), nameof(BaseGameBuilt)));
        harmony.Patch(AccessTools.Method(typeof(GridPermissionsComponent), "Init"), postfix: new HarmonyMethod(typeof(WorldSocial), nameof(GridInitialized)));
        harmony.Patch(AccessTools.Method(typeof(ToppedMechanicalBlockComponent), nameof(ToppedMechanicalBlockComponent.RequestSpawnTop)),
            prefix: new HarmonyMethod(typeof(WorldSocial), nameof(SpawnTopPrefix)), finalizer: new HarmonyMethod(typeof(WorldSocial), nameof(SpawnTopFinalizer)));
    }

    private static void BaseGameBuilt(ref EntityBuilder builder, bool isServer)
    {
        if (isServer) builder.WithComponent<WorldSocialSaveComponent>();
    }

    // Single-player grids keep the game's own defaults; a world that starts hosting later gets its sharing from InitializeGrids.
    private static void GridInitialized(GridPermissionsComponent __instance, GridPermissionsObjectBuilder ob)
    {
        if (!ob.BuildPermissions.HasValue && Online) __instance._buildPermissions = _topBase?.TryGet<GridPermissionsComponent>()?.BuildPermissions ?? Private;
    }

    // A piston, rotor or hinge builds its head as a new grid. Private, only the owner could build on the head of a piston a
    // faction member placed on a faction grid (or place one on another's head); the head takes its base grid's sharing.
    [ThreadStatic] private static Entity? _topBase;
    private static void SpawnTopPrefix(ToppedMechanicalBlockComponent __instance) => _topBase = GridOf(__instance.Entity);
    private static Exception? SpawnTopFinalizer(Exception? __exception) { _topBase = null; return __exception; }

    private static bool Online => MpPlugin.Online;

    public sealed class WorldSocialSaveComponent : Component, IWorldSessionSaveGameListener
    {
        public async Keen.VRage.Library.Threading.Task OnGameSaving(EntityContainerSerializer<WorldSessionSaveGameMetaData> serializer)
        {
            var world = Entity.Get<WorldSessionComponent>();
            var social = For(world);
            byte[] data, unreadable;
            lock (social.Sync)
            {
                // Worlds that never had factions or players online stay free of the plugin's file.
                data = social.State.Players.Count == 0 && social.State.Factions.Count == 0 ? null : social.State.SerializeForSave();
                unreadable = social._unreadable;
            }
            if (data == null && unreadable == null) return;
            var container = ((FileContainerAdapter<WorldSessionSaveGameMetaData>)serializer.Container).Container;
            if (data != null) await Write(container, SaveFile, data);
            if (unreadable != null) await Write(container, SaveFile + ".bad", unreadable);
        }
        private static async Keen.VRage.Library.Threading.Task Write(Container<WorldSessionSaveGameMetaData> container, string name, byte[] data)
        {
            // Temp containers must finish cleanup before a save listener writes into them.
            var file = await container.GetWritableFileHandleAsync(name);
            file.CreateDirectories();
            using var stream = file.Open(FileMode.Create, FileAccess.Write, FileShare.None);
            stream.Write(data);
        }
        public async Keen.VRage.Library.Threading.Task OnGameLoading(EntityContainerDeserializer<WorldSessionSaveGameMetaData> deserializer)
        {
            var social = For(Entity.Get<WorldSessionComponent>());
            using var stream = await deserializer.Container.TryOpenReadStream(SaveFile);
            using var bytes = new MemoryStream();
            if (stream != null) stream.CopyTo(bytes);
            lock (social.Sync)
            {
                social.State = new SocialState(); social._unreadable = null;
                if (stream != null)
                {
                    try { social.State = SocialState.Deserialize(bytes.ToArray()); }
                    catch (Exception ex)
                    {
                        // Keen awaits every save listener at the end of LoadWorldSession: one bad JSON would fail a world whose own
                        // files are fine. The world loads without factions and the file is kept as .bad instead of being overwritten.
                        social._unreadable = bytes.ToArray();
                        GameLog.Instance.Error($"factions: {SaveFile} in this save is unreadable ({ex.GetType().Name}: {ex.Message}); the world loads with no factions and the file is kept beside the next save as {SaveFile}.bad");
                    }
                }
                social._initialized = false;
                social._hostedBefore = stream != null;
                social._tagged.Clear(); social._unmatched = null;
            }
        }
    }

    internal void InitializeGrids(WorldSessionComponent world)
    {
        if (_initialized || world.OwnedSession == null) return;
        WatchTransfers(world.OwnedSession);
        bool shared = MpPlugin.Controller?.Current?.Grids == GridPermissionMode.Team
            || (MpPlugin.Controller?.Current == null && MpMenu.Settings.TeamGrids);
        const long host = PlayerIdentities.HotfixIdentity;
        // SE2 4.0 gives this identity to whoever created the world and keeps it bound to their Steam user: then it is that
        // player's own identity, not SE2 2.4's host slot, and settling the slot took their grids and faction at every restart.
        bool legacySlot = DedicatedRuntime.Requested && world.OwnedSession?.SessionComponents?.TryGet<IdentitiesSessionComponent>()?.TryGetUserOf(new IdentityId(host)) == null;
        if (legacySlot) State.SettleHostSlot();
        // The creator's own Steam id, when this game has seen it already (it usually has not at load): their saved place and
        // grids come back before anyone joins. Otherwise that happens when they join (HostWorld).
        else if (DedicatedRuntime.Requested && world.OwnedSession?.SessionComponents?.TryGet<IdentitiesSessionComponent>()?.TryGetUserOf(new IdentityId(host)) is { } creator
                 && Y51_SE2Online.KeenNetcode.SteamUsers.SteamIdOf(creator) is ulong creatorSteam)
            ReclaimHostSlot(world.OwnedSession, creatorSteam);
        long heir = legacySlot ? State.HostHeir : 0;
        bool keep = KeenNetcode.ServerAccess.Off("sharing"), reviewed = State.SharingReviewed || keep;
        int undecided = 0;
        foreach (var entity in world.OwnedSession.Entities)
        {
            var grid = entity.TryGet<CubeGridComponent>(); var permissions = entity.TryGet<GridPermissionsComponent>();
            if (grid == null || permissions == null || !PlayerIdentities.IsHuman(TeamPermissions.OwnerOf(entity))) continue;
            // Nobody plays as the host's identity on a dedicated server: its grids go to that host's own identity when the
            // save names it, else they are left for anyone to claim.
            if (legacySlot && TeamPermissions.OwnerOf(entity) == host)
            {
                SetOwner(entity, heir);
                if (heir == 0)
                {
                    SetSharing(entity, GridSharing.Everyone);
                    continue;
                }
            }
            bool chosen = IsExplicit(permissions.BuildPermissions);
            if (SocialState.SharingUndecided(chosen, SharingOf(entity), shared, _hostedBefore, reviewed)) undecided++;
            if (SocialState.SharingAtLoad(chosen, SharingOf(entity), shared, _hostedBefore, reviewed) is not { } sharing) continue;
            SetSharing(entity, sharing);
        }
        if (!keep) State.SharingReviewed = true;
        if (undecided > 0)
            GameLog.Instance.Warn($"grid sharing: {undecided} player grid(s) are shared with everyone, left as shared (can't tell if deliberate): earlier SE2 Online versions' team grids shared every grid at each load. Owners can make them private in Grid ownership.");
        if (heir != 0)
        {
            string hostId = host.ToString(System.Globalization.CultureInfo.InvariantCulture);
            foreach (var block in world.OwnedSession.GetEntitiesOfType<BlockPermissionsComponent>())
            {
                var permissions = block.Get<BlockPermissionsComponent>();
                string text = permissions.AccessGroup.String;
                if (BlockAcl.IsAcl(text) && text.Contains(hostId, StringComparison.Ordinal))
                    permissions.AccessGroup = StringId.Get(BlockAcl.Parse(text).Renamed(host, heir).ToString());
            }
        }
        if (!State.AclOwners)
        {
            // Lists written before 0.15.2 name no owner; they belong to the grid's owner when the world first hosts.
            foreach (var block in world.OwnedSession.GetEntitiesOfType<BlockPermissionsComponent>())
            {
                var permissions = block.Get<BlockPermissionsComponent>();
                if (!BlockAcl.IsAcl(permissions.AccessGroup.String) || BlockAcl.Parse(permissions.AccessGroup.String) is not { Owner: 0 } acl) continue;
                permissions.AccessGroup = StringId.Get((acl with { Owner = TeamPermissions.OwnerOf(GridOf(block)) }).ToString());
            }
            State.AclOwners = true;
        }
        _initialized = true;
    }

    /// <summary>
    /// Dedicated, server thread: the player SE2 4.0 binds to <see cref="PlayerIdentities.HotfixIdentity"/> joined. A save from
    /// their co-op hosting names them by their derived identity, and a restart before 0.16.8 moved their grids there: their
    /// faction place, grids and block lists come back to the game's identity.
    /// </summary>
    internal void ReclaimHostSlot(Session session, ulong steamId)
    {
        const long host = PlayerIdentities.HotfixIdentity;
        long derived = PlayerIdentities.Derive(PlayerIdentities.KeyFor(new PeerId(steamId), "?"));
        if (!State.CanReclaim(derived)) return;
        int grids = 0;
        foreach (var entity in session.Entities)
            if (entity.TryGet<CubeGridComponent>() != null && TeamPermissions.OwnerOf(entity) == derived) { SetOwner(entity, host); grids++; }
        string from = derived.ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (var block in session.GetEntitiesOfType<BlockPermissionsComponent>())
        {
            var permissions = block.Get<BlockPermissionsComponent>();
            string text = permissions.AccessGroup.String;
            if (BlockAcl.IsAcl(text) && text.Contains(from, StringComparison.Ordinal))
                permissions.AccessGroup = StringId.Get(BlockAcl.Parse(text).Renamed(derived, host).ToString());
        }
        // Last: a failure above leaves the saved slot, and the next join (or load) tries again.
        State.ReclaimHostSlot(derived);
        GameLog.Instance.Info($"factions: Steam user {steamId} plays as the world's first identity {host}; their faction place and {grids} grid(s) saved under {derived} came back to it");
    }

    private static bool IsExplicit(StringId key) => key == Private || key == Faction || key == Everyone;
    internal static Entity GridOf(Entity entity)
    {
        for (int depth = 0; entity != null && depth < 64; depth++)
        {
            if (entity.TryGet<CubeGridComponent>() != null) return entity;
            if (entity.TryGet<CubeBlockComponent>() is { } block) return block.Grid?.Entity;
            entity = entity.TryGet<HierarchyComponent>()?.Parent;
        }
        return null;
    }
    internal static Entity BlockOf(Entity entity)
    {
        for (int depth = 0; entity != null && depth < 64; depth++)
        {
            if (entity.TryGet<CubeBlockComponent>() != null) return entity;
            if (entity.TryGet<CubeGridComponent>() != null) return null;
            entity = entity.TryGet<HierarchyComponent>()?.Parent;
        }
        return null;
    }
    // ponytail: parsed lists cached by their exact text, never evicted; a world has at most a few thousand distinct lists.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, BlockAcl> Acls = new(StringComparer.Ordinal);
    /// <summary>The block's lists in force for its grid's current owner.</summary>
    internal static BlockAcl AclOf(Entity entity)
    {
        var block = BlockOf(entity);
        string group = block?.TryGet<BlockPermissionsComponent>()?.AccessGroup.String;
        return BlockAcl.IsAcl(group) ? Acls.GetOrAdd(group, BlockAcl.Parse).For(TeamPermissions.OwnerOf(GridOf(block))) : BlockAcl.Empty;
    }
    private static bool EveryoneMayUse(Entity entity)
        => BlockOf(entity)?.TryGet<BlockPermissionsComponent>()?.AccessGroup is { } group && group == Singleton<DefinitionManager>.Instance.GetConfiguration<PermissionsConfiguration>().EveryoneAccess;

    internal static GridSharing SharingOf(Entity entity)
    {
        var key = GridOf(entity)?.TryGet<GridPermissionsComponent>()?.BuildPermissions;
        if (key == Faction) return GridSharing.Faction;
        if (key == Everyone) return GridSharing.Everyone;
        if (key == Private || !key.HasValue) return GridSharing.Private;
        return key == Singleton<DefinitionManager>.Instance.GetConfiguration<PermissionsConfiguration>().EveryoneAccess ? GridSharing.Everyone : GridSharing.Private;
    }
    // Through the game's transfer so PCUTrackerSessionComponent moves the grid's PCU; a bare Owner write left it
    // on the old owner and the new owner went negative as blocks were removed.
    internal static void SetOwner(Entity grid, long owner)
    {
        var to = new IdentityId(owner);
        if ((grid.Scene?.UserObject as Session)?.SessionComponents.TryGet<OwnershipSessionComponent>() is { } ownership)
        {
            _ownTransfer = true;
            try { ownership.TryTransferOwnership(grid, to); }
            finally { _ownTransfer = false; }
        }
        else grid.Get<OwnershipComponent>().Owner = to;
    }

    // Our own transfers reset block access themselves (after setting the new sharing).
    [ThreadStatic] private static bool _ownTransfer;
    private static readonly ConditionalWeakTable<Entity, object> Watched = new();
    // SE2 works out a character's connected state only when it enters the scene or a player joins or leaves, so an NPC
    // spawned under the default owner and then handed to NPC keeps "disconnected" and dies after the disconnect timeout.
    private static readonly System.Reflection.MethodInfo UpdateConnectedStatus = AccessTools.Method(typeof(ConnectedStatusComponent), "UpdateConnectedStatus");

    /// <summary>
    /// Server: SE2's own transfers (accepted transfer requests, mission triggers, NPC handovers) reset block access as
    /// ours do. Through its ownership signal, not a patch on TryTransferOwnership, which R2R may inline into its callers.
    /// </summary>
    private static void WatchTransfers(Session session)
    {
        if (session.SessionComponents.TryGet<OwnershipSessionComponent>()?.Entity is not { } entity || Watched.TryGetValue(entity, out _)) return;
        Watched.Add(entity, new object());
        OwnershipSessionComponent.OnOwnershipTransferredSignal.Subscribe(entity, Transferred);
    }

    private static void Transferred(Entity session, Entity target, IdentityId from, IdentityId to)
    {
        if (PlayerIdentities.IsHuman((long)to) && target != null && target.TryGet<CubeGridComponent>() != null) KeenNetcode.PlayerGridGuard.Queue(target);
        if (target?.TryGet<ConnectedStatusComponent>() is { } status)
            try { UpdateConnectedStatus?.Invoke(status, null); }
            catch (Exception ex) { GameLog.Instance.Warn("connected state: recheck after an owner change failed: " + ex.Message); }
        if (_ownTransfer || (long)from == (long)to || target == null || EngineCodec.IsDead(target) || target.TryGet<CubeGridComponent>() == null) return;
        try { ResetBlockAccess(target); }
        catch (Exception ex) { GameLog.Instance.Warn("grid ownership: resetting block access after a native transfer failed: " + ex.Message); }
    }

    /// <summary>After <paramref name="grid"/> changed owner: its blocks that were open to everyone or had block lists go back to owner-only.</summary>
    internal static void ResetBlockAccess(Entity grid)
    {
        var permissions = Singleton<DefinitionManager>.Instance.GetConfiguration<PermissionsConfiguration>();
        long owner = TeamPermissions.OwnerOf(grid);
        string everyone = permissions.EveryoneAccess.String;
        // Subgrids that keep their own owner keep their blocks' access.
        grid.Get<CubeGridComponent>().VisitAllBlocksWithComponent<BlockPermissionsComponent>(p =>
        {
            if (BlockAcl.ResetsOnNewOwner(p.AccessGroup.String, everyone) && TeamPermissions.OwnerOf(GridOf(p.Entity)) == owner) p.AccessGroup = permissions.OwnerAccess;
        }, includeSubgrids: true);
    }

    internal static void SetSharing(Entity grid, GridSharing sharing)
        => grid.Get<GridPermissionsComponent>().BuildPermissions = sharing switch { GridSharing.Faction => Faction, GridSharing.Everyone => Everyone, _ => Private };

    // Ghost blocks (a projection: planned blocks not yet welded) have no grid of their own. Team grids let every player build
    // wherever SE2 would let the owner, which on a ghost meant anyone could build on and delete another player's ghosts.
    // Ghosts on a grid follow that grid's sharing; a ghost grid on its own is its owner's alone, as a new grid is.
    private static bool? GhostAccess(Entity target, long actor)
    {
        if (target?.TryGet<Keen.Game2.Simulation.GameSystems.BlockPlacement.Projections.ProjectionSourceComponent>() == null) return null;
        if (target.TryGet<AttachedToGridComponent>()?.AttachedGrid is { } attached && GridOf(attached) is { } grid) return Access(grid, actor);
        long owner = TeamPermissions.OwnerOf(target);
        if (!PlayerIdentities.IsHuman(owner)) return null;
        return PlayerIdentities.IsHuman(actor) && owner == actor;
    }

    internal static bool? Access(Entity target, long actor)
    {
        var grid = GridOf(target);
        if (grid == null) return GhostAccess(target, actor);
        long owner = TeamPermissions.OwnerOf(grid);
        // Unowned, NPC and "everyone may use" blocks keep the game's own rules (anyone can claim an unowned grid anyway).
        if (!PlayerIdentities.IsHuman(owner) || EveryoneMayUse(target)) return null;
        if (ClientWorld.Current is { BoundSession: { } client } world)
        {
            var seen = client.Social;
            uint? id = seen.Actor == actor && BlockOf(target) is { } block ? world.NetIdOf(block) : null;
            if (id is uint open && seen.OpenBlocks.Contains(open)) return null;
            var listed = id is uint n ? new BlockAcl(seen.AllowedBlocks.Contains(n) ? new[] { actor } : Array.Empty<long>(), seen.DeniedBlocks.Contains(n) ? new[] { actor } : Array.Empty<long>()) : BlockAcl.Empty;
            return BlockAcl.CanUse(owner, actor, SharingOf(grid), seen.Factions, listed);
        }
        var acl = AclOf(target);
        var social = HostWorld.Current?.Social;
        if (social == null) return null;
        lock (social.Sync) return BlockAcl.CanUse(owner, actor, SharingOf(grid), social.State.Factions, acl);
    }

    internal string Apply(long actor, SocialRequest request, Func<uint, Entity> resolve)
    {
        if (request.Action < SocialAction.ClaimGrid || request.Action is >= SocialAction.SetRole and <= SocialAction.RejectProposal or SocialAction.DeclineInvitation) return State.Apply(actor, request);
        if (request.Action is SocialAction.BlockAllow or SocialAction.BlockDeny) return ApplyBlock(actor, request, resolve);
        if (request.Action == SocialAction.AntennaTag) return ApplyAntennaTag(actor, request, resolve);
        var grid = resolve(request.Grid);
        if (grid == null || GridOf(grid) != grid || grid.TryGet<OwnershipComponent>() == null || grid.TryGet<GridPermissionsComponent>() == null) return "That grid is no longer available.";
        long owner = TeamPermissions.OwnerOf(grid);
        if (!State.Players.Any(p => p.Identity == actor) || !SocialState.CanManageGrid(owner, actor)) return "Only this grid's owner can change ownership or sharing.";
        if (request.Action == SocialAction.ClaimGrid)
        {
            if (owner != 0) return "That grid already has an owner.";
            SetOwner(grid, actor); SetSharing(grid, GridSharing.Private); ResetBlockAccess(grid);
        }
        else
        {
            if (owner == 0) return "Claim this grid before changing it.";
            switch (request.Action)
            {
                case SocialAction.ShareGrid:
                    if (!Enum.IsDefined(request.Sharing)) return "Invalid sharing mode.";
                    if (request.Sharing == GridSharing.Faction && State.FactionOf(actor) == null) return "Join or create a faction before sharing with it.";
                    SetSharing(grid, request.Sharing); break;
                case SocialAction.TransferGrid:
                    if (!State.Players.Any(p => p.Identity == request.Player) || request.Player == actor) return "Choose another player in this world.";
                    SetOwner(grid, request.Player); SetSharing(grid, GridSharing.Private); ResetBlockAccess(grid); break;
                case SocialAction.ReleaseGrid:
                    SetOwner(grid, 0); SetSharing(grid, GridSharing.Everyone); ResetBlockAccess(grid); break;
                default: return "Unknown grid action.";
            }
        }
        State.Revision++; return "Grid ownership updated.";
    }

    private string ApplyBlock(long actor, SocialRequest request, Func<uint, Entity> resolve)
    {
        var block = resolve(request.Grid);
        var permissions = block?.TryGet<BlockPermissionsComponent>();
        var grid = block == null ? null : GridOf(block);
        if (permissions == null || grid == null || BlockOf(block) != block) return "That block is no longer available.";
        long owner = TeamPermissions.OwnerOf(grid);
        if (!PlayerIdentities.IsHuman(owner) || owner != actor) return "Only this grid's owner can change who may use its blocks.";
        bool add = request.Offset == 1, deny = request.Action == SocialAction.BlockDeny;
        if (request.Player == actor || (add && !State.Players.Any(p => p.Identity == request.Player))) return "Choose another player in this world.";
        var acl = (AclOf(block) with { Owner = owner }).With(request.Player, deny, add);
        if (add && !(deny ? acl.Deny : acl.Allow).Contains(request.Player)) return $"A block list holds at most {BlockAcl.MaxPerList} players.";
        permissions.AccessGroup = acl.IsEmpty ? Singleton<DefinitionManager>.Instance.GetConfiguration<PermissionsConfiguration>().OwnerAccess : StringId.Get(acl.ToString());
        State.Revision++;
        return add ? (deny ? "Player blacklisted on this block." : "Player whitelisted on this block.") : "Player removed from the block's list.";
    }

    private string ApplyAntennaTag(long actor, SocialRequest request, Func<uint, Entity> resolve)
    {
        var block = resolve(request.Grid);
        var grid = block == null ? null : GridOf(block);
        if (grid == null || BlockOf(block) != block || block.TryGet<AntennaComponent>() == null) return "That antenna is no longer available.";
        if (!State.MayTag(TeamPermissions.OwnerOf(grid), actor)) return "Only the antenna's owner, or their faction's leader or admins, can change this.";
        bool on = request.Offset == 1;
        if (on) _tagged.Add(block); else _tagged.Remove(block);
        // Builds before 0.15.1 kept the switch in the access group of antennas that have block permissions (modded ones).
        if (!on && block.TryGet<BlockPermissionsComponent>() is { } permissions && AclOf(block).ShowTag)
        {
            var acl = AclOf(block) with { ShowTag = false };
            permissions.AccessGroup = acl.IsEmpty ? Singleton<DefinitionManager>.Instance.GetConfiguration<PermissionsConfiguration>().OwnerAccess : StringId.Get(acl.ToString());
        }
        State.Revision++;
        return on ? "This antenna's signal now shows its owner's faction tag." : "This antenna's signal no longer shows a faction tag.";
    }

    // Server, under Sync. SE2 entities have no id that survives a save, so State.TaggedAntennas keeps each tagged antenna's
    // grid cell and position (refreshed every pass) and a loaded world matches them back to its antennas.
    private readonly HashSet<Entity> _tagged = new();
    private List<TaggedAntenna> _unmatched;
    private long _matchedMs;
    internal bool IsTagged(Entity antenna) => _tagged.Contains(antenna) || AclOf(antenna).ShowTag;

    private static TaggedAntenna Saved(Entity antenna)
    {
        var cell = antenna.Get<CubeBlockComponent>().AABB.Min;
        var at = antenna.Data.GetWorldTransform().Position;
        return new(cell.X, cell.Y, cell.Z, at.X, at.Y, at.Z);
    }

    private void MatchSaved(Session session)
    {
        _unmatched ??= State.TaggedAntennas.ToList();
        long now = Environment.TickCount64;
        if (_unmatched.Count == 0 || now - _matchedMs < 5000) return;
        _matchedMs = now;
        var antennas = session.GetEntitiesOfType<AntennaComponent>().Where(e => e.TryGet<CubeBlockComponent>() != null && !EngineCodec.IsDead(e) && !_tagged.Contains(e)).ToList();
        // ponytail: pending x antennas every 5 s; entries whose antenna is gone stay pending (and saved) for good.
        _unmatched.RemoveAll(saved =>
        {
            var spot = new Keen.VRage.Library.Mathematics.Vector3D(saved.PX, saved.PY, saved.PZ);
            var match = antennas.Where(e => e.Get<CubeBlockComponent>().AABB.Min is var c && c.X == saved.X && c.Y == saved.Y && c.Z == saved.Z)
                .Select(e => (e, d: Keen.VRage.Library.Mathematics.Vector3D.Distance(e.Data.GetWorldTransform().Position, spot)))
                .Where(x => x.d < 25).OrderBy(x => x.d).Select(x => x.e).FirstOrDefault();
            if (match == null) return false;
            _tagged.Add(match); antennas.Remove(match);
            return true;
        });
    }

    /// <summary>
    /// Server: antenna HUD markers get "[TAG] " when the antenna asks for it. SE2 rebuilds the marker name itself on
    /// renames, so the tag is put back on the next pass.
    /// </summary>
    // ponytail: a 1 Hz pass over every antenna marker; after a rename the untagged name shows for up to a second.
    internal void TagAntennas(Session session, Func<long, string> tagOf)
    {
        MatchSaved(session);
        _tagged.RemoveWhere(e => EngineCodec.IsDead(e) || e.TryGet<CubeBlockComponent>() == null);
        // JSON has no NaN: one bad transform would fail the whole faction save.
        State.TaggedAntennas = _tagged.Select(Saved).Where(a => double.IsFinite(a.PX) && double.IsFinite(a.PY) && double.IsFinite(a.PZ)).Concat(_unmatched).ToList();
        var markers = session.SessionComponents.TryGet<AntennaMarkersSessionComponent>()?._markers;
        if (markers == null) return;
        foreach (var (antenna, marker) in markers.AntennaToMarker)
        {
            if (antenna?.Entity == null || EngineCodec.IsDead(antenna.Entity)) continue;
            string name = ((antenna.BroadcastCustomMessage ? null : marker.Grid?.DisplayName) ?? antenna.CustomMessage).EvaluateText() ?? "";
            string tag = IsTagged(antenna.Entity) ? tagOf(TeamPermissions.OwnerOf(antenna.Entity)) : null;
            string wanted = FactionTag.Prefix(tag, name);
            if (marker.Name != wanted) marker.Name = wanted;
        }
    }

    private readonly Dictionary<ushort, string> _motdSeen = new();

    /// <summary>Faction messages of the day not yet shown to each connected member (once per connection and per change).</summary>
    internal List<(ushort ClientId, string Text)> DueFactionMotds(IEnumerable<PlayerInfo> roster)
    {
        var due = new List<(ushort, string)>();
        var online = new HashSet<ushort>();
        foreach (var player in roster)
        {
            if (!player.Connected) continue;
            online.Add(player.ClientId);
            var faction = State.FactionOf(player.Identity);
            string key = faction == null || faction.Motd.Length == 0 ? "" : faction.Id + "\n" + faction.Motd;
            if (_motdSeen.TryGetValue(player.ClientId, out string seen) && seen == key) continue;
            _motdSeen[player.ClientId] = key;
            if (key.Length > 0) due.Add((player.ClientId, "Faction System [" + faction.Tag + "]: " + faction.Motd));
        }
        foreach (ushort gone in _motdSeen.Keys.Where(id => !online.Contains(id)).ToList()) _motdSeen.Remove(gone);
        return due;
    }

    /// <summary>One copy of the state for a whole publish; every player's snapshot shares its lists.</summary>
    internal SocialSnapshot Common(uint[] openBlocks)
    {
        var copy = SocialState.Deserialize(State.Serialize());
        return new SocialSnapshot { Revision = State.Revision, Players = copy.Players.ToArray(), Factions = copy.Factions.ToArray(), Pacts = copy.Pacts.ToArray(), Proposals = copy.Proposals.ToArray(), OpenBlocks = openBlocks };
    }

    /// <param name="sortedGrids">By name, then NetId.</param>
    internal SocialSnapshot Snapshot(long actor, SocialGrid[] sortedGrids, SocialRequest view, string message, SocialSnapshot common)
    {
        var filtered = string.IsNullOrWhiteSpace(view.Name) ? sortedGrids : sortedGrids.Where(g => g.Name.Contains(view.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
        int offset = Math.Clamp(view.Offset, 0, Math.Max(0, ((filtered.Length - 1) / 100) * 100));
        var snapshot = new SocialSnapshot { Actor = actor, Revision = common.Revision, Message = message, Players = common.Players, Factions = common.Factions, TotalGrids = filtered.Length, Offset = offset, Grids = filtered.Skip(offset).Take(100).ToArray(), Focus = view.Grid == 0 ? null : sortedGrids.FirstOrDefault(g => g.NetId == view.Grid) is { } focus ? focus with { Secure = view.Secure } : null, Pacts = common.Pacts, Proposals = common.Proposals, OpenBlocks = common.OpenBlocks };
        if (actor == PlayerIdentities.HotfixIdentity) Local = snapshot;
        return snapshot;
    }
}
