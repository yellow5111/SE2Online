#nullable enable
using System.Runtime.CompilerServices;
using HarmonyLib;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Data;
using Keen.Game2.Simulation.WorldObjects.Characters;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.Pilotable;
using Keen.Game2.Simulation.WorldObjects.Shared;
using Keen.VRage.DCS.Components;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// See <see cref="SeatRules"/>. CanSeat is what the host's SetPilotStory (a client's sit request), the client's own
/// TrySetPilotAsync and the plugin's seat code all ask, so the refusal sits there. The seat charger is also kept
/// consistent should a second seat still happen: the old sink is closed before a new one opens, and a charger left
/// without a seat skips its tick instead of throwing (the NullReferenceException that ended the dedicated server).
/// </summary>
internal static class SeatGuard
{
	internal static long Refused;
	private static readonly System.Func<Keen.VRage.Core.Game.Components.GameComponent, Keen.VRage.Core.Game.Systems.Session> SessionOf =
		AccessTools.MethodDelegate<System.Func<Keen.VRage.Core.Game.Components.GameComponent, Keen.VRage.Core.Game.Systems.Session>>(AccessTools.PropertyGetter(typeof(Keen.VRage.Core.Game.Components.GameComponent), "Session"));

	internal static void Install(Harmony harmony)
	{
		harmony.Patch(AccessTools.Method(typeof(SeatComponent), nameof(SeatComponent.CanSeat)), postfix: new HarmonyMethod(typeof(SeatGuard), nameof(CanSeatPostfix)));
		harmony.Patch(AccessTools.Method(typeof(CharacterChargeStatFromSeatResourceComponent), "ConnectToSeat"), prefix: new HarmonyMethod(typeof(SeatGuard), nameof(ConnectPrefix)));
		harmony.Patch(AccessTools.Method(typeof(CharacterChargeStatFromSeatResourceComponent), "Recharge", new[] { typeof(Keen.VRage.DCS.Accessors.Writable<Keen.Game2.Simulation.WorldObjects.CubeGrids.ResourceDistribution.Resources.ResourceNode>), typeof(Keen.VRage.DCS.Accessors.Optional<Keen.Game2.Simulation.WorldObjects.CubeGrids.ResourceDistribution.Resources.ResourceFlow>) }),
			prefix: new HarmonyMethod(typeof(SeatGuard), nameof(RechargePrefix)));
	}

	private static void CanSeatPostfix(SeatComponent __instance, Entity pilot, ref bool __result)
	{
		if (!__result) return;
		// The host's sit request asks CanSeat too: a seat on a grid the player may not use refuses them there, and the
		// client stands back up (SeatingFailed). Same rule as the grid's terminal: sharing, factions, block lists.
		if (AdminAccess.Hosting && ReferenceEquals(SessionOf(__instance), MpPlugin.App?.ServerSession) && pilot.TryGet<Keen.Game2.Simulation.WorldObjects.Characters.CharacterLifeComponent>() != null
			&& TeamPermissions.OwnerOf(pilot) is long owner && owner != 0 && PlayerIdentities.IsHuman(owner)
			&& !Y51_SE2Online.KeenNetcode.ServerAccess.MayUse(null, new Keen.Game2.Simulation.GameSystems.Ownership.IdentityId(owner), __instance._block.Entity))
		{
			__result = false;
			System.Threading.Interlocked.Increment(ref Y51_SE2Online.KeenNetcode.ServerAccess.Refused);
			GameLog.Instance.Info($"access: refused identity {owner} a seat in {__instance._block.Entity.GetComposition()?.DebugNameShort} (no access)");
			return;
		}
		var seatable = pilot.FirstOrDefault<SeatableComponent>();
		bool unseating = pilot.Data.Has<SeatableComponent.UnseatingComputationData>();
		if (SeatRules.MaySit(seatable?.CurrentSeat != null, unseating)) return;
		__result = false;
		if (System.Threading.Interlocked.Increment(ref Refused) == 1)
			GameLog.Instance.Info($"seats: refused to seat {pilot} again: it is {(seatable?.CurrentSeat != null ? "already in seat " + seatable.CurrentSeat.Entity : "still standing up")} (further ones only counted)");
	}

	private static void ConnectPrefix(CharacterChargeStatFromSeatResourceComponent __instance)
	{
		if (__instance._sink.IsEmpty) return;
		SessionOf(__instance).MarkEntityForClose(__instance._sink);
		__instance._sink = default;
		__instance._seat = null;
	}

	private static bool RechargePrefix(CharacterChargeStatFromSeatResourceComponent __instance) => __instance._seat != null;
}
