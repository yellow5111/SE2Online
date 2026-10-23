#if !PULSAR
using System;
using System.Linq;
using HarmonyLib;
using Keen.Game2.Simulation.GameSystems.BlockPlacement;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>
/// Test rig (-y51online:selftest-pastetwice, player): every paste or block placement request this game sends goes to
/// the server a second time at once, as a modified client stacking pastes would. A server must refuse the second copy of
/// a multi-block paste from a non-admin player ("paste guard: refused ...") and keep every single-block placement.
/// </summary>
internal static class PasteTwiceProbe
{
	[ThreadStatic] private static bool _again;

	public static void Install(Harmony harmony)
	{
		if (!MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "selftest-pastetwice")) return;
		var place = AccessTools.Method(typeof(BlockPlacerNetworkStories), nameof(BlockPlacerNetworkStories.PlaceStandalone));
		if (place == null) throw new MissingMethodException("BlockPlacerNetworkStories.PlaceStandalone");
		harmony.Patch(place, postfix: new HarmonyMethod(typeof(PasteTwiceProbe), nameof(PlacePostfix)));
		GameLog.Instance.Info("selftest: paste twice: every placement request goes to the server twice");
	}

	private static void PlacePostfix(System.Reflection.MethodBase __originalMethod, object[] __args)
	{
		if (_again) return;
		_again = true;
		try
		{
			__originalMethod.Invoke(null, __args);
			GameLog.Instance.Info("selftest: paste twice: sent the same placement request a second time");
		}
		catch (Exception ex) { GameLog.Instance.Warn("selftest: paste twice: the second request failed to send: " + (ex.InnerException ?? ex).Message); }
		finally { _again = false; }
	}
}
#endif
