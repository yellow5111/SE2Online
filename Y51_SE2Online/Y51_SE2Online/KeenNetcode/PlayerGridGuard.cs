#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Keen.Game2.Simulation.GameSystems.Encounters;
using Keen.Game2.Simulation.GameSystems.Ownership;
using Keen.Game2.Simulation.GameSystems.TrashRemoval;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.Components;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>
/// A grid group a player owns any part of (an NPC grid a player hacked or was given included) is never removed by the
/// game itself: not by grid PCU trash removal, encounter and cargo ship despawns, contract cleanup, procedural encounter
/// and contract streaming, or managed world areas. Only SE2 Online's cleanup removes it, and only by the owner-inactivity
/// rule. Two layers: such grids are taken out of every game tracker that would delete them (when ownership moves to a
/// player, and in a sweep every 30 s), and a backstop refuses the close if one of those game paths still asks for it.
/// </summary>
internal static class PlayerGridGuard
{
	[ThreadStatic] private static int _automatic;
	[ThreadStatic] private static int _allowed;
	public static long Kept, Detached;
	private static long _gameRemovals;
	private static readonly Dictionary<Entity, IDisposable> Pins = new(ReferenceEqualityComparer.Instance);
	// Grids that just became a player's (or that the game just tried to remove): taken out of the trackers on the next server tick.
	private static readonly HashSet<Entity> Pending = new(ReferenceEqualityComparer.Instance);
	private static long _sweepMs;

	public static void Install(Harmony harmony)
	{
		Patch(harmony, "close", AccessTools.Method(typeof(Session), nameof(Session.MarkEntityForClose), new[] { typeof(Entity), typeof(bool) }), prefix: nameof(ClosePrefix));
		Patch(harmony, "ownership", AccessTools.Method(typeof(OwnershipSessionComponent), nameof(OwnershipSessionComponent.TryTransferOwnership), new[] { typeof(IdentityId), typeof(IdentityId), typeof(Entity) }), postfix: nameof(TransferPostfix));
		foreach (var (what, method) in AutomaticRemovals())
			Patch(harmony, what, method, prefix: nameof(Enter), finalizer: nameof(Leave));
		// Paths that save a snapshot of their grids before deleting them (and spawn the snapshot again later): a player's
		// grid must leave the list before the snapshot, or refusing the delete would leave two of it.
		foreach (var (what, method) in SnapshotRemovals())
			Patch(harmony, what, method, prefix: nameof(EnterAndPurge), finalizer: nameof(Leave));
		var space = typeof(EncounterSessionComponent).Assembly.GetType(typeof(EncounterSessionComponent).Namespace + ".SpaceEncounters.ProceduralSpaceContract");
		foreach (var owner in new[] { space, typeof(EncounterSessionComponent).Assembly.GetType(typeof(EncounterSessionComponent).Namespace + ".PlanetEncounters.ContractPlanetEnvironmentModuleComponent") })
			foreach (var machine in owner?.GetNestedTypes(System.Reflection.BindingFlags.NonPublic) ?? Type.EmptyTypes)
				if (machine.Name.StartsWith("<Offload>", StringComparison.Ordinal))
					Patch(harmony, "contract offload " + owner!.Name, AccessTools.Method(machine, "MoveNext"), prefix: nameof(Enter), finalizer: nameof(Leave));
		Patch(harmony, "managed world area save", AccessTools.Method(AccessTools.TypeByName("Keen.VRage.Core.Game.GameSystems.ManagedWorldAreas.ManagedWorldArea"), "PerformSave"), prefix: nameof(AreaSavePrefix));
	}

	private static IEnumerable<(string, System.Reflection.MethodBase?)> SnapshotRemovals()
	{
		var encounters = typeof(EncounterSessionComponent).Namespace!;
		var asm = typeof(EncounterSessionComponent).Assembly;
		var space = asm.GetType(encounters + ".SpaceEncounters.ProceduralSpaceContract");
		var manager = asm.GetType(encounters + ".SpaceEncounters.SpaceContractsManagerSessionComponent");
		foreach (var owner in new[] { space, asm.GetType(encounters + ".PlanetEncounters.ContractPlanetEnvironmentModuleComponent") })
			foreach (var machine in owner?.GetNestedTypes(System.Reflection.BindingFlags.NonPublic) ?? Type.EmptyTypes)
				if (machine.Name.StartsWith("<Despawn>", StringComparison.Ordinal))
					yield return ("contract streaming " + owner!.Name, AccessTools.Method(machine, "MoveNext"));
		yield return ("planet contract despawn", AccessTools.Method(asm.GetType(encounters + ".PlanetEncounters.ContractPlanetEnvironmentModuleComponent"), "Despawn"));
		yield return ("space contract dispose", AccessTools.Method(space, "Dispose", new[] { typeof(bool) }));
		yield return ("space contract prevented", AccessTools.Method(space, "PreventSpawning"));
		yield return ("space contracts disposed", AccessTools.Method(manager, "DisposeContracts"));
		yield return ("space contracts cleared", AccessTools.Method(manager, "DisposeAndClearContracts"));
	}

	private static void EnterAndPurge(object __instance, System.Reflection.MethodBase __originalMethod)
	{
		_automatic++;
		if (_automatic > 1 || !On || MpPlugin.App?.ServerSession is not { } session) return;
		// A despawn state machine: only where it starts (the space despawn also at state 0); its later steps run in the
		// game's parallel sections, after the list it saves was taken.
		if (__originalMethod.Name == "MoveNext" && __instance != null && AccessTools.Field(__instance.GetType(), "<>1__state")?.GetValue(__instance) is int state
			&& !(state == -1 || (state == 0 && __instance.GetType().DeclaringType?.Name == "ProceduralSpaceContract"))) return;
		try { PurgeContracts(session); }
		catch (Exception ex) { GameLog.Instance.WarnOnce("player-grid-purge", "cleanup: could not take player grids out of a contract before it was saved: " + ex.Message); }
	}

	// Every contract's tracked grids that belong to a player group (owned, taken over, or docked to a player's ship right now).
	private static void PurgeContracts(Session session)
	{
		var drop = new List<Entity>();
		void Scan(ICollection<Entity> tracked)
		{
			drop.Clear();
			foreach (var e in tracked) if (e != null && e.Scene != null && e.TryGet<CubeGridComponent>() != null && IsPlayerGroup(e)) drop.Add(e);
			foreach (var e in drop) { tracked.Remove(e); e.Data.TryRemove<Keen.VRage.Core.Game.GameSystems.ProceduralGeneration.ProcedurallyGeneratedTag>(); lock (Pending) Pending.Add(e); }
		}
		if (session.SessionComponents.TryGet<Keen.Game2.Simulation.GameSystems.Encounters.SpaceEncounters.SpaceContractsManagerSessionComponent>() is { } space)
			foreach (var contract in space.Contracts.Values) Scan(contract.Entities);
		foreach (var planet in session.GetEntitiesOfType<Keen.Game2.Simulation.GameSystems.Encounters.PlanetEncounters.ContractPlanetEnvironmentModuleComponent>())
			if (AccessTools.Field(typeof(Keen.Game2.Simulation.GameSystems.Encounters.PlanetEncounters.ContractPlanetEnvironmentModuleComponent), "_entities")?.GetValue(planet.Get<Keen.Game2.Simulation.GameSystems.Encounters.PlanetEncounters.ContractPlanetEnvironmentModuleComponent>()) is IDictionary lists)
				foreach (var list in lists.Values.OfType<ICollection<Entity>>().ToList()) Scan(list);
	}

	// A world area saves its grids, then deletes them: a player group never goes into the save, and is no longer managed.
	private static void AreaSavePrefix(object entities)
	{
		if (!On || entities is not IList<Entity> list || MpPlugin.App?.ServerSession is not { } session) return;
		try
		{
			var areas = session.SessionComponents.TryGet<Keen.VRage.Core.Game.GameSystems.ManagedWorldAreas.ManagedWorldAreaSessionComponent>();
			for (int i = list.Count - 1; i >= 0; i--)
			{
				var e = list[i];
				if (e == null || e.TryGet<CubeGridComponent>() == null || !IsPlayerGroup(e)) continue;
				list.RemoveAt(i);
				areas?.TryUnmanage(e);
				lock (Pending) Pending.Add(e);
			}
		}
		catch (Exception ex) { GameLog.Instance.WarnOnce("player-grid-area", "cleanup: could not keep player grids out of a world area save: " + ex.Message); }
	}

	/// <summary>A grid changed owner (the game's own ownership signal, any path): checked on the next server tick.</summary>
	public static void Queue(Entity grid) { if (grid != null) lock (Pending) Pending.Add(grid); }

	// Every game path that removes grids on its own (decompiled 4.0.0.3165): trash removal, encounters, contracts, streaming.
	private static IEnumerable<(string, System.Reflection.MethodBase?)> AutomaticRemovals()
	{
		var encounters = typeof(EncounterSessionComponent).Namespace!;
		var asm = typeof(EncounterSessionComponent).Assembly;
		Type? T(string name) => asm.GetType(name);
		yield return ("grid PCU trash removal", AccessTools.Method(typeof(GridPCUTrashRemovalSessionComponent), "GridTrashRemoval"));
		yield return ("encounter despawn", AccessTools.Method(typeof(EncounterSessionComponent), nameof(EncounterSessionComponent.DespawnEncounter)));
		yield return ("encounter spawner despawn", AccessTools.Method(typeof(EncounterSpawnerComponent), nameof(EncounterSpawnerComponent.Despawn)));
		yield return ("space encounter despawn", AccessTools.Method(T(encounters + ".SpaceEncounters.SpaceEncounterGeneratorSessionComponent"), "Despawn"));
		yield return ("planet encounter despawn", AccessTools.Method(T(encounters + ".PlanetEncounters.EncounterPlanetEnvironmentModuleComponent"), "Despawn"));
		var cargo = T(encounters + ".CargoShips.CargoShipEncounterSessionComponent");
		yield return ("cargo ship despawn", AccessTools.Method(cargo, "TryDespawn"));
		yield return ("cargo ships off", AccessTools.Method(cargo, "OnGenerationEnabledChanged"));
		yield return ("managed world area unload", AccessTools.Method(AccessTools.TypeByName("Keen.VRage.Core.Game.GameSystems.ManagedWorldAreas.ManagedWorldArea"), "PerformUnload"));
	}

	private static void Patch(Harmony harmony, string what, System.Reflection.MethodBase? target, string? prefix = null, string? postfix = null, string? finalizer = null)
	{
		try
		{
			if (target == null) throw new MissingMethodException(what);
			harmony.Patch(target, prefix: prefix == null ? null : new HarmonyMethod(typeof(PlayerGridGuard), prefix), postfix: postfix == null ? null : new HarmonyMethod(typeof(PlayerGridGuard), postfix),
				finalizer: finalizer == null ? null : new HarmonyMethod(typeof(PlayerGridGuard), finalizer));
		}
		catch (Exception ex) { GameLog.Instance.Warn($"cleanup: player grid guard for {what} not installed ({ex.GetType().Name}: {ex.Message})"); }
	}

	private static void Enter() => _automatic++;

	private static Exception? Leave(Exception? __exception) { _automatic--; return __exception; }

	/// <summary>Runs SE2 Online's own removal (the cleanup's inactivity rule, an admin's delete): the backstop lets it through.</summary>
	public static void Allow(Action remove)
	{
		_allowed++;
		try { remove(); }
		finally { _allowed--; }
	}

	private static bool On => MpPlugin.Online && Y51_SE2Online.SyncEngine.AdminAccess.Hosting;

	private static bool ClosePrefix(Session __instance, Entity entity, bool moveToStaging)
	{
		// moveToStaging false is the game closing an empty grid (its last block ground away): always allowed.
		if (_automatic <= 0 || _allowed > 0 || !moveToStaging || entity == null || !On || __instance.IsUnloading) return true;
		try
		{
			if (entity.TryGet<CubeGridComponent>() == null) return true;
			if (!IsPlayerGroup(entity))
			{
				// What the game cleans up on its own is in the log too (0.17 showed only Keen's "Trash-collecting grid").
				if (System.Threading.Interlocked.Increment(ref _gameRemovals) <= 500 || _gameRemovals % 100 == 0)
				{
					var where = entity.Data.GetWorldTransform().Position;
					GameLog.Instance.Info($"cleanup: the game removed {(TeamPermissions_OwnerOf(entity) == 0 ? "unowned" : "NPC")} grid '{Name(entity)}' at {where.X:0}, {where.Y:0}, {where.Z:0} ({_gameRemovals} so far)");
				}
				return true;
			}
			Kept++;
			var at = entity.Data.GetWorldTransform().Position;
			GameLog.Instance.Info($"cleanup: kept player grid '{Name(entity)}' (owner identity {TeamPermissions_OwnerOf(entity)}) at {at.X:0}, {at.Y:0}, {at.Z:0}: the game tried to remove it automatically");
			AuditLog.Write("cleanup", "game", "kept player grid", $"'{Name(entity)}' owner {TeamPermissions_OwnerOf(entity)} at {at.X:0},{at.Y:0},{at.Z:0}");
			// Not here: the game may be walking the very tracker this grid would be taken out of (a contract's list).
			lock (Pending) Pending.Add(entity);
			return false;
		}
		catch (Exception ex)
		{
			GameLog.Instance.WarnOnce("player-grid-guard", "cleanup: player grid guard could not check a grid (" + ex.Message + "); kept it");
			return false;
		}
	}

	private static void TransferPostfix(IdentityId newOwner, Entity target, bool __result)
	{
		if (!__result || !On || !PlayerIdentities.IsHuman((long)newOwner) || target?.TryGet<CubeGridComponent>() == null) return;
		lock (Pending) Pending.Add(target);
	}

	private static long TeamPermissions_OwnerOf(Entity e) => Y51_SE2Online.SyncEngine.TeamPermissions.OwnerOf(e);

	private static string Name(Entity grid)
	{
		try { return SocialState.Clean(grid.Get<CubeGridComponent>().DisplayName.EvaluateText(), 64); } catch (Exception) { return "grid"; }
	}

	/// <summary>The grid and every grid connected to it (rotors, pistons, connectors, merge blocks).</summary>
	internal static List<Entity> Group(Entity grid)
		=> grid.Get<CubeGridComponent>().GridGraphHandler.GetAllConnectedEntities(grid).Where(e => e.TryGet<CubeGridComponent>() != null).Append(grid).Distinct().ToList();

	internal static bool IsPlayerGroup(Entity grid) => Group(grid).Any(g => PlayerIdentities.IsHuman(TeamPermissions_OwnerOf(g)));

	private static WeakReference<Session>? _session;

	/// <summary>Every 30 s on the server: player grid groups out of the game's despawn trackers and pinned against trash removal.</summary>
	public static void Sweep(Session session, bool force = false)
	{
		// A new world: the last world's pins and queue hold entities that are gone.
		if (_session == null || !_session.TryGetTarget(out var last) || !ReferenceEquals(last, session))
		{
			_session = new WeakReference<Session>(session);
			Pins.Clear();
			lock (Pending) Pending.Clear();
		}
		List<Entity> pending;
		lock (Pending) { pending = Pending.ToList(); Pending.Clear(); }
		foreach (var grid in pending)
		{
			try { if (grid.Scene != null && grid.TryGet<CubeGridComponent>() != null && IsPlayerGroup(grid)) Detach(session, Group(grid)); }
			catch (Exception ex) { GameLog.Instance.WarnOnce("player-grid-detach", "cleanup: could not take a player's grid out of the game's trackers: " + ex.Message); }
		}
		long now = MpClock.NowMs();
		if (!force && now - _sweepMs < 30_000) return;
		_sweepMs = now;
		var seen = new HashSet<Entity>(ReferenceEqualityComparer.Instance);
		var keep = new HashSet<Entity>(ReferenceEqualityComparer.Instance);
		foreach (var grid in session.GetEntitiesOfType<CubeGridComponent>())
		{
			if (grid.Scene == null || seen.Contains(grid)) continue;
			var group = Group(grid);
			foreach (var g in group) seen.Add(g);
			if (!group.Any(g => PlayerIdentities.IsHuman(TeamPermissions_OwnerOf(g)))) continue;
			foreach (var g in group) keep.Add(g);
			Detach(session, group);
		}
		foreach (var gone in Pins.Keys.Where(e => !keep.Contains(e)).ToList())
		{
			try { Pins[gone].Dispose(); } catch (Exception) { }
			Pins.Remove(gone);
		}
	}

	private static void Detach(Session session, List<Entity> group)
	{
		bool fresh = false;
		foreach (var spawner in session.GetEntitiesOfType<EncounterSpawnerComponent>())
		{
			var tracked = spawner.Get<EncounterSpawnerComponent>().SpawnedEntities;
			foreach (var g in group) fresh |= tracked.Remove(g);
		}
		if (session.SessionComponents.TryGet<Keen.Game2.Simulation.GameSystems.Encounters.SpaceEncounters.SpaceContractsManagerSessionComponent>() is { } space)
			foreach (var contract in space.Contracts.Values)
				foreach (var g in group) fresh |= contract.Entities.Remove(g);
		foreach (var planet in session.GetEntitiesOfType<Keen.Game2.Simulation.GameSystems.Encounters.PlanetEncounters.ContractPlanetEnvironmentModuleComponent>())
			if (AccessTools.Field(typeof(Keen.Game2.Simulation.GameSystems.Encounters.PlanetEncounters.ContractPlanetEnvironmentModuleComponent), "_entities")?.GetValue(planet.Get<Keen.Game2.Simulation.GameSystems.Encounters.PlanetEncounters.ContractPlanetEnvironmentModuleComponent>()) is IDictionary lists)
				foreach (var list in lists.Values.OfType<ICollection<Entity>>())
					foreach (var g in group) fresh |= list.Remove(g);
		if (session.SessionComponents.TryGet<ContractSessionComponent>() is { } contracts && AccessTools.Field(typeof(ContractSessionComponent), "_contracts")?.GetValue(contracts) is IDictionary local)
			foreach (var contract in local.Values)
				if (contract != null && AccessTools.Field(contract.GetType(), "Spawned")?.GetValue(contract) is ICollection<Entity> spawned)
					foreach (var g in group) fresh |= spawned.Remove(g);
		var areas = session.SessionComponents.TryGet<Keen.VRage.Core.Game.GameSystems.ManagedWorldAreas.ManagedWorldAreaSessionComponent>();
		IGridPinning? pinning = session.SessionComponents.TryGet<GridPCUTrashRemovalSessionComponent>();
		foreach (var g in group)
		{
			if (areas != null && g.Data.Has<Keen.VRage.Core.Game.GameSystems.ManagedWorldAreas.ManagedByWorldAreaTag>()) fresh |= areas.TryUnmanage(g);
			fresh |= g.Data.TryRemove<Keen.VRage.Core.Game.GameSystems.ProceduralGeneration.ProcedurallyGeneratedTag>();
			if (pinning != null && !Pins.ContainsKey(g)) Pins[g] = pinning.Pin(g.Get<CubeGridComponent>());
		}
		if (!fresh) return;
		Detached++;
		GameLog.Instance.Info($"cleanup: player grid '{Name(group[0])}' ({group.Count} grid{(group.Count == 1 ? "" : "s")}) is no longer tracked by the game's encounters, contracts or world areas; only its owner's inactivity can remove it");
	}
}
