using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using HarmonyLib;
using Keen.VRage.Library.Definitions;
using Keen.VRage.Library.Serialization;
using Y51_SE2Online.Core;
using Y51_SE2Online.KeenNetcode;

namespace Y51_SE2Online.Dedicated;

/// <summary>
/// server.json's SessionSettings and DefinitionSettings are the dedicated server's custom rules (CustomRulesHost applies
/// them, co-op worlds use the same engine). A bad override is refused or clamped with a log line; it never stops the server.
/// </summary>
internal static class NativeSessionSettings
{
	private static bool _definitionsDumped;

	internal static void Install(Harmony harmony)
	{
		CustomRulesHost.Install(harmony);
		CustomRulesHost.Source = _ => (Rules(DedicatedRuntime.Config), Path.Combine(DedicatedRuntime.Config.Directory, "custom-rules-loadcheck.json"));
		LogDryRun(DedicatedRuntime.Config);
	}

	internal static RulesDocument Rules(DedicatedConfig config) => new() { SessionSettings = config.SessionSettings, DefinitionSettings = config.DefinitionSettings };

	/// <summary>Structure only: a null section is a broken file. Values are checked per override (<see cref="TypeCheck"/>).</summary>
	internal static void Validate(Dictionary<string, Dictionary<string, JsonElement>> settings)
	{
		if (settings == null) return;
		foreach (var entry in settings)
			if (entry.Value == null) throw new ArgumentException($"'{entry.Key}' has no settings (null); use {{}} or remove it.");
	}

	internal static void LogDryRun(DedicatedConfig config)
	{
		foreach (var issue in TypeCheck(Rules(config)))
			GameLog.Instance.Warn("custom rules: server.json " + issue);
	}

	/// <summary>
	/// Checks every override against the game's own types without a world: the type exists, the member is a writable
	/// setting, the value resolves and the game's serializer reads it (against the type's default value). Each problem is one
	/// line naming the override; the server refuses those overrides at load and applies the rest.
	/// </summary>
	internal static List<string> TypeCheck(RulesDocument rules)
	{
		var problems = new List<string>();
		foreach (var (key, fields) in rules.SessionSettings)
		{
			var type = FindType(key);
			if (type == null || !typeof(IAccessible).IsAssignableFrom(type) || !typeof(ISerializable).IsAssignableFrom(type))
			{
				problems.Add($"SessionSettings[{key}]: no such native session component builder; skipped.");
				continue;
			}
			Check(type, fields, true, $"SessionSettings[{key}]", problems);
		}
		foreach (var (key, fields) in rules.DefinitionSettings)
		{
			string[] parts = key.Split('#');
			if (parts.Length > 2 || parts.Length == 2 && !Guid.TryParse(parts[1], out _))
			{
				problems.Add($"DefinitionSettings[{key}]: use the full definition type name, or type#Guid for one definition; skipped.");
				continue;
			}
			var type = FindType(parts[0]);
			if (type == null || !typeof(Definition).IsAssignableFrom(type) || type.IsAbstract)
			{
				problems.Add($"DefinitionSettings[{key}]: no such concrete native definition type; skipped.");
				continue;
			}
			Check(type, fields, false, $"DefinitionSettings[{key}]", problems);
		}
		return problems;
	}

	private static void Check(Type type, Dictionary<string, JsonElement> fields, bool session, string scope, List<string> problems)
	{
		object instance;
		try { instance = Activator.CreateInstance(type); }
		catch (Exception ex) { problems.Add($"{scope}: cannot be checked offline ({ex.GetBaseException().Message})."); return; }
		var outcome = new CustomRulesEngine.Outcome();
		CustomRulesEngine.TryApply(instance, fields, session, outcome, write: false, journal: false);
		problems.AddRange(outcome.Errors.Select(e => scope + ": " + e.Substring(e.IndexOf("] ", StringComparison.Ordinal) + 2)));
	}

	private static Type FindType(string name)
	{
		var matches = AppDomain.CurrentDomain.GetAssemblies().Select(a => { try { return a.GetType(name, false); } catch (Exception) { return null; } }).Where(t => t != null).Distinct().ToArray();
		return matches.Length == 1 ? matches[0] : null;
	}

	/// <summary>One override on one object (kept for the dedicated validation tool): applied with the custom-rules safety rules, or throws why not.</summary>
	internal static void Write(object parent, string[] path, int index, JsonElement value, bool apply)
	{
		var outcome = new CustomRulesEngine.Outcome();
		CustomRulesEngine.TryApply(parent, new[] { KeyValuePair.Create(string.Join(".", path.Skip(index)), value) }, parent is not Definition, outcome, write: apply, journal: false);
		if (outcome.Errors.Count > 0) throw new ArgumentException(string.Join(" ", outcome.Errors));
	}

	internal static void Finish()
	{
		CustomRulesHost.WriteDumps(DedicatedRuntime.Config.DumpPath, DedicatedRuntime.Config.DefinitionDumpPath, !_definitionsDumped);
		_definitionsDumped = true;
	}
}
