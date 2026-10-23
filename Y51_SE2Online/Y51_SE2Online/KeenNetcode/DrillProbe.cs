#if !PULSAR
using System;
using System.Linq;
using HarmonyLib;
using Keen.Game2.Simulation.WorldObjects.Tools.Voxels;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Mathematics;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>
/// F13 diagnosis: a joined co-op player's hand drill claims a target the server then refuses (active, never working).
/// Test rig (-y51online:drillprobe): logs, at most every 5 s, what the server made of a player's drill claim and what
/// the player's own drill probe found.
/// </summary>
internal static class DrillProbe
{
	private static long _nextMs;

	public static void Install(Harmony harmony)
	{
		if (!MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "drillprobe")) return;
		var validate = AccessTools.Method(typeof(DrillToolComponent), "TryValidateDrillTarget");
		if (validate == null) throw new MissingMethodException("DrillToolComponent.TryValidateDrillTarget");
		harmony.Patch(validate, postfix: new HarmonyMethod(typeof(DrillProbe), nameof(ValidatePostfix)));
		var claim = AccessTools.Method(typeof(Keen.Game2.Client.WorldObjects.Tools.DrillToolControllableComponent), "TrySetTarget");
		if (claim != null) harmony.Patch(claim, prefix: new HarmonyMethod(typeof(DrillProbe), nameof(ClaimPrefix)));
	}

	private static long _nextClaimMs, _nothing, _something;

	// The player's side: what its own probe hit and whether the server can know that entity.
	private static void ClaimPrefix(Keen.Game2.Client.WorldObjects.Tools.DrillToolControllableComponent __instance, Keen.VRage.Multiplayer.Replications.FailureStrategies.NullOnReplicationFail<Entity> target, Vector3 localPoint)
	{
		Entity? entity = target.Value;
		if (entity == null) _nothing++; else _something++;
		long now = MpClock.NowMs();
		if (now < _nextClaimMs) return;
		_nextClaimMs = now + 5000;
		string where = "";
		try
		{
			var origin = __instance.GetProbeOrigin(out float reach, out bool third);
			var t = Traverse.Create(__instance);
			var holder = t.Field("_tool").Property("Holder").GetValue<Entity>();
			var body = holder?.Data.GetWorldTransform().Position ?? default;
			where = $"; probe from {Vector3D.Distance(origin.Position, body):0.00} m off the body, forward {origin.Orientation.GetForward()}, reach {reach:0.00} m, third person {third}, cast collider {(t.Field("_castCollider").GetValue() == null ? "none" : "set")}, physics {(t.Field("_physics").GetValue() == null ? "none" : "set")}, holder {holder}";
		}
		catch (Exception ex) { where = "; origin unknown (" + ex.Message + ")"; }
		GameLog.Instance.Info($"drill: this game's probe since the last line: {_something} claims on something, {_nothing} on nothing{where}");
		_nothing = _something = 0;
		if (entity == null) return;
		GameLog.Instance.Info($"drill: this game claims {entity} ({entity.GetComposition()?.DebugNameShort}), network id {Y51_SE2Online.SyncEngine.ClientWorld.Current?.NetIdOf(entity)?.ToString() ?? "none"}, top-level {entity.GetTopLevelParent()}, local point {localPoint}");
	}

	private static void ValidatePostfix(DrillToolComponent __instance, Entity? target, Vector3 localPoint, bool __result)
	{
		long now = MpClock.NowMs();
		if (now < _nextMs) return;
		_nextMs = now + 5000;
		try
		{
			var t = Traverse.Create(__instance);
			var holder = t.Field("_tool").Property("Holder").GetValue<Entity>();
			var origin = t.Field("_origin").Property("WorldTransform").GetValue<Keen.VRage.Core.WorldTransform>().Position;
			var at = target == null ? default : Keen.VRage.Core.WorldTransform.Transform((Vector3D)localPoint, target.Data.GetWorldTransform());
			string rejected = t.Field("_rejectedClaim").GetValue()?.ToString() ?? "none";
			GameLog.Instance.Info($"drill: the server judged the claim of {holder} on {target?.ToString() ?? "nothing"}: {(__result ? "accepted" : "refused")}; action {t.Property("CurrentToolAction").GetValue()}, kind {(target == null ? "-" : HandDrillContactResolver.GetEntityKind(target).ToString())}, point {Vector3D.Distance(at, origin):0.00} m from the tool origin, holder at {holder?.Data.GetWorldTransform().Position.ToString() ?? "-"}, rejected claim {rejected}");
		}
		catch (Exception ex) { GameLog.Instance.WarnOnce("drill-probe", "drill: probe failed: " + ex.Message); }
	}
}
#endif
