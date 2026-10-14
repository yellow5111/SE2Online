using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Y51_SE2Online.Sync;

/// <summary>Works out which mods own the definitions SE2 refused while loading a world's definition set.</summary>
public static class BrokenModMatch
{
	// DefinitionLoader: "Pre-Init validation failed for {debug name} ({guid}). See the log for details"
	private static readonly Regex Failed = new Regex(@"Pre-Init validation failed for (.+?) \(([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\)", RegexOptions.Compiled);

	/// <summary>The debug names ("Project\path\File.def") of every definition an exception says failed validation.</summary>
	public static List<string> FailedDefinitions(string exceptionText) =>
		Failed.Matches(exceptionText ?? "").Select(m => m.Groups[1].Value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

	/// <summary>
	/// Indexes into <paramref name="modNames"/> of the mods whose content holds a failed definition. A definition's debug
	/// name starts with its project's name; the longest matching name wins, and mods sharing that name are all taken.
	/// Failures that match no mod (vanilla content, or a mod's override of it) are left out.
	/// </summary>
	public static List<int> Owners(IReadOnlyList<string> failed, IReadOnlyList<string> modNames)
	{
		var owners = new List<int>();
		foreach (string definition in failed)
		{
			int best = modNames.Where(n => !string.IsNullOrEmpty(n) && definition.StartsWith(n + "\\", StringComparison.OrdinalIgnoreCase)).Select(n => n.Length).DefaultIfEmpty(0).Max();
			if (best == 0) continue;
			for (int i = 0; i < modNames.Count; i++)
				if (!owners.Contains(i) && modNames[i]?.Length == best && definition.StartsWith(modNames[i] + "\\", StringComparison.OrdinalIgnoreCase)) owners.Add(i);
		}
		return owners;
	}

	public static string Notice(IReadOnlyList<string> skipped) =>
		$"Skipped {skipped.Count} broken mod{(skipped.Count == 1 ? "" : "s")} that {(skipped.Count == 1 ? "doesn't" : "don't")} load on this game version: {string.Join(", ", skipped)}. Their blocks are missing from this world.";
}
