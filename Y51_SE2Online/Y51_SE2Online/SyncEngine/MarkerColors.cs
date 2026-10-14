using System;
using System.Collections.Generic;
using System.Linq;
using Keen.Game2.Client.GameSystems.GPS;
using Keen.Game2.Simulation.GameSystems.Antennas;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Mathematics;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// Multiplayer marker colours by relationship (<see cref="RelationTable"/>, the same one turrets and damage use): own
/// green, faction yellow, allies cyan, factions at peace white, war and NPCs red, and other players red when PvP is on
/// or white when it is off. SE2 draws every antenna signal in one colour whoever owns it.
/// </summary>
internal static class MarkerColors
{
    public static readonly ColorSRGB Own = new(90, 220, 90), Faction = new(255, 210, 60), Ally = new(80, 210, 230), Enemy = new(240, 70, 60), Neutral = new(225, 225, 225);
    private static (SocialSnapshot Snapshot, RelationTable Table) _relations;
    private static long _nextScanMs;
    private static readonly System.Reflection.PropertyInfo SessionProperty = HarmonyLib.AccessTools.Property(typeof(GPSMarkerRenderSessionComponent), "Session");

    /// <summary>Null for unowned things, which keep SE2's own colour.</summary>
    public static ColorSRGB? For(long owner)
    {
        var social = SocialUi.Snapshot;
        if (owner == 0 || social.Actor == 0) return null;
        var cached = _relations;
        if (!ReferenceEquals(cached.Snapshot, social)) _relations = cached = (social, RelationTable.Build(social.Factions, social.Pacts));
        return cached.Table.Of(social.Actor, owner) switch
        {
            PlayerRelation.Own => Own,
            PlayerRelation.Faction => Faction,
            PlayerRelation.Allied => Ally,
            PlayerRelation.Peace => Neutral,
            PlayerRelation.Neutral => PvpOn ? Enemy : Neutral,
            _ => Enemy
        };
    }

    private static bool PvpOn => AdminAccess.Hosting ? AdminAccess.Host.Pvp : AdminAccess.Client?.Admin.Pvp ?? true;

    private static readonly List<Entity> AntennaEntities = new();
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, Entity> MarkerAntenna = new();

    /// <summary>
    /// Every frame: places each antenna marker on its antenna as drawn here and colours it by owner. The marker's own
    /// position is the host's, updated as an occasional model change, so on a moving ship it lagged and jumped
    /// against the smoothly drawn grid. Fields are written, not properties: a joiner's property change is sent to
    /// the host as an edit of its marker.
    /// </summary>
    private static Vector3D MarkerPoint(Entity antenna)
    {
        var at = antenna.Data.GetWorldTransform();
        return WorldTransform.Transform((Vector3D)Keen.Game2.Simulation.GameSystems.GPS.HUDMarkerPositionOffsetBaseComponent.GetOffsetOnEntity(antenna), in at);
    }

    public static void ApplyToAntennas(GPSMarkerRenderSessionComponent renderer)
    {
        var markers = renderer._antennas?.ActiveMarkers;
        if (markers == null) return;
        long now = MpClock.NowMs();
        if (now >= _nextScanMs)
        {
            _nextScanMs = now + 1000;
            AntennaEntities.Clear();
            // Antenna blocks only: characters carry a suit antenna too, and a marker near a player got pinned to their feet.
            AntennaEntities.AddRange(((Session)SessionProperty.GetValue(renderer)).GetEntitiesOfType<AntennaComponent>()
                .Where(e => e.TryGet<Keen.Game2.Simulation.WorldObjects.CubeBlocks.CubeBlockComponent>() != null));
        }
        bool colour = SocialUi.Snapshot.Actor != 0;
        foreach (var marker in markers)
        {
            if (!MarkerAntenna.TryGetValue(marker, out Entity antenna) || EngineCodec.IsDead(antenna))
            {
                // The host puts each marker at its antenna's marker offset: match on that point, not the block centre.
                antenna = null;
                double best = 20 * 20;
                foreach (Entity candidate in AntennaEntities)
                {
                    if (EngineCodec.IsDead(candidate)) continue;
                    double distance = Vector3D.DistanceSquared(MarkerPoint(candidate), marker.Position);
                    if (distance < best) { best = distance; antenna = candidate; }
                }
                if (antenna == null) continue;
                MarkerAntenna.AddOrUpdate(marker, antenna);
            }
            marker._position = MarkerPoint(antenna);
            if (colour && For(TeamPermissions.OwnerOf(antenna)) is ColorSRGB color) marker._color = color;
        }
    }
}
