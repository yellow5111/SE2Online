namespace Y51_SE2Online.Net;

public static class LanePlan
{
	// Control first; State and Bulk share the next level by weight. Bulk last (0, 2, 1) starved the ordered bundles and
	// big batches behind a saturating state stream, and every ordered message after them waited.
	public static readonly int[] Priorities = new int[3] { 0, 1, 1 };

	public static readonly ushort[] Weights = new ushort[3] { 1, 1, 1 };

	public static readonly Channel[] ServiceOrder = new Channel[3]
	{
		Channel.Control,
		Channel.State,
		Channel.Bulk
	};

	public static int PriorityOf(Channel c)
	{
		return Priorities[(uint)c];
	}
}
