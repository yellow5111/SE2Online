using System;
using System.Collections.Generic;

namespace Y51_SE2Online.Core;

/// <summary>The last lines the plugin logged, numbered, for RCON's json log tail. Thread-safe.</summary>
public sealed class LogRing
{
	public readonly record struct Line(long Seq, DateTime Utc, string Level, string Text);

	private readonly Line[] _lines;
	private long _next = 1;

	public LogRing(int capacity = 2000) => _lines = new Line[capacity];

	public static readonly LogRing Server = new();

	public long Last { get { lock (_lines) return _next - 1; } }

	public void Add(string level, string text)
	{
		lock (_lines)
		{
			_lines[_next % _lines.Length] = new Line(_next, DateTime.UtcNow, level, text);
			_next++;
		}
	}

	/// <summary>
	/// Up to <paramref name="max"/> lines after <paramref name="after"/>, oldest first, so a poller continues without gaps
	/// (lines that already fell out of the ring are gone). After 0 gives the newest <paramref name="max"/>.
	/// </summary>
	public List<Line> Since(long after, int max)
	{
		var result = new List<Line>();
		lock (_lines)
		{
			max = Math.Clamp(max, 0, _lines.Length);
			long first = Math.Max(Math.Max(after, 0) + 1, _next - _lines.Length);
			if (after <= 0) first = Math.Max(first, _next - max);
			for (long seq = first; seq < _next && result.Count < max; seq++) result.Add(_lines[seq % _lines.Length]);
		}
		return result;
	}
}
