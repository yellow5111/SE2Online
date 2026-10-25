using System;
using System.Collections.Generic;

namespace Y51_SE2Online.Sync;

/// <summary>
/// Lines of a name no wider than a limit: broken at spaces, and inside a word only when that word alone is wider than a
/// line. Every character of the name is on some line (only the spaces a break falls on go).
/// </summary>
internal static class NameWrap
{
	public static List<string> Lines(string name, float maxWidth, Func<string, float> width)
	{
		var lines = new List<string>();
		if (string.IsNullOrEmpty(name) || !(maxWidth > 0f) || width(name) <= maxWidth)
		{
			lines.Add(name ?? "");
			return lines;
		}
		string line = "";
		foreach (string word in name.Split(' '))
		{
			string joined = line.Length == 0 ? word : line + " " + word;
			if (width(joined) <= maxWidth)
			{
				line = joined;
				continue;
			}
			if (line.Length > 0) lines.Add(line);
			string rest = word;
			while (rest.Length > 1 && width(rest) > maxWidth)
			{
				int n = Fit(rest, maxWidth, width);
				lines.Add(rest[..n]);
				rest = rest[n..];
			}
			line = rest;
		}
		if (line.Length > 0) lines.Add(line);
		return lines;
	}

	// The longest start of the text that fits (at least one character, never half of a surrogate pair).
	private static int Fit(string text, float maxWidth, Func<string, float> width)
	{
		int low = 1, high = text.Length - 1;
		while (low < high)
		{
			int mid = (low + high + 1) / 2;
			if (width(text[..mid]) <= maxWidth) low = mid; else high = mid - 1;
		}
		if (char.IsHighSurrogate(text[low - 1])) low = low > 1 ? low - 1 : low + 1;
		return low;
	}
}
