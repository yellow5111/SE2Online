using System;
using System.Collections.Generic;
using System.Linq;
using Keen.Game2.Client.GameSystems.PlayerControl.PlayerInput.InputHandlers;
using Keen.Game2.Client.WorldObjects.Character;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// While the host holds this player in a fight scene or punch swing, every active SE2 input handler of the local
/// player (movement, body turning, jetpack, tools, toolbar) is deactivated, so nothing moves the character locally,
/// and exactly those handlers are activated again afterwards. The host zeroes the same input on its side.
/// </summary>
internal static class FightLock
{
    private static readonly List<InputHandlerBaseComponent> Held = new();

    internal static bool Active => Sync.FightLockState.Locked || Held.Count > 0;

    internal static void Tick()
    {
        bool want = Sync.FightLockState.Locked && AdminAccess.InSession && MpClock.NowMs() - Sync.FightLockState.ChangedMs < Sync.PunchRules.LockSafetyMs;
        try
        {
            if (want && Held.Count == 0) Hold();
            else if (!want && Held.Count > 0) Release();
        }
        catch (Exception ex)
        {
            Held.Clear();
            Sync.FightLockState.Locked = false;
            GameLog.Instance.Warn("punch: input lock failed: " + ex.Message);
        }
    }

    private static void Hold()
    {
        var session = MpPlugin.App?.ClientSession;
        if (session == null) return;
        foreach (var entity in session.Entities)
            foreach (var handler in entity.Components.OfType<InputHandlerBaseComponent>())
            {
                if (!handler._isActive) continue;
                try
                {
                    Stop(handler);
                    handler.Deactivate();
                    Held.Add(handler);
                }
                catch (Exception ex) { GameLog.Instance.Info($"punch: could not hold {handler.GetType().Name}: {ex.Message}"); }
            }
        GameLog.Instance.Info($"punch: input held for the fight scene ({Held.Count} input handlers)");
    }

    private static void Release()
    {
        int restored = 0;
        foreach (var handler in Held)
        {
            // The emote unequips the held tool, so that tool's input handler (or the tool it served) can be gone by now;
            // equipping a tool again brings its own handler.
            if (handler.Entity?.Scene == null || handler._observerControllable?.Entity?.Scene == null) continue;
            try
            {
                Stop(handler);
                handler.Activate();
                restored++;
            }
            catch (Exception ex) { GameLog.Instance.Info($"punch: could not restore {handler.GetType().Name}: {ex.Message}"); }
        }
        GameLog.Instance.Info($"punch: input released ({restored} of {Held.Count} input handlers restored; the rest left the scene)");
        Held.Clear();
        Sync.FightLockState.Locked = false;
    }

    // A key held when the lock starts would otherwise keep pushing after it ends: its release never reaches a deactivated context.
    private static void Stop(InputHandlerBaseComponent handler)
    {
        if (handler is not CharacterMovementInputHandlerComponent movement) return;
        movement._basicMovementInputs = default;
        movement._mouse = default;
        movement._lookUp = movement._lookDown = movement._lookLeft = movement._lookRight = 0;
        movement.UpdateMovement();
    }
}
