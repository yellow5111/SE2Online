using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Keen.VRage.Library.Definitions;
using Keen.VRage.Library.Serialization;
using Keen.VRage.Library.Utils;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>
/// Writes custom rules into the game's loaded definitions and session builders. Definitions are shared content that stays
/// loaded between worlds, so every changed member's original value is kept and put back before the next world (or the next
/// server a player joins) gets its own rules. Host, dedicated server and players all write the same way: the host sends
/// players the exact values it wrote.
/// </summary>
internal static class CustomRulesEngine
{
	private static readonly MethodInfo DeserializeMethod = typeof(SerializationHelper).GetMethod(nameof(SerializationHelper.DeserializeFromJson));
	private static readonly MethodInfo SerializeMethod = typeof(SerializationHelper).GetMethod(nameof(SerializationHelper.SerializeToJson));
	private static readonly object Gate = new();
	private static readonly Dictionary<object, Dictionary<string, object>> Originals = new(ReferenceEqualityComparer.Instance);
	private static readonly Dictionary<object, Dictionary<string, AccessibleTypeInfo.MemberInfo>> Members = new(ReferenceEqualityComparer.Instance);

	public sealed class Outcome
	{
		public readonly List<AppliedRule> Applied = new();
		public readonly List<string> Errors = new();
		public readonly List<string> Notes = new();
		public int Definitions;
	}

	public static string ToJson(Type type, object value) => (string)SerializeMethod.MakeGenericMethod(type).Invoke(null, new[] { value });

	public static object FromJson(Type type, string json)
	{
		try { return DeserializeMethod.MakeGenericMethod(type).Invoke(null, new object[] { json }); }
		catch (TargetInvocationException ex) { throw new ArgumentException(ex.GetBaseException().Message, ex.GetBaseException()); }
	}

	/// <summary>Puts every changed definition member back to the game's own value.</summary>
	public static int Revert()
	{
		lock (Gate)
		{
			int count = 0;
			foreach (var (target, values) in Originals)
				foreach (var (name, original) in values)
				{
					try { Members[target][name].Set(target, original); count++; }
					catch (Exception ex) { GameLog.Instance.Warn($"custom rules: could not restore {target.GetType().Name}.{name} ({ex.GetBaseException().Message})"); }
				}
			foreach (var definition in Originals.Keys.OfType<Definition>().ToList()) CustomRulesLoadTime.Recompute(definition);
			Originals.Clear();
			Members.Clear();
			return count;
		}
	}

	public static bool Active { get { lock (Gate) return Originals.Count > 0; } }

	private static object Original(object target, string name, AccessibleTypeInfo.MemberInfo member)
		=> Originals.TryGetValue(target, out var values) && values.TryGetValue(name, out var v) ? v : member.Get(target);

	private static void Remember(object target, string name, AccessibleTypeInfo.MemberInfo member)
	{
		if (!Originals.TryGetValue(target, out var values))
		{
			Originals[target] = values = new Dictionary<string, object>(StringComparer.Ordinal);
			Members[target] = new Dictionary<string, AccessibleTypeInfo.MemberInfo>(StringComparer.Ordinal);
		}
		if (values.ContainsKey(name)) return;
		values[name] = member.Get(target);
		Members[target][name] = member;
	}

	private static bool TryMember(object target, string name, out AccessibleTypeInfo.MemberInfo member, out string error)
	{
		error = null;
		member = default;
		if (target is not IAccessible accessible) { error = target.GetType().Name + " has no settings."; return false; }
		if (!accessible.GetTypeInfo().Members.TryGetValue(name, out member)) { error = $"{target.GetType().Name} has no setting '{name}'."; return false; }
		if (!member.CanRead || !member.CanWrite || !member.IsSerializable(SerializerFormat.Json)) { error = $"{target.GetType().Name}.{name} is read-only."; return false; }
		return true;
	}

	/// <summary>
	/// Computes the new value of one top-level member from the game's original value and every override on it (nested paths
	/// included). Returns the member's new JSON, or null with errors.
	/// </summary>
	private static string Compose(bool session, object target, string top, AccessibleTypeInfo.MemberInfo member, object original, IEnumerable<(string Field, JsonElement Spec)> overrides, string scope, Outcome outcome)
	{
		string originalJson = ToJson(member.Type, original);
		JsonElement originalElement = JsonDocument.Parse(originalJson).RootElement.Clone();
		JsonNode node = JsonNode.Parse(originalJson);
		bool changed = false;
		foreach (var (field, spec) in overrides)
		{
			bool nested = field.Contains('.');
			var result = RuleSafety.Resolve(session, field, nested ? null : member.Type.FullName, RuleSafety.At(originalElement, field), spec);
			if (result.Error != null) { outcome.Errors.Add(scope + result.Error); continue; }
			if (result.Note != null) outcome.Notes.Add(scope + result.Note);
			JsonNode value = result.Value is { } v ? JsonNode.Parse(v.GetRawText()) : null;
			if (!nested) { node = value; changed = true; continue; }
			var parts = field.Split('.');
			JsonNode at = node;
			for (int i = 1; i < parts.Length - 1 && at != null; i++) at = at is JsonObject o && o.TryGetPropertyValue(parts[i], out var next) ? next : null;
			if (at is not JsonObject parent || !parent.ContainsKey(parts[^1])) { outcome.Errors.Add(scope + $"{field}: there is no such field inside {top}."); continue; }
			parent[parts[^1]] = value;
			changed = true;
		}
		return changed ? node?.ToJsonString() ?? "null" : null;
	}

	private static string Write(object target, string top, AccessibleTypeInfo.MemberInfo member, string json, bool write = true, bool journal = true)
	{
		object parsed;
		try { parsed = FromJson(member.Type, json); }
		catch (ArgumentException ex) { return $"{top}: the game cannot read {Short(json)} ({ex.Message})."; }
		if (parsed == null && json != "null")
			return $"{top}: {Short(json)} names nothing this game has loaded (a definition from a missing mod?).";
		if (typeof(Definition).IsAssignableFrom(member.Type) && parsed is Definition d && !member.Type.IsInstanceOfType(d))
			return $"{top}: {Short(json)} is a {d.GetType().Name}, not a {member.Type.Name}.";
		if (!write) return null;
		if (journal) Remember(target, top, member);
		member.Set(target, parsed);
		return null;
	}

	private static string Short(string json) => json.Length > 80 ? json.Substring(0, 77) + "..." : json;

	private static List<Definition> AllDefinitions()
	{
		var list = new List<Definition>();
		foreach (var definition in Singleton<DefinitionManager>.Instance.GetDefinitionsOfTypeAndDescendants<Definition>()) list.Add(definition);
		return list;
	}

	/// <summary>Host side: applies a rules document to the loaded definitions (after putting back the previous world's).</summary>
	public static Outcome ApplyDefinitions(RulesDocument doc)
	{
		lock (Gate)
		{
			Revert();
			if (doc == null || doc.DefinitionSettings.Count == 0) return new Outcome();
			return ApplyDefinitions(doc, AllDefinitions());
		}
	}

	/// <summary>Applies a rules document to these definitions (journaled, so <see cref="Revert"/> puts them back).</summary>
	internal static Outcome ApplyDefinitions(RulesDocument doc, IEnumerable<Definition> all)
	{
		var outcome = new Outcome();
		lock (Gate)
		{
			var byType = all.GroupBy(d => d.GetType().FullName).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
			// Type-wide first, so a type#Guid selector for one definition wins over its type's.
			foreach (var (key, fields) in doc.DefinitionSettings.OrderBy(p => p.Key.Contains('#')))
			{
				string[] parts = key.Split('#');
				Guid? id = parts.Length == 2 && Guid.TryParse(parts[1], out var g) ? g : null;
				if (parts.Length > 2 || parts.Length == 2 && id == null) { outcome.Errors.Add($"[{key}] use the full definition type name, or type#Guid for one definition."); continue; }
				if (!byType.TryGetValue(parts[0], out var ofType)) { outcome.Errors.Add($"[{key}] no loaded definition has this type (a mod that is not installed?); skipped."); continue; }
				var selected = id == null ? ofType : ofType.Where(d => d.Guid == id).ToList();
				if (selected.Count == 0) { outcome.Errors.Add($"[{key}] no loaded definition has this GUID; skipped."); continue; }
				foreach (var definition in selected)
				{
					outcome.Definitions++;
					string scope = selected.Count > 1 ? $"[{key}] {definition.DebugName}: " : $"[{key}] ";
					bool changed = false;
					foreach (var group in fields.GroupBy(f => f.Key.Split('.')[0]))
					{
						if (!TryMember(definition, group.Key, out var member, out string error)) { outcome.Errors.Add(scope + error); continue; }
						var applied = Apply(definition, group.Key, member, group.Select(f => (f.Key, f.Value)), scope, outcome);
						if (applied == null) continue;
						outcome.Applied.Add(new AppliedRule(definition.GetType().FullName, definition.Guid, group.Key, applied));
						changed = true;
					}
					if (changed && CustomRulesLoadTime.Recompute(definition) is { } partly) outcome.Notes.Add(scope + partly);
				}
			}
		}
		return outcome;
	}

	private static string Apply(object target, string top, AccessibleTypeInfo.MemberInfo member, IEnumerable<(string, JsonElement)> overrides, string scope, Outcome outcome, bool session = false, bool write = true, bool journal = true)
	{
		try
		{
			if (RuleSafety.Locked(session, top, member.Type.FullName) is { } locked) { outcome.Errors.Add(scope + $"{top}: {locked}."); return null; }
			string json = Compose(session, target, top, member, Original(target, top, member), overrides, scope, outcome);
			if (json == null) return null;
			if (Write(target, top, member, json, write, journal: !session && journal) is { } error) { outcome.Errors.Add(scope + error); return null; }
			return json;
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			outcome.Errors.Add(scope + $"{top}: {ex.GetBaseException().Message}");
			return null;
		}
	}

	/// <summary>Player side: the exact values the host applied. Returns the problems (empty: all applied).</summary>
	public static List<string> ApplyExact(IReadOnlyList<AppliedRule> rules, bool revertFirst)
	{
		var problems = new List<string>();
		lock (Gate)
		{
			if (revertFirst) Revert();
			var byId = new Dictionary<Guid, Definition>();
			foreach (var definition in AllDefinitions()) byId[definition.Guid] = definition;
			var touched = new HashSet<Definition>(ReferenceEqualityComparer.Instance);
			foreach (var rule in rules)
			{
				if (!byId.TryGetValue(rule.Id, out var definition) || definition.GetType().FullName != rule.Type)
				{
					problems.Add($"this game has no {rule.Type.Split('.').Last()} {rule.Id} (a mod the server has?)");
					continue;
				}
				if (!TryMember(definition, rule.Path, out var member, out string error)) { problems.Add(error); continue; }
				try
				{
					if (Write(definition, rule.Path, member, rule.Json) is { } failed) problems.Add(definition.DebugName + ": " + failed);
					else touched.Add(definition);
				}
				catch (Exception ex) when (ex is not OutOfMemoryException) { problems.Add($"{definition.DebugName}.{rule.Path}: {ex.GetBaseException().Message}"); }
			}
			foreach (var definition in touched) CustomRulesLoadTime.Recompute(definition);
		}
		return problems;
	}

	/// <summary>Reads back a few of the applied values from the live definitions (proof in the log that they are in force).</summary>
	public static string Probe(IReadOnlyList<AppliedRule> rules, int max = 6)
	{
		var parts = new List<string>();
		lock (Gate)
		{
			var wanted = rules.GroupBy(r => (r.Type, r.Path)).Select(g => g.First()).Take(max).ToList();
			var byId = new Dictionary<Guid, Definition>();
			foreach (var d in AllDefinitions()) byId[d.Guid] = d;
			foreach (var rule in wanted)
			{
				if (!byId.TryGetValue(rule.Id, out var definition) || !TryMember(definition, rule.Path, out var member, out _)) continue;
				string live;
				try { live = ToJson(member.Type, member.Get(definition)); }
				catch (Exception ex) { live = "?" + ex.GetBaseException().Message; }
				string original = Originals.TryGetValue(definition, out var o) && o.TryGetValue(rule.Path, out var orig) ? ToJson(member.Type, orig) : "(not changed)";
				parts.Add($"{definition.GetType().Name}.{rule.Path} = {live} (game's {original}){(live == rule.Json ? "" : " MISMATCH, expected " + rule.Json)}");
			}
		}
		return string.Join("; ", parts);
	}

	/// <summary>Writes session settings into one session component builder (a fresh object per world load).</summary>
	public static void ApplySession(object builder, Dictionary<string, JsonElement> fields, Outcome outcome) => TryApply(builder, fields, true, outcome, write: true, journal: false);

	/// <summary>
	/// Overrides on one object (a session builder, or a stand-alone definition for checks). <paramref name="write"/> false only
	/// checks: every value is resolved, clamped and read by the game's own serializer, nothing is changed.
	/// </summary>
	public static void TryApply(object target, IEnumerable<KeyValuePair<string, JsonElement>> fields, bool session, Outcome outcome, bool write, bool journal)
	{
		string scope = $"[{target.GetType().FullName}] ";
		foreach (var group in fields.GroupBy(f => f.Key.Split('.')[0]))
		{
			if (!TryMember(target, group.Key, out var member, out string error)) { outcome.Errors.Add(scope + error); continue; }
			Apply(target, group.Key, member, group.Select(f => (f.Key, f.Value)), scope, outcome, session, write, journal);
		}
	}

	/// <summary>Every member of an object as the dumps show it: type, writable, value.</summary>
	public static Dictionary<string, object> Describe(IAccessible accessible)
	{
		var schema = new Dictionary<string, object>();
		foreach (var pair in accessible.GetTypeInfo().Members)
		{
			var member = pair.Value;
			if (!member.CanRead || !member.IsSerializable(SerializerFormat.Json)) continue;
			object value;
			try { value = JsonSerializer.Deserialize<JsonElement>(ToJson(member.Type, member.Get(accessible))); }
			catch (Exception ex) { value = new { SerializationError = ex.GetBaseException().Message }; }
			schema[pair.Key] = new { Type = member.Type.FullName, Writable = member.CanWrite, Value = value };
		}
		return schema;
	}

	/// <summary>The loaded definitions as a catalog with the game's own values (originals where rules changed them).</summary>
	public static RulesCatalog LiveCatalog(IEnumerable<KeyValuePair<string, Dictionary<string, object>>> sessionSchema)
	{
		var catalog = new RulesCatalog();
		foreach (var (type, fields) in sessionSchema)
			catalog.Add(new RulesCatalog.Entry { Key = type, Type = type, Session = true, Fields = ToFields(fields) });
		lock (Gate)
		{
			foreach (var definition in AllDefinitions())
			{
				var fields = new Dictionary<string, RulesCatalog.Field>(StringComparer.Ordinal);
				var info = ((IAccessible)definition).GetTypeInfo();
				foreach (var (name, member) in info.Members)
				{
					if (name is "Guid" || !member.CanRead || !member.IsSerializable(SerializerFormat.Json)) continue;
					JsonElement? value;
					try { value = JsonDocument.Parse(ToJson(member.Type, Original(definition, name, member))).RootElement.Clone(); }
					catch (Exception) { value = null; }
					fields[name] = new RulesCatalog.Field(name, member.Type.FullName, member.CanWrite, value);
				}
				catalog.Add(new RulesCatalog.Entry { Key = definition.GetType().FullName + "#" + definition.Guid, Type = definition.GetType().FullName, Id = definition.Guid, DebugName = definition.DebugName, Fields = fields, LoadTime = CustomRulesLoadTime.Describe(definition.GetType()) });
			}
		}
		return catalog;
	}

	private static Dictionary<string, RulesCatalog.Field> ToFields(Dictionary<string, object> described)
	{
		var element = JsonSerializer.SerializeToElement(described);
		var result = new Dictionary<string, RulesCatalog.Field>(StringComparer.Ordinal);
		foreach (var m in element.EnumerateObject())
			result[m.Name] = new RulesCatalog.Field(m.Name, m.Value.GetProperty("Type").GetString(), m.Value.GetProperty("Writable").GetBoolean(), m.Value.TryGetProperty("Value", out var v) ? v.Clone() : null);
		return result;
	}
}
