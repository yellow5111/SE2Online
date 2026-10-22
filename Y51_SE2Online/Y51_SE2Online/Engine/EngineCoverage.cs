using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.Engine;

/// <summary>
/// What the engine transport actually carried, by kind and name (signal, data type, story, object batch), in each
/// direction: the runtime half of the coverage matrix (the static half is the game's own replication metadata). Written to
/// y51-engine-coverage.csv next to the plugin every minute and when a transport closes. Test rigs only:
/// -y51online:engine-coverage.
/// </summary>
internal static class EngineCoverage
{
	private static readonly ConcurrentDictionary<(string Kind, string Name, string Way), (long Count, long Bytes)> Seen = new();
	private static long _writtenMs;
	internal static readonly bool On = MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "engine-coverage");

	public static void Note(string kind, string name, string way, long bytes = 0)
	{
		if (On) Seen.AddOrUpdate((kind, name, way), (1, bytes), (_, n) => (n.Count + 1, n.Bytes + bytes));
	}

	public static void MaybeWrite(bool force = false)
	{
		if (!On) return;
		long now = MpClock.NowMs();
		if (!force && now - _writtenMs < 60_000) return;
		_writtenMs = now;
		string path = PluginPaths.Data($"y51-engine-coverage-{(MpPlugin.App?.ServerSession != null ? "server" : "client-" + Environment.ProcessId)}.csv");
		var lines = new[] { "kind,name,way,count,bytes" }.Concat(Seen.OrderBy(kv => kv.Key.Kind).ThenBy(kv => kv.Key.Name).Select(kv => $"{kv.Key.Kind},\"{kv.Key.Name}\",{kv.Key.Way},{kv.Value.Count},{kv.Value.Bytes}")).ToArray();
		System.Threading.Tasks.Task.Run(() =>
		{
			try { File.WriteAllLines(path, lines); }
			catch (Exception ex) { GameLog.Instance.WarnOnce("engine-coverage", "engine: coverage file not written: " + ex.Message); }
		});
	}
}
