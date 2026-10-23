using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Multiplayer.Replications.FailureStrategies;
using Keen.VRage.Voxels.Components;
using Keen.VRage.Voxels.Components.Flora;
using Keen.VRage.Voxels.Storage.Planet.PlanetEnvironment;
using Y51_SE2Online.Core;
using Y51_SE2Online.SyncEngine;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>
/// A player's game builds the colliders of planet flora (trees, rocks) for a sector when the server's
/// OnCreateSectorPhysics signal names the sector and its physics entity. The signal is dropped when that entity has not
/// reached the player yet (SkipOnReplicationFail), which is nearly always (1 of 19 sectors got one on a dedicated rig),
/// so the player's game had almost no flora colliders: its predicted character flew and walked through trees the
/// server's crashed into, 3-4 m off with snaps. The entity itself does not say its sector (the replicated SectorId is
/// left default), but the server spawns it at the sector's centre on the planet's mean radius: a sector physics entity
/// still unbuilt a while after it arrived is matched to the sector of this game's own flora at that point and handed
/// over as the signal would have. The flora instances are this game's own (generated from the same seed).
/// </summary>
internal static class FloraPhysicsRecovery
{
	private const long WaitMs = 1000;
	private const double MatchMeters = 2;

	private static readonly Type? ClientFlora = AccessTools.TypeByName("Keen.VRage.Voxels.Client.Components.PlanetFloraClientComponent");
	private static readonly System.Reflection.MethodInfo? CreateSectorPhysics = ClientFlora == null ? null : AccessTools.Method(ClientFlora, "OnCreateSectorPhysics");
	private static readonly System.Reflection.MethodInfo? TryGetClientFlora = ClientFlora == null ? null
		: typeof(Entity).GetMethods().First(m => m.Name == "TryGet" && m.IsGenericMethodDefinition).MakeGenericMethod(ClientFlora);
	private static readonly AccessTools.FieldRef<PlanetFloraSectorPhysicsComponent, bool> Initialized = AccessTools.FieldRefAccess<PlanetFloraSectorPhysicsComponent, bool>("_initialized");
	private static readonly System.Reflection.FieldInfo? SectorsField = AccessTools.Field(typeof(PlanetFloraComponent), "_sectors");
	private static readonly System.Reflection.MethodInfo? AverageRadius = AccessTools.Method(typeof(PlanetFloraComponent), "GetAverageRadius");
	private static readonly System.Reflection.PropertyInfo? StorageProperty = AccessTools.Property(typeof(PlanetFloraComponent), "Storage");

	private static readonly System.Reflection.FieldInfo? PendingBuilds = ClientFlora == null ? null : AccessTools.Field(ClientFlora, "_pendingSectorPhysics");

	// Physics entities seen arriving and still unbuilt, and when; kept until built, named by the server's signal or gone.
	private static readonly Dictionary<PlanetFloraSectorPhysicsComponent, long> Pending = new();

	// The ones built here: the server's own signal coming later must not build them again (a second build of a sector
	// still in progress throws in the game's pending table). Weak, so a world left behind is not kept alive by it.
	private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<PlanetFloraSectorPhysicsComponent, object> Recovered = new();
	private static long _nextMs, _recovered, _unmatched, _duplicates;

	public static void Install(Harmony harmony)
	{
		if (CreateSectorPhysics == null || SectorsField == null || AverageRadius == null || StorageProperty == null || PendingBuilds == null)
			throw new MissingMethodException("PlanetFloraClientComponent.OnCreateSectorPhysics / PlanetFloraComponent internals");
		harmony.Patch(AccessTools.Method(typeof(PlanetFloraSectorPhysicsComponent), "Init", new[] { typeof(PlanetFloraSectorPhysicsComponentObjectBuilder) }),
			postfix: new HarmonyMethod(typeof(FloraPhysicsRecovery), nameof(InitPostfix)));
		harmony.Patch(CreateSectorPhysics, prefix: new HarmonyMethod(typeof(FloraPhysicsRecovery), nameof(SignalPrefix)));
	}

	// The game's own build (the server's signal) and the one this class starts: a sector already built or being built is
	// not built again.
	private static bool SignalPrefix(object __instance, SectorId sectorId, SkipOnReplicationFail<Entity> sectorPhysicsEntity)
	{
		if (((Entity?)sectorPhysicsEntity)?.TryGet<PlanetFloraSectorPhysicsComponent>() is not { } physics) return true;
		lock (Pending) Pending.Remove(physics);
		bool building = PendingBuilds?.GetValue(__instance) is System.Collections.IDictionary pending && pending.Contains(sectorId);
		if (!Recovered.TryGetValue(physics, out _) && !Initialized(physics) && !building) return true;
		if (_duplicates++ == 0) GameLog.Instance.Info("flora: the server's signal for a flora sector came after this game had built its colliders; not built twice");
		return false;
	}

	/// <summary>Main menu: nothing of the last world stays referenced here.</summary>
	internal static void Reset()
	{
		lock (Pending) Pending.Clear();
	}

	private static void InitPostfix(PlanetFloraSectorPhysicsComponent __instance)
	{
		lock (Pending) Pending[__instance] = MpClock.NowMs();
	}

	/// <summary>Client session thread, after each update.</summary>
	public static void ClientTick(Session session)
	{
		long now = MpClock.NowMs();
		if (now < _nextMs || ClientFlora == null) return;
		_nextMs = now + 500;
		List<PlanetFloraSectorPhysicsComponent> due = new();
		lock (Pending)
		{
			foreach (var (physics, seen) in Pending.ToList())
			{
				// Kept while it is unbuilt and alive: its sector may be generated here long after the entity arrived.
				if (physics.Entity == null || EngineCodec.IsDead(physics.Entity) || Initialized(physics)) Pending.Remove(physics);
				else if (now - seen >= WaitMs) due.Add(physics);
			}
		}
		if (due.Count == 0) return;
		var centres = new Dictionary<object, List<(SectorId Sector, Vector3D At)>>();
		foreach (var physics in due)
		{
			var entity = physics.Entity;
			var at = entity.Data.GetWorldTransform().Position;
			var planet = entity.TryGet<FloraSectorComponent>()?.PlanetEntity ?? NearestPlanet(session, at);
			// The server scene's planets (a co-op host) have no client flora: their sectors are built there already.
			if (planet == null || TryGetClientFlora!.Invoke(planet, new object[] { default(Keen.VRage.Library.Utils.StringId) }) is not { } flora) continue;
			try
			{
				if (!centres.TryGetValue(flora, out var list)) centres[flora] = list = Centres((PlanetFloraComponent)flora);
				SectorId? match = null;
				foreach (var c in list)
					if (Vector3D.Distance(c.At, at) < MatchMeters) { match = c.Sector; break; }
				// Not generated here yet: tried again on the next pass.
				if (match is not { } sector) { _unmatched++; continue; }
				lock (Pending) Pending.Remove(physics);
				CreateSectorPhysics!.Invoke(flora, new object[] { sector, (SkipOnReplicationFail<Entity>)entity });
				Recovered.AddOrUpdate(physics, true);
				if (_recovered++ == 0) GameLog.Instance.Info("flora: the server's signal for a flora sector's colliders did not reach this game; building them here");
			}
			catch (Exception ex)
			{
				lock (Pending) Pending.Remove(physics);
				GameLog.Instance.WarnOnce("flora-recover", $"flora: could not build a flora sector's colliders ({(ex.InnerException ?? ex).GetType().Name}: {(ex.InnerException ?? ex).Message})");
			}
		}
	}

	// Where the server puts each sector's physics entity (PlanetFloraServerComponent.SpawnSectorPrefab), for the sectors
	// this game's flora has generated.
	private static List<(SectorId Sector, Vector3D At)> Centres(PlanetFloraComponent flora)
	{
		var list = new List<(SectorId, Vector3D)>();
		if (SectorsField!.GetValue(flora) is not System.Collections.IDictionary sectors) return list;
		float radius = (float)AverageRadius!.Invoke(flora, null)!;
		var storage = (WorldTransform)Traverse.Create(StorageProperty!.GetValue(flora)).Property("Transform").GetValue()!;
		foreach (LogicalFloraSector sector in sectors.Values)
		{
			var b = sector.Builder;
			var local = Keen.VRage.Voxels.Storage.Bitmaps.CubemapCoordinates.GetSpherePositionFromProjection<Vector3, Vector2, float>((Keen.VRage.Voxels.Storage.Bitmaps.CubemapFace)b.SectorId.FaceIndex, b.Range.Center, radius);
			list.Add((b.SectorId, WorldTransform.Transform((Vector3D)local, storage)));
		}
		return list;
	}

	internal static string Stats => $"{_recovered} flora sectors built without the server's signal ({_unmatched} tries before this game had generated the sector, {_duplicates} late signals not built twice)";

	/// <summary>Flora sector physics entities in <paramref name="session"/> whose colliders are built.</summary>
	internal static int Built(Session session)
		=> session.GetEntitiesOfType<PlanetFloraSectorPhysicsComponent>().Count(e => e.TryGet<PlanetFloraSectorPhysicsComponent>() is { } c && Initialized(c));

	private static Entity? NearestPlanet(Session session, Vector3D at)
		=> session.GetEntitiesOfType<VoxelPlanetComponent>().OrderBy(p => Vector3D.DistanceSquared(p.Data.GetWorldTransform().Position, at)).FirstOrDefault();
}
