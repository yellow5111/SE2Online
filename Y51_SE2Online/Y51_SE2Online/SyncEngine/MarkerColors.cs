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

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, Entity> MarkerAntenna = new();
    // Antenna blocks by where their markers were at the last scan, and when an unmatched marker may look again: a marker
    // with no antenna near it (a player's suit) looked through every antenna of the world every frame.
    private const double CellMeters = 50;
    private const int RetryMs = 500;
    private static readonly Dictionary<(long, long, long), List<Entity>> AntennaCells = new();
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, System.Runtime.CompilerServices.StrongBox<long>> MarkerRetry = new();

    private static (long, long, long) CellOf(Vector3D p) => ((long)Math.Floor(p.X / CellMeters), (long)Math.Floor(p.Y / CellMeters), (long)Math.Floor(p.Z / CellMeters));

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
            AntennaCells.Clear();
            // Antenna blocks only: characters carry a suit antenna too, and a marker near a player got pinned to their feet.
            foreach (var e in ((Session)SessionProperty.GetValue(renderer)).GetEntitiesOfType<AntennaComponent>())
            {
                if (e.TryGet<Keen.Game2.Simulation.WorldObjects.CubeBlocks.CubeBlockComponent>() == null || EngineCodec.IsDead(e)) continue;
                var key = CellOf(MarkerPoint(e));
                if (!AntennaCells.TryGetValue(key, out var list)) AntennaCells[key] = list = new List<Entity>(2);
                list.Add(e);
            }
        }
        bool colour = SocialUi.Snapshot.Actor != 0;
        foreach (var marker in markers)
        {
            if (!MarkerAntenna.TryGetValue(marker, out Entity antenna) || EngineCodec.IsDead(antenna))
            {
                // The host puts each marker at its antenna's marker offset: match on that point, not the block centre.
                antenna = null;
                var retry = MarkerRetry.GetValue(marker, _ => new System.Runtime.CompilerServices.StrongBox<long>(long.MinValue));
                if (now < retry.Value) continue;
                double best = 20 * 20;
                var (cx, cy, cz) = CellOf(marker.Position);
                for (long dx = -1; dx <= 1; dx++)
                    for (long dy = -1; dy <= 1; dy++)
                        for (long dz = -1; dz <= 1; dz++)
                        {
                            if (!AntennaCells.TryGetValue((cx + dx, cy + dy, cz + dz), out var cell)) continue;
                            foreach (Entity candidate in cell)
                            {
                                if (EngineCodec.IsDead(candidate)) continue;
                                double distance = Vector3D.DistanceSquared(MarkerPoint(candidate), marker.Position);
                                if (distance < best) { best = distance; antenna = candidate; }
                            }
                        }
                if (antenna == null)
                {
                    retry.Value = now + RetryMs;
                    continue;
                }
                MarkerAntenna.AddOrUpdate(marker, antenna);
            }
            marker._position = MarkerPoint(antenna);
            if (colour && For(TeamPermissions.OwnerOf(antenna)) is ColorSRGB color) marker._color = color;
        }
    }
}
