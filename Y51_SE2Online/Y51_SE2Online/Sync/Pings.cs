using System;
using System.Collections.Generic;
using System.Linq;

namespace Y51_SE2Online.Sync;

/// <summary>
/// What a ping means. The first six come from what the raycast hit (a tap of the ping key); the last six are the radial
/// menu (hold the key). The value is also the glyph, so the set is fixed: no free-form icons (design 10.5).
/// </summary>
public enum PingKind : byte { Point, Rock, Block, Ship, Base, Player, Danger, Go, Regroup, Mine, Build, Look }

/// <summary>Who sees a player's pings (server setting Network.PingAudience).</summary>
public enum PingAudience : byte { Faction, Allies, All, Range }

/// <summary>How a ping is coloured for the one who sees it: the pinger's relation to them, or a server/admin broadcast.</summary>
public enum PingRelation : byte { Own, Faction, Ally, Neutral, Admin }

/// <summary>A player's ping, up to the host: where, its kind, a glyph from the fixed set and an optional label.</summary>
public sealed record PingSend(double X, double Y, double Z, PingKind Kind, PingKind Icon, string Label = "", string What = "");

/// <summary>
/// A ping, host to the players who may see it. The host builds the name and cleans the label, so nobody can forge another
/// player's ping. One ping per player: a new one moves the old one (same Id). Remove takes it away early.
/// </summary>
public sealed record PingShow(uint Id, ushort From, string FromName, string Group, double X, double Y, double Z, PingKind Kind,
	PingKind Icon, string Label, string What, int LifetimeS, PingRelation Relation, bool Remove = false);

/// <summary>The marker filter (Alt+M): every marker, the relevant ones, pings only, or none of ours.</summary>
public enum MarkerMode : byte { All, Relevant, PingsOnly, Off }

/// <summary>The marker categories the filter turns on and off. Vanilla markers are only ever filtered, never restyled.</summary>
public enum MarkerCategory : byte { Pings, Gps, Players, Signals, Quest, Ore }

/// <summary>A server's marker defaults (Network.MarkerDefaults); a player's own saved choice overrides them.</summary>
public sealed class MarkerDefaults
{
	public string Mode { get; set; } = "relevant";
	/// <summary>Categories off by default (pings, gps, players, signals, quest, ore).</summary>
	public List<string> Off { get; set; } = new();
	/// <summary>Per category, markers farther than this many metres are not drawn; missing or 0 is no limit.</summary>
	public Dictionary<string, double> MaxRangeMeters { get; set; } = new();
}

/// <summary>The ping rules, apart from the game so they are unit tested.</summary>
public static class PingRules
{
	public const int MaxLabel = 24, MaxWhat = 48;
	public const int MinLifetimeS = 5, MaxLifetimeS = 600;
	// A third-person camera behind a big ship sits well away from the character the host measures from.
	public const double RangeSlackMeters = 250;

	public static bool IsRadial(PingKind kind) => kind is PingKind.Danger or PingKind.Go or PingKind.Regroup or PingKind.Mine or PingKind.Build or PingKind.Look;

	/// <summary>Why the host refuses this ping, or null: finite, in range of the pinger's character, kind and glyph in the set.</summary>
	public static string Refusal(PingSend ping, (double X, double Y, double Z)? from, double maxRangeMeters)
	{
		if (ping == null || !double.IsFinite(ping.X) || !double.IsFinite(ping.Y) || !double.IsFinite(ping.Z)) return "That ping has no place.";
		if (!Enum.IsDefined(ping.Kind) || !Enum.IsDefined(ping.Icon)) return "Unknown ping.";
		if (from is not { } at) return "You need a character to ping.";
		double d = Distance(at, (ping.X, ping.Y, ping.Z));
		if (d > maxRangeMeters + RangeSlackMeters) return $"That is too far to ping ({DistanceText(d)}; at most {DistanceText(maxRangeMeters)}).";
		return null;
	}

	/// <summary>A label as everyone sees it: one line, no control or direction-changing characters, at most
	/// <see cref="MaxLabel"/> characters, the server's words replaced by ***.</summary>
	public static string CleanLabel(string label, IReadOnlyCollection<string> words = null) => CleanText(label, MaxLabel, words);

	/// <summary>Any player text on a ping (label, the thing hit): one line, no control or direction-changing characters, the
	/// server's words replaced, at most <paramref name="max"/> characters.</summary>
	public static string CleanText(string value, int max, IReadOnlyCollection<string> words = null)
	{
		string text = new string((value ?? "").Where(c => !char.IsControl(c) && !char.IsSurrogate(c)
			&& System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format).ToArray()).Trim();
		if (words != null)
			foreach (string word in words)
				if (!string.IsNullOrWhiteSpace(word)) text = text.Replace(word.Trim(), "***", StringComparison.OrdinalIgnoreCase);
		return text.Length > max ? text[..max].TrimEnd() : text;
	}

	/// <summary>Whether a player sees another player's ping. Players at war never do; the pinger always sees their own. With
	/// the faction audience, players who are both in no faction count as one team (co-op friends who never made one).</summary>
	public static bool Sees(PingAudience audience, PlayerRelation relation, double distanceMeters, double rangeMeters, bool bothFactionless = false)
	{
		if (relation == PlayerRelation.Own) return true;
		if (relation is PlayerRelation.War or PlayerRelation.Npc or PlayerRelation.None) return false;
		if (bothFactionless && relation == PlayerRelation.Neutral && audience is PingAudience.Faction or PingAudience.Allies or PingAudience.Range) return true;
		return audience switch
		{
			PingAudience.Faction => relation == PlayerRelation.Faction,
			PingAudience.Allies => relation is PlayerRelation.Faction or PlayerRelation.Allied,
			PingAudience.Range => relation is PlayerRelation.Faction || distanceMeters <= rangeMeters,
			_ => true
		};
	}

	public static PingRelation RelationFor(PlayerRelation relation, bool admin)
		=> admin ? PingRelation.Admin : relation switch
		{
			PlayerRelation.Own => PingRelation.Own,
			PlayerRelation.Faction => PingRelation.Faction,
			PlayerRelation.Allied => PingRelation.Ally,
			_ => PingRelation.Neutral
		};

	public static bool TryAudience(string word, out PingAudience audience)
	{
		audience = PingAudience.Faction;
		return word != null && !int.TryParse(word, out _) && Enum.TryParse(word.Trim(), true, out audience) && Enum.IsDefined(audience);
	}

	public static bool TryMode(string word, out MarkerMode mode)
	{
		mode = MarkerMode.Relevant;
		string w = (word ?? "").Trim().Replace(" ", "").Replace("-", "").Replace("_", "");
		if (w.Equals("pings", StringComparison.OrdinalIgnoreCase)) w = "PingsOnly";
		return w.Length > 0 && !int.TryParse(w, out _) && Enum.TryParse(w, true, out mode) && Enum.IsDefined(mode);
	}

	public static bool TryCategory(string word, out MarkerCategory category)
	{
		category = MarkerCategory.Pings;
		string w = (word ?? "").Trim();
		if (w.Equals("nametags", StringComparison.OrdinalIgnoreCase)) w = "Players";
		if (w.Equals("antennas", StringComparison.OrdinalIgnoreCase)) w = "Signals";
		return w.Length > 0 && !int.TryParse(w, out _) && Enum.TryParse(w, true, out category) && Enum.IsDefined(category);
	}

	public static string ModeText(MarkerMode mode) => mode switch { MarkerMode.All => "All", MarkerMode.PingsOnly => "Pings only", MarkerMode.Off => "Off", _ => "Relevant" };

	public static double Distance((double X, double Y, double Z) a, (double X, double Y, double Z) b)
	{
		double x = a.X - b.X, y = a.Y - b.Y, z = a.Z - b.Z;
		return Math.Sqrt(x * x + y * y + z * z);
	}

	public static string DistanceText(double m) => !double.IsFinite(m) || m < 0 ? "" : m >= 1000 ? (m / 1000).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " km" : m.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " m";

	/// <summary>The glyph file name for a kind (Content/Textures/GUI/Icons/SE2Online/Markers/&lt;name&gt;.png), plus "dot".</summary>
	public static string Glyph(PingKind kind) => kind.ToString().ToLowerInvariant();

	public static string KindText(PingKind kind) => kind switch
	{
		PingKind.Rock => "Rock", PingKind.Block => "Block", PingKind.Ship => "Ship", PingKind.Base => "Base", PingKind.Player => "Player",
		PingKind.Danger => "Danger", PingKind.Go => "Go", PingKind.Regroup => "Regroup", PingKind.Mine => "Mine", PingKind.Build => "Build",
		PingKind.Look => "Look", _ => "Point"
	};

	/// <summary>The marker category a ping kind's own markers fall under (always Pings) and nametags (Players).</summary>
	public static MarkerCategory CategoryOfNametag => MarkerCategory.Players;
}

/// <summary>
/// The per-player ping rate limit: a burst of <see cref="Burst"/> pings, then one every <see cref="RefillSeconds"/>.
/// Thread-safe (the net thread asks).
/// </summary>
public sealed class PingLimiter
{
	private readonly Dictionary<ulong, (double Tokens, long At)> _buckets = new();
	public int Burst { get; set; } = 3;
	public double RefillSeconds { get; set; } = 5;

	public bool Allow(ulong player, long nowMs)
	{
		lock (_buckets)
		{
			int burst = Math.Max(1, Burst);
			var (tokens, at) = _buckets.TryGetValue(player, out var b) ? b : (burst, nowMs);
			tokens = Math.Min(burst, tokens + Math.Max(0, nowMs - at) / 1000.0 / Math.Max(0.1, RefillSeconds));
			bool ok = tokens >= 1;
			_buckets[player] = (ok ? tokens - 1 : tokens, nowMs);
			if (_buckets.Count > 4096) foreach (var stale in _buckets.Where(kv => nowMs - kv.Value.At > 600_000).Select(kv => kv.Key).ToList()) _buckets.Remove(stale);
			return ok;
		}
	}
}
