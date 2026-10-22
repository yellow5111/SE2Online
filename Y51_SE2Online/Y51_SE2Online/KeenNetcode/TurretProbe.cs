#if !PULSAR
using System;
using System.Collections.Generic;
using System.Linq;
using Keen.Game2.Simulation.GameSystems.Identities;
using Keen.Game2.Simulation.GameSystems.Ownership;
using Keen.Game2.Simulation.GameSystems.Relations;
using Keen.Game2.Simulation.WorldObjects.Characters;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.Weapons;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.Game2.Simulation.WorldObjects.CubeGrids.Weapons;
using Keen.Game2.Simulation.WorldObjects.Items;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Definitions;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Library.Utils;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;
using Y51_SE2Online.SyncEngine;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>
/// Test rig only (`-y51online:selftest-turret`): on the server, once a player who joined is in the world, the grid with
/// turrets gets ammo, a hostile NPC appears 11 m above its first turret, and the grid is owned by that player, then by
/// the world owner, then by the player again with the world owner's character as the only target (PvP off, then on).
/// Each phase logs every turret's state. "selftest-turret: ..." lines.
/// </summary>
internal static class TurretProbe
{
	private static bool Requested => MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "selftest-turret");

	private static int _phase = -1;
	private static long _phaseMs, _nextLogMs, _joinedSeenMs;
	private static Entity? _grid, _npc;
	private static long _player, _owner;

	public static void Tick(Session session)
	{
		if (!Requested || !ReferenceEquals(session, MpPlugin.App?.ServerSession) || _phase >= 6) return;
		try { Step(session); }
		catch (Exception ex) { GameLog.Instance.Warn("selftest-turret: FAIL " + ex); _phase = 6; }
	}

	private static void Step(Session session)
	{
		long now = MpClock.NowMs();
		var log = GameLog.Instance;
		var identities = session.SessionComponents.Get<IdentitiesSessionComponent>();
		long local = (long)identities.LocalIdentity;
		if (_phase < 0)
		{
			var joined = session.GetEntitiesOfType<CharacterLifeComponent>().Select(TeamPermissions.OwnerOf)
				.FirstOrDefault(o => PlayerIdentities.IsHuman(o) && o != local && identities.TryGetClientFromIdentity(new IdentityId(o)) != null);
			if (joined == 0) return;
			if (_joinedSeenMs == 0) { _joinedSeenMs = now; log.Info($"selftest-turret: player identity {joined} is in the world (world owner {local}); starting in 60 s"); return; }
			if (now - _joinedSeenMs < 60000) return;
			_player = joined;
			_owner = local;
			_grid = session.GetEntitiesOfType<TargetingGridComponent>().FirstOrDefault(g => Turrets(g).Any());
			if (_grid == null) { log.Warn("selftest-turret: FAIL no grid with turrets"); _phase = 6; return; }
			var ammo = Singleton<DefinitionManager>.Instance.GetDefinitionsOfTypeAndDescendants<ItemDefinition>().Where(d => d.ToString().Contains("GatlingAmmo", StringComparison.OrdinalIgnoreCase)).OrderBy(d => d.ToString().Length).FirstOrDefault();
			foreach (var turret in Turrets(_grid))
				foreach (var inventory in turret.Entity.GetDescendants(includeRoot: true, recursive: true).SelectMany(d => d.Components.OfType<InventoryComponent>()))
					if (ammo != null) inventory.AddItemByDef(ammo, null, (FixedPoint)300);
			int enabled = 0;
			_grid.Get<CubeGridComponent>().VisitAllBlocksWithComponent((PowerableBlockComponent p) => { if (!p.Enabled) { p.Enabled = true; enabled++; } });
			log.Info($"selftest-turret: turned on {enabled} blocks of the turret grid");
			var first = Turrets(_grid).First().Entity.Data.GetWorldTransform();
			var config = Singleton<DefinitionManager>.Instance.GetConfiguration<Keen.Game2.Simulation.GameSystems.AI.Definitions.AIControllerConfiguration>();
			_npc = session.EntitySerializer.BuildFrom(default, config.NPCCharacterPrefab.Get());
			ref var at = ref _npc.Data.GetWritePtr<WorldTransform>();
			at.Position = first.Position + first.Orientation.GetUp() * 11;
			session.AddEntityToScene(_npc);
			session.SessionComponents.Get<OwnershipSessionComponent>().TryTransferToNPC(_npc, hostile: true);
			SetOwner(session, _player, "the joined player");
			return;
		}
		if (now >= _nextLogMs) { _nextLogMs = now + 3000; Report(session, log); }
		if (now - _phaseMs < 20000) return;
		switch (_phase)
		{
			case 0: SetOwner(session, _owner, "the world owner"); break;
			case 1: SetOwner(session, _player, "the joined player again"); break;
			case 2:
				// PvP: only the world owner's character as a target.
				if (_npc != null) session.MarkEntityForClose(_npc);
				_npc = session.GetEntitiesOfType<CharacterLifeComponent>().FirstOrDefault(c => TeamPermissions.OwnerOf(c) == _owner);
				if (_npc != null)
				{
					var first = Turrets(_grid!).First().Entity.Data.GetWorldTransform();
					_npc.Data.GetWritePtr<WorldTransform>().Position = first.Position + first.Orientation.GetUp() * 11;
				}
				TurretRelations.SetPvp(false);
				TurretRelations.Refresh(session);
				log.Info($"selftest-turret: phase 3, PvP off, target the world owner's character ({(_npc == null ? "none" : "moved over the turret")})");
				_phase = 3; _phaseMs = now; return;
			case 3:
				TurretRelations.SetPvp(true);
				TurretRelations.Refresh(session);
				log.Info("selftest-turret: phase 4, PvP on, same target");
				_phase = 4; _phaseMs = now; return;
			case 4:
				TurretRelations.SetPvp(false);
				TurretRelations.Refresh(session);
				log.Info("selftest-turret: done");
				_phase = 6; return;
		}
	}

	private static void SetOwner(Session session, long owner, string who)
	{
		long before = TeamPermissions.OwnerOf(_grid!);
		bool done = before == owner || session.SessionComponents.Get<OwnershipSessionComponent>().TryTransferOwnership(_grid!, new IdentityId(owner));
		_phase++;
		_phaseMs = MpClock.NowMs();
		GameLog.Instance.Info($"selftest-turret: phase {_phase}, grid owned by {who} ({owner}): {done} (was {before}, now {TeamPermissions.OwnerOf(_grid!)})");
	}

	private static IEnumerable<AutoTargetingComponent> Turrets(Entity grid)
	{
		var list = new List<AutoTargetingComponent>();
		grid.Get<CubeGridComponent>().VisitAllBlocksWithComponent((CubeBlockComponent b) => { if (b.Entity.TryGet<AutoTargetingComponent>() is { } t) list.Add(t); });
		return list;
	}

	private static void Report(Session session, IMpLog log)
	{
		if (_grid == null) return;
		var relations = session.Get<IRelations>();
		long owner = TeamPermissions.OwnerOf(_grid);
		long target = _npc == null ? 0 : TeamPermissions.OwnerOf(_npc);
		foreach (var t in Turrets(_grid))
		{
			var powerable = t.Entity.TryGet<PowerableBlockComponent>();
			string verify = _npc == null ? "-" : t.VerifyRules(_npc).ToString();
			int ammo = t.Entity.GetDescendants(includeRoot: true, recursive: true).SelectMany(d => d.Components.OfType<InventoryComponent>()).Sum(i => (int)i.Items.Length);
			if (t == Turrets(_grid).First()) log.Info("selftest-turret: potential targets: " + string.Join(", ", (t._gridTargeting?.PotentialTargets.ToArray() ?? Array.Empty<Entity>()).Select(e => $"{e.GetComposition()?.DebugNameShort} (owner {TeamPermissions.OwnerOf(e)})")));
			log.Info($"selftest-turret: {t.Entity.GetComposition()?.DebugNameShort} owner {owner}: target {(t._target.Entity == null ? "none" : t._target.Entity == _npc ? "THE TARGET" : "other")}, shooting {t._weapon.IsShooting}, los {t._targetLineOfSight}, powered {t.IsPowered}"
				+ $" (enabled {powerable?.Enabled}, supplied {powerable?.Supplied}, working {powerable?.Working}), idle {t.Entity.Data.Has<AutoTargetingComponent.IdleTag>()}, controlled {t.IsControlling}, potential {t._gridTargeting?.PotentialTargets.Count}"
				+ $", eligible {(_npc == null ? "-" : t._gridTargeting?.IsEligibleTarget(_npc).ToString())}, relation {(target == 0 ? "-" : relations.GetRelationFromOwners(new IdentityId(owner), new IdentityId(target)).ToString())}, verify {verify}, ammo stacks {ammo}, retargeted {TurretRelations.Retargeted}");
		}
	}
}
#endif
