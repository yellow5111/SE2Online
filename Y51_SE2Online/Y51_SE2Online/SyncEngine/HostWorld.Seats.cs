using System;
using System.Globalization;
using System.Linq;
using Keen.Game2.Simulation.GameSystems.Encounters;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.Pilotable;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.Core.Game.Definitions;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Definitions;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Library.Utils;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.SyncEngine;

internal sealed partial class HostWorld
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<ushort> _notSeated = new();
#if !PULSAR
    private readonly System.Collections.Generic.Dictionary<string, Entity> _rigSeats = new();
#endif

    /// <summary>The player's client has its character standing although it rejoined seated: it could not build that seat.</summary>
    public void OnNotSeated(ushort clientId) => _notSeated.Enqueue(clientId);

    /// <summary>
    /// A rejoining player's saved character keeps the seat it was saved in. One sitting in a seat that is gone or broken
    /// here, or parented to a grid with no seat, stands up where the seat put it instead of being taken over stuck.
    /// </summary>
    private void RestoreSeat(Joiner j, Entity character)
    {
        j.RestoredSeat = null;
        var seat = character.AsInterface<ISeatable>()?.CurrentSeat as SeatComponent;
        if (seat != null && seat.Pilot == character && !EngineCodec.IsDead(seat.Entity) && seat.IsFunctional)
        {
            j.RestoredSeat = seat.Entity;
            _log.Info($"host world sync: ClientId {j.ClientId} '{j.Name}' rejoins seated in {seat.Entity.GetComposition()?.DebugNameShort}");
            return;
        }
        if (seat != null && seat.Pilot == character) Unseat(j, seat, "its seat is broken");
        else if (character.Data.Has<EntityParentData>())
        {
            character.Data.TryRemove<EntityParentData>();
            _log.Info($"host world sync: ClientId {j.ClientId} '{j.Name}' was attached to a grid without a seat; released where it is");
        }
    }

    private void UpdateSeats()
    {
        if (_worldSession?.OwnedSession is { } session) PassengerSeats.HoldSeated(session, server: true);
        while (_notSeated.TryDequeue(out ushort clientId))
        {
            var j = _joiners.Values.FirstOrDefault(x => x.ClientId == clientId);
            var restored = j?.RestoredSeat;
            if (j == null || restored == null) continue;
            j.RestoredSeat = null;
            // Only the seat it rejoined in: a seat taken since then is the client's own doing and it knows about it.
            if (j.Character is { } body && body.AsInterface<ISeatable>()?.CurrentSeat is SeatComponent seat && seat.Entity == restored && seat.Pilot == body)
                Unseat(j, seat, "its client could not build that seat");
        }
    }

    // SeatComponent.TryClearPilotStory's server half: the pilot stands up at the seat's exit, every client is told.
    private void Unseat(Joiner j, SeatComponent seat, string why)
    {
        seat.UnsetPilotServer();
        seat.BroadcastUnsetPilot();
        _log.Info($"host world sync: ClientId {j.ClientId} '{j.Name}' stood up from {seat.Entity.GetComposition()?.DebugNameShort}: {why}");
    }

#if !PULSAR
    // SE2's Ransacked Station space encounter: the nearest vanilla content with Seat, Toilet and Bed blocks.
    private static readonly Guid SeatProbePrefab = new("7ed22360-5637-4fdd-9903-672b58bb17e0");

    /// <summary>Rig: "spawn &lt;clientId&gt;" puts the seat probe station 60 m ahead of that player, "list" shows the passenger
    /// seats, "move dx dy dz" shifts the grid of every occupied passenger seat.</summary>
    internal string SeatRig(string args)
    {
        var session = _worldSession?.OwnedSession;
        if (session == null) return "no server session";
        var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        switch (parts.FirstOrDefault())
        {
            case "sit":
            {
                // sit <clientId> [composite prefix]: that player walks up to the nearest free matching seat (passenger
                // seats without a prefix; the one used last time for the same prefix first) and sits down, the server
                // half of SeatComponent.SetPilotStory
                var body = CharacterOf(ushort.Parse(parts[1], CultureInfo.InvariantCulture));
                if (body == null) return "no such character";
                if (body.AsInterface<ISeatable>()?.CurrentSeat != null) return "already seated";
                var at = body.Data.GetWorldTransform().Position;
                string key = parts.Length < 3 ? "" : parts[2];
                var seat = _rigSeats.TryGetValue(key, out var last) && !EngineCodec.IsDead(last) && last.Get<SeatComponent>().CanSeat(body) ? last.Get<SeatComponent>() : session.GetEntitiesOfType<SeatComponent>().Where(e => parts.Length < 3 ? PassengerSeats.IsPassengerSeat(e) : (e.GetComposition()?.DebugNameShort ?? "").StartsWith(parts[2], StringComparison.OrdinalIgnoreCase))
                    .Select(e => e.Get<SeatComponent>()).Where(s => s.CanSeat(body)).OrderBy(s => Vector3D.Distance(s.Entity.Data.GetWorldTransform().Position, at)).FirstOrDefault();
                if (seat == null) return "no free seat";
                _rigSeats[key] = seat.Entity;
                var block = seat.Entity.Data.GetWorldTransform();
                body.Data.Set(new WorldTransform(block.Position + (Vector3D)block.Orientation.GetForward() * 1.5, block.Orientation));
                SeatRestore.SetPilotInternal(seat, body);
                seat.BroadcastSetPilot(body);
                return $"seated in {seat.Entity.GetComposition()?.DebugNameShort}";
            }
            case "tune":
                return PassengerSeats.Tune(string.Join(' ', parts.Skip(1)));
            case "spawn":
            {
                var body = CharacterOf(ushort.Parse(parts[1], CultureInfo.InvariantCulture));
                if (body == null) return "no such character";
                var at = body.Data.GetWorldTransform();
                var encounters = session.SessionComponents.TryGet<EncounterSessionComponent>();
                if (encounters == null || !Singleton<DefinitionManager>.Instance.TryGetDefinition(SeatProbePrefab, out PrefabDefinition prefab)) return "no encounter spawner or probe prefab";
                SpawnProbe(session, encounters, prefab, new WorldTransform(at.Position + (Vector3D)at.Orientation.GetForward() * 60.0, at.Orientation));
                return "spawning the seat probe station";
            }
            case "move":
            {
                double D(int i) => double.Parse(parts[i], CultureInfo.InvariantCulture);
                var by = new Vector3D(D(1), D(2), D(3));
                int moved = 0;
                foreach (var grid in session.GetEntitiesOfType<SeatComponent>().Where(e => PassengerSeats.IsPassengerSeat(e) && e.Get<SeatComponent>().Pilot != null)
                    .Select(e => e.TryGet<CubeBlockComponent>()?.Grid?.Entity).Where(g => g != null).Distinct())
                {
                    var t = grid!.Data.GetWorldTransform();
                    grid.Data.Set(new WorldTransform(t.Position + by, t.Orientation));
                    moved++;
                }
                return $"moved {moved} grid(s) by {by}";
            }
            case "view":
            {
                // view <clientId> [right] [up] [forward]: that player stands at this offset (in the seated character's frame)
                // from the first seated character, looking at its chest
                var viewer = CharacterOf(ushort.Parse(parts[1], CultureInfo.InvariantCulture));
                var seat = session.GetEntitiesOfType<SeatComponent>().Select(e => e.Get<SeatComponent>()).FirstOrDefault(s => s.Pilot != null && s.Pilot != viewer);
                if (viewer == null || seat == null) return "no viewer or no seated character";
                double O(int i, double d) => parts.Length > i ? double.Parse(parts[i], CultureInfo.InvariantCulture) : d;
                var p = seat.Pilot!.Data.GetWorldTransform();
                Vector3D right = (Vector3D)p.Orientation.GetRight(), up = (Vector3D)p.Orientation.GetUp(), forward = (Vector3D)p.Orientation.GetForward();
                var eye = p.Position + right * O(2, 0) + up * O(3, -0.6) + forward * O(4, 2.0);
                var look = Vector3D.Normalize(p.Position + up * 0.5 - (eye + up * 1.6));
                viewer.Data.Set(new WorldTransform(eye, Quaternion.CreateFromForwardUp((Vector3)look, (Vector3)up)));
                return $"viewer placed; seat {seat.Entity.GetComposition()?.DebugNameShort}";
            }
            default:
                return PassengerSeats.Describe(session);
        }
    }

    private static async void SpawnProbe(Keen.VRage.Core.Game.Systems.Session session, EncounterSessionComponent encounters, PrefabDefinition prefab, WorldTransform at)
    {
        try
        {
            var grids = ReleaseEncounter(session, await SpawnEncounter(session, encounters, prefab, at));
            GameLog.Instance.Info($"seat probe: {grids.Count} entities spawned; {PassengerSeats.Describe(session)}");
        }
        catch (Exception ex) { GameLog.Instance.Warn("seat probe: " + ex); }
    }
#endif
}
