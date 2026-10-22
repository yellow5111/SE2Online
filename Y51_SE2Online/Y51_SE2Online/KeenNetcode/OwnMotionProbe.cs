#if !PULSAR
using System;
using System.Collections.Generic;
using System.Linq;
using Keen.Game2.Client.GameSystems.PlayerControl;
using Keen.Game2.Client.WorldObjects.Character;
using Keen.Game2.Simulation.WorldObjects.Characters;
using Keen.Game2.Simulation.WorldObjects.Movement;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Physics.Data;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>
/// Test rig for predicted own-character movement (<see cref="OwnMotion"/>).
/// Player, -y51online:selftest-ownmotion: 20 s after its character appears it flies a 60 s script (moves, stops,
/// direction changes, turns, boosts), logging how many frames each start took to move and every frame whose step is off
/// the body's own velocity by more than <see cref="VisibleMeters"/> or a quarter of the step (a visible jump). At 85 s it sends absurd inputs
/// for 2 s without moving itself (the server must clamp them), at 95 s it jumps its character 500 m locally (the server
/// must not follow and must pull it back).
/// Server, same switch: logs where it has each player's character and how fast it moves, and 110 s after a player's
/// inputs started teleports that character 30 m (the resync). -y51online:stall: the server sleeps 500 ms every 5 s.
/// -y51online:fakelag:&lt;ms&gt;: this game's sends wait that long.
/// </summary>
internal static class OwnMotionProbe
{
	private static bool On => MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "selftest-ownmotion");

	// A frame's step off the body's velocity by more than this, or by a quarter of the step itself at speed, is visible.
	private const double VisibleMeters = 0.1, VisibleShare = 0.25;

	private static bool _lagSet;

	/// <summary>The jetpack parameters Keen's server owns and replicates (boost, speed limit, thrust).</summary>
	internal static string Params(Entity e)
	{
		e.Data.TryGet<RigidBodyData>(out var body);
		e.Data.TryGet<ControlData>(out var control);
		string limit = e.Data.TryGet<SoftSpeedLimitData>(out var l) ? $"{l.Speed:0}" : "-";
		string boost = e.Data.TryGet<CharacterMovementComponent.JetpackModeData>(out var j) ? $"{(j.IsBoosting ? "boost" : "precision")} torque {j.MaxTorque:0}" : "no jetpack";
		string thrust = e.Data.TryGet<Keen.Game2.Simulation.WorldObjects.Shared.Movement.MaxThrustData>(out var t) ? t.Regular.ToString() : "-";
		string over = e.Data.TryGet<Keen.Game2.Simulation.WorldObjects.Shared.Movement.OverriddenThrustData>(out var o) ? $"{o.DirectionalThrust.Length():0}" : "-";
		string movement = e.Data.TryGet<Keen.Game2.Simulation.WorldObjects.Shared.Movement.MovementData>(out var m) ? $"{m.Definition.GetHashCode():x8}{(m.SuperDampeners ? " super" : "")}" : "-";
		var f = e.Data.GetWorldTransform().Orientation.GetForward();
		return $"{e.TryGet<CharacterMovementComponent>()?.CurrentMovementState} crouch {e.Data.Has<CharacterCrouchTag>()} grav {(e.Data.TryGet<Keen.VRage.Core.Game.GameSystems.Gravity.GravityEffectData>(out var gr) ? gr.GravitySum.Length() : 0):0.0} forward {f.X:0.000},{f.Y:0.000},{f.Z:0.000} soft {e.Data.Has<Keen.VRage.Physics.Character.SoftTransform>()} v {body.LinearVelocity.Length():0.0} input {control.Movement.X:0.##},{control.Movement.Y:0.##},{control.Movement.Z:0.##} turn {control.Rotation.Y:0.###} limit {limit} {boost} override {over} dampers {e.Data.Has<DampeningData>()} thrust {thrust} braking {(e.Data.TryGet<Keen.Game2.Simulation.WorldObjects.Shared.Movement.MaxThrustData>(out var tb) ? tb.Braking.ToString() : "-")} movement {movement} mass {(e.Data.TryGet<Keen.VRage.Physics.Data.RigidBodyMassProperties>(out var mp) ? mp.Mass : 0):0}";
	}

	public static void FakeLag()
	{
		if (_lagSet) return;
		_lagSet = true;
		string arg = MpPlugin.LaunchArgs.FirstOrDefault(a => a.StartsWith(MpOptions.Prefix + "fakelag:", StringComparison.OrdinalIgnoreCase));
		if (arg == null || !int.TryParse(arg[(MpOptions.Prefix.Length + 8)..], out int ms)) return;
		FramedSession.LagMs = ms;
		GameLog.Instance.Info($"selftest: this game's sends on the game connection wait {ms} ms");
		string? jitter = MpPlugin.LaunchArgs.FirstOrDefault(a => a.StartsWith(MpOptions.Prefix + "fakejitter:", StringComparison.OrdinalIgnoreCase));
		if (jitter?[(MpOptions.Prefix.Length + "fakejitter:".Length)..].Split(':') is [var every, var hold] && long.TryParse(every, out long e) && long.TryParse(hold, out long h))
		{
			FramedSession.JitterEveryMs = e;
			FramedSession.JitterHoldMs = h;
			GameLog.Instance.Info($"selftest: every {e} ms this game's sends are held {h} ms and leave together");
		}
	}

	// --- player ---

	private enum Act { None, Boost, Unboost, Jump, Sprint, Unsprint, Crouch }

	// One 15 s cycle, played four times: (start s, movement, yaw per tick, request at the start).
	private static readonly (double At, Vector3 Move, float Yaw, Act Act)[] Fly =
	{
		(0, new Vector3(0, 0, -1), 0, Act.None),
		(3, Vector3.Zero, 0, Act.None),
		(4, new Vector3(1, 0, 0), 0, Act.None),
		(6, Vector3.Zero, 0, Act.None),
		(7, new Vector3(0, 1, 1), 0, Act.None),
		(9, new Vector3(0, 0, -1), 0.01f, Act.None),
		(11, Vector3.Zero, 0, Act.None),
		(12, new Vector3(0, 0, -1), 0, Act.Boost),
		(14, Vector3.Zero, 0, Act.Unboost),
	};

	// On foot: walks, turns (the server's grounded yaw snap), a jump, a sprint and a crouched walk.
	private static readonly (double At, Vector3 Move, float Yaw, Act Act)[] Walk =
	{
		(0, new Vector3(0, 0, -1), 0, Act.None),
		(2, Vector3.Zero, 0, Act.None),
		(3, new Vector3(1, 0, 0), 0, Act.None),
		(4.5, new Vector3(0, 0, 1), 0, Act.None),
		(6, Vector3.Zero, 0, Act.None),
		(6.5, new Vector3(0, 0, -1), 0.02f, Act.None),
		(8.5, new Vector3(0, 0, -1), 0, Act.Jump),
		(9.5, new Vector3(0, 0, -1), 0, Act.Sprint),
		(11, Vector3.Zero, 0, Act.Unsprint),
		(11.5, new Vector3(0, 0, -1), -0.02f, Act.Crouch),
		(13.5, Vector3.Zero, 0, Act.Crouch),
	};

	private static (double At, Vector3 Move, float Yaw, Act Act)[]? _cycle;

	private static Entity? _body;
	private static long _foundMs;
	private static int _segment = -1, _visible, _starts, _steps;
	private static long _startFrame, _maxStartFrames = -1;
	private static bool _waitingMove, _boosting, _reported, _absurd, _absurdDone, _jumped, _jumpReported;
	private static double _maxStep, _maxOff, _travelled;
	private static Vector3D _last, _jumpedFrom;
	private static Vector3 _lastVelocity;

	/// <summary>Player's client session thread, after <see cref="OwnMotion.ClientTick"/>.</summary>
	public static void ClientTick(Session session)
	{
		if (PlanetProbe.On) PlanetProbe.ClientTick(session);
		if (!On) return;
		var controller = session.SessionComponents.TryGet<ClientPlayersSessionComponent>()?.LocalPlayerController;
		var own = controller?.ControlledEntities.Select(c => (c.Controllable as Component)?.Entity).FirstOrDefault(e => e != null && e.TryGet<CharacterLifeComponent>() != null);
		if (own == null) return;
		long now = MpClock.NowMs();
		var p = own.Data.GetWorldTransform().Position;
		own.Data.TryGet<RigidBodyData>(out var body);
		if (!ReferenceEquals(own, _body))
		{
			_body = own;
			_foundMs = now;
			_last = p;
			GameLog.Instance.Info($"selftest: own motion: body at {p.X:0.00}, {p.Y:0.00}, {p.Z:0.00}, state {own.TryGet<CharacterMovementComponent>()?.CurrentMovementState}; {(OwnMotion.Driving != null ? "this game predicts it" : "the server moves it")}; script in 20 s");
			return;
		}
		double t = (now - _foundMs) / 1000.0 - 20;
		double step = Vector3D.Distance(p, _last);
		if (t >= 0 && t < 60)
		{
			if (_cycle == null)
			{
				var state = own.TryGet<CharacterMovementComponent>()?.CurrentMovementState;
				_cycle = state == CharacterMovementState.Ground ? Walk : Fly;
				GameLog.Instance.Info($"selftest: own motion: {(_cycle == Walk ? "walking" : "flying")} script ({state})");
			}
			var cycle = _cycle;
			double inCycle = t % 15;
			int segment = Array.FindLastIndex(cycle, s => s.At <= inCycle) + (int)(t / 15) * cycle.Length;
			var (_, move, yaw, act) = cycle[segment % cycle.Length];
			if (segment != _segment)
			{
				_segment = segment;
				if (move != Vector3.Zero && _lastVelocity.Length() < 0.05f)
				{
					_waitingMove = true;
					_startFrame = OwnMotion.Frames;
				}
				var requests = own.TryGet<CharacterMovementControlComponent>();
				switch (act)
				{
				case Act.Boost: requests?.RequestBoost(_boosting = true); break;
				case Act.Unboost: requests?.RequestBoost(_boosting = false); break;
				case Act.Jump: requests?.RequestJump(); break;
				case Act.Sprint: requests?.RequestSprint(true); break;
				case Act.Unsprint: requests?.RequestSprint(false); break;
				case Act.Crouch: requests?.RequestCrouchToggle(); break;
				}
			}
			own.Data.Set(new ControlData { Movement = move, Rotation = new Vector3(0, yaw, 0) });
			if (_steps % 30 == 0) GameLog.Instance.Info($"selftest: own motion params t={t:0.0}: {Params(own)}");
			if (_waitingMove && step > 0.001)
			{
				_waitingMove = false;
				_starts++;
				long frames = OwnMotion.Frames - _startFrame;
				_maxStartFrames = Math.Max(_maxStartFrames, frames);
				if (_starts <= 3) GameLog.Instance.Info($"selftest: own motion: moved {frames} frames after the input; {(OwnMotion.Driving != null ? "this game predicts it" : "the server moves it")}");
			}
			// What the body's own velocity explains of this frame's step; the rest is a correction (or a collision).
			double off = ((Vector3)(p - _last) - _lastVelocity * (1 / 60f)).Length();
			_steps++;
			_travelled += step;
			_maxStep = Math.Max(_maxStep, step);
			_maxOff = Math.Max(_maxOff, off);
			if (off > Math.Max(VisibleMeters, VisibleShare * _lastVelocity.Length() / 60))
			{
				_visible++;
				GameLog.Instance.Info($"selftest: own motion: VISIBLE JUMP {off * 100:0} cm off the velocity at t={t:0.00} s");
			}
		}
		else if (t >= 60 && !_reported)
		{
			_reported = true;
			own.Data.Set(default(ControlData));
			if (_boosting) own.TryGet<CharacterMovementControlComponent>()?.RequestBoost(_boosting = false);
			GameLog.Instance.Info($"selftest: own motion: script done: {_travelled:0.0} m over {_steps} frames, {_starts} starts (slowest {_maxStartFrames} frames to move), largest step {_maxStep * 100:0} cm, largest step off the velocity {_maxOff * 100:0.0} cm, {_visible} visible jumps (> {VisibleMeters * 100:0} cm or a quarter of the step); {OwnMotion.DescribeCorrection()}");
		}
		else if (t >= 65 && !_absurd)
		{
			_absurd = true;
			_jumpedFrom = p;
			OwnMotion.ProbeSentMovement = new System.Numerics.Vector3(1000, 0, 0);
			GameLog.Instance.Info($"selftest: own motion: sending movement (1000, 0, 0) for 2 s without moving; {OwnMotion.DescribeCorrection()}");
		}
		else if (t >= 67 && !_absurdDone)
		{
			_absurdDone = true;
			OwnMotion.ProbeSentMovement = null;
			GameLog.Instance.Info($"selftest: own motion: absurd inputs done; this character is {Vector3D.Distance(p, _jumpedFrom):0.0} m from where it stood; {OwnMotion.DescribeCorrection()}");
		}
		else if (t >= 75 && !_jumped && OwnMotion.Driving != null)
		{
			_jumped = true;
			_jumpedFrom = p;
			var at = own.Data.GetWorldTransform();
			own.Data.SetWorldTransform(new WorldTransform(at.Position + new Vector3D(500, 0, 0), at.Orientation));
			GameLog.Instance.Info("selftest: own motion: jumped this character 500 m locally; the server must not follow");
		}
		else if (_jumped && t >= 78 && !_jumpReported)
		{
			_jumpReported = true;
			GameLog.Instance.Info($"selftest: own motion: 3 s after the local jump the character is {Vector3D.Distance(p, _jumpedFrom):0.0} m from where it jumped from (near 0 = pulled back); {OwnMotion.DescribeCorrection()}");
		}
		_last = p;
		_lastVelocity = body.LinearVelocity;
	}

	// --- server ---

	private static readonly Dictionary<ushort, (Vector3D At, long LogMs, long DrivenMs, bool Teleported, double MaxSpeed)> Server = new();
	private static long _nextStallMs;

	public static void ServerTick(Session session, IEnumerable<(ushort ClientId, Entity? Character)> players)
	{
		long now = MpClock.NowMs();
		if (MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "stall"))
		{
			if (_nextStallMs == 0) _nextStallMs = now + 5000;
			else if (now >= _nextStallMs)
			{
				_nextStallMs = now + 5000;
				GameLog.Instance.Info($"selftest: server stall 500 ms from {now}");
				System.Threading.Thread.Sleep(500);
			}
		}
		if (PlanetProbe.ServerOn) PlanetProbe.ServerTick(session, players);
		if (!On) return;
		foreach (var (clientId, character) in players)
		{
			if (character == null || SyncEngine.EngineCodec.IsDead(character)) continue;
			var at = character.Data.GetWorldTransform();
			character.Data.TryGet<RigidBodyData>(out var body);
			var (applied, guessed, merged, depth, driven) = OwnMotion.StatsOf(clientId);
			Server.TryGetValue(clientId, out var s);
			if (driven && s.DrivenMs == 0) s.DrivenMs = now;
			double speed = body.LinearVelocity.Length();
			s.MaxSpeed = Math.Max(s.MaxSpeed, speed);
			if (Vector3D.Distance(at.Position, s.At) > 0.01 && now - s.LogMs >= 250 || now - s.LogMs >= 5000)
			{
				s.LogMs = now;
				GameLog.Instance.Info($"selftest: own motion server params: {Params(character)}");
				GameLog.Instance.Info($"selftest: own motion server: ClientId {clientId} at {at.Position.X:0.00}, {at.Position.Y:0.00}, {at.Position.Z:0.00} speed {speed:0.0} m/s (max {s.MaxSpeed:0.0}) t={now}, {(driven ? "from its inputs" : "from Keen's ControlData")}, {applied} applied, {guessed} guessed, {merged} merged, {depth} waiting");
				s.MaxSpeed = 0;
			}
			s.At = at.Position;
			if (s.DrivenMs != 0 && !s.Teleported && now - s.DrivenMs > 110000)
			{
				s.Teleported = true;
				character.Data.Teleport(new WorldTransform(at.Position + new Vector3D(0, 30, 0), at.Orientation));
				GameLog.Instance.Info($"selftest: own motion server: teleported ClientId {clientId} 30 m up");
			}
			Server[clientId] = s;
		}
	}
}
#endif
