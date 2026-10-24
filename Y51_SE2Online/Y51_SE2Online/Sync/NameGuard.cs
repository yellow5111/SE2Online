using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Y51_SE2Online.Sync;

/// <summary>
/// Player names are free Steam names, so "[Admin] Bob" typed into one would pass for a role tag. The host cleans every
/// name once at join, before anything shows it: invisible and direction-changing characters go, fullwidth and other
/// look-alike forms are folded (NFKC), and any configured name prefix or suffix and any [TAG] shape at either end are
/// stripped. Role decorations are drawn by the players' games from the style table, never from text in the name.
/// </summary>
public static class NameGuard
{
	private static readonly Regex TagAtStart = new(@"^\s*\[[^\]]{0,12}\]\s*", RegexOptions.CultureInvariant);
	private static readonly Regex TagAtEnd = new(@"\s*\[[^\[]{0,12}\]\s*$", RegexOptions.CultureInvariant);

	/// <summary>Zero-width (ZWJ, ZWNJ), bidi embedding/override/isolate, BOM and other format (Cf) characters.</summary>
	public static string StripInvisible(string text)
	{
		if (string.IsNullOrEmpty(text)) return "";
		var sb = new StringBuilder(text.Length);
		foreach (char c in text)
		{
			if (char.IsControl(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format) continue;
			if (c is '\u2028' or '\u2029' or '\u3164' or '\uFFA0' or '\u115F' or '\u1160') continue;
			sb.Append(c);
		}
		return sb.ToString();
	}

	/// <summary>The text without unpaired surrogates (half of a character outside the basic plane).</summary>
	public static string WholeCharacters(string text)
	{
		if (string.IsNullOrEmpty(text)) return "";
		var sb = new StringBuilder(text.Length);
		for (int i = 0; i < text.Length; i++)
		{
			if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) { sb.Append(text, i, 2); i++; }
			else if (!char.IsSurrogate(text[i])) sb.Append(text[i]);
		}
		return sb.ToString();
	}

	private static string Fold(string text)
	{
		text = WholeCharacters(StripInvisible(text ?? ""));
		try { return text.Normalize(NormalizationForm.FormKC); } catch (ArgumentException) { return text; }
	}

	public static string Clean(string name, ulong steamId, IEnumerable<string> affixes)
	{
		string text = StripInvisible(Fold(name)).Trim();
		var parts = (affixes ?? Enumerable.Empty<string>()).Select(a => StripInvisible(Fold(a)).Trim())
			.Where(a => a.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(a => a.Length).ToList();
		for (int guard = 0; guard < 16; guard++)
		{
			string before = text;
			foreach (string a in parts)
			{
				if (text.StartsWith(a, StringComparison.OrdinalIgnoreCase)) text = text[a.Length..].TrimStart();
				if (text.EndsWith(a, StringComparison.OrdinalIgnoreCase)) text = text[..^a.Length].TrimEnd();
			}
			text = TagAtEnd.Replace(TagAtStart.Replace(text, ""), "");
			if (text == before) break;
		}
		text = SocialState.Clean(text, 64);
		if (text.Length == 0 || text.Equals("Server", StringComparison.OrdinalIgnoreCase)) text = "Player " + (steamId % 10000).ToString("0000", System.Globalization.CultureInfo.InvariantCulture);
		return text;
	}
}
