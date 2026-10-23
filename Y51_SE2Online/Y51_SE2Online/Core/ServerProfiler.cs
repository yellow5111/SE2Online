using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Y51_SE2Online.Core;

/// <summary>Where a host's server frame goes; the plugin's own work, everything else is the game.</summary>
public enum ServerSection
{
	/// <summary>Dedicated runtime upkeep at the start of the frame: status file, console commands, save scheduling.</summary>
	Dedicated,
	/// <summary>Walking the frame's replication commands into state entries and encoded signals.</summary>
	Forward,
	/// <summary>Factions, admin, GPS, hacking, water, trades, punches, voice positions, seats, player registration.</summary>
	Social,
	/// <summary>String tables, signal batches and despawns published to the clients.</summary>
	Signals,
	Observables,
	Stories,
	/// <summary>Character spawns, releases and respawn ships.</summary>
	Spawns,
	/// <summary>Taking the clients' input and injecting it into the server.</summary>
	Inputs,
	/// <summary>Per-client character upkeep, player status and the nametag roster.</summary>
	Joiners,
	Streaming,
	/// <summary>World captures for joining clients.</summary>
	Capture,
	/// <summary>Desync detection (state hashes for the clients to compare).</summary>
	Desync,
	/// <summary>SE2 Online's own upkeep on the game's threads: controller, host world, session update work.</summary>
	Online,
	Count
}

/// <summary>One closed server frame. Times in ms; <see cref="TickMs"/> is the game's server tick (dedicated only, else -1).</summary>
public sealed record ServerFrame(long StartUnixMs, double FrameMs, double TickMs, double[] SectionMs, int Gc0, int Gc1, int Gc2, double GcPauseMs, long AllocBytes, bool Saving)
{
	public double PluginMs => SectionMs.Sum();

	/// <summary>The game's own share: its tick minus our work inside it, or the whole frame minus our work without a tick span.</summary>
	public double GameMs => TickMs >= 0 ? Math.Max(0, TickMs - (PluginMs - SectionMs[(int)ServerSection.Dedicated])) : Math.Max(0, FrameMs - PluginMs);

	/// <summary>Frame time outside the game tick and our upkeep: the engine's other jobs, task continuations, frame pacing.</summary>
	public double OutsideMs => TickMs >= 0 ? Math.Max(0, FrameMs - TickMs - SectionMs[(int)ServerSection.Dedicated]) : 0;

	public string Describe()
	{
		var plugin = Enumerable.Range(0, (int)ServerSection.Count).Where(i => SectionMs[i] >= 0.5).OrderByDescending(i => SectionMs[i])
			.Select(i => $"{ServerProfiler.Name((ServerSection)i)} {SectionMs[i]:0}");
		string parts = TickMs >= 0
			? $"game tick {TickMs:0} ms (game {GameMs:0}), outside the tick {OutsideMs:0} ms"
			: $"game {GameMs:0} ms";
		return FormattableString.Invariant($"{FrameMs:0} ms: {parts}, plugin {PluginMs:0} ms ({(plugin.Any() ? string.Join(", ", plugin) : "nothing over 0.5 ms")}); GC {Gc0} collections ({Gc1} gen1, {Gc2} full) paused {GcPauseMs:0} ms, allocated {MpLogExtensions.Bytes(AllocBytes)}{(Saving ? "; saving" : "")}");
	}
}

/// <summary>
/// Server frame profiler: the plugin's sections are timed with <see cref="Lap"/>, a frame is closed by <see cref="BeginFrame"/>.
/// Callers only add numbers and queue lines; the optional JSONL profile is written by a background thread.
/// </summary>
public static class ServerProfiler
{
	public const double SlowFrameMs = 100;

	/// <summary>Frames at least this long get their own JSONL record.</summary>
	public const double RecordFrameMs = 50;

	public static volatile bool Enabled;

	private static readonly long[] Acc = new long[(int)ServerSection.Count];

	private static long _frameStart, _frameUnixMs, _tickStart, _tickTicks = -1;

	private static int _gc0, _gc1, _gc2;

	private static TimeSpan _gcPause;

	private static long _alloc;

	private static readonly object Sync = new();

	private static Window _status = new(), _second = new();

	private static long _secondStartMs;

	public static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

	public static long Now => Stopwatch.GetTimestamp();

	public static string Name(ServerSection s) => s.ToString().ToLowerInvariant();

	/// <summary>Adds the time since <paramref name="since"/> to the section and returns now, for the next lap.</summary>
	public static long Lap(ServerSection section, long since)
	{
		long now = Stopwatch.GetTimestamp();
		if (Enabled) Interlocked.Add(ref Acc[(int)section], now - since);
		return now;
	}

	public static void TickStart() => _tickStart = Stopwatch.GetTimestamp();

	public static void TickEnd()
	{
		if (_tickStart != 0) _tickTicks = Math.Max(_tickTicks, 0) + Stopwatch.GetTimestamp() - _tickStart;
		_tickStart = 0;
	}

	/// <summary>Closes the running frame (null for the first) and starts the next. Call once per server frame, from one thread.</summary>
	public static ServerFrame? BeginFrame(bool saving)
	{
		long now = Stopwatch.GetTimestamp();
		long unix = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		int gc0 = GC.CollectionCount(0), gc1 = GC.CollectionCount(1), gc2 = GC.CollectionCount(2);
		TimeSpan pause = GC.GetTotalPauseDuration();
		long alloc = GC.GetTotalAllocatedBytes(false);
		ServerFrame? frame = null;
		if (Enabled && _frameStart != 0)
		{
			var sections = new double[(int)ServerSection.Count];
			for (int i = 0; i < sections.Length; i++) sections[i] = Ms(Interlocked.Exchange(ref Acc[i], 0));
			frame = new ServerFrame(_frameUnixMs, Ms(now - _frameStart), _tickTicks < 0 ? -1 : Ms(_tickTicks), sections,
				gc0 - _gc0, gc1 - _gc1, gc2 - _gc2, (pause - _gcPause).TotalMilliseconds, alloc - _alloc, saving);
			Add(frame);
		}
		else
		{
			for (int i = 0; i < Acc.Length; i++) Interlocked.Exchange(ref Acc[i], 0);
		}
		_frameStart = now;
		_frameUnixMs = unix;
		_tickTicks = -1;
		_gc0 = gc0; _gc1 = gc1; _gc2 = gc2; _gcPause = pause; _alloc = alloc;
		return frame;
	}

	private static void Add(ServerFrame frame)
	{
		Window? second = null;
		lock (Sync)
		{
			_status.Add(frame);
			_second.Add(frame);
			if (_secondStartMs == 0) _secondStartMs = frame.StartUnixMs;
			if (frame.StartUnixMs - _secondStartMs >= 1000)
			{
				LastSecond = second = _second;
				_second = new Window();
				_secondStartMs = frame.StartUnixMs;
			}
		}
		if (!Recording) return;
		if (frame.FrameMs >= RecordFrameMs) Json(FrameJson(frame));
		if (second != null) Json(second.Json("second", frame.StartUnixMs));
	}

	/// <summary>The newest complete one-second window (read-only once published), for the F11 page.</summary>
	public static volatile Window? LastSecond;

	/// <summary>Everything since the previous call (the 30 s status cadence).</summary>
	public static Window TakeWindow()
	{
		lock (Sync)
		{
			var w = _status;
			_status = new Window();
			return w;
		}
	}

	public static void Reset()
	{
		lock (Sync)
		{
			_status = new Window();
			_second = new Window();
			_secondStartMs = 0;
		}
		_frameStart = 0;
		_tickTicks = -1;
	}

	/// <summary>Frames aggregated over a period.</summary>
	public sealed class Window
	{
		private readonly List<float> _frames = new();

		private readonly double[] _sections = new double[(int)ServerSection.Count];

		public int Frames => _frames.Count;

		public double FrameSumMs, GameSumMs, OutsideSumMs, GcPauseMs;

		public int Slow, Gc0, Gc2;

		public long AllocBytes;

		/// <summary>Where the time of the slow frames went, summed.</summary>
		public double SlowGameMs, SlowOutsideMs, SlowPluginMs, SlowGcMs;

		public int SlowSaving;

		public ServerFrame? Worst;

		public void Add(ServerFrame f)
		{
			_frames.Add((float)f.FrameMs);
			FrameSumMs += f.FrameMs;
			GameSumMs += f.GameMs;
			OutsideSumMs += f.OutsideMs;
			GcPauseMs += f.GcPauseMs;
			Gc0 += f.Gc0;
			Gc2 += f.Gc2;
			AllocBytes += f.AllocBytes;
			for (int i = 0; i < _sections.Length; i++) _sections[i] += f.SectionMs[i];
			if (f.FrameMs >= SlowFrameMs)
			{
				Slow++;
				SlowGameMs += f.GameMs;
				SlowOutsideMs += f.OutsideMs;
				SlowPluginMs += f.PluginMs;
				SlowGcMs += f.GcPauseMs;
				if (f.Saving) SlowSaving++;
			}
			if (Worst == null || f.FrameMs > Worst.FrameMs) Worst = f;
		}

		public double SectionMs(ServerSection s) => _sections[(int)s];

		public double PluginMs => _sections.Sum();

		public double Percentile(double p)
		{
			if (_frames.Count == 0) return 0;
			var sorted = _frames.ToArray();
			Array.Sort(sorted);
			return sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];
		}

		public string Describe()
		{
			if (Frames == 0) return "no server frames";
			var top = Enumerable.Range(0, _sections.Length).Where(i => _sections[i] / Frames >= 0.01).OrderByDescending(i => _sections[i])
				.Select(i => $"{Name((ServerSection)i)} {_sections[i] / Frames:0.00}");
			string slow = Slow == 0 ? "" : $"; {Slow} slow (>= {SlowFrameMs:0} ms): game {SlowGameMs / Slow:0}, outside the tick {SlowOutsideMs / Slow:0}, plugin {SlowPluginMs / Slow:0}, GC pause {SlowGcMs / Slow:0} ms each{(SlowSaving > 0 ? $", {SlowSaving} while saving" : "")}; worst {Worst!.Describe()}";
			return FormattableString.Invariant($"{Frames} frames, {Frames * 1000.0 / Math.Max(1, FrameSumMs):0.0}/s, p50 {Percentile(0.5):0.0} p95 {Percentile(0.95):0.0} p99 {Percentile(0.99):0.0} max {Percentile(1):0} ms; per frame game {GameSumMs / Frames:0.00} outside {OutsideSumMs / Frames:0.00} plugin {PluginMs / Frames:0.00} ms ({(top.Any() ? string.Join(", ", top) : "all under 0.01")}); GC {Gc0} collections ({Gc2} full) paused {GcPauseMs:0} ms, allocated {MpLogExtensions.Bytes(AllocBytes)}{slow}");
		}

		public string Json(string kind, long unixMs)
		{
			var sb = new StringBuilder(512);
			sb.Append(FormattableString.Invariant($"{{\"t\":{unixMs},\"kind\":\"{kind}\",\"frames\":{Frames},\"p50\":{Percentile(0.5):0.0},\"p95\":{Percentile(0.95):0.0},\"p99\":{Percentile(0.99):0.0},\"max\":{Percentile(1):0.0},\"sum\":{FrameSumMs:0.0},\"game\":{GameSumMs:0.0},\"outside\":{OutsideSumMs:0.0},\"gc0\":{Gc0},\"gc2\":{Gc2},\"gcPause\":{GcPauseMs:0.0},\"alloc\":{AllocBytes},\"slow\":{Slow}"));
			for (int i = 0; i < _sections.Length; i++) sb.Append(FormattableString.Invariant($",\"{Name((ServerSection)i)}\":{_sections[i]:0.00}"));
			sb.Append(FormattableString.Invariant($",\"heap\":{GC.GetTotalMemory(false)},\"ws\":{Environment.WorkingSet}}}"));
			return sb.ToString();
		}
	}

	private static string FrameJson(ServerFrame f)
	{
		var sb = new StringBuilder(384);
		sb.Append(FormattableString.Invariant($"{{\"t\":{f.StartUnixMs},\"kind\":\"frame\",\"ms\":{f.FrameMs:0.0},\"tick\":{f.TickMs:0.0},\"game\":{f.GameMs:0.0},\"outside\":{f.OutsideMs:0.0},\"gc0\":{f.Gc0},\"gc1\":{f.Gc1},\"gc2\":{f.Gc2},\"gcPause\":{f.GcPauseMs:0.0},\"alloc\":{f.AllocBytes},\"saving\":{(f.Saving ? "true" : "false")}"));
		for (int i = 0; i < f.SectionMs.Length; i++) sb.Append(FormattableString.Invariant($",\"{Name((ServerSection)i)}\":{f.SectionMs[i]:0.00}"));
		return sb.Append('}').ToString();
	}

	private static BlockingCollection<string>? _queue;

	private static long _dropped;

	public static bool Recording => _queue != null;

	public static string? RecordPath { get; private set; }

	/// <summary>Appends JSON lines to <paramref name="path"/> from a background thread until the process exits.</summary>
	public static void StartRecording(string path, IMpLog log)
	{
		if (_queue != null) return;
		RecordPath = Path.GetFullPath(path);
		var queue = new BlockingCollection<string>(new ConcurrentQueue<string>(), 20_000);
		var writer = new Thread(() =>
		{
			StreamWriter? w = null;
			foreach (string line in queue.GetConsumingEnumerable())
			{
				try
				{
					w ??= new StreamWriter(new FileStream(RecordPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete), new UTF8Encoding(false));
					w.Write(line);
					w.Write('\n');
					if (queue.Count == 0) w.Flush();
				}
				catch (Exception e) when (e is IOException or UnauthorizedAccessException)
				{
					log.WarnOnce("profile-write", $"profile: writing {RecordPath} failed ({e.Message}); retrying");
					w?.Dispose();
					w = null;
				}
			}
		}) { IsBackground = true, Name = "Y51_SE2Online-Profile" };
		writer.Start();
		_queue = queue;
		log.Info($"profile: recording server frames >= {RecordFrameMs:0} ms, per-second summaries and network samples to {RecordPath}");
	}

	public static void Json(string line)
	{
		var queue = _queue;
		if (queue != null && !queue.TryAdd(line)) Interlocked.Increment(ref _dropped);
	}

	public static long DroppedLines => Interlocked.Read(ref _dropped);

	/// <summary>A JSON string literal (quotes included).</summary>
	public static string Quote(string s)
	{
		var sb = new StringBuilder(s.Length + 2).Append('"');
		foreach (char c in s)
		{
			if (c == '"' || c == '\\') sb.Append('\\').Append(c);
			else if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
			else sb.Append(c);
		}
		return sb.Append('"').ToString();
	}
}
