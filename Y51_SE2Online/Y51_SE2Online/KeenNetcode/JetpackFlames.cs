using System.Linq;
using HarmonyLib;
using Keen.Game2.Client.WorldObjects.Character.Effects;
using Keen.Game2.Simulation.WorldObjects.Characters;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>
/// CharacterMovementEffectsComponent.CharacterStateChanged switches the jetpack flames on for a Jetpack state and then off
/// when the previous state was Jetpack, so the same state arriving twice leaves a flying character without flames (and
/// starts its idle sound again). In multiplayer it does arrive twice: the server's echo of a toggle this game already
/// predicted, and the current value of every sync property a player gets once an entity reaches it (a character that
/// streamed in while flying). A repeated Jetpack state changes nothing now. -y51online:jetflames:off leaves it to the game.
/// </summary>
internal static class JetpackFlames
{
	internal static readonly bool Off = MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "jetflames:off");

	public static void Install(Harmony harmony)
	{
		InstallEchoLog(harmony);
		if (Off) return;
		harmony.Patch(AccessTools.Method(typeof(CharacterMovementEffectsComponent), "CharacterStateChanged")
			?? throw new System.MissingMethodException("CharacterMovementEffectsComponent.CharacterStateChanged"),
			prefix: new HarmonyMethod(typeof(JetpackFlames), nameof(Prefix)));
	}

	private static bool Prefix(CharacterMovementEffectsComponent __instance, CharacterMovementState state)
		=> !(state == CharacterMovementState.Jetpack && __instance._previousState == CharacterMovementState.Jetpack && MpPlugin.Online);

	private static long _echoLoggedMs;

	// Diagnostic only ("jetpack won't turn on"): the server's movement state applied over a different one this game had for
	// its own character, which is what turns a jetpack the player just switched on back off.
	private static void InstallEchoLog(Harmony harmony)
	{
		var callbacks = AccessTools.TypeByName("Game2.Simulation.Replication.CB.InProcessReplicationReaderCallbacks");
		var apply = callbacks == null ? null : AccessTools.Method(callbacks, "Keen_Game2_Simulation_WorldObjects_Characters_CharacterMovementComponent_CurrentMovementStateProperty_Apply");
		if (apply == null) { GameLog.Instance.Warn("jetpack: the movement state reader was not found; server corrections of it are not logged"); return; }
		harmony.Patch(apply, prefix: new HarmonyMethod(typeof(JetpackFlames), nameof(ApplyPrefix)));
	}

	private static void ApplyPrefix(Keen.VRage.DCS.Components.Entity entity, in CharacterMovementState value)
	{
		try
		{
			if (!MpPlugin.Online || !ReferenceEquals(entity, OwnMotion.OwnEntity) || !ReferenceEquals(entity.Scene, MpPlugin.App?.ClientSession?.Scene)) return;
			if (entity.TryGet<CharacterMovementComponent>() is not { } movement || movement.CurrentMovementState == value) return;
			long now = MpClock.NowMs();
			if (now - _echoLoggedMs < 5000) return;
			_echoLoggedMs = now;
			GameLog.Instance.Info($"jetpack: the server's movement state {value} replaced this game's {movement.CurrentMovementState} on its own character (input {OwnMotion.Seq}, prediction {(OwnMotion.Driving != null ? "on" : "off")}, toggle prediction {(OwnMotion.TogglePredicting != null ? "on" : "off")})");
		}
		catch (System.Exception) { }
	}
}
