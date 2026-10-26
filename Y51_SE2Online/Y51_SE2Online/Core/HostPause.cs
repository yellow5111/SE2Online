namespace Y51_SE2Online.Core;

/// <summary>Whether the game's own auto pause (ESC menu, pausing videos) may stop the world (HostPauseGuard).</summary>
public static class HostPause
{
	public const string KillSwitch = "hostpause:on";

	/// <summary>
	/// Single player pauses as before. Hosting never does, with or without players connected: the world is everyone's,
	/// and a player joining a paused host would load into a frozen world. A joined player has no in-process server to pause.
	/// </summary>
	public static bool Allowed(bool hosting, bool joined, bool killSwitch) => !joined && (!hosting || killSwitch);
}
