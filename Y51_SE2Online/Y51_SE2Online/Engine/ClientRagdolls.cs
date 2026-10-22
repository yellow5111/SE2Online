using System.Linq;
using HarmonyLib;
using Keen.Game2.Client.WorldObjects.Character;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.Engine;

/// <summary>
/// A death ragdoll on a joined game used to drive the body's transform from its own local simulation, so every corpse came
/// to rest wherever this game's physics put it, metres from where the server has it (and where it is looted, dragged or
/// saved). On a network client the server's replicated transform keeps driving the body instead and the ragdoll follows it:
/// the limbs still fall and flop locally, the body lies where the server says.
/// </summary>
internal static class ClientRagdolls
{
	public static long Followed;

	public static void Install(Harmony harmony)
		=> harmony.Patch(AccessTools.Method(typeof(CharacterRagdollComponent), nameof(CharacterRagdollComponent.EnableDeathRagdoll)), postfix: new HarmonyMethod(typeof(ClientRagdolls), nameof(EnablePostfix)));

	private static void EnablePostfix(CharacterRagdollComponent __instance)
	{
		if (MpPlugin.App is not { ServerSession: null, ClientSession: not null } || MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "local-ragdolls")) return;
		__instance.DriveWorldTransform = false;
		if (++Followed == 1) GameLog.Instance.Info("ragdolls: death ragdolls on this joined game follow the server's body position");
	}
}
