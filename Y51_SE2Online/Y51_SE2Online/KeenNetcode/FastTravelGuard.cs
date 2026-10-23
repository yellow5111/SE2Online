using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Keen.Game2.Simulation.GameSystems.FastTravel;
using Keen.VRage.Multiplayer;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>
/// SE2 4.0 keeps one fast travel operation per player until it completes, and every later request from that player gets
/// the same operation back. The player's game loops the travel video until it sees the operation complete. A travel whose
/// server part faults (PerformFastTravel only SkipWaits it) or never finishes (the destination's spawn waits for every
/// player to build it) stays "Confirmed" forever: the video loops and the player cannot travel again until it rejoins
/// (F22). Such an operation is cancelled here, which ends the video and lets the player travel again.
/// </summary>
internal static class FastTravelGuard
{
	private const long StuckMs = 180_000;

	private static readonly AccessTools.FieldRef<object, object> Operations = AccessTools.FieldRefAccess<object>(typeof(FastTravelServerSessionComponent), "_operations");

	// Each operation's own server part: a player's next travel is a new operation, never judged by the last one's task.
	private static readonly ConditionalWeakTable<FastTravelOperation, StrongBox<Keen.VRage.Library.Threading.Task>> Running = new();

	private static readonly ConditionalWeakTable<FastTravelOperation, StrongBox<long>> ConfirmedAt = new();

	public static void Install(Harmony harmony)
	{
		var type = typeof(FastTravelServerSessionComponent);
		harmony.Patch(AccessTools.Method(type, "PerformFastTravel"), postfix: new HarmonyMethod(typeof(FastTravelGuard), nameof(PerformPostfix)));
		harmony.Patch(AccessTools.Method(type, "CheckPendingOperations"), postfix: new HarmonyMethod(typeof(FastTravelGuard), nameof(CheckPostfix)));
		harmony.Patch(AccessTools.Method(type, "CollectEntities"), prefix: new HarmonyMethod(typeof(FastTravelGuard), nameof(CollectPrefix)));
	}

	private static void PerformPostfix(object __instance, ClientId client, Keen.VRage.Library.Threading.Task __result)
	{
		if (Operations(__instance) is IDictionary operations && operations.Contains(client) && operations[client] is FastTravelOperation op)
			Running.AddOrUpdate(op, new StrongBox<Keen.VRage.Library.Threading.Task>(__result));
	}

	// Server session thread, every tick (where the game itself re-validates waiting operations).
	private static void CheckPostfix(object __instance)
	{
		if (Operations(__instance) is not IDictionary operations || operations.Count == 0) return;
		long now = MpClock.NowMs();
		foreach (DictionaryEntry entry in operations)
		{
			if (entry.Key is not ClientId client || entry.Value is not FastTravelOperation op) continue;
			if (op.IsCompleted || op.IsCountingDown || op.Result == FastTravelResult.Validated)
			{
				ConfirmedAt.Remove(op);
				continue;
			}
			long since = ConfirmedAt.GetValue(op, _ => new StrongBox<long>(now)).Value;
			bool ended = Running.TryGetValue(op, out var task) && task.Value.IsCompleted;
			if (!ended && now - since < StuckMs) continue;
			GameLog.Instance.Warn(ended
				? $"fast travel: ClientId {client.Id}'s travel failed on the server while {op.Result}; cancelled so the player's travel screen closes and it can travel again"
				: $"fast travel: ClientId {client.Id}'s travel has been {op.Result} for {(now - since) / 1000} s (the destination never finished loading); cancelled so the player can travel again");
			op.Result = FastTravelResult.Cancelled;
			ConfirmedAt.Remove(op);
			Running.Remove(op);
		}
	}

	// A travel cancelled here can still get its destination afterwards: it must not teleport anyone then.
	private static bool CollectPrefix(object operation, ref bool __result)
	{
		if (operation is not FastTravelOperation op || op.Result == FastTravelResult.Confirmed) return true;
		__result = false;
		return false;
	}
}
