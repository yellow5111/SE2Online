#if !PULSAR
using System;
using System.Linq;
using Keen.Game2.Client.GameSystems.BlockPlacement.BlockPlacer;
using Keen.Game2.Client.GameSystems.PlayerControl;
using Keen.Game2.Simulation.GameSystems.BlockPlacement;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
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
using Keen.VRage.Library.Threading;
using Keen.VRage.Library.Utils;
using Y51_SE2Online.Core;
using Y51_SE2Online.SyncEngine;

namespace Y51_SE2Online.Engine;

/// <summary>
/// Test rig (-y51online:selftest-blocks, joined game): 40 s after joining, every 60 s, this game builds an armor block
/// next to the nearest grid through the game's own placement story, checks the block appears in its own scene (the server
/// built it and replicated it back), then deletes it the same way and checks it is gone. Logs "actions: ..." lines.
/// The same block placement requests a player's build tool sends (BlockPlacerNetworkStories.PlaceOnGrid/DeleteCubeBlock).
/// </summary>
internal static class ActionProbe
{
	private static bool Requested => MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "selftest-blocks");

	private static long _nextMs;
	private static bool _busy;
	public static int Built, Deleted, Failed;

	/// <summary>Client session thread, while playing on a joined game.</summary>
	public static void Tick(Session session)
	{
		SpawnPick();
		if (!Requested || _busy) return;
		long now = MpClock.NowMs();
		if (_nextMs == 0) { _nextMs = now + 40_000; return; }
		if (now < _nextMs) return;
		_nextMs = now + 60_000;
		_busy = true;
		GameLog.Instance.Info("actions: building and deleting a block next to the nearest grid");
		Run(session).SkipWait();
	}

	private static object? _pickedMenu;

	// -y51online:selftest-spawn: a test player takes the first spawn the host offers, as a person would from the menu.
	private static void SpawnPick()
	{
		if (!MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "selftest-spawn") || KeenNetcode.KeenRespawn.Menu is not { } menu || ReferenceEquals(menu, _pickedMenu)) return;
		int index = Array.FindIndex(menu.Options, o => o.Available);
		if (index < 0) return;
		_pickedMenu = menu;
		GameLog.Instance.Info($"selftest: spawning at menu option {index}");
		KeenNetcode.KeenRespawn.Choose(index);
	}

	private static async Task Run(Session session)
	{
		var players = session.SessionComponents.TryGet<ClientPlayersSessionComponent>();
		if (players == null) { _busy = false; return; }
		// A session component as the task's lifetime: the Session itself has no lifetime adapter.
		await Task.SetLifetime(players);
		try
		{
			var character = players.LocalPlayerController?.GetBottomControllable()?.Entity;
			if (character == null) { GameLog.Instance.Info("actions: no character yet"); return; }
			var at = character.Data.GetWorldTransform().Position;
			CubeGridComponent? grid = null;
			double best = 500;
			foreach (Entity e in session.GetEntitiesOfType<CubeGridComponent>())
			{
				var g = e.TryGet<CubeGridComponent>();
				if (g?.CubeBlockStorage == null || g.CubeBlockStorage.GetAllCubeBlocks().Length == 0) continue;
				double d = Vector3D.Distance(at, e.Data.GetWorldTransform().Position);
				if (d < best) { best = d; grid = g; }
			}
			if (grid == null) { GameLog.Instance.Info("actions: no grid within 500 m"); return; }
			var blocks = grid.CubeBlockStorage.GetAllCubeBlocks().ToArray();
			Vector3I cell = default;
			bool found = false;
			// Blocks nearest the player first: the server only builds within the player's reach.
			var ordered = blocks.Where(b => b != null).OrderBy(b => Vector3D.DistanceSquared(at, b.Entity.Data.GetWorldTransform().Position)).Take(200).ToArray();
			if (ordered.Length > 0) GameLog.Instance.Info($"actions: nearest block of '{grid.Entity}' is {Vector3D.Distance(at, ordered[0].Entity.Data.GetWorldTransform().Position):0.0} m away");
			foreach (var block in ordered)
			{
				foreach (var step in new[] { new Vector3I(1, 0, 0), new Vector3I(-1, 0, 0), new Vector3I(0, 1, 0), new Vector3I(0, -1, 0), new Vector3I(0, 0, 1), new Vector3I(0, 0, -1) })
				{
					var candidate = block.AABB.Max + step;
					if (step.X < 0 || step.Y < 0 || step.Z < 0) candidate = block.AABB.Min + step;
					if (BlockAt(grid, candidate) == null) { cell = candidate; found = true; break; }
				}
				if (found) break;
			}
			if (!found) { GameLog.Instance.Info("actions: no free cell next to the grid"); return; }
			var prefab = ArmorPrefab();
			if (prefab == null) { GameLog.Instance.Warn("actions: no armor block prefab"); Failed++; return; }
			int before = blocks.Length;
			var bundle = SingleBlockBundle(prefab);
			var volumes = new[] { new BlockPlacerNetworkStories.VolumePlacementArgs { Mountpoint = cell, SignedVolume = Vector3I.Zero, Rotation = IntegerOrientation.Identity } };
			var placed = await BlockPlacerNetworkStories.PlaceOnGrid(grid, bundle, volumes, default(BlockPlacerNetworkStories.SupportArgs));
			int built = placed?.BlocksBuilt ?? 0;
			await Task.Delay(TimeSpan.FromSeconds(3));
			var mine = BlockAt(grid, cell);
			GameLog.Instance.Info($"actions: build on '{grid.Entity}' at {cell}: server built {built}, this game sees it {(mine != null ? "yes" : "NO")} (blocks {before} -> {grid.CubeBlockStorage.GetAllCubeBlocks().Length})");
			if (built == 0 || mine == null) { Failed++; return; }
			Built++;
			var deleted = await BlockPlacerNetworkStories.DeleteCubeBlock(mine);
			await Task.Delay(TimeSpan.FromSeconds(3));
			bool gone = BlockAt(grid, cell) == null;
			GameLog.Instance.Info($"actions: delete at {cell}: server deleted {deleted?.BlocksDeleted ?? 0}, gone here {(gone ? "yes" : "NO")} (blocks {grid.CubeBlockStorage.GetAllCubeBlocks().Length})");
			if ((deleted?.BlocksDeleted ?? 0) == 0 || !gone) { Failed++; return; }
			Deleted++;
		}
		catch (Exception ex)
		{
			Failed++;
			GameLog.Instance.Warn("actions: failed: " + ex);
		}
		finally { _busy = false; }
	}

	private static CubeBlockComponent? BlockAt(CubeGridComponent grid, Vector3I cell)
	{
		foreach (var block in grid.CubeBlockStorage.GetAllCubeBlocks())
		{
			if (block == null) continue;
			var b = block.AABB;
			if (cell.X >= b.Min.X && cell.Y >= b.Min.Y && cell.Z >= b.Min.Z && cell.X <= b.Max.X && cell.Y <= b.Max.Y && cell.Z <= b.Max.Z) return block;
		}
		return null;
	}

	// The server composition's default prefab (a client composition crashes the server's bundle init).
	private static PrefabDefinition? ArmorPrefab()
	{
		var defaults = Singleton<DefinitionManager>.Instance.GetPostProcessor<DefaultPrefabsPostProcessor>();
		PrefabDefinition? best = null;
		int bestLength = int.MaxValue;
		foreach (var pair in defaults.DefaultPrefabs)
		{
			if (pair.Key.DebugName?.IndexOf("ServerComposition", StringComparison.OrdinalIgnoreCase) is not >= 0) continue;
			foreach (var definition in pair.Key.Definitions)
				if (definition is CubeBlockDefinition block && block.DebugName?.IndexOf("LightArmor", StringComparison.OrdinalIgnoreCase) >= 0 && block.DebugName.Length < bestLength)
				{
					best = pair.Value;
					bestLength = block.DebugName.Length;
				}
		}
		return best;
	}

	private static EntityBundle SingleBlockBundle(PrefabDefinition blockPrefab)
	{
		var placer = Singleton<DefinitionManager>.Instance.GetDefinitionsOfType<BlockPlacerEntityComponentDefinition>().First();
		var gridRoot = placer.DefaultPrefabServerGrid.Get();
		var blockOb = blockPrefab.Get();
		gridRoot.OB<HierarchyComponent, HierarchyComponentObjectBuilder>().AddChild(blockOb);
		var bundle = new EntityBundle();
		bundle.Add(gridRoot, null, isRoot: true);
		bundle.Add(blockOb, null);
		return bundle;
	}
}
#endif
