using System.Linq;
using HarmonyLib;
using Keen.Game2;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;
using Y51_SE2Online.SyncEngine;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>
/// A player who exits a joined world to the main menu has left: SE2 Online's own session to the host closes with "left
/// the game" right then. It used to stay open, half alive, until the next join found it dead, called it a lost connection
/// and reconnected once more. The game disconnects the same way when the connection drops; its reconnect helper is
/// handling an unexpected disconnect then, and that stays a loss (SE2 Online rejoins by itself).
/// </summary>
internal static class LeaveDetect
{
	private static readonly AccessTools.FieldRef<ReconnectComponent, Keen.VRage.Library.Threading.AtomicFlag> Unexpected =
		AccessTools.FieldRefAccess<ReconnectComponent, Keen.VRage.Library.Threading.AtomicFlag>("_handlingUnexpectedDisconnect");

	public static void Install(Harmony harmony)
	{
		harmony.Patch(AccessTools.Method(typeof(GameAppComponent), nameof(GameAppComponent.Disconnect)), prefix: new HarmonyMethod(typeof(LeaveDetect), nameof(DisconnectPrefix)));
	}

	private static void DisconnectPrefix(GameAppComponent __instance)
	{
		if (ClientWorld.Current is not { } client || ClientWorld.Progress.Stage != JoinStage.Playing || __instance.Client == null) return;
		bool dropped = __instance.Scene.Entities.Select(e => e.TryGet<ReconnectComponent>()).Any(r => r != null && Unexpected(r).IsSet);
		if (dropped) return;
		GameLog.Instance.Info("client: exited the world; leaving the host's session");
		ClientWorld.Progress.Set(JoinStage.Left, MpClock.NowMs());
		client.Leave();
	}
}
