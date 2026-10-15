using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Keen.Game2.Simulation.GameSystems.Permissions;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Definitions;
using Keen.VRage.Library.Utils;
using Keen.VRage.Multiplayer.SecureIds;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

internal sealed partial class HostWorld
{
    // ponytail: one focused block per player, kept until their next control panel refresh; fine for a few hundred players.
    private readonly ConcurrentDictionary<long, (Entity Block, Guid Secure)> _focusBlocks = new();

    /// <summary>
    /// A request naming its target by a control panel EntityId gets a NetId instead: the block for block access
    /// changes, otherwise its grid. A Refresh also remembers the block so the snapshot can carry its access lists.
    /// </summary>
    private SocialRequest WithSecureTarget(Session session, long actor, SocialRequest request)
    {
        if (request.Secure == Guid.Empty) return request;
        EntityId id = default;
        Unsafe.AsRef(in id._entityGuid) = request.Secure;
        var entity = session.SessionComponents.Components.OfType<ISecureEntityIds>().FirstOrDefault()?.TryGetEntity(id, null);
        if (entity != null && EngineCodec.IsDead(entity)) entity = null;
        var block = entity == null ? null : WorldSocial.BlockOf(entity);
        if (request.Action == SocialAction.Refresh)
        {
            // Every block gets an answer, so the panel can say at once when its type has no access lists.
            if (block != null) _focusBlocks[actor] = (block, request.Secure);
            else _focusBlocks.TryRemove(actor, out _);
        }
        var target = request.Action switch
        {
            SocialAction.BlockAllow or SocialAction.BlockDeny => block?.TryGet<BlockPermissionsComponent>() != null ? block : null,
            // SE2 antennas have no block permissions; their tag switch is kept by WorldSocial.
            SocialAction.AntennaTag => block?.TryGet<Keen.Game2.Simulation.GameSystems.Antennas.AntennaComponent>() != null ? block : null,
            _ => entity == null ? null : WorldSocial.GridOf(entity)
        };
        return request with { Grid = target == null ? 0 : _ids.GetOrAssign(target), Secure = Guid.Empty };
    }

    /// <summary>
    /// Blocks with a whitelist or blacklist, and blocks open to everyone on a player's grid that is not shared with
    /// everyone: the only blocks whose access a joiner cannot work out from the grid's owner and sharing.
    /// </summary>
    private (List<(uint Id, BlockAcl Acl)> Listed, uint[] Open) BlockLists(Session session)
    {
        var listed = new List<(uint, BlockAcl)>();
        var open = new List<uint>();
        var everyone = Singleton<DefinitionManager>.Instance.GetConfiguration<PermissionsConfiguration>().EveryoneAccess;
        foreach (var block in session.GetEntitiesOfType<BlockPermissionsComponent>())
        {
            var group = block.Get<BlockPermissionsComponent>().AccessGroup;
            if (BlockAcl.IsAcl(group.String))
            {
                var acl = WorldSocial.AclOf(block);
                if (acl.Allow.Length + acl.Deny.Length > 0) listed.Add((_ids.GetOrAssign(block), acl));
            }
            else if (group == everyone && WorldSocial.GridOf(block) is { } grid && PlayerIdentities.IsHuman(TeamPermissions.OwnerOf(grid)) && WorldSocial.SharingOf(grid) != GridSharing.Everyone)
                open.Add(_ids.GetOrAssign(block));
        }
        return (listed, open.ToArray());
    }

    private SocialBlock FocusBlockOf(long actor)
    {
        if (!_focusBlocks.TryGetValue(actor, out var focus)) return null;
        var block = focus.Block;
        if (EngineCodec.IsDead(block)) { _focusBlocks.TryRemove(actor, out _); return null; }
        bool lists = block.TryGet<BlockPermissionsComponent>() != null;
        var acl = lists ? WorldSocial.AclOf(block) : new BlockAcl(Array.Empty<long>(), Array.Empty<long>());
        string name = block.TryGet<CubeBlockComponent>()?.Definition?.DebugNameShort ?? "Block";
        bool antenna = block.TryGet<Keen.Game2.Simulation.GameSystems.Antennas.AntennaComponent>() != null;
        return new SocialBlock(_ids.GetOrAssign(block), SocialState.Clean(name, 100), TeamPermissions.OwnerOf(block), acl.Allow, acl.Deny, focus.Secure, antenna, antenna && (Social?.IsTagged(block) ?? acl.ShowTag), lists);
    }
}
