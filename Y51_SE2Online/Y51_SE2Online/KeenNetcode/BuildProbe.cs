#if !PULSAR
using System;
using System.Linq;
using Keen.Game2.Client.GameSystems.BlockPlacement.BlockPlacer;
using Keen.Game2.Client.GameSystems.PlayerControl;
using Keen.Game2.Simulation.GameSystems;
using Keen.Game2.Simulation.GameSystems.BlockPlacement;
using Keen.Game2.Simulation.GameSystems.BlockPlacement.Projections;
using Keen.Game2.Simulation.GameSystems.Ownership;
using Keen.Game2.Simulation.GameSystems.Permissions;
using Keen.Game2.Simulation.GameSystems.Player;
using Keen.Game2.Simulation.WorldObjects.Characters;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.DCS.ObjectBuilders;
using Keen.VRage.Library.Utils;
using Keen.VRage.Core.Game.Definitions;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Definitions;
using Keen.VRage.Library.Mathematics;
using Y51_SE2Online.Core;
using Y51_SE2Online.SyncEngine;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>
/// Test rig only (`-y51online:selftest-build`): the client builds the way the block placer does, through the same
/// network stories, and logs the placer's own ownership check before each step. "selftest-build: ..." lines.
/// With `-y51online:selftest-build-foreign` it instead tries to build on and delete another player's projection.
/// </summary>
internal static class BuildProbe
{
	private static bool Requested => MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "selftest-build");
	private static bool Foreign => MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "selftest-build-foreign");

	private static long _atMs, _weldScanMs;
	private static readonly System.Collections.Generic.Dictionary<Entity, int> MostBlocks = new();

	/// <summary>Server, with -y51online:selftest-build-weld: welds the last ghost block of a player's projection that once held two,
	/// the way a welder finishes one (GridProjectionsSessionComponent.TryMaterializeBlock).</summary>
	public static void Server(Session session, IMpLog log)
	{
		if (!MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "selftest-build-weld")) return;
		long now = MpClock.NowMs();
		if (now < _weldScanMs) return;
		_weldScanMs = now + 1000;
		foreach (var entity in session.GetEntitiesOfType<ProjectionSourceComponent>().ToList())
		{
			long owner = TeamPermissions.OwnerOf(entity);
			if (!Sync.PlayerIdentities.IsHuman(owner) || Blocks(entity) != 1) continue;
			if (!MostBlocks.TryGetValue(entity, out int seenAt)) { MostBlocks[entity] = (int)(now / 1000); continue; }
			if (seenAt < 0 || now / 1000 - seenAt < 4) continue;
			MostBlocks[entity] = -1;
			Span<int> path = stackalloc int[] { 0, 0 };
			var result = session.SessionComponents.Get<GridProjectionsSessionComponent>().TryMaterializeBlock(entity.Get<ProjectionSourceComponent>(), new ProjectionSourceComponent.ChildRef(path), new IdentityId(owner));
			log.Info($"selftest-build: server welded the ghost block of identity {owner}: {result.Result}, block {result.MaterializedBlock?.GetComposition()?.DebugNameShort}");
		}
	}

	// A ghost block on top of the player's own ship (a respawn ship), the way the block placer builds on a grid.
	private static async System.Threading.Tasks.Task<Entity?> GhostOnShip(Session session, Func<EntityBundle> bundle, long own, IMpLog log)
	{
		var ship = session.GetEntitiesOfType<CubeGridComponent>().FirstOrDefault(g => TeamPermissions.OwnerOf(g) == own && WorldSocial.GridOf(g) == g);
		if (ship == null) { log.Warn("selftest-build: no own ship here"); return null; }
		var grid = ship.Get<CubeGridComponent>();
		BoundingBoxI? top = null;
		grid.VisitAllBlocksWithComponent((CubeBlockComponent b) => { if (top == null || b.AABB.Max.Y > top.Value.Max.Y) top = b.AABB; });
		if (top is not { } t) return null;
		var mount = new Vector3I(t.Min.X + 4, t.Max.Y + 5, t.Min.Z + 4);
		log.Info($"selftest-build: own ship '{grid.DisplayName}': {Check(session, ship, new IdentityId(own))}");
		var placed = await BlockPlacerNetworkStories.PlaceOnGrid(grid, bundle(), new[] { new BlockPlacerNetworkStories.VolumePlacementArgs { Mountpoint = mount, Rotation = IntegerOrientation.Identity } }, default).Task;
		var target = placed?.Grid.Value;
		log.Info($"selftest-build: ghost block on top of the ship at {mount.X},{mount.Y},{mount.Z}: {(placed == null ? "refused" : $"{placed.Value.BlocksBuilt} built, target {target?.GetComposition()?.DebugNameShort}")}");
		await Keen.VRage.Library.Threading.Task.Delay(TimeSpan.FromSeconds(2));
		return target?.TryGet<ProjectionSourceComponent>() != null ? target : session.GetEntitiesOfType<ProjectionSourceComponent>()
			.FirstOrDefault(e => TeamPermissions.OwnerOf(e) == own && e.TryGet<AttachedToGridComponent>()?.AttachedGrid == ship);
	}

	// A player welds their first ghost block, then builds next to what they still see and deletes it, as the block placer does
	// (its awaits are async void: a fault goes to the crash handler).
	private static async System.Threading.Tasks.Task WeldThenBuild(Session session, Entity target, Func<EntityBundle> bundle, long own, IMpLog log)
	{
		var first = FirstBlock(target);
		await Keen.VRage.Library.Threading.Task.Delay(TimeSpan.FromSeconds(10));
		var at = target.Data.GetWorldTransform().Position;
		var grids = session.GetEntitiesOfType<CubeGridComponent>().Where(g => TeamPermissions.OwnerOf(g) == own && Vector3D.Distance(g.Data.GetWorldTransform().Position, at) < 20).ToList();
		bool ghost = !EngineCodec.IsDead(target) && target.TryGet<ProjectionSourceComponent>() != null && Blocks(target) > 0;
		log.Info($"selftest-build: after the weld: {grids.Count} own grid(s) here; the ghost {(ghost ? $"is still shown ({Blocks(target)} block)" : "is gone")}");
		if (!ghost || first is not { } cells) return;
		long sentMs = MpClock.NowMs();
		var size = cells.GetSizeIncludingMax();
		var next = cells.Min + size / 2 - Vector3I.One + new Vector3I(size.X, 0, 0);
		AwaitLikeTheGame(BlockPlacerNetworkStories.PlaceOnProjection(target.Get<ProjectionSourceComponent>(), bundle(), new[] { new BlockPlacerNetworkStories.VolumePlacementArgs { Mountpoint = next, Rotation = IntegerOrientation.Identity } }, default).Task);
		await Keen.VRage.Library.Threading.Task.Delay(TimeSpan.FromSeconds(4));
		if (target.Get<GridProjectionAABBComponent>().TryGetBlockAt(0, cells.Min) is { } child)
			AwaitLikeTheGame(BlockPlacerNetworkStories.DeleteProjectedBlock(target.Get<ProjectionSourceComponent>(), child).Task);
		await Keen.VRage.Library.Threading.Task.Delay(TimeSpan.FromSeconds(4));
		var said = Sync.ChatLog.Recent(50).Where(l => l.ReceivedMs >= sentMs).Select(l => l.Line.From + ": " + l.Line.Text).ToArray();
		log.Info($"selftest-build: built next to the ghost and deleted it; chat since: [{string.Join(" | ", said)}]");
	}

	public static void Client(IMpLog log, long now)
	{
		if (_atMs < 0 || !Requested || MpPlugin.App?.ClientSession is not { } session || MpPlugin.App._isTransitioning.IsSet || Me(session) == null) return;
		if (_atMs == 0) { _atMs = now + 20000; return; }
		if (now < _atMs) return;
		_atMs = -1;
		_ = Run(session, log);
	}

	private static Entity? Me(Session session) => session.SessionComponents.Get<ClientPlayersSessionComponent>().LocalPlayerController?.ControlledEntities
		.Select(c => (c.Controllable as Component)?.Entity).FirstOrDefault(e => e?.TryGet<CharacterLifeComponent>() != null);

	private static string Check(Session session, Entity root, IdentityId who)
	{
		var permissions = session.SessionComponents.Get<PermissionsSessionComponent>();
		var rules = session.Get<IGameplayRules>();
		bool place = permissions.CanPlaceBlock(root, who);
		var rule = rules.CanPlaceBlocks(new IGameplayRules.BlockOperationContext { Requester = who, TargetGrid = root, Area = WorldTransform.TransformOriented(new BoundingBoxD(-Vector3D.One, Vector3D.One), root.Data.GetWorldTransform()) });
		return $"CanPlaceBlock={place} rules={(rule.Allowed ? "allowed" : "rejected " + rule.RejectReason)} (target owner {TeamPermissions.OwnerOf(root)}, asker {(long)who})";
	}

	private static async System.Threading.Tasks.Task Run(Session session, IMpLog log)
	{
		try
		{
			var me = Me(session);
			if (me == null) { log.Error("selftest-build: FAIL no own character"); return; }
			var players = session.SessionComponents.Get<ClientPlayersSessionComponent>();
			long own = TeamPermissions.OwnerOf(me);
			log.Info($"selftest-build: own character owner {own}, local player identity {(long)players.LocalPlayerIdentity}, table identity {ClientWorld.Current?.BoundSession?.IdentityId}");
			foreach (var placer in session.GetEntitiesOfType<BlockPlacerEntityComponent>())
			{
				var bp = placer.Get<BlockPlacerEntityComponent>();
				log.Info($"selftest-build: block placer {placer.GetComposition()?.DebugNameShort} owner provider says {(long)bp._ownerProvider.Owner}");
			}

			var definitions = Singleton<DefinitionManager>.Instance;
			var placerDef = definitions.GetDefinitionsOfType<BlockPlacerEntityComponentDefinition>().First();
			definitions.TryGetDefinition(new Guid("2eacbbf2-d8fb-4a78-91dc-7b492517ef97"), out PrefabDefinition? armor);
			EntityBundle Bundle()
			{
				var grid = placerDef.DefaultPrefabServerGrid.Get();
				var block = armor!.Get();
				grid.OB<HierarchyComponent, HierarchyComponentObjectBuilder>().AddChild(block);
				var bundle = new BlockPlacerEntityBundle(new EntityBundle(), block.Definition<CubeBlockComponent, CubeBlockDefinition>().BoundingBox);
				bundle.Add(grid, null, isRoot: true);
				bundle.Add(block, null);
				return bundle.Bundle.CloneBundle();
			}
			_bundle = Bundle;
			if (MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "selftest-build-ship") && await GhostOnShip(session, Bundle, own, log) is { } onShip)
			{
				await WeldThenBuild(session, onShip, Bundle, own, log);
				log.Info("selftest-build: done");
				return;
			}
			var at = me.Data.GetWorldTransform();
			var spot = new WorldTransform(at.Position + at.Orientation.GetForward() * 8 + at.Orientation.GetUp() * 2, at.Orientation);
			var first = await BlockPlacerNetworkStories.PlaceStandalone(session, new BlockPlacerNetworkStories.NewGridArgs { Transform = spot, Static = true }, Bundle(),
				Vector3I.Zero, Vector3I.Zero, new MatrixI(IntegerOrientation.Identity), null, default).Task;
			Entity? target = first?.Grid.Value;
			log.Info($"selftest-build: first block: {(first == null ? "refused" : $"{first.Value.BlocksBuilt} built, new grid {first.Value.IsNewGrid}, target {target?.GetComposition()?.DebugNameShort}")}");
			if (target == null) { log.Error("selftest-build: FAIL first block not placed"); return; }
			await Keen.VRage.Library.Threading.Task.Delay(TimeSpan.FromSeconds(3));
			var projection = target.TryGet<ProjectionSourceComponent>();
			log.Info($"selftest-build: target is {(projection != null ? "a projection" : target.TryGet<CubeGridComponent>() != null ? "a grid" : "something else")}, owner on this client {TeamPermissions.OwnerOf(target)}; own character: {Check(session, target, new IdentityId(own))}");
			foreach (var placer in session.GetEntitiesOfType<BlockPlacerEntityComponent>())
				log.Info($"selftest-build: as the block placer checks it: {Check(session, target, placer.Get<BlockPlacerEntityComponent>()._ownerProvider.Owner)}");
			if (MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "selftest-build-weld")) await WeldThenBuild(session, target, Bundle, own, log);
			else
			{
				try { await BuildAndDelete(target, Bundle, "own", log); }
				catch (Exception ex) { log.Warn($"selftest-build: own: {ex.GetType().Name}: {ex.Message.Split('\n')[0]}"); }
			}
			if (Foreign) await TryForeign(session, me, own, log);
			log.Info("selftest-build: done");
		}
		catch (Exception ex) { log.Error($"selftest-build: FAIL {ex.GetType().Name}: {ex.Message.Split('\n')[0]}"); }
	}

	private static Func<EntityBundle>? _bundle;

	// The game's own awaiters of a delete (building analytics) are async void: a fault goes to the crash handler.
	private static async void AwaitLikeTheGame<T>(Keen.VRage.Library.Threading.Task<T> task) => await task;

	private static int Blocks(Entity target) => target.TryGet<ProjectionSourceComponent>()?.ProjectionData is { } data && data.Roots > 0 ? data.RootBuilders[0].TryGetDirectChilds().Length : 0;

	private static bool Note(IMpLog log, string line) { log.Info(line); return true; }

	private static async System.Threading.Tasks.Task TryForeign(Session session, Entity me, long own, IMpLog log)
	{
		Entity? theirs = null;
		for (int i = 0; i < 150 && theirs == null; i++)
		{
			theirs = session.GetEntitiesOfType<ProjectionSourceComponent>().Where(e => TeamPermissions.OwnerOf(e) is long o && o != own && Sync.PlayerIdentities.IsHuman(o) && Blocks(e) > 0).FirstOrDefault();
			if (theirs == null && i >= 145) theirs = session.GetEntitiesOfType<CubeGridComponent>().Where(e => TeamPermissions.OwnerOf(e) is long o && o != own && Sync.PlayerIdentities.IsHuman(o))
				.OrderBy(e => WorldSocial.SharingOf(e) != Sync.GridSharing.Private).FirstOrDefault();
			if (theirs == null) await Keen.VRage.Library.Threading.Task.Delay(TimeSpan.FromSeconds(2));
		}
		if (theirs == null || _bundle == null) { log.Warn("selftest-build: no other player's grid or projection here"); return; }
		log.Info($"selftest-build: foreign {(theirs.TryGet<ProjectionSourceComponent>() != null ? "projection" : "grid")} owned by {TeamPermissions.OwnerOf(theirs)} (sharing {WorldSocial.SharingOf(theirs)}): {Check(session, theirs, new IdentityId(own))}");
		await BuildAndDelete(theirs, _bundle, "foreign", log);
	}

	// The first block's cells: a projection keeps it as an object builder, a grid as a block.
	private static BoundingBoxI? FirstBlock(Entity target)
	{
		if (target.TryGet<ProjectionSourceComponent>() is { ProjectionData: { } data })
		{
			var block = data.RootBuilders[0].TryGetDirectChilds()[0];
			return block.Definition.TryGetDefinition<CubeBlockComponent, CubeBlockDefinition>()!.ComputeBlockBoundsAndRotation(block.OB<ChildTransformComponent, ChildTransformComponentObjectBuilder>().Transform).Bounds;
		}
		BoundingBoxI? found = null;
		target.TryGet<CubeGridComponent>()?.VisitAllBlocksWithComponent((CubeBlockComponent b) => found ??= b.AABB);
		return found;
	}

	private static async System.Threading.Tasks.Task BuildAndDelete(Entity target, Func<EntityBundle> bundle, string what, IMpLog log)
	{
		if (FirstBlock(target) is not { } first) { log.Warn($"selftest-build: {what}: no first block found"); return; }
		var size = first.GetSizeIncludingMax();
		var next = first.Min + size / 2 - Vector3I.One + new Vector3I(size.X, 0, 0);
		var volumes = new[] { new BlockPlacerNetworkStories.VolumePlacementArgs { Mountpoint = next, Rotation = IntegerOrientation.Identity } };
		var projection = target.TryGet<ProjectionSourceComponent>();
		var grid = target.TryGet<CubeGridComponent>();
		var placed = projection != null ? await BlockPlacerNetworkStories.PlaceOnProjection(projection, bundle(), volumes, default).Task
			: await BlockPlacerNetworkStories.PlaceOnGrid(grid!, bundle(), volumes, default).Task;
		log.Info($"selftest-build: {what}: second block at {next.X},{next.Y},{next.Z} next to {first.Min.X},{first.Min.Y},{first.Min.Z}: {(placed == null ? "refused" : $"{placed.Value.BlocksBuilt} built")}");
		await Keen.VRage.Library.Threading.Task.Delay(TimeSpan.FromSeconds(2));
		if (projection != null && target.Get<GridProjectionAABBComponent>().TryGetBlockAt(0, first.Min) is { } child)
		{
			// What the block placer sends for one ghost block, sent the way it sends it (not awaited).
			long sentMs = MpClock.NowMs();
			int before = Blocks(target);
			string answer;
			try { answer = await BlockPlacerNetworkStories.DeleteProjectedBlock(projection, child).Task is { } r ? r.BlocksDeleted + " deleted" : "refused"; }
			catch (Exception ex)
			{
				answer = "faulted: " + ex.Message.Split('\n')[0];
				// As the block placer sends it: not awaited, so the fault surfaces when the task is collected.
				Keen.VRage.Library.Threading.TaskExtensions.SkipWait(Keen.VRage.Library.Threading.TaskExtensions.StripResult(BlockPlacerNetworkStories.DeleteProjectedBlock(projection, child).Task));
				AwaitLikeTheGame(BlockPlacerNetworkStories.DeleteProjectedBlock(projection, child).Task);
				await Keen.VRage.Library.Threading.Task.Delay(TimeSpan.FromSeconds(2));
				GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
			}
			log.Info($"selftest-build: {what}: delete one ghost block: {answer}");
			await Keen.VRage.Library.Threading.Task.Delay(TimeSpan.FromSeconds(3));
			int left = Blocks(target);
			var said = Sync.ChatLog.Recent(50).Where(l => l.ReceivedMs >= sentMs).Select(l => l.Line.From + ": " + l.Line.Text).ToArray();
			log.Info($"selftest-build: {what}: delete one ghost block (as the placer sends it): {left} of {before} ghost blocks left, first cell {(target.Get<GridProjectionAABBComponent>().TryGetBlockAt(0, first.Min) != null ? "still filled" : "empty")}; chat since: [{string.Join(" | ", said)}]");
			if (left < before) return;
		}
		else if (grid != null && grid.CubeBlockStorage.TryGetCubeBlock(first.Min) is { } block)
		{
			var single = await BlockPlacerNetworkStories.DeleteCubeBlock(block).Task;
			log.Info($"selftest-build: {what}: delete one block: {(single == null ? "refused" : $"{single.Value.BlocksDeleted} deleted")}");
			return;
		}
		// What the block placer sends for a dragged delete.
		var gone = await BlockPlacerNetworkStories.DeleteBlocksInVolume(target, new System.Collections.Generic.List<BoundingBoxI> { first }).Task;
		await Keen.VRage.Library.Threading.Task.Delay(TimeSpan.FromSeconds(2));
		log.Info($"selftest-build: {what}: delete by volume: {(gone == null ? "refused" : $"{gone.Value.BlocksDeleted} deleted")}, {Blocks(target)} ghost blocks left here");
	}
}
#endif
