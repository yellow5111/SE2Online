using System;
using System.Collections.Concurrent;
using System.Linq;
using Keen.Game2.Simulation.GameSystems.GPS;
using Keen.Game2.Simulation.GameSystems.Ownership;
using Keen.Game2.Simulation.GameSystems.Player;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.DCS.Components;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.Library.Definitions;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Library.Utils;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

internal sealed partial class HostWorld
{
    private const int MaxSharedTargetMarkers = 1000;
    private readonly ConcurrentQueue<(ushort From, string FromName, GpsShare Share)> _gpsShares = new();

    public void OnGpsShare(ushort fromClientId, string fromName, GpsShare share)
    {
        if (_gpsShares.Count < 64) _gpsShares.Enqueue((fromClientId, fromName, share));
    }

    private void UpdateGps(Session session)
    {
        while (_gpsShares.TryDequeue(out var request))
        {
            try { ShareGps(session, request.From, request.FromName, request.Share); }
            catch (Exception ex)
            {
                _log.Warn("gps share: from ClientId " + request.From + " failed: " + ex);
                _host?.GpsNotice(request.From, "Sharing that marker failed.");
            }
        }
    }

    private void ShareGps(Session session, ushort from, string fromName, GpsShare s)
    {
        var host = _host;
        if (host == null) return;
        var target = host.PlayerTable().FirstOrDefault(p => p.ClientId == s.To && p.Connected);
        if (target.ClientId == 0 || target.Identity == 0)
        {
            host.GpsNotice(from, "That player is not online.");
            return;
        }
        if (!double.IsFinite(s.X) || !double.IsFinite(s.Y) || !double.IsFinite(s.Z)) return;
        var position = new Vector3D(s.X, s.Y, s.Z);
        var config = Singleton<DefinitionManager>.Instance.GetConfiguration<GPSMarkersConfiguration>();
        var data = session.Get<IPerPlayerData>().GetPerPlayerData<GPSMarkersServerData>(new IdentityId(target.Identity));
        if (data.GPSMarkers.Count >= MaxSharedTargetMarkers)
        {
            host.GpsNotice(from, target.Name + "'s GPS list is full.");
            return;
        }
        string name = string.IsNullOrWhiteSpace(s.Name) ? "Shared marker" : s.Name;
        if (name.Length > config.NameMaxLength) name = name[..config.NameMaxLength];
        bool kept = target.ClientId == from;
        string description = kept ? "Kept from a ping" : "Shared by " + fromName;
        if (description.Length > config.DescriptionMaxLength) description = description[..config.DescriptionMaxLength];
        var icon = s.Icon >= 0 && s.Icon < config.Icons.Length ? config.Icons[s.Icon] : config.PlayerCreatedMarkerIcon;
        data.GPSMarkers.Add(new GPSMarker(config.PlayerMadeMarkerType, name, description, position, icon, new ColorSRGB(s.R, s.G, s.B, (byte)255), alwaysShow: false, hidden: false));
        _log.Info($"gps share: ClientId {from} '{fromName}' -> ClientId {target.ClientId} '{target.Name}': '{name}'");
        if (kept) { host.GpsNotice(from, $"Kept '{name}' as a GPS marker."); return; }
        host.GpsNotice(from, $"Shared GPS marker '{name}' with {target.Name}.");
        host.GpsNotice(target.ClientId, $"{fromName} shared GPS marker '{name}' with you.");
    }
}
