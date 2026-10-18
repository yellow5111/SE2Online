#nullable enable
using System;
using System.Collections.Generic;
using HarmonyLib;
using Keen.Game2.Client.GameSystems.PlayerControl.PlayerInput.InputHandlers;
using Keen.Game2.Client.WorldObjects.CubeBlocks.Toolbar;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.Pilotable;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.DCS.Components;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// The local side of one pilot per ship (the host decides and enforces it in HostWorld.MayDriveGrid / PassengerGate):
/// sitting down in a cockpit or control seat of a ship another connected player already flies makes the local player a
/// passenger. They can look around; their flight keys, ship toggles and the cockpit toolbar do nothing, and a toast says
/// who flies. Once everyone who sat down before them has stood up, died or left, they fly it from where they sit.
/// </summary>
internal static class CockpitPassenger
{
    internal static volatile bool Active;

    private static Entity? _seat;
    private static readonly HashSet<Entity> Ahead = new(ReferenceEqualityComparer.Instance);
    private static long _nextMs;

    internal static void Install(Harmony harmony)
    {
        var skip = new HarmonyMethod(typeof(CockpitPassenger), nameof(SkipPrefix));
        foreach (string name in new[] { "UpdateControlData", "ToggleDampeners", "ToggleLights", "ToggleParkingBrakes", "TogglePower", "HandleBoost" })
        {
            harmony.Patch(AccessTools.Method(typeof(CockpitInputHandlerComponent), name), skip);
        }
        harmony.Patch(AccessTools.Method(typeof(BlockToolbarHandlerComponent), nameof(BlockToolbarHandlerComponent.TryHandle)), new HarmonyMethod(typeof(CockpitPassenger), nameof(ToolbarPrefix)));
    }

    private static bool SkipPrefix() => !Active;

    private static bool ToolbarPrefix(ref bool __result)
    {
        if (!Active) return true;
        __result = false;
        return false;
    }

    /// <summary>Client tick with the local character and the seat it sits in.</summary>
    internal static void Tick(Keen.VRage.Core.Game.Systems.Session? session, Entity? own, SeatComponent? seat)
    {
        long now = MpClock.NowMs();
        if (now < _nextMs) return;
        _nextMs = now + 250;
        try
        {
            if (session == null || own == null || seat == null || !AdminAccess.InSession || !IsControlSeat(seat.Entity))
            {
                _seat = null;
                Ahead.Clear();
                Active = false;
                return;
            }
            // A copy: the world sync refills the live list on its own frame.
            var players = NametagOverlay.Last.Players is { } live ? new List<(ushort ClientId, string Name, Entity? Target)>(live) : null;
            var flying = new Dictionary<Entity, string>(ReferenceEqualityComparer.Instance);
            if (players != null)
            {
                var root = RootOf(seat.Entity);
                foreach (var e in session.GetEntitiesOfType<SeatComponent>())
                {
                    var pilot = e.Get<SeatComponent>().Pilot;
                    if (pilot == null || pilot == own || !IsControlSeat(e) || RootOf(e) != root) continue;
                    // Only connected players: a character parked by a player who left does not fly the ship.
                    foreach (var p in players)
                    {
                        if (p.Target == pilot) flying[pilot] = p.Name;
                    }
                }
            }
            if (_seat != seat.Entity)
            {
                _seat = seat.Entity;
                Ahead.Clear();
                foreach (var pilot in flying.Keys) Ahead.Add(pilot);
                Active = Ahead.Count > 0;
                if (Active)
                {
                    string name = "";
                    foreach (var n in flying.Values) { name = n; break; }
                    PermissionFeedback.Notify("Passenger", ShipPilots.PassengerNotice(name));
                    GameLog.Instance.Info($"cockpit: {ShipPilots.PassengerNotice(name)} (the host ignores this seat's flight input too)");
                }
                return;
            }
            if (Active)
            {
                Ahead.RemoveWhere(p => !flying.ContainsKey(p));
                if (Ahead.Count == 0)
                {
                    Active = false;
                    PermissionFeedback.Notify("Pilot", "The pilot left; you fly the ship now");
                    GameLog.Instance.Info("cockpit: the pilot left; this seat flies the ship now");
                }
            }
        }
        catch (Exception ex)
        {
            Active = false;
            GameLog.Instance.WarnOnce("cockpit-passenger", "cockpit: passenger check failed (the host still decides who flies): " + ex.Message);
        }
    }

    private static bool IsControlSeat(Entity e) => e.TryGet<CockpitComponent>() != null && !PassengerSeats.IsPassengerSeat(e);

    private static Entity RootOf(Entity e)
    {
        for (int depth = 0; depth < 16 && e.TryGet<HierarchyComponent>()?.Parent is { } parent; depth++) e = parent;
        return e;
    }
}
