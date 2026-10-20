#if !PULSAR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Keen.Game2.Client.GameSystems.PlayerControl;
using Keen.Game2.Simulation.WorldObjects.Characters;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.Game2.Simulation.WorldObjects.CubeGrids.Physicss;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Multiplayer.Replications;
using Keen.VRage.Physics.Data;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;
using Y51_SE2Online.SyncEngine;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>
/// SE2 4.0 ships its client motion playback switched off: <see cref="MotionInterpolationSettings.Interpolate"/> is a
/// debug-menu toggle that defaults to false, so a client snaps every replicated character, grid and ragdoll to each
/// motion sample as it arrives. Samples come in network bursts, not one per client frame, so remote players, ships
/// (one's own too: SE2 4.0 is server-authoritative) and ragdolls (keyframed to that transform) moved in steps and
/// jerks. The game's own playback (interpolation between the two newest samples, capped extrapolation, error fade,
/// parent-relative motion for characters on ships) is turned on for every SE2 Online session. Its playout delay is
/// the sample gap minus one frame: none while samples arrive every frame. -y51online:interpolate:off keeps it off.
/// </summary>
internal static class MotionSmoothing
{
	public static void Install()
	{
		bool on = !MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "interpolate:off");
		MotionInterpolationSettings.Instance.Interpolate = on;
		GameLog.Instance.Info(on ? "motion: the game's motion playback (interpolation) is on" : "motion: playback left off (-y51online:interpolate:off); replicated motion snaps to each sample");
	}
}

/// <summary>
/// Test rig only (-y51online:selftest): measures how smoothly replicated things move in this game's client scene
/// (a joined client's, or the listen host's own). Every frame it samples other players' characters, moving grids and
/// ragdolls; every 30 s it logs, per category, the jerk (second difference of position over the frame time, m/s^2)
/// and the snaps (a frame step over 3x what the entity's velocity explains). With -y51online:selftest-move the host
/// also drives a grid near the client in a circle at 20 m/s and kills its own character once, for a ragdoll.
/// </summary>
internal static class MotionProbe
{
	private const long ReportMs = 30_000, RescanFrames = 60, MaxTracked = 64;
	private const double GridRange = 1_000_000, MinGridSpeed = 1;

	private sealed class Track
	{
		public string Category = "";
		public Vector3D P1, P2;
		public double Dt1;
		public int Count;
	}

	private sealed class Stats
	{
		public readonly List<float> Jerk = new();
		public int Snaps;
	}

	private static readonly Dictionary<Entity, Track> Tracks = new();
	private static readonly Dictionary<Entity, Vector3D> ScanPositions = new();
	private const double ScanSeconds = 0.5;
	private static double _maxScanMove;
	private static bool eyeKnown;
	private static Vector3D? _eye;
	private static string _maxScanGrid = "";
	private static readonly Dictionary<string, Stats> ByCategory = new();
	private static List<Entity> _candidates = new();
	private static Session? _session;
	private static double _lastSeconds;
	private static long _frame, _nextReportMs;

	public static void Tick(Keen.Game2.GameAppComponent app)
	{
		if (!SelfTest.Requested || app.ClientSession is not { } session || app._isTransitioning.IsSet) return;
		// The scene's own clock, not the wall clock: a co-op host's client scene advances one simulation step per frame
		// (one server tick each), so wall-clock frame pacing made smooth motion read as snaps. A frame in which the scene
		// did not tick (CanTick false) moved nothing and is skipped.
		var time = Keen.VRage.Core.Game.Components.GameEntityExtensions.Get<Keen.VRage.Core.Game.GameSystems.GameTimes.IGameTime>(session);
		if (time == null) return;
		double seconds = (time.CurrentGameTime - DateTime.MinValue).TotalSeconds, dt = seconds - _lastSeconds;
		_lastSeconds = seconds;
		if (!ReferenceEquals(session, _session)) { _session = session; Tracks.Clear(); _candidates.Clear(); _frame = 0; }
		if (dt <= 0 || dt > 0.25) return;
		if (_frame++ % RescanFrames == 0) _candidates = Candidates(session);
		foreach (var entity in _candidates)
		{
			if (EngineCodec.IsDead(entity)) continue;
			string category = Category(entity, out double speed);
			if (category.Length == 0) continue;
			var p = entity.Data.GetWorldTransform().Position;
			if (!Tracks.TryGetValue(entity, out var t) || t.Category != category) Tracks[entity] = t = new Track { Category = category };
			if (t.Count >= 2)
			{
				var stats = Get(category);
				double jerk = (p - 2 * t.P1 + t.P2).Length() / (dt * t.Dt1);
				if (stats.Jerk.Count < 50_000) stats.Jerk.Add((float)jerk);
				if ((p - t.P1).Length() > 3 * Math.Max(speed * dt, 0.05)) stats.Snaps++;
			}
			t.P2 = t.P1; t.P1 = p; t.Dt1 = dt; t.Count++;
		}
		long now = MpClock.NowMs();
		if (_nextReportMs == 0) _nextReportMs = now + ReportMs;
		if (now < _nextReportMs) return;
		_nextReportMs = now + ReportMs;
		GameLog.Instance.Info($"selftest: motion scan: eye {(eyeKnown ? "known" : "unknown")}, {_candidates.Count} tracked, largest grid move between scans {_maxScanMove:0.0} m ({_maxScanGrid})");
		foreach (var g in session.GetEntitiesOfType<CubeGridComponent>())
			if (g.Get<CubeGridComponent>().DisplayName.ToString().Contains("Pathfinder"))
			{
				var gp = g.Data.GetWorldTransform().Position;
				GameLog.Instance.Info($"selftest: motion ship '{g.Get<CubeGridComponent>().DisplayName}' at {gp.X:0.0}, {gp.Y:0.0}, {gp.Z:0.0}, {(g.Data.TryGet<RigidBodyData>(out var rb) ? rb.LinearVelocity.Length().ToString("0.0") + " m/s" : "no velocity")}, eye {(_eye is { } e ? Vector3D.Distance(e, gp).ToString("0") + " m away" : "unknown")}");
			}
		_maxScanMove = 0;
		foreach (var (category, stats) in ByCategory)
		{
			if (stats.Jerk.Count == 0) continue;
			stats.Jerk.Sort();
			float P(double q) => stats.Jerk[(int)Math.Min(stats.Jerk.Count - 1, q * stats.Jerk.Count)];
			GameLog.Instance.Info($"selftest: motion {category}: {stats.Jerk.Count} samples, jerk p50 {P(0.5):0.0} / p95 {P(0.95):0.0} / max {stats.Jerk[^1]:0.0} m/s2, snaps {stats.Snaps}");
			stats.Jerk.Clear();
			stats.Snaps = 0;
		}
	}

	private static Stats Get(string category)
	{
		if (!ByCategory.TryGetValue(category, out var stats)) ByCategory[category] = stats = new Stats();
		return stats;
	}

	// Other players' living characters, ragdolls, and grids moving near this game's camera; this game's own body is not "replicated motion seen from outside".
	private static List<Entity> Candidates(Session session)
	{
		var controller = session.SessionComponents.TryGet<ClientPlayersSessionComponent>()?.LocalPlayerController;
		var own = new HashSet<Entity>();
		if (controller != null)
			foreach (var c in controller.ControlledEntities)
				if ((c.Controllable as Component)?.Entity is { } e) own.Add(e);
		// Measured around this player's own body (a hidden game's render camera sits at the origin).
		var eye = own.FirstOrDefault()?.Data.GetWorldTransform().Position ?? controller?.CameraSystem?.RenderCameraEntity?.Data.GetWorldTransform().Position;
		eyeKnown = eye != null;
		_eye = eye;
		var list = new List<Entity>();
		foreach (var character in session.GetEntitiesOfType<CharacterLifeComponent>())
			if (!own.Contains(character) && list.Count < MaxTracked) list.Add(character);
		if (eye is { } at)
			foreach (var grid in session.GetEntitiesOfType<CubeGridComponent>())
			{
				if (list.Count >= MaxTracked) break;
				if (own.Contains(grid) || Vector3D.Distance(grid.Data.GetWorldTransform().Position, at) > GridRange) continue;
				// A client is not sent remote grids' velocities: a grid counts as moving when it went MinGridSpeed m/s since the last scan.
				var now = grid.Data.GetWorldTransform().Position;
				bool moving = grid.Data.TryGet<RigidBodyData>(out var body) && body.LinearVelocity.Length() > MinGridSpeed
					|| ScanPositions.TryGetValue(grid, out var before) && Vector3D.Distance(now, before) > MinGridSpeed * ScanSeconds;
				if (ScanPositions.TryGetValue(grid, out var last)) { double d = Vector3D.Distance(now, last); if (d > _maxScanMove) { _maxScanMove = d; _maxScanGrid = grid.Get<CubeGridComponent>().DisplayName.ToString(); } }
				ScanPositions[grid] = now;
				if (moving) list.Add(grid);
			}
		foreach (var gone in Tracks.Keys.Where(e => !list.Contains(e)).ToList()) Tracks.Remove(gone);
		return list;
	}

	private static string Category(Entity entity, out double speed)
	{
		speed = entity.Data.TryGet<RigidBodyData>(out var body) ? body.LinearVelocity.Length() : 0;
		if (entity.TryGet<CharacterLifeComponent>() is { } life) return life.IsAlive ? "character" : "ragdoll";
		return entity.TryGet<CubeGridComponent>() != null ? "grid" : "";
	}

	// --- rig mover (host, server thread) ---

	private static long _moveSinceMs, _killAtMs;
	private static Entity? _moved;
	private static Vector3D _centre;
	private static long _moveLogMs;
	private static double _angle;

	private static bool _spawning;
	private static Entity? _spawned;

	// No free ship near the client (every one parked or docked): the mover brings its own, a space respawn ship 800 m off (closer, the game found no free route for it).
	private static async void SpawnTestShip(Session session, Vector3D near)
	{
		if (_spawning) return;
		_spawning = true;
		try
		{
			var encounters = session.SessionComponents.TryGet<Keen.Game2.Simulation.GameSystems.Encounters.EncounterSessionComponent>();
			var ship = Y51_SE2Online.Sync.RespawnShips.Catalog.FirstOrDefault(s => s.Space);
			if (encounters == null || ship == null || !Keen.VRage.Library.Utils.Singleton<Keen.VRage.Library.Definitions.DefinitionManager>.Instance.TryGetDefinition(ship.Prefab, out Keen.VRage.Core.Game.Definitions.PrefabDefinition prefab)) return;
			var spawn = session.SessionComponents.TryGet<Keen.Game2.Simulation.GameSystems.Respawn.RespawnSessionComponent>()?.WorldSpawn ?? Keen.VRage.Core.WorldTransform.Identity;
			// The way a space respawn ship is placed (it finds its route there): open space by the client, the world spawn's facing.
			var roots = await HostWorld.SpawnEncounter(session, encounters, prefab, new Keen.VRage.Core.WorldTransform(HostWorld.OpenSpace(session, near + new Vector3D(150, 150, 0)), spawn.Orientation));
			var grids = HostWorld.ReleaseEncounter(session, roots).Where(e => e.TryGet<CubeGridComponent>() != null).ToList();
			GameLog.Instance.Info($"selftest: spawned {ship.Name} for the mover ({grids.Count} grids)");
			_spawned = grids.FirstOrDefault(g => g.TryGet<GridPhysicsComponent>() != null);
		}
		catch (Exception ex) { GameLog.Instance.Warn("selftest: spawning the mover's ship failed: " + ex.Message); }
	}

	// A ship the mover can push: small, and not held by a locked landing gear (a parked ship did not move at all).
	private static bool Free(Entity grid)
	{
		int blocks = 0;
		bool locked = false;
		var cubeGrid = grid.Get<CubeGridComponent>();
		cubeGrid.VisitAllBlocksWithComponent((Keen.Game2.Simulation.WorldObjects.CubeBlocks.CubeBlockComponent _) => blocks++);
		cubeGrid.VisitAllBlocksWithComponent((Keen.Game2.Simulation.WorldObjects.CubeBlocks.MechanicalBlocks.MagneticBlockComponent m) => locked |= m.IsLocked);
		return !locked && blocks > 0 && blocks < 300;
	}

	/// <summary>Server thread of a hosted world with -y51online:selftest-move: something for the client's probe to watch.</summary>
	public static void ServerTick(Session session, long hostIdentity)
	{
		if (!MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "selftest-move")) return;
		long now = MpClock.NowMs();
		var client = session.GetEntitiesOfType<CharacterLifeComponent>().FirstOrDefault(c => c.Get<CharacterLifeComponent>().IsAlive
			&& TeamPermissions.OwnerOf(c) is long owner && owner != 0 && owner != hostIdentity && PlayerIdentities.IsHuman(owner));
		if (client == null) { _moveSinceMs = 0; return; }
		if (_moveSinceMs == 0) { _moveSinceMs = now; _killAtMs = now + 90_000; return; }
		if (now - _moveSinceMs < 60_000) return;
		var near = client.Data.GetWorldTransform().Position;
		if (_moved == null || EngineCodec.IsDead(_moved))
		{
			// Only the mover's own ship: driving parked ships in a populated world smashed them apart.
			_moved = _spawned != null && !EngineCodec.IsDead(_spawned) ? _spawned : null;
			if (_moved == null) { SpawnTestShip(session, near); return; }
			_moved.Get<GridPhysicsComponent>().SetGridMotion(false);
			_centre = _moved.Data.GetWorldTransform().Position;
			GameLog.Instance.Info($"selftest: moving grid {_moved.Get<CubeGridComponent>().DisplayName} in a 60 m circle at 20 m/s for the motion probe");
		}
		// 20 m/s on a 60 m circle, plus a pull back onto it: a smooth, known path (gravity is overridden every tick).
		_angle += 20.0 / 60.0 / 60.0;
		var target = _centre + new Vector3D(Math.Cos(_angle), 0, Math.Sin(_angle)) * 60;
		var tangent = new Vector3D(-Math.Sin(_angle), 0, Math.Cos(_angle)) * 20;
		var velocity = tangent + (target - _moved.Data.GetWorldTransform().Position) * 0.5;
		_moved.Data.Set(new RigidBodyData((Vector3)velocity, Vector3.Zero));
		if (now >= _moveLogMs)
		{
			_moveLogMs = now + 10_000;
			var at = _moved.Data.GetWorldTransform().Position;
			GameLog.Instance.Info($"selftest: moved grid {Vector3D.Distance(at, _centre):0} m from its start, {Vector3D.Distance(at, target):0} m off its path, host identity {hostIdentity}");
		}
		if (_killAtMs != 0 && now >= _killAtMs)
		{
			_killAtMs = 0;
			var host = session.GetEntitiesOfType<CharacterLifeComponent>().FirstOrDefault(c => c.Get<CharacterLifeComponent>().IsAlive && TeamPermissions.OwnerOf(c) == hostIdentity);
			if (host != null) { GameLog.Instance.Info("selftest: killing the host's character for a ragdoll sample"); host.Get<CharacterLifeComponent>().RequestDeath(); }
		}
	}
}
#endif
