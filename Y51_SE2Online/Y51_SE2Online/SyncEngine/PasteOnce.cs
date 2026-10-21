using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Keen.Game2.Client.GameSystems.BlockPlacement.BlockPlacer;
using Keen.Game2.Simulation.GameSystems.BlockPlacement;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.ObjectBuilders;
using Keen.VRage.Input;
using Keen.VRage.Library.Mathematics;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// In a multiplayer session a paste projection (toolbar blueprint, blueprint screen, Ctrl+V, copy or cut) places once.
/// Clicks while its placement is on the way to the host are dropped; a placed paste ends the projection the way SE2's
/// own Cancel does; a failed one leaves it up to move and retry. The host refuses a player's paste that overlaps their
/// own from a moment ago (<see cref="PasteGuard"/>), so a modified client cannot stack them either.
/// </summary>
internal static class PasteOnce
{
	private static readonly PasteGuard Guard = new();
	private static readonly Action Placed = OnPlaced;
	private static FieldInfo _state = null!, _this = null!;
	private static BlockPlacerEntityComponent? _sending, _tool;
	private static ActiveBundleData? _sendingPrefab, _prefab;
	private static bool _inFlight, _placed;

	public static long ClicksDropped, Ended, HostRefused;

	internal static void Install(Harmony harmony)
	{
		if (Dedicated.DedicatedRuntime.Headless) return;
		MethodInfo build = AccessTools.Method(typeof(BlockPlacerEntityComponent), "Build", new[] { typeof(bool), typeof(ControlActivation) });
		// Build is async void and awaits only the placement story: its state machine says when that is out and back.
		Type sm = build.GetCustomAttribute<AsyncStateMachineAttribute>()!.StateMachineType;
		_state = AccessTools.Field(sm, "<>1__state") ?? throw new MissingFieldException(sm.Name, "<>1__state");
		_this = AccessTools.Field(sm, "<>4__this") ?? throw new MissingFieldException(sm.Name, "<>4__this");
		harmony.Patch(build, prefix: new HarmonyMethod(typeof(PasteOnce), nameof(BuildPrefix)), postfix: new HarmonyMethod(typeof(PasteOnce), nameof(BuildPostfix)));
		harmony.Patch(AccessTools.Method(sm, "MoveNext"), postfix: new HarmonyMethod(typeof(PasteOnce), nameof(StepPostfix)));
	}

	private static bool BuildPrefix(BlockPlacerEntityComponent __instance)
	{
		_sending = null;
		if (!AdminAccess.InSession || !__instance.IsPasteMode || __instance.ActivePrefab is not { } prefab) return true;
		if (prefab == _prefab && (_inFlight || _placed))
		{
			if (++ClicksDropped <= 5) GameLog.Instance.Info($"paste once: click dropped, this projection {(_placed ? "already placed" : "is still on its way to the host")}");
			return false;
		}
		__instance.OnPrefabPlaced -= Placed;
		__instance.OnPrefabPlaced += Placed;
		_sending = __instance;
		_sendingPrefab = prefab;
		return true;
	}

	private static void BuildPostfix() => _sending = null;

	private static void StepPostfix(object __instance)
	{
		if (_sending == null && !_inFlight) return;
		object tool = _this.GetValue(__instance)!;
		int state = (int)_state.GetValue(__instance)!;
		if (state == 0 && tool == _sending)
		{
			_tool = _sending;
			_prefab = _sendingPrefab;
			_inFlight = true;
			_placed = false;
		}
		else if (state == -2 && tool == _tool)
		{
			_inFlight = false;
		}
	}

	private static void OnPlaced()
	{
		if (!AdminAccess.InSession) return;
		BlockPlacerEntityComponent? tool = _sending ?? _tool;
		if (tool == null) return;
		if (_sending != null) _prefab = _sendingPrefab;
		_tool = tool;
		_placed = true;
		_inFlight = false;
		if (++Ended <= 5) GameLog.Instance.Info("paste once: paste placed, projection ended" + (tool._selectedBlueprintToolbarTile != null ? " (toolbar blueprint cancelled)" : ""));
		// Clipboard and blueprint-screen pastes already close on this callback (ToolbarInputHandlerComponent.FinishPasteOperation);
		// a toolbar blueprint stays up for another paste unless cancelled.
		if (tool._selectedBlueprintToolbarTile != null) tool.CancelPlacement();
	}

	/// <summary>Host, for a player's PlaceStandalone story (from ServerAccess's story check): null to run it, else why it is refused.</summary>
	internal static string? HostGate(ushort clientId, object box)
	{
		Type type = box.GetType();
		if (type.DeclaringType != typeof(BlockPlacerNetworkStories) || !type.Name.StartsWith("<PlaceStandalone>", StringComparison.Ordinal)) return null;
		try
		{
			var fields = ObjectCodec.Fields(type);
			if (fields.FirstOrDefault(f => f.Name == "detachedBundle")?.GetValue(box) is not Keen.VRage.Multiplayer.Utils.Detached<EntityBundle> { Value: { } bundle } || bundle.Roots == 0
				|| fields.FirstOrDefault(f => f.Name == "newGrid")?.GetValue(box) is not BlockPlacerNetworkStories.NewGridArgs grid) return null;
			EntityObjectBuilder root = bundle.RootBuilders[0];
			// One block is ordinary placement (also line and plane modes); a paste carries more.
			if (root.OB<HierarchyComponent, HierarchyComponentObjectBuilder>().Children.Count(c => c.Has<CubeBlockComponent>()) < 2) return null;
			var obb = new OrientedBoundingBoxD((BoundingBoxD)CubeGridCoords.GridToLocal(ComplexTopologyAggregator.GetBoundingBox(root)), (MatrixD)grid.Transform);
			if (Guard.TryAccept(clientId, BuildGhosts.Box(obb), MpClock.NowMs())) return null;
			if (++HostRefused <= 5) GameLog.Instance.Info($"paste guard: refused ClientId {clientId}'s paste overlapping their own from the last {PasteGuard.WindowMs / 1000} s");
			return "that paste lands on the grid you pasted a moment ago";
		}
		catch (Exception ex)
		{
			GameLog.Instance.WarnOnce("paste-once-host", $"paste guard: could not read a paste request ({ex.GetType().Name}: {ex.Message}); pastes are not checked for stacking");
			return null;
		}
	}

	public static string Describe() => $"paste once: clicks dropped {ClicksDropped}, projections ended {Ended}, stacked pastes refused {HostRefused}";
}
