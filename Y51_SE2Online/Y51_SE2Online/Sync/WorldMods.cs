using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Y51_SE2Online.Net;

namespace Y51_SE2Online.Sync;

/// <summary>A mod the world runs. WorkshopId 0 is a local mod that joiners cannot download; Version is empty when the mod has none.</summary>
public sealed record WorldMod(Guid Project, ulong WorkshopId, string Name, string Version)
{
	public string Describe() => (string.IsNullOrWhiteSpace(Name) ? Project.ToString() : Name) + (WorkshopId != 0 ? $" ({WorkshopId})" : " (local)");
}

public static class WorldMods
{
	public const int MaxMods = 100;

	public const uint SteamAppId = 1133870u;

	public static void Write(WireWriter w, IReadOnlyList<WorldMod> mods)
	{
		mods ??= Array.Empty<WorldMod>();
		w.U8((byte)Math.Min(mods.Count, MaxMods));
		foreach (WorldMod mod in mods.Take(MaxMods))
		{
			w.Bytes(mod.Project.ToByteArray()).U64(mod.WorkshopId).Str(Clean(mod.Name, 120)).Str(Clean(mod.Version, 40));
		}
	}

	/// <summary>Reads what <see cref="Write"/> wrote; the host is not trusted, so names are cleaned and versions must parse.</summary>
	public static List<WorldMod> Read(ref WireReader r)
	{
		var list = new List<WorldMod>();
		if (r.Remaining < 1) return list;
		int count = r.U8();
		if (count > MaxMods) throw new FormatException($"the host listed {count} mods; at most {MaxMods} are allowed");
		for (int i = 0; i < count; i++)
		{
			var project = new Guid(r.Bytes(16));
			ulong workshop = r.U64();
			string name = Clean(r.Str(), 120);
			string version = Clean(r.Str(), 40);
			if (project == Guid.Empty) throw new FormatException("the host listed a mod without a project id");
			if (version.Length > 0 && !System.Version.TryParse(version, out _)) throw new FormatException($"the host listed mod '{name}' with version '{version}'");
			if (list.Any(m => m.Project == project)) continue;
			list.Add(new WorldMod(project, workshop, name, version));
		}
		return list;
	}

	/// <summary>A Workshop item id from a number, a numeric string or a Workshop page link (…?id=123).</summary>
	public static bool TryParseWorkshopId(string text, out ulong id)
	{
		id = 0;
		text = (text ?? "").Trim();
		int at = text.IndexOf("id=", StringComparison.OrdinalIgnoreCase);
		if (at >= 0)
		{
			text = text[(at + 3)..];
			int end = text.IndexOfAny(new[] { '&', '#', '/' });
			if (end >= 0) text = text[..end];
		}
		return ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id != 0;
	}

	/// <summary>Workshop ids from a dedicated config, in order, without duplicates; throws on anything unusable.</summary>
	public static ulong[] ParseWorkshopIds(IEnumerable<string> entries)
	{
		var ids = new List<ulong>();
		foreach (string entry in entries ?? Array.Empty<string>())
		{
			if (!TryParseWorkshopId(entry, out ulong id)) throw new ArgumentException($"Mods: '{entry}' is not a Steam Workshop item id or link.");
			if (!ids.Contains(id)) ids.Add(id);
		}
		if (ids.Count > MaxMods) throw new ArgumentException($"Mods: at most {MaxMods} Workshop items.");
		return ids.ToArray();
	}

	/// <summary>The world's own mods followed by the added ones; a project listed twice keeps the added (configured) entry.</summary>
	public static List<WorldMod> Merge(IEnumerable<WorldMod> world, IEnumerable<WorldMod> added)
	{
		var result = new List<WorldMod>();
		var extra = (added ?? Array.Empty<WorldMod>()).ToList();
		foreach (WorldMod mod in world ?? Array.Empty<WorldMod>())
		{
			if (extra.Any(a => a.Project == mod.Project || (a.WorkshopId != 0 && a.WorkshopId == mod.WorkshopId))) continue;
			if (result.All(r => r.Project != mod.Project)) result.Add(mod);
		}
		foreach (WorldMod mod in extra)
		{
			if (result.All(r => r.Project != mod.Project)) result.Add(mod);
		}
		return result;
	}

	/// <summary>
	/// An A2S_RULES reply: the rules, or null with the challenge to ask again with. Anything else is null too, a split
	/// reply included (SE2 Online's rules fit one packet).
	/// </summary>
	public static Dictionary<string, string>? ParseA2sRules(byte[] reply, out byte[]? challenge)
	{
		challenge = null;
		if (reply.Length < 5 || BitConverter.ToInt32(reply, 0) != -1) return null;
		if (reply[4] == (byte)'A')
		{
			if (reply.Length >= 9) challenge = reply[5..9];
			return null;
		}
		if (reply[4] != (byte)'E' || reply.Length < 7) return null;
		int count = reply[5] | reply[6] << 8, at = 7;
		var rules = new Dictionary<string, string>();
		for (int i = 0; i < count; i++)
		{
			if (CString(reply, ref at) is not { } key || CString(reply, ref at) is not { } value) return null;
			rules[key] = value;
		}
		return rules;
	}

	public const int MaxSplitPackets = 16;

	/// <summary>
	/// A split A2S reply (Source format: id, total, number, size, then the part) joined once every part of the reply the
	/// last packet belongs to is in; null while parts are missing, and for compressed or malformed parts.
	/// </summary>
	public static byte[]? JoinSplit(IReadOnlyList<byte[]> packets)
	{
		if (packets.Count == 0 || packets[^1].Length < 12) return null;
		int id = BitConverter.ToInt32(packets[^1], 4), total = packets[^1][8];
		if (id < 0 || total is 0 or > MaxSplitPackets) return null;
		var parts = new byte[]?[total];
		foreach (byte[] packet in packets)
		{
			if (packet.Length < 12 || BitConverter.ToInt32(packet, 4) != id) continue;
			if (packet[8] != total || packet[9] >= total) return null;
			parts[packet[9]] = packet[12..];
		}
		return parts.Any(p => p == null) ? null : parts.SelectMany(p => p!).ToArray();
	}

	private static string? CString(byte[] bytes, ref int at)
	{
		int end = Array.IndexOf(bytes, (byte)0, at);
		if (end < 0) return null;
		string text = System.Text.Encoding.UTF8.GetString(bytes, at, end - at);
		at = end + 1;
		return text;
	}

	public static string Summary(IReadOnlyList<WorldMod> mods) =>
		mods == null || mods.Count == 0 ? "None" : string.Join(", ", mods.Select(m => m.Describe()));

	private static string Clean(string s, int max)
	{
		s = new string((s ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
		return s.Length > max ? s[..max] : s;
	}
}
