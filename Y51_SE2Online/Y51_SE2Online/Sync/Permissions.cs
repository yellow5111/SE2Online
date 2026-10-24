using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Y51_SE2Online.Sync;

/// <summary>Name and chat decoration of one group (section 4 of the 0.18 design). Empty fields inherit.</summary>
public sealed class GroupStyle
{
	public const int MaxAffix = 16;
	public string NamePrefix { get; set; } = "";
	public string NamePrefixColor { get; set; } = "";
	public string NameColor { get; set; } = "";
	public string NameSuffix { get; set; } = "";
	public string NameSuffixColor { get; set; } = "";
	public string ChatPrefix { get; set; } = "";
	public string ChatPrefixColor { get; set; } = "";
	public string ChatSuffix { get; set; } = "";
	public string ChatSuffixColor { get; set; } = "";
	public string TextColor { get; set; } = "";

	public GroupStyle Copy() => (GroupStyle)MemberwiseClone();

	/// <summary>This style with every empty field taken from <paramref name="parent"/>.</summary>
	public GroupStyle Over(GroupStyle parent)
	{
		if (parent == null) return Copy();
		static string Pick(string own, string inherited) => string.IsNullOrEmpty(own) ? inherited ?? "" : own;
		return new GroupStyle
		{
			NamePrefix = Pick(NamePrefix, parent.NamePrefix), NamePrefixColor = Pick(NamePrefixColor, parent.NamePrefixColor), NameColor = Pick(NameColor, parent.NameColor),
			NameSuffix = Pick(NameSuffix, parent.NameSuffix), NameSuffixColor = Pick(NameSuffixColor, parent.NameSuffixColor),
			ChatPrefix = Pick(ChatPrefix, parent.ChatPrefix), ChatPrefixColor = Pick(ChatPrefixColor, parent.ChatPrefixColor),
			ChatSuffix = Pick(ChatSuffix, parent.ChatSuffix), ChatSuffixColor = Pick(ChatSuffixColor, parent.ChatSuffixColor), TextColor = Pick(TextColor, parent.TextColor)
		};
	}

	/// <summary>Cleans every field in place: affixes lose control and bidi characters and are cut to 16, colours must be #RRGGBB.</summary>
	public void Sanitize(string group, List<string> warnings)
	{
		string Affix(string value, string field)
		{
			// Not trimmed: "[Admin] " keeps the space before the name.
			string clean = NameGuard.StripInvisible(new string((value ?? "").Where(c => !char.IsControl(c)).Take(64).ToArray()));
			var info = new System.Globalization.StringInfo(clean);
			if (info.LengthInTextElements > MaxAffix) { warnings?.Add($"group {group}: Style.{field} is longer than {MaxAffix} characters; cut"); clean = info.SubstringByTextElements(0, MaxAffix); }
			return NameGuard.WholeCharacters(clean);
		}
		string Color(string value, string field)
		{
			value = (value ?? "").Trim();
			if (value.Length == 0 || IsColor(value)) return value.ToUpperInvariant();
			warnings?.Add($"group {group}: Style.{field} '{value}' is not a #RRGGBB colour; ignored");
			return "";
		}
		NamePrefix = Affix(NamePrefix, nameof(NamePrefix)); NameSuffix = Affix(NameSuffix, nameof(NameSuffix));
		ChatPrefix = Affix(ChatPrefix, nameof(ChatPrefix)); ChatSuffix = Affix(ChatSuffix, nameof(ChatSuffix));
		NamePrefixColor = Color(NamePrefixColor, nameof(NamePrefixColor)); NameColor = Color(NameColor, nameof(NameColor)); NameSuffixColor = Color(NameSuffixColor, nameof(NameSuffixColor));
		ChatPrefixColor = Color(ChatPrefixColor, nameof(ChatPrefixColor)); ChatSuffixColor = Color(ChatSuffixColor, nameof(ChatSuffixColor)); TextColor = Color(TextColor, nameof(TextColor));
	}

	public static bool IsColor(string value) => value is { Length: 7 } && value[0] == '#' && value.Skip(1).All(Uri.IsHexDigit);

	/// <summary>A #RRGGBB colour kept readable on the HUD: its luminance is clamped into 0.25-0.95 by scaling toward black
	/// or white, so nobody's name can be made invisible. Null for an empty or invalid colour.</summary>
	public static (byte R, byte G, byte B)? Readable(string hex)
	{
		if (!IsColor(hex)) return null;
		int value = Convert.ToInt32(hex[1..], 16);
		double r = (value >> 16) & 255, g = (value >> 8) & 255, b = value & 255;
		double l = (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255;
		if (l < 0.25)
		{
			// Mix toward white until the luminance reaches 0.25 (works for pure black too).
			double t = (0.25 - l) / (1 - l);
			r += (255 - r) * t; g += (255 - g) * t; b += (255 - b) * t;
		}
		else if (l > 0.95)
		{
			double t = 0.95 / l;
			r *= t; g *= t; b *= t;
		}
		return ((byte)Math.Round(r), (byte)Math.Round(g), (byte)Math.Round(b));
	}
}

/// <summary>One group: its own entries, parent and rank. The file name is the group name.</summary>
public sealed class PermissionGroup
{
	public string Name { get; init; } = "";
	public string Inherits { get; init; }
	public int Rank { get; init; }
	public GroupStyle Style { get; init; } = new();
	/// <summary>Key (or "prefix.*") to true/false, or to a number for value keys.</summary>
	public Dictionary<string, object> Entries { get; init; } = new(StringComparer.Ordinal);
}

/// <summary>players.json: which group each SteamID is in, optional per-player entries, and the admin list last mirrored
/// into access.json (so an edit of access.json made while the server was stopped can be told apart from our own mirror).</summary>
public sealed class PlayerAssignments
{
	public int Version { get; set; } = 1;
	public Dictionary<string, PlayerAssignment> Players { get; set; } = new();
	public List<string> MirroredAdmins { get; set; } = new();
}

public sealed class PlayerAssignment
{
	public string Group { get; set; } = PermissionSet.DefaultGroup;
	public string Name { get; set; } = "";
	public Dictionary<string, JsonElement> Permissions { get; set; }
}

/// <summary>
/// Rising World style permissions: Permissions/Player.json applies to everyone, Groups/&lt;name&gt;.json override the keys
/// they set and inherit the rest, players.json puts SteamIDs into groups. Resolution walks player entries, then the
/// group, its parents and default; within one layer the exact key wins over wildcards, the longest wildcard first, and the
/// first layer with a match decides. Owners (co-op host, console, RCON password, OwnerSteamIds) always pass and outrank
/// everyone. Thread-safe: groups are immutable after load, assignments are replaced whole on every change.
/// </summary>
public sealed class PermissionSet
{
	/// <summary>The group everyone is in (Permissions/Player.json). Group names keep the case of their file name for
	/// display and match without case everywhere ("group set bob admin").</summary>
	public const string DefaultGroup = "Player";
	public const string AdminGroup = "Admin";
	public const int OwnerRank = 1000, MaxRank = 999, MaxDepth = 8;
	private static readonly Regex KeyGrammar = new("^[a-z0-9_]+(\\.[a-z0-9_]+)*(\\.\\*)?$", RegexOptions.CultureInvariant);
	private static readonly Regex GroupGrammar = new("^[A-Za-z0-9_]{1,32}$", RegexOptions.CultureInvariant);

	/// <summary>The 0.18.0 pre-releases called the default group "default".</summary>
	private static string Alias(string name) => string.Equals(name, "default", StringComparison.OrdinalIgnoreCase) ? DefaultGroup : name;

	private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

	/// <summary>A group name as this server spells it, or null when there is no such group.</summary>
	public string Canonical(string name) => Group(Alias((name ?? "").Trim()))?.Name;

	private readonly Dictionary<string, PermissionGroup> _groups;
	private readonly object _write = new();
	private volatile Dictionary<ulong, Assigned> _players = new();
	private PlayerAssignments _file = new();
	private string _playersPath;
	private readonly ConcurrentDictionary<(ulong, string), (long Revision, object Value)> _cache = new();
	private volatile HashSet<ulong> _owners = new();
	private long _revision;

	private sealed record Assigned(string Group, string Name, Dictionary<string, object> Entries);

	private PermissionSet(Dictionary<string, PermissionGroup> groups) { _groups = groups; }

	/// <summary>Bumps on every reload, assignment or owner change; every effective-permission cache keys on it.</summary>
	public long Revision => System.Threading.Interlocked.Read(ref _revision);

	public IReadOnlyCollection<PermissionGroup> Groups => _groups.Values;

	public PermissionGroup Group(string name) => name != null && _groups.TryGetValue(Alias(name), out var g) ? g : null;

	/// <summary>Called after every assignment change, with every player in a group of rank 50 or more (the access.json mirror).</summary>
	public Action<IReadOnlyList<(ulong SteamId, string Name)>> AdminsChanged { get; set; }

	public const int AdminRank = 50;

	/// <summary>A reloaded set goes on from the old one's revision.</summary>
	public void ContinueFrom(PermissionSet previous)
	{
		if (previous == null) return;
		System.Threading.Interlocked.Exchange(ref _revision, previous.Revision + 1);
		_cache.Clear();
	}

	/// <summary>The owners: co-op host's own SteamID and server.json OwnerSteamIds. The console and RCON password are owners by call site.</summary>
	public void SetOwners(IEnumerable<ulong> owners)
	{
		_owners = new HashSet<ulong>(owners.Where(o => o != 0));
		Bump();
	}

	public bool IsOwner(ulong steamId) => steamId != 0 && _owners.Contains(steamId);

	private void Bump() { System.Threading.Interlocked.Increment(ref _revision); _cache.Clear(); }

	// ---- loading --------------------------------------------------------------------------------------------------

	/// <summary>An empty set: just the built-in default group with catalogue defaults (tests, and a host before load).</summary>
	public static PermissionSet Empty() => new(new Dictionary<string, PermissionGroup>(StringComparer.OrdinalIgnoreCase) { [DefaultGroup] = new PermissionGroup { Name = DefaultGroup } });

	/// <summary>Reads Player.json, Groups/*.json and the players file. Throws InvalidDataException for what makes the set
	/// unusable (bad JSON, an inheritance cycle or a chain over 8 deep); everything else is a warning.</summary>
	public static PermissionSet Load(string folder, string playersFile, out List<string> warnings)
	{
		warnings = new List<string>();
		var groups = new Dictionary<string, PermissionGroup>(StringComparer.OrdinalIgnoreCase);
		PermissionFiles.RenamePreReleaseFiles(folder, warnings);
		string defaults = Path.Combine(folder, DefaultGroup + ".json");
		groups[DefaultGroup] = File.Exists(defaults) ? ReadGroup(Read(defaults), DefaultGroup + ".json", DefaultGroup, warnings) : new PermissionGroup { Name = DefaultGroup };
		string groupDir = Path.Combine(folder, "Groups");
		if (Directory.Exists(groupDir))
			foreach (string file in Directory.GetFiles(groupDir, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
			{
				string name = Path.GetFileNameWithoutExtension(file);
				if (!GroupGrammar.IsMatch(name)) { warnings.Add($"Groups/{Path.GetFileName(file)}: group names are letters, digits and _ (at most 32); file ignored"); continue; }
				if (Same(Alias(name), DefaultGroup)) { warnings.Add($"Groups/{Path.GetFileName(file)}: everyone's group is Permissions/{DefaultGroup}.json; file ignored"); continue; }
				if (groups.ContainsKey(name)) { warnings.Add($"Groups/{Path.GetFileName(file)}: group {name} is there twice; file ignored"); continue; }
				groups[name] = ReadGroup(Read(file), "Groups/" + Path.GetFileName(file), name, warnings);
			}
		var set = Build(groups, warnings);
		set.LoadPlayers(playersFile, warnings);
		return set;
	}

	private static string Read(string path)
	{
		try { return File.ReadAllText(path); }
		catch (IOException e) { throw new InvalidDataException($"{Path.GetFileName(path)}: {e.Message}", e); }
	}

	/// <summary>The 0.17 rights in memory (default and admin groups, access.json's admins in group admin), for a host
	/// whose Permissions folder could not be prepared. Nothing is written.</summary>
	public static PermissionSet FromAccess(AccessList access)
	{
		var warnings = new List<string>();
		var groups = new Dictionary<string, PermissionGroup>(StringComparer.OrdinalIgnoreCase)
		{
			[DefaultGroup] = ReadGroup(PermissionFiles.DefaultGroupJson(Array.Empty<string>()).ToJsonString(), DefaultGroup, DefaultGroup, warnings),
			[AdminGroup] = ReadGroup(PermissionFiles.AdminGroupJson(PermissionFiles.DefaultRconAdmin).ToJsonString(), AdminGroup, AdminGroup, warnings)
		};
		var set = Build(groups, warnings);
		var players = new Dictionary<ulong, Assigned>();
		foreach (var a in access?.Copy(0).Admins ?? Array.Empty<AccessEntry>()) players[a.SteamId] = new Assigned(AdminGroup, a.Name, null);
		set._players = players;
		return set;
	}

	private static PermissionSet Build(Dictionary<string, PermissionGroup> groups, List<string> warnings)
	{
		foreach (var group in groups.Values)
		{
			if (Same(group.Name, DefaultGroup)) continue;
			int depth = 0;
			var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { group.Name };
			for (var g = group; !Same(g.Name, DefaultGroup);)
			{
				string parent = Alias(g.Inherits ?? DefaultGroup);
				if (!groups.TryGetValue(parent, out var next))
				{
					warnings.Add($"group {g.Name}: inherits '{parent}', which does not exist; it inherits {DefaultGroup} instead");
					next = groups[DefaultGroup];
				}
				if (!seen.Add(next.Name)) throw new InvalidDataException($"group {group.Name}: inheritance cycle ({string.Join(" -> ", seen)} -> {next.Name})");
				if (++depth > MaxDepth) throw new InvalidDataException($"group {group.Name}: inheritance is more than {MaxDepth} groups deep");
				g = next;
			}
		}
		return new PermissionSet(groups);
	}

	private static PermissionGroup ReadGroup(string text, string file, string name, List<string> warnings)
	{
		JsonDocument doc;
		try { doc = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
		catch (JsonException e) { throw new InvalidDataException($"{file}: {e.Message}", e); }
		using (doc)
		{
			var root = doc.RootElement;
			if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"{file}: not a JSON object");
			string inherits = null; int rank = 0; GroupStyle style = new();
			Dictionary<string, object> entries = new(StringComparer.Ordinal);
			foreach (var p in root.EnumerateObject())
			{
				switch (p.Name.ToLowerInvariant())
				{
					case "version": break;
					case "inherits": inherits = p.Value.ValueKind == JsonValueKind.String ? Alias(p.Value.GetString()?.Trim()) : null; break;
					case "rank":
						if (!p.Value.TryGetInt32(out rank) || rank < 0 || rank > MaxRank) { warnings.Add($"group {name}: Rank must be 0-{MaxRank}; 0 used"); rank = 0; }
						break;
					case "style":
						try { style = p.Value.Deserialize<GroupStyle>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new(); }
						catch (JsonException e) { warnings.Add($"group {name}: Style is not readable ({e.Message}); ignored"); }
						break;
					case "permissions": entries = ReadEntries(p.Value, "group " + name, warnings); break;
					case "blocks": break;
					default: warnings.Add($"group {name}: unknown setting '{p.Name}' ignored"); break;
				}
			}
			if (Same(name, DefaultGroup)) { inherits = null; if (rank != 0) { warnings.Add($"{DefaultGroup}.json: everyone's group's rank is always 0"); rank = 0; } }
			style.Sanitize(name, warnings);
			return new PermissionGroup { Name = name, Inherits = inherits, Rank = rank, Style = style, Entries = entries };
		}
	}

	private static Dictionary<string, object> ReadEntries(JsonElement obj, string where, List<string> warnings)
	{
		var entries = new Dictionary<string, object>(StringComparer.Ordinal);
		if (obj.ValueKind != JsonValueKind.Object) { warnings.Add(where + ": Permissions is not an object; ignored"); return entries; }
		foreach (var p in obj.EnumerateObject())
		{
			string key = p.Name.Trim().ToLowerInvariant();
			if (key == "*" || !KeyGrammar.IsMatch(key)) { warnings.Add($"{where}: '{p.Name}' is not a permission key; ignored"); continue; }
			bool wildcard = key.EndsWith(".*", StringComparison.Ordinal);
			var info = wildcard ? null : PermissionCatalog.Find(key);
			if (!wildcard && info == null && !IsDynamic(key))
			{
				string hint = Core.JsonDefaults.LikelyTypo(key, PermissionCatalog.Keys) is { } meant ? $"; did you mean '{meant}'?" : "";
				warnings.Add($"{where}: unknown key '{key}'{hint} (kept, a newer version's key)");
			}
			object value;
			if (p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
			{
				if (info?.IsValue == true) { warnings.Add($"{where}: '{key}' takes a number; ignored"); continue; }
				value = p.Value.GetBoolean();
			}
			else if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetDouble(out double number) && double.IsFinite(number))
			{
				if (wildcard) { warnings.Add($"{where}: '{key}' is a wildcard and cannot hold a number; ignored"); continue; }
				if (info != null && !info.IsValue) { warnings.Add($"{where}: '{key}' is true or false, not a number; ignored"); continue; }
				value = number;
			}
			else { warnings.Add($"{where}: '{key}' must be true, false or a number; ignored"); continue; }
			// Duplicate keys in one file: deny wins, so a copy-paste mistake never grants more than one of the lines says.
			if (entries.TryGetValue(key, out object before))
			{
				warnings.Add($"{where}: '{key}' is set twice; the stricter value is used");
				if (before is bool b && value is bool v) value = b && v;
				else if (before is double x && value is double y) value = Math.Min(x, y);
			}
			entries[key] = value;
		}
		return entries;
	}

	/// <summary>Keys made from names: per-group assignment scopes (admin.players.set_group.&lt;group&gt;).</summary>
	private static bool IsDynamic(string key) => key.StartsWith(SetGroupScope, StringComparison.Ordinal) && GroupGrammar.IsMatch(key[SetGroupScope.Length..]);

	/// <summary>The key that lets a group hand out <paramref name="group"/> (keys are lower case).</summary>
	public static string ScopeKey(string group) => SetGroupScope + (group ?? "").ToLowerInvariant();

	public const string SetGroupScope = "admin.players.set_group.";

	private void LoadPlayers(string path, List<string> warnings)
	{
		_playersPath = path;
		PlayerAssignments file = null;
		if (path != null && File.Exists(path))
		{
			try { file = JsonSerializer.Deserialize<PlayerAssignments>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
			catch (Exception e) when (e is JsonException or IOException) { throw new InvalidDataException($"{Path.GetFileName(path)}: {e.Message}", e); }
		}
		file ??= new PlayerAssignments();
		file.Players ??= new(); file.MirroredAdmins ??= new();
		var players = new Dictionary<ulong, Assigned>();
		foreach (var (id, entry) in file.Players)
		{
			if (!ulong.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out ulong steam) || steam == 0 || entry == null) { warnings.Add($"players.json: '{id}' is not a SteamID64; ignored"); continue; }
			string group = Alias((entry.Group ?? DefaultGroup).Trim());
			if (_groups.TryGetValue(group, out var known)) group = known.Name;
			else warnings.Add($"players.json: {id} is in group '{group}', which does not exist; {DefaultGroup} used");
			Dictionary<string, object> own = null;
			if (entry.Permissions is { Count: > 0 })
			{
				using var doc = JsonDocument.Parse(JsonSerializer.Serialize(entry.Permissions));
				own = ReadEntries(doc.RootElement, "players.json " + id, warnings);
			}
			players[steam] = new Assigned(group, SocialState.Clean(entry.Name, 64), own);
		}
		_file = file;
		_players = players;
	}

	// ---- resolution -----------------------------------------------------------------------------------------------

	/// <summary>The group a SteamID is in (default when unassigned or the group is gone).</summary>
	public string GroupOf(ulong steamId) => _players.TryGetValue(steamId, out var a) && _groups.TryGetValue(a.Group, out var g) ? g.Name : DefaultGroup;

	public int RankOf(ulong steamId) => IsOwner(steamId) ? OwnerRank : Group(GroupOf(steamId))?.Rank ?? 0;

	public bool HasOverrides(ulong steamId) => _players.TryGetValue(steamId, out var a) && a.Entries != null;

	/// <summary>Whether this player may do <paramref name="key"/>. Owners always may.</summary>
	public bool Has(ulong steamId, string key)
	{
		if (IsOwner(steamId)) return true;
		return Resolve(steamId, key) is bool b ? b : (PermissionCatalog.Find(key)?.Default ?? IsDynamic(key));
	}

	/// <summary>A value key's number for this player; owners get the key's "no limit" (its catalogue default).</summary>
	public double Value(ulong steamId, string key)
	{
		var info = PermissionCatalog.Find(key);
		double fallback = info?.DefaultValue ?? -1;
		if (IsOwner(steamId)) return fallback;
		return Resolve(steamId, key) is double d ? d : fallback;
	}

	/// <summary>Where a key's answer for this player comes from, for perms check: "allow, from admin.* in group admin".</summary>
	public string Explain(ulong steamId, string key)
	{
		if (IsOwner(steamId)) return "allow (owner)";
		foreach (var (layer, entries) in Layers(steamId))
			if (Match(entries, key) is { } hit)
				return (hit.Value is bool b ? (b ? "allow" : "deny") : Convert.ToString(hit.Value, CultureInfo.InvariantCulture)) + $", from {hit.Key} in {layer}";
		var info = PermissionCatalog.Find(key);
		return info == null && !IsDynamic(key) ? "unknown key" : (info?.IsValue == true ? info.DefaultValue.Value.ToString(CultureInfo.InvariantCulture) : (info?.Default ?? true) ? "allow" : "deny") + " (built-in default)";
	}

	private object Resolve(ulong steamId, string key)
	{
		if (string.IsNullOrEmpty(key)) return null;
		var cacheKey = (steamId, key);
		// The revision is read before resolving: an answer computed while an assignment changed is never served after it.
		long revision = Revision;
		if (_cache.TryGetValue(cacheKey, out var cached) && cached.Revision == revision) return cached.Value;
		object result = null;
		foreach (var (_, entries) in Layers(steamId))
			if (Match(entries, key) is { } hit) { result = hit.Value; break; }
		// ponytail: one cache entry per (player, key) asked; cleared on any change. Fine for ~130 keys x a few hundred players.
		if (_cache.Count < 200_000) _cache[cacheKey] = (revision, result);
		return result;
	}

	private IEnumerable<(string Layer, Dictionary<string, object> Entries)> Layers(ulong steamId)
	{
		if (_players.TryGetValue(steamId, out var a) && a.Entries != null) yield return ("players.json", a.Entries);
		var group = Group(GroupOf(steamId));
		for (int depth = 0; group != null && depth <= MaxDepth + 1; depth++)
		{
			yield return ("group " + group.Name, group.Entries);
			if (Same(group.Name, DefaultGroup)) yield break;
			group = Group(group.Inherits ?? DefaultGroup) ?? Group(DefaultGroup);
		}
	}

	private static KeyValuePair<string, object>? Match(Dictionary<string, object> entries, string key)
	{
		if (entries.Count == 0) return null;
		if (entries.TryGetValue(key, out object exact)) return new(key, exact);
		// A per-group scope (admin.players.set_group.<group>) answers only to itself and admin.players.set_group.*: a
		// broad "admin.*": false in default must not take every group off an actor whose group grants set_group.
		int floor = IsDynamic(key) ? SetGroupScope.Length - 2 : 0;
		for (int dot = key.LastIndexOf('.'); dot > floor; dot = key.LastIndexOf('.', dot - 1))
		{
			string wildcard = key[..(dot + 1)] + "*";
			if (entries.TryGetValue(wildcard, out object value) && value is bool) return new(wildcard, value);
		}
		return null;
	}

	/// <summary>The style a player shows, after inheritance along the group chain.</summary>
	public GroupStyle StyleOf(ulong steamId) => StyleOfGroup(GroupOf(steamId));

	public GroupStyle StyleOfGroup(string name)
	{
		var chain = new List<PermissionGroup>();
		for (var g = Group(name) ?? Group(DefaultGroup); g != null && chain.Count <= MaxDepth + 1; g = Same(g.Name, DefaultGroup) ? null : Group(g.Inherits ?? DefaultGroup) ?? Group(DefaultGroup))
			chain.Add(g);
		GroupStyle style = new();
		for (int i = chain.Count - 1; i >= 0; i--) style = chain[i].Style.Over(style);
		return style;
	}

	private (long Revision, Dictionary<string, GroupStyle> Styles) _styles = (-1, null);

	/// <summary>Every group's inherited style, for the public style table players receive (built once per revision).</summary>
	public Dictionary<string, GroupStyle> Styles()
	{
		var cached = _styles;
		if (cached.Revision == Revision && cached.Styles != null) return cached.Styles;
		var styles = _groups.Keys.ToDictionary(g => g, StyleOfGroup, StringComparer.Ordinal);
		_styles = (Revision, styles);
		return styles;
	}

	/// <summary>Every allow/deny key of the catalogue this player has: the admin panel shows what these allow.</summary>
	public string[] Grants(ulong steamId) => IsOwner(steamId) ? new[] { "*" } : PermissionCatalog.All.Where(p => !p.IsValue && Has(steamId, p.Key)).Select(p => p.Key).ToArray();

	/// <summary>Assigned players with their group and saved name.</summary>
	public (ulong SteamId, string Group, string Name)[] Assignments() => _players.Select(p => (p.Key, p.Value.Group, p.Value.Name)).OrderBy(p => p.Key).ToArray();

	public string NameOf(ulong steamId) => _players.TryGetValue(steamId, out var a) ? a.Name : null;

	// ---- assignment -----------------------------------------------------------------------------------------------

	/// <summary>
	/// Puts <paramref name="target"/> into <paramref name="group"/> for an actor (owner or a player's SteamID). Non-owners
	/// need admin.players.set_group (and admin.players.set_group.&lt;group&gt;, allowed unless a group file denies it), may not
	/// change their own group, may only touch players ranked below themselves and may only hand out groups ranked below
	/// their own. Returns (changed, message).
	/// </summary>
	public (bool Changed, string Message) SetGroup(ulong actor, bool actorOwner, ulong target, string targetName, string group)
	{
		group = Alias((group ?? "").Trim());
		string who = string.IsNullOrWhiteSpace(targetName) ? target.ToString(CultureInfo.InvariantCulture) : targetName;
		if (target == 0) return (false, "Choose a player.");
		if (!_groups.TryGetValue(group, out var to)) return (false, $"There is no group '{group}'. Groups: {string.Join(", ", _groups.Values.Select(g => g.Name).OrderBy(g => g, StringComparer.OrdinalIgnoreCase))}.");
		group = to.Name;
		if (IsOwner(target)) return (false, $"{who} is an owner of this server; owners are not in groups.");
		if (!actorOwner)
		{
			if (actor == target) return (false, "You cannot change your own group.");
			if (RefusalFor(actor, target, group) is { } refused) return (false, refused);
		}
		lock (_write)
		{
			string current = GroupOf(target);
			if (current == group && _players.ContainsKey(target) == (group != DefaultGroup || HasOverrides(target)))
				return (false, $"{who} is already in group {group}.");
			var players = new Dictionary<ulong, Assigned>(_players);
			players.TryGetValue(target, out var before);
			if (group == DefaultGroup && before?.Entries == null) players.Remove(target);
			else players[target] = new Assigned(group, SocialState.Clean(string.IsNullOrWhiteSpace(targetName) ? before?.Name ?? "" : targetName, 64), before?.Entries);
			try { SaveLocked(players); }
			catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return (false, $"Could not save players.json ({e.Message}); nothing changed."); }
			_players = players;
		}
		Bump();
		AdminsChanged?.Invoke(AdminMembers());
		return (true, $"{who} is now in group {group}" + (to.Rank > 0 ? $" (rank {to.Rank})." : "."));
	}

	/// <summary>Why a non-owner actor may not move <paramref name="target"/> into <paramref name="group"/>, or null.</summary>
	public string RefusalFor(ulong actor, ulong target, string group)
	{
		if (!Has(actor, "admin.players.set_group")) return "Your group may not change players' groups.";
		group = Canonical(group) ?? group;
		if (!Has(actor, ScopeKey(group))) return $"Your group may not hand out group {group}.";
		int mine = RankOf(actor);
		if (RankOf(target) >= mine) return "Only someone ranked above that player can change their group.";
		if ((Group(group)?.Rank ?? 0) >= mine) return $"Group {group} is ranked {Group(group)?.Rank ?? 0}; you can only hand out groups ranked below your own ({mine}).";
		return null;
	}

	/// <summary>Players in groups of rank 50 or more: the access.json Admins mirror a 0.17 rollback reads.</summary>
	public IReadOnlyList<(ulong SteamId, string Name)> AdminMembers() => AdminMembersOf(_players);

	private IReadOnlyList<(ulong SteamId, string Name)> AdminMembersOf(Dictionary<ulong, Assigned> players)
		=> players.Where(p => (Group(p.Value.Group)?.Rank ?? 0) >= AdminRank).Select(p => (p.Key, p.Value.Name)).OrderBy(p => p.Key).ToList();

	/// <summary>
	/// Brings in access.json admin changes made while this version was not running (the runner's stopped-server editor, a
	/// 0.17 rollback): an id added since the last mirror goes into group admin, an id removed goes back to default.
	/// </summary>
	public List<string> ReconcileAdmins(IEnumerable<(ulong SteamId, string Name)> accessAdmins)
	{
		var notes = new List<string>();
		var now = accessAdmins.ToList();
		var mirrored = new HashSet<ulong>(_file.MirroredAdmins.Select(s => ulong.TryParse(s, out ulong v) ? v : 0).Where(v => v != 0));
		bool admin = _groups.ContainsKey(AdminGroup);
		lock (_write)
		{
			var players = new Dictionary<ulong, Assigned>(_players);
			foreach (var (id, name) in now.Where(a => !mirrored.Contains(a.SteamId)))
				if (admin && (Group(GroupOf(id))?.Rank ?? 0) < AdminRank)
				{
					players.TryGetValue(id, out var before);
					players[id] = new Assigned(Group(AdminGroup)!.Name, SocialState.Clean(name, 64), before?.Entries);
					notes.Add($"{id} ({name}) is on access.json's admin list: group {AdminGroup}");
				}
			var listed = new HashSet<ulong>(now.Select(a => a.SteamId));
			foreach (ulong id in mirrored.Where(m => !listed.Contains(m)))
				if (players.TryGetValue(id, out var was) && (Group(was.Group)?.Rank ?? 0) >= AdminRank)
				{
					if (was.Entries == null) players.Remove(id); else players[id] = was with { Group = DefaultGroup };
					notes.Add($"{id} was taken off access.json's admin list: group {DefaultGroup}");
				}
			SaveLocked(players);
			_players = players;
		}
		if (notes.Count > 0) Bump();
		return notes;
	}

	private void SaveLocked(Dictionary<ulong, Assigned> players)
	{
		var file = new PlayerAssignments { Version = _file.Version };
		file.Players = players.OrderBy(p => p.Key).ToDictionary(p => p.Key.ToString(CultureInfo.InvariantCulture), p => new PlayerAssignment
		{
			Group = p.Value.Group, Name = p.Value.Name,
			Permissions = _file.Players.TryGetValue(p.Key.ToString(CultureInfo.InvariantCulture), out var old) ? old.Permissions : null
		});
		file.MirroredAdmins = AdminMembersOf(players).Select(a => a.SteamId.ToString(CultureInfo.InvariantCulture)).ToList();
		if (_playersPath != null)
		{
			Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_playersPath))!);
			string tmp = _playersPath + ".tmp";
			File.WriteAllText(tmp, JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }));
			File.Move(tmp, _playersPath, true);
		}
		_file = file;
	}
}
