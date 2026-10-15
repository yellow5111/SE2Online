#nullable enable
using System;
using System.Collections.Generic;
using HarmonyLib;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.Pilotable;
using Keen.VRage.Core;
using Keen.VRage.DCS.Builders;
using Keen.VRage.DCS.Components;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// Seats a character that was saved sitting, on a client that receives it with the world.
/// <para>SE2 re-seats a saved pilot when its seat block is added to the scene, expecting the pilot in the same bundle. A
/// client receives the world in parts, so a ship can arrive one part before the character sitting in it: the pilot is
/// then an unbuilt stub, SE2 throws reading its name and the whole part (the ship with it) is dropped. Such seats wait
/// here until their pilot is built.</para>
/// <para>If the player's own character still stands a few seconds after the join, the client says so and the host
/// stands it up from a seat it rejoined in (a seat this client could not build), instead of leaving it stuck there.</para>
/// </summary>
internal static class SeatRestore
{
    private const long ReportAfterMs = 5000;
    private static readonly object Lock = new();
    // Protected virtual, so not publicized; the delegate still calls the client seat's override.
    internal static readonly Action<SeatComponent, Entity> SetPilotInternal = AccessTools.MethodDelegate<Action<SeatComponent, Entity>>(AccessTools.Method(typeof(SeatComponent), "SetPilotInternal"));
    private static readonly List<(SeatComponent Seat, Entity Pilot, RelativeTransform At)> Waiting = new();
    private static ClientWorld? _world;
    private static Entity? _own;
    private static long _ownSinceMs;
    private static bool _ownChecked;
    private static long _nextMs;

    internal static void Install(Harmony harmony) =>
        harmony.Patch(AccessTools.Method(typeof(SeatComponent), "Keen.VRage.Core.Game.Systems.IEntityBundleListener.OnBundleAddedToScene"),
            prefix: new HarmonyMethod(typeof(SeatRestore), nameof(BundleAddedPrefix)));

    private static bool BundleAddedPrefix(SeatComponent __instance)
    {
        var ob = __instance._ob;
        if (ob?.Pilot == null || !EntityAllocator.IsStub(ob.Pilot)) return true;
        lock (Lock) Waiting.Add((__instance, ob.Pilot, ob.PilotTransform));
        GameLog.Instance.Info($"seat restore: {__instance.Entity.GetComposition()?.DebugNameShort} arrived before its pilot; it sits down when the pilot is built");
        return false;
    }

    /// <summary>Client main thread.</summary>
    internal static void Tick()
    {
        var world = ClientWorld.Current;
        long now = MpClock.NowMs();
        if (world != _world)
        {
            _world = world;
            _own = null;
            lock (Lock) Waiting.Clear();
        }
        if (world == null || now < _nextMs) return;
        _nextMs = now + 200;
        try
        {
            SeatWaiting(world);
            var own = world.OwnCharacter;
            if (own == null) return;
            if (own != _own) { _own = own; _ownSinceMs = now; _ownChecked = false; }
            if (_ownChecked) return;
            if (own.AsInterface<ISeatable>()?.CurrentSeat != null) _ownChecked = true;
            else if (now - _ownSinceMs > ReportAfterMs)
            {
                _ownChecked = true;
                world.ReportNotSeated("own character standing after the join; if it rejoined seated, that seat was not built here");
            }
        }
        catch (Exception ex)
        {
            GameLog.Instance.WarnOnce("seat-restore", "seat restore: " + ex);
        }
    }

    private static void SeatWaiting(ClientWorld world)
    {
        List<(SeatComponent Seat, Entity Pilot, RelativeTransform At)>? ready = null;
        lock (Lock)
        {
            for (int i = Waiting.Count - 1; i >= 0; i--)
            {
                var (seat, stub, at) = Waiting[i];
                if (EngineCodec.IsDead(seat.Entity)) { Waiting.RemoveAt(i); continue; }
                if (world.ClientEntityOf(stub) is not { } pilot) continue;
                Waiting.RemoveAt(i);
                (ready ??= new()).Add((seat, pilot, at));
            }
        }
        if (ready == null) return;
        foreach (var (seat, pilot, at) in ready)
        {
            if (seat.Pilot != null || pilot.AsInterface<ISeatable>()?.CurrentSeat != null) continue;
            // What SE2's own OnBundleAddedToScene does with a pilot that came in the same bundle.
            SetPilotInternal(seat, pilot);
            seat.OverwritePilotData(at);
            GameLog.Instance.Info($"seat restore: pilot seated in {seat.Entity.GetComposition()?.DebugNameShort}");
        }
    }
}
