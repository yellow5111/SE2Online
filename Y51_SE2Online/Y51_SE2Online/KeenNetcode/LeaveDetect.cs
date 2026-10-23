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
		harmony.Patch(AccessTools.Method(typeof(ReconnectComponent), "OnClientUnexpectedDisconnect"), prefix: new HarmonyMethod(typeof(LeaveDetect), nameof(UnexpectedDisconnectPrefix)));
	}

	// The game's own reconnect prompt ran alongside SE2 Online's automatic rejoin, and its Reconnect started a join of its
	// own that counted as the player's (the rejoin count began again at zero). While SE2 Online's rejoin handles the loss
	// the prompt is skipped; the flag it would set still marks the disconnect as a loss for DisconnectPrefix.
	private static int _promptSkipped;

	private static bool UnexpectedDisconnectPrefix(ReconnectComponent __instance, Keen.VRage.Core.Services.Networking.ConnectionFailureReason reason)
	{
		if (!ClientWorld.OwnsLoss) return true;
		Unexpected(__instance).Set();
		// Told at once: SE2 Online's own link would only notice at its ping timeout.
		ClientWorld.KeenLost("connection lost (" + reason + ")");
		if (System.Threading.Interlocked.Exchange(ref _promptSkipped, 1) == 0) GameLog.Instance.Info("client: the connection dropped; SE2 Online rejoins by itself, so the game's own reconnect prompt is skipped");
		return false;
	}

	private static readonly AccessTools.FieldRef<ReconnectComponent, Keen.VRage.Core.Services.GameSessions.GameSessionID?> LastSession =
		AccessTools.FieldRefAccess<ReconnectComponent, Keen.VRage.Core.Services.GameSessions.GameSessionID?>("_lastNetworkSessionId");

	/// <summary>
	/// A join of <paramref name="target"/> is the game's own reconnect helper redialling the session it lost (its loop joins
	/// _lastNetworkSessionId). The helper's flag stays set after a server shutdown until the next connect, so the flag alone
	/// would also catch an unrelated join from the menu.
	/// </summary>
	internal static bool KeenReconnecting(GameAppComponent app, Keen.VRage.Core.Services.GameSessions.GameSessionID target)
		=> app.Scene.Entities.Select(e => e.TryGet<ReconnectComponent>()).Any(r => r != null && Unexpected(r).IsSet && Equals(LastSession(r), target));

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
