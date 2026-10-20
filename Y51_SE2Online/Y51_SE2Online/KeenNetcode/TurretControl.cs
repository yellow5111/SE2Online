using HarmonyLib;
using Keen.Game2.Simulation.StreamedUI.Terminal.ControlPanel.BlockDetails;
using Y51_SE2Online.Core;
using Y51_SE2Online.SyncEngine;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>
/// SE2 4.0 tells every client "this turret is now under direct control" (a broadcast signal), and each client that has the
/// turret then possesses it: whoever took a turret pulled every nearby player into it. A turret's control panel model is
/// sent to one player only, so a client now takes the turret only when its own panel asked for control a moment ago.
/// </summary>
internal static class TurretControl
{
	private const long RequestWindowMs = 5000;

	private static long _requestedAtMs;

	public static long Ignored;

	public static void Install(Harmony harmony)
	{
		harmony.Patch(AccessTools.PropertySetter(typeof(AutomatedTurretBlockDetailModel), nameof(AutomatedTurretBlockDetailModel.IsControlling)),
			postfix: new HarmonyMethod(typeof(TurretControl), nameof(PanelSetPostfix)));
		var client = AccessTools.TypeByName("Keen.Game2.Client.WorldObjects.CubeBlocks.Weapons.AutoTargetingClientComponent");
		harmony.Patch(AccessTools.Method(client, "SetControl"), prefix: new HarmonyMethod(typeof(TurretControl), nameof(SetControlPrefix)));
	}

	// The server's own copy of a player's panel (in a listen host's process) is that player's request, not the host's.
	private static void PanelSetPostfix(AutomatedTurretBlockDetailModel __instance, bool value)
	{
		if (value && __instance is not AutomatedTurretBlockDetailModelServer) _requestedAtMs = MpClock.NowMs();
	}

	private static bool SetControlPrefix(bool control)
	{
		if (!control || !AdminAccess.InSession) return true;
		if (_requestedAtMs != 0 && MpClock.NowMs() - _requestedAtMs <= RequestWindowMs)
		{
			_requestedAtMs = 0;
			return true;
		}
		Ignored++;
		GameLog.Instance.WarnOnce("turret-control-ignored", "turrets: another player took direct control of a turret; this game does not follow them into it");
		return false;
	}
}
