using HarmonyLib;
using Keen.Game2.Client.GameSystems.PlayerControl;
using Keen.Game2.Client.WorldObjects.CubeBlocks.Cockpit;
using Keen.Game2.Client.WorldObjects.CubeBlocks.Effects;
using Keen.Game2.Simulation.WorldObjects.Characters;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.DCS.Components;
using Y51_SE2Online.SyncEngine;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>
/// 2.4's AudioGate fixes for sounds SE2 4.0 still plays for the wrong player:
/// - a cockpit starts its interior sound for whoever sits in it, so every player heard every other pilot's cockpit as
///   their own;
/// - a landing gear plays its lock sound as a 2D sound, heard at full volume from anywhere; it now plays at the block.
/// </summary>
internal static class PlayerAudio
{
	public static void Install(Harmony harmony)
	{
		harmony.Patch(AccessTools.Method(typeof(CockpitAudioComponent), "PossessPilot"), prefix: new HarmonyMethod(typeof(PlayerAudio), nameof(CockpitPilotPrefix)));
		harmony.Patch(AccessTools.Method(typeof(CockpitAudioComponent), "UnpossessPilot"), prefix: new HarmonyMethod(typeof(PlayerAudio), nameof(CockpitReadyPrefix)));
		harmony.Patch(AccessTools.Method(typeof(MagneticBlockEffectsComponent), "PlayLockAudio"), prefix: new HarmonyMethod(typeof(PlayerAudio), nameof(MagneticLockPrefix)));
	}

	/// <summary>A character owned by someone other than this game's player (another player or an NPC).</summary>
	private static bool IsOtherCharacter(Entity? character)
	{
		if (character == null || character.TryGet<CharacterLifeComponent>() == null || MpPlugin.App?.ClientSession is not { } session) return false;
		var players = session.SessionComponents.TryGet<ClientPlayersSessionComponent>();
		return players != null && TeamPermissions.OwnerOf(character) != (long)players.LocalPlayerIdentity;
	}

	// A pilot set in the same batch that creates the cockpit arrives before the cockpit is in the scene, where SE2 4.0
	// creates its interior sound: the game's handler then threw and the crash handler closed the game (a co-op host's own
	// game, which applies the server's signals in process, without the network path's guards).
	private static bool CockpitPilotPrefix(CockpitAudioComponent __instance, Entity pilot) => __instance._snapshot != null && (!AdminAccess.InSession || !IsOtherCharacter(pilot));

	private static bool CockpitReadyPrefix(CockpitAudioComponent __instance) => __instance._snapshot != null;

	private static bool MagneticLockPrefix(MagneticBlockEffectsComponent __instance, string state)
	{
		if (!AdminAccess.InSession) return true;
		if (__instance._definition.OnLockStateChangedAudioEvent is not { } audioEvent) return false;
		var sound = __instance._audio.CreateAudioEventInstance3D(audioEvent);
		if (sound.TryGetParameterFromString(__instance._definition.LockStateToggleParameter, out var parameter)) sound.SetParameter(parameter, state);
		sound.UpdatePosition(__instance.Entity.Data.GetWorldTransform().Position);
		sound.Play();
		sound.Dispose();
		return false;
	}
}
