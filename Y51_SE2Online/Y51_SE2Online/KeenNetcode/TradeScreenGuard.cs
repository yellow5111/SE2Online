using System;
using System.Linq;
using HarmonyLib;
using Keen.Game2.Client.UI.TerminalScreen.Trade;
using Keen.Game2.Simulation.GameSystems.Economy;
using Keen.Game2.Simulation.WorldObjects.Items;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Utils;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;
using KTask = Keen.VRage.Library.Threading.Task;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>
/// F71 "no items at the trade station": the trade screen fills the station's list only after it has asked the server the
/// price of every item stack in every inventory listed (the player's and every container of the station, one request
/// each) and waits for all of them together (TradeScreenViewModel.GetAvailableItems). One request that ends skipped or
/// failed, which the game's own replication never does and SE2 Online's does for a request naming something one side
/// lacks, left the station's list empty and nothing marked. Each price now settles on its own: a failed one only leaves
/// that item untradable. -y51online:tradeguard:off restores the game's own wait.
/// </summary>
internal static class TradeScreenGuard
{
	public static long PricesFailed;

	public static void Install(Harmony harmony)
	{
		if (MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "tradeguard:off"))
		{
			GameLog.Instance.Info("trade: the trade screen waits for every price together (tradeguard:off)");
			return;
		}
		harmony.Patch(AccessTools.Method(typeof(TradeScreenViewModel), "WaitOnPrice") ?? throw new MissingMethodException("TradeScreenViewModel.WaitOnPrice"),
			prefix: new HarmonyMethod(typeof(TradeScreenGuard), nameof(WaitOnPricePrefix)));
	}

	private static bool WaitOnPricePrefix(TradeScreenViewModel __instance, Entity entity, ItemDefinition item, ref Keen.VRage.Library.Threading.Task<ItemDefinition?> __result)
	{
		if (!MpPlugin.Online) return true;
		__result = Price(__instance, entity, item);
		return false;
	}

	private static async Keen.VRage.Library.Threading.Task<ItemDefinition?> Price(TradeScreenViewModel screen, Entity entity, ItemDefinition item)
	{
		// Static, so no lifetime to take from `this` (the game throws at the first real wait): the screen's, cancelled when it closes.
		await KTask.SetLifetime(screen);
		float? price = null;
		try { price = await TraderComponent.GetItemPrice(entity, item).Task; }
		catch (Exception ex) when (ex.CanHandle() && ex is not System.Threading.Tasks.TaskCanceledException)
		{
			if (System.Threading.Interlocked.Increment(ref PricesFailed) == 1)
				GameLog.Instance.Warn($"trade: the price of {item?.ToString() ?? "an item"} did not come back ({ex.GetType().Name}: {ex.Message.Split('\n')[0]}); that item is shown as not tradable, the rest of the station's list still shows (further ones not logged)");
		}
		return StoryLoss.Tradable(price) ? item : null;
	}
}
