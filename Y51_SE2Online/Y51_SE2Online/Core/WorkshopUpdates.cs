using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Y51_SE2Online.Core;

/// <summary>
/// Whether a dedicated server's Workshop mods have newer versions than the ones it loaded. Steam's public Web API
/// (ISteamRemoteStorage/GetPublishedFileDetails, no key) gives each item's time_updated; the server compares it with
/// the install time Steam reported when it downloaded the item. A dedicated server only downloads mods at start.
/// </summary>
public static class WorkshopUpdates
{
	public const string DetailsUrl = "https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/";

	/// <summary>The API's own limit per request is far above this; SE2 Online worlds carry at most 100 mods.</summary>
	public const int MaxIds = 100;

	/// <summary><paramref name="Content"/>: the item's content manifest (hcontent_file); it changes with the files, unlike
	/// time_updated, which also moves when the author edits the title, description or preview.</summary>
	public readonly record struct Detail(ulong Id, int Result, long TimeUpdated, string Title, string Content = "");

	/// <summary>The form fields of one request.</summary>
	public static List<KeyValuePair<string, string>> Form(IReadOnlyList<ulong> ids)
	{
		var form = new List<KeyValuePair<string, string>> { new("itemcount", ids.Count.ToString(CultureInfo.InvariantCulture)) };
		for (int i = 0; i < ids.Count; i++) form.Add(new($"publishedfileids[{i}]", ids[i].ToString(CultureInfo.InvariantCulture)));
		return form;
	}

	/// <summary>The details per item, or null when the answer is not the API's shape.</summary>
	public static Dictionary<ulong, Detail>? Parse(string json)
	{
		try
		{
			using var doc = JsonDocument.Parse(json);
			if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("response", out var response) || response.ValueKind != JsonValueKind.Object
				|| !response.TryGetProperty("publishedfiledetails", out var items) || items.ValueKind != JsonValueKind.Array) return null;
			var details = new Dictionary<ulong, Detail>();
			foreach (var item in items.EnumerateArray())
			{
				if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("publishedfileid", out var idProp)) continue;
				ulong id = idProp.ValueKind == JsonValueKind.String && ulong.TryParse(idProp.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out ulong s) ? s
					: idProp.ValueKind == JsonValueKind.Number && idProp.TryGetUInt64(out ulong n) ? n : 0;
				if (id == 0) continue;
				int result = item.TryGetProperty("result", out var r) && r.TryGetInt32(out int rv) ? rv : 0;
				long updated = item.TryGetProperty("time_updated", out var t) && t.ValueKind == JsonValueKind.Number && t.TryGetInt64(out long tv) ? tv : 0;
				string title = item.TryGetProperty("title", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() ?? "" : "";
				string content = item.TryGetProperty("hcontent_file", out var c) && c.ValueKind is JsonValueKind.String or JsonValueKind.Number ? c.ToString() : "";
				details[id] = new Detail(id, result, updated, title, content);
			}
			return details;
		}
		catch (JsonException) { return null; }
	}

	/// <summary>
	/// Items with a newer Workshop version than the one installed (result 1 = found). An item the server already restarted
	/// for at that version is left out (<paramref name="restartedFor"/>): Steam handed back a stale copy, and restarting
	/// again would loop.
	/// </summary>
	/// <para><paramref name="installedContent"/>: content manifests known to be the installed copy (<see cref="Learn"/>); an
	/// item whose manifest is still that one only had its page edited and needs no restart.</para>
	public static List<ulong> Changed(IReadOnlyDictionary<ulong, long> installed, IReadOnlyDictionary<ulong, Detail> details, IReadOnlyDictionary<ulong, long> restartedFor,
		IReadOnlyDictionary<ulong, string>? installedContent = null)
	{
		var changed = new List<ulong>();
		foreach (var (id, stamp) in installed)
		{
			if (!Newer(id, stamp, details, installedContent, out var d)) continue;
			if (restartedFor.TryGetValue(id, out long done) && done >= d.TimeUpdated) continue;
			changed.Add(id);
		}
		changed.Sort();
		return changed;
	}

	private static bool Newer(ulong id, long stamp, IReadOnlyDictionary<ulong, Detail> details, IReadOnlyDictionary<ulong, string>? installedContent, out Detail d) =>
		details.TryGetValue(id, out d) && d.Result == 1 && d.TimeUpdated > stamp
		&& !(installedContent != null && d.Content.Length > 0 && installedContent.TryGetValue(id, out string known) && known == d.Content);

	/// <summary>Remembers the content manifest of every item the Workshop has not updated since its install (the copy the
	/// server runs), so a later page edit (new time_updated, same manifest) is told apart from new files.</summary>
	public static void Learn(IReadOnlyDictionary<ulong, long> installed, IReadOnlyDictionary<ulong, Detail> details, IDictionary<ulong, string> installedContent)
	{
		foreach (var (id, stamp) in installed)
			if (!installedContent.ContainsKey(id) && details.TryGetValue(id, out var d) && d.Result == 1 && d.TimeUpdated <= stamp && d.Content.Length > 0)
				installedContent[id] = d.Content;
	}

	/// <summary>Items whose newest version the server restarted for and still does not have (the loop guard caught them).</summary>
	public static List<ulong> Stale(IReadOnlyDictionary<ulong, long> installed, IReadOnlyDictionary<ulong, Detail> details, IReadOnlyDictionary<ulong, long> restartedFor,
		IReadOnlyDictionary<ulong, string>? installedContent = null) =>
		installed.Where(i => Newer(i.Key, i.Value, details, installedContent, out var d) && restartedFor.TryGetValue(i.Key, out long done) && done >= d.TimeUpdated)
			.Select(i => i.Key).OrderBy(i => i).ToList();

	/// <summary>workshop-updates.json beside server.json: the Workshop time each item was last restarted for.</summary>
	public static Dictionary<ulong, long> LoadRestarted(string path)
	{
		try
		{
			if (File.Exists(path) && JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(path)) is { } map)
				return map.Where(kv => ulong.TryParse(kv.Key, NumberStyles.None, CultureInfo.InvariantCulture, out _)).ToDictionary(kv => ulong.Parse(kv.Key, CultureInfo.InvariantCulture), kv => kv.Value);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
		return new Dictionary<ulong, long>();
	}

	public static void SaveRestarted(string path, IReadOnlyDictionary<ulong, long> map)
	{
		string temporary = path + ".tmp";
		File.WriteAllText(temporary, JsonSerializer.Serialize(map.ToDictionary(kv => kv.Key.ToString(CultureInfo.InvariantCulture), kv => kv.Value), new JsonSerializerOptions { WriteIndented = true }));
		File.Move(temporary, path, true);
	}

	public static string Time(long unix) => unix <= 0 ? "unknown" : DateTimeOffset.FromUnixTimeSeconds(unix).ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
}
