using System;

namespace Y51_SE2Online.Core;

/// <summary>
/// The rule for stories this side moved to a peer (review of 0.16.4, finding 2): one that comes back under its id must be
/// the same state machine, and only this side's own stories are recorded, so the table never holds more than what is in
/// flight to that peer.
/// </summary>
public static class ReturningStories
{
	/// <summary>A server records the ids it started (from the server); a client records the ones it started.</summary>
	public static bool Records(bool idFromServer, bool serverSide) => idFromServer == serverSide;

	/// <summary>Refused: a recorded story (its type's assembly-qualified name) that came back under another type name.</summary>
	public static bool Refuses(string? sent, string? came) => sent != null && !string.Equals(sent, came, StringComparison.Ordinal);
}
