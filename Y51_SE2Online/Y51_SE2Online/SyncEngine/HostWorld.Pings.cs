using System;
using System.Collections.Concurrent;
using System.Linq;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Mathematics;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

/// <summary>Pings, server side: the range from the pinger's character, then each connected player who may see it.</summary>
internal sealed partial class HostWorld
{
    private readonly ConcurrentQueue<(ushort From, string FromName, string Group, PingSend Ping)> _pings = new();

    public void OnPing(ushort fromClientId, string fromName, PingSend ping) => OnPing(fromClientId, fromName, "", ping);

    public void OnPing(ushort fromClientId, string fromName, string group, PingSend ping)
    {
        if (_pings.Count < 64) _pings.Enqueue((fromClientId, fromName, group, ping));
    }

    private void UpdatePings()
    {
        var host = _host;
        while (_pings.TryDequeue(out var request))
        {
            try { if (host != null) SendPing(host, request.From, request.FromName, request.Group, request.Ping); }
            catch (Exception ex) { _log.Warn("ping: from ClientId " + request.From + " failed: " + ex); }
        }
    }

    private void SendPing(HostSession host, ushort from, string fromName, string group, PingSend ping)
    {
        var settings = host.PingSettings;
        long senderIdentity = from == SyncProtocol.HostClientId ? _hostIdentity : IdentityOf(from);
        var body = CharacterOf(from);
        (double X, double Y, double Z)? at = body != null && !EngineCodec.IsDead(body) ? Tuple(body.Data.GetWorldTransform().Position) : null;
        if (PingRules.Refusal(ping, at, settings.MaxRangeMeters) is { } refused) { host.GpsNotice(from, refused); return; }
        var social = Social;
        RelationTable relations;
        if (social == null) relations = RelationTable.Empty;
        else lock (social.Sync) relations = RelationTable.Build(social.State.Factions, social.State.Pacts);
        var spot = (ping.X, ping.Y, ping.Z);
        string what = ping.What.Length > 0 ? ping.What : PingRules.KindText(ping.Kind);
        int sent = 0;
        foreach (var player in host.PlayerTable().Where(p => p.Connected))
        {
            long viewer = player.ClientId == SyncProtocol.HostClientId ? _hostIdentity : player.Identity;
            var relation = player.ClientId == from ? PlayerRelation.Own : relations.Of(viewer, senderIdentity);
            var viewerBody = CharacterOf(player.ClientId);
            double distance = viewerBody != null && !EngineCodec.IsDead(viewerBody) ? PingRules.Distance(Tuple(viewerBody.Data.GetWorldTransform().Position), spot) : double.MaxValue;
            bool factionless = relations.FactionOf(viewer) == null && relations.FactionOf(senderIdentity) == null;
            if (!PingRules.Sees(settings.Audience, relation, distance, settings.RangeMeters, factionless)) continue;
            host.SendPing(player.ClientId, new PingShow(from, from, fromName, group, ping.X, ping.Y, ping.Z, ping.Kind, ping.Icon, ping.Label, what,
                settings.LifetimeS, PingRules.RelationFor(relation, admin: false)));
            sent++;
        }
        _log.Info($"ping: ClientId {from} '{fromName}' pinged {PingRules.KindText(ping.Kind)}{(ping.Label.Length > 0 ? " '" + ping.Label + "'" : "")}; {sent} player(s) see it");
    }

    private static (double X, double Y, double Z) Tuple(Vector3D v) => (v.X, v.Y, v.Z);
}
