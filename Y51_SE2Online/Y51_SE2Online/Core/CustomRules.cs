using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Y51_SE2Online.Core;

/// <summary>
/// A shareable set of custom rules: overrides of the game's native session settings (keyed by object builder type) and
/// definitions (keyed by definition type, or type#Guid for one definition). The same two sections live in a dedicated
/// server's server.json, in a co-op world's rules file and in an exported .se2rules.json file.
/// </summary>
public sealed class RulesDocument
{
	public const string FormatName = "SE2Online.CustomRules";

	public string Format { get; set; } = FormatName;
	public int Version { get; set; } = 1;
	public string Name { get; set; } = "";
	public string Description { get; set; } = "";
	/// <summary>Game build the rules were written against; informational.</summary>
	public string Game { get; set; } = "";
	public Dictionary<string, Dictionary<string, JsonElement>> SessionSettings { get; set; } = new();
	public Dictionary<string, Dictionary<string, JsonElement>> DefinitionSettings { get; set; } = new();
	/// <summary>SE2 Online's own gameplay options (PvP, usage and inventory multipliers, hacking...): see <see cref="OnlineRules"/>. Null when the file has none.</summary>
	[System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
	public Dictionary<string, JsonElement> Online { get; set; }

	public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

	[System.Text.Json.Serialization.JsonIgnore]
	public bool IsEmpty => SessionSettings.All(s => s.Value.Count == 0) && DefinitionSettings.All(s => s.Value.Count == 0);

	[System.Text.Json.Serialization.JsonIgnore]
	public int Count => SessionSettings.Sum(s => s.Value.Count) + DefinitionSettings.Sum(s => s.Value.Count);

	/// <summary>Reads a rules file, or any JSON with SessionSettings / DefinitionSettings sections (a server.json).</summary>
	public static RulesDocument Parse(string text)
	{
		var doc = JsonSerializer.Deserialize<RulesDocument>(text, Json) ?? throw new FormatException("The rules file is empty.");
		doc.SessionSettings ??= new();
		doc.DefinitionSettings ??= new();
		foreach (var section in doc.SessionSettings.Concat(doc.DefinitionSettings))
			if (section.Value == null) throw new FormatException($"'{section.Key}' has no settings (null).");
		if (doc.Count > RuleSafety.MaxOverrides) throw new FormatException($"Too many overrides ({doc.Count}); at most {RuleSafety.MaxOverrides}.");
		return doc;
	}

	public static RulesDocument Load(string path) => File.Exists(path) ? Parse(File.ReadAllText(path)) : new RulesDocument();

	public string ToJson() => JsonSerializer.Serialize(this, Json);

	public void Save(string path)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
		string temp = path + ".tmp";
		File.WriteAllText(temp, ToJson());
		File.Move(temp, path, true);
	}

	public RulesDocument Clone() => Parse(ToJson());

	/// <summary>Adds <paramref name="other"/>'s overrides; on the same field, <paramref name="other"/> wins.</summary>
	public RulesDocument Merge(RulesDocument other)
	{
		var result = Clone();
		Into(result.SessionSettings, other.SessionSettings);
		Into(result.DefinitionSettings, other.DefinitionSettings);
		if (other.Online is { Count: > 0 })
		{
			result.Online ??= new Dictionary<string, JsonElement>();
			foreach (var (key, value) in other.Online) result.Online[key] = value.Clone();
		}
		return result;
		static void Into(Dictionary<string, Dictionary<string, JsonElement>> to, Dictionary<string, Dictionary<string, JsonElement>> from)
		{
			foreach (var (key, fields) in from)
			{
				if (!to.TryGetValue(key, out var target)) to[key] = target = new Dictionary<string, JsonElement>();
				foreach (var (field, value) in fields) target[field] = value.Clone();
			}
		}
	}

	/// <summary>Sets one override; a null value removes it (back to the game's own value).</summary>
	public void Set(bool session, string key, string field, JsonElement? value)
	{
		var section = session ? SessionSettings : DefinitionSettings;
		if (value is { } v)
		{
			if (!section.TryGetValue(key, out var fields)) section[key] = fields = new Dictionary<string, JsonElement>();
			fields[field] = v.Clone();
		}
		else if (section.TryGetValue(key, out var fields))
		{
			fields.Remove(field);
			if (fields.Count == 0) section.Remove(key);
		}
	}

	public JsonElement? Get(bool session, string key, string field)
		=> (session ? SessionSettings : DefinitionSettings).TryGetValue(key, out var fields) && fields.TryGetValue(field, out var v) ? v : null;

	/// <summary>Stable over key order and formatting: two hosts with the same rules get the same hash.</summary>
	public string Hash()
	{
		var sb = new StringBuilder();
		foreach (var (name, section) in new[] { ("S", SessionSettings), ("D", DefinitionSettings) })
			foreach (var key in section.Keys.OrderBy(k => k, StringComparer.Ordinal))
				foreach (var field in section[key].Keys.OrderBy(k => k, StringComparer.Ordinal))
					sb.Append(name).Append('|').Append(key).Append('|').Append(field).Append('|').Append(JsonSerializer.Serialize(section[key][field])).Append('\n');
		return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())), 0, 8);
	}

	/// <summary>The two sections of a server.json as a document (other keys ignored).</summary>
	public static RulesDocument FromServerJson(JsonObject root)
	{
		var sections = new JsonObject();
		foreach (var (key, value) in root)
			if (key.Equals("SessionSettings", StringComparison.OrdinalIgnoreCase) || key.Equals("DefinitionSettings", StringComparison.OrdinalIgnoreCase))
				sections[key] = value?.DeepClone();
		return Parse(sections.ToJsonString());
	}

	private Dictionary<string, Dictionary<string, JsonElement>> Get(string name) => name == "SessionSettings" ? SessionSettings : DefinitionSettings;

	/// <summary>Writes the two sections into a server.json tree, keeping every other key as it was.</summary>
	public void IntoServerJson(JsonObject root)
	{
		foreach (var name in new[] { "SessionSettings", "DefinitionSettings" })
		{
			foreach (var stale in root.Where(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase) && p.Key != name).Select(p => p.Key).ToList()) root.Remove(stale);
			root[name] = JsonNode.Parse(JsonSerializer.Serialize(Get(name), Json));
		}
	}
}

/// <summary>One value the host actually applied (after $mul, clamping and type-wide expansion): what players apply too.</summary>
public readonly record struct AppliedRule(string Type, Guid Id, string Path, string Json);

/// <summary>Host to player: the exact definition values to apply before the world arrives.</summary>
public static class RulesWire
{
	public const int MaxRules = 250_000;
	public const int MaxInflatedBytes = 32 << 20;

	public static byte[] Encode(byte message, string hash, IReadOnlyList<AppliedRule> rules)
	{
		using var raw = new MemoryStream();
		using (var deflate = new DeflateStream(raw, CompressionLevel.Optimal, true))
		using (var w = new BinaryWriter(deflate, Encoding.UTF8, true))
		{
			w.Write(hash);
			w.Write(rules.Count);
			// Types and paths repeat across thousands of rules; a string table keeps big type-wide rule sets small.
			var strings = new Dictionary<string, int>();
			foreach (var r in rules)
			{
				Str(w, strings, r.Type);
				w.Write(r.Id.ToByteArray());
				Str(w, strings, r.Path);
				w.Write(r.Json);
			}
		}
		var result = new byte[1 + raw.Length];
		result[0] = message;
		raw.ToArray().CopyTo(result, 1);
		return result;
	}

	private static void Str(BinaryWriter w, Dictionary<string, int> table, string s)
	{
		if (table.TryGetValue(s, out int i)) { w.Write(i); return; }
		w.Write(-1);
		w.Write(s);
		table[s] = table.Count;
	}

	public static (string Hash, List<AppliedRule> Rules) Decode(ReadOnlySpan<byte> data)
	{
		using var inflated = new MemoryStream();
		using (var deflate = new DeflateStream(new MemoryStream(data.Slice(1).ToArray()), CompressionMode.Decompress))
		{
			var buffer = new byte[81920];
			int n;
			while ((n = deflate.Read(buffer, 0, buffer.Length)) > 0)
			{
				inflated.Write(buffer, 0, n);
				if (inflated.Length > MaxInflatedBytes) throw new ArgumentException("Custom rules message is too large.");
			}
		}
		inflated.Position = 0;
		using var r = new BinaryReader(inflated, Encoding.UTF8);
		string hash = r.ReadString();
		int count = r.ReadInt32();
		if (count < 0 || count > MaxRules) throw new ArgumentException("Custom rules message has a bad rule count.");
		var table = new List<string>();
		var rules = new List<AppliedRule>(count);
		for (int i = 0; i < count; i++)
		{
			string type = Read(r, table);
			var id = new Guid(r.ReadBytes(16));
			string path = Read(r, table);
			rules.Add(new AppliedRule(type, id, path, r.ReadString()));
		}
		return (hash, rules);
	}

	private static string Read(BinaryReader r, List<string> table)
	{
		int i = r.ReadInt32();
		if (i == -1) { string s = r.ReadString(); table.Add(s); return s; }
		if (i < 0 || i >= table.Count) throw new ArgumentException("Custom rules message has a bad string reference.");
		return table[i];
	}

	/// <summary>Player to host: the rules with this hash were applied (error empty), or why not.</summary>
	public static byte[] EncodeAck(byte message, string hash, string error)
	{
		using var ms = new MemoryStream();
		ms.WriteByte(message);
		using (var w = new BinaryWriter(ms, Encoding.UTF8, true)) { w.Write(hash ?? ""); w.Write(error ?? ""); }
		return ms.ToArray();
	}

	public static (string Hash, string Error) DecodeAck(ReadOnlySpan<byte> data)
	{
		using var r = new BinaryReader(new MemoryStream(data.Slice(1).ToArray()), Encoding.UTF8);
		string hash = r.ReadString(), error = r.ReadString();
		if (hash.Length > 64 || error.Length > 4000) throw new ArgumentException("Custom rules answer is too large.");
		return (hash, error);
	}
}

/// <summary>The rules a hosted world runs, ready to send to each player before the game sends them the world.</summary>
public sealed record RulesOffer(string Hash, byte[] Message, int Count);

public static class HostRules
{
	/// <summary>Null when the hosted world has no custom definition values.</summary>
	public static volatile RulesOffer Offer;
}

/// <summary>
/// SE2 Online's own options a shared rules file may carry: gameplay only. Who you are and how your PC is reached (name,
/// password, ports, addresses, visibility, keys, voice device, update choices) never travel in a shared file.
/// </summary>
public static partial class OnlineRules
{
	public static readonly string[] Keys =
	{
		"Description", "MaxPlayers", "TeamGrids", "SharedResearch", "Nametags", "NametagRange", "SpawnNearHost", "StreamingRadiusMeters", "WaterSync",
		"BuildGhosts", "ClientMovement", "OwnReplication", "VoiceEnabled", "VoiceRadiusMeters", "EnergyUsageMultiplier", "HydrogenUsageMultiplier",
		"OxygenUsageMultiplier", "PlayerInventoryMultiplier", "CargoInventoryMultiplier", "RespawnShips", "RespawnShipCooldownMinutes", "VanillaRespawnShips", "Pvp",
		"OfflineRaidProtection", "PauseOfflineStats", "PcuLimit", "SunRotation", "SunPeriodMinutes", "Hacking", "AllowHackingPlayerGrids",
		"HackTimeMultiplier", "HackLockdownSeconds", "HackLockdownCooldownSeconds", "Motd", "MotdTitle"
	};

	private static readonly HashSet<string> Shareable = new(Keys, StringComparer.OrdinalIgnoreCase);

	public static bool IsShareable(string key) => Shareable.Contains(key);

	public static string Canonical(string key) => Keys.FirstOrDefault(k => k.Equals(key, StringComparison.OrdinalIgnoreCase)) ?? key;
}
