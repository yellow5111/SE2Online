using System;

namespace Y51_SE2Online.Sync;

/// <summary>A join capture walked over several server frames (HostWorld.Capture.cs).</summary>
public static class CapturePlan
{
	/// <summary>World parts reserved for a world of this many entities: a part closes once it holds <paramref name="perPart"/>, the last one may be short, and blocks can join a root while it is walked.</summary>
	public static int PartsFor(int entities, int perPart) => entities / Math.Max(1, perPart) + 2;

	/// <summary>Capture time per server frame: more while players queue up, so a burst of joins does not wait twice as long.</summary>
	public static double BudgetMs(int jobs) => Math.Min(16.0, 8.0 + 4.0 * Math.Max(0, jobs - 1));
}
