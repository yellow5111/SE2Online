using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Y51_SE2Online.Core;

/// <summary>
/// Brings a host's settings file up to date: options added in a newer version are written in with their defaults, so
/// hosts see them, and nothing already in the file is changed, reordered or removed.
/// </summary>
public static class JsonDefaults
{
	/// <summary>
	/// Adds every key of <paramref name="defaults"/> missing from <paramref name="target"/> (case-insensitive, like the
	/// readers), recursing into objects present in both. Arrays and the skipped paths ("Network.Motd") are never touched:
	/// a list or a free-form map the host left out is theirs to fill. Returns the dotted paths added.
	/// </summary>
	public static List<string> AddMissing(JsonObject target, JsonObject defaults, ISet<string> skip, string prefix = "")
	{
		var added = new List<string>();
		foreach (var (key, value) in defaults.ToList())
		{
			string path = prefix + key;
			if (skip.Contains(path)) continue;
			JsonNode? existing = null;
			bool found = false;
			foreach (var (have, node) in target)
			{
				if (!string.Equals(have, key, StringComparison.OrdinalIgnoreCase)) continue;
				existing = node;
				found = true;
				break;
			}
			if (!found)
			{
				target[key] = value?.DeepClone();
				added.Add(path);
			}
			else if (existing is JsonObject inner && value is JsonObject innerDefaults)
			{
				added.AddRange(AddMissing(inner, innerDefaults, skip, path + "."));
			}
		}
		return added;
	}

	/// <summary>
	/// Rewrites <paramref name="path"/> with the missing defaults added, after copying the original to "<paramref name="path"/>.bak".
	/// Returns the paths added (none: the file is left alone). Call only after the file has been read successfully.
	/// </summary>
	public static List<string> UpgradeFile(string path, JsonObject defaults, ISet<string> skip, Action<string> mustRead)
	{
		string text = File.ReadAllText(path);
		if (JsonNode.Parse(text) is not JsonObject root) return new List<string>();
		var added = AddMissing(root, defaults, skip);
		if (added.Count == 0) return added;
		string upgraded = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
		// The file is only replaced by one its reader still takes (it throws otherwise and nothing is written).
		mustRead(upgraded);
		File.Copy(path, path + ".bak", true);
		string temp = path + ".tmp";
		File.WriteAllText(temp, upgraded);
		AutoUpdate.ReplaceFile(temp, path);
		return added;
	}

	/// <summary>
	/// The keys of <paramref name="file"/> that <paramref name="known"/> (a default instance serialized) has no setting for,
	/// as dotted paths, compared case-insensitively like the readers and one level into objects both have. Keys in
	/// <paramref name="freeForm"/> are maps of the host's own entries and are not looked into.
	/// </summary>
	public static List<string> UnknownKeys(JsonObject file, JsonObject known, ISet<string> freeForm)
	{
		var unknown = new List<string>();
		foreach (var (key, value) in file)
		{
			var match = known.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));
			if (match.Key == null) { unknown.Add(key); continue; }
			if (freeForm.Contains(key) || value is not JsonObject inner || match.Value is not JsonObject knownInner) continue;
			foreach (var (innerKey, _) in inner)
				if (!knownInner.Any(p => string.Equals(p.Key, innerKey, StringComparison.OrdinalIgnoreCase))) unknown.Add(key + "." + innerKey);
		}
		return unknown;
	}

	/// <summary>
	/// The known option an unknown key is most likely a typo of (case-insensitive edit distance of at most 2, e.g.
	/// "Pasword" for "Password"), or null. A reader refuses such a file instead of starting with that setting at its default.
	/// </summary>
	public static string? LikelyTypo(string key, IEnumerable<string> known)
	{
		string? best = null;
		int bestDistance = 3;
		foreach (string candidate in known)
		{
			int d = EditDistance(key.ToLowerInvariant(), candidate.ToLowerInvariant(), bestDistance);
			if (d < bestDistance) { bestDistance = d; best = candidate; }
		}
		return best;
	}

	/// <summary>The known keys of the section an unknown path ("Network.Pasword" or "FutureOption") sits in.</summary>
	public static IEnumerable<string> KnownKeysAt(JsonObject known, string path)
	{
		int dot = path.IndexOf('.');
		if (dot < 0) return known.Select(p => p.Key);
		string section = path[..dot];
		return known.FirstOrDefault(p => string.Equals(p.Key, section, StringComparison.OrdinalIgnoreCase)).Value is JsonObject inner
			? inner.Select(p => p.Key) : Enumerable.Empty<string>();
	}

	// Levenshtein distance, given up (returning limit) once it cannot come under limit.
	private static int EditDistance(string a, string b, int limit)
	{
		if (Math.Abs(a.Length - b.Length) >= limit) return limit;
		var previous = new int[b.Length + 1];
		var current = new int[b.Length + 1];
		for (int j = 0; j <= b.Length; j++) previous[j] = j;
		for (int i = 1; i <= a.Length; i++)
		{
			current[0] = i;
			int rowBest = current[0];
			for (int j = 1; j <= b.Length; j++)
			{
				current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
				rowBest = Math.Min(rowBest, current[j]);
			}
			if (rowBest >= limit) return limit;
			(previous, current) = (current, previous);
		}
		return Math.Min(previous[b.Length], limit);
	}
}
