#if !PULSAR
using System;
using System.Collections.Generic;
using System.Linq;
using Keen.Game2.Client.GameSystems.PlayerControl;
using Keen.Game2.Client.WorldObjects.Character;
using Keen.Game2.Simulation.WorldObjects.Characters;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.Pilotable;
using Keen.Game2.Simulation.WorldObjects.Movement;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.Core.Game.GameSystems.Gravity;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.Accessors;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Library.Threading;
using Keen.VRage.Physics.Character;
using Keen.VRage.Physics.Data;
using Y51_SE2Online.Core;
using Y51_SE2Online.SyncEngine;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>
/// Test rig for predicted own movement in gravity (-y51online:selftest-planet, player side). A seated spawn (a planet
/// lander) leaves its seat once the ship rests; on foot it runs a scripted lap of named actions twice (walk, turn,
/// strafe, sprint, crouch, jumps, jetpack take-off, hover, climb, boost, dampeners off and on, jetpack off and landing),
/// then sits back in the seat and leaves it again. Per action it logs the prediction error against the server's acks,
/// eased steps, snaps, visible jumps, how many frames a start or a toggle took to show, and the ground grade walked.
/// Server, -y51online:selftest-planet-server: a player's character that is out of its seat more than
/// <see cref="PlaceAboveMeters"/> above the ground (SE2 Online's landers hover 2 km up for their pilot) is put on the
/// ground below it.
/// </summary>
internal static class PlanetProbe
{
	internal static bool On => MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "selftest-planet") || Observer;

	// A second player's game that only leaves its seat and stands; the server puts it next to the scripted player.
	internal static bool Observer => MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "selftest-planet-observer");

	internal static bool ServerOn => MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "selftest-planet-server");

	private const double PlaceAboveMeters = 40;

	private enum Act { None, Jump, Sprint, Unsprint, Crouch, Jet, Damp, Boost, Unboost }

	private static readonly (double At, string Name, Vector3 Move, float Yaw, Act Act)[] Lap =
	{
		(0, "walk", new Vector3(0, 0, -1), 0, Act.None),
		(3, "stop", Vector3.Zero, 0, Act.None),
		(4.5, "walk+turn", new Vector3(0, 0, -1), 0.02f, Act.None),
		(7.5, "strafe", new Vector3(1, 0, 0), 0, Act.None),
		(9, "backward", new Vector3(0, 0, 1), 0, Act.None),
		(10.5, "stop", Vector3.Zero, 0, Act.None),
		(11.5, "sprint", new Vector3(0, 0, -1), 0, Act.Sprint),
		(14.5, "sprint stop", Vector3.Zero, 0, Act.Unsprint),
		(15.5, "crouch", Vector3.Zero, 0, Act.Crouch),
		(16.5, "crouch walk", new Vector3(0, 0, -1), 0, Act.None),
		(19.5, "stand up", Vector3.Zero, 0, Act.Crouch),
		(21, "jump standing", Vector3.Zero, 0, Act.Jump),
		(23.5, "run", new Vector3(0, 0, -1), 0, Act.None),
		(24.5, "jump running", new Vector3(0, 0, -1), 0, Act.Jump),
		(26.5, "stop", Vector3.Zero, 0, Act.None),
		(27.5, "jetpack on (hover)", Vector3.Zero, 0, Act.Jet),
		(30, "jetpack up", new Vector3(0, 1, 0), 0, Act.None),
		(31.5, "hover", Vector3.Zero, 0, Act.None),
		(32, "jetpack forward", new Vector3(0, 0, -1), 0, Act.None),
		(34, "jetpack boost", new Vector3(0, 0, -1), 0, Act.Boost),
		(38, "boost off (brake)", Vector3.Zero, 0, Act.Unboost),
		(40, "dampeners off", Vector3.Zero, 0, Act.Damp),
		(41.5, "dampeners on", Vector3.Zero, 0, Act.Damp),
		(43.5, "jetpack off (fall, land)", Vector3.Zero, 0, Act.Jet),
		(47, "walk back+turn", new Vector3(0, 0, 1), -0.02f, Act.None),
		(50, "stop", Vector3.Zero, 0, Act.None),
	};

	// After the laps: the server (selftest-planet-server) leaves the jetpack a few percent of its fuel on the third boost.
	private static readonly (double At, string Name, Vector3 Move, float Yaw, Act Act)[] FuelLap =
	{
		(0, "fuel: jetpack on", Vector3.Zero, 0, Act.Jet),
		(2.5, "fuel: boost until empty", new Vector3(0, 0, -1), 0, Act.Boost),
		(10, "fuel: out (fall, land)", Vector3.Zero, 0, Act.Unboost),
	};

	private const double FuelSeconds = 16;

	// Then the server (selftest-planet-server) puts the character on top of its lander and drags the lander along the
	// ground at GridSpeed: walking on a moving grid in gravity.
	private static readonly (double At, string Name, Vector3 Move, float Yaw, Act Act)[] GridLap =
	{
		(0, "grid: placed", Vector3.Zero, 0, Act.None),
		(3, "grid: stand (moving)", Vector3.Zero, 0, Act.None),
		(7, "grid: walk (moving)", new Vector3(0, 0, -0.5f), 0, Act.None),
		(10, "grid: walk+turn (moving)", new Vector3(0, 0, -0.5f), 0.02f, Act.None),
		(12, "grid: stop (moving)", Vector3.Zero, 0, Act.None),
	};

	private const double GridSeconds = 16;
	private const float GridSpeed = 3;

	// A velocity change this big in one tick is a collision (or a correction): both sides log theirs.
	private const float JumpSpeed = 4;
	private static int _jumpLogs;

	internal static string Contacts(Entity e)
	{
		if (!e.Data.TryGet<CharacterSupportData>(out var support) || support.CharacterContacts.Count == 0) return "no contacts";
		var names = new List<string>();
		foreach (var c in support.CharacterContacts)
			names.Add($"{(Entity.TryGetFromDataEntity(new DEntityContext(e.Scene, c.Entity)) is { } touched ? touched.ToString() : "?")}{(c.IsSupportContact ? " (support)" : "")}");
		return "contacts " + string.Join(", ", names);
	}

	private const double LapSeconds = 52, VisibleMeters = 0.1, VisibleShare = 0.25;
	private const int Laps = 2;

	private sealed class Seg
	{
		public string Name = "";
		public int Frames, Visible, Snaps, Rebases;
		public long Acks;
		public double ErrorSum, ErrorMax, StepMax, TurnSum, TurnMax, MaxOff, Travel, Rise;
		public int MoveFrames = -1, ToggleFrames = -1;
		public bool WaitMove, WaitToggle;
		public Func<Entity, bool>? Toggled;
		public Vector3D From;
	}

	private enum Phase { Find, Seated, Leaving, Settle, Script, SitBack, Sitting, LeaveAgain, Done }

	private static Phase _phase;
	private static Entity? _body;
	private static ISeat? _seat;
	private static long _phaseMs, _restSinceMs, _requestMs, _requestFrame;
	private static double _scriptStart;
	private static int _segment = -1, _lap;
	private static Seg? _seg;
	private static readonly List<Seg> Done = new();
	private static Vector3D _last;
	private static Vector3 _lastVelocity;
	private static bool _boosting, _sprinting;

	// Round trip of an input to its ack (wall clock of the send of each sequence number).
	private static readonly Dictionary<uint, long> SentMs = new();
	private static readonly List<double> RoundTrips = new();

	private static Entity? Own(Session session)
	{
		if (OwnMotion.Driving is { } driving) return driving;
		long me = OwnMotion.LocalIdentity(session);
		if (me == 0) return null;
		foreach (Entity character in session.GetEntitiesOfType<CharacterLifeComponent>())
			if (!EngineCodec.IsDead(character) && character.Get<CharacterLifeComponent>().IsAlive && TeamPermissions.OwnerOf(character) == me) return character;
		return null;
	}

	private static ISeat? SeatOf(Entity character) => character.AsInterface<ISeatable>()?.CurrentSeat;

	private static Entity Root(Entity e)
	{
		for (int i = 0; e.TryGet<HierarchyComponent>()?.Parent is { } up && i < 16; i++) e = up;
		return e;
	}

	private static Vector3 Up(Entity e) => e.Data.TryGet<GravityEffectData>(out var g) && g.GravitySum.LengthSquared() > 1e-4f ? -Vector3.Normalize(g.GravitySum) : Vector3.Zero;

	private static CharacterMovementState? State(Entity e) => e.TryGet<CharacterMovementComponent>()?.CurrentMovementState;

	private static void Enter(Phase phase, long now, string why)
	{
		_phase = phase;
		_phaseMs = now;
		GameLog.Instance.Info($"selftest: planet: {phase}: {why}");
	}

	/// <summary>Player's client session thread, after <see cref="OwnMotion.ClientTick"/>.</summary>
	public static void ClientTick(Session session)
	{
		long now = MpClock.NowMs();
		uint seq = OwnMotion.Seq;
		if (seq != 0 && !SentMs.ContainsKey(seq)) SentMs[seq] = now;
		uint ack = OwnMotion.ClientCorrection.NewestAck;
		if (ack != 0 && SentMs.Remove(ack, out long sent)) RoundTrips.Add(now - sent);
		if (SentMs.Count > 2000) SentMs.Clear();

		var own = Own(session);
		if (own == null) return;
		if (!ReferenceEquals(own, _body))
		{
			_body = own;
			_last = own.Data.GetWorldTransform().Position;
			if (_phase is Phase.Find or Phase.Settle or Phase.Script)
				Enter(SeatOf(own) != null ? Phase.Seated : Phase.Settle, now, $"body {own}, state {State(own)}, gravity {(own.Data.TryGet<GravityEffectData>(out var g) ? g.GravitySum.Length() : 0):0.00} m/s2");
		}
		var p = own.Data.GetWorldTransform().Position;
		own.Data.TryGet<RigidBodyData>(out var body);
		var requests = own.TryGet<CharacterMovementControlComponent>();
		FallingCheck(session, own, p, now);
		switch (_phase)
		{
		case Phase.Seated:
		{
			var seat = SeatOf(own);
			if (seat == null) { Enter(Phase.Settle, now, "no longer seated"); break; }
			_seat = seat;
			var grid = Root(seat.Entity);
			grid.Data.TryGet<RigidBodyData>(out var gb);
			if (gb.LinearVelocity.Length() > 0.5f) _restSinceMs = now;
			if (now - _phaseMs > 20000 && now - _restSinceMs > 5000)
			{
				_requestMs = now;
				_requestFrame = OwnMotion.Frames;
				GameLog.Instance.Info($"selftest: planet: leaving the seat on {grid} (speed {gb.LinearVelocity.Length():0.00} m/s)");
				seat.TryClearPilotAsync().SkipWait();
				Enter(Phase.Leaving, now, "asked to leave the seat");
			}
			break;
		}
		case Phase.Leaving:
		case Phase.LeaveAgain:
			if (SeatOf(own) == null && OwnMotion.Driving != null)
			{
				GameLog.Instance.Info($"selftest: planet: on foot and predicted {now - _requestMs} ms ({OwnMotion.Frames - _requestFrame} predicted frames) after asking to leave the seat; state {State(own)}; {OwnMotion.DescribeCorrection()}");
				Enter(_phase == Phase.Leaving ? Phase.Settle : Phase.Done, now, "left the seat");
				if (_phase == Phase.Done) Report();
			}
			else if (now - _requestMs > 15000) { Enter(Phase.Done, now, "still seated 15 s after asking to leave"); Report(); }
			break;
		case Phase.Settle:
			if (SeatOf(own) != null) { Enter(Phase.Seated, now, "seated"); break; }
			// The join still finishing (HUD, inventories, nametags arriving) hitched the first walk of the script.
			if (OwnMotion.Driving == null || ClientWorld.Progress.Stage != Sync.JoinStage.Playing || Vector3D.Distance(p, _last) > 20) { _phaseMs = now; break; }
			if (State(own) == CharacterMovementState.Jetpack && Up(own) != Vector3.Zero && now - _phaseMs > 2000 && now - _requestMs > 3000)
			{
				_requestMs = now;
				requests?.RequestStateToggle();
				GameLog.Instance.Info("selftest: planet: jetpack off to stand");
			}
			if (State(own) == CharacterMovementState.Ground && now - _phaseMs > 5000 && Observer)
			{
				Enter(Phase.Done, now, "observer: standing and watching");
				break;
			}
			if (State(own) == CharacterMovementState.Ground && now - _phaseMs > 5000)
			{
				_scriptStart = now / 1000.0;
				_segment = -1;
				_lap = 0;
				Enter(Phase.Script, now, $"script starts, gravity {(own.Data.TryGet<GravityEffectData>(out var g) ? g.GravitySum.Length() : 0):0.00} m/s2");
			}
			break;
		case Phase.Script:
			Script(own, requests, now, p, body);
			break;
		case Phase.SitBack:
			if (_seat == null || EngineCodec.IsDead(_seat.Entity)) { Enter(Phase.Done, now, "no seat to go back to"); Report(); break; }
			if (now - _phaseMs > 3000 && _requestMs < _phaseMs)
			{
				_requestMs = now;
				GameLog.Instance.Info($"selftest: planet: sitting back down, {Vector3D.Distance(p, _seat.Entity.Data.GetWorldTransform().Position):0.0} m from the seat");
				_seat.TrySetPilotAsync(own).SkipWait();
			}
			if (_requestMs >= _phaseMs && SeatOf(own) != null)
			{
				GameLog.Instance.Info($"selftest: planet: seated {now - _requestMs} ms after asking; {OwnMotion.DescribeCorrection()}");
				Enter(Phase.Sitting, now, "seated");
			}
			else if (_requestMs >= _phaseMs && now - _requestMs > 15000) { Enter(Phase.Done, now, "not seated 15 s after asking"); Report(); }
			break;
		case Phase.Sitting:
			if (now - _phaseMs > 4000)
			{
				_requestMs = now;
				_requestFrame = OwnMotion.Frames;
				(SeatOf(own) ?? _seat)?.TryClearPilotAsync().SkipWait();
				Enter(Phase.LeaveAgain, now, "asked to leave the seat again");
			}
			break;
		}
		_last = p;
		_lastVelocity = body.LinearVelocity;
	}

	// On foot and not on the ground for over a second while predicted: what this game's planet physics has under the
	// character (co-op runs fell through the ground for half a minute after the server put the player on it).
	private static int _fallingFrames, _fallLogs;
	private static long _fallLogMs;

	private static void FallingCheck(Session session, Entity own, Vector3D p, long now)
	{
		bool falling = OwnMotion.Driving != null && State(own) == CharacterMovementState.Ground
			&& own.Data.TryGet<CharacterPhysicsState>(out var ps) && ps.State != CharacterState.OnGround;
		_fallingFrames = falling ? _fallingFrames + 1 : 0;
		if (_fallingFrames < 60 || now - _fallLogMs < 1000 || _fallLogs >= 20) return;
		_fallLogMs = now;
		_fallLogs++;
		try
		{
			var planet = session.GetEntitiesOfType<Keen.VRage.Voxels.Components.VoxelPlanetComponent>().OrderBy(e => Vector3D.DistanceSquared(e.Data.GetWorldTransform().Position, p)).FirstOrDefault();
			string surface = "no planet", chunks = "no voxel physics";
			if (planet != null)
			{
				var voxels = planet.Get<Keen.VRage.Voxels.Components.VoxelPlanetComponent>();
				var local = WorldTransform.TransformInv(p, planet.Data.GetWorldTransform());
				surface = voxels.QuerySurfacePosition((Vector3)Vector3D.Normalize(local) * voxels.Radius, out Keen.VRage.Voxels.Storage.Planet.SampledPlanetPosition ground)
					? $"{local.Length() - ground.SurfaceDistance:0.00} m above the smooth surface" : "surface query failed";
				var physicsEntity = session.GetEntitiesOfType<Keen.VRage.Voxels.Components.VoxelPhysicsComponent>().OrderBy(e => Vector3D.DistanceSquared(e.Data.GetWorldTransform().Position, p)).FirstOrDefault();
				if (physicsEntity?.TryGet<Keen.VRage.Voxels.Components.VoxelPhysicsComponent>() is { } physics)
				{
					var t = HarmonyLib.Traverse.Create(physics);
					var all = t.Field("_chunks").GetValue<System.Collections.IDictionary>();
					var pending = t.Field("_pendingMaterialize").GetValue<System.Collections.IDictionary>();
					var bounds = HarmonyLib.AccessTools.Method(typeof(Keen.VRage.Voxels.Components.VoxelPhysicsComponent), "GetChunkWorldBounds");
					int here = 0, built = 0;
					foreach (System.Collections.DictionaryEntry c in all)
						if (((BoundingBoxD)bounds.Invoke(physics, new[] { c.Key })!).Contains(in p))
						{
							here++;
							if (HarmonyLib.Traverse.Create(c.Value).Field("Collider").GetValue() != null) built++;
						}
					int pendingHere = 0;
					foreach (System.Collections.DictionaryEntry c in pending)
						if (((BoundingBoxD)bounds.Invoke(physics, new[] { c.Key })!).Contains(in p)) pendingHere++;
					chunks = $"voxel physics {all.Count} chunks ({here} here, {built} with a collider), {pending.Count} pending ({pendingHere} here)";
				}
			}
			GameLog.Instance.Info($"selftest: planet: falling on foot for {_fallingFrames} frames at {p.X:0.00}, {p.Y:0.00}, {p.Z:0.00}: {surface}; {chunks}; dynamic tag {own.Data.Has<DynamicTag>()}, {Contacts(own)}");
		}
		catch (Exception ex) { GameLog.Instance.Info($"selftest: planet: falling check failed: {ex.GetType().Name}: {ex.Message}"); }
	}

	private static void Script(Entity own, CharacterMovementControlComponent? requests, long now, Vector3D p, in RigidBodyData body)
	{
		double t = now / 1000.0 - _scriptStart;
		int lap = Math.Min((int)(t / LapSeconds), Laps);
		if (t >= Laps * LapSeconds + FuelSeconds) lap = Laps + 1;
		if (t >= Laps * LapSeconds + FuelSeconds + GridSeconds)
		{
			own.Data.Set(default(ControlData));
			Close();
			if (_boosting) requests?.RequestBoost(_boosting = false);
			if (_sprinting) requests?.RequestSprint(_sprinting = false);
			Enter(_seat != null ? Phase.SitBack : Phase.Done, now, "script done");
			if (_phase == Phase.Done) Report();
			return;
		}
		var table = lap < Laps ? Lap : lap == Laps ? FuelLap : GridLap;
		double lapStart = lap <= Laps ? lap * LapSeconds : Laps * LapSeconds + FuelSeconds;
		int segment = Array.FindLastIndex(table, s => s.At <= t - lapStart) + lap * Lap.Length;
		var (_, name, move, yaw, act) = table[segment - lap * Lap.Length];
		if (segment != _segment)
		{
			_segment = segment;
			Close();
			var seg = _seg = new Seg { Name = name, From = p };
			var c = OwnMotion.ClientCorrection;
			seg.Acks = c.Acks; seg.ErrorSum = c.ErrorSum; seg.TurnSum = c.TurnErrorSum; seg.Snaps = (int)c.Snaps; seg.Rebases = (int)c.Rebases;
			c.ErrorMax = 0; c.StepMax = 0; c.TurnErrorMax = 0;
			if (move != Vector3.Zero && _lastVelocity.Length() < 0.05f) seg.WaitMove = true;
			bool crouched = own.Data.Has<CharacterCrouchTag>();
			var state = State(own);
			bool damped = own.Data.Has<DampeningData>();
			switch (act)
			{
			case Act.Jump: requests?.RequestJump(); seg.WaitToggle = true; seg.Toggled = e => e.Data.TryGet<CharacterPhysicsState>(out var s) && s.State != CharacterState.OnGround; break;
			case Act.Sprint: requests?.RequestSprint(_sprinting = true); break;
			case Act.Unsprint: requests?.RequestSprint(_sprinting = false); break;
			case Act.Crouch: requests?.RequestCrouchToggle(); seg.WaitToggle = true; seg.Toggled = e => e.Data.Has<CharacterCrouchTag>() != crouched; break;
			case Act.Jet: requests?.RequestStateToggle(); seg.WaitToggle = true; seg.Toggled = e => State(e) != state; break;
			case Act.Damp: requests?.RequestDampenerToggle(); seg.WaitToggle = true; seg.Toggled = e => e.Data.Has<DampeningData>() != damped; break;
			case Act.Boost: requests?.RequestBoost(_boosting = true); seg.WaitToggle = true; seg.Toggled = e => e.Data.TryGet<CharacterMovementComponent.JetpackModeData>(out var j) && j.IsBoosting; break;
			case Act.Unboost: requests?.RequestBoost(_boosting = false); break;
			}
			if (lap == 0 || lap >= Laps) GameLog.Instance.Info($"selftest: planet: params at '{name}': {OwnMotionProbe.Params(own)}; {Fuel(own)}");
		}
		own.Data.Set(new ControlData { Movement = move, Rotation = new Vector3(0, yaw, 0) });
		var s2 = _seg!;
		double step = Vector3D.Distance(p, _last);
		double off = ((Vector3)(p - _last) - _lastVelocity * (1 / 60f)).Length();
		s2.Frames++;
		s2.Travel += step;
		s2.MaxOff = Math.Max(s2.MaxOff, off);
		if (off > Math.Max(VisibleMeters, VisibleShare * _lastVelocity.Length() / 60)) s2.Visible++;
		var up = Up(own);
		if (up != Vector3.Zero) s2.Rise = Vector3.Dot((Vector3)(p - s2.From), up);
		if (s2.WaitMove && step > 0.001) { s2.WaitMove = false; s2.MoveFrames = s2.Frames; }
		if ((body.LinearVelocity - _lastVelocity).Length() > JumpSpeed && _jumpLogs++ < 40)
			GameLog.Instance.Info($"selftest: planet: velocity jump in '{s2.Name}' frame {s2.Frames}: {_lastVelocity.Length():0.0} -> {body.LinearVelocity.Length():0.0} m/s at {p.X:0.00}, {p.Y:0.00}, {p.Z:0.00}, {Contacts(own)}");
		if (s2.WaitToggle && s2.Toggled != null && s2.Toggled(own)) { s2.WaitToggle = false; s2.ToggleFrames = s2.Frames; }
	}

	private static void Close()
	{
		if (_seg is not { } s) return;
		var c = OwnMotion.ClientCorrection;
		s.Acks = c.Acks - s.Acks; s.ErrorSum = c.ErrorSum - s.ErrorSum; s.TurnSum = c.TurnErrorSum - s.TurnSum;
		s.Snaps = (int)c.Snaps - s.Snaps; s.Rebases = (int)c.Rebases - s.Rebases;
		s.ErrorMax = c.ErrorMax; s.StepMax = c.StepMax; s.TurnMax = c.TurnErrorMax;
		double horizontal = Math.Sqrt(Math.Max(0, s.Travel * s.Travel - s.Rise * s.Rise));
		GameLog.Instance.Info($"selftest: planet seg '{s.Name}': {s.Frames} frames, {s.Travel:0.0} m (rise {s.Rise:0.0} m, grade {(horizontal > 1 ? 100 * s.Rise / horizontal : 0):0}%), {s.Acks} acks, error mean {(s.Acks == 0 ? 0 : s.ErrorSum / s.Acks) * 100:0.0} cm max {s.ErrorMax * 100:0.0} cm, eased step max {s.StepMax * 100:0.0} cm, facing off mean {(s.Acks == 0 ? 0 : s.TurnSum / s.Acks):0.00} max {s.TurnMax:0.00} deg, {s.Snaps} snaps, {s.Rebases} rebases, {s.Visible} visible jumps (largest step off the velocity {s.MaxOff * 100:0.0} cm), moved after {s.MoveFrames} frames, toggle shown after {s.ToggleFrames} frames");
		Done.Add(s);
		_seg = null;
	}

	private static long _serverLogMs, _serverTicks;
	private static DateTime _serverGameTime;
	private static int _serverJumpLogs;
	private static readonly Dictionary<ushort, Vector3> ServerVelocity = new();
	private static readonly Dictionary<ushort, (bool Charging, int Starts, long DrainAt)> ServerBoosts = new();
	private static readonly Dictionary<ushort, Entity> ServerLanders = new();
	private static int _seatSearchLogs;
	private static readonly Dictionary<ushort, (long PlaceAt, long MoveFrom, long MoveUntil, Vector3 Direction)> ServerGrid = new();

	// The fuel lap ends 10.5 s after the fuel is emptied; the grid lap starts then.
	private static void GridTick(Session session, ushort clientId, Entity character, long now, bool log)
	{
		if (SeatOf(character) is { } seat) { ServerLanders[clientId] = Root(seat.Entity); return; }
		// The server's list has no character for a seated player: the lander is the seat nearest where it first stands.
		if (!ServerLanders.ContainsKey(clientId))
		{
			var at = character.Data.GetWorldTransform().Position;
			var nearest = session.GetEntitiesOfType<SeatComponent>().OrderBy(e => Vector3D.Distance(e.Data.GetWorldTransform().Position, at)).FirstOrDefault();
			if (nearest != null && Vector3D.Distance(nearest.Data.GetWorldTransform().Position, at) < 30)
			{
				ServerLanders[clientId] = Root(nearest);
				GameLog.Instance.Info($"selftest: planet server: ClientId {clientId} came out of the seat of {Root(nearest)}");
			}
			else if (_seatSearchLogs++ < 3)
				GameLog.Instance.Info($"selftest: planet server: ClientId {clientId} stands {(nearest == null ? "with no seat in the world" : $"{Vector3D.Distance(nearest.Data.GetWorldTransform().Position, at):0} m from the nearest seat ({Root(nearest)})")}");
		}
		if (!ServerGrid.TryGetValue(clientId, out var g) || !ServerLanders.TryGetValue(clientId, out var lander) || EngineCodec.IsDead(lander)) return;
		var up = Up(character);
		if (g.PlaceAt != 0 && now >= g.PlaceAt && up != Vector3.Zero)
		{
			var box = BoundingBoxData.GetWorldOriented(lander);
			var axes = new[] { Vector3.Transform(Vector3.Right, box.Orientation), Vector3.Transform(Vector3.Up, box.Orientation), Vector3.Transform(Vector3.Backward, box.Orientation) };
			double top = Math.Abs(Vector3.Dot(axes[0], up)) * box.HalfExtent.X + Math.Abs(Vector3.Dot(axes[1], up)) * box.HalfExtent.Y + Math.Abs(Vector3.Dot(axes[2], up)) * box.HalfExtent.Z;
			var at = box.Center + (Vector3D)(up * (float)(top + 1.0));
			var forward = character.Data.GetWorldTransform().Orientation.GetForward();
			forward -= up * Vector3.Dot(forward, up);
			if (forward.LengthSquared() < 1e-4f) forward = Vector3.Cross(up, axes[0]);
			forward = Vector3.Normalize(forward);
			character.Data.Teleport(new WorldTransform(at, Quaternion.CreateFromForwardUp(forward, up)));
			character.Data.Set(new RigidBodyData(Vector3.Zero, Vector3.Zero));
			g.PlaceAt = 0;
			g.Direction = forward;
			GameLog.Instance.Info($"selftest: planet server: put ClientId {clientId} on top of its lander {lander} ({top:0.0} m above its centre); dragging it at {GridSpeed} m/s in 1.5 s");
		}
		// The lander is flown, not pushed (its dampeners took back any velocity set from outside): forward thrust while
		// slower than GridSpeed, none (its dampeners brake) while faster.
		if (g.MoveFrom != 0 && now >= g.MoveFrom && lander.Data.TryGet<RigidBodyData>(out var rb))
		{
			var forward = lander.Data.GetWorldTransform().Orientation.GetForward();
			bool flying = now < g.MoveUntil;
			float speed = Vector3.Dot(rb.LinearVelocity, forward);
			lander.Data.Set(new ControlData { Movement = flying && speed < GridSpeed ? new Vector3(0, 0, -1) : Vector3.Zero });
			if (!flying) g.MoveFrom = 0;
			if (log) GameLog.Instance.Info($"selftest: planet server: lander {lander} at {rb.LinearVelocity.Length():0.0} m/s{(flying ? "" : ", stopped")}");
		}
		ServerGrid[clientId] = g;
	}

	private static string Fuel(Entity character)
	{
		if (character.TryGet<JetpackFuelComponent>() is not { } fuel) return "no fuel component";
		var precision = fuel.PrecisionFlightFuel;
		var boost = fuel.BoostFlightFuel;
		return $"fuel {precision.Value:0.###}/{precision.MaxValue:0.###}{(fuel.IsPrecisionFlightFuelDepleted ? " (out)" : "")}, boost fuel {boost.Value:0.###}/{boost.MaxValue:0.###}{(fuel.IsBoostFlightFuelDepleted ? " (out)" : "")}";
	}

	// The third boost of the script is the fuel lap's: 3 s after its charge starts (it is boosting by then), the jetpack's
	// fuel is emptied, as when it runs out mid-boost (this world's jetpack burns none).
	private static void DrainOnThirdBoost(ushort clientId, Entity character, long now)
	{
		bool charging = character.TryGet<ServerJetpackBoostComponent>()?.BoostState == CharacterBoostState.Charging;
		ServerBoosts.TryGetValue(clientId, out var seen);
		if (charging && !seen.Charging && ++seen.Starts == 3) seen.DrainAt = now + 3000;
		if (seen.DrainAt != 0 && now >= seen.DrainAt && character.TryGet<JetpackFuelComponent>() is { } fuel
			&& character.TryGet<Keen.Game2.Simulation.WorldObjects.Characters.Stats.StatCollectionComponent>() is { } stats)
		{
			seen.DrainAt = 0;
			ServerGrid[clientId] = (now + 10500, now + 12000, now + 26000, Vector3.Zero);
			var definition = HarmonyLib.AccessTools.FieldRefAccess<JetpackFuelComponent, JetpackFuelComponentDefinition>("_definition")(fuel);
			// Left nearly empty, not emptied: about 1.2 s of boost, then 1.3 s of flight burn on both sides, so the player's
			// game has the replicated stat and can see the boost and then the jetpack run dry when the server's does.
			// A world whose jetpack burns nothing (SurvivalNOFTUE) would never run dry from there: emptied at once instead.
			bool burns = stats.GetStat(definition.PrecisionFlightFuel.Stat) is var flight && flight.Value < flight.MaxValue - 0.01f;
			stats.TrySetValue(definition.BoostFlightFuel.Stat, stats.GetStat(definition.BoostFlightFuel.Stat).MinValue + (burns ? 4f : 0f), ignoreCreative: true);
			stats.TrySetValue(definition.PrecisionFlightFuel.Stat, stats.GetStat(definition.PrecisionFlightFuel.Stat).MinValue + (burns ? 0.6f : 0f), ignoreCreative: true);
			GameLog.Instance.Info($"selftest: planet server: ClientId {clientId} fuel nearly emptied in its third boost: {Fuel(character)}; state {character.TryGet<CharacterMovementComponent>()?.CurrentMovementState}");
		}
		seen.Charging = charging;
		ServerBoosts[clientId] = seen;
	}

	/// <summary>Server thread (rig): puts unseated players high above a planet on the ground under them.</summary>
	public static void ServerTick(Session session, IEnumerable<(ushort ClientId, Entity? Character)> players)
	{
		long now = MpClock.NowMs();
		_serverTicks++;
		bool log = now - _serverLogMs >= 1000;
		if (log)
		{
			var game = Keen.VRage.Core.Game.Components.GameEntityExtensions.Get<Keen.VRage.Core.Game.GameSystems.GameTimes.IGameTime>(session)?.CurrentGameTime ?? default;
			if (_serverLogMs != 0) GameLog.Instance.Info($"selftest: planet server: {_serverTicks} ticks and {(game - _serverGameTime).TotalSeconds:0.000} s of game time in {(now - _serverLogMs) / 1000.0:0.000} s; flora sectors {session.GetEntitiesOfType<Keen.VRage.Voxels.Components.Flora.PlanetFloraSectorPhysicsComponent>().Count()} with physics ({FloraPhysicsRecovery.Built(session)} with colliders built), {session.GetEntitiesOfType<Keen.VRage.Voxels.Components.Flora.FloraSectorComponent>().Count()} logical");
			_serverGameTime = game;
			_serverTicks = 0;
			_serverLogMs = now;
		}
		var list = players.ToList();
		Entity? scripted = list.Where(x => x.Character != null && !EngineCodec.IsDead(x.Character)).OrderBy(x => x.ClientId).Select(x => x.Character).FirstOrDefault();
		foreach (var (clientId, character) in list)
		{
			if (log && character != null && !EngineCodec.IsDead(character)) GameLog.Instance.Info($"selftest: planet server params ClientId {clientId}: {OwnMotionProbe.Params(character)}; {Fuel(character)}");
			if (character != null && !EngineCodec.IsDead(character)) DrainOnThirdBoost(clientId, character, now);
			if (character != null && !EngineCodec.IsDead(character)) GridTick(session, clientId, character, now, log);
			if (character != null && !EngineCodec.IsDead(character) && character.Data.TryGet<RigidBodyData>(out var rb))
			{
				if (ServerVelocity.TryGetValue(clientId, out var before) && (rb.LinearVelocity - before).Length() > JumpSpeed && _serverJumpLogs++ < 40)
				{
					var where = character.Data.GetWorldTransform().Position;
					GameLog.Instance.Info($"selftest: planet server: ClientId {clientId} velocity jump {before.Length():0.0} -> {rb.LinearVelocity.Length():0.0} m/s at {where.X:0.00}, {where.Y:0.00}, {where.Z:0.00}, {Contacts(character)}");
				}
				ServerVelocity[clientId] = rb.LinearVelocity;
			}
			if (character == null || EngineCodec.IsDead(character) || SeatOf(character) != null || Up(character) == Vector3.Zero) continue;
			var at = character.Data.GetWorldTransform();
			if (scripted is { } lead && !ReferenceEquals(lead, character))
			{
				var leadAt = lead.Data.GetWorldTransform();
				if (Vector3D.Distance(leadAt.Position, at.Position) > 25 && SeatOf(lead) == null)
				{
					var side = Vector3.Cross(leadAt.Orientation.GetForward(), Up(lead));
					if (side.LengthSquared() < 1e-4f) side = leadAt.Orientation.GetRight();
					if (Place(session, character, leadAt.Position + (Vector3D)(Vector3.Normalize(side) * 6), 0, out _))
						GameLog.Instance.Info($"selftest: planet server: put ClientId {clientId} 6 m beside the scripted player to watch it");
				}
				continue;
			}
			if (Place(session, character, at.Position, PlaceAboveMeters, out double height))
				GameLog.Instance.Info($"selftest: planet server: put ClientId {clientId} on the ground ({height:0} m below it)");
			else if (FarMeters > 0 && ReferenceEquals(character, scripted)) FarMove(session, clientId, character, at, now);
		}
	}

	// -y51online:selftest-planet-far:<m>: once the scripted player has been predicted for 2 s, the server puts it on the
	// ground that far away, where its game has loaded nothing (as the lander drop of a co-op world does).
	private static readonly double FarMeters = MpPlugin.LaunchArgs.Select(a => a.StartsWith(MpOptions.Prefix + "selftest-planet-far:", StringComparison.Ordinal)
		&& double.TryParse(a[(MpOptions.Prefix.Length + 20)..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double m) ? m : 0).FirstOrDefault(m => m > 0);
	private static readonly Dictionary<ushort, long> FarSince = new();
	private static readonly HashSet<ushort> FarDone = new();

	private static void FarMove(Session session, ushort clientId, Entity character, WorldTransform at, long now)
	{
		if (FarDone.Contains(clientId)) return;
		if (!OwnMotion.StatsOf(clientId).Driven) { FarSince.Remove(clientId); return; }
		if (!FarSince.TryGetValue(clientId, out long since)) { FarSince[clientId] = now; return; }
		if (now - since < 2000) return;
		FarDone.Add(clientId);
		var side = at.Orientation.GetRight();
		if (Place(session, character, at.Position + (Vector3D)(side * (float)FarMeters), double.MinValue, out double height))
			GameLog.Instance.Info($"selftest: planet server: put ClientId {clientId} on the ground {FarMeters:0} m away (it was {height:0} m above that ground)");
	}

	// Onto the planet surface under a point, when the character is more than minHeight above it.
	private static bool Place(Session session, Entity character, Vector3D point, double minHeight, out double height)
	{
		height = 0;
		var at = character.Data.GetWorldTransform();
		{
			Entity? planet = null;
			double best = double.MaxValue;
			foreach (Entity p in session.GetEntitiesOfType<Keen.VRage.Voxels.Components.VoxelPlanetComponent>())
			{
				double d = Vector3D.Distance(p.Data.GetWorldTransform().Position, at.Position);
				if (d < best) { best = d; planet = p; }
			}
			if (planet == null) return false;
			var voxels = planet.Get<Keen.VRage.Voxels.Components.VoxelPlanetComponent>();
			var frame = planet.Data.GetWorldTransform();
			var local = WorldTransform.TransformInv(point, frame);
			var dir = (Vector3)Vector3D.Normalize(local);
			if (!voxels.QuerySurfacePosition(dir * voxels.Radius, out Keen.VRage.Voxels.Storage.Planet.SampledPlanetPosition ground)) return false;
			height = local.Length() - ground.SurfaceDistance;
			if (height < minHeight) return false;
			var position = WorldTransform.Transform((Vector3D)(dir * (float)(ground.SurfaceDistance + 1.5)), frame);
			var up = (Vector3)Vector3D.Normalize(position - frame.Position);
			var forward = at.Orientation.GetForward();
			forward -= up * Vector3.Dot(forward, up);
			if (forward.LengthSquared() < 1e-4f) forward = Vector3.Cross(up, Math.Abs(up.Y) < 0.9f ? Vector3.Up : Vector3.Right);
			character.Data.Teleport(new WorldTransform(position, Quaternion.CreateFromForwardUp(Vector3.Normalize(forward), up)));
			character.Data.Set(new RigidBodyData(Vector3.Zero, Vector3.Zero));
			return true;
		}
	}

	private static void Report()
	{
		var rtt = RoundTrips.OrderBy(x => x).ToList();
		GameLog.Instance.Info($"selftest: planet: done; input round trip {(rtt.Count == 0 ? "n/a" : $"mean {rtt.Average():0} / p95 {rtt[(int)(0.95 * (rtt.Count - 1))]:0} / max {rtt[^1]:0} ms")}; {OwnMotion.DescribeCorrection()}");
		foreach (var g in Done.GroupBy(s => s.Name))
		{
			long acks = g.Sum(s => s.Acks);
			GameLog.Instance.Info($"selftest: planet sum '{g.Key}': error mean {(acks == 0 ? 0 : g.Sum(s => s.ErrorSum) / acks) * 100:0.0} cm max {g.Max(s => s.ErrorMax) * 100:0.0} cm, eased step max {g.Max(s => s.StepMax) * 100:0.0} cm, facing max {g.Max(s => s.TurnMax):0.00} deg, snaps {g.Sum(s => s.Snaps)}, visible {g.Sum(s => s.Visible)}, moved after {string.Join("/", g.Select(s => s.MoveFrames))} frames, toggle after {string.Join("/", g.Select(s => s.ToggleFrames))} frames");
		}
	}
}
#endif
