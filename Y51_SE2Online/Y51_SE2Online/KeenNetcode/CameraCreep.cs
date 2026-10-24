using System;
using System.Linq;
using HarmonyLib;
using Keen.Game2.Client.GameSystems.CameraSystems.Adapters;
using Keen.Game2.Client.GameSystems.CameraSystems.Modes;
using Keen.Game2.Simulation.WorldObjects.Characters;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.DCS.Accessors;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Physics;
using Keen.VRage.Physics.Data;
using Keen.VRage.Physics.Queries;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>
/// F50, third-person camera pulled in to (and past) a player's own character while flying. Keen's camera has no positional
/// spring: each tick it is placed at the focus point of the character's WorldTransform minus Distance along its view
/// (ThirdPersonCameraComponent.CameraSpringUpdate), and the only thing that brings it closer is the obstruction cast
/// (CameraCollisionDetection: SafeDistance, then forced first person at the head once the hit is inside the body). That
/// cast ignores only the character's root entity (CastColliderObstacles, CanObstructView), so a body attached under the
/// character counts as an obstacle. A predicted character is a dynamic body here; whatever hangs under it is placed from
/// the parent's pose before the physics step and so trails it by a step's travel (1.7 m at 100 m/s), right on the camera
/// ray while flying forward. Bodies in the character's own hierarchy never obstruct its camera now.
/// -y51online:camignore:off leaves the cast as the game has it; -y51online:camdiag logs every 2 s while flying in third
/// person what the camera sees: distances, the first thing the cast hits, where the camera is against the character, and
/// the last correction shift.
/// </summary>
internal static class CameraCreep
{
	private static readonly bool IgnoreOff = MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "camignore:off");
	private static readonly bool Diag = MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "camdiag");

	public static void Install(Harmony harmony)
	{
		if (!IgnoreOff)
			harmony.Patch(AccessTools.Method(typeof(CharacterThirdPersonAdapterComponent), nameof(CharacterThirdPersonAdapterComponent.CanObstructView)),
				prefix: new HarmonyMethod(typeof(CameraCreep), nameof(CanObstructPrefix)));
		if (Diag)
			harmony.Patch(AccessTools.Method(typeof(ThirdPersonCameraComponent), "CameraCollisionDetection"),
				postfix: new HarmonyMethod(typeof(CameraCreep), nameof(CollisionPostfix)));
	}

	private static bool OwnHierarchy(Entity root, DEntity entity)
		=> entity != root.DEntity && new DEntityContext(root.Scene, entity).GetTopLevelParent().Entity == root.DEntity;

	private static bool CanObstructPrefix(CharacterThirdPersonAdapterComponent __instance, DEntity entity, ref bool __result)
	{
		if (!MpPlugin.Online || __instance.ObservedEntityRootInHierarchy is not { } root || !OwnHierarchy(root, entity)) return true;
		__result = false;
		return false;
	}

	// Camera job thread; one camera, so plain statics.
	private static long _nextLogMs, _frames, _obstructed, _firstPerson, _shiftsAtLog;
	private static float _minSafe = float.MaxValue;

	private static void CollisionPostfix(ThirdPersonCameraComponent __instance, ref ThirdPersonCameraComponent.ThirdPersonCameraData cameraData)
	{
		try
		{
			var own = OwnMotion.OwnEntity;
			if (own == null || !ReferenceEquals(__instance._observedEntity, own)
				|| own.TryGet<CharacterMovementComponent>()?.CurrentMovementState != CharacterMovementState.Jetpack) return;
			_frames++;
			if (cameraData.IsObstructed) { _obstructed++; _minSafe = Math.Min(_minSafe, cameraData.SafeDistance); }
			if (cameraData.InFirstPerson) _firstPerson++;
			long now = MpClock.NowMs();
			if (now < _nextLogMs) return;
			_nextLogMs = now + 2000;
			GameLog.Instance.Info("camera: " + Describe(__instance, cameraData, own));
			_frames = _obstructed = _firstPerson = 0;
			_minSafe = float.MaxValue;
			_shiftsAtLog = OwnMotion.Shifts;
		}
		catch (Exception ex) { GameLog.Instance.WarnOnce("camera-creep-diag", "camera: diagnostic failed: " + ex.Message); }
	}

	private static string Describe(ThirdPersonCameraComponent camera, in ThirdPersonCameraComponent.ThirdPersonCameraData data, Entity own)
	{
		var desired = camera.Data.Get<ThirdPersonCameraComponent.ThirdPersonDesiredWorldTransform>();
		var requested = desired.IsActive ? desired.Value : camera.Data.GetWorldTransform();
		var focus = camera.ComputeFocusPointWorldPosition(data);
		var body = own.Data.GetWorldTransform();
		own.Data.TryGet<RigidBodyData>(out var rb);
		var forward = (Vector3D)requested.Orientation.GetForward();
		var placed = camera._unobstructedWorldTransform.Position;
		// Positive: the character is ahead of the camera along the view, as it should be; negative: the camera is past it.
		double ahead = Vector3D.Dot(focus - placed, forward);
		double side = Math.Sqrt(Math.Max(0, Vector3D.DistanceSquared(in focus, in placed) - ahead * ahead));
		var spring = requested.Position - placed;
		return $"speed {rb.LinearVelocity.Length():0.0} m/s; distance target {data.TargetDistance:0.00} now {data.Distance:0.00} safe {data.SafeDistance:0.00} (target {data.TargetSafeDistance:0.00}) min {camera.MinDistance:0.00}"
			+ $"; obstructed behind {data.IsObstructedBehind} front {data.IsObstructedInFront} first person {data.InFirstPerson} snap {camera._snappingState}"
			+ $"; last {_frames} frames: {_obstructed} obstructed (closest safe {(_obstructed > 0 ? _minSafe.ToString("0.00") : "-")}), {_firstPerson} forced first person"
			+ $"; character {ahead:0.00} m ahead of the camera, {side:0.00} m off its axis, camera {spring.Length():0.00} m from where the spring put it"
			+ $"; body {Vector3D.Distance(in body.Position, in focus):0.00} m from the focus"
			+ $"; cast: {Probe(camera, data, focus, requested, own, rb.LinearVelocity)}"
			+ $"; shifts {OwnMotion.Shifts - _shiftsAtLog} since the last line, last {OwnMotion.LastShiftMeters * 100:0.0} cm";
	}

	// The game's near-plane sweep from the focus to where the camera wants to be, walked hit by hit as
	// CastColliderObstacles does; names every hit up to the first one that counts.
	private static string Probe(ThirdPersonCameraComponent camera, in ThirdPersonCameraComponent.ThirdPersonCameraData data, Vector3D focus, in WorldTransform requested, Entity own, Vector3 ownVelocity)
	{
		var offset = requested.Position - focus;
		double length = offset.Length();
		if (length < 1e-3) return "camera at the focus";
		var args = new ColliderCastArgs
		{
			Transform = new WorldTransform(focus, requested.Orientation),
			Collider = camera.GetNearPlaneCollider(),
			Direction = (Vector3)offset,
		};
		args.EntityFilter = camera._filterForObstacles;
		var root = camera._thirdPersonAdapter.ObservedEntityRootInHierarchy;
		args.IgnoredEntities.Add(root.DEntity);
		var said = new System.Text.StringBuilder();
		try
		{
			using var hits = Keen.VRage.Library.Memory.Buffer.Inline1<SweepQueryHit>("CameraCreep");
			for (int i = 0; i < 8; i++)
			{
				hits.Clear();
				camera._physics.CastCollider(hits, in args, CollisionPreset.Closest);
				if (hits.Count == 0) return said.Length == 0 ? "clear" : said + "nothing past those";
				var hit = hits[0];
				var ctx = new DEntityContext(own.Scene, hit.Body);
				string name = Entity.TryGetFromDataEntity(ctx) is { } e ? e.DebugName : "?";
				bool mine = OwnHierarchy(root, hit.Body);
				bool small = ctx.TryGet<BoundingBoxData>(out var bb) && bb.BoundingBox.Volume() < camera._definition.IgnoreCollisionVolumeThreshold;
				string moving = ctx.TryGet<RigidBodyData>(out var other) ? $", {(other.LinearVelocity - ownVelocity).Length():0.0} m/s against this character" : ", no body data";
				said.Append($"{name} at {hit.Fraction * length:0.00} m{(mine ? " (under this character)" : "")}{(small ? " (small)" : "")}{moving}");
				if (!mine && !small && camera._thirdPersonAdapter.CanObstructView(hit.Body)) return said + " counts";
				said.Append(" ignored, ");
				args.IgnoredEntities.Add(hit.Body);
			}
			return said + "...";
		}
		finally { args.Dispose(); }
	}
}
