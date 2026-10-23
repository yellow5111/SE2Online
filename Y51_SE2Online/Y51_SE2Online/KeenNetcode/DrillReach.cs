using System;
using System.Collections.Generic;
using HarmonyLib;
using Keen.Game2.Client.GameSystems.PlayerControl;
using Keen.Game2.Simulation.WorldObjects.Tools;
using Keen.Game2.Simulation.WorldObjects.Tools.Voxels;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Voxels.Components;
using Keen.VRage.Voxels.Storage.Planet.PlanetEnvironment;
using Y51_SE2Online.Core;
using Y51_SE2Online.SyncEngine;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>
/// F13: a remote player's hand drill on a planet boulder (a planet environment voxel entity on the planet's surface).
/// Server (postfix on DrillToolComponent.TryValidateDrillTarget): the game re-sweeps from its copy of the player's head
/// toward the claimed point and wants the claimed boulder as the first drillable hit. From a head a little off the
/// player's own (the copy lags a moving player, a crouch arrives later) the sweep grazes the planet's ground just before
/// the boulder and the claim was refused, while drilling the ground itself always passed. Such a refusal is taken when
/// <see cref="DrillReachRule"/> holds: the same sweep, from the server's head and within the tool's reach, stopped on
/// that boulder's planet (or the boulder) within a metre of the claimed point, in front of the player. A grid, a block or
/// anything else in the way still refuses; reach, safe zones and the drill's own rate stay the game's.
/// Player (prefix on the claim): a claim on this game's own copy of a planet entity (the server's copy never replaced
/// it) names an entity the server does not know; the game asks the server for its copy again (Sync, every 10 s at most).
/// Switch -y51online:drillreach:off. Logged "drill: ..." 5 s apart.
/// </summary>
internal static class DrillReach
{
	private const long ResyncEveryMs = 10_000;
	private static readonly bool Off = System.Linq.Enumerable.Contains(MpPlugin.LaunchArgs, MpOptions.Prefix + "drillreach:off");

	public static long Accepted, Refused, Resynced;
	private static long _nextLogMs, _nextClaimLogMs;
	private static readonly Dictionary<PlanetEntityId, long> ResyncAt = new();

	// VRage.Voxels is not publicized: the environment's own-copy table and its Sync request, by reflection.
	private static readonly System.Reflection.FieldInfo? IdByEntity = AccessTools.Field(typeof(PlanetEnvironmentComponent), "_idByEntity");
	private static readonly System.Reflection.MethodInfo? SyncMethod = AccessTools.Method(typeof(PlanetEnvironmentComponent), "Sync", new[] { typeof(PlanetEntityId) });

	public static void Install(Harmony harmony)
	{
		if (Off) { GameLog.Instance.Info("drill: boulder claims stay the game's own (drillreach:off)"); return; }
		harmony.Patch(AccessTools.Method(typeof(DrillToolComponent), "TryValidateDrillTarget") ?? throw new MissingMethodException("DrillToolComponent.TryValidateDrillTarget"),
			postfix: new HarmonyMethod(typeof(DrillReach), nameof(ValidatePostfix)));
		var claim = AccessTools.Method(typeof(Keen.Game2.Client.WorldObjects.Tools.DrillToolControllableComponent), "TrySetTarget");
		if (claim != null) harmony.Patch(claim, prefix: new HarmonyMethod(typeof(DrillReach), nameof(ClaimPrefix)));
	}

	private static void ValidatePostfix(DrillToolComponent __instance, Entity? target, Vector3 localPoint, Vector3 localNormal, ref HandDrillRawContact contact, ref bool __result)
	{
		if (__result || !AdminAccess.Hosting || target == null || __instance.CurrentToolAction == DrillComponent.ToolAction.None) return;
		try
		{
			// Only the sweep's refusal (a reach refusal keeps its shoulder distance) of a voxel claim on a planet environment entity.
			if (__instance._rejectedClaim is not { ShoulderDistance: <= 0f } || HandDrillContactResolver.GetEntityKind(target) != HandDrillContactKind.Voxel
				|| target.TryGet<PlanetEntityComponent>() is not { } boulder) return;
			Entity? holder = __instance._tool.Holder;
			if (holder == null || __instance._validationCollider is not { } collider || !MathHelper.IsValid(localPoint) || localNormal.LengthSquared() <= 1E-05f) return;
			long owner = TeamPermissions.OwnerOf(holder);
			// The co-op host's own character is not lagging: it runs in the same process as the server.
			bool remote = owner != 0 && Sync.PlayerIdentities.IsHuman(owner)
				&& !(MpPlugin.App?.ClientSession?.SessionComponents.TryGet<ClientPlayersSessionComponent>() is { } local && (long)local.LocalPlayerIdentity == owner);
			if (!remote) return;
			Vector3D world = WorldTransform.Transform((Vector3D)localPoint, target.Data.GetWorldTransform());
			if (!MathHelper.IsValid(in world)) return;
			// The claimed point must lie on the claimed boulder: no bounds, no override.
			double pad = __instance._definition.CastColliderRadius + __instance._definition.ContactValidationTolerance;
			bool onBoulder = BoundingBoxData.TryGetWorldOriented(target.Data) is { } bounds && bounds.Inflate(pad).Contains(in world);
			// The game's own front check (IsClaimReachableByCast): the point must not be behind the body.
			var body = __instance._origin.WorldTransform;
			bool inFront = Vector3D.Dot(world - body.Position, (Vector3D)body.Orientation.GetForward()) >= -0.05;
			bool reach = __instance.TryGetShoulderDistance(world, out float shoulder, out _) && shoulder <= __instance._definition.MaxShoulderDistance;
			if (holder.TryGet("InteractionOriginProvider") is not IToolOriginProvider { WorldTransform: var eye }) return;
			Vector3D toPoint = world - eye.Position;
			double length = toPoint.Length();
			if (length <= 1E-05) return;
			Vector3 forward = (Vector3)(toPoint / length), up = eye.Orientation.GetUp();
			if (Math.Abs(Vector3.Dot(forward, up)) > 0.99f) up = eye.Orientation.GetRight();
			var from = new WorldTransform(eye.Position, Quaternion.CreateFromForwardUp(forward, up));
			var hit = HandDrillContactResolver.Sweep(__instance._physics, __instance.Entity.Scene, holder, collider, in from,
				(float)length + __instance._definition.ContactValidationTolerance, __instance._definition.CastColliderRadius);
			Entity planet = (boulder.ParentPlanet ?? target).GetTopLevelParent();
			bool same = hit.HasContact && hit.Target != null && (hit.Target == target || hit.Target.GetTopLevelParent() == planet);
			double off = hit.HasContact ? Vector3D.Distance(hit.Position, world) : -1;
			bool accept = DrillReachRule.Accepts(remote, true, true, onBoulder, inFront, reach, hit.HasContact && hit.Kind == HandDrillContactKind.Voxel, same, off);
			if (accept)
			{
				contact = new HandDrillRawContact(HandDrillContactKind.Voxel, target, localPoint, Vector3.Normalize(localNormal), __instance.CurrentToolAction);
				__instance._rejectedClaim = null;
				__result = true;
				Accepted++;
			}
			else Refused++;
			long now = MpClock.NowMs();
			if (now < _nextLogMs) return;
			_nextLogMs = now + 5000;
			GameLog.Instance.Info($"drill: identity {owner}'s claim on {target.GetComposition()?.DebugNameShort ?? target.ToString()} missed by the server's own sweep, which stopped on "
				+ (hit.HasContact ? $"{hit.Kind} {hit.Target?.GetComposition()?.DebugNameShort ?? "?"} {off:0.00} m from the claimed point" : "nothing")
				+ $"; on the boulder {onBoulder}, shoulder {shoulder:0.00} m, in front {inFront}; {(accept ? "accepted (the same planet at that point)" : "refused")} ({Accepted} accepted, {Refused} refused so far)");
		}
		catch (Exception ex) { GameLog.Instance.WarnOnce("drill-reach", "drill: the boulder claim check failed (" + ex.GetType().Name + ": " + ex.Message + "); the game's own answer stands"); }
	}

	// The player's game: a claim on its own copy of a planet environment entity (the server's copy never replaced it).
	private static void ClaimPrefix(Keen.VRage.Multiplayer.Replications.FailureStrategies.NullOnReplicationFail<Entity> target)
	{
		try
		{
			if (!MpPlugin.Online || target.Value is not { } entity || entity.TryGet<PlanetEntityComponent>() is not { Id: { } id } planetEntity) return;
			if (planetEntity.ParentPlanet?.TryGet<PlanetEnvironmentComponent>() is not { } environment
				|| IdByEntity?.GetValue(environment) is not System.Collections.IDictionary local || !local.Contains(entity) || SyncMethod == null) return;
			long now = MpClock.NowMs();
			if (ResyncAt.TryGetValue(id, out long at) && now < at) return;
			if (ResyncAt.Count > 4096) ResyncAt.Clear();
			ResyncAt[id] = now + ResyncEveryMs;
			SyncMethod.Invoke(environment, new object[] { id });
			Resynced++;
			if (now < _nextClaimLogMs) return;
			_nextClaimLogMs = now + 5000;
			GameLog.Instance.Info($"drill: this game's drill touches its own copy of planet entity {id} ({entity.GetComposition()?.DebugNameShort}), which the server does not know; asked the server for its copy again ({Resynced} so far)");
		}
		catch (Exception ex) { GameLog.Instance.WarnOnce("drill-resync", "drill: asking the server for a planet entity failed (" + ex.GetType().Name + ": " + ex.Message + ")"); }
	}
}
