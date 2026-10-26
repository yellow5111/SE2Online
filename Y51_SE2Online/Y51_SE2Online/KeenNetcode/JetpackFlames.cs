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

	// -y51online:jetecho:off applies the server's movement state over this game's prediction as before.
	private static readonly bool EchoOff = MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "jetecho:off");

	/// <summary>Server values of the jetpack switch this game kept its own prediction over.</summary>
	internal static long EchoesKept;

	// The server's movement state for this game's own character. While this game predicts its toggles, a jetpack on/off
	// value from the server is left out (Sync.OwnState.KeepsOwnMovement): it was sent before the server ran the input
	// that toggled it here, and it switched a jetpack just turned on in gravity back off. The acks carry the server's
	// state; a refusal or a change the server made itself comes back flagged there and is taken at once.
	private static void InstallEchoLog(Harmony harmony)
	{
		var callbacks = AccessTools.TypeByName("Game2.Simulation.Replication.CB.InProcessReplicationReaderCallbacks");
		var apply = callbacks == null ? null : AccessTools.Method(callbacks, "Keen_Game2_Simulation_WorldObjects_Characters_CharacterMovementComponent_CurrentMovementStateProperty_Apply");
		if (apply == null) { GameLog.Instance.Warn("jetpack: the movement state reader was not found; server corrections of it are not logged"); return; }
		harmony.Patch(apply, prefix: new HarmonyMethod(typeof(JetpackFlames), nameof(ApplyPrefix)));
	}

	private static bool ApplyPrefix(Keen.VRage.DCS.Components.Entity entity, in CharacterMovementState value)
	{
		try
		{
			if (!MpPlugin.Online || !ReferenceEquals(entity, OwnMotion.OwnEntity) || !ReferenceEquals(entity.Scene, MpPlugin.App?.ClientSession?.Scene)) return true;
			if (entity.TryGet<CharacterMovementComponent>() is not { } movement || movement.CurrentMovementState == value) return true;
			bool predicting = ReferenceEquals(entity, OwnMotion.TogglePredicting);
			bool alive = !SyncEngine.EngineCodec.IsDead(entity) && entity.TryGet<CharacterLifeComponent>()?.IsAlive == true;
			bool keep = !EchoOff && Sync.OwnState.KeepsOwnMovement(predicting, alive, (int)movement.CurrentMovementState, (int)value);
			if (keep) EchoesKept++;
			long now = MpClock.NowMs();
			if (now - _echoLoggedMs >= 5000)
			{
				_echoLoggedMs = now;
				GameLog.Instance.Info($"jetpack: the server's movement state {value} {(keep ? "left out over" : "replaced")} this game's {movement.CurrentMovementState} on its own character (input {OwnMotion.Seq}, prediction {(OwnMotion.Driving != null ? "on" : "off")}, toggle prediction {(predicting ? "on" : "off")}{(keep ? "; the acks carry the server's state" : "")})");
			}
			return !keep;
		}
		catch (System.Exception) { return true; }
	}
}
