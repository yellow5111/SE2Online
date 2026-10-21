using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using HarmonyLib;
using Keen.VRage.DCS.Scenes;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.Dedicated;

/// <summary>
/// Which of the game's jobs a slow server frame went to, for the slow-frame warning. Timing every job run costs a few
/// tenths of a ms a frame, so it is installed only once a server keeps having slow frames after its world loaded.
/// Jobs tick nested scenes (the game update job runs the world's whole tick): each one is timed without its children.
/// </summary>
internal static class JobProfiler
{
	private enum Kind : byte { Job, Replay, Async }

	private readonly struct Span(long start, long childTicks)
	{
		public readonly long Start = start, ChildTicks = childTicks;
	}

	private static readonly object Sync = new();

	private static readonly Dictionary<(Scene Scene, int Job, Kind Kind), long> Frame = new();

	[System.ThreadStatic] private static long _childTicks;

	[System.ThreadStatic] private static int _lastJob;

	public static bool Installed { get; private set; }

	private static readonly System.Func<Scene, int, bool> RunJob = AccessTools.MethodDelegate<System.Func<Scene, int, bool>>(AccessTools.Method(typeof(Scene), "RunJob"));

	private static readonly System.Reflection.MethodInfo ReplayMethod = AccessTools.Method(typeof(Scene), "CloseAndReplayCommandBuffer");

	// Its command buffer type is internal to the engine: called through a compiled cast.
	private static readonly System.Action<Scene, object> Replay = CompileReplay();

	private static System.Action<Scene, object> CompileReplay()
	{
		var scene = System.Linq.Expressions.Expression.Parameter(typeof(Scene));
		var buffer = System.Linq.Expressions.Expression.Parameter(typeof(object));
		var call = System.Linq.Expressions.Expression.Call(scene, ReplayMethod, System.Linq.Expressions.Expression.Convert(buffer, ReplayMethod.GetParameters()[0].ParameterType));
		return System.Linq.Expressions.Expression.Lambda<System.Action<Scene, object>>(call, scene, buffer).Compile();
	}

	private static readonly System.Action<Scene, int, int> ExecuteAsync = AccessTools.MethodDelegate<System.Action<Scene, int, int>>(AccessTools.Method(typeof(Scene), "ExecuteAsync"));

	/// <summary>
	/// Rewrites Scene.Tick's job loop to call the timed wrappers below. RunJob itself cannot be patched (its body uses
	/// pointers to private types Harmony cannot re-emit); the small helpers are inlined into Tick's precompiled code.
	/// </summary>
	public static void Install(Harmony harmony)
	{
		if (Installed) return;
		Installed = true;
		harmony.Patch(AccessTools.Method(typeof(Scene), nameof(Scene.Tick)), transpiler: Own(nameof(Transpiler)));
	}

	private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code)
	{
		var run = AccessTools.Method(typeof(Scene), "RunJob");
		var asyncs = AccessTools.Method(typeof(Scene), "ExecuteAsync");
		int swapped = 0;
		foreach (var op in code)
		{
			if (op.Calls(run)) { op.opcode = System.Reflection.Emit.OpCodes.Call; op.operand = AccessTools.Method(typeof(JobProfiler), nameof(TimedRunJob)); swapped++; }
			else if (op.Calls(ReplayMethod)) { op.opcode = System.Reflection.Emit.OpCodes.Call; op.operand = AccessTools.Method(typeof(JobProfiler), nameof(TimedReplay)); swapped++; }
			else if (op.Calls(asyncs)) { op.opcode = System.Reflection.Emit.OpCodes.Call; op.operand = AccessTools.Method(typeof(JobProfiler), nameof(TimedAsync)); swapped++; }
			yield return op;
		}
		if (swapped < 3) throw new System.InvalidOperationException($"Scene.Tick changed: {swapped} of its job calls found");
	}

	private static HarmonyMethod Own(string name) => new(typeof(JobProfiler), name);

	private static Span Begin()
	{
		var span = new Span(Stopwatch.GetTimestamp(), _childTicks);
		_childTicks = 0;
		return span;
	}

	private static bool TimedRunJob(Scene scene, int jobIndex)
	{
		Span span = Begin();
		try { return RunJob(scene, jobIndex); }
		finally
		{
			End(scene, jobIndex, Kind.Job, span);
			_lastJob = jobIndex;
		}
	}

	// The commands a job recorded replay right after it (signals, writes to other entities): its cost too.
	private static void TimedReplay(Scene scene, object commandBuffer)
	{
		Span span = Begin();
		try { Replay(scene, commandBuffer); }
		finally { End(scene, _lastJob, Kind.Replay, span); }
	}

	private static void TimedAsync(Scene scene, int from, int to)
	{
		Span span = Begin();
		try { ExecuteAsync(scene, from, to); }
		finally { End(scene, from, Kind.Async, span); }
	}

	private static void End(Scene scene, int job, Kind kind, Span state)
	{
		long elapsed = Stopwatch.GetTimestamp() - state.Start;
		long own = elapsed - _childTicks;
		_childTicks = state.ChildTicks + elapsed;
		if (own < Stopwatch.Frequency / 10000) return;
		lock (Sync)
		{
			Frame.TryGetValue((scene, job, kind), out long sum);
			Frame[(scene, job, kind)] = sum + own;
		}
	}

	/// <summary>The frame's top jobs ("" when none were timed), and a fresh frame.</summary>
	public static string TakeFrame(bool describe, int top = 5)
	{
		if (!Installed) return "";
		lock (Sync)
		{
			string text = describe && Frame.Count > 0
				? string.Join(", ", Frame.OrderByDescending(e => e.Value).Take(top).Select(e => $"{Name(e.Key.Scene, e.Key.Job, e.Key.Kind)} {ServerProfiler.Ms(e.Value):0}"))
				: "";
			Frame.Clear();
			return text;
		}
	}

	private static readonly AccessTools.FieldRef<Scene, string[]> GroupNames = AccessTools.FieldRefAccess<Scene, string[]>("_jobGroupNames");

	private static readonly System.Reflection.FieldInfo Jobs = AccessTools.Field(typeof(Scene), "_jobInfos");

	private static System.Reflection.FieldInfo? _debugName;

	// "Keen.Game2.X.SolarGeneratorComponent->Void UpdateSunNormalSparse(Keen.X.Data ByRef)" -> "SolarGeneratorComponent.UpdateSunNormalSparse"
	private static string Short(string name)
	{
		int arrow = name.IndexOf("->", System.StringComparison.Ordinal);
		if (arrow < 0) return name.Substring(name.LastIndexOf('.') + 1);
		string owner = name.Substring(0, arrow);
		string method = name.Substring(arrow + 2);
		int paren = method.IndexOf('(');
		if (paren >= 0) method = method.Substring(0, paren);
		return owner.Substring(owner.LastIndexOf('.') + 1) + "." + method.Substring(method.LastIndexOf(' ') + 1);
	}

	private static string Name(Scene scene, int job, Kind kind)
	{
		if (kind == Kind.Async)
		{
			string[] groups = GroupNames(scene);
			return $"async continuations from group {(job >= 0 && job < groups.Length ? groups[job] : job.ToString())}";
		}
		var jobs = (System.Array)Jobs.GetValue(scene)!;
		object? info = job >= 0 && job < jobs.Length ? jobs.GetValue(job) : null;
		string? name = info == null ? null : (string?)(_debugName ??= AccessTools.Field(info.GetType(), "DebugName")).GetValue(info);
		name = string.IsNullOrEmpty(name) ? $"job {job}" : Short(name);
		return kind == Kind.Replay ? name + " (replay)" : name;
	}
}
