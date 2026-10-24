using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Keen.Game2.Client.GameSystems.BlockPlacement.BlockPlacer;
using Keen.Game2.Client.GameSystems.Render;
using Keen.Game2.Simulation.GameSystems.BlockPlacement.Projections;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.Core.Game.Definitions;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.Components;
using Keen.VRage.DCS.Definitions;
using Keen.VRage.DCS.ObjectBuilders;
using Keen.VRage.Library.Definitions;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Library.Utils;
using Keen.VRage.Render.Data;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// Other players' placement holograms: SE2's own previewer (the projection the local block placer spawns in
/// BlockPlacementPreview.UpdatePreviewVolume), spawned in this client's session only and moved to where the builder's
/// hologram is. They are local entities, so nothing about them reaches the host (SignalCodec and ClientStories refuse
/// anything that names an entity the host does not have), and the host's game never holds them.
/// A projection is something tools can target, so these are hidden from every way the game finds one: every frame
/// they are taken out of GridProjectionsSessionComponent.Projections (the set behind RaycastWorldForProjections,
/// SphereTestWorldForProjections, AllProjections and AnyProjectionsOverlap), and the per-projection lookups the
/// placer, welder, grinder, area tools and tool UI call (Raycast, QuerySphere, TryGetBlockAt, IsAreaEmpty, TryGetOB)
/// answer "nothing here" for them, which also covers the frame between spawning and the first strip.
/// </summary>
internal static class BuildGhostModels
{
	/// <summary>Hologram blocks across every builder at once; past it the rest keep the outline.</summary>
	internal const int MaxTotalBlocks = 300;

	/// <summary>The local client session's UI3D (set from its update); holograms live only in that session.</summary>
	internal static Keen.Game2.Client.GameSystems.UI3D.UI3DSessionComponent ClientUi3D;

	private static readonly Dictionary<ushort, Model> Models = new();
	private static readonly List<(ushort Builder, BuildGhost Ghost, GhostShape Shape)> Live = new();
	private static readonly List<ushort> Gone = new();
	private static HashSet<Entity> _tagged = new(ReferenceEqualityComparer.Instance);
	private static Session _owner;
	private static BlockPlacerEntityComponentDefinition _placer;
	private static bool _failed;

	private sealed class Model
	{
		public uint ShapeId;
		public PreviewHostComponent Preview;
		public RelativeTransform InverseRoot;
		public bool? Blocked;
		public int Blocks;
		public readonly GhostSmoother Smooth = new();
	}

	internal static int Count => Models.Count;

	internal static void Install(Harmony harmony)
	{
		harmony.Patch(AccessTools.Method(typeof(Session), "Update", new[] { typeof(bool) }), prefix: Hm(nameof(SessionUpdatePrefix)));
		var aabb = typeof(GridProjectionAABBComponent);
		harmony.Patch(AccessTools.Method(aabb, nameof(GridProjectionAABBComponent.Raycast)), prefix: Hm(nameof(Untouchable)));
		harmony.Patch(AccessTools.Method(aabb, nameof(GridProjectionAABBComponent.QuerySphere)), prefix: Hm(nameof(Untouchable)));
		harmony.Patch(AccessTools.Method(aabb, nameof(GridProjectionAABBComponent.TryGetBlockAt), new[] { typeof(Vector3D) }), prefix: Hm(nameof(NoBlock)));
		harmony.Patch(AccessTools.Method(aabb, nameof(GridProjectionAABBComponent.TryGetBlockAt), new[] { typeof(int), typeof(Vector3I) }), prefix: Hm(nameof(NoBlock)));
		harmony.Patch(AccessTools.Method(aabb, nameof(GridProjectionAABBComponent.IsAreaEmpty)), prefix: Hm(nameof(Empty)));
		harmony.Patch(AccessTools.Method(typeof(ProjectionSourceComponent), nameof(ProjectionSourceComponent.TryGetOB)), prefix: Hm(nameof(NoOb)));
		static HarmonyMethod Hm(string name) => new(typeof(BuildGhostModels), name);
	}

	internal static bool IsGhost(Entity entity)
	{
		var tagged = _tagged;
		return tagged.Count != 0 && entity != null && tagged.Contains(entity);
	}

	private static bool Untouchable(Component __instance) => !IsGhost(__instance.Entity);

	private static bool NoBlock(Component __instance, ref ProjectionSourceComponent.ChildRef? __result)
	{
		if (!IsGhost(__instance.Entity)) return true;
		__result = null;
		return false;
	}

	private static bool Empty(Component __instance, ref bool __result)
	{
		if (!IsGhost(__instance.Entity)) return true;
		__result = true;
		return false;
	}

	private static bool NoOb(Component __instance, ref EntityObjectBuilder __result)
	{
		if (!IsGhost(__instance.Entity)) return true;
		__result = null;
		return false;
	}

	// Main thread, before the session's own update: the only place entities are spawned and deleted.
	private static void SessionUpdatePrefix(Session __instance, bool doEntityLifetimeUpdates)
	{
		if (!doEntityLifetimeUpdates || ClientUi3D == null || __instance.SessionComponents.TryGet<Keen.Game2.Client.GameSystems.UI3D.UI3DSessionComponent>() != ClientUi3D) return;
		try
		{
			Reconcile(__instance);
		}
		catch (Exception ex)
		{
			_failed = true;
			GameLog.Instance.Warn("build ghosts: holograms off for this session, outlines only (" + ex.GetType().Name + ": " + ex.Message + ")");
			try { RemoveAll(__instance); } catch (Exception) { Models.Clear(); }
		}
	}

	private static void Reconcile(Session session)
	{
		if (_owner != session)
		{
			// The old session's holograms went with it.
			Models.Clear();
			_tagged = new HashSet<Entity>(ReferenceEqualityComparer.Instance);
			_owner = session;
			_failed = false;
		}
		if (!BuildGhosts.InSession || _failed)
		{
			if (Models.Count > 0) RemoveAll(session);
			return;
		}
		BuildGhosts.Board.Live(MpClock.NowMs(), Live);
		int budget = MaxTotalBlocks;
		Gone.Clear();
		Gone.AddRange(Models.Keys);
		foreach (var (builder, ghost, shape) in Live)
		{
			if (shape == null || ghost.Pose is not { } pose || shape.Blocks.Length > budget) continue;
			if (Models.TryGetValue(builder, out var model) && (model.ShapeId != shape.Id || EngineCodec.IsDead(model.Preview.Entity)))
			{
				Remove(session, builder);
				model = null;
			}
			model ??= Spawn(session, builder, shape, pose);
			if (model == null) continue;
			budget -= model.Blocks;
			Gone.Remove(builder);
			Place(model, pose, (ghost.Flags & BuildGhost.Blocked) != 0);
		}
		foreach (ushort builder in Gone) Remove(session, builder);
		if (Models.Count == 0) return;
		var projections = session.Get<GridProjectionsSessionComponent>().Projections;
		foreach (var model in Models.Values) projections.Remove(model.Preview.Entity.Get<GridProjectionAABBComponent>());
	}

	private static Model Spawn(Session session, ushort builder, GhostShape shape, GhostPose pose)
	{
		var defs = Singleton<DefinitionManager>.Instance;
		var placer = _placer ??= defs.GetDefinitionsOfType<BlockPlacerEntityComponentDefinition>().First();
		var prefabs = defs.GetPostProcessor<DefaultPrefabsPostProcessor>();
		EntityObjectBuilder grid = placer.DefaultPrefabServerGrid.Get();
		var hierarchy = grid.OB<HierarchyComponent, HierarchyComponentObjectBuilder>();
		var tint = new ColorHSV(BuildGhosts.Hue(builder), 0.6f, 0.9f, 1f);
		var blocks = new List<EntityObjectBuilder>(shape.Blocks.Length);
		foreach (var b in shape.Blocks)
		{
			if (!defs.TryGetDefinition(shape.Types[b.Type], out EntityCompositeDefinition composite) || prefabs.GetDefaultPrefabFor(composite) is not { } prefab) continue;
			EntityObjectBuilder ob = prefab.Get();
			if (!ob.Has<CubeBlockComponent>()) continue;
			ob.GetChildTransform() = new RelativeTransform(new Vector3(b.X, b.Y, b.Z), new Quaternion(b.Qx, b.Qy, b.Qz, b.Qw));
			ob.OB<CubeBlockComponent, CubeBlockObjectBuilder>().Color = tint;
			hierarchy.AddChild(ob);
			blocks.Add(ob);
		}
		if (blocks.Count == 0) return null;
		var bundle = new EntityBundle();
		bundle.Add(grid, null, isRoot: true);
		foreach (var ob in blocks) bundle.Add(ob, null);
		var inverseRoot = RelativeTransform.Inverse(bundle.GetRelativeTransform(0));
		EntityObjectBuilder hostOb = placer.PreviewerPrefab.Get();
		hostOb.OB<WorldTransformComponent, WorldTransformComponentObjectBuilder>().Transform = HostTransform(pose, inverseRoot);
		Entity entity = session.Get<IEntitySpawner>().SpawnEntity(hostOb);
		var tagged = new HashSet<Entity>(_tagged, ReferenceEqualityComparer.Instance) { entity };
		_tagged = tagged;
		var preview = entity.Get<PreviewHostComponent>();
		preview.UseBuildStages = false;
		preview.ForcePhysicsMaterializationAround = false;
		entity.Get<ProjectionSourceComponent>().ProjectionData = bundle;
		var model = new Model { ShapeId = shape.Id, Preview = preview, InverseRoot = inverseRoot, Blocks = blocks.Count };
		Models[builder] = model;
		return model;
	}

	private static WorldTransform HostTransform(GhostPose p, RelativeTransform inverseRoot) =>
		new WorldTransform(new Vector3D(p.Position.X, p.Position.Y, p.Position.Z), new Quaternion(p.Qx, p.Qy, p.Qz, p.Qw)) * inverseRoot;

	private static void Place(Model model, GhostPose pose, bool blocked)
	{
		var (p, x, y, z, w) = model.Smooth.Step(pose.ShapeId, pose.Position, pose.Qx, pose.Qy, pose.Qz, pose.Qw, MpClock.NowMs());
		WorldTransform at = HostTransform(pose with { Position = p, Qx = x, Qy = y, Qz = z, Qw = w }, model.InverseRoot);
		model.Preview.Data.SetWorldTransform(in at);
		// Before its preview grids exist an opacity change would not reach the blocks made later.
		if (model.Blocked == blocked || model.Preview._previewGrids.Count == 0) return;
		model.Blocked = blocked;
		if (blocked) model.Preview.SetOpacity(HologramState.ProjectorBlocked.GetStateEncodedAsOpacity());
		else model.Preview.Show();
	}

	private static void Remove(Session session, ushort builder)
	{
		if (!Models.Remove(builder, out var model)) return;
		Entity entity = model.Preview.Entity;
		if (!EngineCodec.IsDead(entity)) session.Get<IEntitySpawner>().DeleteEntity(entity);
		var tagged = new HashSet<Entity>(_tagged, ReferenceEqualityComparer.Instance);
		tagged.Remove(entity);
		_tagged = tagged;
	}

	private static void RemoveAll(Session session)
	{
		foreach (ushort builder in Models.Keys.ToList()) Remove(session, builder);
	}
}
