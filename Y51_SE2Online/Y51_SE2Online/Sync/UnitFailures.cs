using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Y51_SE2Online.Sync;

/// <summary>
/// Diagnostic: which replication units a player's game fails to read or apply (pairing failures, apply failures, skips),
/// to find why some replicated values (PCU, toolbars) go stale. The first <see cref="FirstKinds"/> distinct (kind, what)
/// pairs of a session are logged with their detail, then every pair is counted and summed up once a minute.
/// </summary>
public sealed class UnitFailures
{
	public const int FirstKinds = 20;

	public const long SummaryMs = 60000;

	private readonly HashSet<string> _seen = new();

	private readonly Dictionary<string, long> _counts = new();

	private object? _session;

	private long _summaryMs;

	/// <summary>What to log for one failure: its own line (first ones of a session) and/or the minute's summary.</summary>
	public (string? Line, string? Summary) Note(object session, string kind, string what, string detail, long now)
	{
		lock (_seen)
		{
			if (!ReferenceEquals(session, _session))
			{
				_session = session;
				_seen.Clear();
				_counts.Clear();
				_summaryMs = now;
			}
			string key = kind + ": " + what;
			string? line = null, summary = null;
			if (_seen.Count < FirstKinds && _seen.Add(key)) line = key + " (" + detail + ")";
			else _counts[key] = _counts.TryGetValue(key, out long n) ? n + 1 : 1;
			if (now - _summaryMs >= SummaryMs)
			{
				_summaryMs = now;
				if (_counts.Count > 0) summary = string.Join("; ", _counts.OrderByDescending(kv => kv.Value).Select(kv => kv.Key + " x" + kv.Value));
				_counts.Clear();
			}
			return (line, summary);
		}
	}

	private static readonly Regex Frames = new(@"\bat (?:[\w`.]+[.+])?(?<type>[\w`]+)\+Serializer\.|<(?<story>\w+)>d__\d+", RegexOptions.Compiled);

	/// <summary>
	/// From Keen's "Transport unit failed network pairing" line (its exception in full): the units being read when the
	/// reference failed, outermost first (generated serializers and story state machines in the stack), and the reference
	/// that did not resolve. What is stable across occurrences (numbers become #); the detail keeps them.
	/// </summary>
	public static (string What, string Detail) DescribePairing(string message)
	{
		const string Marker = "NoMatchingLocalPairingException: ";
		int at = message.IndexOf(Marker, StringComparison.Ordinal);
		string reason = at < 0 ? "?" : FirstLine(message.Substring(at + Marker.Length));
		var chain = new List<string>();
		foreach (Match m in Frames.Matches(message))
		{
			string name = m.Groups["type"].Success ? m.Groups["type"].Value : m.Groups["story"].Value + " story";
			if (chain.Count == 0 || chain[^1] != name) chain.Add(name);
		}
		chain.Reverse();
		var distinct = chain.Distinct().ToList();
		string units = distinct.Count == 0 ? "unit" : string.Join(" > ", distinct.Skip(Math.Max(0, distinct.Count - 4)));
		return (units + ": " + Regex.Replace(Kind(reason), @"\d+", "#"), reason);
	}

	// The reason without what names the instance: "Remote pairing 1234 is not known to us" stays, "... for remote
	// entity: <entity dump>" is cut at the colon.
	private static string Kind(string reason)
	{
		int colon = reason.IndexOf(": ", StringComparison.Ordinal);
		return colon > 0 ? reason.Substring(0, colon) : reason;
	}

	private static string FirstLine(string text)
	{
		int end = text.IndexOfAny(new[] { '\r', '\n' });
		return (end < 0 ? text : text.Substring(0, end)).Trim();
	}
}
