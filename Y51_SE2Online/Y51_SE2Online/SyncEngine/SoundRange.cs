using System;
using System.Linq.Expressions;
using System.Threading;
using HarmonyLib;
using Keen.VRage.Audio;
using Keen.VRage.Audio.EngineComponents;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.GameSystems.Audio;
using Keen.VRage.Library.Mathematics;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// SE2's FMOD events do no distance attenuation of their own; the game silences a 3D sound only when it moves out of
/// its event's max distance (AudioEventInstance3D.SetTransform -> OnInRangeChanged -> Pause). IsInRange starts false and
/// is only refreshed by the tracker, so a sound that STARTS out of range never gets that transition and plays at full
/// volume at any distance. In single player every tool, footstep and build sound starts next to the listener; online,
/// another player's welder, grinder or drill hundreds of kilometres away was heard as if held here. Out-of-range
/// sounds now start the way they would have ended up: loops paused (the game resumes them when they come in range),
/// one-shots not played. The distance is measured fresh, as SetTransform does; a reused instance's IsInRange can be stale.
/// </summary>
internal static class SoundRange
{
	public static long Silenced;

	private static Func<AudioEventInstance3D, Vector3D>? _position;
	private static Func<WorldTransform>? _listener;

	internal static void Install(Harmony harmony)
	{
		_position = AccessTools.MethodDelegate<Func<AudioEventInstance3D, Vector3D>>(AccessTools.Method(typeof(AudioEventInstance3D), "GetWorldPosition"));
		Type manager = AccessTools.TypeByName("Keen.VRage.Audio.AudioManager");
		_listener = Expression.Lambda<Func<WorldTransform>>(Expression.Property(Expression.Property(null, AccessTools.Property(manager.BaseType, "Instance")), AccessTools.Property(manager, "ListenerTransform"))).Compile();
		harmony.Patch(AccessTools.Method(typeof(AudioEventLoopingInstance3D), nameof(AudioEventLoopingInstance3D.Play)), postfix: new HarmonyMethod(typeof(SoundRange), nameof(LoopPostfix)));
		harmony.Patch(AccessTools.Method(typeof(AudioEventInstance), nameof(AudioEventInstance.Play)), prefix: new HarmonyMethod(typeof(SoundRange), nameof(OneShotPrefix)));
		harmony.Patch(AccessTools.Method(typeof(AudioEngineComponent), nameof(AudioEngineComponent.Play), new[] { typeof(AudioEvent), typeof(WorldTransform), typeof(float) }), prefix: new HarmonyMethod(typeof(SoundRange), nameof(PlayAtPrefix)));
		harmony.Patch(AccessTools.Method(typeof(AudioEventLoopingInstance3D), nameof(AudioEventLoopingInstance3D.Stop)), prefix: new HarmonyMethod(typeof(SoundRange), nameof(StopPrefix)));
	}

	private static readonly AccessTools.FieldRef<AudioEventInstance, FMOD.Studio.EventInstance> Fmod = AccessTools.FieldRefAccess<AudioEventInstance, FMOD.Studio.EventInstance>("_fmodInstance");

	// A paused FMOD event cannot finish a fade-out, and release() only frees stopped events: loops paused here and then
	// ended with fadeOut (Clear always does) stayed in FMOD as STOPPING + paused, owned by nothing (one per tool
	// switched at range on the rig). Out of range is silent anyway, so stop it at once.
	private static void StopPrefix(AudioEventLoopingInstance3D __instance, ref bool fadeOut)
	{
		if (fadeOut && !__instance.IsInvalid && Fmod(__instance).getPaused(out bool paused) == FMOD.RESULT.OK && paused) fadeOut = false;
	}

	private static bool Online => ClientWorld.Current != null || HostWorld.Current != null;

	private static bool OutOfRange(AudioEventInstance3D sound)
	{
		if (!sound.IsAttached || !Online) return false;
		double max = sound.MaxDistance;
		// No max distance (0) means the event is not range-limited; a zero position means it has not been placed yet.
		if (!(max > 0)) return false;
		Vector3D at = _position!(sound);
		if (at == Vector3D.Zero) return false;
		double distance = (at - _listener!().Position).Length();
		if (distance < max) return false;
		Note(sound.ToString(), distance, max);
		return true;
	}

	private static int _noted;

	// The first few silenced sounds, so a wrongly silenced one shows up in the log with its numbers.
	private static void Note(string what, double distance, double max)
	{
		if (Interlocked.Increment(ref _noted) <= 20)
			GameLog.Instance.Info(FormattableString.Invariant($"sound range: silenced {what} at {distance:0} m (max {max:0} m)"));
	}

	private static void LoopPostfix(AudioEventLoopingInstance3D __instance)
	{
		if (!OutOfRange(__instance)) return;
		__instance.Pause();
		Interlocked.Increment(ref Silenced);
	}

	private static bool OneShotPrefix(AudioEventInstance __instance)
	{
		if (__instance is not AudioEventOneShotInstance3D oneShot || !OutOfRange(oneShot)) return true;
		Interlocked.Increment(ref Silenced);
		return false;
	}

	// ISpatialAudioPlayer.Play(event, transform): a one-shot at a point, started before the game ever positions it.
	private static bool PlayAtPrefix(AudioEngineComponent __instance, AudioEvent audioEvent, WorldTransform transform)
	{
		if (!Online || __instance.GetEventDescription(audioEvent) is not { } description || description.getMinMaxDistance(out _, out float max) != FMOD.RESULT.OK) return true;
		if (!(max > 0) || transform.Position == Vector3D.Zero) return true;
		double distance = __instance.GetDistanceToListener(transform);
		if (distance < max) return true;
		Note(audioEvent.ToString(), distance, max);
		Interlocked.Increment(ref Silenced);
		return false;
	}

	public static string Describe() => $"out-of-range sounds silenced {Interlocked.Read(in Silenced)}";
}
