using System;
using HarmonyLib;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.Multiplayer.Replications.SceneSubset;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>
/// SE2 4.0 streams each client only part of the world (its scene subset): every object publishes a sphere and a client
/// gets the objects whose sphere holds what it controls. Keen sized that sphere by the object's apparent size (250 m to
/// 20 km). In an SE2 Online session the sphere follows SE2 Online's streaming rules (<see cref="StreamingRules"/>): an
/// object is sent while its nearest edge is within the streaming radius of the player, and leaves only past
/// <see cref="StreamingRules.OutFactor"/> times it. A radius of 0 leaves the subset off, as SE2 4.0 ships: the whole world.
/// </summary>
internal static class Streaming
{
	private static readonly System.Reflection.MethodInfo? SetEnforced = AccessTools.PropertySetter(typeof(SceneSubsetCoreConfiguration), nameof(SceneSubsetCoreConfiguration.EnforceSceneSubset));

	private static readonly AccessTools.FieldRef<SceneSubsetServerSessionComponent, SceneSubsetCoreConfiguration> Config =
		AccessTools.FieldRefAccess<SceneSubsetServerSessionComponent, SceneSubsetCoreConfiguration>("_configuration");

	private static readonly System.Reflection.MethodInfo? RepublishAll = AccessTools.Method(typeof(SceneSubsetServerSessionComponent), "RepublishAll");

	private static SceneSubsetServerSessionComponent? _applied;

	/// <summary>The radius the subset follows; negative while no SE2 Online host session has set one (Keen's own rule).</summary>
	private static double _radius = -1;

	public static void Install(Harmony harmony)
	{
		harmony.Patch(AccessTools.Method(typeof(SceneSubsetServerSessionComponent), "ComputeRadii"), prefix: new HarmonyMethod(typeof(Streaming), nameof(ComputeRadiiPrefix)));
	}

	// Visibility is the sphere a client must be inside; the margin only lets the object move that far before it republishes.
	private static bool ComputeRadiiPrefix(double boundingRadius, ref (double Visibility, double Margin) __result)
	{
		if (_radius <= 0) return true;
		double visibility = _radius + boundingRadius;
		__result = (visibility, Math.Min(250, visibility * 0.05));
		return false;
	}

	private static readonly System.Reflection.MethodInfo? ApplyInitialObservedState = AccessTools.Method(typeof(SceneSubsetServerSessionComponent), "ApplyInitialObservedState");

	private static bool _switchLogged;

	/// <summary>Server tick of a hosted world: brings the subset in line with the current setting.</summary>
	public static void Tick(Session session, double radius, int remoteClients, IMpLog log)
	{
		if (session.SessionComponents.TryGet<SceneSubsetServerSessionComponent>() is not { } subset) return;
		if (ReferenceEquals(subset, _applied) && (radius > 0 ? radius == _radius : _radius < 0)) return;
		bool enforce = radius > 0;
		if (Config(subset).EnforceSceneSubset != enforce)
		{
			// Switched on or off once per world, when its host session starts: a later switch would leave the objects of the
			// other mode without a path to new players (on: in the broadcast set; off: never in it).
			if (ReferenceEquals(subset, _applied))
			{
				if (!_switchLogged) log.Info("streaming: switching between the whole world and a radius applies after the world is loaded again");
				_switchLogged = true;
				return;
			}
			SetEnforced?.Invoke(Config(subset), new object[] { enforce });
			if (enforce) AdoptLoadedWorld(session, subset, remoteClients, log);
		}
		subset.Tuning.MembershipExitMargin = enforce ? radius * (StreamingRules.OutFactor - 1) : 1000;
		_applied = subset;
		_radius = enforce ? radius : -1;
		_switchLogged = false;
		// Objects keep the sphere they published until they move; parked ones (stations, wrecks) need it now.
		if (enforce) RepublishAll?.Invoke(subset, null);
		log.Info(radius > 0 ? $"streaming: players are sent everything within {radius:0} m of them (kept to {radius * StreamingRules.OutFactor:0} m)" : "streaming: players are sent the whole world");
	}

	// The subset goes on after the world loaded (the host session starts then), but SE2 4.0 decides at load whether an object
	// is broadcast: everything loaded went into the broadcast set, which every joining client receives whole
	// (ReplicationServerSessionComponent.ReplicateInitialEntitySetToPeer), and those objects never entered a client's subset,
	// so they never left it either: a 5000 m radius trimmed nothing. Subset-managed objects (roots with bounds, and their
	// children; planets stay broadcast, AlwaysReplicatedTag) leave the broadcast set, so a client gets them as they come
	// into its radius, and roots nobody is near stop recording changes, as roots loaded under the subset do.
	private static void AdoptLoadedWorld(Session session, SceneSubsetServerSessionComponent subset, int remoteClients, IMpLog log)
	{
		if (session.SessionComponents.TryGet<Keen.VRage.Multiplayer.Replications.ReplicationServerSessionComponent>() is not { } replication) return;
		int moved = 0;
		foreach (var entity in System.Linq.Enumerable.ToList(replication.ReplicatedEntities))
		{
			var root = entity.Data.GetTopLevelParent();
			if (!SceneSubsetServerSessionComponent.IsSubsetManaged(root)) continue;
			replication.RemoveReplicatedEntity(entity);
			moved++;
			if (!entity.Data.Has<Keen.VRage.Core.Game.Data.ParentData>()) ApplyInitialObservedState?.Invoke(subset, new object[] { root, entity.DEntity });
		}
		log.Info($"streaming: {moved} loaded objects now reach players by distance" + (remoteClients > 0 ? $" ({remoteClients} player(s) already here keep what they have)" : ""));
	}

	/// <summary>The hosted world ended: the next world starts from Keen's rule until its host sets ours.</summary>
	public static void Reset()
	{
		_applied = null;
		_radius = -1;
		_switchLogged = false;
	}
}
