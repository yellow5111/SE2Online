#if !PULSAR
using System;
using System.Collections.Generic;
using System.Linq;
using Keen.Game2.Simulation.GameSystems.BlockPlacement;
using Keen.Game2.Simulation.GameSystems.Ownership;
using Keen.Game2.Simulation.WorldObjects.Characters;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.MechanicalBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.Core.Game.Definitions;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.Components;
using Keen.VRage.DCS.ObjectBuilders;
using Keen.VRage.Library.Definitions;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Library.Utils;
using Keen.VRage.Physics.Data;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;
using Y51_SE2Online.SyncEngine;
using Keen.Game2.Client.GameSystems.BlockPlacement.BlockPlacer;
using Keen.Game2.Simulation.WorldObjects.CubeGrids.Physicss;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>
/// Test rig (-y51online:selftest-dock, server or co-op host): 40 s after the first player character appears, the server
/// spawns 12 m in front of it, in any game mode, four built pairs: a static Connector150 with a second one drifting down
/// onto it (two facings, one of them puts the connecting faces together), and a static armor cube with a LandingGear100
/// drifting down onto it (two facings). Every 30 s for 10 min it logs whether each connector is connected and each gear
/// locked, so the connector and landing-gear rows can be played from there. "selftest: dock: ..." lines.
/// </summary>
internal static class DockProbe
{
	private static readonly Guid Connector = new("c5d67afb-8335-4699-a14f-f40d1a2fe557");
	private static readonly Guid Gear = new("8574735f-e98b-487e-9662-21435f0ce3ed");
	private static readonly Guid Armor = new("2eacbbf2-d8fb-4a78-91dc-7b492517ef97");
	private const long WaitMs = 40_000, LogMs = 30_000, LogForMs = 600_000;

	private static long _seenMs, _spawnedMs, _logMs;
	private static readonly List<(string What, Entity[] Low, Entity[] High)> Pairs = new();

	public static void Tick(Session session)
	{
		if (!ReferenceEquals(session, MpPlugin.App?.ServerSession) || !MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "selftest-dock")) return;
		long now = MpClock.NowMs();
		try
		{
			if (_spawnedMs == 0)
			{
				var me = session.GetEntitiesOfType<CharacterLifeComponent>().FirstOrDefault(c => PlayerIdentities.IsHuman(TeamPermissions.OwnerOf(c)));
				if (me == null) return;
				if (_seenMs == 0) { _seenMs = now; GameLog.Instance.Info($"selftest: dock: player character seen; grids in {WaitMs / 1000} s"); return; }
				if (now - _seenMs < WaitMs) return;
				_spawnedMs = _logMs = now;
				Spawn(session, me.Data.GetWorldTransform());
				return;
			}
			if (now < _logMs + LogMs || now - _spawnedMs > LogForMs) return;
			_logMs = now;
			foreach (var (what, low, high) in Pairs) GameLog.Instance.Info($"selftest: dock: {what}: {State(low, high)}");
		}
		catch (Exception ex) { GameLog.Instance.WarnOnce("dock-probe", "selftest: dock: FAIL " + ex); }
	}

	private static string State(Entity[] low, Entity[] high)
	{
		if (low.Length == 0 || high.Length == 0 || low.Concat(high).Any(EngineCodec.IsDead)) return "a grid is gone";
		double gap = Vector3D.Distance(high[0].Data.GetWorldTransform().Position, low[0].Data.GetWorldTransform().Position);
		var parts = new List<string> { $"grids {gap:0.00} m apart" };
		foreach (var e in low.Concat(high).SelectMany(Blocks))
		{
			if (e.TryGet<ConnectorComponent>() is { } c) parts.Add($"connector {e} connected {c.IsConnected} to {c.ConnectedEntity?.ToString() ?? "nothing"}");
			if (e.TryGet<MagneticBlockComponent>() is { } m) parts.Add($"gear {e} locked {m.IsLocked}");
		}
		return string.Join("; ", parts);
	}

	private static IEnumerable<Entity> Blocks(Entity grid) => grid.TryGet<HierarchyComponent>()?.Children ?? Enumerable.Empty<Entity>();

	private static void Spawn(Session session, WorldTransform player)
	{
		Vector3 up = player.Orientation.GetUp(), forward = player.Orientation.GetForward(), right = player.Orientation.GetRight();
		var upright = player.Orientation;
		var forwardUp = Quaternion.CreateFromForwardUp(up, -forward);
		var forwardDown = Quaternion.CreateFromForwardUp(-up, forward);
		var upsideDown = Quaternion.CreateFromForwardUp(forward, -up);
		var pairs = new[]
		{
			("connector pair, block forward to block forward", Connector, forwardUp, Connector, forwardDown, -6f),
			("connector pair, block up to block up", Connector, upright, Connector, upsideDown, -2f),
			("landing gear on armor, gear upright", Armor, upright, Gear, upright, 2f),
			("landing gear on armor, gear upside down", Armor, upright, Gear, upsideDown, 6f),
		};
		foreach (var (what, lowPrefab, lowFace, highPrefab, highFace, side) in pairs)
		{
			var centre = player.Position + (Vector3D)(forward * 12 + right * side + up * 1.5f);
			float lowSize = Size(lowPrefab), highSize = Size(highPrefab);
			var low = SpawnBlock(session, lowPrefab, centre, lowFace, true, Vector3.Zero);
			var high = SpawnBlock(session, highPrefab, centre + (Vector3D)(up * (0.5f * (lowSize + highSize) + 0.75f)), highFace, false, -up * 0.5f);
			Pairs.Add((what, low, high));
			GameLog.Instance.Info($"selftest: dock: {what}: spawned {low.Length} + {high.Length} grid(s) at {centre.X:0.0}, {centre.Y:0.0}, {centre.Z:0.0}");
		}
	}

	private static PrefabDefinition Prefab(Guid id) =>
		Singleton<DefinitionManager>.Instance.TryGetDefinition(id, out PrefabDefinition? p) ? p! : throw new InvalidOperationException("no prefab " + id);

	private static float Size(Guid prefab) => Prefab(prefab).Get().Definition<CubeBlockComponent, CubeBlockDefinition>().BoundingBox.GetSizeIncludingMax().Y * CubeGridCoords.CELL_SIZE;

	// The block placer's own new-grid bundle (as PlaceStandalone builds it), moved so the block's centre sits at the point.
	private static Entity[] SpawnBlock(Session session, Guid prefab, Vector3D centre, Quaternion facing, bool isStatic, Vector3 velocity)
	{
		var placer = Singleton<DefinitionManager>.Instance.GetDefinitionsOfType<BlockPlacerEntityComponentDefinition>().First();
		var grid = placer.DefaultPrefabServerGrid.Get();
		var block = Prefab(prefab).Get();
		grid.OB<HierarchyComponent, HierarchyComponentObjectBuilder>().AddChild(block);
		var box = block.Definition<CubeBlockComponent, CubeBlockDefinition>().BoundingBox;
		var placed = new BlockPlacerEntityBundle(new EntityBundle(), box);
		placed.Add(grid, null, isRoot: true);
		placed.Add(block, null);
		var bundle = placed.Bundle.CloneBundle();
		var root = bundle.RootBuilders[0];
		bundle.TransformInvBundle(root.OB<WorldTransformComponent, WorldTransformComponentObjectBuilder>().Transform);
		var local = CubeGridCoords.GridToLocal(box).Center;
		bundle.TransformBundle(new WorldTransform(centre - (Vector3D)Vector3.Transform(local, facing), facing));
		var physics = root.OB<GridPhysicsComponent, GridPhysicsObjectBuilder>();
		physics.MotionType = isStatic ? BodyArgs.Motion.Static : BodyArgs.Motion.Dynamic;
		physics.LinearVelocity = velocity;
		return session.Get<IEntitySpawner>().SpawnBundle(bundle);
	}
}
#endif
