using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Y51_SE2Online.Core;

/// <summary>
/// Everything the game lets a host change, as the game reports it: every session component builder (native-settings.json)
/// and every loaded definition (native-definitions.json), each member with its type and current value. The runner reads the
/// dumps a server writes at startup; the game builds the same catalog from what it has loaded.
/// </summary>
public sealed class RulesCatalog
{
	public sealed record Field(string Name, string Type, bool Writable, JsonElement? Value);

	public sealed class Entry
	{
		public string Key;
		public string Type;
		public Guid? Id;
		public string DebugName;
		public bool Session;
		/// <summary>Set when the definition works values out once at load (and whether custom rules refresh them).</summary>
		public string LoadTime;
		public Dictionary<string, Field> Fields = new(StringComparer.Ordinal);
		public string Category => RulesCatalog.Category(Type);
		public string ShortType => Type.Substring(Type.LastIndexOf('.') + 1);
	}

	public readonly List<Entry> Session = new();
	public readonly Dictionary<string, List<Entry>> Definitions = new(StringComparer.Ordinal);
	public readonly Dictionary<Guid, Entry> ById = new();

	public int DefinitionCount => ById.Count;

	public void Add(Entry entry)
	{
		if (entry.Session) { Session.Add(entry); return; }
		if (!Definitions.TryGetValue(entry.Type, out var list)) Definitions[entry.Type] = list = new List<Entry>();
		list.Add(entry);
		if (entry.Id is Guid id) ById[id] = entry;
	}

	/// <summary>Reads the server's dumps (either may be null).</summary>
	public static RulesCatalog FromDumps(JsonElement? settings, JsonElement? definitions)
	{
		var catalog = new RulesCatalog();
		if (settings is { ValueKind: JsonValueKind.Object } s)
			foreach (var component in s.EnumerateObject())
				catalog.Add(new Entry { Key = component.Name, Type = component.Name, Session = true, Fields = Fields(component.Value) });
		if (definitions is { ValueKind: JsonValueKind.Object } d)
			foreach (var definition in d.EnumerateObject())
			{
				string[] parts = definition.Name.Split('#');
				if (parts.Length != 2 || !Guid.TryParse(parts[1], out var id) || !definition.Value.TryGetProperty("Settings", out var fields)) continue;
				catalog.Add(new Entry
				{
					Key = definition.Name, Type = parts[0], Id = id,
					DebugName = definition.Value.TryGetProperty("DebugName", out var name) ? name.GetString() : null,
					LoadTime = definition.Value.TryGetProperty("LoadTime", out var loadTime) && loadTime.ValueKind == JsonValueKind.String ? loadTime.GetString() : null,
					Fields = Fields(fields)
				});
			}
		return catalog;
	}

	private static Dictionary<string, Field> Fields(JsonElement members)
	{
		var result = new Dictionary<string, Field>(StringComparer.Ordinal);
		if (members.ValueKind != JsonValueKind.Object) return result;
		foreach (var m in members.EnumerateObject())
		{
			if (m.Value.ValueKind != JsonValueKind.Object) continue;
			string type = m.Value.TryGetProperty("Type", out var t) ? t.GetString() : "";
			bool writable = !m.Value.TryGetProperty("Writable", out var w) || w.ValueKind == JsonValueKind.True;
			result[m.Name] = new Field(m.Name, type, writable, m.Value.TryGetProperty("Value", out var v) ? v : null);
		}
		return result;
	}

	// Namespace words that say nothing about what a type is for.
	private static readonly HashSet<string> Generic = new(StringComparer.Ordinal)
	{
		"Keen", "Game2", "VRage", "Simulation", "Client", "Game", "WorldObjects", "GameSystems", "Definitions", "Components", "Core", "Library",
		"RuntimeSystems", "Shared", "Data", "CoreConfigurations", "DefaultDefinitions", "EntityComponents", "GameObjects", "Systems", "Utils"
	};

	/// <summary>A browsing group from the type's namespace: Characters, CubeBlocks, Items, Tools, Weather, Physics...</summary>
	public static string Category(string type)
	{
		var parts = type.Split('.');
		for (int i = 0; i < parts.Length - 1; i++)
			if (!Generic.Contains(parts[i])) return parts[i];
		return "General";
	}

	/// <summary>Players' games only show this (render, sound, camera, UI); the server does not use it.</summary>
	public static bool Visual(string type) => Regex.IsMatch(type, @"\.(Client|Render|Render12|Audio|Animation|UI)\.|Effects?Definition$|Camera|Particle|Material|Sound|Decal|Hud|HUD");

	/// <summary>The definitions a DefinitionSettings key selects: every one of a type, or type#Guid for one.</summary>
	public IEnumerable<Entry> Select(string selector)
	{
		string[] parts = selector.Split('#');
		if (!Definitions.TryGetValue(parts[0], out var list)) return Enumerable.Empty<Entry>();
		if (parts.Length == 1) return list;
		return Guid.TryParse(parts[1], out var id) ? list.Where(e => e.Id == id) : Enumerable.Empty<Entry>();
	}

	public Entry SessionEntry(string type) => Session.FirstOrDefault(e => e.Type == type);
}

public enum RuleSeverity { Clamped, Warning, Error }

public sealed record RuleIssue(RuleSeverity Severity, bool Session, string Key, string Field, string Message)
{
	public override string ToString() => $"{Severity}: {(Session ? "SessionSettings" : "DefinitionSettings")}[{Key}].{Field}: {Message}";
}

/// <summary>The dry run: checks a rules document against a catalog, without touching any game.</summary>
public static class RulesCheck
{
	private static readonly Regex TypeName = new(@"^[A-Za-z_][\w]*(\.[A-Za-z_][\w`]*)+$", RegexOptions.Compiled);

	public static List<RuleIssue> Validate(RulesDocument doc, RulesCatalog catalog)
	{
		var issues = new List<RuleIssue>();
		if (doc.Count > RuleSafety.MaxOverrides) issues.Add(new RuleIssue(RuleSeverity.Error, false, "", "", $"{doc.Count} overrides; at most {RuleSafety.MaxOverrides}."));
		foreach (var (key, fields) in doc.SessionSettings)
		{
			if (!TypeName.IsMatch(key)) { issues.Add(new RuleIssue(RuleSeverity.Error, true, key, "", "not a full object builder type name.")); continue; }
			var entry = catalog?.SessionEntry(key);
			if (catalog != null && entry == null)
			{
				issues.Add(new RuleIssue(RuleSeverity.Warning, true, key, "", "this world has no such session component (it is skipped)."));
				continue;
			}
			foreach (var (field, value) in fields) Check(issues, true, key, field, value, entry == null ? null : new[] { entry });
		}
		foreach (var (key, fields) in doc.DefinitionSettings)
		{
			string[] parts = key.Split('#');
			if (parts.Length > 2 || !TypeName.IsMatch(parts[0]) || parts.Length == 2 && !Guid.TryParse(parts[1], out _))
			{
				issues.Add(new RuleIssue(RuleSeverity.Error, false, key, "", "use the full definition type name, or type#Guid for one definition."));
				continue;
			}
			RulesCatalog.Entry[] selected = catalog?.Select(key).ToArray();
			if (catalog != null && selected.Length == 0)
			{
				issues.Add(new RuleIssue(RuleSeverity.Warning, false, key, "", "no loaded definition matches (a mod that is not installed?); it is skipped."));
				continue;
			}
			foreach (var (field, value) in fields) Check(issues, false, key, field, value, selected);
		}
		return issues;
	}

	private static void Check(List<RuleIssue> issues, bool session, string key, string field, JsonElement value, RulesCatalog.Entry[] entries)
	{
		if (string.IsNullOrWhiteSpace(field) || field.Split('.').Any(string.IsNullOrWhiteSpace))
		{
			issues.Add(new RuleIssue(RuleSeverity.Error, session, key, field, "empty field name."));
			return;
		}
		if (entries == null)
		{
			// No catalog: only what can be told without the game's value (a multiplier is checked against a stand-in number).
			bool multiply = value.ValueKind == JsonValueKind.Object && value.TryGetProperty(RuleSafety.Multiply, out _);
			var r = RuleSafety.Resolve(session, field, null, multiply ? JsonDocument.Parse("1").RootElement : null, value);
			if (r.Error != null) issues.Add(new RuleIssue(RuleSeverity.Error, session, key, field, r.Error));
			return;
		}
		string top = field.Split('.')[0];
		int errors = 0, clamps = 0;
		string firstError = null, firstNote = null;
		foreach (var entry in entries)
		{
			if (!entry.Fields.TryGetValue(top, out var member))
			{
				errors++;
				firstError ??= $"{entry.ShortType} has no setting '{top}'" + Suggest(entry, top) + ".";
				continue;
			}
			if (!member.Writable) { errors++; firstError ??= $"'{top}' is read-only."; continue; }
			var result = RuleSafety.Resolve(session, field, field.Contains('.') ? null : member.Type, RuleSafety.At(member.Value, field), value);
			if (result.Error != null) { errors++; firstError ??= result.Error; }
			else if (result.Note != null) { clamps++; firstNote ??= result.Note; }
		}
		string scope(int n) => entries.Length > 1 ? $" ({n} of {entries.Length} definitions)" : "";
		if (errors > 0) issues.Add(new RuleIssue(RuleSeverity.Error, session, key, field, firstError + scope(errors)));
		if (clamps > 0) issues.Add(new RuleIssue(RuleSeverity.Clamped, session, key, field, firstNote + scope(clamps)));
	}

	private static string Suggest(RulesCatalog.Entry entry, string name)
	{
		var close = entry.Fields.Keys.FirstOrDefault(k => k.Equals(name, StringComparison.OrdinalIgnoreCase))
			?? entry.Fields.Keys.FirstOrDefault(k => k.Contains(name, StringComparison.OrdinalIgnoreCase) || name.Contains(k, StringComparison.OrdinalIgnoreCase));
		return close == null ? "" : $" (did you mean '{close}'?)";
	}
}

/// <summary>Ready-made rule sets; applying one merges it over the current rules.</summary>
public static class RulePresets
{
	public sealed record Preset(string Name, string Description, Func<RulesDocument> Build);

	private const string Inventory = "Keen.Game2.Simulation.WorldObjects.Items.InventoryDefinition";
	private const string HandTools = "Keen.Game2.Simulation.WorldObjects.Tools.IntegrityHandToolDefinition";
	private const string ShipTools = "Keen.Game2.Simulation.WorldObjects.Tools.IntegrityToolDefinition";
	private const string Converters = "Keen.Game2.Simulation.WorldObjects.CubeBlocks.Production.ItemConverters.ItemConverterDefinition";
	private const string Backpack = "Keen.Game2.Simulation.WorldObjects.Tools.BackpackItemConverterDefinition";
	private const string Jetpack = "Keen.Game2.Simulation.WorldObjects.Characters.JetpackMovementComponentDefinition";
	private const string Containers = "Keen.Game2.Simulation.WorldObjects.CubeBlocks.ResourceDistribution.Resources.ResourceContainerDefinition";
	private const string Solar = "Keen.Game2.Simulation.WorldObjects.CubeBlocks.Production.SolarGeneratorDefinition";
	private const string Wind = "Keen.Game2.Simulation.WorldObjects.CubeBlocks.Production.WindTurbineDefinition";

	private static JsonElement Mul(double factor) => JsonDocument.Parse($"{{\"{RuleSafety.Multiply}\":{factor.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}").RootElement.Clone();

	private static RulesDocument Doc(string name, params (string Type, string Field, double Factor)[] rules)
	{
		var doc = new RulesDocument { Name = name };
		foreach (var (type, field, factor) in rules) doc.Set(false, type, field, Mul(factor));
		return doc;
	}

	public static readonly Preset[] All =
	{
		new("Inventories x10", "Every inventory (players, cargo, tools) holds ten times the mass.", () => Doc("Inventories x10", (Inventory, "MaxMass", 10))),
		new("Inventories x100", "Every inventory holds a hundred times the mass.", () => Doc("Inventories x100", (Inventory, "MaxMass", 100))),
		new("Fast building x10", "Hand and ship welders and grinders work ten times faster.", () => Doc("Fast building x10", (HandTools, "IntegrityChange", 10), (ShipTools, "IntegrityChange", 10))),
		new("Fast production x10", "Refineries, assemblers and the backpack converter run ten times faster.", () => Doc("Fast production x10", (Converters, "SpeedMultiplier", 10), (Backpack, "SpeedMultiplier", 10))),
		new("Strong jetpack x5", "Five times the jetpack thrust.", () => Doc("Strong jetpack x5", (Jetpack, "MaxThrust", 5))),
		new("More power x10", "Batteries store ten times as much; solar panels and wind turbines make ten times the power.",
			() => Doc("More power x10", (Containers, "MaxCapacity", 10), (Solar, "MaxOutput", 10), (Wind, "MaxOutput", 10))),
		new("Realistic", "Half-size inventories, slower building and production: every trip and resource counts.",
			() => Doc("Realistic", (Inventory, "MaxMass", 0.5), (HandTools, "IntegrityChange", 0.5), (ShipTools, "IntegrityChange", 0.5), (Converters, "SpeedMultiplier", 0.5))),
		new("Everything crazy", "x100 inventories, x10 building and production, x5 jetpack, x10 power.",
			() => Doc("Everything crazy", (Inventory, "MaxMass", 100), (HandTools, "IntegrityChange", 10), (ShipTools, "IntegrityChange", 10), (Converters, "SpeedMultiplier", 10),
				(Backpack, "SpeedMultiplier", 10), (Jetpack, "MaxThrust", 5), (Containers, "MaxCapacity", 10), (Solar, "MaxOutput", 10), (Wind, "MaxOutput", 10))),
	};
}

/// <summary>
/// How the editors show and read values: readable labels, units, durations as "1 h 30 min". "x10" multiplies; a duration
/// field also takes "90s", "10m", "1h30m", "2d"; anything else is JSON (or text). The raw member path stays visible beside.
/// </summary>
public static class RuleText
{
	private static readonly System.Globalization.CultureInfo Invariant = System.Globalization.CultureInfo.InvariantCulture;
	private const double TicksPerSecond = 10_000_000;
	private static readonly Regex Duration = new(@"^\s*(?:(\d+(?:\.\d+)?)\s*(ms|s|sec|m|min|h|hr|d)\s*)+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
	private static readonly Regex DurationPart = new(@"(\d+(?:\.\d+)?)\s*(ms|s|sec|m|min|h|hr|d)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

	public static bool IsDuration(string type) => type != null && RuleSafety.Underlying(type, out _) == "System.TimeSpan";

	public static JsonElement? Parse(string text, string type = null)
	{
		text = (text ?? "").Trim();
		if (text.Length == 0) return null;
		if ((text[0] is 'x' or 'X' or '*') && double.TryParse(text.Substring(1), System.Globalization.NumberStyles.Float, Invariant, out double factor))
			return Number($"{{\"{RuleSafety.Multiply}\":{factor.ToString("R", Invariant)}}}");
		if (IsDuration(type) && Duration.IsMatch(text))
		{
			double seconds = 0;
			foreach (Match m in DurationPart.Matches(text))
			{
				double n = double.Parse(m.Groups[1].Value, Invariant);
				seconds += m.Groups[2].Value.ToLowerInvariant() switch { "ms" => n / 1000, "s" or "sec" => n, "m" or "min" => n * 60, "h" or "hr" => n * 3600, _ => n * 86400 };
			}
			return Number(Math.Round(seconds * TicksPerSecond).ToString("R", Invariant));
		}
		try { return JsonDocument.Parse(text).RootElement.Clone(); }
		catch (JsonException) { return JsonDocument.Parse(JsonSerializer.Serialize(text)).RootElement.Clone(); }
	}

	private static JsonElement Number(string json) => JsonDocument.Parse(json).RootElement.Clone();

	/// <summary>The text an editor box shows for an override.</summary>
	public static string Edit(JsonElement? v, string type = null)
	{
		if (v is not { } e) return "";
		if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(RuleSafety.Multiply, out var f) && f.ValueKind == JsonValueKind.Number) return "x" + f.GetRawText();
		if (IsDuration(type) && e.ValueKind == JsonValueKind.Number) return Time(e.GetDouble(), compact: true);
		return e.GetRawText();
	}

	public static string Show(JsonElement? v, int max = 60, string type = null)
	{
		if (v is not { } e) return "-";
		if (IsDuration(type) && e.ValueKind == JsonValueKind.Number) return Time(e.GetDouble(), compact: false);
		string text = e.ValueKind == JsonValueKind.String ? e.GetString() : JsonSerializer.Serialize(e);
		return text.Length > max ? text.Substring(0, max - 3) + "..." : text;
	}

	/// <summary>Ticks as "1 h 30 min", "2.5 s", "250 ms" (compact: "1h30m", "2.5s").</summary>
	public static string Time(double ticks, bool compact)
	{
		if (ticks == 0) return compact ? "0s" : "0 s";
		string sign = ticks < 0 ? "-" : "";
		double s = Math.Abs(ticks) / TicksPerSecond;
		if (s < 1) return sign + (s * 1000).ToString("0.###", Invariant) + (compact ? "ms" : " ms");
		var parts = new List<string>();
		foreach (var (size, full, shortName) in new[] { (86400.0, " d", "d"), (3600.0, " h", "h"), (60.0, " min", "m") })
		{
			if (s < size) continue;
			double whole = Math.Floor(s / size);
			parts.Add(whole.ToString("0", Invariant) + (compact ? shortName : full));
			s -= whole * size;
		}
		if (s > 0.0005 || parts.Count == 0) parts.Add(s.ToString("0.###", Invariant) + (compact ? "s" : " s"));
		return sign + string.Join(compact ? "" : " ", parts);
	}

	/// <summary>A unit when the field's name says it: m/s, m, kg, N; "x" for multipliers.</summary>
	public static string Unit(string field, string type)
	{
		if (IsDuration(type)) return "";
		string name = (field ?? "").Split('.').Last();
		if (name.Length == 0) return null;
		if (Regex.IsMatch(name, "(Speed|Velocity)(Limit|Linear)?$")) return "m/s";
		if (Regex.IsMatch(name, "(Range|Radius|Distance|Height|Length|Depth)$")) return "m";
		if (Regex.IsMatch(name, "Mass$")) return "kg";
		if (Regex.IsMatch(name, "(Thrust|Force)$")) return "N";
		if (Regex.IsMatch(name, "(Multiplier|Scalar|Modifier)$")) return "x";
		return null;
	}

	/// <summary>"MaxThrust" as "Max Thrust", "JetpackMovementComponentDefinition" as "Jetpack Movement Component".</summary>
	public static string Label(string name)
	{
		if (string.IsNullOrEmpty(name)) return "";
		foreach (var suffix in new[] { "ObjectBuilder", "Definition", "Configuration", "Builder" })
			if (name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.Ordinal)) { name = name.Substring(0, name.Length - suffix.Length); break; }
		return Regex.Replace(name.Replace('_', ' '), "(?<=[a-z])(?=[A-Z0-9])|(?<=[A-Z])(?=[A-Z][a-z])|(?<=[0-9])(?=[A-Za-z])", " ").Trim();
	}

	/// <summary>A definition's name for people, from its file: "...\SolarPanel500_SolarGeneratorDefinition.def" is "Solar Panel 500".</summary>
	public static string DefinitionName(string debugName, string type)
	{
		if (string.IsNullOrWhiteSpace(debugName)) return Label(type?.Split('.').Last());
		string stem = System.IO.Path.GetFileNameWithoutExtension(debugName.Replace('\\', '/').Split('/').Last());
		int cut = stem.IndexOf('_');
		return Label(cut > 0 ? stem.Substring(0, cut) : stem);
	}

	/// <summary>"Single", "Int32?", "List"...: a member type as a person reads it.</summary>
	public static string TypeName(string type) => string.IsNullOrEmpty(type) ? "" : IsDuration(type) ? "duration" : RuleSafety.Underlying(type, out bool nullable).Split('.').Last().Split('`')[0] + (nullable ? "?" : "");

	/// <summary>What one override does to one value (or why it is refused), for an editor's result column.</summary>
	public static (string Text, RuleSeverity? Severity) Outcome(bool session, string field, string type, JsonElement? game, JsonElement spec)
	{
		var r = RuleSafety.Resolve(session, field, field.Contains('.') ? null : type, RuleSafety.At(game, field), spec);
		string shownType = field.Contains('.') ? null : type;
		if (r.Error != null) return (r.Error, RuleSeverity.Error);
		return r.Note != null ? ("-> " + Show(r.Value, 60, shownType) + ": " + r.Note, RuleSeverity.Clamped) : ("-> " + Show(r.Value, 60, shownType), null);
	}
}
