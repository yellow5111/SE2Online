using System;

namespace Y51_SE2Online.Core;

/// <summary>
/// Fixed-step pacing for the headless dedicated loop. Each tick advances the world by one fixed step, so a server that
/// falls behind the wall clock runs the missed ticks early, up to twice real speed, until it is level again; only a backlog older than
/// the limit is dropped. Keen's own pacer drops everything once it is five frames (83 ms) behind, so every stall
/// longer than that was lost world time for good (sim speed below 1).
/// </summary>
public static class TickPacer
{
	/// <summary>The next tick's deadline in timer ticks; <paramref name="dropped"/> is the backlog given up, if any.</summary>
	public static long NextTarget(long lastTarget, long now, long frameTicks, long maxBacklogFrames, out long dropped)
	{
		long target = lastTarget + frameTicks;
		long oldest = now - frameTicks * maxBacklogFrames;
		dropped = target < oldest ? oldest - target : 0;
		// A deadline far ahead of the clock means the clock was reset or moved back: start again from now.
		if (target > now + 2 * frameTicks) target = now;
		return Math.Max(target, oldest);
	}

	/// <summary>
	/// Catch-up runs the world at most this many times real speed. Ticks run back to back after a stall made clients
	/// see every moving grid jump (all the stall's motion arrived in one burst); at 2x they speed up for a moment instead.
	/// </summary>
	public const int CatchUpSpeed = 2;

	/// <summary>When the tick with this deadline may start, given when the previous tick started.</summary>
	public static long RunAt(long deadline, long lastRun, long frameTicks) => Math.Max(deadline, lastRun + frameTicks / CatchUpSpeed);

	/// <summary>Backlog limit in frames for a CatchUpMs setting.</summary>
	public static int BacklogFrames(int catchUpMs, double hz) => Math.Max(1, (int)Math.Round(catchUpMs * hz / 1000.0));

	/// <summary>
	/// Relative due time (100 ns units, negative as SetWaitableTimer wants) for sleeping on the timer until <paramref name="spin"/>
	/// before a deadline <paramref name="ahead"/> timer ticks away; 0 when the wait is short enough to spin outright.
	/// </summary>
	public static long TimerDue(long ahead, long spin, long frequency) => ahead > spin ? -((ahead - spin) * 10_000_000 / frequency) : 0;

	/// <summary>LoopPriority setting to a thread priority; null for Normal (the game's own) or anything else.</summary>
	public static System.Threading.ThreadPriority? LoopPriority(string? name) => name?.ToLowerInvariant() switch
	{
		"abovenormal" => System.Threading.ThreadPriority.AboveNormal,
		"highest" => System.Threading.ThreadPriority.Highest,
		_ => null
	};

	public static bool IsLoopPriority(string? name) => name?.ToLowerInvariant() is "normal" or "abovenormal" or "highest";

}
