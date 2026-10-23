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
using Transport = Keen.VRage.Multiplayer.Transport.OutOfProcess.OutOfProcessPeerTransport;

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

	/// <summary>Main menu: let go of the last world's scene and characters.</summary>
	internal static void Reset()
	{
		_scene = null;
		_characters = new();
		Widened.Clear();
		Buffered.Clear();
		lock (Gate) Held.Clear();
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
			if (_buffer) harmony.Patch(AccessTools.Method(typeof(Transport), "ProcessIncomingSampledData"), prefix: new HarmonyMethod(typeof(MotionSmoothing), nameof(SampledPrefix)));
		}
		if (on && !MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "blendcap:off"))
		{
			var system = AccessTools.TypeByName("Keen.VRage.Game.Multiplayer.MotionStateApplicationSystem");
			harmony.Patch(AccessTools.Method(system, "RecordMotionState"), postfix: new HarmonyMethod(typeof(MotionSmoothing), nameof(RecordPostfix)));
			harmony.Patch(AccessTools.Method(system, "EvaluateMotionState"), prefix: new HarmonyMethod(typeof(MotionSmoothing), nameof(BlendEndPrefix)) { priority = Priority.Last });
		}
		GameLog.Instance.Info(on ? $"motion: the game's motion playback (interpolation) is on; other characters {HoldFrames} frames back{(_buffer ? ", their samples buffered" : "")}" : "motion: playback left off (-y51online:interpolate:off); replicated motion snaps to each sample");
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
		if (!_characters.Contains(entity) || history.Newest.ServerTime == TimeSpan.Zero) return;
		if (Widened.TryGetValue(entity, out var done) && done == history.Newest.ServerTime) return;
		Widened[entity] = history.Newest.ServerTime;
		// Only a character whose samples go through the buffer can be played further back: a co-op host's own scene gets
		// them straight from its in-process server, and the playback keeps two samples, so a cursor three frames back
		// would stand on the older one.
		history.Cadence += interpolation.FrameStep * (Buffered.ContainsKey(entity) ? HoldFrames : 1);
	}

	// Network path (client): another character's motion samples go to the buffer instead of straight into its data.
	private static void SampledPrefix(Transport __instance, Transport.SampledDataBatch batch)
	{
		var characters = _characters;
		var scene = __instance.SessionComponent.Entity.Scene;
		if (characters.Count == 0 || !ReferenceEquals(scene, _scene)) return;
		foreach (var list in batch.Lists)
		{
			if (list is not Transport.SampledList<EntityMotionState> motion) continue;
			lock (Gate)
				motion.Entries.RemoveAll(e =>
				{
					var target = __instance.ResolveSampledTarget(e.Id, scene);
					if (target == null || !characters.Contains(target.DEntity) || !target.Data.Has<MotionHistory>()) return false;
					if (!Held.TryGetValue(target, out var queue)) Held[target] = queue = new Sync.SampleBuffer<EntityMotionState>();
					Buffered[target.DEntity] = true;
					queue.Add(e.Value.ServerTime.Ticks, (e.Value.Flags & MotionStateFlags.Teleported) != 0, e.Value);
					HeldSamples++;
					return true;
				});
		}
	}

	/// <summary>SE2 Online's engine transport (client): another character's motion sample goes to the buffer instead of
	/// straight into its data, as on Keen's transport above. True when it was held.</summary>
	internal static bool TryHold(Entity target, in EntityMotionState state)
	{
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

	/// <summary>The game's playback cursor for a character (MotionStateApplicationSystem.Cursor).</summary>
	internal static TimeSpan Cursor(MotionInterpolationSessionComponent interpolation, in MotionHistory history)
		=> interpolation.Clock - (history.Cadence > interpolation.FrameStep ? history.Cadence - interpolation.FrameStep : TimeSpan.Zero);

	internal static string Stats => (_buffer ? $"motion buffer: {HeldSamples} samples held, {Released} handed over, {Late} late" : "motion buffer off") + $", {BlendsCut} long blends cut";

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
