using System.Linq;
using HarmonyLib;
using Keen.Game2.Simulation.WorldObjects.Characters;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.Pilotable;
using Keen.VRage.Animation.Client.GameObjects;
using Keen.VRage.DCS.Components;
using Keen.VRage.Physics.Data;
using Y51_SE2Online.Core;
using Y51_SE2Online.SyncEngine;

namespace Y51_SE2Online.KeenNetcode;

internal static class SeatedAvatarPose
{
	private static int _logged;
	private static readonly bool OwnRagdollPose = MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "ownragdollpose:on");
	internal static long Skipped;

	public static void Install(Harmony harmony)
	{
		harmony.Patch(AccessTools.Method(typeof(CharacterThirdPersonAnimatorStrategyComponent), "OnRagdollPosePublished"),
			prefix: new HarmonyMethod(typeof(SeatedAvatarPose), nameof(PublishPrefix)));
	}

	private static bool PublishPrefix(CharacterThirdPersonAnimatorStrategyComponent __instance)
	{
		var session = MpPlugin.App?.ClientSession;
		if (!MpPlugin.Online || session == null) return true;
		var character = __instance.Entity;
		if (character.Scene != session.Scene || character.TryGet<CharacterLifeComponent>() is not { IsAlive: true }
			|| EngineCodec.IsDead(character)) return true;
		long identity = OwnMotion.LocalIdentity(session);
		if (identity == 0 || TeamPermissions.OwnerOf(character) != identity) return true;
		// The ragdoll bones trail this player's predicted root by a physics tick and only some frames publish a pose, so
		// the body flicked back and forth between trailing and in place at any speed (walking too).
		// -y51online:ownragdollpose:on puts the ragdoll pose back.
		if (OwnRagdollPose) return true;
		Skipped++;
		if (System.Threading.Interlocked.Exchange(ref _logged, 1) == 0)
			GameLog.Instance.Info("avatar: keeping this player's animation pose instead of the ragdoll physics pose");
		return false;
	}
}
