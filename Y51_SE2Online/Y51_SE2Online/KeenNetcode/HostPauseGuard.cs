using System.Linq;
using HarmonyLib;
using Keen.Game2;
using Y51_SE2Online.Core;
using Y51_SE2Online.Dedicated;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>
/// SE2 pauses an in-process world while its ESC menu or a pausing video is open: AutoPauseWorldComponent counts them and
/// sets TickManagementComponent.Pause, which stops the server and client world ticks (physics, clock, and the server
/// session update SE2 Online's netcode runs in). A host is that in-process server, so every player froze with it.
/// While hosting, the count is still kept but a menu opening never pauses; Tick lifts a pause already on when hosting starts from the
/// pause menu, and puts it back when single player resumes with the menu still open. The host's held keys are not
/// touched here: the menu's UI input context captures all input, which cancels them as on a joined player's game.
/// Terminal, map, inventory and focus loss never paused. -y51online:hostpause:on keeps the game's pause while hosting.
/// </summary>
internal static class HostPauseGuard
{
	private static readonly bool KillSwitch = MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + HostPause.KillSwitch);

	private static AutoPauseWorldComponent? _live;

	// Whether _live's pause currently follows its count (single player) or is held off (hosting).
	private static bool _applied = true;

	public static void Install(Harmony harmony) =>
		harmony.Patch(AccessTools.PropertySetter(typeof(AutoPauseWorldComponent), "PauseCounter"), prefix: new HarmonyMethod(typeof(HostPauseGuard), nameof(PauseCounterPrefix)));

	// The state, not Current: a change that failed (Failed) keeps the old options in Current.
	private static bool Allowed
	{
		get
		{
			var state = MpPlugin.Controller?.State;
			// A restart or settings change made from the open menu passes through Stopping: still hosting for the players.
			bool hosting = state == OnlineSessionState.Hosting || state == OnlineSessionState.Stopping && MpPlugin.Controller?.Current?.Host == true;
			return HostPause.Allowed(hosting || DedicatedRuntime.Requested, state == OnlineSessionState.Joining, KillSwitch);
		}
	}

	private static bool PauseCounterPrefix(AutoPauseWorldComponent __instance, int value)
	{
		bool allowed = Allowed;
		_live = __instance;
		_applied = allowed;
		// A count back to 0 (the last menu closed, the session closing) runs as the game's: its unpause is harmless, and
		// TickManagementComponent outlives the session, so a pause left on would start the next world paused.
		if (allowed || value == 0) return true;
		if (!__instance._closing) __instance._pauseCounter = value;
		return false;
	}

	/// <summary>Main thread, every frame.</summary>
	public static void Tick()
	{
		if (_live is not { _closing: false } auto) return;
		bool allowed = Allowed;
		if (allowed == _applied) return;
		_applied = allowed;
		if (auto._pauseCounter == 0) return;
		auto._tickManagement.Pause = allowed;
		GameLog.Instance.Info(allowed ? "host pause: single player again with the menu open; the world pauses as before" : "host pause: hosting with the menu open; the world keeps running for players");
	}
}
