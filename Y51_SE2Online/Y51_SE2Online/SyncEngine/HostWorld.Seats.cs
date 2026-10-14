using System;
using System.Globalization;
using System.Linq;
using Keen.Game2.Simulation.GameSystems.Encounters;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.Pilotable;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.Core.Game.Definitions;
using Keen.VRage.Library.Definitions;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Library.Utils;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.SyncEngine;

internal sealed partial class HostWorld
{
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
}
