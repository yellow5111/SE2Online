#if !PULSAR
using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Keen.Game2.Client.GameSystems.PlayerControl;
using Keen.Game2.Client.WorldObjects.Character;
using Keen.Game2.Simulation.WorldObjects.Characters;
using Keen.Game2.Simulation.WorldObjects.Movement;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.Components;
using Keen.VRage.Game.Components.Animations;
using Keen.VRage.Game.Multiplayer;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Library.Utils;
using Keen.VRage.Multiplayer.Replications;
using Y51_SE2Online.Core;
using Y51_SE2Online.SyncEngine;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>
/// Test rig only (-y51online:selftest): how far a remote character's animation and body are apart on this game, and how
/// far its played-back body is from where the server had it. Per remote character (players and NPCs, never this game's
/// own) every 30 s:
/// lead = server time of the blackboard frame the animation shows minus the body's playout cursor (positive: the animation
/// is ahead of the body); behind = newest received motion sample minus the cursor; error = the body's position at the
/// cursor against the server's own samples around that time.
/// </summary>
internal static class AnimSyncProbe
{
	// -y51online:selftest-animwindow:<ms> reports in shorter windows (the planet rig lines them up with its actions).
	private static readonly long ReportMs = MpPlugin.LaunchArgs.FirstOrDefault(a => a.StartsWith(MpOptions.Prefix + "selftest-animwindow:")) is { } w
		&& long.TryParse(w[(MpOptions.Prefix.Length + "selftest-animwindow:".Length)..], out long ms) ? ms : 30_000;

	private sealed class Track
	{
		public readonly List<(long T, Vector3D P, int F)> Samples = new();
		public readonly List<(long C, Vector3D P)> Shown = new();
		public string Category = "";
		public Entity? Entity;
	}

	private sealed class Stats
	{
		public readonly List<double> Lead = new(), Behind = new(), Error = new();
		public int Events, OwnStamp, HeldEvents, Held, Late;
	}

	private static readonly object Sync = new();
	private static readonly List<(Entity Entity, string Keys, long? Stamp)> Received = new();
	private static readonly Dictionary<Entity, Track> Tracks = new();
	private static readonly Dictionary<string, Stats> ByCategory = new();
	private static readonly List<double> FrameSteps = new();
	private static long _sampleMax, _nextReportMs;
	private static int _eventLines;
	private static readonly List<double> ClockRatio = new(), MotionRatio = new();
	private static DateTime _lastGame;
	private static int _remoteFrames, _remoteEvents;

	public static void Install(Harmony harmony)
	{
		if (!SelfTest.Requested) return;
		harmony.Patch(AccessTools.Method(typeof(AnimationBlackboardComponent), "BlackboardReceive"), postfix: new HarmonyMethod(typeof(AnimSyncProbe), nameof(ReceivePostfix)));
		if (AccessTools.TypeByName("Keen.VRage.Voxels.Client.Components.PlanetFloraClientComponent") is { } flora)
		{
			harmony.Patch(AccessTools.Method(flora, "OnCreateSectorPhysics"), prefix: new HarmonyMethod(typeof(AnimSyncProbe), nameof(SectorPhysicsPrefix)));
			harmony.Patch(AccessTools.Method(typeof(Keen.VRage.Voxels.Components.Flora.FloraSectorComponent), nameof(Keen.VRage.Voxels.Components.Flora.FloraSectorComponent.SetBuilder)),
				prefix: new HarmonyMethod(typeof(AnimSyncProbe), nameof(SetBuilderPrefix)));
		}
	}

	private static int _sectorSignals, _sectorSignalsNoEntity, _sectorBuilds;

	private static void SectorPhysicsPrefix(object[] __args)
	{
		_sectorSignals++;
		if (__args.Length > 1 && __args[1]?.ToString() is var text && (text == null || text.Contains("null", StringComparison.OrdinalIgnoreCase))) _sectorSignalsNoEntity++;
	}

	private static void SetBuilderPrefix() => _sectorBuilds++;

	private static void ReceivePostfix(AnimationBlackboardComponent __instance, bool __runOriginal, ReadOnlySpan<(StringHash, float)> floats, ReadOnlySpan<(StringHash, int)> ints, ReadOnlySpan<(StringHash, StringHash)> strings, ReadOnlySpan<(StringHash, bool)> bools)
	{
		if (!__runOriginal || floats.Length + ints.Length + strings.Length + bools.Length == 0) return;
		string keys = "";
		foreach (var (k, v) in bools) keys += $" {k.String}={v}";
		foreach (var (k, v) in ints) keys += $" {k.String}={v}";
		foreach (var (k, v) in strings) keys += $" {k.String}={v.String}";
		lock (Sync) Received.Add((__instance.Entity, keys, AnimPlayout.Replaying));
	}

	internal static void NoteSample(long serverTicks)
	{
		if (serverTicks > _sampleMax) _sampleMax = serverTicks;
	}

	internal static long PlayoutCursor(MotionInterpolationSessionComponent interpolation, in MotionHistory history)
	{
		var delay = history.Cadence > interpolation.FrameStep ? history.Cadence - interpolation.FrameStep : TimeSpan.Zero;
		return (interpolation.Clock - delay).Ticks;
	}

	/// <summary>Client session thread, after each update.</summary>
	public static void ClientTick(Session session)
	{
		if (!SelfTest.Requested) return;
		var own = new HashSet<Entity>();
		if (session.SessionComponents.TryGet<ClientPlayersSessionComponent>()?.LocalPlayerController is { } controller)
			foreach (var c in controller.ControlledEntities)
				if ((c.Controllable as Component)?.Entity is { } e) own.Add(e);
		if (OwnMotion.Driving is { } driven) own.Add(driven);
		Drive(own.FirstOrDefault(e => e.TryGet<CharacterLifeComponent>() != null));
		if (OwnMotion.Driving is { } predicted) WatchOwn(predicted);
		var interpolation = session.SessionComponents.TryGet<MotionInterpolationSessionComponent>();
		if (interpolation != null) FrameSteps.Add(interpolation.FrameStep.TotalMilliseconds);
		// Animation advances on game time, motion playback on raw frame time: a game clock run faster or slower than the
		// frame (ClientClock easing) plays animations at another speed than the body moves.
		if (interpolation != null && Keen.VRage.Core.Game.Components.GameEntityExtensions.Get<Keen.VRage.Core.Game.GameSystems.GameTimes.IGameTime>(session) is { } time)
		{
			var game = time.CurrentGameTime;
			var played = interpolation.Clock - interpolation.PreviousClock;
			if (_lastGame != default && time.DeltaTime > TimeSpan.Zero)
			{
				ClockRatio.Add((game - _lastGame) / time.DeltaTime);
				if (played > TimeSpan.Zero) MotionRatio.Add(played / time.DeltaTime);
			}
			_lastGame = game;
		}

		List<(Entity Entity, string Keys, long? Stamp)> received;
		lock (Sync) { received = Received.ToList(); Received.Clear(); }
		foreach (var (entity, keys, held) in received)
		{
			if (!own.Contains(entity)) _remoteEvents++;
			if (interpolation == null || own.Contains(entity) || EngineCodec.IsDead(entity) || !entity.Data.TryGet<MotionHistory>(out var h) || h.Newest.ServerTime == TimeSpan.Zero) continue;
			var stats = Get(Category(entity));
			stats.Events++;
			// The frame's motion samples left the server with its blackboard: this character's own newest sample if it
			// came this frame, else the newest sample of any entity this frame.
			// A change held by AnimPlayout carries its own stamp.
			long stamp = held ?? (h.Newest.ServerTime.Ticks >= _sampleMax ? h.Newest.ServerTime.Ticks : _sampleMax);
			if (held != null) stats.HeldEvents++;
			else if (stamp == h.Newest.ServerTime.Ticks) stats.OwnStamp++;
			double lead = (stamp - PlayoutCursor(interpolation, h)) / (double)TimeSpan.TicksPerMillisecond;
			stats.Lead.Add(lead);
			if (keys.Length > 0 && Math.Abs(lead) > 30 && _eventLines++ < 20) GameLog.Instance.Info($"selftest: anim event{keys} on {Category(entity)} {entity}: animation {lead:0} ms ahead of the body");
		}

		foreach (var character in session.GetEntitiesOfType<CharacterLifeComponent>())
		{
			if (own.Contains(character)) continue;
			_remoteFrames++;
			if (interpolation == null || !character.Data.TryGet<MotionHistory>(out var h) || h.Newest.ServerTime == TimeSpan.Zero) continue;
			if (!Tracks.TryGetValue(character, out var t)) Tracks[character] = t = new Track { Entity = character };
			t.Category = Category(character);
			if ((h.Newest.Flags & MotionStateFlags.Teleported) != 0 && t.Samples.Count > 0 && t.Samples[^1].T != h.Newest.ServerTime.Ticks) { t.Samples.Clear(); t.Shown.Clear(); }
			if (h.Previous.ServerTime != TimeSpan.Zero) Add(t.Samples, h.Previous);
			Add(t.Samples, h.Newest);
			long cursor = PlayoutCursor(interpolation, h);
			var stats = Get(t.Category);
			stats.Behind.Add((h.Newest.ServerTime.Ticks - cursor) / (double)TimeSpan.TicksPerMillisecond);
			if (h.BlendDuration != TimeSpan.Zero) stats.Held++;
			t.Shown.Add((cursor, character.Data.GetWorldTransform().Position));
			Resolve(t, stats);
		}
		foreach (var gone in Tracks.Keys.Where(e => EngineCodec.IsDead(e)).ToList()) Tracks.Remove(gone);

		long now = MpClock.NowMs();
		if (_nextReportMs == 0) _nextReportMs = now + ReportMs;
		if (now < _nextReportMs) return;
		_nextReportMs = now + ReportMs;
		var me = own.FirstOrDefault()?.Data.GetWorldTransform().Position;
		var others = session.GetEntitiesOfType<AnimationBlackboardComponent>().Where(e => !own.Contains(e)).Select(e => e.Data.GetWorldTransform().Position).ToList();
		string nearest = me is { } at && others.Count > 0 ? $"{others.Min(o => Vector3D.Distance(o, at)):0} m" : "-";
		GameLog.Instance.Info($"selftest: anim scene: {others.Count} other animated entities (nearest {nearest}), {_remoteFrames} remote character frames, {_remoteEvents} remote blackboard changes, motion playback {(interpolation == null ? "absent (in-process scene)" : MotionInterpolationSettings.Instance.Interpolate ? "on" : "off")}");
		_remoteFrames = _remoteEvents = 0;
		foreach (var (category, s) in ByCategory.ToList())
		{
			GameLog.Instance.Info($"selftest: anim {category}: {s.Events} blackboard changes ({s.OwnStamp} stamped by the character's own sample, {s.HeldEvents} held to the body), animation ahead of body {Describe(s.Lead)} ms; body behind newest sample {Describe(s.Behind)} ms; body vs server {Describe(s.Error, 100)} cm over {s.Error.Count} frames ({s.Late} unresolved, {s.Held} blending), client frame {Describe(FrameSteps)} ms");
			ByCategory[category] = new Stats();
		}
		if (ClockRatio.Count > 0) GameLog.Instance.Info($"selftest: anim clock: per frame, animation (game) time {Describe(ClockRatio)} x frame time, {ClockRatio.Count(r => Math.Abs(r - 1) > 0.05)} of {ClockRatio.Count} frames off by more than 5%; body playback time {Describe(MotionRatio)} x, {MotionRatio.Count(r => Math.Abs(r - 1) > 0.05)} off by more than 5%; client clock last off the server's by {ClientClock.LastErrorMs:0} ms");
		ClockRatio.Clear();
		MotionRatio.Clear();
		GameLog.Instance.Info($"selftest: anim {MotionSmoothing.Stats}; {FloraPhysicsRecovery.Stats}; flora physics signals {_sectorSignals} ({_sectorSignalsNoEntity} without entity), builds {_sectorBuilds}; flora sectors here: {session.GetEntitiesOfType<Keen.VRage.Voxels.Components.Flora.PlanetFloraSectorPhysicsComponent>().Count()} with physics ({FloraPhysicsRecovery.Built(session)} with colliders built), {session.GetEntitiesOfType<Keen.VRage.Voxels.Components.Flora.FloraSectorComponent>().Count()} logical");
		foreach (var (key, w) in OwnWatch.ToList())
		{
			if (w.Frames == 0) continue;
			GameLog.Instance.Info($"selftest: anim own {key}: {w.Changes} changes of the predicted body, animation caught up after {Describe(w.Latency)} ms; animation off the body in {w.Off} of {w.Frames} frames");
			OwnWatch[key] = new Watch { Expected = w.Expected, Since = w.Since };
		}
		FrameSteps.Clear();
		_eventLines = 0;
	}

	// -y51online:selftest-anim: this game's own character loops moves, stops, turns, jumps, sprints and crouches so the
	// other games have a remote character to watch; as far back as forth at the same heading, so it stays near them.
	private static readonly (double At, Vector3 Move, float Yaw, int Act)[] Loop =
	{
		(0, new Vector3(0, 0, -1), 0, 0),
		(2, Vector3.Zero, 0, 1),
		(3, Vector3.Zero, 0.03f, 0),
		(4, Vector3.Zero, -0.03f, 0),
		(5, new Vector3(0, 0, 1), 0, 2),
		(7, Vector3.Zero, 0, 3),
		(7.5, new Vector3(1, 0, 0), 0, 0),
		(8.5, new Vector3(-1, 0, 0), 0, 4),
		(9.5, Vector3.Zero, 0, 4),
	};

	private static Entity? _driven;
	private static long _drivenSinceMs;
	private static int _loopSegment = -1;

	private static void Drive(Entity? own)
	{
		if (own == null || !MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "selftest-anim")) return;
		long now = MpClock.NowMs();
		if (!ReferenceEquals(own, _driven)) { _driven = own; _drivenSinceMs = now + 15_000; _loopSegment = -1; GameLog.Instance.Info("selftest: anim: looping a movement script on this game's character in 15 s"); }
		if (now < _drivenSinceMs) return;
		double t = (now - _drivenSinceMs) / 1000.0;
		int segment = Array.FindLastIndex(Loop, l => l.At <= t % 10) + (int)(t / 10) * Loop.Length;
		var (_, move, yaw, act) = Loop[segment % Loop.Length];
		if (segment != _loopSegment)
		{
			_loopSegment = segment;
			var requests = own.TryGet<CharacterMovementControlComponent>();
			bool ground = own.TryGet<CharacterMovementComponent>()?.CurrentMovementState == CharacterMovementState.Ground;
			switch (act)
			{
			case 1: requests?.RequestJump(); break;
			case 2: if (ground) requests?.RequestSprint(true); break;
			case 3: if (ground) requests?.RequestSprint(false); break;
			case 4: if (ground) requests?.RequestCrouchToggle(); break;
			}
		}
		own.Data.Set(new ControlData { Movement = move, Rotation = new Vector3(0, yaw, 0) });
	}

	// -y51online:selftest-npc (server): 30 s after a player's character appears, a neutral SE2 4.0 NPC is made 8 m in
	// front of it and strafes left and right, 3 s each way, so every game has an AI character to watch.
	private static Entity? _npc;
	private static long _npcAtMs, _npcMoveMs;
	private static bool _npcTried, _npcLeft, _intervalSet;

	public static void ServerTick(Session session)
	{
		// -y51online:selftest-sendinterval:<frames>: motion samples every few server frames, as Keen's streaming sends far
		// entities (and as a client drawing faster than the server ticks sees them): a playout delay to measure.
		if (!_intervalSet && MpPlugin.LaunchArgs.FirstOrDefault(a => a.StartsWith(MpOptions.Prefix + "selftest-sendinterval:")) is { } arg
			&& int.TryParse(arg[(MpOptions.Prefix.Length + "selftest-sendinterval:".Length)..], out int frames))
		{
			_intervalSet = true;
			Keen.VRage.Multiplayer.Sessions.ConstantDataSendFrequencyProvider.Instance.IntervalFrames = frames;
			GameLog.Instance.Info($"selftest: anim: motion samples go out every {frames} server frames");
		}
		if (!MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "selftest-npc")) return;
		long now = MpClock.NowMs();
		if (_npc == null)
		{
			if (_npcTried) return;
			var player = session.GetEntitiesOfType<CharacterLifeComponent>().FirstOrDefault(c => c.Get<CharacterLifeComponent>().IsAlive && TeamPermissions.OwnerOf(c) is long o && Y51_SE2Online.Sync.PlayerIdentities.IsHuman(o));
			if (player == null) { _npcAtMs = 0; return; }
			if (_npcAtMs == 0) _npcAtMs = now + 30_000;
			if (now < _npcAtMs) return;
			_npcTried = true;
			try
			{
				var config = Keen.VRage.Library.Utils.Singleton<Keen.VRage.Library.Definitions.DefinitionManager>.Instance.GetConfiguration<Keen.Game2.Simulation.GameSystems.AI.Definitions.AIControllerConfiguration>();
				var ob = config.NPCCharacterPrefab.Get();
				bool ground = player.TryGet<CharacterMovementComponent>()?.CurrentMovementState == CharacterMovementState.Ground;
				if (Keen.VRage.DCS.ObjectBuilders.EntityObjectBuilderFunctions.TryGetOB<CharacterMovementComponent, CharacterMovementComponentObjectBuilder>(ob) is { } movement)
					movement.MovementState = ground ? CharacterMovementState.Ground : CharacterMovementState.Jetpack;
				if (Keen.VRage.DCS.ObjectBuilders.EntityObjectBuilderFunctions.TryGetOB<Keen.Game2.Simulation.GameSystems.AI.AIMovementComponent, Keen.Game2.Simulation.GameSystems.AI.AIMovementObjectBuilder>(ob) is { } ai
					&& AccessTools.Field(ai.GetType(), "MovementType") is { } type)
					type.SetValue(ai, Enum.ToObject(type.FieldType, ground ? 2 : 1));
				var at = player.Data.GetWorldTransform();
				var forward = Keen.VRage.Library.Mathematics.Quaternion.GetForward(at.Orientation);
				var home = at.Position + (Vector3D)forward * 8;
				var npc = session.EntitySerializer.BuildFrom(default, ob);
				var data = npc.Data;
				ref var wt = ref data.GetWritePtr<Keen.VRage.Core.WorldTransform>();
				wt.Position = home;
				wt.Orientation = at.Orientation;
				session.AddEntityToScene(npc);
				session.SessionComponents.TryGet<Keen.Game2.Simulation.GameSystems.Ownership.OwnershipSessionComponent>()?.TryTransferToNPC(npc, hostile: false);
				_npc = npc;
				GameLog.Instance.Info($"selftest: anim: made an NPC ({(ground ? "on foot" : "jetpack")}) 8 m in front of a player at {home.X:0}, {home.Y:0}, {home.Z:0}");
			}
			catch (Exception ex) { GameLog.Instance.Warn("selftest: anim: making an NPC failed: " + ex); }
			return;
		}
		if (EngineCodec.IsDead(_npc)) return;
		// Its own inputs, not the AI's pathing (no navigation in open space): strafe one way, then back.
		if (now >= _npcMoveMs) { _npcMoveMs = now + 3000; _npcLeft = !_npcLeft; }
		_npc.Data.Set(new ControlData { Movement = new Vector3(_npcLeft ? -1 : 1, 0, 0) });
	}

	// The predicted own character: how long after its body changes state its animation follows (by the server's
	// round trip, or at once when OwnAnimation drives it).
	private sealed class Watch
	{
		public string? Expected;
		public long Since;
		public bool Pending;
		public int Changes, Off, Frames;
		public readonly List<double> Latency = new();
	}

	private static readonly Dictionary<string, Watch> OwnWatch = new();
	private static Keen.VRage.Physics.Character.CharacterState _ownLastState = (Keen.VRage.Physics.Character.CharacterState)(-1);
	private static string _ownCharacterState = "";

	private static void WatchOwn(Entity own)
	{
		if (own.TryGet<AnimationBlackboardComponent>()?.Blackboard is not { } bb || !own.Data.TryGet<Keen.VRage.Physics.Character.CharacterPhysicsState>(out var ps)) return;
		var states = Keen.VRage.Library.Utils.ImmutableSingleton<Keen.Game2.Simulation.WorldObjects.Characters.AnimationsBlackboard.BlackboardConstants.CharacterStates>.Instance;
		bool gravity = own.Data.Has<Keen.VRage.Core.Game.GameSystems.Gravity.GravityEffectData>();
		if (ps.State != _ownLastState)
		{
			_ownCharacterState = ps.State switch
			{
				Keen.VRage.Physics.Character.CharacterState.Jumping => states.Jumping,
				Keen.VRage.Physics.Character.CharacterState.Falling => !gravity ? states.Floating : _ownLastState == Keen.VRage.Physics.Character.CharacterState.Jumping ? _ownCharacterState : states.Falling,
				Keen.VRage.Physics.Character.CharacterState.OnGround => states.OnGround,
				Keen.VRage.Physics.Character.CharacterState.Floating => states.Floating,
				_ => _ownCharacterState,
			};
			_ownLastState = ps.State;
		}
		own.Data.TryGet<ControlData>(out var control);
		long now = MpClock.NowMs();
		Check("CharacterState", _ownCharacterState, bb.Get<StringId>("CharacterState").ToString(), now);
		Check("Jetpack", (own.TryGet<CharacterMovementComponent>()?.CurrentMovementState == CharacterMovementState.Jetpack).ToString(), bb.Get<bool>("Jetpack").ToString(), now);
		Check("Crouching", own.Data.Has<CharacterCrouchTag>().ToString(), bb.Get<bool>("Crouching").ToString(), now);
		Check("turning", Math.Sign(Math.Round(control.Rotation.Y * 100)).ToString(), Math.Sign(Math.Round(bb.Get<float>("AngularVelY_driver") * 10)).ToString(), now);
		Check("moving", (ps.GroundVelocity.Length() > 0.5f).ToString(), (bb.Get<float>("GroundVelocityTotal") > 0.05f).ToString(), now);
	}

	private static void Check(string key, string expected, string shown, long now)
	{
		if (!OwnWatch.TryGetValue(key, out var w)) OwnWatch[key] = w = new Watch { Expected = expected };
		w.Frames++;
		if (expected != w.Expected) { w.Expected = expected; w.Since = now; w.Pending = true; w.Changes++; }
		if (shown != expected) w.Off++;
		else if (w.Pending) { w.Pending = false; w.Latency.Add(now - w.Since); }
	}

	private static void Add(List<(long T, Vector3D P, int F)> samples, in EntityMotionState state)
	{
		long t = state.ServerTime.Ticks;
		if (samples.Count > 0 && samples[^1].T >= t) return;
		samples.Add((t, state.Transform.Position, (int)state.Flags | state.ParentEpoch << 16));
		if (samples.Count > 600) samples.RemoveRange(0, 300);
	}

	// A shown position is checked once the server's samples on both sides of its cursor are in.
	private static void Resolve(Track t, Stats stats)
	{
		int done = 0;
		foreach (var (c, p) in t.Shown)
		{
			int i = t.Samples.FindIndex(s => s.T >= c);
			if (i < 0) break;
			if (i == 0 && t.Samples[0].T > c) { stats.Late++; done++; continue; }
			var (t1, p1, _) = i == 0 ? t.Samples[0] : t.Samples[i - 1];
			var (t2, p2, _) = t.Samples[i];
			var truth = t2 == t1 ? p2 : Vector3D.Lerp(in p1, in p2, (double)(c - t1) / (t2 - t1));
			// Samples more than 100 ms apart (a resting character) say nothing about what lay between them.
			if (t2 - t1 <= 100 * TimeSpan.TicksPerMillisecond)
			{
				double error = Vector3D.Distance(p, truth);
				stats.Error.Add(error);
				if (error > 1 && _spikeLines++ < 40)
				{
					int k = t.Shown.IndexOf((c, p));
					string before = k > 0 ? $"{Vector3D.Distance(p, t.Shown[k - 1].P) * 100:0} cm from the frame before (cursor {(c - t.Shown[k - 1].C) / 10000.0:0.0} ms on)" : "first frame";
					string near = string.Join(", ", t.Samples.Skip(Math.Max(0, i - 3)).Take(6).Select(x => $"{(x.T - c) / 10000.0:0.0} ms {Vector3D.Distance(x.P, p) * 100:0} cm flags {x.F & 0xFFFF:X} epoch {x.F >> 16}"));
					GameLog.Instance.Info($"selftest: anim spike {t.Category} {Describe(t)}: {error * 100:0} cm off at cursor {c / 10000.0:0.0} ms, samples {(t1 - c) / 10000.0:0.0}/{(t2 - c) / 10000.0:0.0} ms {Vector3D.Distance(p1, p2) * 100:0} cm apart; body {before}; samples around (time, distance from the body): {near}");
				}
			}
			done++;
		}
		t.Shown.RemoveRange(0, done);
		if (t.Shown.Count > 600) { stats.Late += t.Shown.Count - 300; t.Shown.RemoveRange(0, t.Shown.Count - 300); }
	}

	private static int _spikeLines;

	private static string Describe(Track t) => t.Entity is { } e ? $"{e} parent {e.TryGet<Keen.VRage.Core.Game.Components.HierarchyComponent>()?.Parent?.ToString() ?? "none"}, simulated {e.TryGet<Keen.Game2.Simulation.WorldObjects.Characters.CharacterPhysicsEntityComponent>()?.CharacterPhysicsDefinition.Simulated}, {(e.Data.TryGet<Keen.VRage.Physics.Character.CharacterPhysicsState>(out var ps) ? ps.State.ToString() : "no state")}, {PlanetProbe.Contacts(e)}" : "?";

	private static string Describe(List<double> values, double scale = 1)
	{
		if (values.Count == 0) return "n/a";
		var sorted = values.Select(v => v * scale).OrderBy(v => v).ToList();
		return $"mean {sorted.Average():0.0} / p95 {sorted[(int)(0.95 * (sorted.Count - 1))]:0.0} / max {sorted[^1]:0.0}";
	}

	private static Stats Get(string category)
	{
		if (!ByCategory.TryGetValue(category, out var s)) ByCategory[category] = s = new Stats();
		return s;
	}

	private static string Category(Entity character)
		=> TeamPermissions.OwnerOf(character) is long owner && Y51_SE2Online.Sync.PlayerIdentities.IsHuman(owner) ? "player" : "npc";
}
#endif
