using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Y51_SE2Online.Core;

/// <summary>Moving SE2 Online options between a rules file and OnlineSettings (the plugin side; the runner has its own checks).</summary>
public static partial class OnlineRules
{
	/// <summary>The shareable options of a settings object (export).</summary>
	public static Dictionary<string, JsonElement> From(OnlineSettings settings)
	{
		var all = JsonSerializer.SerializeToElement(settings);
		var result = new Dictionary<string, JsonElement>();
		foreach (var p in all.EnumerateObject())
			if (Shareable.Contains(p.Name)) result[Canonical(p.Name)] = p.Value.Clone();
		return result;
	}

	/// <summary>
	/// <paramref name="current"/> with the file's options on top, checked the way the Host screen checks them
	/// (<see cref="OnlineSettings.Validate"/>). Each refused option is one problem; the rest still apply.
	/// </summary>
	public static OnlineSettings Apply(OnlineSettings current, Dictionary<string, JsonElement> online, List<string> problems)
	{
		var node = JsonSerializer.SerializeToNode(current)!.AsObject();
		foreach (var (rawKey, value) in online ?? new())
		{
			if (!Shareable.Contains(rawKey)) { problems.Add($"Online.{rawKey}: not an SE2 Online option a shared file may set (only gameplay options travel)."); continue; }
			string key = node.Select(p => p.Key).FirstOrDefault(k => k.Equals(rawKey, StringComparison.OrdinalIgnoreCase));
			if (key == null) { problems.Add($"Online.{rawKey}: this version of SE2 Online has no such option; skipped."); continue; }
			var trial = node.DeepClone().AsObject();
			trial[key] = JsonNode.Parse(value.GetRawText());
			OnlineSettings parsed;
			try { parsed = trial.Deserialize<OnlineSettings>(); }
			catch (JsonException ex) { problems.Add($"Online.{key}: {value.GetRawText()} is the wrong kind of value ({ex.Message.Split('.')[0]})."); continue; }
			if (parsed?.Validate() is { } error) { problems.Add($"Online.{key}: {error}"); continue; }
			node = trial;
		}
		return node.Deserialize<OnlineSettings>();
	}
}
