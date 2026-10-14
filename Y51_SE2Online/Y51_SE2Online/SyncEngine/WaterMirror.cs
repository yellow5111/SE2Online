using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Keen.Game2.Client.GameSystems.Water;
using Keen.VRage.Core.Game.Definitions;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.Components;
using Keen.VRage.DCS.ObjectBuilders;
using Keen.VRage.Library.Definitions;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Library.Utils;
using Keen.VRage.Multiplayer.Data;
using Keen.VRage.Water;
using Keen.VRage.Water.Client.Components;
using Keen.VRage.Water.Components;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// Joiner side of the experimental water sync (HostWorld.Water): rebuilds the host's water grids inside the replicated
/// domains from the game's own packed cell data, and gives the joiner's client session the water renderer, which the
/// game only adds to an in-process server session.
/// </summary>
internal static class WaterMirror
{
	private static readonly Dictionary<(uint, int, int, int, byte), Entity> Grids = new();

	public static long Applied, Removed, Skipped;

	private static bool _logged;

	internal static void Install(Harmony harmony)
	{
		harmony.Patch(AccessTools.Method(typeof(WaterSessionClientConfiguratorComponent), nameof(WaterSessionClientConfiguratorComponent.ConfigureSession)), postfix: new HarmonyMethod(typeof(WaterMirror), nameof(ConfigurePostfix)));
		harmony.Patch(AccessTools.Method(typeof(WaterGridComponent), "Init", new[] { typeof(WaterGridComponentObjectBuilder) }), transpiler: new HarmonyMethod(typeof(WaterMirror), nameof(InitTranspiler)));
	}

	/// <summary>
	/// SE2 2.4 refuses water grids outside the "Server" scene ("Water Syncing to Clients was disabled"). The throw becomes
	/// a call that still throws unless the host streams water to this joiner.
	/// </summary>
	private static IEnumerable<CodeInstruction> InitTranspiler(IEnumerable<CodeInstruction> instructions)
	{
		var list = instructions.ToList();
		int guard = list.FindIndex(i => i.opcode == System.Reflection.Emit.OpCodes.Ldstr && i.operand is string text && text.StartsWith("Water Syncing to Clients was disabled", StringComparison.Ordinal));
		int throwAt = guard < 0 ? -1 : list.FindIndex(guard, i => i.opcode == System.Reflection.Emit.OpCodes.Throw);
		if (throwAt < 0) throw new InvalidOperationException("water sync: the client water grid guard was not found in WaterGridComponent.Init");
		list[throwAt] = new CodeInstruction(System.Reflection.Emit.OpCodes.Call, AccessTools.Method(typeof(WaterMirror), nameof(ClientGridGuard))).MoveLabelsFrom(list[throwAt]);
		return list;
	}

	private static void ClientGridGuard(Exception refused)
	{
		if (!WaterGridsMessage.Enabled) throw refused;
	}

	private static void ConfigurePostfix(SessionBuilder sessionBuilder)
	{
		if (WaterGridsMessage.Enabled && !sessionBuilder.IsServer) sessionBuilder.SessionComponents.WithComponent(typeof(WaterRenderComponent));
	}

	internal static void Reset()
	{
		WaterGridsMessage.Inbox.Clear();
		Grids.Clear();
	}

	/// <summary>Game thread, once per client tick.</summary>
	internal static void Apply(Func<uint, Entity?> resolve)
	{
		while (WaterGridsMessage.Inbox.TryDequeue(out byte[] data))
		{
			List<WaterGridsMessage.Grid> grids;
			try { grids = WaterGridsMessage.Decode(data); }
			catch (Exception ex) when (ex is FormatException or ArgumentOutOfRangeException)
			{
				Skipped++;
				continue;
			}
			foreach (var g in grids)
			{
				try { ApplyOne(g, resolve); }
				catch (Exception ex)
				{
					Skipped++;
					if (Skipped % 500 == 1) GameLog.Instance.Info("water sync: a water grid could not be applied: " + RemoteClientGame.Describe(ex) + (Skipped == 1 ? " " + ex : ""));
				}
			}
		}
	}

	private static void ApplyOne(WaterGridsMessage.Grid g, Func<uint, Entity?> resolve)
	{
		if (Grids.TryGetValue(g.Key, out var entity) && EngineCodec.IsDead(entity))
		{
			Grids.Remove(g.Key);
			entity = null;
		}
		if (g.Cells == null)
		{
			if (entity?.Get<WaterGridComponent>() is { } gone)
			{
				var owner = gone.Domain;
				bool forbid = owner.DebugForbidWatergridRemovals;
				owner.DebugForbidWatergridRemovals = false;
				try { owner.RemoveGridHandler(gone); }
				finally { owner.DebugForbidWatergridRemovals = forbid; }
				Removed++;
			}
			Grids.Remove(g.Key);
			return;
		}
		if (entity == null)
		{
			var domain = resolve(g.Domain)?.TryGet<WaterDomainComponent>();
			if (domain == null)
			{
				Skip($"water domain NetworkId {g.Domain} is not here (yet)");
				return;
			}
			entity = CreateGrid(domain, g);
			if (entity == null)
			{
				Skip("no client prefab is bound to " + domain._waterDomainDefinition.WaterGridPrefab.Composition.Guid);
				return;
			}
			Grids[g.Key] = entity;
		}
		// The game's own handler for its (never enabled) cell sync: unpack, refresh domain influence and render data.
		entity.Get<WaterGridComponent>().HandleIncomingCellData(g.Cells);
		Applied++;
		if (!_logged)
		{
			_logged = true;
			GameLog.Instance.Info("water sync (experimental): the host's water grids are mirrored here");
		}
	}

	private static readonly Dictionary<PrefabDefinition, PrefabDefinition?> ClientPrefabs = new();

	/// <summary>Server water grid composite to client grid prefab, from WaterGridPrefab.def and DynamicWaterGridPrefab.def.</summary>
	private static readonly Dictionary<Guid, Guid> KnownClientPrefabs = new()
	{
		[new Guid("9d08c40c-00ab-4ff2-ac8d-be04c5505f4c")] = new Guid("48bf916f-c8e3-4a1e-a7a0-56599097da56"),
		[new Guid("4ba5408e-f8a5-4924-b162-54f99709abaa")] = new Guid("33be1394-8091-4159-a9de-bc8561255fa9")
	};

	private static void Skip(string why)
	{
		if (Skipped++ % 500 == 0) GameLog.Instance.Info($"water sync: grid skipped ({Skipped} so far): {why}");
	}

	/// <summary>
	/// WaterDomainComponent.CreateWaterGrid with the client prefab: the domain definition names the server grid prefab,
	/// whose composite a client scene cannot allocate, so the grid is built from the prefab its binding names for clients.
	/// </summary>
	private static Entity? CreateGrid(WaterDomainComponent domain, WaterGridsMessage.Grid g)
	{
		var server = domain._waterDomainDefinition.WaterGridPrefab;
		if (!ClientPrefabs.TryGetValue(server, out var client))
		{
			Guid composite = server.Composition.Guid;
			Guid? bound = Singleton<DefinitionManager>.Instance.GetDefinitionsOfType<PrefabBindingDefinition>().Where(b => b.ServerComposite == composite).Select(b => (Guid?)b.ClientPrefab).FirstOrDefault();
			// 2.4 ships these bindings (System/Water/WaterGrid) but does not load them, so fall back to the same pairs.
			bound ??= KnownClientPrefabs.TryGetValue(composite, out Guid known) ? known : null;
			client = bound is Guid id && Singleton<DefinitionManager>.Instance.TryGetDefinition(id, out PrefabDefinition p) ? p : null;
			ClientPrefabs[server] = client;
		}
		if (client == null) return null;
		EntityObjectBuilder ob = client.Get();
		ob.DebugName = "Water Grid";
		ref WaterGridComponentObjectBuilder grid = ref ob.OB<WaterGridComponent, WaterGridComponentObjectBuilder>();
		grid.LOD = g.Lod;
		grid.Resolution = g.Resolution;
		grid.Position = new Vector3I(g.X, g.Y, g.Z);
		grid.CellsBlob = null;
		grid.ExecutionSignature = domain.GetNextGridSignature();
		grid.SkipAdd = false;
		grid.Flags = GridFlags.Unknown;
		return domain._hierarchyComponent.AddChild(ob);
	}

	internal static string Describe() => $"water sync: {Grids.Count} grids mirrored, {Applied} applied, {Removed} removed, {Skipped} skipped";
}
