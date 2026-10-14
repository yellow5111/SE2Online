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
using Keen.Game2.Simulation.GameSystems.Ownership;
using Keen.Game2.Simulation.GameSystems.Permissions;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.Core.Worlds;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.DCS.Components;
using Keen.VRage.DCS.Builders;
using Keen.VRage.Library.Definitions;
using Keen.VRage.Library.Utils;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;
using Y51_SE2Online.Dedicated;

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
    internal SocialSnapshot Local { get; private set; } = new();
    internal static WorldSocial For(WorldSessionComponent world) => Worlds.GetValue(world, _ => new());

    public static void Install(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(GameWorldBuilder), nameof(GameWorldBuilder.CreateBaseGame)), postfix: new HarmonyMethod(typeof(WorldSocial), nameof(BaseGameBuilt)));
        harmony.Patch(AccessTools.Method(typeof(GridPermissionsComponent), "Init"), postfix: new HarmonyMethod(typeof(WorldSocial), nameof(GridInitialized)));
    }

    private static void BaseGameBuilt(ref EntityBuilder builder, bool isServer)
    {
        if (isServer) builder.WithComponent<WorldSocialSaveComponent>();
    }

    // Single-player grids keep the game's own defaults; a world that starts hosting later gets its sharing from InitializeGrids.
    private static void GridInitialized(GridPermissionsComponent __instance, GridPermissionsObjectBuilder ob)
    {
        if (!ob.BuildPermissions.HasValue && Online) __instance._buildPermissions = Private;
    }

    private static bool Online => DedicatedRuntime.Requested || MpPlugin.Controller?.Current is { } current && (current.Host || current.Join);

    public sealed class WorldSocialSaveComponent : Component, IWorldSessionSaveGameListener
    {
        public async Keen.VRage.Library.Threading.Task OnGameSaving(EntityContainerSerializer<WorldSessionSaveGameMetaData> serializer)
        {
            var world = Entity.Get<WorldSessionComponent>();
            var social = For(world);
            byte[] data;
            lock (social.Sync)
            {
                // Worlds that never had factions or players online stay free of the plugin's file.
                if (social.State.Players.Count == 0 && social.State.Factions.Count == 0) return;
                data = social.State.Serialize();
            }
            var container = ((FileContainerAdapter<WorldSessionSaveGameMetaData>)serializer.Container).Container;
            // Temp containers must finish cleanup before a save listener writes into them.
            var file = await container.GetWritableFileHandleAsync(SaveFile);
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
                social.State = stream == null ? new SocialState() : SocialState.Deserialize(bytes.ToArray());
                social._initialized = false;
            }
        }
    }

    internal void InitializeGrids(WorldSessionComponent world)
    {
        if (_initialized || world.OwnedSession == null) return;
        bool shared = MpPlugin.Controller?.Current?.Grids == GridPermissionMode.Team
            || (MpPlugin.Controller?.Current == null && MpMenu.Settings.TeamGrids);
        foreach (var entity in world.OwnedSession.Entities)
        {
            var grid = entity.TryGet<CubeGridComponent>(); var permissions = entity.TryGet<GridPermissionsComponent>();
            if (grid == null || permissions == null || !PlayerIdentities.IsHuman(TeamPermissions.OwnerOf(entity))) continue;
            // Dedicated worlds have no host player to manage legacy host-owned grids.
            if (DedicatedRuntime.Requested && TeamPermissions.OwnerOf(entity) == PlayerIdentities.HotfixIdentity)
            {
                SetOwner(entity, 0);
                SetSharing(entity, GridSharing.Everyone);
                continue;
            }
            if (IsExplicit(permissions.BuildPermissions)) continue;
            SetSharing(entity, shared ? GridSharing.Everyone : SharingOf(entity));
        }
        _initialized = true;
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
    internal static BlockAcl AclOf(Entity entity)
    {
        string group = BlockOf(entity)?.TryGet<BlockPermissionsComponent>()?.AccessGroup.String;
        return BlockAcl.IsAcl(group) ? Acls.GetOrAdd(group, BlockAcl.Parse) : BlockAcl.Empty;
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
        if ((grid.Scene?.UserObject as Session)?.SessionComponents.TryGet<OwnershipSessionComponent>() is { } ownership) ownership.TryTransferOwnership(grid, to);
        else grid.Get<OwnershipComponent>().Owner = to;
    }

    internal static void SetSharing(Entity grid, GridSharing sharing)
        => grid.Get<GridPermissionsComponent>().BuildPermissions = sharing switch { GridSharing.Faction => Faction, GridSharing.Everyone => Everyone, _ => Private };

    internal static bool? Access(Entity target, long actor)
    {
        var grid = GridOf(target);
        if (grid == null) return null;
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
            SetOwner(grid, actor); SetSharing(grid, GridSharing.Private);
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
                    SetOwner(grid, request.Player); SetSharing(grid, GridSharing.Private); break;
                case SocialAction.ReleaseGrid:
                    SetOwner(grid, 0); SetSharing(grid, GridSharing.Everyone); break;
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
        var acl = AclOf(block).With(request.Player, deny, add);
        if (add && !(deny ? acl.Deny : acl.Allow).Contains(request.Player)) return $"A block list holds at most {BlockAcl.MaxPerList} players.";
        permissions.AccessGroup = acl.IsEmpty ? Singleton<DefinitionManager>.Instance.GetConfiguration<PermissionsConfiguration>().OwnerAccess : StringId.Get(acl.ToString());
        State.Revision++;
        return add ? (deny ? "Player blacklisted on this block." : "Player whitelisted on this block.") : "Player removed from the block's list.";
    }

    private string ApplyAntennaTag(long actor, SocialRequest request, Func<uint, Entity> resolve)
    {
        var block = resolve(request.Grid);
        var permissions = block?.TryGet<BlockPermissionsComponent>();
        var grid = block == null ? null : GridOf(block);
        if (permissions == null || grid == null || BlockOf(block) != block || block.TryGet<AntennaComponent>() == null) return "That antenna is no longer available.";
        if (!State.MayTag(TeamPermissions.OwnerOf(grid), actor)) return "Only the antenna's owner, or their faction's leader or admins, can change this.";
        var acl = AclOf(block) with { ShowTag = request.Offset == 1 };
        permissions.AccessGroup = acl.IsEmpty ? Singleton<DefinitionManager>.Instance.GetConfiguration<PermissionsConfiguration>().OwnerAccess : StringId.Get(acl.ToString());
        State.Revision++;
        return acl.ShowTag ? "This antenna's signal now shows its owner's faction tag." : "This antenna's signal no longer shows a faction tag.";
    }

    /// <summary>
    /// Server: antenna HUD markers get "[TAG] " when the antenna asks for it. SE2 rebuilds the marker name itself on
    /// renames, so the tag is put back on the next pass.
    /// </summary>
    // ponytail: a 1 Hz pass over every antenna marker; after a rename the untagged name shows for up to a second.
    internal static void TagAntennas(Session session, Func<long, string> tagOf)
    {
        var markers = session.SessionComponents.TryGet<AntennaMarkersSessionComponent>()?._markers;
        if (markers == null) return;
        foreach (var (antenna, marker) in markers.AntennaToMarker)
        {
            if (antenna?.Entity == null || EngineCodec.IsDead(antenna.Entity)) continue;
            string name = ((antenna.BroadcastCustomMessage ? null : marker.Grid?.DisplayName) ?? antenna.CustomMessage).EvaluateText() ?? "";
            string tag = AclOf(antenna.Entity).ShowTag ? tagOf(TeamPermissions.OwnerOf(antenna.Entity)) : null;
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

    internal SocialSnapshot Snapshot(long actor, SocialGrid[] grids, SocialRequest view, string message)
    {
        var copy = SocialState.Deserialize(State.Serialize());
        var filtered = grids.Where(g => string.IsNullOrWhiteSpace(view.Name) || g.Name.Contains(view.Name, StringComparison.OrdinalIgnoreCase)).OrderBy(g => g.Name).ThenBy(g => g.NetId).ToArray();
        int offset = Math.Clamp(view.Offset, 0, Math.Max(0, ((filtered.Length - 1) / 100) * 100));
        var snapshot = new SocialSnapshot { Actor = actor, Revision = State.Revision, Message = message, Players = copy.Players.ToArray(), Factions = copy.Factions.ToArray(), TotalGrids = filtered.Length, Offset = offset, Grids = filtered.Skip(offset).Take(100).ToArray(), Focus = view.Grid == 0 ? null : grids.FirstOrDefault(g => g.NetId == view.Grid), Pacts = copy.Pacts.ToArray(), Proposals = copy.Proposals.ToArray() };
        if (actor == PlayerIdentities.HotfixIdentity) Local = snapshot;
        return snapshot;
    }
}
