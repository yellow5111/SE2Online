using System;
using System.Collections.Concurrent;
using System.Linq;
using Keen.Game2.Simulation.GameSystems.Ownership;
using Keen.Game2.Simulation.GameSystems.Player;
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

    /// <summary>Safe zones for the admin panel, refreshed on the server thread.</summary>
    public SafeZoneInfo[] SafeZones { get; private set; } = Array.Empty<SafeZoneInfo>();

    public void OnAdminRequest(PeerId? peer, ushort clientId, AdminRequest request)
    {
        if (_adminRequests.Count < 64) _adminRequests.Enqueue((peer, clientId, request));
    }

    private void UpdateAdmin(Session session)
    {
        while (_adminRequests.TryDequeue(out var command))
        {
            string message;
            try { message = Admin(session, command.ClientId, command.Request); }
            catch (Exception ex) { message = "That admin action failed: " + ex.Message; _log.Warn("admin: " + command.Request.Action + " failed: " + ex); }
            _log.Info($"admin: ClientId {command.ClientId} {command.Request.Action}: {message}");
            _host?.SendAdminMessage(command.Peer, message);
        }
        long now = MpClock.NowMs();
        if (now < _nextSafeZoneScan) return;
        _nextSafeZoneScan = now + 2000;
        SafeZones = session.GetEntitiesOfType<SafeZoneComponent>().Where(e => !EngineCodec.IsDead(e))
            .Select(e => { var zone = e.Get<SafeZoneComponent>(); return new SafeZoneInfo(_ids.GetOrAssign(e), SocialState.Clean(zone.ZoneName, 64), zone.Radius, 0); }).ToArray();
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

    private string Admin(Session session, ushort clientId, AdminRequest r)
    {
        long actor = clientId == SyncProtocol.HostClientId ? PlayerIdentities.HotfixIdentity : IdentityOf(clientId);
        Entity body = CharacterOf(clientId);
        WorldTransform? here = body != null && !EngineCodec.IsDead(body) ? body.Data.GetWorldTransform() : null;
        switch (r.Action)
        {
        case AdminAction.Creative:
        {
            if (actor == 0) return "Your player identity is not ready.";
            var mode = session.SessionComponents.TryGet<GameModeSessionComponent>();
            if (mode == null) return "This world has no game mode component.";
            var identity = new IdentityId(actor);
            if (mode.IsCreative(identity) != r.Value) mode.CreativeChanged(identity, r.Value);
            return r.Value ? "Creative tools on: build, paste blueprints and spawn without resources." : "Creative tools off.";
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
            from.Data.Set(new WorldTransform(target.Position + WorldTransform.TransformDirection(new Vector3D(0, 0, 2), target), target.Orientation));
            return r.Action == AdminAction.TeleportTo ? "Teleported to the player." : "Brought the player to you.";
        }
        default:
            return "Unknown admin action.";
        }
    }

    private static WorldTransform Ahead(WorldTransform from, double distance)
        => new(from.Position + WorldTransform.TransformDirection(new Vector3D(0, 0, -distance), from), from.Orientation);
}
