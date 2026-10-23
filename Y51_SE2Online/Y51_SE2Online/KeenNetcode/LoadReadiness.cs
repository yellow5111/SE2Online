using System;
using Keen.Game2.Client.GameSystems.PlayerControl;
using Keen.Game2.Client.GameSystems.Respawn;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.Pilotable;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.Game2.Simulation.WorldObjects.CubeGrids.Physicss;
using Keen.Game2.Simulation.WorldObjects.Characters;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.Core.Game.GameSystems.SpaceProbe;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Library.Threading;
using Keen.VRage.Library.Utils;
using Keen.VRage.Physics.Character;
using Keen.VRage.Physics.Data;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;
using KTask = Keen.VRage.Library.Threading.Task;
using Probe = Y51_SE2Online.Sync.WorldReadiness.Probe;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>
/// The joining game's side of the loading screen wait (<see cref="WorldReadiness"/>): on the client session's thread it
/// looks at each part of the world around the player and holds the player's own predicted character still meanwhile.
/// The parts use the game's own fences where it has them: the space probe preload fast travel waits on for voxel and
/// planet environment physics (ISpaceProbeSystem.PreloadAsync, Precision.High), and the resource wait the game's first
/// spawn and respawn use for voxel render and render streaming (IClientResourceLoading.AwaitAllResourcesLoaded).
/// </summary>
internal static class LoadReadiness
{
	private const double GridMeters = 200, PreloadHalfMeters = 48, PreloadMoveMeters = 50, FloraMeters = 150;
	private const long IdleWithinMs = 1000, EmptyWorldMs = 5000;

	/// <summary>Set by the main thread while the loading screen waits for the world (JoinStatusUi).</summary>
	internal static volatile bool Active;

	private static volatile Probe[]? _probes;

	/// <summary>The newest look at every part, or null before the first.</summary>
	internal static Probe[]? Probes => _probes;

	private static long _beganMs = -1, _idleMs = -1;
	private static int _gen, _resourcesNo;
	// The preload's completion (a KTask continuation) and End/Reset hand the preloaded space over under this lock.
	private static readonly object PreloadGate = new();
	private static bool _preloading;
	private static int _preloadNo;
	private static IPreloadedSpace? _preloaded;
	private static bool _preloadDone;
	private static Vector3D _preloadAt;
	private static bool _resourcesStarted, _resourcesDone;
	// When everything near the player (world, character, grids, terrain, flora) was first ready, -1 while it is not.
	private static long _nearbyMs = -1;
	private static bool _texturesLeft;
	private static Vector3D? _frozenAt;
	private static long _frozenTicks;

	/// <summary>Client session thread, after each update (HostWorld.SessionUpdate).</summary>
	internal static void ClientTick(Session session)
	{
		if (!Active)
		{
			if (_beganMs >= 0) End();
			return;
		}
		long now = MpClock.NowMs();
		if (_beganMs < 0) Begin(now);
		try
		{
			var controller = session.SessionComponents.TryGet<ClientPlayersSessionComponent>()?.LocalPlayerController;
			Entity? body = controller?.GetBottomControllable()?.Entity;
			if (body != null && SyncEngine.EngineCodec.IsDead(body)) body = null;
			var probes = new Probe[WorldReadiness.Parts];
			probes[(int)WorldReadiness.Part.World] = World(now);
			probes[(int)WorldReadiness.Part.Character] = Character(controller, body);
			if (body == null)
			{
				var moot = new Probe(false, 0, "waiting for your character") { Moot = true };
				probes[(int)WorldReadiness.Part.Grids] = probes[(int)WorldReadiness.Part.Terrain] = probes[(int)WorldReadiness.Part.Details] = moot;
			}
			else
			{
				var at = body.Data.GetWorldTransform().Position;
				probes[(int)WorldReadiness.Part.Grids] = Grids(session, at);
				probes[(int)WorldReadiness.Part.Terrain] = Terrain(session, controller!, at);
				bool nearby = probes[(int)WorldReadiness.Part.World].Ready && probes[(int)WorldReadiness.Part.Character].Ready
					&& probes[(int)WorldReadiness.Part.Grids].Ready && probes[(int)WorldReadiness.Part.Terrain].Ready;
				probes[(int)WorldReadiness.Part.Details] = Details(session, controller!, at, nearby, now);
				// Held only while what it could stand on may be missing; terrain detail and textures do not need it.
				if (probes[(int)WorldReadiness.Part.World].Ready && probes[(int)WorldReadiness.Part.Grids].Ready && probes[(int)WorldReadiness.Part.Terrain].Ready) _frozenAt = null;
				else Hold(body);
			}
			_probes = probes;
		}
		catch (Exception ex)
		{
			// A look that fails must not keep the screen up: every part counts as ready, the log says why.
			GameLog.Instance.WarnOnce("load-readiness", $"loading screen: checking the world around the player failed ({ex.GetType().Name}: {ex.Message}); not waiting for it");
			var done = new Probe[WorldReadiness.Parts];
			for (int i = 0; i < done.Length; i++) done[i] = Probe.Done;
			_probes = done;
		}
	}

	private static void Begin(long now)
	{
		_beganMs = now;
		_idleMs = -1;
		lock (PreloadGate)
		{
			_gen++;
			_preloaded = null;
		}
		_preloading = false;
		_preloadDone = _resourcesStarted = _resourcesDone = _texturesLeft = false;
		_nearbyMs = -1;
		_frozenAt = null;
		_frozenTicks = 0;
	}

	private static void End()
	{
		if (_frozenTicks > 0) GameLog.Instance.Info($"loading screen: the character was held still for {_frozenTicks} ticks while the world loaded");
		_beganMs = -1;
		_probes = null;
		LetGo();
		_preloading = false;
		_frozenAt = null;
		_frozenTicks = 0;
	}

	// Ends this wait's generation and disposes what it preloaded (a preload finishing later disposes its own space).
	private static void LetGo()
	{
		IPreloadedSpace? space;
		lock (PreloadGate)
		{
			_gen++;
			space = _preloaded;
			_preloaded = null;
		}
		try { space?.Dispose(); }
		catch (Exception ex) when (ex.CanHandle()) { GameLog.Instance.WarnOnce("load-preload-dispose", $"loading screen: letting go of the preloaded terrain failed ({ex.GetType().Name}: {ex.Message})"); }
	}

	/// <summary>Main menu: nothing of the last world stays referenced (its session is gone with the preloads it held).</summary>
	internal static void Reset()
	{
		Active = false;
		_beganMs = -1;
		_probes = null;
		LetGo();
		_preloading = false;
		_frozenAt = null;
	}

	// --- parts ---

	private static Probe World(long now)
	{
		var transport = Engine.Y51Replication.Client;
		if (transport == null) return new Probe(false, 0, "waiting for the server's world");
		int building = transport.Building, frames = transport.FramesWaiting, staged = transport.StagedWaiting();
		long built = transport.EntityBatchesBuilt;
		// The server sends the world it shares with every player as one batch at admission; a world with nothing in it sends none.
		bool initial = built > 0 || now - _beganMs >= EmptyWorldMs;
		// A live world always has something arriving: what counts is that the backlog drains (nothing being built and no frame
		// left waiting after an inbound job) at least once a second, not that it is empty at the moment of looking.
		if (building == 0 && frames == 0) _idleMs = now;
		bool ready = initial && staged == 0 && _idleMs >= 0 && now - _idleMs <= IdleWithinMs;
		double fraction = built + building == 0 ? 0 : (double)built / (built + building);
		string detail = building > 0 ? $"building {building} object batch{(building == 1 ? "" : "es")}"
			: frames > 0 ? $"{frames} update{(frames == 1 ? "" : "s")} queued"
			: staged > 0 ? $"{staged} object{(staged == 1 ? "" : "s")} held by the server"
			: !initial ? "waiting for the server's objects" : "objects arriving";
		return new Probe(ready, initial ? fraction : fraction / 2, detail);
	}

	private static Probe Character(PlayerControllerComponent? controller, Entity? body)
	{
		if (body == null) return new Probe(false, 0, "waiting for your character");
		if (controller!.IsDirty) return new Probe(false, 0.5, "taking control");
		// A character's body is made when it enters the scene (RigidBodyComponent.OnAddedToScene sets _initialized).
		if (body.TryGet<CharacterPhysicsEntityComponent>() is { } physics && !physics._initialized) return new Probe(false, 0.75, "waiting for its physics");
		return Probe.Done;
	}

	private static Probe Grids(Session session, Vector3D at)
	{
		int near = 0, waiting = 0;
		foreach (Entity grid in session.GetEntitiesOfType<CubeGridComponent>())
		{
			if (SyncEngine.EngineCodec.IsDead(grid)) continue;
			var wt = grid.Data.GetWorldTransform();
			double edge = Vector3D.Distance(wt.Position, at);
			if (grid.Data.TryGet<BoundingBoxData>(out var bb))
			{
				var box = bb.BoundingBox;
				edge = Vector3D.Distance(WorldTransform.Transform((Vector3D)box.Center, in wt), at) - box.HalfExtents.Length();
			}
			if (edge > GridMeters) continue;
			near++;
			bool staged = grid.Data.Has<Keen.VRage.DCS.CoreData.StagingTag>();
			if (staged || grid.TryGet<GridPhysicsComponent>() is { _initialized: false }) waiting++;
		}
		if (waiting == 0) return Probe.Done;
		return new Probe(false, (double)(near - waiting) / near, $"{near - waiting} of {near} nearby grids built");
	}

	private static Probe Terrain(Session session, PlayerControllerComponent controller, Vector3D at)
	{
		if (session.TryGet<ISpaceProbeSystem>() is not { } probe) return Probe.Done;
		if (!_preloading || Vector3D.Distance(at, _preloadAt) > PreloadMoveMeters)
		{
			// Moved far (a teleport, a respawn ship): the old area is let go and the new one loaded, and the resource wait
			// (camera based) runs again there.
			IPreloadedSpace? old;
			int no;
			lock (PreloadGate)
			{
				old = _preloaded;
				_preloaded = null;
				_preloadDone = false;
				no = ++_preloadNo;
			}
			old?.Dispose();
			_resourcesStarted = _resourcesDone = false;
			_preloadAt = at;
			_preloading = true;
			Watch(probe.PreloadAsync(BoundingBoxD.CreateFromHalfExtent(at, PreloadHalfMeters), Precision.High), controller, _gen, no).SkipWait();
		}
		return _preloadDone ? Probe.Done : new Probe(false, 0.5, "voxel and planet physics around you");
	}

	// The preload holds what it loaded until disposed: kept while the screen is up, let go when it closes.
	private static async KTask Watch(Keen.VRage.Library.Threading.Task<IPreloadedSpace> preload, object lifetime, int gen, int no)
	{
		await KTask.SetLifetime(lifetime);
		IPreloadedSpace? space = null;
		try { space = await preload; }
		catch (Exception ex) when (ex.CanHandle())
		{
			GameLog.Instance.WarnOnce("load-preload", $"loading screen: preloading the terrain around the player failed ({ex.GetType().Name}: {ex.Message}); not waiting for it");
		}
		bool kept = false;
		lock (PreloadGate)
		{
			if (gen == _gen && no == _preloadNo)
			{
				_preloaded = space;
				_preloadDone = kept = true;
			}
		}
		if (!kept) space?.Dispose();
	}

	private static Probe Details(Session session, PlayerControllerComponent controller, Vector3D at, bool nearby, long now)
	{
		int flora = FloraPhysicsRecovery.PendingNear(at, FloraMeters);
		nearby &= flora == 0;
		if (!nearby) _nearbyMs = -1;
		else if (_nearbyMs < 0) _nearbyMs = now;
		if (!_resourcesStarted)
		{
			_resourcesStarted = true;
			if (session.TryGet<IClientResourceLoading>() is { } loading) WatchResources(loading.AwaitAllResourcesLoaded(), controller, _gen, ++_resourcesNo).SkipWait();
			else _resourcesDone = true;
		}
		if (_resourcesDone && flora == 0) return Probe.Done;
		// The game's resource wait also holds for optional distant textures (up to a minute; 21.6 s for two far terrain
		// textures on record): once everything near the player is ready it gets only WorldReadiness.TextureWaitMs more.
		if (nearby && !WorldReadiness.TexturesHeld(now - _nearbyMs))
		{
			if (!_texturesLeft) GameLog.Instance.Info($"loading screen: everything near the player is ready; not waiting longer than {WorldReadiness.TextureWaitMs / 1000} s for the game's textures (they finish loading in the world)");
			_texturesLeft = true;
			return Probe.Done;
		}
		return new Probe(false, _resourcesDone ? 0.75 : 0.25, flora > 0 ? $"{flora} flora sector{(flora == 1 ? "" : "s")} nearby" : "terrain detail and textures");
	}

	private static async KTask WatchResources(KTask loading, object lifetime, int gen, int no)
	{
		await KTask.SetLifetime(lifetime);
		try { await loading; }
		catch (Exception ex) when (ex.CanHandle())
		{
			GameLog.Instance.WarnOnce("load-resources", $"loading screen: waiting for the game's resources failed ({ex.GetType().Name}: {ex.Message}); not waiting for them");
		}
		if (gen == _gen && no == _resourcesNo) _resourcesDone = true;
	}

	// The player's own predicted character is a simulated body: a grid or deck under it that has not arrived yet would let
	// it fall while the screen is up (voxels already hold the simulation until their colliders exist, VoxelPhysicsComponent
	// WaitForChunkCreation). Held where it is with no velocity; a bigger move than gravity makes in a tick is the game or
	// SE2 Online placing it (a teleport, a correction from the server) and is kept.
	private static void Hold(Entity body)
	{
		if (body.TryGet<CharacterPhysicsEntityComponent>() is not { } physics || physics.CharacterPhysicsDefinition?.Simulated != true
			|| body.AsInterface<ISeatable>()?.CurrentSeat != null) { _frozenAt = null; return; }
		var wt = body.Data.GetWorldTransform();
		if (_frozenAt is not { } held || Vector3D.Distance(held, wt.Position) > 0.02)
		{
			_frozenAt = wt.Position;
			body.Data.Set(new RigidBodyData(default, default));
			return;
		}
		wt.Position = held;
		body.Data.SetWorldTransform(wt);
		body.Data.Set(new RigidBodyData(default, default));
		if (body.Data.TryGet<SoftTransform>(out var soft))
		{
			soft.Value = wt;
			body.Data.Set(soft);
		}
		_frozenTicks++;
	}
}
