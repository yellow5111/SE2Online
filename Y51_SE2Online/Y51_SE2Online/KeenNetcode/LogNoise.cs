using System;
using System.Collections.Concurrent;
using System.Linq;
using HarmonyLib;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>
/// The game logs "Type ... is not serializable" as an error every time it interns the name of a [NoSerialize] type, which
/// its out-of-process replication does for each observable UI model batch: thousands per session, all harmless (the models
/// replicate). The first line per type still goes to the log; repeats are counted and summed up every 5 minutes.
/// </summary>
internal static class LogNoise
{
	private const string Head = "Type ", Tail = " is not serializable";
	private const long SummaryMs = 300_000;

	private static readonly ConcurrentDictionary<string, long> Repeats = new();
	private static long _summaryMs;

	public static void Install(Harmony harmony)
	{
		var error = AccessTools.Method(typeof(Keen.VRage.Library.Diagnostics.LogExtensions), nameof(Keen.VRage.Library.Diagnostics.LogExtensions.Error),
			new[] { typeof(Keen.VRage.Library.Diagnostics.Log), typeof(string) }) ?? throw new MissingMethodException("LogExtensions.Error(Log, string)");
		harmony.Patch(error, prefix: new HarmonyMethod(typeof(LogNoise), nameof(ErrorPrefix)));
	}

	private static bool ErrorPrefix(string __1)
	{
		string message = __1;
		if (message == null || !message.StartsWith(Head, StringComparison.Ordinal) || !message.EndsWith(Tail, StringComparison.Ordinal)) return true;
		long count = Repeats.AddOrUpdate(message, 1, (_, n) => n + 1);
		long now = MpClock.NowMs();
		if (now >= _summaryMs)
		{
			bool first = _summaryMs == 0;
			_summaryMs = now + SummaryMs;
			if (!first)
			{
				var top = Repeats.ToArray().OrderByDescending(r => r.Value).Take(4).Select(r => $"{r.Key.Substring(Head.Length, Math.Min(90, r.Key.Length - Head.Length - Tail.Length)).Trim()} x{r.Value}");
				GameLog.Instance.Info($"log noise: the game's \"Type ... is not serializable\" note, {Repeats.Values.Sum()} times so far over {Repeats.Count} type(s), first of each logged: {string.Join("; ", top)}");
			}
		}
		return count == 1;
	}
}
