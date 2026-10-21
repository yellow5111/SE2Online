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
}
