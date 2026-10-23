using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using HarmonyLib;
using Keen.Game2;
using Keen.Game2.Game.SessionComponents;
using Keen.Game2.Simulation.GameSystems.GameTimes;
using Keen.VRage.Library.Utils;
using Y51_SE2Online.Core;
using Y51_SE2Online.NetHost;

namespace Y51_SE2Online.Dedicated;

/// <summary>
/// Headless server speed: missed ticks are caught up (see <see cref="TickPacer"/>), Keen's worker pool uses every core,
/// the process runs above normal priority, and a 30 s line reports world time against the wall clock.
/// </summary>
internal static class ServerPerf
{
	private static WaitForTargetFrameRate? _waiter;
	private static long _backlogFrames;
	private static long _droppedTicks, _caughtUpTicks, _lastRun;

	private static readonly AccessTools.FieldRef<WaitForTargetFrameRate, long> Target = AccessTools.FieldRefAccess<WaitForTargetFrameRate, long>("_targetTicks");
	private static readonly AccessTools.FieldRef<WaitForTargetFrameRate, AppTimer> Timer = AccessTools.FieldRefAccess<WaitForTargetFrameRate, AppTimer>("_timer");
	private static readonly AccessTools.FieldRef<WaitForTargetFrameRate, TimeSpan> Delay = AccessTools.FieldRefAccess<WaitForTargetFrameRate, TimeSpan>("_delta");

	private static int _stallMs, _stallEveryMs;
	private static long _nextStallMs;

	/// <summary>-y51online:frametrace: every server frame with its pacing wait, GC and slowest jobs to frametrace.jsonl.</summary>
	public static bool Trace { get; private set; }

	/// <summary>Time the pacer waited since the last closed frame (timer ticks).</summary>
	private static long _waitTicks;

	private static long _traceAhead, _traceAsk, _traceSlept, _traceSpin, _traceLate, _traceCycles;
	private static double _sleep1Ms, _sysLoad, _cyclesPerMs = 1;

	/// <summary>High-resolution waitable timer for the pacing wait; zero = Thread.Sleep.</summary>
	private static IntPtr _timer;

	/// <summary>How long before the deadline the pacing wait stops sleeping and spins.</summary>
	private static long _spinTicks = Stopwatch.Frequency * 2 / 1000;

	private static ThreadPriority? _loopPriority;

	private static bool _paced;

	private static class Native
	{
		[DllImport("kernel32.dll", SetLastError = true)]
		public static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, IntPtr name, uint flags, uint access);
		[DllImport("kernel32.dll", SetLastError = true)]
		public static extern bool SetWaitableTimer(IntPtr timer, ref long dueTime, int period, IntPtr completion, IntPtr arg, bool resume);
		[DllImport("kernel32.dll", SetLastError = true)]
		public static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
		[DllImport("kernel32.dll")]
		public static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
		[DllImport("kernel32.dll")]
		public static extern bool QueryThreadCycleTime(IntPtr thread, out ulong cycles);
		[DllImport("kernel32.dll")]
		public static extern IntPtr GetCurrentThread();
		[DllImport("kernel32.dll")]
		public static extern uint GetCurrentProcessorNumber();
	}

	private static ulong Cycles() => Native.QueryThreadCycleTime(Native.GetCurrentThread(), out ulong c) ? c : 0;

	public static void Install(Harmony harmony, string[] args)
	{
		var config = DedicatedRuntime.Config;
		Trace = args.Contains(MpOptions.Prefix + "frametrace");
		if (Trace)
		{
			if (!ServerProfiler.Recording) ServerProfiler.StartRecording(System.IO.Path.Combine(config.Directory, "frametrace.jsonl"), GameLog.Instance);
			JobProfiler.Install(harmony);
			new Thread(SampleSleep) { IsBackground = true, Name = "Y51_SE2Online sleep probe" }.Start();
		}
		// Launch switches override server.json for A/B runs: -y51online:precisetimer:off|on, -y51online:looppriority:<level>.
		bool precise = Arg(args, "precisetimer:") is { } p ? p != "off" : config.PreciseTimer;
		string priority = Arg(args, "looppriority:") ?? config.LoopPriority;
		if (precise)
		{
			// Thread.Sleep wake-ups ran 10-60 ms late now and then on a lightly loaded host; this timer does not, and it
			// needs no system-wide timer resolution (Windows 10 1803+; where it cannot be created, Thread.Sleep stays).
			try { _timer = Native.CreateWaitableTimerExW(IntPtr.Zero, IntPtr.Zero, 2 /* HIGH_RESOLUTION */, 0x1F0003); }
			catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException) { }
			if (_timer != IntPtr.Zero) _spinTicks = Stopwatch.Frequency / 1000;
			else GameLog.Instance.Warn("dedicated: no high-resolution timer on this system; ticks wait with Thread.Sleep");
		}
		_loopPriority = TickPacer.LoopPriority(priority);
		_paced = config.CatchUpMs > 0;
		if (_paced)
			harmony.Patch(AccessTools.Method(typeof(WaitForTargetFrameRate), nameof(WaitForTargetFrameRate.Wait)),
				prefix: new HarmonyMethod(typeof(ServerPerf), nameof(WaitPrefix)));
		try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.AboveNormal; }
		catch (Exception ex) { GameLog.Instance.Warn("dedicated: could not raise the process priority: " + ex.Message); }
		// Test rig: -y51online:perf-stall:500/5000 blocks the server frame 500 ms every 5 s.
		string stall = args.FirstOrDefault(a => a.StartsWith(MpOptions.Prefix + "perf-stall:", StringComparison.Ordinal));
		if (stall != null && stall.Substring((MpOptions.Prefix + "perf-stall:").Length).Split('/') is [var ms, var every]
			&& int.TryParse(ms, out _stallMs) && int.TryParse(every, out _stallEveryMs) && _stallMs > 0 && _stallEveryMs > 0)
			GameLog.Instance.Warn($"dedicated: test stall: the server frame blocks {_stallMs} ms every {_stallEveryMs} ms");
	}

	private static string? Arg(string[] args, string name) =>
		args.FirstOrDefault(a => a.StartsWith(MpOptions.Prefix + name, StringComparison.OrdinalIgnoreCase))?.Substring(MpOptions.Prefix.Length + name.Length);

	/// <summary>From the headless engine composition: the main loop's pacer and Keen's worker pool.</summary>
	public static void Configure(AppLoop loop, Keen.VRage.Core.EngineComponents.EngineBuilder engine)
	{
		var config = DedicatedRuntime.Config;
		_waiter = loop.Waiter;
		_backlogFrames = TickPacer.BacklogFrames(config.CatchUpMs, loop.TargetFrequency);
		if (config.WorkerThreads > 0) engine.Configure<Keen.VRage.Core.CoreConfigurations.ThreadingConfigurationObjectBuilder>().WorkerCount = config.WorkerThreads;
		GameLog.Instance.Info($"dedicated: {(config.WorkerThreads > 0 ? config.WorkerThreads.ToString() : "the game's own count of")} worker threads on {Environment.ProcessorCount} logical processors; "
			+ (config.CatchUpMs > 0 ? $"missed ticks are caught up to {config.CatchUpMs} ms behind" : "Keen's own pacing (no catch-up beyond 83 ms)")
			+ (config.CatchUpMs > 0 ? $"; ticks wait on {(_timer != IntPtr.Zero ? "a high-resolution timer" : "Thread.Sleep")}, server loop thread priority {_loopPriority?.ToString() ?? "Normal"}" : ""));
	}

	private static bool WaitPrefix(WaitForTargetFrameRate __instance)
	{
		if (!ReferenceEquals(__instance, _waiter)) return true;
		if (_loopPriority is { } priority)
		{
			_loopPriority = null;
			try { Thread.CurrentThread.Priority = priority; }
			catch (Exception ex) { GameLog.Instance.Warn($"dedicated: could not set the server loop thread to {priority}: {ex.Message}"); }
		}
		AppTimer timer = Timer(__instance);
		TimeSpan delay = Delay(__instance);
		if (delay != TimeSpan.Zero)
		{
			Delay(__instance) = TimeSpan.Zero;
			timer.AddElapsed(-delay);
		}
		long frame = __instance.TicksPerFrame, now = timer.ElapsedTicks;
		long target = TickPacer.NextTarget(Target(__instance), now, frame, _backlogFrames, out long dropped);
		if (dropped > 0) _droppedTicks += dropped;
		if (!__instance.WaitForInterval) target = now;
		Target(__instance) = target;
		if (target < now) _caughtUpTicks++;
		long runAt = __instance.WaitForInterval ? TickPacer.RunAt(target, _lastRun, frame) : now;
		long ahead = runAt - now;
		long entered = Stopwatch.GetTimestamp(), slept = entered, ask = 0;
		if (ahead > 0)
		{
			if (_timer != IntPtr.Zero)
			{
				long due = TickPacer.TimerDue(ahead, _spinTicks, Stopwatch.Frequency);
				ask = -due * Stopwatch.Frequency / 10_000_000;
				// A timer that never fires (a broken emulation) must not stall the server: bounded wait, then Thread.Sleep for good.
				if (due < 0 && (!Native.SetWaitableTimer(_timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false) || Native.WaitForSingleObject(_timer, (uint)(-due / 10_000) + 20) != 0))
				{
					_timer = IntPtr.Zero;
					_spinTicks = Stopwatch.Frequency * 2 / 1000;
					GameLog.Instance.Warn("dedicated: the high-resolution timer failed; ticks wait with Thread.Sleep from now on");
				}
			}
			else
			{
				int sleepMs = (int)(ahead * 1000 / Stopwatch.Frequency) - 2;
				if (sleepMs > 0) { Thread.Sleep(sleepMs); ask = sleepMs * Stopwatch.Frequency / 1000; }
			}
			slept = Stopwatch.GetTimestamp();
			while (timer.ElapsedTicks < runAt) Thread.SpinWait(20);
		}
		_lastRun = Math.Max(now, runAt);
		long left = Stopwatch.GetTimestamp();
		_waitTicks += left - entered;
		if (Trace)
		{
			_traceAhead += Math.Max(0, ahead);
			_traceAsk += Math.Max(0, ask);
			_traceSlept += slept - entered;
			_traceSpin += left - slept;
			_traceLate += Math.Max(0, now - target);
		}
		return false;
	}

	private static void SampleSleep()
	{
		try { SampleSleepCore(); }
		catch (Exception ex) { GameLog.Instance.Warn("frametrace: sleep/load probe stopped: " + ex.Message); }
	}

	private static void SampleSleepCore()
	{
		ulong c0 = Cycles();
		long t0 = Stopwatch.GetTimestamp();
		while (Stopwatch.GetTimestamp() - t0 < Stopwatch.Frequency / 10) { }
		_cyclesPerMs = Math.Max(1, (Cycles() - c0) / ServerProfiler.Ms(Stopwatch.GetTimestamp() - t0));
		Native.GetSystemTimes(out long idle0, out long kernel0, out long user0);
		for (int n = 0; ; n++)
		{
			if (n % 5 == 0)
			{
				long start = Stopwatch.GetTimestamp();
				for (int i = 0; i < 10; i++) Thread.Sleep(1);
				_sleep1Ms = ServerProfiler.Ms(Stopwatch.GetTimestamp() - start) / 10;
			}
			Thread.Sleep(1000);
			Native.GetSystemTimes(out long idle, out long kernel, out long user);
			long total = kernel - kernel0 + user - user0;
			if (total > 0) _sysLoad = 1 - (double)(idle - idle0) / total;
			idle0 = idle; kernel0 = kernel; user0 = user;
		}
	}

	private static int _players;

	private static void TraceFrame(ServerFrame frame, double wait, string jobs)
	{
		if (frame.StartUnixMs / 1000 != (frame.StartUnixMs - (long)frame.FrameMs) / 1000)
		{
			int players = 0;
			if (NetSession.Current is { } net) foreach (var p in net.Players) if (p.Peer.HasValue) players++;
			_players = players;
		}
		ulong cycles = Cycles();
		double cpu = _traceCycles == 0 ? 0 : (cycles - (ulong)_traceCycles) / _cyclesPerMs;
		_traceCycles = (long)cycles;
		ServerProfiler.Json(FormattableString.Invariant(
			$"{{\"t\":{frame.StartUnixMs},\"kind\":\"f\",\"ms\":{frame.FrameMs:0.00},\"busy\":{frame.FrameMs - wait:0.00},\"wait\":{wait:0.00},\"ahead\":{ServerProfiler.Ms(_traceAhead):0.00},\"ask\":{ServerProfiler.Ms(_traceAsk):0.00},\"slept\":{ServerProfiler.Ms(_traceSlept):0.00},\"spin\":{ServerProfiler.Ms(_traceSpin):0.00},\"late\":{ServerProfiler.Ms(_traceLate):0.00},\"tick\":{frame.TickMs:0.00},\"game\":{frame.GameMs:0.00},\"outside\":{frame.OutsideMs:0.00},\"ded\":{frame.SectionMs[(int)ServerSection.Dedicated]:0.00},\"online\":{frame.SectionMs[(int)ServerSection.Online]:0.00},\"gc0\":{frame.Gc0},\"gc1\":{frame.Gc1},\"gc2\":{frame.Gc2},\"gcp\":{frame.GcPauseMs:0.00},\"alloc\":{frame.AllocBytes},\"save\":{(frame.Saving ? 1 : 0)},\"pl\":{_players},\"sleep1\":{_sleep1Ms:0.00},\"cpu\":{cpu:0.00},\"core\":{Native.GetCurrentProcessorNumber()},\"sys\":{_sysLoad:0.00},\"jobs\":{ServerProfiler.Quote(jobs)}}}"));
		_traceAhead = _traceAsk = _traceSlept = _traceSpin = _traceLate = 0;
	}

	/// <summary>Whether the closed frame's slowest jobs are wanted for the trace.</summary>
	public static bool TraceJobs(ServerFrame? frame) => Trace && frame != null && (frame.FrameMs >= 18 || frame.FrameMs - ServerProfiler.Ms(_waitTicks) >= 8);

	private static long _windowStartMs, _windowTicks;
	private static double _windowWorldS, _worstMs, _worstWorkMs;
	private static TimeSpan _windowCpu;

	public static void Tick(GameAppComponent app, ServerFrame? frame, string jobs)
	{
		double wait = ServerProfiler.Ms(_waitTicks);
		_waitTicks = 0;
		if (Trace && frame != null) TraceFrame(frame, wait, jobs);
		long now = MpClock.NowMs();
		if (_stallMs > 0 && app.Server != null)
		{
			if (_nextStallMs == 0) _nextStallMs = now + _stallEveryMs;
			else if (now >= _nextStallMs) { _nextStallMs += _stallEveryMs; Thread.Sleep(_stallMs); }
		}
		if (frame != null)
		{
			_windowTicks++;
			_worstMs = Math.Max(_worstMs, frame.FrameMs);
			_worstWorkMs = Math.Max(_worstWorkMs, frame.FrameMs - wait);
		}
		var clock = app.Server?.Entity.Get<WorldSessionComponent>()?.OwnedSession?.SessionComponents.TryGet<GameTimeSessionComponent>();
		if (clock == null) { _windowStartMs = 0; return; }
		double world = clock.FrameTime.TotalSeconds;
		if (_windowStartMs == 0) { Reset(now, world); return; }
		if (now - _windowStartMs < 30_000) return;
		double wall = (now - _windowStartMs) / 1000.0, advanced = world - _windowWorldS;
		TimeSpan cpu = Process.GetCurrentProcess().TotalProcessorTime;
		// Worst frame is start to start, so a clean 60 Hz window reads 17 ms; the longest work shows what the tick itself took.
		GameLog.Instance.Info(FormattableString.Invariant(
			$"perf: world time +{advanced:0.0} s over {wall:0.0} s wall (sim speed {advanced / wall:0.00}); {_windowTicks} ticks ({_windowTicks / wall:0.0}/s), {_caughtUpTicks} run late to catch up, {ServerProfiler.Ms(_droppedTicks) / 1000:0.0} s of backlog dropped; worst frame {_worstMs:0} ms{(_paced ? FormattableString.Invariant($", longest work {_worstWorkMs:0} ms") : "")}; CPU {(cpu - _windowCpu).TotalSeconds / wall:0.0} of {Environment.ProcessorCount} logical processors"));
		Reset(now, world);
	}

	private static void Reset(long now, double world)
	{
		_windowStartMs = now;
		_windowWorldS = world;
		_windowTicks = _caughtUpTicks = _droppedTicks = 0;
		_worstMs = _worstWorkMs = 0;
		_windowCpu = Process.GetCurrentProcess().TotalProcessorTime;
	}
}
