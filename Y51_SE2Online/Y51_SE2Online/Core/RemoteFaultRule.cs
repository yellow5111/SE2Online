namespace Y51_SE2Online.Core;

/// <summary>
/// What KeenGuards.RefusedRequestPrefix does with an exception the game's crash handler got. A request the server turned
/// down reaches it twice: at once from the terminal's async void handler, and again when the game's unawaited-exception
/// finalizer is collected, which can be after the player left the world (the GC at the main menu closed the game).
/// Kept free of game types so the tests can run it.
/// </summary>
public static class RemoteFaultRule
{
	public enum Verdict { Crash, Tell, Quiet }

	/// <param name="remoteFault">A NetworkStoryRemoteFaultException: a request that failed on the server.</param>
	/// <param name="ownRefusal">SE2 Online's own access refusal raised in this game.</param>
	/// <param name="fromOwnSession">An SE2 Online session's transport delivered this very fault.</param>
	/// <param name="told">The player was already told about this very fault.</param>
	public static Verdict Judge(bool inSession, bool remoteFault, bool ownRefusal, bool fromOwnSession, bool told)
	{
		if (!remoteFault && !ownRefusal) return Verdict.Crash;
		if (told) return Verdict.Quiet;
		if (inSession) return Verdict.Tell;
		return fromOwnSession || ownRefusal ? Verdict.Quiet : Verdict.Crash;
	}
}
