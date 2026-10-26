using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Keen.Game2.Client.GameSystems.PlayerControl;
using Keen.Game2.Simulation.WorldObjects.Characters;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.Accessors;
using Keen.VRage.DCS.Components;
using Keen.VRage.Game.Multiplayer;
using Keen.VRage.Multiplayer.Replications;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>
/// SE2 4.0 ships its client motion playback switched off: <see cref="MotionInterpolationSettings.Interpolate"/> is a
/// debug-menu toggle that defaults to false, so a client snaps every replicated character, grid and ragdoll to each
/// motion sample as it arrives. Samples come in network bursts, not one per client frame, so remote players, ships
/// (one's own too: SE2 4.0 is server-authoritative) and ragdolls (keyframed to that transform) moved in steps and
/// jerks. The game's own playback (interpolation between the two newest samples, capped extrapolation, error fade,
/// parent-relative motion for characters on ships) is turned on for every SE2 Online session. -y51online:interpolate:off
/// keeps it off.
/// Its playout delay is the sample gap minus one frame: none at 60 samples/s, so the cursor runs one frame past the
/// newest sample and every remote character is extrapolated all the time. Extrapolation stops after two sample gaps, so
/// a character whose samples paused for three frames or more (a network burst, a server hitch) stood still and then
/// jumped up to 2 m when they came back. Other characters (not this game's own body, nor grids: piloting stays as
/// quick as it was) are played <see cref="HoldFrames"/> frames further back, and their samples are held in a small
/// buffer and handed to the playback in order, each once the cursor is a frame short of it: the playback keeps only
/// two samples, so a sample that arrived early must wait, and a pause of up to that many frames is played from samples
/// already here instead of being extrapolated. -y51online:motiondelay:off plays them as the game does,
/// -y51online:motionbuffer:off goes back to one frame further back without the buffer.
/// On a joined player's game (0.18.2) everything the server moves goes through SE2 Online's own playback instead
/// (<see cref="Sync.PlayoutClock"/>): every motion sample is held per body, the game's clock is replaced by one that runs
/// at the measured server rate a jitter-sized delay behind the stream, and each tick the pair each body plays and its
/// cursor are written into its MotionHistory, so the game's interpolation (parent frames included) plays exactly that.
/// The game's clock chased the newest packet and its delay flipped with the gap between the last two samples: remote
/// players, ships (the one this player flies too) and the deck under a predicted character stepped back and forth.
/// Body velocities are scaled by the server rate, so a deck moves as fast as its velocity says on a slower server.
/// -y51online:playout:off goes back to the game's clock (0.18.1), -y51online:playoutrate:off leaves velocities alone.
/// </summary>
internal static class MotionSmoothing
{
	private static int HoldFrames = 3;

	private static bool _buffer;

	private static volatile HashSet<DEntity> _characters = new();

	// The client scene those DEntities belong to (a co-op host's server scene numbers its entities on its own).
	private static volatile Keen.VRage.DCS.Scenes.Scene? _scene;

	// The newest sample time each character's cadence was already widened for (Evaluate runs every frame, the game
	// recomputes the cadence only when a sample arrives).
	private static readonly ConcurrentDictionary<DEntity, TimeSpan> Widened = new();

	// Samples not yet handed to the playback, per character, oldest first. Filled on the network path, emptied on the
	// client session thread.
	private static readonly object Gate = new();
	private static readonly Dictionary<Entity, Sync.SampleBuffer<EntityMotionState>> Held = new();

	// Characters whose samples arrived through the buffer (read by the playback job).
	private static readonly ConcurrentDictionary<DEntity, bool> Buffered = new();
	internal static long HeldSamples, Released, Late;

	private static readonly bool PlayoutOff = MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "playout:off");
	private static readonly bool PlayoutRateOff = MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "playoutrate:off");
	private static bool _playoutInstalled;

	// Under Gate: the joined player's playback clock and each body's samples on it.
	// -y51online:playbeat:off: pre2's clock (held at the newest sample whenever no sample came, every quiet tick counted).
	private static readonly Sync.PlayoutClock Clock = new() { LegacyCap = MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "playbeat:off") };
	private static readonly Dictionary<Entity, Sync.PlayoutTrack<EntityMotionState>> Tracks = new();
	private static volatile MotionInterpolationSessionComponent? _playInterpolation;
	private static long _nextClock, _nextStep, _playoutLogMs, _playoutRetryMs;
	private static volatile bool _haveNext;
	internal static long PlayoutHolds, PlayoutBlends, PlayoutFailures, BodyFailures;

	// Bodies whose playback failed: left to the game's own playback while this world lasts (under Gate).
	private static readonly HashSet<Entity> Refused = new();

	/// <summary>A joined player's game: SE2 Online plays back everything the server moves (<see cref="Sync.PlayoutClock"/>).</summary>
	private static bool Playout => _playoutInstalled && MpPlugin.App?.ServerSession == null && Y51_SE2Online.NetHost.NetSession.Current?.Client != null;

	/// <summary>Main menu: let go of the last world's scene and characters.</summary>
	internal static void Reset()
	{
		_scene = null;
		_characters = new();
		Widened.Clear();
		Buffered.Clear();
		lock (Gate)
		{
			Held.Clear();
			Tracks.Clear();
			Refused.Clear();
			Clock.Reset();
			_haveNext = false;
			_playInterpolation = null;
			_playoutRetryMs = 0;
			_linksSession = null;
			_links = null;
			AskedParent.Clear();
		}
	}

	public static void Install(Harmony harmony)
	{
		bool on = !MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "interpolate:off");
		MotionInterpolationSettings.Instance.Interpolate = on;
		if (on && !MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "motiondelay:off"))
		{
			_buffer = !MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "motionbuffer:off");
			if (!_buffer) HoldFrames = 1;
			harmony.Patch(AccessTools.Method(AccessTools.TypeByName("Keen.VRage.Game.Multiplayer.MotionStateApplicationSystem"), "EvaluateMotionState"),
				prefix: new HarmonyMethod(typeof(MotionSmoothing), nameof(EvaluatePrefix)) { priority = Priority.First });
		}
		if (on && !MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "blendcap:off"))
		{
			var system = AccessTools.TypeByName("Keen.VRage.Game.Multiplayer.MotionStateApplicationSystem");
			harmony.Patch(AccessTools.Method(system, "RecordMotionState"), postfix: new HarmonyMethod(typeof(MotionSmoothing), nameof(RecordPostfix)));
			harmony.Patch(AccessTools.Method(system, "EvaluateMotionState"), prefix: new HarmonyMethod(typeof(MotionSmoothing), nameof(BlendEndPrefix)) { priority = Priority.Last });
		}
		if (on && !PlayoutOff)
		{
			Clock.Wall = !WallOff;
			Sync.PlayoutTrack<EntityMotionState>.SplitReordered = !MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "playgap:off");
			harmony.Patch(AccessTools.Method(typeof(MotionInterpolationSessionComponent), "AdvanceClock"), prefix: new HarmonyMethod(typeof(MotionSmoothing), nameof(AdvanceClockPrefix)));
			_playoutInstalled = true;
		}
		GameLog.Instance.Info(on ? $"motion: the game's motion playback (interpolation) is on; {(_playoutInstalled ? "a joined game plays everything the server moves on SE2 Online's playback clock" : "the game's own clock")}; on a hosting game other characters {HoldFrames} frames back{(_buffer ? ", their samples buffered" : "")}" : "motion: playback left off (-y51online:interpolate:off); replicated motion snaps to each sample");
	}

	/// <summary>Client session thread, after each update: the other characters in this game's scene, and the held samples
	/// due for the next frame.</summary>
	public static void ClientTick(Session session)
	{
		var own = new HashSet<Entity>();
		if (session.SessionComponents.TryGet<ClientPlayersSessionComponent>()?.LocalPlayerController is { } controller)
			foreach (var c in controller.ControlledEntities)
				if ((c.Controllable as Component)?.Entity is { } e) own.Add(e);
		long me = OwnMotion.LocalIdentity(session);
		var characters = new HashSet<DEntity>();
		foreach (var character in session.GetEntitiesOfType<CharacterLifeComponent>())
			if (!own.Contains(character) && (me == 0 || SyncEngine.TeamPermissions.OwnerOf(character) != me)) characters.Add(character.DEntity);
		_characters = characters;
		_scene = session.GetEntitiesOfType<CharacterLifeComponent>().FirstOrDefault()?.Scene;
		foreach (var gone in Widened.Keys)
			if (!characters.Contains(gone)) Widened.TryRemove(gone, out _);
		foreach (var gone in Buffered.Keys)
			if (!characters.Contains(gone)) Buffered.TryRemove(gone, out _);
		if (Playout)
		{
			try { if (MpClock.NowMs() >= _playoutRetryMs) Play(session); }
			catch (Exception ex)
			{
				// A frozen clock would freeze every body: the game's own clock and samples take over for a while, then it retries.
				lock (Gate)
				{
					_haveNext = false;
					try { foreach (var e in Tracks.Keys.ToList()) Drop(e); }
					catch { Tracks.Clear(); }
					_playoutRetryMs = MpClock.NowMs() + 5000;
				}
				GameLog.Instance.WarnOnce("motion-playout", "motion: SE2 Online's playback failed; the game's own playback takes over for 5 s at a time: " + ex);
				PlayoutFailures++;
			}
			return;
		}
		_haveNext = false;
		if (_buffer) Release(session);
	}

	// A sample that arrives after the playback ran past the newest one starts a blend out of the old pair's track, as long
	// as the playback ran past it, up to MaxExtrapolation (5 s). The old track is clamped two sample gaps past its end, so
	// after a pause in the samples (a character standing still sends none, then walks off) the body stayed pinned near
	// where it had stood for seconds while the server's moved on: the 1-3 m spikes on remote players and NPCs.
	private static readonly TimeSpan MaxBlend = TimeSpan.FromMilliseconds(100);
	internal static long BlendsCut;

	private static void RecordPostfix(ref MotionHistory history)
	{
		if (history.BlendDuration <= MaxBlend) return;
		history.BlendDuration = MaxBlend;
		BlendsCut++;
	}

	// The game ends a blend only when the next sample arrives; past its end the blend weight goes negative and pushes the
	// body away from the old track. A cut blend ends on time even when the samples pause right after it began.
	private static void BlendEndPrefix(ref MotionHistory history, MotionInterpolationSessionComponent interpolation)
	{
		if (history.BlendDuration != TimeSpan.Zero && Cursor(interpolation, history) - history.BlendStart >= history.BlendDuration) history.BlendDuration = TimeSpan.Zero;
	}

	private static void EvaluatePrefix(DEntity entity, ref MotionHistory history, MotionInterpolationSessionComponent interpolation)
	{
		// On the playback clock every body's cadence is written by Play.
		if (_haveNext && ReferenceEquals(interpolation, _playInterpolation)) return;
		if (!_characters.Contains(entity) || history.Newest.ServerTime == TimeSpan.Zero) return;
		if (Widened.TryGetValue(entity, out var done) && done == history.Newest.ServerTime) return;
		Widened[entity] = history.Newest.ServerTime;
		// Only a character whose samples go through the buffer can be played further back: a co-op host's own scene gets
		// them straight from its in-process server, and the playback keeps two samples, so a cursor three frames back
		// would stand on the older one.
		history.Cadence += interpolation.FrameStep * (Buffered.ContainsKey(entity) ? HoldFrames : 1);
	}

	/// <summary>SE2 Online's transport (client): another character's motion sample goes to the buffer instead of straight
	/// into its data. True when it was held.</summary>
	internal static bool TryHold(Entity target, in EntityMotionState state)
	{
		if (MotionTrace.Recording) MotionTrace.Inbound(target, in state);
		if (Playout) return TryTrack(target, in state);
		var characters = _characters;
		if (!_buffer || characters.Count == 0 || !ReferenceEquals(target.Scene, _scene) || !characters.Contains(target.DEntity) || !target.Data.Has<MotionHistory>()) return false;
		lock (Gate)
		{
			if (!Held.TryGetValue(target, out var queue)) Held[target] = queue = new Sync.SampleBuffer<EntityMotionState>();
			Buffered[target.DEntity] = true;
			queue.Add(state.ServerTime.Ticks, (state.Flags & MotionStateFlags.Teleported) != 0, state);
			HeldSamples++;
		}
		return true;
	}

	/// <summary>SE2 Online's transport (client): something from the server was read this tick (the server runs).</summary>
	internal static void Heartbeat()
	{
		if (!Playout) return;
		lock (Gate) Clock.Heartbeat();
	}

	// Joined game: every sample sets the clock; every body with a playback (not the character this game predicts) is held.
	private static bool TryTrack(Entity target, in EntityMotionState state)
	{
		lock (Gate)
		{
			Clock.Arrive(state.ServerTime.Ticks);
			if (MpClock.NowMs() < _playoutRetryMs) return false;
			if (!ReferenceEquals(target.Scene, MpPlugin.App?.ClientSession?.Scene) || ReferenceEquals(target, OwnMotion.Driving)) return false;
			bool first = (state.Flags & MotionStateFlags.Teleported) != 0;
			// The sample that attaches a body's playback goes to the game as before, and starts its track too (the next one
			// would otherwise be held for a delay).
			bool playback = target.Data.Has<MotionHistory>();
			if (!playback && !first) return false;
			// Dead characters (ragdolls, corpses) stay with the game's own playback and SE2 Online's corpse motion.
			if (Dead(target)) { Drop(target); return false; }
			if (Refused.Contains(target)) return false;
			if (!Tracks.TryGetValue(target, out var track))
				Tracks[target] = track = new Sync.PlayoutTrack<EntityMotionState>(static (s, time) => s with { ServerTime = TimeSpan.FromTicks(time) },
					VelocityOff ? null : (s, time) => Extrapolated(target, s, time), static s => s.ParentEpoch == 0) { BlendGap = BlendAlwaysOn ? null : PoseGap };
			track.Add(state.ServerTime.Ticks, first, (state.Flags & MotionStateFlags.Resting) != 0, state);
			if (!playback) return false;
			HeldSamples++;
		}
		return true;
	}

	// -y51online:playwall:off: the tick-counted clock of pre3 (Sync.PlayoutClock.Wall).
	private static readonly bool WallOff = MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "playwall:off");
	private static readonly System.Diagnostics.Stopwatch Wall = System.Diagnostics.Stopwatch.StartNew();
	// -y51online:playblend:off: every late pair blends in as in pre3.
	private static readonly bool BlendAlwaysOn = MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "playblend:off");

	private static double PoseGap(EntityMotionState a, EntityMotionState b, double fa, EntityMotionState c, EntityMotionState d, double fc)
	{
		if (KeenLerp == null) return double.PositiveInfinity;
		var com = Keen.VRage.Library.Mathematics.Vector3D.Zero;
		var old = KeenLerp(in a, in b, (float)fa, com).Transform;
		var now = KeenLerp(in c, in d, (float)fc, com).Transform;
		// Turned apart by more than half a degree: blended, whatever the distance (a spinning body).
		float dot = Math.Abs(Keen.VRage.Library.Mathematics.Quaternion.Dot(old.Orientation, now.Orientation));
		if (dot < 0.99999f) return double.PositiveInfinity;
		return (old.Position - now.Position).Length();
	}

	// -y51online:playvelocity:off: past the newest sample the game's line through the last two samples, as in pre2.
	private static readonly bool VelocityOff = MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "playvelocity:off");

	private static bool Dead(Entity e) => SyncEngine.EngineCodec.IsDead(e) || e.TryGet<CharacterLifeComponent>() is { IsAlive: false };

	// A sample carried to server time <paramref name="time"/> by its velocity: the centre of mass moves on, the body turns
	// about it (the game's own interpolation works on the centre of mass the same way). Bodies in a parent's frame (a
	// character on a ship) keep the game's line through their last two relative poses (PlayoutTrack byVelocity).
	private static EntityMotionState Extrapolated(Entity body, EntityMotionState s, long time)
	{
		var moved = s with { ServerTime = TimeSpan.FromTicks(time) };
		if ((s.Flags & MotionStateFlags.HasBody) == 0 || (s.Flags & MotionStateFlags.Resting) != 0) return moved;
		float dt = (float)((time - s.ServerTime.Ticks) / (double)TimeSpan.TicksPerSecond);
		if (dt <= 0) return moved;
		var com = CentreOfMass(body);
		var centre = Keen.VRage.Core.WorldTransform.Transform(in com, in s.Transform) + (Keen.VRage.Library.Mathematics.Vector3D)(s.Body.LinearVelocity * dt);
		float spin = s.Body.AngularVelocity.Length();
		if (spin > 1e-4f)
			moved.Transform.Orientation = Keen.VRage.Library.Mathematics.Quaternion.Normalize(Keen.VRage.Library.Mathematics.Quaternion.CreateFromAxisAngle(s.Body.AngularVelocity / spin, spin * dt) * s.Transform.Orientation);
		moved.Transform.Position = centre - Keen.VRage.Core.WorldTransform.TransformDirection(com, moved.Transform);
		return moved;
	}

	private static Keen.VRage.Library.Mathematics.Vector3D CentreOfMass(Entity body)
		=> body.Data.TryGet<Keen.VRage.Physics.Data.RigidBodyMassProperties>(out var mass) ? (Keen.VRage.Library.Mathematics.Vector3D)mass.CenterOfMass : Keen.VRage.Library.Mathematics.Vector3D.Zero;

	// A body leaving SE2 Online's playback (dead, now predicted, or the playback failed) gets its newest real sample alone,
	// as the game would have it: our pair, cadence and blend (and a sample carried past the newest) would stay in its
	// history, where the game extrapolates them further and drops its own later samples as older.
	private static void Drop(Entity entity)
	{
		if (!Tracks.Remove(entity, out var track)) return;
		AskedParent.Remove(entity);
		var scene = MpPlugin.App?.ClientSession?.Scene;
		if (scene == null || !track.Last(out var last)) return;
		ref var history = ref DataMarshal.TryGetDataPointer<MotionHistory>(scene, entity.DEntity, writeAccess: true);
		if (System.Runtime.CompilerServices.Unsafe.IsNullRef(ref history)) return;
		// A corpse: its ragdoll takes over a frame later (f75); a sample put back would pin it there meanwhile.
		if (Dead(entity)) { history = default; return; }
		history.Previous = default;
		history.Newest = last;
		history.Cadence = TimeSpan.Zero;
		history.BlendDuration = TimeSpan.Zero;
	}

	// The game's clock for the next tick is ours (Play worked it out after the last tick).
	private static bool AdvanceClockPrefix(MotionInterpolationSessionComponent __instance)
	{
		if (!_haveNext || !ReferenceEquals(__instance, _playInterpolation)) return true;
		__instance._previousClock = __instance._clock;
		__instance._clock = TimeSpan.FromTicks(System.Threading.Interlocked.Read(ref _nextClock));
		__instance._frameStep = TimeSpan.FromTicks(System.Threading.Interlocked.Read(ref _nextStep));
		return false;
	}

	// After each tick: the clock for the next one, and for every held body the pair and the cursor it plays there.
	private static void Play(Session session)
	{
		var interpolation = session.SessionComponents.TryGet<MotionInterpolationSessionComponent>();
		var scene = MpPlugin.App?.ClientSession?.Scene;
		lock (Gate)
		{
			if (interpolation == null || scene == null) { _haveNext = false; return; }
			if (!ReferenceEquals(interpolation, _playInterpolation)) { _playInterpolation = interpolation; _haveNext = false; DriveDecks(session); }
			double was = Clock.Play;
			bool primed = Clock.Primed;
			// Havok moves a character at most 310 m/s (CharacterSpeedLimit): a 300 m/s deck played faster than its speed
			// leaves the character standing on it behind. While the player stands on a deck over 250 m/s a tick plays at most
			// 2% more than one server tick.
			float deckSpeed = Deck is { } onDeck && !Dead(onDeck) && onDeck.Data.TryGet<Keen.VRage.Physics.Data.RigidBodyData>(out var deckBody) ? deckBody.LinearVelocity.Length() : 0;
			_fastDeck = deckSpeed > (_fastDeck ? FastDeckOff : FastDeck);
			Clock.StepCap = _fastDeck ? Sync.PlayoutClock.Frame * (1 + FastDeckNudge) : double.PositiveInfinity;
			double play = Clock.Advance(WallOff ? double.NaN : Wall.Elapsed.TotalMilliseconds);
			if (!Clock.Primed) return;
			if (MotionTrace.Recording)
			{
				var t = Clock.LastTick;
				MotionTrace.Note("clock", "clock", FormattableString.Invariant($"gap={t.GapFrames:0.00} steady={(t.Steady ? 1 : 0)} play={play:0} expected={Clock.Expected:0} line={t.Line:0} newest={Clock.Newest} delay={Clock.Delay / 1e4:0.0} jitter={Clock.Jitter / 1e4:0.0} rate={Clock.Rate / Sync.PlayoutClock.Frame:0.000} error={t.Error / 1e4:0.0} jumped={(t.Jumped ? 1 : 0)} held={(t.HeldAtCap ? 1 : 0)} pending={(Clock.Newest - play) / Sync.PlayoutClock.Frame:0.00}"));
			}
			long clock = (long)play;
			long step = Math.Max(1, (long)Clock.Rate);
			double moved = primed ? play - was : Clock.Rate;
			// k: server time per client time; a body's velocity moves it that much faster or slower here.
			float k = PlayoutRateOff ? 1f : (float)Math.Clamp(Clock.Rate / Sync.PlayoutClock.Frame, 0.5, 2.0);
			List<Entity>? gone = null, dropped = null;
			var deck = Deck;
			_deckNextValid = false;
			foreach (var (entity, track) in Tracks)
			{
				try
				{
					if (Dead(entity) || ReferenceEquals(entity, OwnMotion.Driving)) { (dropped ??= new()).Add(entity); continue; }
					ref var history = ref DataMarshal.TryGetDataPointer<MotionHistory>(scene, entity.DEntity, writeAccess: true);
					if (System.Runtime.CompilerServices.Unsafe.IsNullRef(ref history))
					{
						// Its playback went (it stopped being a body the game plays back): it gets the newest held sample as the game
						// would. A track with only the sample that went to the game anyway (never got a playback) just goes.
						if (track.Count > 1 && track.Last(out var last)) entity.Data.Set(last);
						(gone ??= new()).Add(entity);
						continue;
					}
					bool isDeck = ReferenceEquals(entity, deck);
					// A pause in the stream (a burst held up on the way) stopped the deck in Havok at the end of its samples while
					// the character on it flew on at the ship's speed: the deck goes on by its velocity up to DeckLead instead,
					// ahead of the held clock (its samples are written that much earlier, so the game's cursor finds it there).
					track.LongExtrapolation = isDeck && deckSpeed > 1 ? DeckLead : 0;
					track.CursorStepCap = isDeck ? Clock.StepCap : double.PositiveInfinity;
					double cursor = track.Cursor(play, moved, Clock.Rate);
					if (!track.Pick(ref cursor, out var previous, out var next, out bool extrapolating, out var blend)) continue;
					if (MotionTrace.Recording) MotionTrace.Play(entity, (long)cursor, previous.ServerTime.Ticks, next.ServerTime.Ticks, extrapolating, track.Lead, Clock.Newest);
					if (extrapolating) PlayoutHolds++;
					AskParent(session, entity, in next);
					long lead = (long)track.Lead;
					history.Previous = Scaled(Earlier(previous, lead), k);
					history.Newest = Scaled(Earlier(next, lead), k);
					long at = (long)cursor - lead;
					// The game's cursor is its clock minus (cadence - frame step): this body's own cursor.
					history.Cadence = TimeSpan.FromTicks(Sync.PlayoutClock.CadenceFor(clock, at, step));
					if (blend is { } b)
					{
						history.BlendA = Scaled(Earlier(b.A, lead), k);
						history.BlendB = Scaled(Earlier(b.B, lead), k);
						history.BlendStart = TimeSpan.FromTicks((long)b.Start - lead);
						history.BlendDuration = TimeSpan.FromTicks(Math.Max(1, (long)b.Duration));
						PlayoutBlends++;
					}
					else history.BlendDuration = TimeSpan.Zero;
					// Where the game's playback puts the grid the predicted character stands on next tick
					// (MotionStateApplicationSystem.Play without a parent frame): OwnMotion carries the character by the same motion.
					if (ReferenceEquals(entity, deck) && !entity.Data.Has<MotionParent>() && KeenEvaluate != null && KeenLerp != null)
					{
						var com = CentreOfMass(entity);
						var configuration = interpolation.Configuration;
						var cursorAt = TimeSpan.FromTicks(at);
						var pose = KeenEvaluate(in history.Previous, in history.Newest, cursorAt, com, configuration, out _);
						if (history.BlendDuration != TimeSpan.Zero)
							pose = KeenLerp(in pose, KeenEvaluate(in history.BlendA, in history.BlendB, cursorAt, com, configuration, out _), 1f - (float)((cursorAt - history.BlendStart) / history.BlendDuration), com);
						_deckNext = pose.Transform;
						_deckNextBody = pose.Body;
						_deckNextCentre = Keen.VRage.Core.WorldTransform.Transform(in com, in pose.Transform);
						_deckNextValid = true;
					}
				}
				catch (Exception ex)
				{
					// One body that cannot be played (a pose the game's code chokes on) goes back to the game's own playback;
					// before, any failure handed every body to the game's clock for 5 s at a time.
					(dropped ??= new()).Add(entity);
					Refused.Add(entity);
					BodyFailures++;
					GameLog.Instance.WarnOnce("motion-playout-body", "motion: one body could not be played back; it goes back to the game's own playback: " + ex);
				}
			}
			if (gone != null) foreach (var e in gone) { Tracks.Remove(e); AskedParent.Remove(e); }
			if (dropped != null) foreach (var e in dropped) Drop(e);
			System.Threading.Interlocked.Exchange(ref _nextClock, clock);
			System.Threading.Interlocked.Exchange(ref _nextStep, step);
			_haveNext = true;
			long now = MpClock.NowMs();
			if (now - _playoutLogMs >= 60000)
			{
				_playoutLogMs = now;
				GameLog.Instance.Info($"motion: playback {Clock.Delay / 1e4:0} ms behind the stream (arrival jitter {Clock.Jitter / 1e4:0} ms), server time {Clock.Rate / Sync.PlayoutClock.Frame * 60:0.#} ticks/s against our 60, {Tracks.Count} bodies; {Clock.Jumps} catch-ups, {Clock.Stalls} server stalls, {Clock.Held} ticks held at the stream's end, {PlayoutHolds} body-ticks extrapolated, {PlayoutBlends} blends, {PlayoutFailures} playback failures (5 s on the game's clock each), {BodyFailures} bodies handed back (totals)");
			}
		}
	}

	// Havok moves a played-back grid (a kinematic body here) to each new pose by velocity over the step, unless the pose is
	// 5.5 m or more away (SceneSettings.MinDrivingTeleportDistance): then it puts it there. A grid at the 300 m/s speed
	// limit moves 5 m a tick, 5.5 m with the clock's 10% catch-up: every such tick the deck jumped out from under a
	// character standing on it instead of carrying it (thrown across the deck, out of a cockpit). Here only a pose more
	// than DriveTeleportMeters away (a real teleport, a jump) is put there. -y51online:deckdrive:off keeps the game's.
	private const float DriveTeleportMeters = 7;
	private static readonly bool DriveOff = MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "deckdrive:off");

	private const float FastDeck = 250, FastDeckOff = 230;
	private const long DeckLead = TimeSpan.TicksPerSecond / 4;
	private static bool _fastDeck;
	private const double FastDeckNudge = 0.02;

	private static void DriveDecks(Session session)
	{
		if (DriveOff || session.SessionComponents.TryGet<Keen.VRage.Physics.Havok.HavokSessionComponent>() is not { } havok) return;
		var settings = havok._bodyDrivingSettings;
		if (settings.m_minTeleportDistanceSquared >= DriveTeleportMeters * DriveTeleportMeters) return;
		float was = MathF.Sqrt(settings.m_minTeleportDistanceSquared);
		settings.m_minTeleportDistanceSquared = DriveTeleportMeters * DriveTeleportMeters;
		havok._bodyDrivingSettings = settings;
		GameLog.Instance.Info($"motion: played-back grids are driven to poses up to {DriveTeleportMeters:0} m away (the game: {was:0.#} m), so a fast ship carries whoever stands on it");
	}

	/// <summary>The newest server time any motion sample carried (joined games; the trace).</summary>
	internal static long NewestServerTime => Clock.Newest;

	/// <summary>The grid the player's predicted character stands on (OwnMotion); Play works out its next pose.</summary>
	internal static volatile Entity? Deck;
	private static Keen.VRage.Core.WorldTransform _deckNext;
	private static Keen.VRage.Physics.Data.RigidBodyData _deckNextBody;
	private static Keen.VRage.Library.Mathematics.Vector3D _deckNextCentre;
	private static volatile bool _deckNextValid;

	/// <summary>Client session thread, after Play: where the game's playback puts <paramref name="grid"/> on the next tick;
	/// <paramref name="body"/> is its played-back velocity there (at the centre of mass <paramref name="centre"/>).</summary>
	internal static bool DeckNext(Entity grid, out Keen.VRage.Core.WorldTransform next, out Keen.VRage.Physics.Data.RigidBodyData body, out Keen.VRage.Library.Mathematics.Vector3D centre)
	{
		lock (Gate)
		{
			next = _deckNext;
			body = _deckNextBody;
			centre = _deckNextCentre;
			return _deckNextValid && _haveNext && ReferenceEquals(grid, Deck);
		}
	}

	private delegate EntityMotionState EvaluateFn(in EntityMotionState previous, in EntityMotionState newest, TimeSpan cursor, Keen.VRage.Library.Mathematics.Vector3D centerOfMass, MotionInterpolationConfiguration configuration, out TimeSpan exactAt);
	private delegate EntityMotionState LerpFn(in EntityMotionState a, in EntityMotionState b, float factor, Keen.VRage.Library.Mathematics.Vector3D centerOfMass);
	private static readonly EvaluateFn? KeenEvaluate = KeenDelegate<EvaluateFn>("Evaluate");
	private static readonly LerpFn? KeenLerp = KeenDelegate<LerpFn>("Lerp");

	private static T? KeenDelegate<T>(string name) where T : Delegate
	{
		try
		{
			var method = AccessTools.Method(AccessTools.TypeByName("Keen.VRage.Game.Multiplayer.MotionStateApplicationSystem"), name);
			return method == null ? null : (T)System.Delegate.CreateDelegate(typeof(T), method);
		}
		catch (Exception ex)
		{
			GameLog.Instance.WarnOnce("motion-" + name, $"motion: the game's {name} cannot be called ({ex.GetType().Name}); the deck under a predicted character is not carried");
			return null;
		}
	}

	// The game asks the server for a body's motion parent (a character on a ship) when a sample names a parent link it
	// does not have, again on later samples while it is missing. Held samples never reach that check after the first,
	// so a link that missed its signal (a body streamed in before its ship) was never asked for again.
	private static readonly Type? ParentLinks = AccessTools.TypeByName("Keen.VRage.Game.Multiplayer.MotionParentClientSessionComponent");
	private static readonly System.Reflection.MethodInfo? RequestParent = ParentLinks == null ? null : AccessTools.Method(ParentLinks, "RequestParent");
	private static readonly System.Reflection.MethodInfo? ClearRequest = ParentLinks == null ? null : AccessTools.Method(ParentLinks, "ClearRequest");
	private static Session? _linksSession;
	private static object? _links;
	private static readonly HashSet<Entity> AskedParent = new();

	private static void AskParent(Session session, Entity entity, in EntityMotionState next)
	{
		if (RequestParent == null || ClearRequest == null) return;
		if (!ReferenceEquals(session, _linksSession))
		{
			_linksSession = session;
			_links = null;
			AskedParent.Clear();
			foreach (var component in session.SessionComponents.Components)
				if (ParentLinks!.IsInstanceOfType(component)) { _links = component; break; }
		}
		if (_links == null) return;
		bool missing = next.ParentEpoch != 0 && (!entity.Data.TryGet<MotionParent>(out var link) || link.Epoch != next.ParentEpoch);
		if (missing)
		{
			AskedParent.Add(entity);
			RequestParent.Invoke(_links, new object[] { entity.DEntity });
		}
		else if (AskedParent.Remove(entity)) ClearRequest.Invoke(_links, new object[] { entity.DEntity });
	}

	private static EntityMotionState Earlier(in EntityMotionState s, long by)
		=> by == 0 || s.ServerTime == TimeSpan.Zero ? s : s with { ServerTime = s.ServerTime - TimeSpan.FromTicks(by) };

	private static EntityMotionState Scaled(in EntityMotionState s, float k)
	{
		if (k == 1f || (s.Flags & MotionStateFlags.HasBody) == 0) return s;
		var scaled = s;
		scaled.Body.LinearVelocity *= k;
		scaled.Body.AngularVelocity *= k;
		return scaled;
	}

	/// <summary>The game's playback cursor for a character (MotionStateApplicationSystem.Cursor).</summary>
	internal static TimeSpan Cursor(MotionInterpolationSessionComponent interpolation, in MotionHistory history)
		=> interpolation.Clock - (history.Cadence > interpolation.FrameStep ? history.Cadence - interpolation.FrameStep : TimeSpan.Zero);

	internal static string Stats => (_buffer || _playoutInstalled ? $"motion buffer: {HeldSamples} samples held, {Released} handed over, {Late} late" : "motion buffer off") + $", {BlendsCut} long blends cut" + (_playoutInstalled ? $", playback {Clock.Delay / 1e4:0} ms behind (jitter {Clock.Jitter / 1e4:0} ms)" : "");

	private static void Release(Session session)
	{
		var interpolation = session.SessionComponents.TryGet<MotionInterpolationSessionComponent>();
		lock (Gate)
		{
			foreach (var (entity, queue) in Held.ToList())
			{
				if (queue.Count == 0)
				{
					if (SyncEngine.EngineCodec.IsDead(entity)) Held.Remove(entity);
					continue;
				}
				if (SyncEngine.EngineCodec.IsDead(entity) || !entity.Data.TryGet<MotionHistory>(out var history) || interpolation == null)
				{
					// Gone, or no playback here: the newest held sample goes in as the game would have applied it.
					if (queue.Last(out var last) && !SyncEngine.EngineCodec.IsDead(entity)) entity.Data.Set(last);
					Held.Remove(entity);
					continue;
				}
				// The cursor next frame (the newest sample handed over must be at or past it).
				long next = (Cursor(interpolation, history) + interpolation.FrameStep).Ticks;
				if (!queue.Due(next, interpolation.FrameStep.Ticks, history.Newest.ServerTime.Ticks, out var sample, out long time)) continue;
				if (time <= history.Newest.ServerTime.Ticks) Late++;
				DataMarshal.GetDataPointer<EntityMotionState>(entity.Scene, entity.DEntity) = sample;
				Released++;
			}
		}
	}
}
