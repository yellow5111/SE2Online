#if !PULSAR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Keen.Game2.Simulation.WorldObjects.Characters;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.Components;
using Keen.VRage.Game.Components.Animations;
using Keen.VRage.Multiplayer.Transport.OutOfProcess;
using Keen.VRage.Physics.Data;
using Y51_SE2Online.Core;
using Y51_SE2Online.SyncEngine;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.Engine;

/// <summary>
/// Test rig (-y51online:syncprobe): every 250 ms the server and each joined game log, per replicated character and moving
/// grid, the network id, wall clock, position, velocity, movement state and the animation blackboard. Both sides run on one
/// PC in the rig, so the same wall clock lines them up: tools/syncprobe.py pairs each client line with the server's state
/// one playout delay earlier and reports position error and blackboard mismatches.
/// </summary>
internal static class SyncProbe
{
	public static bool Requested => MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "syncprobe");

	private static long _serverNextMs, _clientNextMs;

	private static bool Walk => MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "selftest-walk");

	private static long _walkStartMs;
	private static bool _boosting;
	private static long _jumpCycle = -1;

	// Test rig (-y51online:selftest-walk, host): the host's own character walks or flies a 20 s script on the server (turning
	// forward, sprint/boost, jumps, stop), so a joined game has a moving, animating remote player to compare.
	private static void WalkHost(Session session)
	{
		if (session.SessionComponents.TryGet<Keen.Game2.Simulation.GameSystems.Identities.IdentitiesSessionComponent>() is not { } ids) return;
		long me = (long)ids.LocalIdentity;
		Entity? own = null;
		foreach (Entity c in session.GetEntitiesOfType<CharacterLifeComponent>())
			if (c.Get<CharacterLifeComponent>().IsAlive && TeamPermissions.OwnerOf(c) == me) { own = c; break; }
		if (own == null) return;
		long now = MpClock.NowMs();
		if (_walkStartMs == 0) _walkStartMs = now + 30_000;
		if (now < _walkStartMs) return;
		double t = (now - _walkStartMs) % 20_000 / 1000.0;
		var control = new Keen.Game2.Simulation.WorldObjects.Movement.ControlData();
		if (t < 14) control.Movement = new Keen.VRage.Library.Mathematics.Vector3(0, t is > 10 and < 12 ? 1 : 0, -1);
		if (t < 14) control.Rotation = new Keen.VRage.Library.Mathematics.Vector3(0, (float)(0.6 * Math.Sin(t)), 0);
		own.Data.Set(control);
		own.Data.Set(new Keen.Game2.Simulation.WorldObjects.Movement.AngularControlData { TargetAngularVelocity = control.Rotation });
		var requests = own.TryGet<CharacterMovementServerComponent>();
		bool boost = t is > 6 and < 9;
		if (requests != null && boost != _boosting) { requests.RequestBoost(boost); requests.RequestSprint(boost); _boosting = boost; }
		long cycle = (now - _walkStartMs) / 20_000;
		if (requests != null && t >= 9.5 && cycle != _jumpCycle) { _jumpCycle = cycle; requests.RequestJump(); }
	}

	public static void Tick(Session session, bool server)
	{
		if (server && Walk) WalkHost(session);
		if (!Requested) return;
		long now = MpClock.NowMs();
		ref long next = ref server ? ref _serverNextMs : ref _clientNextMs;
		if (now < next) return;
		next = now + 250;
		var mapper = server
			? session.SessionComponents.TryGet<Keen.VRage.Multiplayer.Transport.OutOfProcess.Server.OutOfProcessServerPeerTransportSessionComponent>()?.NetworkIdMapper
			: session.SessionComponents.TryGet<Keen.VRage.Multiplayer.Transport.OutOfProcess.Client.OutOfProcessClientPeerTransportSessionComponent>()?.NetworkIdMapper;
		if (mapper == null) return;
		var sb = new StringBuilder();
		int n = 0;
		foreach (Entity character in session.GetEntitiesOfType<CharacterLifeComponent>())
		{
			if (n++ >= 16) break;
			Line(sb, server, now, character, mapper, "char");
		}
		n = 0;
		foreach (Entity grid in session.GetEntitiesOfType<CubeGridComponent>())
		{
			if (!grid.Data.TryGet<RigidBodyData>(out var body) || body.LinearVelocity.LengthSquared() < 0.25f) continue;
			if (n++ >= 8) break;
			Line(sb, server, now, grid, mapper, "grid");
		}
		if (sb.Length > 0) GameLog.Instance.Info(sb.ToString().TrimEnd('\n'));
	}

	private static void Line(StringBuilder sb, bool server, long now, Entity e, NetworkIdMapper mapper, string kind)
	{
		if (EngineCodec.IsDead(e) || mapper.TryGetId(e) is not { } id) return;
		var at = e.Data.GetWorldTransform();
		e.Data.TryGet<RigidBodyData>(out var body);
		sb.Append($"syncprobe {(server ? "S" : "C")} {now} {kind} {id.Value} p={at.Position.X:0.000},{at.Position.Y:0.000},{at.Position.Z:0.000} v={body.LinearVelocity.X:0.00},{body.LinearVelocity.Y:0.00},{body.LinearVelocity.Z:0.00}");
		if (kind == "char")
		{
			var movement = e.TryGet<CharacterMovementComponent>();
			sb.Append($" ms={movement?.CurrentMovementState.ToString() ?? "-"} crouch={e.Data.Has<CharacterCrouchTag>()}");
			if (e.TryGet<AnimationBlackboardComponent>()?.Blackboard is { } bb)
			{
				sb.Append(" bb=");
				foreach (var (key, value) in bb.BlackboardValues.OrderBy(kv => kv.Key.ToString(), StringComparer.Ordinal))
					sb.Append(key).Append(':').Append(value.Type == Keen.VRage.Core.Game.GameSystems.Animations.AnimatedValueType.Float ? value.GetValue<float>().ToString("0.0") : value.ToString()).Append(';');
			}
		}
		sb.Append('\n');
	}
}
#endif
