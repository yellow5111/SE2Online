using System;
using System.Linq.Expressions;
using System.Threading;
using HarmonyLib;
using Keen.VRage.Audio;
using Keen.VRage.Audio.EngineComponents;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.GameSystems.Audio;
using Keen.VRage.Library.Mathematics;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// SE2's spatializers fade most events to silence at their max distance, but some carry unspatialized layers
/// (survival kit, hydrogen engine, ion push, reactor explosions are still -10 to -60 dB kilometres away, measured
/// offline on the shipped banks), so the game also pauses a 3D sound when it moves out of its event's max distance
/// (AudioEventInstance3D.SetTransform -> OnInRangeChanged -> Pause). IsInRange starts false and only changes on that
/// transition, so a sound that STARTS out of range never gets it: every machine of a world loading (the listen host
/// measured 161 of its 163 playing loops beyond their max, up to 11 km away), and online other players' tools and
/// explosions start anywhere. Out-of-range sounds now start the way they would have ended up: loops paused (the game
/// resumes them when they come in range), one-shots not played. The distance is measured fresh, as SetTransform does; a reused instance's IsInRange can be stale.
/// Only in SE2 Online sessions: single player keeps the game's own audio.
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
		_listenerValid = Expression.Lambda<Func<bool>>(Expression.Field(Expression.Property(null, AccessTools.Property(manager.BaseType, "Instance")), AccessTools.Field(manager, "_hasValidListenerTransform"))).Compile();
		harmony.Patch(AccessTools.Method(typeof(AudioEventLoopingInstance3D), nameof(AudioEventLoopingInstance3D.Play)), postfix: new HarmonyMethod(typeof(SoundRange), nameof(LoopPostfix)));
		harmony.Patch(AccessTools.Method(typeof(AudioEventInstance), nameof(AudioEventInstance.Play)), prefix: new HarmonyMethod(typeof(SoundRange), nameof(OneShotPrefix)));
		harmony.Patch(AccessTools.Method(typeof(AudioEngineComponent), nameof(AudioEngineComponent.Play), new[] { typeof(AudioEvent), typeof(WorldTransform), typeof(float) }), prefix: new HarmonyMethod(typeof(SoundRange), nameof(PlayAtPrefix)));
		harmony.Patch(AccessTools.Method(typeof(AudioEventLoopingInstance3D), nameof(AudioEventLoopingInstance3D.Stop)), prefix: new HarmonyMethod(typeof(SoundRange), nameof(StopPrefix)));
		harmony.Patch(AccessTools.Method(typeof(AudioEventLoopingInstance3D), nameof(AudioEventLoopingInstance3D.Resume)), prefix: new HarmonyMethod(typeof(SoundRange), nameof(ResumePrefix)));
		harmony.Patch(AccessTools.Method(typeof(AudioEventInstance3D), nameof(AudioEventInstance3D.UpdatePosition)), postfix: new HarmonyMethod(typeof(SoundRange), nameof(PlacedPostfix)));
	}

	internal static Vector3D ListenerPosition => _listener!().Position;

	private static Func<bool>? _listenerValid;

	private static bool ListenerValid => _listenerValid?.Invoke() ?? true;

	private static readonly AccessTools.FieldRef<AudioEventInstance, FMOD.Studio.EventInstance> Fmod = AccessTools.FieldRefAccess<AudioEventInstance, FMOD.Studio.EventInstance>("_fmodInstance");

	// A paused FMOD event cannot finish a fade-out, and release() only frees stopped events: loops paused here and then
	// ended with fadeOut (Clear always does) stayed in FMOD as STOPPING + paused, owned by nothing (one per tool
	// switched at range on the rig). Out of range is silent anyway, so stop it at once.
	private static void StopPrefix(AudioEventLoopingInstance3D __instance, ref bool fadeOut)
	{
		if (fadeOut && MpPlugin.Online && !__instance.IsInvalid && Fmod(__instance).getPaused(out bool paused) == FMOD.RESULT.OK && paused) fadeOut = false;
	}

	private static bool OutOfRange(AudioEventInstance3D sound)
	{
		double distance = Distance(sound), max = Range(sound);
		if (!AudioRouting.OutOfRange(distance, max)) return false;
		Note(sound.Path.Path, distance, max);
		return true;
	}

	// From the listener; 0 when not known yet.
	private static double Distance(AudioEventInstance3D sound)
	{
		// Without a listener SE2 holds every sound back itself; ListenerTransform is then the world origin.
		if (sound.IsInvalid || !ListenerValid) return 0;
		if (sound.IsAttached)
		{
			Vector3D at = _position!(sound);
			return at == Vector3D.Zero ? 0 : (at - _listener!().Position).Length();
		}
		// Explosions, shots, impacts and construction stages are not attached; UpdatePosition leaves their
		// listener-relative position in the FMOD instance (zero until placed).
		if (Fmod(sound).get3DAttributes(out FMOD.ATTRIBUTES_3D a) != FMOD.RESULT.OK) return 0;
		return Math.Sqrt(a.position.x * a.position.x + a.position.y * a.position.y + a.position.z * a.position.z);
	}

	// MaxDistance is the event's capped at AudioConfiguration.MaximumUpdateDistance (500 m): the distance at which
	// the game pauses a loop. Nothing cuts a one-shot there; explosions are mixed to be heard out to 2 km.
	private static double Range(AudioEventInstance3D sound) =>
		sound is AudioEventOneShotInstance3D && Fmod(sound).getDescription(out FMOD.Studio.EventDescription description) == FMOD.RESULT.OK
			&& description.getMinMaxDistance(out _, out float eventMax) == FMOD.RESULT.OK ? eventMax : sound.MaxDistance;

	private static int _noted;

	// The first few silenced sounds, so a wrongly silenced one shows up in the log with its numbers.
	private static void Note(string what, double distance, double max)
	{
		if (Interlocked.Increment(ref _noted) <= 20)
			GameLog.Instance.Info(FormattableString.Invariant($"sound range: silenced {what} at {distance:0} m (max {max:0} m)"));
	}

	private static void LoopPostfix(AudioEventLoopingInstance3D __instance)
	{
		if (!MpPlugin.Online || !OutOfRange(__instance)) return;
		__instance.Pause();
		Interlocked.Increment(ref Silenced);
	}

	// TickManagementComponent.PauseClient (world load, in-game menu, leaving) calls Set3DScenePaused(false), which
	// resumes every borrowed sound, including loops the game paused for being out of range; nothing pauses them again
	// until they cross the range edge twice, so a base's machines kept playing from anywhere.
	// Only loops the game itself holds out of range: one it still counts in range would never get the transition
	// that resumes it.
	private static bool ResumePrefix(AudioEventLoopingInstance3D __instance) => !MpPlugin.Online || __instance.IsInRange || !OutOfRange(__instance);

	// Explosions call Play before UpdatePosition, so the start-time check could not know where they are.
	private static void PlacedPostfix(AudioEventInstance3D __instance)
	{
		if (!MpPlugin.Online || __instance.IsAttached || __instance.IsInRange || !OutOfRange(__instance)) return;
		if (__instance is AudioEventLoopingInstance3D loop) loop.Pause();
		else Fmod(__instance).stop(FMOD.Studio.STOP_MODE.IMMEDIATE);
		Interlocked.Increment(ref Silenced);
	}

	private static bool OneShotPrefix(AudioEventInstance __instance)
	{
		if (__instance is not AudioEventOneShotInstance3D oneShot || !MpPlugin.Online) return true;
		if (OutOfRange(oneShot))
		{
			Interlocked.Increment(ref Silenced);
			return false;
		}
		// Construction and deconstruction stage sounds (Progress_Construction/Deconstruction) are 2D events played on
		// the block: FMOD ignores their position, so every client heard another player's welding at full volume out to
		// the event's range. They now fade with distance like a 3D event, full volume within a hand tool's reach.
		var fmod = Fmod(oneShot);
		if (fmod.getDescription(out FMOD.Studio.EventDescription description) == FMOD.RESULT.OK && description.is3D(out bool is3D) == FMOD.RESULT.OK && !is3D
			&& Distance(oneShot) is var distance and > 0 && fmod.getVolume(out float volume) == FMOD.RESULT.OK)
			fmod.setVolume(volume * AudioRouting.FlatSoundGain(distance, Range(oneShot)));
		return true;
	}

	// ISpatialAudioPlayer.Play(event, transform): a one-shot at a point, started before the game ever positions it.
	private static bool PlayAtPrefix(AudioEngineComponent __instance, AudioEvent audioEvent, WorldTransform transform)
	{
		if (!MpPlugin.Online || __instance.GetEventDescription(audioEvent) is not { } description || description.getMinMaxDistance(out _, out float max) != FMOD.RESULT.OK) return true;
		if (transform.Position == Vector3D.Zero) return true;
		double distance = __instance.GetDistanceToListener(transform);
		if (!AudioRouting.OutOfRange(distance, max)) return true;
		Note(audioEvent.ToString(), distance, max);
		Interlocked.Increment(ref Silenced);
		return false;
	}

	public static string Describe() => $"out-of-range sounds silenced {Interlocked.Read(in Silenced)}";
}
