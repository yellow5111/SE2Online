using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Y51_SE2Online.Sync;

/// <summary>One fact on the admin panel's More info view; <paramref name="Copy"/> gives it a copy button (ids, positions).</summary>
public sealed record DetailLine(string Label, string Value, bool Copy = false);
public sealed record DetailSection(string Title, DetailLine[] Lines);

/// <summary>One grid group a player owns, as the server's world thread saw it (plain values, so the answer is tested without a game).</summary>
public sealed record OwnedGrid(uint NetId, string Name, int Blocks, int Pcu, int Grids, bool Static, bool Powered, bool Moving, Vec3D At, string Place, string Cleanup, bool Hacked);

/// <summary>
/// The answer to <see cref="AdminAction.PlayerInfo"/>: built on the server from what it knows about the account (session,
/// group, moderation) and what the world holds (character, inventory, faction, PCU, grids), sent only to the admin who
/// asked as an <see cref="AdminQuery"/> of <see cref="Kind"/>. The grids come a page at a time, biggest first.
/// </summary>
public static class PlayerDetails
{
	public const string Kind = "player";
	public const int PageSize = 25, TopGrids = 5;
	private const int MaxValue = 1000, MaxLines = 60;

	/// <summary>The first row of the page holding <paramref name="offset"/>; the last page when it is past the end.</summary>
	public static int PageStart(int offset, int total) => total <= 0 ? 0 : Math.Clamp(offset, 0, total - 1) / PageSize * PageSize;

	public static AdminQuery Build(ulong steam, string name, IEnumerable<DetailSection> sections, IReadOnlyCollection<OwnedGrid> grids, Vec3D? from, int offset)
	{
		name = SocialState.Clean(name, 64);
		var sorted = grids.OrderByDescending(g => g.Blocks).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.NetId).ToList();
		int start = PageStart(offset, sorted.Count);
		var page = sorted.Skip(start).Take(PageSize)
			.Select(g => new QueryRow(g.NetId, SocialState.Clean(g.Name, 64), SocialState.Clean(g.Place, 64), g.Blocks, from is Vec3D at ? Math.Round((g.At - at).Length) : -1, Note(g))).ToArray();
		var summary = new DetailSection("GRIDS", sorted.Count == 0 ? [new DetailLine("Grid groups", "none")] :
		[
			new DetailLine("Grid groups", N(sorted.Count) + (sorted.Sum(g => g.Grids) is int all && all > sorted.Count ? $" ({N(all)} grids with subgrids)" : "")),
			new DetailLine("Blocks", N(sorted.Sum(g => g.Blocks))),
			new DetailLine("PCU in these grids", N(sorted.Sum(g => g.Pcu))),
			new DetailLine("Static / ships", $"{N(sorted.Count(g => g.Static))} / {N(sorted.Count(g => !g.Static))}"),
			new DetailLine("Powered", N(sorted.Count(g => g.Powered))),
			new DetailLine("Moving now", N(sorted.Count(g => g.Moving))),
			new DetailLine("Top by PCU", string.Join("; ", sorted.OrderByDescending(g => g.Pcu).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase).Take(TopGrids)
				.Select(g => $"{SocialState.Clean(g.Name, 40)} {N(g.Pcu)}"))),
		]);
		return new AdminQuery
		{
			Kind = Kind, Target = steam, Rows = page, Total = sorted.Count, Offset = start,
			Sections = sections.Append(summary).Select(Clean).ToArray(),
			Text = sorted.Count == 0 ? $"{name} owns no grids." : $"{name} owns {N(sorted.Count)} grid group{(sorted.Count == 1 ? "" : "s")}; {start + 1}-{start + page.Length} shown, biggest first."
		};
	}

	public static string Note(OwnedGrid g)
		=> $"{N(g.Pcu)} PCU, {(g.Static ? "static" : "ship")}{(g.Grids > 1 ? $", {g.Grids} grids" : "")}, {(g.Powered ? "powered" : "no power")}{(g.Moving ? ", moving" : "")}{(g.Hacked ? ", being hacked" : "")}"
			+ $", at {Position(g.At)}{(string.IsNullOrWhiteSpace(g.Place) ? " (space)" : " (" + SocialState.Clean(g.Place, 64) + ")")}; cleanup: {SocialState.Clean(g.Cleanup, 160)}";

	/// <summary>The answer as text for players info in chat, RCON and the console: the GRIDS summary first (count and the
	/// biggest by PCU, so it is inside the 20 lines chat shows), then every other section on lines of at most a chat line,
	/// then <paramref name="maxGrids"/> grids from grid <paramref name="from"/> on (RCON and the console get all of it).</summary>
	public static string Text(AdminQuery q, int from = 0, int maxGrids = 8)
	{
		var lines = new List<string>();
		foreach (var s in q.Sections.OrderBy(x => x.Title == "GRIDS" ? 0 : 1))
		{
			string line = s.Title + ":";
			bool first = true;
			foreach (string fact in s.Lines.Select(l => l.Label + " " + l.Value))
			{
				string piece = (first ? " " : "; ") + fact;
				if (!first && line.Length + piece.Length > ChatLog.MaxText) { lines.Add(line); piece = "  " + fact; line = ""; }
				line += piece; first = false;
				while (line.Length > ChatLog.MaxText) { lines.Add(line[..ChatLog.MaxText]); line = "  " + line[ChatLog.MaxText..]; }
			}
			lines.Add(line);
		}
		lines.Add(q.Text);
		int skip = Math.Clamp(from - q.Offset, 0, q.Rows.Length);
		var shown = q.Rows.Skip(skip).Take(maxGrids).ToArray();
		foreach (var r in shown)
		{
			string grid = $"  {r.Name}: {N(r.Blocks)} blocks, {r.Note}" + (r.Distance >= 0 ? $", {N((long)r.Distance)} m away" : "");
			lines.Add(grid.Length > ChatLog.MaxText ? grid[..ChatLog.MaxText] : grid);
		}
		int next = q.Offset + skip + shown.Length;
		if (next < q.Total) lines.Add($"  More: players info <player> {next}, or More info on the admin panel.");
		return string.Join("\n", lines);
	}

	public static string Position(Vec3D p) => p.X.ToString("0", CultureInfo.InvariantCulture) + ", " + p.Y.ToString("0", CultureInfo.InvariantCulture) + ", " + p.Z.ToString("0", CultureInfo.InvariantCulture);

	public static string N(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

	public static string Percent(double value, double max) => max <= 0 ? "?" : (100 * value / max).ToString("0", CultureInfo.InvariantCulture) + " %";

	/// <summary>"3 days 4 h" for a span in milliseconds.</summary>
	public static string Span(long ms) => ms < 60_000 ? Math.Max(0, ms / 1000) + " s" : AccessList.Remaining(ms);

	public static string When(long unixMs, long nowUnixMs)
		=> unixMs <= 0 ? "never" : DateTimeOffset.FromUnixTimeMilliseconds(unixMs).UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC (" + Span(nowUnixMs - unixMs) + " ago)";

	private static DetailSection Clean(DetailSection s)
		=> new(SocialState.Clean(s.Title, 64), (s.Lines ?? []).Where(l => l != null).Take(MaxLines).Select(l => new DetailLine(SocialState.Clean(l.Label, 64), SocialState.Clean(l.Value, MaxValue), l.Copy)).ToArray());
}
