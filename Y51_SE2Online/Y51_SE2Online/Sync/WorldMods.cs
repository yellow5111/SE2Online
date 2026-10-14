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

	public static string Summary(IReadOnlyList<WorldMod> mods) =>
		mods == null || mods.Count == 0 ? "None" : string.Join(", ", mods.Select(m => m.Describe()));

	private static string Clean(string s, int max)
	{
		s = new string((s ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
		return s.Length > max ? s[..max] : s;
	}
}
