using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
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
/// OnCreateSectorPhysics signal names the sector's physics entity. The server sends it the moment it spawns that entity,
/// and the signal is dropped when the entity has not reached the player yet (SkipOnReplicationFail): on a dedicated
/// server every one was (0 of 18 sectors built), so the player's game had no flora colliders at all, its predicted
/// character ran through trees the server's stopped at and was pulled back metres. A sector physics entity that is
/// still unbuilt a while after it arrived is handed to the planet's flora as the signal would have.
/// </summary>
internal static class FloraPhysicsRecovery
{
	private const long WaitMs = 2000;

	private static readonly Type? ClientFlora = AccessTools.TypeByName("Keen.VRage.Voxels.Client.Components.PlanetFloraClientComponent");
	private static readonly System.Reflection.MethodInfo? CreateSectorPhysics = ClientFlora == null ? null : AccessTools.Method(ClientFlora, "OnCreateSectorPhysics");
	private static readonly System.Reflection.MethodInfo? TryGetClientFlora = ClientFlora == null ? null
		: typeof(Entity).GetMethods().First(m => m.Name == "TryGet" && m.IsGenericMethodDefinition).MakeGenericMethod(ClientFlora);
	private static readonly AccessTools.FieldRef<PlanetFloraSectorPhysicsComponent, bool> Initialized = AccessTools.FieldRefAccess<PlanetFloraSectorPhysicsComponent, bool>("_initialized");

	// Physics components seen being made, with their sector and when.
	private static readonly Dictionary<PlanetFloraSectorPhysicsComponent, (SectorId Sector, long SeenMs)> Pending = new();

	// Sectors whose signal did arrive since their physics entity came: their colliders are built (or being built) already.
	// A sector with no collider-bearing flora never reports itself built, and a second build of one still in progress
	// throws in Keen's pending table.
	private static readonly HashSet<SectorId> Signaled = new();
	private static long _nextMs, _recovered;

	public static void Install(Harmony harmony)
	{
		if (CreateSectorPhysics == null) throw new MissingMethodException("PlanetFloraClientComponent.OnCreateSectorPhysics");
		harmony.Patch(AccessTools.Method(typeof(PlanetFloraSectorPhysicsComponent), "Init", new[] { typeof(PlanetFloraSectorPhysicsComponentObjectBuilder) }),
			postfix: new HarmonyMethod(typeof(FloraPhysicsRecovery), nameof(InitPostfix)));
		harmony.Patch(CreateSectorPhysics, prefix: new HarmonyMethod(typeof(FloraPhysicsRecovery), nameof(SignalPrefix)));
	}

	private static void SignalPrefix(SectorId sectorId)
	{
		lock (Pending) Signaled.Add(sectorId);
	}

	private static void InitPostfix(PlanetFloraSectorPhysicsComponent __instance, PlanetFloraSectorPhysicsComponentObjectBuilder ob)
	{
		lock (Pending)
		{
			Pending[__instance] = (ob.SectorId, MpClock.NowMs());
			Signaled.Remove(ob.SectorId);
		}
	}

	/// <summary>Client session thread, after each update.</summary>
	public static void ClientTick(Session session)
	{
		long now = MpClock.NowMs();
		if (now < _nextMs || ClientFlora == null) return;
		_nextMs = now + 500;
		List<(PlanetFloraSectorPhysicsComponent Physics, SectorId Sector)> due = new();
		lock (Pending)
		{
			foreach (var (physics, (sector, seen)) in Pending.ToList())
			{
				if (physics.Entity == null || EngineCodec.IsDead(physics.Entity) || Initialized(physics) || Signaled.Contains(sector)) Pending.Remove(physics);
				else if (now - seen >= WaitMs)
				{
					Pending.Remove(physics);
					due.Add((physics, sector));
				}
			}
		}
		foreach (var (physics, sector) in due)
		{
			var entity = physics.Entity;
			var planet = entity.TryGet<FloraSectorComponent>()?.PlanetEntity ?? NearestPlanet(session, entity.Data.GetWorldTransform().Position);
			// The server scene's planets (a co-op host) have no client flora: their sectors are built there already.
			if (planet == null || TryGetClientFlora!.Invoke(planet, new object[] { default(Keen.VRage.Library.Utils.StringId) }) is not { } flora) continue;
			try
			{
				CreateSectorPhysics!.Invoke(flora, new object[] { sector, (SkipOnReplicationFail<Entity>)entity });
				if (_recovered++ == 0) GameLog.Instance.Info("flora: the server's signal for a flora sector's colliders did not reach this game; building them here");
			}
			catch (Exception ex)
			{
				GameLog.Instance.WarnOnce("flora-recover", $"flora: could not build a flora sector's colliders ({(ex.InnerException ?? ex).GetType().Name}: {(ex.InnerException ?? ex).Message})");
			}
		}
	}

	internal static string Stats => $"{_recovered} flora sectors built without the server's signal";

	/// <summary>Flora sector physics entities in <paramref name="session"/> whose colliders are built.</summary>
	internal static int Built(Session session)
		=> session.GetEntitiesOfType<PlanetFloraSectorPhysicsComponent>().Count(e => e.TryGet<PlanetFloraSectorPhysicsComponent>() is { } c && Initialized(c));

	private static Entity? NearestPlanet(Session session, Vector3D at)
		=> session.GetEntitiesOfType<VoxelPlanetComponent>().OrderBy(p => Vector3D.DistanceSquared(p.Data.GetWorldTransform().Position, at)).FirstOrDefault();
}
