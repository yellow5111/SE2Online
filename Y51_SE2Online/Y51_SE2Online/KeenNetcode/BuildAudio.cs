using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Keen.Game2.Client.GameSystems.BlockPlacement;
using Keen.Game2.Simulation.GameSystems.BlockPlacement;
using Keen.VRage.Audio;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.Core.Game.GameSystems.Audio;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Multiplayer.Replications.FailureStrategies;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;
using Y51_SE2Online.SyncEngine;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>
/// SE2 4.0 plays every player's block place/remove sound as a 2D event on every client, so everyone heard everyone's
/// building at full volume from anywhere (2.4's AudioGate fixed this). The block signal that follows the audio signal
/// says where it happened: the sound waits for it and plays with distance falloff, or not at all beyond range. This
/// player's own builds (it sent a placement or deletion story a moment ago) keep full volume.
/// </summary>
internal static class BuildAudio
{
	private static (IAudioManager Audio, AudioEvent Event, bool Own)? _pending;

	public static long Muted;

	public static void Install(Harmony harmony)
	{
		var placement = typeof(BlockPlacementClientSessionComponent);
		foreach (string name in new[] { "OnBlocksAddedAudio", "OnBlocksRemovedAudio" })
			harmony.Patch(AccessTools.Method(placement, name), prefix: new HarmonyMethod(typeof(BuildAudio), nameof(AudioPrefix)));
		foreach (string name in new[] { "OnBlockAdded", "OnBlockRemoved", "OnMultiBlocksAdded", "OnMultiBlocksAddedAABB" })
			harmony.Patch(AccessTools.Method(placement, name), postfix: new HarmonyMethod(typeof(BuildAudio), nameof(BlockPostfix)));
		if (AccessTools.TypeByName("Keen.VRage.Audio.AudioManager") is { } manager && AccessTools.Method(manager, "Update") is { } update)
			harmony.Patch(update, prefix: new HarmonyMethod(typeof(BuildAudio), nameof(Flush)));
		// The local player's own placement and deletion requests (the stories start on the client that builds).
		foreach (var story in AccessTools.GetDeclaredMethods(typeof(BlockPlacerNetworkStories))
			.Where(m => m.IsStatic && m.IsPublic && (m.Name.StartsWith("Place", StringComparison.Ordinal) || m.Name.StartsWith("Delete", StringComparison.Ordinal) || m.Name == "TryDelete")))
			harmony.Patch(story, prefix: new HarmonyMethod(typeof(BuildAudio), nameof(OwnBuildPrefix)));
	}

	private static void OwnBuildPrefix()
	{
		if (AdminAccess.InSession && MpPlugin.App?.ClientSession != null) AudioRouting.Instance.NoteOwnBuild(MpClock.NowMs());
	}

	private static bool AudioPrefix(BlockPlacementClientSessionComponent __instance, MethodBase __originalMethod)
	{
		if (!AdminAccess.InSession) return true;
		Flush();
		var config = __instance._effectsConfig;
		bool creative = __instance._gameMode.IsCreative(__instance._players.LocalPlayerIdentity);
		AudioEvent? audioEvent = __originalMethod.Name == "OnBlocksAddedAudio"
			? creative ? config.OnPlaceBlockCreativeAudioEvent : config.OnPlaceBlockSurvivalAudioEvent
			: creative ? config.OnDeleteBlockCreativeAudioEvent : config.OnDeleteBlockSurvivalAudioEvent;
		if (audioEvent == null) return false;
		_pending = (__instance._audio, audioEvent.Value, AudioRouting.Instance.TakeOwnBuild(MpClock.NowMs()));
		return false;
	}

	private static void BlockPostfix(object[] __args)
	{
		if (_pending is not { } pending || __args.Length == 0 || __args[0] is not NullOnReplicationFail<Entity> target) return;
		var entity = target.Value;
		if (entity == null || !entity.Scene.IsEntityAlive(entity.DEntity)) return;
		// A group placement reports the placed blocks' bounds (grid-local or world): measured to their nearest point.
		Vector3D listener = SoundRange.ListenerPosition;
		WorldTransform where = entity.Data.GetWorldTransform();
		double distance = __args.Length < 2 ? (where.Position - listener).Length()
			: __args[1] is BoundingBoxD world ? AudioRouting.DistanceToBox(V(listener), V(world.Min), V(world.Max))
			: __args[1] is BoundingBox local ? AudioRouting.DistanceToBox(V(WorldTransform.GetRelativeTransform(new WorldTransform(listener), where).Position), V(local.Min), V(local.Max))
			: (where.Position - listener).Length();
		_pending = null;
		Play(pending, distance);
	}

	// A removed block is often gone before its signal: no position, so only this player's own removal plays.
	private static void Flush()
	{
		if (_pending is not { } pending) return;
		_pending = null;
		Play(pending, -1);
	}

	private static void Play((IAudioManager Audio, AudioEvent Event, bool Own) pending, double distance)
	{
		float gain = AudioRouting.BuildSoundGain(pending.Own, distance);
		if (gain > 0) pending.Audio.Play(pending.Event, gain);
		else Muted++;
	}

	private static Vec3D V(Vector3D v) => new(v.X, v.Y, v.Z);

	private static Vec3D V(Vector3 v) => new(v.X, v.Y, v.Z);
}
