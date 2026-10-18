using System.Diagnostics;

namespace Y51_SE2Online.Core;

public static class MpClock
{
	private static readonly double MsPerTick = 1000.0 / (double)Stopwatch.Frequency;

	public static long NowMs()
	{
		return (long)((double)Stopwatch.GetTimestamp() * MsPerTick);
	}

	public static double NowMsExact() => Stopwatch.GetTimestamp() * MsPerTick;
}
