using System;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.Game2.Simulation.WorldObjects.CubeGrids.Physicss;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.Core.Game.GameSystems.Queries;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Physics;
using Keen.VRage.Physics.Queries;
using Keen.VRage.Voxels.Components;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// The own body is shown ahead of the host's newest sample by the movement lead (one input round trip). Into a wall or
/// a hill the host has already stopped, so the body went that far into it (about 3 m at 180 ms) and was pulled back
/// a round trip later. The lead stops at the first grid or voxel in its way.
/// </summary>
internal static class LeadClamp
{
    private const float Radius = 0.45f;

    // Knee, waist and chest: the hull's gaps (ramps, gear wells) let a single ray through.
    private static readonly float[] Heights = { 0.4f, 0.9f, 1.5f };

    // ponytail: three rays; a capsule cast (IPhysics.CastCollider) if thin rails or overhangs still let the body in.
    internal static System.Numerics.Vector3 Clamp(Entity? own, double x, double y, double z, System.Numerics.Quaternion orientation, System.Numerics.Vector3 lead)
    {
        float length = lead.Length();
        if (length < 0.05f || own == null || own.Scene == null) return lead;
        try { return Cast(own, x, y, z, orientation, lead, length); }
        catch (Exception) { return lead; }
    }

    private static System.Numerics.Vector3 Cast(Entity own, double x, double y, double z, System.Numerics.Quaternion orientation, System.Numerics.Vector3 lead, float length)
    {
        var physics = own.GetSession<IPhysics>();
        var scene = own.Scene;
        if (physics == null || scene == null) return lead;
        var up = System.Numerics.Vector3.Transform(System.Numerics.Vector3.UnitY, orientation);
        var dir = lead / length;
        double nearest = double.MaxValue;
        using Keen.VRage.Library.Memory.Buffer<SweepQueryHit> hits = default;
        foreach (float height in Heights)
        {
            var from = new Vector3D(x + up.X * height, y + up.Y * height, z + up.Z * height);
            var ray = RayCastArgs.CreateFromTo(from, from + new Vector3D(dir.X, dir.Y, dir.Z) * (length + Radius));
            hits.Clear();
            physics.CastRay(hits, in ray, CollisionPreset.Default);
            foreach (var hit in hits)
            {
                if (hit.Body == own.DEntity) continue;
                bool solid = VoxelStorageComponentBase.IsEntityVoxel((Scene: scene, Entity: hit.Body))
                    || Entity.TryGetFromDataEntity((Scene: scene, Entity: hit.Body)) is { } body && (body.TryGet<CubeGridComponent>() != null || body.TryGet<GridPhysicsComponent>() != null);
                if (solid) nearest = Math.Min(nearest, Vector3D.Distance(in from, in hit.Position));
            }
        }
        return nearest == double.MaxValue ? lead : dir * (float)Math.Clamp(nearest - Radius, 0.0, length);
    }
}
