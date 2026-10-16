using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Y51_SE2Online.Sync;

/// <summary>A host Workshop mod this game lacks; Bytes is Steam's size for it, 0 when Steam did not say.</summary>
public sealed record ModDownload(WorldMod Mod, string Title, ulong Bytes);

/// <summary>The limits and wording for the host-mod download a player is asked about before joining.</summary>
public static class ModDownloads
{
	public const ulong MaxItemBytes = 4UL << 30;

	public const int DefaultMaxGiB = 10;

	public const int MaxGiBLimit = 1024;

	public static ulong TotalBytes(IEnumerable<ModDownload> needed) => needed.Aggregate(0UL, (sum, d) => sum + d.Bytes);

	public static ulong CapBytes(int maxGiB) => (ulong)Math.Clamp(maxGiB, 1, MaxGiBLimit) << 30;

	/// <summary>Why the join must not download these, or null when it may.</summary>
	public static string Refusal(IReadOnlyList<ModDownload> needed, int maxGiB)
	{
		foreach (ModDownload d in needed)
		{
			if (d.Bytes > MaxItemBytes) return $"The host's Workshop mod {d.Title} is {Size(d.Bytes)}; at most {Size(MaxItemBytes)} per mod is downloaded.";
		}
		ulong total = TotalBytes(needed);
		return total > CapBytes(maxGiB)
			? $"The host's Workshop mods need {Size(total)}; this game downloads at most {Size(CapBytes(maxGiB))} per server (MaxModDownloadGiB in Y51_SE2Online.settings.json)."
			: null;
	}

	/// <summary>The question shown before downloading: what is missing with sizes and the total, then what is already here.</summary>
	public static string Prompt(IReadOnlyList<ModDownload> needed, IReadOnlyList<string> installed, int shown = 8)
	{
		var lines = new List<string> { $"This server runs {needed.Count} Workshop mod{(needed.Count == 1 ? "" : "s")} you don't have. Download {(needed.Count == 1 ? "it" : "them")} from Steam to join?", "" };
		lines.AddRange(needed.Take(shown).Select(d => $"{d.Title}  /  {(d.Bytes == 0 ? "size unknown" : Size(d.Bytes))}"));
		if (needed.Count > shown) lines.Add($"...and {needed.Count - shown} more");
		bool unknown = needed.Any(d => d.Bytes == 0);
		lines.Add("");
		lines.Add($"Total: {Size(TotalBytes(needed))}{(unknown ? " plus items Steam gave no size for" : "")}");
		if (installed.Count > 0) lines.Add($"Already installed: {string.Join(", ", installed.Take(shown))}{(installed.Count > shown ? $" and {installed.Count - shown} more" : "")}");
		return string.Join("\n", lines);
	}

	public static string Size(ulong bytes) => bytes >= 1UL << 30
		? (bytes / 1073741824.0).ToString("0.0", CultureInfo.InvariantCulture) + " GiB"
		: (bytes / 1048576.0).ToString(bytes < 10UL << 20 ? "0.0" : "0", CultureInfo.InvariantCulture) + " MiB";
}

/// <summary>Which Workshop mods this player agreed to download for which server, so a rejoin with the same mods doesn't ask again.</summary>
public sealed class ModConsent
{
	public const int MaxServers = 100;

	public sealed class Entry
	{
		public string Server { get; set; } = "";

		public List<ulong> Mods { get; set; } = new();
	}

	/// <summary>Most recently approved last.</summary>
	public List<Entry> Servers { get; set; } = new();

	public bool Covers(string server, IEnumerable<ulong> mods)
	{
		Entry entry = Servers.FirstOrDefault(e => e.Server == server);
		return entry != null && mods.All(entry.Mods.Contains);
	}

	public void Approve(string server, IEnumerable<ulong> mods)
	{
		Entry entry = Servers.FirstOrDefault(e => e.Server == server) ?? new Entry { Server = server };
		Servers.Remove(entry);
		Servers.Add(entry);
		entry.Mods = entry.Mods.Concat(mods).Distinct().TakeLast(WorldMods.MaxMods * 2).ToList();
		if (Servers.Count > MaxServers) Servers.RemoveRange(0, Servers.Count - MaxServers);
	}

	public static ModConsent Load(string path)
	{
		try
		{
			var consent = File.Exists(path) ? JsonSerializer.Deserialize<ModConsent>(File.ReadAllText(path)) : null;
			if (consent?.Servers != null && consent.Servers.All(e => e?.Server != null && e.Mods != null)) return consent;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
		return new ModConsent();
	}

	public void Save(string path)
	{
		string tmp = path + ".tmp";
		File.WriteAllText(tmp, JsonSerializer.Serialize(this));
		File.Move(tmp, path, true);
	}
}
