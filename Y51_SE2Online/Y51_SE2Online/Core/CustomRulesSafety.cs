using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Y51_SE2Online.Core;

/// <summary>What one override resolves to: the value to write, a clamping note, or why it is refused.</summary>
public readonly record struct RuleResult(JsonElement? Value, string Note, string Error)
{
	public bool Ok => Error == null;
}

/// <summary>
/// The per-field rules that keep custom values from breaking the game. Everything is decided from the member's type name
/// and the game's own value, so the same checks run in the runner (from the dumps), on the host and on every player.
/// </summary>
public static class RuleSafety
{
	public const string Multiply = "$mul";
	public const int MaxOverrides = 20_000;
	public const int MaxItems = 4096;
	public const int MaxDepth = 16;
	public const int MaxText = 4096;
	public const int MaxValueBytes = 256 * 1024;
	public const double MaxFactor = 1_000_000;
	/// <summary>A number may go this many times past the game's own value (either way), and at least to the floor.</summary>
	public const double MaxScale = 1000;

	// Fields whose sign may flip (positions, angles, offsets, biases, curves); every other number keeps the game's sign.
	private static readonly Regex Signed = new("(Offset|Position|Angle|Rotation|Bias|Delta|Direction|Shift|Pitch|Yaw|Roll|Temperature|Axis|Exponent|Deformation|Tilt|Lean)", RegexOptions.Compiled);
	private static readonly Regex Unit = new("(Ratio|Chance|Probability|Fraction|Normalized|Opacity|Alpha)$", RegexOptions.Compiled);

	// Session builder members that are the world's own records, not settings: editing them corrupts the save.
	private static readonly HashSet<string> WorldState = new(StringComparer.Ordinal)
	{
		"Identities", "PlayerData", "LocalIdentity", "GridIds", "LastId", "NamedEntities", "ElapsedTime", "Valid", "PersistentID", "SaveName",
		"UsedDebugMenu", "SpawnedEncounterSectors", "SpawnedEncounters", "ManualEncounters", "ProceduralDataSnapshots", "SkippedEntityIds", "Factions",
		"PerPlayerData", "Instances", "ContractBuilders", "Areas", "GroupDeaths", "SectorProgression", "LootTags", "EntityNames", "ProjectionEntityNames",
		"SimulatedProjectiles", "Variants", "Definition", "InitialCameraTransform", "WorldSpawn", "PermanentPreventionZones", "SpawnPreventionZones", "Activated",
		"WorldType", "LoadingFrames"
	};

	/// <summary>Why a member can never be overridden, or null.</summary>
	public static string Locked(bool session, string field, string typeName)
	{
		string top = field.Split('.')[0];
		if (top is "Guid" or "DebugName") return "the definition's identity cannot change";
		if (session && WorldState.Contains(top)) return "this is the world's own saved state, not a setting";
		if (typeName != null && (typeName.Contains("ResourceHandle", StringComparison.Ordinal) || typeName.Contains("ModelAsset", StringComparison.Ordinal)))
			return "asset references (models, textures, sounds) are locked: a missing file crashes every player";
		if (typeName != null && typeName.Contains("Delegate", StringComparison.Ordinal)) return "code references cannot be set";
		return null;
	}

	private enum Kind { Number, Integer, Bool, Text, Guid, Reference, List, Object, Unknown }

	private readonly record struct NumberType(Kind Kind, double Min, double Max);

	private static readonly Dictionary<string, NumberType> Numbers = new(StringComparer.Ordinal)
	{
		["System.Single"] = new(Kind.Number, -1e12, 1e12),
		["System.Double"] = new(Kind.Number, -1e12, 1e12),
		["System.Decimal"] = new(Kind.Number, -1e12, 1e12),
		["Keen.VRage.Library.Mathematics.FixedPoint"] = new(Kind.Number, -1e9, 1e9),
		["System.Byte"] = new(Kind.Integer, 0, byte.MaxValue),
		["System.SByte"] = new(Kind.Integer, sbyte.MinValue, sbyte.MaxValue),
		["System.Int16"] = new(Kind.Integer, short.MinValue, short.MaxValue),
		["System.UInt16"] = new(Kind.Integer, 0, ushort.MaxValue),
		["System.Int32"] = new(Kind.Integer, int.MinValue, int.MaxValue),
		["System.UInt32"] = new(Kind.Integer, 0, uint.MaxValue),
		["System.Int64"] = new(Kind.Integer, -1e15, 1e15),
		["System.UInt64"] = new(Kind.Integer, 0, 1e15),
		// Ticks: 10,000,000 per second; about 3 years either way.
		["System.TimeSpan"] = new(Kind.Integer, -1e15, 1e15),
	};

	/// <summary>The type inside Nullable`1[[...]], or the name itself.</summary>
	public static string Underlying(string typeName, out bool nullable)
	{
		nullable = typeName != null && typeName.StartsWith("System.Nullable`1[[", StringComparison.Ordinal);
		if (!nullable) return typeName?.Split(',')[0].Trim() ?? "";
		return typeName.Substring("System.Nullable`1[[".Length).Split(',')[0].Trim();
	}

	/// <summary>
	/// Resolves an override against the game's current value: {"$mul": n} multiplies it, anything else replaces it. Numbers are
	/// clamped (with a note) to finite, type-safe, sane bounds; wrong kinds, nulls and oversized values are refused.
	/// </summary>
	public static RuleResult Resolve(bool session, string field, string typeName, JsonElement? current, JsonElement spec)
	{
		string leaf = field.Split('.').Last();
		if (Locked(session, field, typeName) is { } locked) return Fail($"{field}: {locked}.");
		if (spec.GetRawText().Length > MaxValueBytes) return Fail($"{field}: the value is larger than {MaxValueBytes / 1024} KiB.");
		string underlying = Underlying(typeName, out bool nullable);
		var kind = KindOf(underlying, current);
		bool currentNull = current is not { } c0 || c0.ValueKind == JsonValueKind.Null;

		if (spec.ValueKind == JsonValueKind.Object && spec.TryGetProperty(Multiply, out var factorElement))
		{
			if (session) return Fail($"{field}: session settings are saved into the world, so a multiplier would grow at every restart; give the value itself.");
			if (spec.EnumerateObject().Count() != 1) return Fail($"{field}: {{\"{Multiply}\": n}} takes nothing else.");
			if (factorElement.ValueKind != JsonValueKind.Number || !factorElement.TryGetDouble(out double factor) || !double.IsFinite(factor))
				return Fail($"{field}: the multiplier must be a number.");
			if (factor < 0 || factor > MaxFactor) return Fail($"{field}: the multiplier must be between 0 and {MaxFactor:0}.");
			if (currentNull || current.Value.ValueKind != JsonValueKind.Number || kind is not (Kind.Number or Kind.Integer or Kind.Unknown))
				return Fail($"{field}: only a number can be multiplied (the game's value is {(currentNull ? "empty" : current.Value.ValueKind.ToString().ToLowerInvariant())}).");
			return Number(field, leaf, underlying, kind, current, current.Value.GetDouble() * factor);
		}

		if (spec.ValueKind == JsonValueKind.Null)
			return nullable || currentNull ? new RuleResult(spec.Clone(), null, null) : Fail($"{field}: cannot be empty (null); the game needs a value here.");

		switch (kind)
		{
			case Kind.Number or Kind.Integer:
				if (spec.ValueKind != JsonValueKind.Number || !spec.TryGetDouble(out double number)) return Fail($"{field}: needs a number.");
				return Number(field, leaf, underlying, kind, current, number);
			case Kind.Bool:
				return spec.ValueKind is JsonValueKind.True or JsonValueKind.False ? new RuleResult(spec.Clone(), null, null) : Fail($"{field}: needs true or false.");
			case Kind.Guid:
			case Kind.Reference:
				if (spec.ValueKind != JsonValueKind.String || !Guid.TryParse(spec.GetString(), out var id) || id == Guid.Empty)
					return Fail($"{field}: needs a definition GUID like \"93977c5f-ca4b-4b15-a124-ccf019bcdbcd\".");
				return new RuleResult(spec.Clone(), null, null);
			case Kind.Text:
				if (spec.ValueKind is not (JsonValueKind.String or JsonValueKind.Number)) return Fail($"{field}: needs text.");
				return Text(field, spec);
		}
		if (current is { } now && now.ValueKind != JsonValueKind.Null && now.ValueKind != spec.ValueKind && !(now.ValueKind is JsonValueKind.True or JsonValueKind.False && spec.ValueKind is JsonValueKind.True or JsonValueKind.False))
			return Fail($"{field}: the game's value is {Describe(now.ValueKind)}, this is {Describe(spec.ValueKind)}.");
		if (spec.ValueKind == JsonValueKind.Number) return Number(field, leaf, underlying, Kind.Unknown, current, spec.GetDouble());
		if (spec.ValueKind == JsonValueKind.String) return Text(field, spec);
		if (Shape(spec, 0, out int items) is { } bad) return Fail($"{field}: {bad}");
		if (items > MaxItems) return Fail($"{field}: {items} entries; at most {MaxItems}.");
		if (session && spec.ValueKind is JsonValueKind.Array or JsonValueKind.Object && !SmallNumericObject(spec))
			return Fail($"{field}: lists and records in session settings are world state; change them in game.");
		return new RuleResult(spec.Clone(), null, null);
	}

	private static RuleResult Fail(string error) => new(null, null, error);

	private static string Describe(JsonValueKind kind) => kind switch
	{
		JsonValueKind.Number => "a number", JsonValueKind.String => "text", JsonValueKind.Array => "a list", JsonValueKind.Object => "a record",
		JsonValueKind.True or JsonValueKind.False => "true/false", _ => "empty"
	};

	private static Kind KindOf(string underlying, JsonElement? current)
	{
		if (Numbers.TryGetValue(underlying, out var n)) return n.Kind;
		if (underlying == "System.Boolean") return Kind.Bool;
		if (underlying == "System.String" || underlying.EndsWith(".LocKey", StringComparison.Ordinal) || underlying.EndsWith(".StringId", StringComparison.Ordinal)) return Kind.Text;
		if (underlying == "System.Guid") return Kind.Guid;
		bool guidText = current is { ValueKind: JsonValueKind.String } s && Guid.TryParse(s.GetString(), out _);
		if (guidText && (underlying.EndsWith("Definition", StringComparison.Ordinal) || underlying.EndsWith("Configuration", StringComparison.Ordinal))) return Kind.Reference;
		if (underlying.Contains("Reader`1", StringComparison.Ordinal) || underlying.Contains("List`1", StringComparison.Ordinal) || underlying.EndsWith("[]", StringComparison.Ordinal)
			|| underlying.Contains("Array`1", StringComparison.Ordinal) || underlying.Contains("Set`1", StringComparison.Ordinal) || underlying.Contains("Dictionary`2", StringComparison.Ordinal))
			return Kind.List;
		return Kind.Unknown;
	}

	private static RuleResult Number(string field, string leaf, string underlying, Kind kind, JsonElement? current, double value)
	{
		if (!double.IsFinite(value)) return Fail($"{field}: must be a finite number.");
		var type = Numbers.TryGetValue(underlying, out var t) ? t : new NumberType(Kind.Number, -1e12, 1e12);
		double game = current is { ValueKind: JsonValueKind.Number } c && c.TryGetDouble(out double g) ? g : 0;
		bool integer = kind == Kind.Integer || type.Kind == Kind.Integer;
		double floor = underlying == "System.TimeSpan" ? 864_000_000_000 : integer ? 1_000_000 : 1000;
		double reach = Math.Max(Math.Abs(game), Math.Min(Math.Max(Math.Abs(game) * MaxScale, floor), type.Max));
		// The game's own value is always allowed, even past the type cap (FixedPoint's "unlimited" inventories).
		double min = Math.Min(game, Math.Max(type.Min, -reach)), max = Math.Max(game, Math.Min(type.Max, reach));
		string low = $"within {MaxScale:0}x the game's {Format(game)} and the type's safe range", high = low;
		if (game >= 0 && !Signed.IsMatch(leaf) && min < 0) { min = 0; low = "this value cannot go below zero"; }
		else if (game < 0 && !Signed.IsMatch(leaf)) { max = Math.Min(max, 0); high = "this value keeps the game's minus sign"; }
		if (game is >= 0 and <= 1 && Unit.IsMatch(leaf)) { max = Math.Min(max, 1); high = "a 0 to 1 share"; }
		double clamped = Math.Clamp(value, min, max);
		if (integer) clamped = Math.Round(clamped, MidpointRounding.AwayFromZero);
		string note = null;
		if (clamped != value)
		{
			note = integer && Math.Abs(clamped - value) < 1 && value >= min && value <= max
				? $"{field}: {Format(value)} rounded to {Format(clamped)} (whole numbers only)."
				: clamped == game && (game > type.Max || game < type.Min)
				? $"{field}: kept at the game's own {Format(game)}, already past the safe range ('unlimited')."
				: $"{field}: {Format(value)} clamped to {Format(clamped)}; allowed {Format(min)} to {Format(max)} ({(value < min ? low : high)}).";
		}
		return new RuleResult(JsonDocument.Parse(clamped.ToString("R", CultureInfo.InvariantCulture)).RootElement.Clone(), note, null);
	}

	private static string Format(double v) => Math.Abs(v) >= 1e7 ? v.ToString("0.###e0", CultureInfo.InvariantCulture) : v.ToString("0.######", CultureInfo.InvariantCulture);

	private static RuleResult Text(string field, JsonElement spec)
	{
		string text = spec.ValueKind == JsonValueKind.String ? spec.GetString() : spec.GetRawText();
		if (text.Length > MaxText) return Fail($"{field}: text is longer than {MaxText} characters.");
		if (text.Any(ch => char.IsControl(ch) && ch is not ('\n' or '\t' or '\r'))) return Fail($"{field}: text has control characters.");
		return new RuleResult(spec.Clone(), null, null);
	}

	// Walks a list or record: depth, entry count, finite numbers, text lengths.
	private static string Shape(JsonElement e, int depth, out int items)
	{
		items = 0;
		if (depth > MaxDepth) return $"nested deeper than {MaxDepth} levels.";
		switch (e.ValueKind)
		{
			case JsonValueKind.Array:
				foreach (var x in e.EnumerateArray())
				{
					if (Shape(x, depth + 1, out int inner) is { } bad) return bad;
					items += 1 + inner;
					if (items > MaxItems) return $"more than {MaxItems} entries.";
				}
				return null;
			case JsonValueKind.Object:
				foreach (var p in e.EnumerateObject())
				{
					if (p.Name.Length > 256) return "a key is longer than 256 characters.";
					if (Shape(p.Value, depth + 1, out int inner) is { } bad) return bad;
					items += 1 + inner;
					if (items > MaxItems) return $"more than {MaxItems} entries.";
				}
				return null;
			case JsonValueKind.Number:
				return e.TryGetDouble(out double d) && double.IsFinite(d) ? null : "has a number that is not finite.";
			case JsonValueKind.String:
				return e.GetString().Length > MaxText ? $"has text longer than {MaxText} characters." : null;
			default:
				return null;
		}
	}

	// Vectors and colours ({X,Y,Z}) are settings, not records.
	private static bool SmallNumericObject(JsonElement e)
		=> e.ValueKind == JsonValueKind.Object && e.EnumerateObject().Count() <= 8 && e.EnumerateObject().All(p => p.Value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False);

	/// <summary>The game's value at a dotted path inside a member's JSON (the first segment is the member itself).</summary>
	public static JsonElement? At(JsonElement? member, string field)
	{
		var parts = field.Split('.');
		var at = member;
		for (int i = 1; i < parts.Length && at is { } e; i++)
			at = e.ValueKind == JsonValueKind.Object && e.TryGetProperty(parts[i], out var next) ? next : null;
		return at;
	}
}
