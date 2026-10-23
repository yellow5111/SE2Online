using System;
using System.Collections.Generic;

namespace Y51_SE2Online.Sync;

/// <summary>
/// What still goes out when a frame's signals and stories cannot be written together. The frame is halved until each
/// part either writes or is a single unit (a join frame holds tens of thousands of units: one at a time stalled the
/// server's frame for seconds), order is kept, and a unit that cannot be written is replaced by its fallback (a skipped
/// story answer, so nobody waits for it forever) or dropped.
/// </summary>
internal static class SequenceSalvage
{
	public sealed record Failure<T>(T Unit, Exception Error, T? Replacement) where T : class;

	/// <summary>
	/// The units to send and the ones that could not be written, or null when nothing writes at all (the first
	/// <paramref name="probe"/> single units all fail before any part wrote: the writer itself is broken, not a unit).
	/// <paramref name="write"/> returns null when the units write, else the error.
	/// </summary>
	public static (List<T> Kept, List<Failure<T>> Failed)? Keep<T>(IReadOnlyList<T> units, Func<List<T>, Exception?> write, Func<T, T?> fallback, int probe = 3) where T : class
	{
		var kept = new List<T>(units.Count);
		var failed = new List<Failure<T>>();
		bool anyWrote = false;
		int leafFailures = 0;
		// Explicit stack of ranges, left part first, so the order of the frame is kept without recursion depth limits.
		var ranges = new Stack<(int Start, int Count)>();
		ranges.Push((0, units.Count));
		while (ranges.Count > 0)
		{
			var (start, count) = ranges.Pop();
			var part = new List<T>(count);
			for (int i = start; i < start + count; i++) part.Add(units[i]);
			if (write(part) is not { } error)
			{
				anyWrote = true;
				kept.AddRange(part);
				continue;
			}
			if (count > 1)
			{
				int left = count / 2;
				ranges.Push((start + left, count - left));
				ranges.Push((start, left));
				continue;
			}
			if (!anyWrote && ++leafFailures >= probe) return null;
			T unit = part[0];
			T? replacement = fallback(unit) is { } candidate && write(new List<T> { candidate }) == null ? candidate : null;
			if (replacement != null) kept.Add(replacement);
			failed.Add(new Failure<T>(unit, error, replacement));
		}
		return (kept, failed);
	}
}
