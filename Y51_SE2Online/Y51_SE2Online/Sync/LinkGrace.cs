namespace Y51_SE2Online.Sync;

/// <summary>
/// When a peer whose pongs stopped still gets the long grace (MpTransport): only while its link shows a real backlog, data
/// queued ahead of the pings. A connected session with nothing queued is a game that stopped answering, not a slow link,
/// and keeps the normal ping timeout.
/// </summary>
public static class LinkGrace
{
	public const long BacklogBytes = 64 * 1024;

	public const long BacklogQueueMicroseconds = 1_000_000;

	/// <summary>Ping at or above this is worth a link log line at once.</summary>
	public const int BadPingMs = 300;

	public static bool Backlogged(long pendingReliableBytes, long queueMicroseconds) => pendingReliableBytes > BacklogBytes || queueMicroseconds > BacklogQueueMicroseconds;

	public static bool Bad(int pingMs, long pendingReliableBytes, long queueMicroseconds) => pingMs >= BadPingMs || Backlogged(pendingReliableBytes, queueMicroseconds);
}
