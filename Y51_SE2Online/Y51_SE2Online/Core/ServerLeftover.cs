namespace Y51_SE2Online.Core;

/// <summary>What the runner knows about the process its server folder still has when a start is asked for.</summary>
public readonly record struct LeftoverFacts(
	bool StatusForThisRun, int ServerPid, bool ServerAlive, bool FinalSaveCompleted, bool SaveInProgress, bool WorldLoaded, bool Exiting,
	double StatusQuietSeconds, bool BootstrapAlive, double SinceStartSeconds);

/// <summary>
/// When a process of a server folder is only a leftover of a stopped server (F21): ended by the next start, instead of
/// that start refusing with "already running". A live server, or one still loading or saving, is never a leftover.
/// </summary>
public static class ServerLeftover
{
	/// <summary>Seconds without a status update after which a loaded, idle server counts as hung.</summary>
	public const double HungSeconds = 180;

	/// <summary>Seconds after its start by which a server must have written a status of its own.</summary>
	public const double FirstStatusSeconds = 180;

	/// <summary>Why the process is a leftover, or null for a server that is live (or may still be).</summary>
	public static string? Reason(in LeftoverFacts f)
	{
		if (!f.StatusForThisRun)
			return f.BootstrapAlive && f.SinceStartSeconds > FirstStatusSeconds ? "no status from the server 3 minutes after it started" : null;
		if (!f.ServerAlive) return "the server process " + f.ServerPid + " has exited";
		if (f.FinalSaveCompleted) return "the server finished its final save but did not exit";
		// Loading and saving can stall the status for a while; a loaded, idle server that stops updating is hung.
		return f.StatusQuietSeconds > HungSeconds && !f.SaveInProgress && (f.WorldLoaded || f.Exiting)
			? $"no status update for {f.StatusQuietSeconds:0} s" : null;
	}
}
