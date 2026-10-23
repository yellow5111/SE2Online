using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Keen.Game2.Client.WorldObjects.CubeBlocks.Toolbar;
using Keen.Game2.Simulation.StreamedUI.Terminal.ControlPanel;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.Toolbar;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.DCS.Components;
using Keen.VRage.Multiplayer.Replications.FailureStrategies;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>
/// Sitting down asks the server for the seat's toolbar (BlockToolbarControllerComponent: RequestToolbarIdFromEntity, then
/// RequestToolbarFromId). The seat goes as SkipOnReplicationFail&lt;Entity&gt;, but SE2 4.0 only skips a story for an
/// unreplicated observable: a block the player's game holds without a network id is sent as null, the server's TryGetAt
/// faulted on it, and the player's game kept that failed request (BlockToolbarReplicationSessionComponent removes a pending
/// request only after it succeeds), so that cockpit's toolbar never loaded again until the player left the world.
/// The server answers such a request with "no toolbar" (<see cref="ToolbarStoryRule"/>), the player's game drops a failed
/// request so the next seating asks again, and it does not ask for a block it cannot name.
/// </summary>
internal static class ToolbarRequests
{
	public static long Unnamed, Disarmed, HintsRefused;

	// ToolbarActionHintReplicationSessionComponent.GetTile: same block and index, but it asserts the toolbar and builds a
	// model from it, so a request naming no block is refused before it runs (ServerAccess.RestoredStoryPostfix: the swap
	// must wait until the transport has completed the server request inside the restored state machine).
	private static System.Type? _hintMachine;

	private static System.Reflection.FieldInfo? _hintBlock;

	public static void Install(Harmony harmony)
	{
		// First, so the hint check (run from ServerAccess) is set up even if a patch below fails.
		_hintMachine = typeof(ToolbarActionHintReplicationSessionComponent).GetNestedTypes(System.Reflection.BindingFlags.NonPublic).FirstOrDefault(t => t.Name.StartsWith("<GetTile>", System.StringComparison.Ordinal));
		_hintBlock = _hintMachine?.GetField("toolbarBlock") is { } block && block.FieldType == typeof(SkipOnReplicationFail<Entity>) ? block : null;
		// ProjectileWeaponBaseComponent.Detach: the same shape (it calls entity.Value.TryGet on the server, and a weapon the
		// player's game could not name arrives as null: an error and an assertion each time on PingPerfect).
		_weaponMachine = typeof(Keen.Game2.Simulation.WorldObjects.Weapons.ProjectileWeaponBaseComponent).GetNestedTypes(System.Reflection.BindingFlags.NonPublic).FirstOrDefault(t => t.Name.StartsWith("<Detach>", System.StringComparison.Ordinal));
		_weaponEntity = _weaponMachine?.GetField("entity") is { } weapon && weapon.FieldType == typeof(SkipOnReplicationFail<Entity>) ? weapon : null;
		if (_weaponEntity == null) GameLog.Instance.Warn("toolbars: ProjectileWeaponBaseComponent.Detach not found; weapon detach requests naming no weapon are not refused");
		if (_hintBlock == null) GameLog.Instance.Warn("toolbars: ToolbarActionHintReplicationSessionComponent.GetTile not found; toolbar hint requests naming no block are not refused");
		harmony.Patch(AccessTools.Method(typeof(BlockToolbarReplicationSessionComponent), nameof(BlockToolbarReplicationSessionComponent.TryGetToolbar), new[] { typeof(Entity), typeof(int) })
			?? throw new System.MissingMethodException("BlockToolbarReplicationSessionComponent.TryGetToolbar(Entity, int)"),
			prefix: new HarmonyMethod(typeof(ToolbarRequests), nameof(TryGetToolbarPrefix)));
		// Detach is async void: awaiting a failed request there rethrows on the game's scheduler.
		harmony.Patch(AccessTools.Method(typeof(BlockToolbarReplicationSessionComponent), nameof(BlockToolbarReplicationSessionComponent.Detach), new[] { typeof(Entity), typeof(int) })
			?? throw new System.MissingMethodException("BlockToolbarReplicationSessionComponent.Detach(Entity, int)"),
			prefix: new HarmonyMethod(typeof(ToolbarRequests), nameof(DetachPrefix)));
	}

	/// <summary>A player's story rebuilt on the server (Engine/OwnStories.Restore): only the stories whose code answers a null
	/// toolbar with nothing (the id request and the tile press and clears) are disarmed.</summary>
	internal static void Restored(IAsyncStateMachine __result)
	{
		if (__result == null || !MpPlugin.Online) return;
		var owner = __result.GetType().DeclaringType;
		if (owner != typeof(BlockToolbarReplicationServerSessionComponent) && owner != typeof(BlockToolbarServerComponent)) return;
		if (!ToolbarStoryRule.Disarm(__result, v => v is SkipOnReplicationFail<Entity> { Value: null })) return;
		if (System.Threading.Interlocked.Increment(ref Disarmed) == 1)
			GameLog.Instance.Warn("toolbars: a player asked for a toolbar of a block their game could not name; answered with no toolbar (their game asks again when they sit down again)");
	}

	private static System.Type? _weaponMachine;

	private static System.Reflection.FieldInfo? _weaponEntity;

	public static long WeaponDetachesRefused;

	/// <summary>A player's request that names no entity where the server's story needs one: a toolbar hint without its block,
	/// a weapon detach without its weapon (refused by ServerAccess as a cancellation).</summary>
	internal static bool NamesNoHintBlock(IAsyncStateMachine machine)
	{
		if (!MpPlugin.Online) return false;
		var type = machine.GetType();
		if (_weaponEntity != null && type == _weaponMachine)
		{
			if (_weaponEntity.GetValue(machine) is not SkipOnReplicationFail<Entity> { Value: null }) return false;
			if (System.Threading.Interlocked.Increment(ref WeaponDetachesRefused) == 1)
				GameLog.Instance.Warn("weapons: a player's game asked to detach from a weapon it could not name; refused");
			return true;
		}
		if (_hintBlock == null || type != _hintMachine) return false;
		if (_hintBlock.GetValue(machine) is not SkipOnReplicationFail<Entity> { Value: null }) return false;
		if (System.Threading.Interlocked.Increment(ref HintsRefused) == 1)
			GameLog.Instance.Warn("toolbars: a player asked for a toolbar hint of a block their game could not name; refused");
		return true;
	}

	private static bool TryGetToolbarPrefix(BlockToolbarReplicationSessionComponent __instance, Entity toolbar, int toolbarIndex,
		ref Keen.VRage.Library.Threading.Task<BlockToolbarModel> __result)
	{
		if (!MpPlugin.Online || toolbar == null) return true;
		var key = (toolbar.DEntity, toolbarIndex);
		DropFailed(__instance, toolbar, toolbarIndex);
		foreach (var model in __instance._pendingModels.Where(p => Failed(p.Value.GetAwaiter())).Select(p => p.Key).ToList()) __instance._pendingModels.Remove(model);
		if (__instance._entityToId.ContainsKey(key)) return true;
		// The co-op host's own game runs in process: no network ids, nothing to name.
		var session = __instance.Entity.GetSession();
		var mapper = Engine.OwnHost.Existing(session) is { IsServer: false } own ? own.Ids : null;
		if (mapper == null || mapper.TryGetId(toolbar) != null || mapper.TryGetRetiredId(toolbar) != null) return true;
		if (System.Threading.Interlocked.Increment(ref Unnamed) == 1)
			GameLog.Instance.Warn($"toolbars: {Name(toolbar)} ({toolbar}) has no network id in this game, so its toolbar cannot be asked for yet; sitting down again asks again. {Why(session.SessionComponents, mapper, toolbar)}");
		__result = Keen.VRage.Library.Threading.Task.FromResult<BlockToolbarModel>(null);
		return false;
	}

	private static void DetachPrefix(BlockToolbarReplicationSessionComponent __instance, Entity toolbar, int toolbarIndex)
	{
		if (MpPlugin.Online && toolbar != null) DropFailed(__instance, toolbar, toolbarIndex);
	}

	// A failed id request stays pending in SE2 4.0: every later sit awaits it again and Detach rethrows it.
	private static void DropFailed(BlockToolbarReplicationSessionComponent owner, Entity toolbar, int toolbarIndex)
	{
		var key = (toolbar.DEntity, toolbarIndex);
		if (!owner._pendingEntityIds.TryGetValue(key, out var pendingId) || !Failed(pendingId.GetAwaiter())) return;
		owner._pendingEntityIds.Remove(key);
		if (FaultLogged.Add(toolbar.DEntity))
			GameLog.Instance.Warn($"toolbars: the server failed the toolbar request for {Name(toolbar)} ({toolbar}, toolbar {toolbarIndex}): {Fault(pendingId.GetAwaiter())}; asking again");
	}

	// Main thread only (TryGetToolbar's and Detach's callers): the seats whose failed request was already logged.
	private static readonly System.Collections.Generic.HashSet<Keen.VRage.DCS.Accessors.DEntity> FaultLogged = new();

	private static string Name(Entity entity) => Safe(() => entity.GetComposition()?.DebugNameShort ?? "?");

	private static string Fault<T>(Keen.VRage.Library.Threading.TaskAwaiter<T> awaiter) => Safe(() => awaiter.GetExceptionResult() is { } e ? e.SourceException.GetType().Name + ": " + e.SourceException.Message : "?");

	// What settles why a seat has no network id: whether Keen's replay pairs it (a client-only entity gets Keen's ExclusiveTag
	// when it enters the scene), whether it is still a stub, and its grid ("_Split" in the name is a split made in this game).
	private static string Why(Entity components, Engine.OwnIds mapper, Entity seat)
	{
		string paired = Safe(() => components.TryGet<Keen.VRage.Multiplayer.Replications.InProcess.LegacyClientSessionComponent>() is { } replication
			? replication.ReplayContext.HasPairingLocal(seat).ToString() : "no replication component");
		string stub = Safe(() => Keen.VRage.DCS.Builders.EntityAllocator.IsStub(seat).ToString());
		string clientOnly = Safe(() => seat.Data.Has<Keen.VRage.Multiplayer.Replications.ExclusiveTag>().ToString());
		string grid = Safe(() =>
		{
			var top = Keen.VRage.Core.Game.Data.ParentDataFunctions.GetTopLevelParent(seat);
			if (top == null || ReferenceEquals(top, seat)) return "none";
			return $"{top}, network id {mapper.TryGetId(top)?.ToString() ?? mapper.TryGetRetiredId(top)?.ToString() ?? "none"}, {top.GetComposition()?.DebugNameShort ?? "?"}";
		});
		return $"Paired by the replay: {paired}; stub: {stub}; client-only tag: {clientOnly}; grid: {grid}";
	}

	private static string Safe(System.Func<string> read)
	{
		try { return read(); }
		catch (System.Exception ex) { return "(" + ex.GetType().Name + ")"; }
	}

	private static bool Failed<T>(Keen.VRage.Library.Threading.TaskAwaiter<T> awaiter) => awaiter.IsCompleted && awaiter.GetExceptionResult() != null;
}
