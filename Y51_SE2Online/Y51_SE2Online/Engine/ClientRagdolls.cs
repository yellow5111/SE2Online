using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using Keen.Game2.Client.WorldObjects.Character;
using Keen.VRage.DCS.Components;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.Engine;

/// <summary>
/// A death ragdoll on a joined game simulates the corpse here, as SE2 does (<see cref="CorpseRules"/>). The body's playback
/// goes with it and SE2 Online's corpse samples from the server (<see cref="CorpseMotion"/>) are not applied to it: the
/// playback wrote the last sample over the body every frame, and on this player's own (predicted, game-simulated) body
/// that pinned the character controller, and with it the corpse's position, to where the server had it a round trip
/// before the death. A body that dies before this game has it (a late player) has no ragdoll here and still follows those
/// samples. 0.16.4 to 0.18.1 cleared RagdollDrivesWorldTransform so the server's transform kept driving the body; Havok
/// then pulls the ragdoll to that transform every tick and never reads its pose back, so corpses stood frozen in their
/// last living pose. -y51online:ragdollfix:off goes back to that.
/// </summary>
internal static class ClientRagdolls
{
	public static long Followed, Simulated, SamplesIgnored;

	private static readonly bool KillSwitch = MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + CorpseRules.KillSwitch);
	private static readonly bool LocalArg = MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "local-ragdolls");
	private static volatile bool _installed;

	public static void Install(Harmony harmony)
	{
		// Fails here (and the transport never asks) if this game build has no such data.
		_ = typeof(Keen.VRage.Physics.Data.RagdollDrivesWorldTransform);
		harmony.Patch(AccessTools.Method(typeof(CharacterRagdollComponent), nameof(CharacterRagdollComponent.EnableDeathRagdoll)), postfix: new HarmonyMethod(typeof(ClientRagdolls), nameof(EnablePostfix)));
		_installed = true;
	}

	private static void EnablePostfix(CharacterRagdollComponent __instance)
	{
		if (MpPlugin.App is not { ServerSession: null, ClientSession: not null }) return;
		if (CorpseRules.SimulateHere(KillSwitch, LocalArg))
		{
			__instance.Entity.Data.TryRemove<Keen.VRage.Game.Multiplayer.MotionHistory>();
			if (Interlocked.Increment(ref Simulated) == 1) GameLog.Instance.Info("ragdolls: death ragdolls on this joined game are simulated here, as the game does");
			return;
		}
		__instance.DriveWorldTransform = false;
		if (Interlocked.Increment(ref Followed) == 1) GameLog.Instance.Info("ragdolls: death ragdolls on this joined game follow the server's body position (ragdollfix:off)");
	}

	/// <summary>Client transport: a motion sample for this body is dropped (its death ragdoll drives it here).</summary>
	internal static bool IgnoresSample(bool server, Entity entity)
	{
		if (server || !_installed || !CorpseRules.IgnoreSample(server, RagdollDrives(entity))) return false;
		if (Interlocked.Increment(ref SamplesIgnored) == 1) GameLog.Instance.Info("ragdolls: the server's corpse samples are left out for bodies whose ragdoll this game simulates");
		return true;
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	private static bool RagdollDrives(Entity entity) => entity.Data.Has<Keen.VRage.Physics.Data.RagdollDrivesWorldTransform>();
}
