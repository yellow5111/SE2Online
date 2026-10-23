using System;
using System.Linq;
using HarmonyLib;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.Core.Game.GameSystems.Animations;
using Keen.VRage.DCS.Components;
using Keen.VRage.Game.Components.Animations;
using Keen.VRage.Game.Multiplayer;
using Keen.VRage.Library.Utils;
using Keen.VRage.Multiplayer.Replications;
using Keen.VRage.Multiplayer.Transport.OutOfProcess;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;
using Transport = Keen.VRage.Multiplayer.Transport.OutOfProcess.OutOfProcessPeerTransport;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>
/// Plays a remote character's animation in step with its body. SE2 4.0 sends a character's animation blackboard
/// changes (walk, jump, land, crouch, jetpack, aim, shots, flinches) reliably and applies them the moment they arrive,
/// while its body is played back from the motion samples of the same server frame a playout delay later
/// (<see cref="MotionInterpolationSettings"/>, turned on by <see cref="MotionSmoothing"/>). Here each change is held,
/// stamped with the server time of its frame, and handed to the game right before that character's animation update in
/// the frame its playout cursor reaches that time, in arrival order. A joined player's game stamps a change with the
/// motion samples that left the server right after it; a co-op host's own game (its scene follows the server's one
/// frame at a time, in process) with the frame after the newest sample it has seen. Changes of a character without
/// played-back motion go through at once; a held change is never kept longer than
/// <see cref="PlayoutQueue{TKey,T}.MaxHoldMs"/>; a character that is gone takes its held changes with it (a character
/// that comes back, or a player who joins later, gets its full blackboard in the entity itself). This game's own
/// predicted character takes the server's changes at once, minus what <see cref="OwnAnimation"/> sets itself.
/// -y51online:animsync:off leaves Keen's timing alone.
/// </summary>
internal static class AnimPlayout
{
	private delegate void ReceiveFn(AnimationBlackboardComponent self, ReadOnlySpan<(StringHash, float)> floats, ReadOnlySpan<(StringHash, int)> ints, ReadOnlySpan<(StringHash, StringHash)> strings, ReadOnlySpan<(StringHash, bool)> bools);

	private sealed record Delta(AnimationBlackboardComponent Target, (StringHash, float)[] Floats, (StringHash, int)[] Ints, (StringHash, StringHash)[] Strings, (StringHash, bool)[] Bools);

	private static ReceiveFn _receive = null!;

	// Filled on the inbound replication job, released on each character's animation job (several run at once) and after
	// the frame: every use takes the lock.
	private static readonly PlayoutQueue<Entity, Delta> Held = new();
	private static bool _off, _inbound;
	private static long _newestSample;
	private static Session? _session;
	private static MotionInterpolationSessionComponent? _interpolation;

	/// <summary>The server time of the held change being applied now on this thread; null while the game applies one.</summary>
	[ThreadStatic]
	internal static long? Replaying;

	/// <summary>Main menu: let go of the last world's session and held changes.</summary>
	internal static void Reset()
	{
		_session = null;
		_interpolation = null;
		_newestSample = 0;
		lock (Held) Held.Clear();
	}

	public static void Install(Harmony harmony)
	{
		if (MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "animsync:off"))
		{
			_off = true;
			GameLog.Instance.Info("animation timing: remote characters' animation changes are applied on arrival (-y51online:animsync:off)");
		}
		var receive = AccessTools.Method(typeof(AnimationBlackboardComponent), "BlackboardReceive");
		_receive = AccessTools.MethodDelegate<ReceiveFn>(receive);
		harmony.Patch(receive, prefix: new HarmonyMethod(typeof(AnimPlayout), nameof(ReceivePrefix)));
		harmony.Patch(AccessTools.Method(typeof(OutOfProcessPeerTransportSessionComponent), "ProcessFromRemote"),
			prefix: new HarmonyMethod(typeof(AnimPlayout), nameof(BeforeInbound)), postfix: new HarmonyMethod(typeof(AnimPlayout), nameof(AfterInbound)));
		harmony.Patch(AccessTools.Method(typeof(Transport), "ProcessIncomingSampledData"), prefix: new HarmonyMethod(typeof(AnimPlayout), nameof(SampledPrefix)));
		harmony.Patch(AccessTools.Method(typeof(Keen.VRage.Animation.Client.GameObjects.MultiStrategyAnimationControllerComponent), "UpdateAnimationController"),
			prefix: new HarmonyMethod(typeof(AnimPlayout), nameof(AnimationPrefix)) { priority = Priority.Last });
	}

	private static bool ReceivePrefix(AnimationBlackboardComponent __instance, ReadOnlySpan<(StringHash, float)> floats, ReadOnlySpan<(StringHash, int)> ints, ReadOnlySpan<(StringHash, StringHash)> strings, ReadOnlySpan<(StringHash, bool)> bools)
	{
		if (Replaying != null || floats.Length + ints.Length + strings.Length + bools.Length == 0) return true;
		var entity = __instance.Entity;
		if (OwnAnimation.Animating is { } own && ReferenceEquals(entity, own))
		{
			Apply(new Delta(__instance,
				OwnAnimation.Keep(floats.ToArray(), k => (string)k.String, v => BlackboardValue.CreateFrom(v)),
				OwnAnimation.Keep(ints.ToArray(), k => (string)k.String, v => BlackboardValue.CreateFrom(v)),
				OwnAnimation.Keep(strings.ToArray(), k => (string)k.String, v => BlackboardValue.CreateFrom((StringId)v.String)),
				OwnAnimation.Keep(bools.ToArray(), k => (string)k.String, v => BlackboardValue.CreateFrom(v))), 0);
			return false;
		}
		if (!Active() || _interpolation is not { } interpolation) return true;
		lock (Held)
		{
			if (!Held.Holds(entity) && !PlayedBack(entity, out _)) return true;
			Held.Add(entity, new Delta(__instance, floats.ToArray(), ints.ToArray(), strings.ToArray(), bools.ToArray()), MpClock.NowMs());
			// In process the change belongs to the server frame after the newest sample seen (samples are recorded later in
			// this frame); a joined player's game stamps it when that frame's samples are read (SampledPrefix, AfterInbound).
			if (!_inbound) Held.Stamp(Math.Max(_newestSample, interpolation._newestServerTime.Ticks) + interpolation.FrameStep.Ticks);
		}
		return false;
	}

	private static bool Active()
	{
		if (_off || MpPlugin.App?.ClientSession is not { } session) return false;
		if (!ReferenceEquals(session, _session))
		{
			_session = session;
			_interpolation = session.SessionComponents.TryGet<MotionInterpolationSessionComponent>();
			lock (Held) Held.Clear();
			_newestSample = 0;
		}
		return _interpolation?.Enabled == true;
	}

	private static bool PlayedBack(Entity entity, out MotionHistory history)
		=> entity.Data.TryGet(out history) && history.Newest.ServerTime != TimeSpan.Zero && !ReferenceEquals(entity, OwnMotion.Driving);

	private static void BeforeInbound(OutOfProcessPeerTransportSessionComponent __instance)
	{
		if (__instance.IsServer) return;
		_inbound = true;
		OwnAnimation.Restore();
	}

	private static void AfterInbound(OutOfProcessPeerTransportSessionComponent __instance)
	{
		if (__instance.IsServer) return;
		_inbound = false;
		// Changes read with no motion sample after them (nothing moved, or the samples come next read): the newest known time.
		lock (Held) Held.Stamp(_newestSample);
	}

	// The server sends a frame's signals, then that frame's motion samples: changes received before a sample batch
	// belong to its server time.
	private static void SampledPrefix(Transport.SampledDataBatch batch)
	{
		long newest = 0;
		foreach (var list in batch.Lists)
			if (list is Transport.SampledList<EntityMotionState> motion)
				foreach (var entry in motion.Entries)
					newest = Math.Max(newest, entry.Value.ServerTime.Ticks);
		NoteSamples(newest);
	}

	/// <summary>Motion samples were read (the engine transport calls this; Keen's transport through the prefix above).</summary>
	internal static void NoteSamples(long newestServerTicks)
	{
		if (!_inbound || newestServerTicks == 0) return;
		_newestSample = Math.Max(_newestSample, newestServerTicks);
		lock (Held) Held.Stamp(newestServerTicks);
	}

	// Right before a character's animation update, after this frame's motion playback: its changes up to the cursor.
	private static void AnimationPrefix(Component __instance)
	{
		if (_interpolation is not { } interpolation) return;
		var entity = __instance.Entity;
		lock (Held)
		{
			if (!Held.Holds(entity)) return;
			if (!_off && interpolation.Enabled && PlayedBack(entity, out var history)) Held.Release(entity, Cursor(interpolation, history), MpClock.NowMs(), Apply);
			else Held.Flush(entity, Apply);
		}
	}

	/// <summary>Client session thread, after each update: characters whose animation did not update this frame (far
	/// away, or animations off) still get their changes, at most a frame late; gone ones are dropped.</summary>
	public static void ClientTick(Session session)
	{
		if (_interpolation is not { } interpolation || !ReferenceEquals(session, _session)) return;
		lock (Held)
		{
			if (Held.Count == 0) return;
			long now = MpClock.NowMs();
			foreach (var entity in Held.Keys.ToList())
			{
				if (!session.Scene.IsEntityAlive(entity.DEntity)) Held.Drop(entity);
				else if (!_off && interpolation.Enabled && PlayedBack(entity, out var history)) Held.Release(entity, Cursor(interpolation, history), now, Apply);
				else Held.Flush(entity, Apply);
			}
		}
	}

	// The cursor this frame's playback used. Stamps come in whole server frames; one within half a frame of it is due.
	private static long Cursor(MotionInterpolationSessionComponent interpolation, in MotionHistory history)
	{
		var delay = history.Cadence > interpolation.FrameStep ? history.Cadence - interpolation.FrameStep : TimeSpan.Zero;
		return (interpolation.Clock + interpolation.FrameStep * 0.5 - delay).Ticks;
	}

	private static void Apply(Delta delta, long stamp)
	{
		Replaying = stamp;
		try { _receive(delta.Target, delta.Floats, delta.Ints, delta.Strings, delta.Bools); }
		finally { Replaying = null; }
	}
}
