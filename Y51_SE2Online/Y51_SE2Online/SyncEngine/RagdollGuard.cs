using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Keen.Game2.Simulation.WorldObjects.Characters;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Library.Memory;
using Keen.VRage.Physics.Data;
using Keen.VRage.Physics.Havok;
using Keen.VRage.Physics.Havok.Core;
using Keen.VRage.Physics.Havok.Ragdolls;

namespace Y51_SE2Online.SyncEngine;

/// <summary>Whether SE2's ragdoll drives an entity's transform (the data type is internal to VRage.Physics).</summary>
internal static unsafe class RagdollGuard
{
    private static readonly Type Tag = typeof(RigidBodyData).Assembly.GetType("Keen.VRage.Physics.Data.RagdollDrivesWorldTransform");
    private static MethodInfo _has;

    public static bool DrivesTransform(Entity entity)
    {
        if (Tag == null) return false;
        object data = entity.Data;
        _has ??= data.GetType().GetMethods().First(m => m.Name == "Has" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0).MakeGenericMethod(Tag);
        return (bool)_has.Invoke(data, null);
    }

    // The jetpack clip leaves the limbs to Havok, on weak motors behind a torso keyframed to the entity transform every
    // physics step: any unevenness in that transform's per-step motion yanks the torso and the limbs flail. On a joined
    // client the transform is our per-frame write of host samples (a few % step noise), and after a client hitch it
    // catches up on the wall clock while physics still takes one fixed 1/60 s step (game time stretches), so one step can
    // carry metres the host's velocity does not explain. The ragdoll is keyframed to a copy that moves with the host's
    // velocity. Up to CapMeters of whatever else the real transform does goes into an offset that eases back to zero
    // along a third-order critically damped curve (no acceleration step at either end); past the cap the whole offset
    // moves every ragdoll body rigidly in Havok, keeping orientations and velocities, so the limbs never see it.
    // Real accelerations (the host's velocity changing) still swing them. The pose is read back relative to the same
    // copy, so the rendered limbs ride along rigidly with the body.
    internal static bool Smooth = true;
    internal static double Omega = 3;
    internal static double CapMeters = 0.01;
    internal static double DriftSeconds = 1;
    internal static double TeleportMeters = 500;

    private sealed class Track
    {
        // Shown = real - Offset.
        public Vector3D Offset, OffsetRate, OffsetAccel, LastReal, Drift;
        public Vector3 Velocity;
        public bool Live, Pending;
        public HknpBodyId[]? Bodies;
        public nint BodiesWorld;
        public int ScanCooldown;
        public int Expected;
        public bool Native;
    }

    private static readonly ConditionalWeakTable<RagdollInstance, Track> Tracks = new();
    [ThreadStatic] private static bool _inner;

    internal static void Install(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(RagdollInstance), nameof(RagdollInstance.Update)), prefix: new HarmonyMethod(typeof(RagdollGuard), nameof(UpdatePrefix)));
        harmony.Patch(AccessTools.Method(typeof(RagdollInstance), nameof(RagdollInstance.GetRagdollPose)), prefix: new HarmonyMethod(typeof(RagdollGuard), nameof(PosePrefix)));
    }

    /// <summary>Main thread, once a frame: each character's replicated (the host's) velocity.</summary>
    internal static void Feed(Session session)
    {
        if (!Smooth) return;
        foreach (var e in session.GetEntitiesOfType<CharacterLifeComponent>())
        {
            if (!e.Data.TryGet<RagdollInfo>(out var info) || info.Ragdoll.Value is not { } ragdoll) continue;
            var t = Tracks.GetOrCreateValue(ragdoll);
            t.Velocity = e.Data.TryGet<RigidBodyData>(out var rb) ? rb.LinearVelocity : default;
            if (t.Expected == 0 && e.TryGet<Keen.Game2.Client.WorldObjects.Character.CharacterRagdollComponent>() is { } component) t.Expected = component._bodiesMapping.BodiesCount;
        }
    }

    private static void Ease(Track t, double dt)
    {
        double w = Omega;
        t.OffsetAccel -= (t.Offset * (w * w * w) + t.OffsetRate * (3 * w * w) + t.OffsetAccel * (3 * w)) * dt;
        t.OffsetRate += t.OffsetAccel * dt;
        t.Offset += t.OffsetRate * dt;
    }

    private static bool UpdatePrefix(RagdollInstance __instance, BodyProxy proxy, ReadOnlySpan<RelativeTransform> targetLocalTransforms, bool discontinuity, in WorldTransform worldTransform, float deltaTime)
    {
        if (_inner || !Smooth || ClientWorld.Current == null) return true;
        var t = Tracks.GetOrCreateValue(__instance);
        if (!t.Native)
        {
            // HkRagdoll snaps every body to the animated pose when its target moves faster than MinTeleportVelocity
            // (350 m/s, 5.8 m in a step); a rigid catch-up translation of that size must not trigger it.
            t.Native = true;
            ((HkRagdoll.__Internal*)__instance.Ragdoll)->m_teleportVelocitySq = float.MaxValue;
        }
        var at = worldTransform.Position;
        if (t.Live && deltaTime > 0f)
        {
            // Drift: the playout running a fraction of a percent fast or slow while it adjusts its delay (metres per
            // second at 300 m/s), learnt from sub-cap steps only so a catch-up does not count; untracked it kept the offset at the
            // cap and moved the bodies nearly every step.
            var unexplained = at - t.LastReal - ((Vector3D)t.Velocity + t.Drift) * deltaTime;
            if (unexplained.Length() < CapMeters) t.Drift += unexplained * (1.0 / DriftSeconds);
            t.Offset += unexplained;
            double size = t.Offset.Length();
            if (size > TeleportMeters)
            {
                t.Live = false;
                discontinuity = true;
            }
            else if (size > CapMeters && Translate(t, proxy, t.Offset))
            {
                // All of it, and the ease's state too: easing a clamped offset wound up a velocity that kicked the
                // limbs when a catch-up ended.
                t.Offset = t.OffsetRate = t.OffsetAccel = default;
            }
        }
        if (!t.Live || deltaTime <= 0f)
        {
            t.Offset = t.OffsetRate = t.OffsetAccel = t.Drift = default;
            t.Live = true;
        }
        else
        {
            Ease(t, deltaTime);
        }
        t.LastReal = at;
        t.Pending = true;
        var shown = new WorldTransform(at - t.Offset, worldTransform.Orientation);
        _inner = true;
        try { __instance.Update(proxy, targetLocalTransforms, discontinuity, in shown, deltaTime); }
        finally { _inner = false; }
        return false;
    }

    private static unsafe bool Translate(Track t, BodyProxy proxy, Vector3D delta)
    {
        if (t.Expected == 0) return false;
        var world = proxy.World;
        var mainBody = world == null ? null : world->getBody((HknpBodyId)proxy.BodyIndex);
        if (mainBody == null) return false;
        var main = mainBody->Members.m_id;
        // A fast character's bodies migrate between cluster worlds, and a migrated ragdoll's old ids can be valid ids
        // of other (or half-added) bodies in the new world: setBodyPosition on those crashed natively.
        if (t.Bodies == null || t.BodiesWorld != (nint)world || !Ours(world, main, t.Bodies))
        {
            t.Bodies = null;
            if (t.ScanCooldown > 0)
            {
                t.ScanCooldown--;
                return false;
            }
            var found = FindBodies(world, main, t.Expected);
            if (found.Length == 0)
            {
                t.ScanCooldown = 60;
                return false;
            }
            t.Bodies = found;
            t.BodiesWorld = (nint)world;
        }
        var bodies = t.Bodies;
        foreach (var id in bodies)
        {
            var at = world->getBodyTransform(id)->__Instance.m_translation;
            var moved = new Vector4D(at.X + delta.X, at.Y + delta.Y, at.Z + delta.Z, at.W);
            world->setBodyPosition(id, &moved, HknpWorldWriter.ActivationMode.ACTIVATE);
        }
        return true;
    }

    private static unsafe bool Ours(HknpWorld* world, HknpBodyId.__Internal main, HknpBodyId[] bodies)
    {
        foreach (var id in bodies)
        {
            if (!world->isBodyValid(id) || !world->isBodyAdded(id)) return false;
            var info = VrBodyPartInfo.GetInfo(world, id);
            if (info == null || info->__Instance.m_mainBody.m_value != main.m_value || info->__Instance.m_partId.Provider != VrBodyPartProvider.Ragdoll) return false;
        }
        return true;
    }

    // HkRagdoll keeps its body ids native-side; its bodies carry body-part info naming the character's main body. Ids
    // are index | serial << 24: most bodies have a low serial (one pass over every index), a reused index can have any
    // (a second pass over the low indices, where the bodies live), until the ragdoll's own body count is reached.
    private static unsafe HknpBodyId[] FindBodies(HknpWorld* world, HknpBodyId.__Internal main, int expected)
    {
        var found = new System.Collections.Generic.List<HknpBodyId>();
        uint capacity = world->getBodyCapacity();
        void Scan(int indices, int fromSerial, int toSerial)
        {
            for (int i = 0; i < indices && found.Count < expected; i++)
            {
                for (int serial = fromSerial; serial < toSerial; serial++)
                {
                    var id = (HknpBodyId)new HknpBodyIndex((serial << 24) | i);
                    if (!world->isBodyValid(id)) continue;
                    if (!world->isBodyAdded(id)) break;
                    var info = VrBodyPartInfo.GetInfo(world, id);
                    if (info != null && info->__Instance.m_mainBody.m_value == main.m_value && info->__Instance.m_partId.Provider == VrBodyPartProvider.Ragdoll)
                    {
                        found.Add(id);
                    }
                    break;
                }
            }
        }
        Scan((int)capacity, 0, 16);
        if (found.Count < expected) Scan((int)Math.Min(capacity, 8192), 16, 256);
        if (found.Count != expected)
        {
            GameLog.Instance.Warn($"ragdoll smoothing: found {found.Count} of {expected} ragdoll bodies; catch-ups ease instead of moving them");
            return Array.Empty<HknpBodyId>();
        }
        GameLog.Instance.Info($"ragdoll smoothing: moving {found.Count} ragdoll bodies on catch-ups");
        return found.ToArray();
    }

    // The death ragdoll stops Update and drives the transform from its own root body. A residual offset keeps being read
    // into its pose and eases out on the same curve, so the corpse slides onto its bodies instead of jumping there.
    private static bool PosePrefix(RagdollInstance __instance, BodyProxy proxy, BufferReference<Matrix> actualModelSpaceTransforms, ReadOnlySpan<RelativeTransform> targetLocalTransforms, in WorldTransform worldTransform)
    {
        if (_inner || !Tracks.TryGetValue(__instance, out var t)) return true;
        if (t.Pending) t.Pending = false;
        else if (t.Offset.LengthSquared() > 1e-8) Ease(t, 1.0 / 60);
        else return true;
        var shown = new WorldTransform(worldTransform.Position - t.Offset, worldTransform.Orientation);
        _inner = true;
        try { __instance.GetRagdollPose(proxy, actualModelSpaceTransforms, targetLocalTransforms, in shown); }
        finally { _inner = false; }
        return false;
    }
}
