using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Y51_SE2Online.Sync;

/// <summary>One server setting for the Server info screen; new settings need no protocol change.</summary>
public sealed record ServerInfoRow(string Section, string Name, string Value);

public sealed record ServerInfoRequest(int Version = 1);

public sealed record ServerInfoMessage(ServerInfoRow[] Rows);

public static class ServerInfo
{
	public const int MaxRows = 200;

	/// <summary>Plain text grouped by section; every value is capped and stripped of control characters (it may come from another machine).</summary>
	public static string Text(IEnumerable<ServerInfoRow> rows)
	{
		var sb = new StringBuilder();
		foreach (var section in (rows ?? Array.Empty<ServerInfoRow>()).Where(r => r != null).Take(MaxRows).GroupBy(r => Clean(r.Section, 40)))
		{
			if (sb.Length > 0) sb.Append('\n');
			sb.Append(section.Key.ToUpperInvariant()).Append('\n');
			foreach (var row in section) sb.Append(Clean(row.Name, 40)).Append(": ").Append(Clean(row.Value, 300)).Append('\n');
		}
		return sb.ToString().TrimEnd();
	}

	public static string Clean(string s, int max)
	{
		s = SocialState.Clean(s ?? "", max);
		return s.Length == 0 ? "-" : s;
	}

	public static string OnOff(bool value) => value ? "On" : "Off";

	public static string Duration(TimeSpan span) => span.TotalDays >= 1 ? $"{(int)span.TotalDays} d {span.Hours} h" : span.TotalHours >= 1 ? $"{(int)span.TotalHours} h {span.Minutes} min" : $"{Math.Max(0, span.Minutes)} min";
}
