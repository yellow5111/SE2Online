#nullable enable
using HarmonyLib;
using Keen.Game2.Simulation.WorldObjects.Characters;
using Keen.Game2.Simulation.WorldObjects.Tools;
using Keen.VRage.Core.Game.Data;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// See <see cref="CreativeTools"/>. IntegrityToolComponent (server only) asks the world's creative flag in three places of a
/// weld: securing the next component, moving components into the block's pool and consuming them. For a free welder's
/// hand tool those answer as in a creative world; grinding and ship welders are unchanged.
/// </summary>
internal static class CreativeWeld
{
	internal static long FreeWelds;

	internal static void Install(Harmony harmony)
	{
		harmony.Patch(AccessTools.Method(typeof(IntegrityToolComponent), "SecureItemForWeld"), prefix: new HarmonyMethod(typeof(CreativeWeld), nameof(SecurePrefix)));
		harmony.Patch(AccessTools.Method(typeof(IntegrityToolComponent), "TransferEligibleItemsToPool"), prefix: new HarmonyMethod(typeof(CreativeWeld), nameof(SkipPrefix)));
		harmony.Patch(AccessTools.Method(typeof(IntegrityToolComponent), "ConsumeItems"), prefix: new HarmonyMethod(typeof(CreativeWeld), nameof(SkipPrefix)));
	}

	private static bool Free(IntegrityToolComponent tool)
	{
		try
		{
			if (!AdminAccess.Hosting || HostWorld.Current is not { } world) return false;
			// A hand tool hangs under the character that holds it; a ship welder's top parent is its grid.
			var holder = tool.Entity.GetTopLevelParent();
			long owner = (long)tool._owner.Owner;
			bool hand = holder?.TryGet<CharacterLifeComponent>() != null && TeamPermissions.OwnerOf(holder) == owner;
			return CreativeTools.FreeWeld(true, hand, owner, world.FreeWelders);
		}
		catch (System.Exception) { return false; }
	}

	private static bool SecurePrefix(IntegrityToolComponent __instance, ref (bool Ready, bool Crafting, bool isCraftingBlocked) __result)
	{
		if (!Free(__instance)) return true;
		if (System.Threading.Interlocked.Increment(ref FreeWelds) == 1) GameLog.Instance.Info("admin: a creative admin's hand welder welds without components (first free weld this run)");
		__result = (true, false, false);
		return false;
	}

	private static bool SkipPrefix(IntegrityToolComponent __instance) => !Free(__instance);
}
