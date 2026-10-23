namespace Y51_SE2Online.Sync;

/// <summary>
/// What a dedicated server does after a periodic save that did not complete. The game saving the world itself the whole time
/// (SaveResults.FailureSaveInProgress) is no failure: try again soon, no warning, no failure streak. Anything else is: try
/// again in a minute, and players are told once per streak of failures.
/// </summary>
public static class SaveRetry
{
	public const long BusyRetryMs = 15_000, FailedRetryMs = 60_000;

	/// <summary><paramref name="failing"/>: the previous save failed too. Failure: the save is now failing (busy keeps the streak as it was).</summary>
	public static (long RetryMs, bool Failure, bool Warn) After(bool busy, bool failing)
		=> busy ? (BusyRetryMs, failing, false) : (FailedRetryMs, true, !failing);
}
