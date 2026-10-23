#if !PULSAR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Keen.Game2.Client.GameSystems.Render;
using Keen.Game2.Simulation.GameSystems.BlockPlacement.Projections;
using Keen.Game2.Simulation.GameSystems.Ownership;
using Keen.Game2.Simulation.WorldObjects.Characters;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.Armors;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.Core.Game.Definitions;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.Components;
using Keen.VRage.DCS.Definitions;
using Keen.VRage.DCS.ObjectBuilders;
using Keen.VRage.DCS.Scenes;
using Keen.VRage.Library.Definitions;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Library.Utils;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;
using Y51_SE2Online.SyncEngine;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>
/// Test rig only (`-y51online:selftest-proj`): the server projects three armor blocks in front of the newest player,
/// then every 5 s removes one ghost block, materializes one (the block placer's path) and closes the projection with
/// the last. Every game logs the block references its projection signals carry and its ghost block counts, so a
/// player's log shows whether ghosts go away with the server's. "selftest-proj: ..." lines.
/// </summary>
internal static class ProjectionProbe
{
	private static bool Requested => MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "selftest-proj");

	private const long StepMs = 5000, SecondPlayerMs = 120000, AloneMs = 300000, ClientLogMs = 5000;

	private static readonly HashSet<Entity> Seen = new();
	private static Entity? _newest, _projection;
	private static IdentityId _owner;
	private static Scene? _serverScene;
	private static long _firstHumanMs, _stepMs, _clientLogMs;
	private static int _step = -1;

	public static void Install(Harmony harmony)
	{
		if (!Requested) return;
		foreach (string name in new[] { "OnChildAdded", "OnChildUpdated", "OnChildRemoved" })
			harmony.Patch(AccessTools.Method(typeof(ProjectionSourceComponent), name), prefix: new HarmonyMethod(typeof(ProjectionProbe), nameof(RefPrefix)),
				finalizer: new HarmonyMethod(typeof(ProjectionProbe), nameof(RefFinalizer)));
		harmony.Patch(AccessTools.Method(typeof(PreviewHostComponent), nameof(PreviewHostComponent.RemoveBlockDataFromPreview)),
			postfix: new HarmonyMethod(typeof(ProjectionProbe), nameof(PreviewPostfix)));
		harmony.Patch(AccessTools.Method(typeof(PreviewHostComponent), "AddBlockDataToPreviewGrid"), postfix: new HarmonyMethod(typeof(ProjectionProbe), nameof(PreviewPostfix)));
	}

	public static void Tick(Session session)
	{
		if (!Requested) return;
		try
		{
			if (ReferenceEquals(session, MpPlugin.App?.ServerSession)) Server(session);
			else if (ReferenceEquals(session, MpPlugin.App?.ClientSession)) Client(session);
		}
		catch (Exception ex) { GameLog.Instance.WarnOnce("proj-probe", "selftest-proj: tick failed: " + ex); }
	}

	private static string Role(Component c) => ReferenceEquals(c.Entity.Scene, _serverScene) ? "server" : "client";

	private static int Blocks(ProjectionSourceComponent? source) => source?.ProjectionData is { Roots: > 0 } data ? data.RootBuilders[0].TryGetDirectChilds().Length : 0;

	private static int Previews(PreviewHostComponent preview) => preview._previewGrids.Sum(g => g.Blocks.Count);

	private static string Describe(ProjectionSourceComponent.ChildRef r) => $"depth={r.Depth} [{string.Join(",", r.Hierarchy.ToArray())}]";

	private static void RefPrefix(ProjectionSourceComponent __instance, ProjectionSourceComponent.ChildRef hierarchy, MethodBase __originalMethod)
	{
		GameLog.Instance.Info($"selftest-proj: {Role(__instance)} {__originalMethod.Name} ref {Describe(hierarchy)}, ghost blocks before {Blocks(__instance)}");
	}

	private static Exception? RefFinalizer(Exception? __exception, ProjectionSourceComponent __instance, MethodBase __originalMethod)
	{
		if (__exception != null) GameLog.Instance.Warn($"selftest-proj: {Role(__instance)} {__originalMethod.Name} FAILED {__exception.GetType().Name}: {__exception.Message.Split('\n')[0]}");
		else GameLog.Instance.Info($"selftest-proj: {Role(__instance)} {__originalMethod.Name} applied, ghost blocks now {Blocks(__instance)}");
		return __exception;
	}

	private static void PreviewPostfix(PreviewHostComponent __instance, MethodBase __originalMethod)
	{
		if (BuildGhostModels.IsGhost(__instance.Entity)) return;
		GameLog.Instance.Info($"selftest-proj: {Role(__instance)} preview {__originalMethod.Name}: {Previews(__instance)} hologram blocks");
	}

	private static void Server(Session session)
	{
		_serverScene = session.Scene;
		if (_step >= 4) return;
		long now = MpClock.NowMs();
		var log = GameLog.Instance;
		if (_step < 0)
		{
			int humans = 0;
			foreach (Entity character in session.GetEntitiesOfType<CharacterLifeComponent>())
			{
				if (!PlayerIdentities.IsHuman(TeamPermissions.OwnerOf(character))) continue;
				humans++;
				if (Seen.Add(character)) _newest = character;
			}
			if (humans == 0 || _newest == null) return;
			if (_firstHumanMs == 0)
			{
				_firstHumanMs = now;
				log.Info($"selftest-proj: server sees a player character; projection in {SecondPlayerMs / 1000} s once a second player is here, else in {AloneMs / 1000} s");
				return;
			}
			if (now - _firstHumanMs < (humans >= 2 ? SecondPlayerMs : AloneMs)) return;
			_owner = new IdentityId(TeamPermissions.OwnerOf(_newest));
			_projection = Spawn(session, _newest.Data.GetWorldTransform(), _owner);
			_step = 0;
			_stepMs = now;
			log.Info($"selftest-proj: server spawned a projection for identity {(long)_owner} with {Blocks(_projection.TryGet<ProjectionSourceComponent>())} ghost blocks ({humans} player characters)");
			return;
		}
		if (now - _stepMs < StepMs) return;
		_stepMs = now;
		var source = EngineCodec.IsDead(_projection) ? null : _projection?.TryGet<ProjectionSourceComponent>();
		if (_step == 3)
		{
			log.Info(source == null ? "selftest-proj: server PASS projection entity is gone" : $"selftest-proj: server FAIL projection entity still alive with {Blocks(source)} ghost blocks");
			_step = 4;
			return;
		}
		if (source == null)
		{
			log.Warn($"selftest-proj: server FAIL projection gone before step {_step}");
			_step = 4;
			return;
		}
		switch (_step)
		{
			case 0:
				source.RemoveChild(Ref(0, 2));
				log.Info($"selftest-proj: server removed ghost block 2, ghost blocks now {Blocks(source)} (expect 2)");
				break;
			case 1:
				// The welder's path ends in the same RemoveChild once the block is real (IntegrityToolComponent.RemoveBlockProjection).
				source.RemoveChild(Ref(0, 1));
				log.Info($"selftest-proj: server removed ghost block 1, ghost blocks now {Blocks(source)} (expect 1)");
				break;
			case 2:
				bool closed = source.RemoveChildAndCloseIfEmpty(Ref(0, 0));
				log.Info($"selftest-proj: server removed the last ghost block, projection closing: {closed} (expect True)");
				break;
		}
		_step++;
	}

	private static ProjectionSourceComponent.ChildRef Ref(int grid, int block) => new(stackalloc int[] { grid, block });

	// Three one-cell armor cubes in a row; PrepareGridProjection wraps block roots into a grid itself.
	private static Entity Spawn(Session session, WorldTransform player, IdentityId owner)
	{
		var defs = Singleton<DefinitionManager>.Instance;
		// Light armor cube (the block placer's own prefab), a block every client has a render prefab for.
		defs.TryGetDefinition(new Guid("2eacbbf2-d8fb-4a78-91dc-7b492517ef97"), out PrefabDefinition? cube);
		float step = cube!.Get().Definition<CubeBlockComponent, CubeBlockDefinition>().BoundingBox.GetSizeIncludingMax().X * CubeGridCoords.CELL_SIZE;
		var bundle = new EntityBundle();
		for (int i = 0; i < 3; i++)
		{
			EntityObjectBuilder block = cube.Get();
			block.GetChildTransform() = new RelativeTransform(new Vector3(i * step, 0f, 0f), Quaternion.Identity);
			bundle.Add(block, null, isRoot: true);
		}
		EntityObjectBuilder projection = session.Get<GridProjectionsSessionComponent>().PrepareGridProjection(bundle, owner);
		projection.OB<WorldTransformComponent, WorldTransformComponentObjectBuilder>().Transform = new WorldTransform(player.Position + player.Orientation.GetForward() * 15, player.Orientation);
		return session.Get<IEntitySpawner>().SpawnEntity(projection);
	}

	private static void Client(Session session)
	{
		long now = MpClock.NowMs();
		if (now - _clientLogMs < ClientLogMs) return;
		_clientLogMs = now;
		var lines = new List<string>();
		foreach (Entity entity in session.GetEntitiesOfType<ProjectionSourceComponent>())
		{
			if (BuildGhostModels.IsGhost(entity)) continue;
			var preview = entity.TryGet<PreviewHostComponent>();
			lines.Add($"{entity.GetComposition()?.DebugNameShort}: {Blocks(entity.Get<ProjectionSourceComponent>())} ghost blocks, {(preview == null ? "no" : Previews(preview).ToString())} hologram blocks");
		}
		if (lines.Count > 0) GameLog.Instance.Info($"selftest-proj: client projections: {string.Join("; ", lines)}; refs written {ProjectionRefs.Written}, read {ProjectionRefs.Read}");
	}
}
#endif
