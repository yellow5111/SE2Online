using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using HarmonyLib;
using Keen.Game2.Client.GameSystems.PlayerControl;
using Keen.Game2.Simulation.WorldObjects.Characters;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.Accessors;
using Keen.VRage.DCS.Components;
using Keen.VRage.Game.Multiplayer;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Multiplayer.Replications;
using Keen.VRage.Physics.Character;
using Keen.VRage.Physics.Data;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>
/// -y51online:motiontrace (off by default): for 60 s from the first movement of this player's character or of what it
/// controls, a CSV in SE2 Online's data folder with one row per write to a traced body (this player's character, the grid
/// it controls, the deck under it, the three nearest other moving bodies), on a hosting and a joined game alike:
/// -y51online:motiontrace:ship waits for a moving controlled grid and records 120 s, covering the ship test after entering it.
/// -y51online:motiontrace:fast waits until the own character or controlled grid reaches 100 m/s and records 120 s.
/// <list type="bullet">
/// <item>phase eval: the game's playback (MotionStateApplicationSystem.EvaluateMotionState) wrote the transform, with its
/// cursor, the pair's sample times, the blend and the interpolation factor;</item>
/// <item>phase play: SE2 Online's playback chose the pair and cursor for the next tick (joined games);</item>
/// <item>phase phys: the transform after the tick's physics step (read before any of SE2 Online's client work);</item>
/// <item>phase own: a write by SE2 Online's own-character prediction (shift, rebase, thaw, freeze, carry);</item>
/// <item>phase post: the transform after SE2 Online's client work, what the frame is rendered from;</item>
/// <item>phase cam: the third-person camera's placed position;</item>
/// <item>phase inbound: a motion sample for the body arrived (its server time in next);</item>
/// <item>phase clock (joined): the playback clock each tick, key=value in note; ack: each ack's age and error; inputs:
/// inputs sent and catch-up ticks held per wall second; server (host): server ticks and catch-up ticks per wall second;
/// collect: Havok's transform change kind for this player's character (1 = a written WorldTransform, 4 = SoftTransform).</item>
/// </list>
/// Tests/Movement/motiontrace.py reads it (ghost events, writer conflicts).
/// </summary>
internal static class MotionTrace
{
	private static readonly bool ShipOnly = MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "motiontrace:ship");
	private static readonly bool FastOnly = MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "motiontrace:fast");
	internal static readonly bool On = ShipOnly || FastOnly || MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "motiontrace");
	private static readonly long DurationMs = ShipOnly || FastOnly ? 120_000 : 60_000;

	private static readonly ConcurrentQueue<string> Rows = new();
	private static volatile Dictionary<DEntity, string> _who = new();
	private static Entity? _own, _ctrl, _deck;
	private static readonly List<(Entity Entity, string Who)> Traced = new();
	private static StreamWriter? _out;
	private static long _startMs, _frame, _ticks, _nextPickTick, _flushMs;
	private static bool _done;
	private static Session? _session;
	private static readonly System.Diagnostics.Stopwatch Wall = System.Diagnostics.Stopwatch.StartNew();
	private static string? _path;

	internal static bool Recording => _out != null;

	public static void Install(Harmony harmony)
	{
		if (!On) return;
		AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { Stop("the game closed"); } catch { } };
		var system = AccessTools.TypeByName("Keen.VRage.Game.Multiplayer.MotionStateApplicationSystem");
		harmony.Patch(AccessTools.Method(system, "EvaluateMotionState"), postfix: new HarmonyMethod(typeof(MotionTrace), nameof(EvaluatePostfix)) { priority = Priority.Last });
		// Once per app frame (the render frame): whether a client tick ran in it, the camera and this player's character.
		harmony.Patch(AccessTools.Method(AccessTools.TypeByName("Keen.Game2.GameAppComponent"), "GameUpdate"), postfix: new HarmonyMethod(typeof(MotionTrace), nameof(FramePostfix)));
		// What the renderer is handed (model roots and the camera), as opposed to the entity state after the frame.
		var commands = typeof(Keen.VRage.Render.FrameData.RenderCommandBuffer);
		harmony.Patch(AccessTools.Method(commands, "RootEntity_UpdateTransform", new[] { typeof(Keen.VRage.Library.Utils.RenderId), typeof(WorldTransform).MakeByRefType() }), postfix: new HarmonyMethod(typeof(MotionTrace), nameof(DrawnPostfix)));
		harmony.Patch(AccessTools.Method(commands, "RenderSettings_SetCameraParameters"), postfix: new HarmonyMethod(typeof(MotionTrace), nameof(DrawnCameraPostfix)));
		harmony.Patch(AccessTools.Method(commands, "Commit"), postfix: new HarmonyMethod(typeof(MotionTrace), nameof(CommitPostfix)));
		// Anything that hides, shows or re-parents a render entity, counted per frame.
		foreach (var name in RenderOps)
			if (AccessTools.Method(commands, name) is { } op) harmony.Patch(op, postfix: new HarmonyMethod(typeof(MotionTrace), nameof(RenderOpPostfix)));
		GameLog.Instance.Info($"motion trace: on; {DurationMs / 1000} s from the first {(FastOnly ? "jetpack or controlled grid above 100 m/s" : ShipOnly ? "moving controlled grid" : "movement")}, written to " + PluginPaths.DataDir);
	}

	/// <summary>Main menu: a new world traces again.</summary>
	internal static void Reset()
	{
		Stop("left the world");
		_done = false;
		_session = null;
		_who = new();
		Traced.Clear();
	}

	// --- writers ---

	private static void EvaluatePostfix(DEntity entity, in MotionHistory history, Writable<WorldTransform> transform, OptionalWritable<RigidBodyData> body, MotionInterpolationSessionComponent interpolation)
	{
		if (_out == null || !_who.TryGetValue(entity, out var who)) return;
		try
		{
			if (history.Newest.ServerTime == TimeSpan.Zero || ReferenceEquals(OwnMotion.Driving, _own) && _own?.DEntity == entity) return;
			long cursor = MotionSmoothing.Cursor(interpolation, history).Ticks;
			long prev = history.Previous.ServerTime.Ticks, next = history.Newest.ServerTime.Ticks;
			double alpha = next > prev && prev != 0 ? (double)(cursor - prev) / (next - prev) : double.NaN;
			var v = body.HasValue ? body.Value.LinearVelocity : Vector3.Zero;
			Add("eval", who, entity, "keen-eval", transform.Value, v, cursor, prev, next, alpha, interpolation.Clock.Ticks,
				history.BlendDuration != TimeSpan.Zero ? $"blend {history.BlendStart.Ticks}+{history.BlendDuration.Ticks}" : "");
		}
		catch (Exception ex) { GameLog.Instance.WarnOnce("motion-trace-eval", "motion trace: " + ex.Message); }
	}

	/// <summary>SE2 Online's playback chose this body's pair and cursor for the next tick (MotionSmoothing.Play).</summary>
	internal static void Play(Entity entity, long cursor, long prev, long next, bool extrapolating, double lead, long newest)
	{
		if (_out == null || !_who.TryGetValue(entity.DEntity, out var who)) return;
		Add("play", who, entity.DEntity, "playout", null, null, cursor, prev, next, next > prev && prev != 0 ? (double)(cursor - prev) / (next - prev) : double.NaN, newest,
			(extrapolating ? "extrapolating" : "") + (lead > 0 ? $" lead {lead:0}" : ""));
	}

	/// <summary>A write by SE2 Online's own-character code (<paramref name="writer"/>: shift, rebase, thaw, freeze, carry).</summary>
	internal static void Own(Entity entity, string writer, in WorldTransform at, Vector3 velocity, string note = "")
	{
		if (_out == null || !ReferenceEquals(entity.Scene, _session?.Scene) || !_who.TryGetValue(entity.DEntity, out var who)) return;
		Add("own", who, entity.DEntity, writer, at, velocity, 0, 0, 0, double.NaN, 0, note);
	}

	/// <summary>A motion sample for a traced body arrived from the server (joined games, SE2 Online's transport).</summary>
	internal static void Inbound(Entity entity, in EntityMotionState state)
	{
		if (_out == null || !_who.TryGetValue(entity.DEntity, out var who) || !ReferenceEquals(entity.Scene, _session?.Scene)) return;
		Add("inbound", who, entity.DEntity, "sample", state.Transform, state.Body.LinearVelocity, 0, 0, state.ServerTime.Ticks, double.NaN, 0,
			((state.Flags & MotionStateFlags.Teleported) != 0 ? "teleported " : "") + ((state.Flags & MotionStateFlags.Resting) != 0 ? "resting " : "") + (state.ParentEpoch != 0 ? $"parent {state.ParentEpoch}" : ""));
	}

	/// <summary>A row of named values (the clock each tick, acks, inputs and server ticks per second).</summary>
	internal static void Note(string phase, string who, string note)
	{
		if (_out == null) return;
		Add(phase, who, default, phase, null, null, 0, 0, 0, double.NaN, 0, note);
	}

	/// <summary>Havok's character input job for this player's character: what kind of transform change it takes this tick.</summary>
	internal static void Collect(in WorldTransform at, int changeTypes)
	{
		if (_out == null || _own == null) return;
		Add("collect", "own", _own.DEntity, "havok-collect", at, null, 0, 0, 0, double.NaN, 0, $"changes={changeTypes}");
	}

	/// <summary>The third-person camera's placed position this frame.</summary>
	internal static void Camera(in WorldTransform placed)
	{
		if (_out == null) return;
		Add("cam", "camera", default, "camera", placed, null, 0, 0, 0, double.NaN, 0, "");
	}

	private static volatile Dictionary<Keen.VRage.Library.Utils.RenderId, (DEntity Entity, string Who)> _drawn = new();

	private static void DrawnPostfix(Keen.VRage.Render.FrameData.RenderCommandBuffer __instance, Keen.VRage.Library.Utils.RenderId thiz, in WorldTransform transform)
	{
		if (_out == null || !_drawn.TryGetValue(thiz, out var d)) return;
		Add("drawn", d.Who, d.Entity, "render", transform, null, 0, 0, 0, double.NaN, 0, "buffer " + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(__instance));
	}

	private static readonly string[] RenderOps = { "RootEntity_Activate", "RootEntity_Deactivate", "IChildEntity_SetParent", "ModelEntity_SetRenderFlags", "ModelEntity_SetRenderFlagsState", "ModelEntity_SetOpacity", "InstancedModelEntity_SetRenderFlags", "InstancedModelEntity_SetRenderFlagsState", "InstancedModelEntity_SetOpacity", "ModelEntity_UpdateModel" };
	private static readonly int[] _renderOps = new int[10];

	private static void RenderOpPostfix(System.Reflection.MethodBase __originalMethod)
	{
		if (_out != null) System.Threading.Interlocked.Increment(ref _renderOps[Array.IndexOf(RenderOps, __originalMethod.Name)]);
	}

	private static string TakeRenderOps()
	{
		var sb = new StringBuilder();
		for (int i = 0; i < RenderOps.Length; i++)
			if (System.Threading.Interlocked.Exchange(ref _renderOps[i], 0) is var n and > 0) sb.Append(' ').Append(RenderOps[i]).Append('=').Append(n);
		return sb.ToString();
	}

	// The render draws a frame once its command buffer is committed: a transform written after that is drawn a frame late.
	private static void CommitPostfix(Keen.VRage.Render.FrameData.RenderCommandBuffer __instance)
	{
		if (_out == null) return;
		Add("commit", "render", default, "buffer " + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(__instance), null, null, 0, 0, 0, double.NaN, 0, "");
	}

	private static void DrawnCameraPostfix(Keen.VRage.Render.FrameData.RenderCommandBuffer __instance, in WorldTransform cameraTransform, bool smooth)
	{
		if (_out == null) return;
		Add("drawn", "camera", default, "render-camera", cameraTransform, null, 0, 0, 0, double.NaN, 0, "buffer " + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(__instance));
	}

	private static long _ticksSeenAtFrame, _renderFrames;

	private static void FramePostfix()
	{
		if (_out == null) return;
		try
		{
			bool ticked = _frame != _ticksSeenAtFrame;
			_ticksSeenAtFrame = _frame;
			_renderFrames++;
			var camera = MpPlugin.App?.ClientSession?.SessionComponents.TryGet<ClientPlayersSessionComponent>()?.LocalPlayerController?.CameraSystem?.RenderCameraEntity;
			string note = (ticked ? "tick=1" : "tick=0") + $" render={_renderFrames} gc={GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)} gcpause={GC.GetTotalPauseDuration().TotalMilliseconds:0.0} alloc={GC.GetAllocatedBytesForCurrentThread() / 1024}" + TakeRenderOps();
			if (camera != null && camera.Scene != null) Add("render", "camera", default, "render-camera", camera.Data.GetWorldTransform(), null, 0, 0, 0, double.NaN, 0, note);
			if (_own != null && _own.Scene != null) Add("render", "own", _own.DEntity, "render-own", _own.Data.GetWorldTransform(), null, 0, 0, 0, double.NaN, 0, note);
			if (_ctrl != null && _ctrl.Scene != null) Add("render", "ctrl", _ctrl.DEntity, "render-ctrl", _ctrl.Data.GetWorldTransform(), null, 0, 0, 0, double.NaN, 0, note);
		}
		catch (Exception ex) { GameLog.Instance.WarnOnce("motion-trace-frame", "motion trace: " + ex.Message); }
	}

	// --- the client tick ---

	/// <summary>Client session thread, before SE2 Online's client work: the physics step's result.</summary>
	internal static void BeforeClientWork(Session session)
	{
		if (!On || _done && _out == null) return;
		try
		{
			if (!ReferenceEquals(session, _session)) { Stop("a new session"); _session = session; _done = false; }
			// Ticks are counted also before recording: 0.18.2 counted only recorded frames, so the bodies were picked once
			// at the world's start (no character yet) and the trace never started.
			if (++_ticks >= _nextPickTick) Pick(session);
			if (_out == null)
			{
				if (!_done && Moving()) Start(session);
				return;
			}
			Snapshot("phys");
		}
		catch (Exception ex) { GameLog.Instance.WarnOnce("motion-trace-tick", "motion trace: " + ex.Message); }
	}

	/// <summary>Client session thread, after SE2 Online's client work: what this frame is rendered from.</summary>
	internal static void AfterClientWork()
	{
		if (_out == null) return;
		try
		{
			Snapshot("post");
			lock (FileGate)
			{
				Flush();
				// On disk every 2 s: a game closed before the 60 s are up still leaves what was recorded.
				if (Wall.ElapsedMilliseconds - _flushMs >= 2000) { _flushMs = Wall.ElapsedMilliseconds; _out?.Flush(); }
			}
			_frame++;
			if (Wall.ElapsedMilliseconds - _startMs >= DurationMs) Stop($"{DurationMs / 1000} s recorded");
		}
		catch (Exception ex) { GameLog.Instance.WarnOnce("motion-trace-tick", "motion trace: " + ex); Stop("failed"); }
	}

	private static bool Moving()
	{
		if (ShipOnly) return _ctrl != null && _ctrl.Data.TryGet<RigidBodyData>(out var controlled) && controlled.LinearVelocity.LengthSquared() > 0.25f;
		foreach (var e in FastOnly ? new[] { _own, _ctrl } : Traced.Select(t => t.Entity).ToArray())
			if (e != null && e.Data.TryGet<RigidBodyData>(out var b) && b.LinearVelocity.LengthSquared() > (FastOnly ? 10_000f : 0.25f)) return true;
		return false;
	}

	private static void Pick(Session session)
	{
		_nextPickTick = _ticks + 60;
		var controller = session.SessionComponents.TryGet<ClientPlayersSessionComponent>()?.LocalPlayerController;
		Entity? own = OwnMotion.OwnEntity, ctrl = null;
		if (controller != null)
			foreach (var c in controller.ControlledEntities)
			{
				if ((c.Controllable as Component)?.Entity is not { } e) continue;
				if (e.TryGet<CharacterLifeComponent>() != null) own ??= e;
				else if (SyncEngine.WorldSocial.GridOf(e) is { } grid) ctrl ??= Root(grid);
			}
		_own = own;
		_ctrl = ctrl;
		_deck = MotionSmoothing.Deck;
		Traced.Clear();
		if (own != null) Traced.Add((own, "own"));
		if (ctrl != null) Traced.Add((ctrl, "ctrl"));
		if (_deck != null && !ReferenceEquals(_deck, ctrl)) Traced.Add((_deck, "deck"));
		var from = (own ?? ctrl)?.Data.GetWorldTransform().Position;
		if (from is { } at)
		{
			var others = new List<(double D, Entity E)>();
			foreach (var e in session.GetEntitiesOfType<CharacterLifeComponent>())
				if (!ReferenceEquals(e, own) && e.Data.Has<MotionHistory>()) others.Add((Vector3D.DistanceSquared(e.Data.GetWorldTransform().Position, at), e));
			foreach (var e in session.GetEntitiesOfType<CubeGridComponent>())
				if (!Traced.Any(t => ReferenceEquals(t.Entity, e)) && e.Data.Has<MotionHistory>() && e.Data.TryGet<RigidBodyData>(out var b) && b.LinearVelocity.LengthSquared() > 0.01f)
					others.Add((Vector3D.DistanceSquared(e.Data.GetWorldTransform().Position, at), e));
			int n = 0;
			foreach (var (_, e) in others.OrderBy(o => o.D).Take(3)) Traced.Add((e, "r" + ++n));
		}
		var who = new Dictionary<DEntity, string>();
		var drawn = new Dictionary<Keen.VRage.Library.Utils.RenderId, (DEntity, string)>();
		foreach (var (e, w) in Traced)
		{
			who.TryAdd(e.DEntity, w);
			if (e.TryGet<Keen.VRage.Game.Client.Render.RootEntityRenderComponent>() is { } r && r.RenderRootEntity.IsValid) drawn.TryAdd(r.RenderRootEntity.Id, (e.DEntity, w));
		}
		_who = who;
		_drawn = drawn;
	}

	private static Entity Root(Entity e)
	{
		for (int depth = 0; e.TryGet<HierarchyComponent>()?.Parent is { } up && depth < 16; depth++) e = up;
		return e;
	}

	private static void Snapshot(string phase)
	{
		foreach (var (e, who) in Traced)
		{
			if (e.Scene == null) continue;
			var at = e.Data.GetWorldTransform();
			e.Data.TryGet<RigidBodyData>(out var b);
			string note = "";
			if (e.Data.TryGet<SoftTransform>(out var soft)) note = $"soft {F(soft.Value.Position.X)} {F(soft.Value.Position.Y)} {F(soft.Value.Position.Z)}";
			string deck = "";
			Vector3D? local = null;
			if (who == "own" && MotionSmoothing.Deck is { } d && d.Scene != null)
			{
				deck = d.DEntity.ToString();
				local = WorldTransform.TransformInv(at.Position, d.Data.GetWorldTransform());
			}
			Add(phase, who, e.DEntity, phase == "phys" ? "physics" : "frame", at, b.LinearVelocity, 0, 0, 0, double.NaN, MotionSmoothing.NewestServerTime, note, deck, local);
		}
	}

	// --- file ---

	private const string Header = "wall_ms,frame,phase,who,entity,writer,x,y,z,qx,qy,qz,qw,vx,vy,vz,cursor,prev,next,alpha,clock,deck,lx,ly,lz,note";

	private static void Start(Session session)
	{
		try
		{
			string role = MpPlugin.App?.ServerSession != null ? "host" : "client";
			_path = Path.Combine(PluginPaths.DataDir, $"motiontrace-{DateTime.Now:yyyyMMdd-HHmmss}-{role}.csv");
			_out = new StreamWriter(_path, false, new UTF8Encoding(false), 1 << 16);
			_out.WriteLine(Header);
			_startMs = Wall.ElapsedMilliseconds;
			_frame = 0;
			_flushMs = Wall.ElapsedMilliseconds;
			GameLog.Instance.Info($"motion trace: recording {DurationMs / 1000} s to {_path} ({string.Join(", ", Traced.Select(t => t.Who))})");
		}
		catch (Exception ex) { _out = null; _done = true; GameLog.Instance.Warn("motion trace: cannot write the trace: " + ex.Message); }
	}

	private static readonly object FileGate = new();

	private static void Stop(string why)
	{
		lock (FileGate)
		{
			if (_out == null) return;
			try { Flush(); _out.Dispose(); }
			catch { }
			_out = null;
		}
		_done = true;
		GameLog.Instance.Info($"motion trace: {why}; {_frame} frames in {_path}");
	}

	private static void Flush()
	{
		if (_out == null) return;
		while (Rows.TryDequeue(out var row)) _out.WriteLine(row);
	}

	private static void Add(string phase, string who, DEntity entity, string writer, WorldTransform? at, Vector3? v, long cursor, long prev, long next, double alpha, long clock, string note, string deck = "", Vector3D? local = null)
	{
		var sb = new StringBuilder(220);
		sb.Append((Wall.Elapsed.TotalMilliseconds - _startMs).ToString("0.00", CultureInfo.InvariantCulture)).Append(',').Append(_frame).Append(',').Append(phase).Append(',').Append(who).Append(',').Append(entity.ToString()).Append(',').Append(writer).Append(',');
		if (at is { } t)
			sb.Append(F(t.Position.X)).Append(',').Append(F(t.Position.Y)).Append(',').Append(F(t.Position.Z)).Append(',')
				.Append(F(t.Orientation.X)).Append(',').Append(F(t.Orientation.Y)).Append(',').Append(F(t.Orientation.Z)).Append(',').Append(F(t.Orientation.W)).Append(',');
		else sb.Append(",,,,,,,");
		if (v is { } u) sb.Append(F(u.X)).Append(',').Append(F(u.Y)).Append(',').Append(F(u.Z)).Append(',');
		else sb.Append(",,,");
		sb.Append(cursor == 0 ? "" : cursor.ToString(CultureInfo.InvariantCulture)).Append(',')
			.Append(prev == 0 ? "" : prev.ToString(CultureInfo.InvariantCulture)).Append(',')
			.Append(next == 0 ? "" : next.ToString(CultureInfo.InvariantCulture)).Append(',')
			.Append(double.IsNaN(alpha) ? "" : alpha.ToString("0.###", CultureInfo.InvariantCulture)).Append(',')
			.Append(clock == 0 ? "" : clock.ToString(CultureInfo.InvariantCulture)).Append(',').Append(deck).Append(',');
		if (local is { } l) sb.Append(F(l.X)).Append(',').Append(F(l.Y)).Append(',').Append(F(l.Z)).Append(',');
		else sb.Append(",,,");
		sb.Append(note.Replace(',', ';'));
		Rows.Enqueue(sb.ToString());
	}

	private static string F(double d) => d.ToString("0.####", CultureInfo.InvariantCulture);
}
