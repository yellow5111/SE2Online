using System;
using System.Collections.Generic;
using System.Linq;
using Keen.Game2.Simulation.GameSystems.Ownership;
using Keen.Game2.Simulation.GameSystems.Player;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.Water.Components;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// Experimental water sync. SE2 2.4 never replicates water grid entities (their server composites have no prefab
/// binding in the replication strategy) and joiners have no water renderer, so debug-menu water only ever existed on
/// the host. With WaterSync on, every water grid in a replicated domain is packed with the game's own cell packer and
/// streamed to joiners (changed grids only, about twice a second, capped); WaterMirror rebuilds them on the client.
/// </summary>
internal sealed partial class HostWorld
{
	private const int WaterIntervalMs = 500;

	/// <summary>Changed grids sent per update to joiners already up to date (about 512 KiB/s); a new joiner gets everything once.</summary>
	private const int WaterBudgetBytes = 256 * 1024;

	private readonly Dictionary<(uint, int, int, int, byte), byte[]> _waterSent = new();

	private readonly HashSet<ushort> _waterClients = new();

	private long _waterNextMs;

	public long WaterGridsSent, WaterBytesSent, WaterDeferred;

	internal static bool IsWaterSignal(string signal) => signal.StartsWith("Keen.VRage.Water.", StringComparison.Ordinal) || signal.Contains(".GameSystems.Water.", StringComparison.Ordinal);

	/// <summary>Water edits from a joiner (debug menu water hand, mass fill, clear) need admin or creative.</summary>
	private bool MayEditWater(ushort clientId, long actor, Session session) =>
		IsAdminClient(clientId) || (actor != 0 && session.SessionComponents.TryGet<GameModeSessionComponent>()?.IsCreative(new IdentityId(actor)) == true);

	private void UpdateWater(Session session)
	{
		var host = _host;
		if (host == null || !host.WaterSync) return;
		long now = MpClock.NowMs();
		if (now < _waterNextMs) return;
		_waterNextMs = now + WaterIntervalMs;
		var peers = host.WaterPeers();
		_waterClients.IntersectWith(peers.Select(p => p.ClientId));
		if (peers.Count == 0)
		{
			_waterSent.Clear();
			return;
		}
		var current = new Dictionary<(uint, int, int, int, byte), WaterGridsMessage.Grid>();
		foreach (var e in session.GetEntitiesOfType<WaterGridComponent>())
		{
			var grid = e.Get<WaterGridComponent>();
			// ProxyId -1: created but not added to its domain yet (WaterTags.UnaddedGrid, which this assembly may not name at run time).
			if (grid.ProxyId == -1) continue;
			if (grid.Domain?.Entity is not { } domain || !_ids.TryGet(domain, out uint domainId)) continue;
			var g = new WaterGridsMessage.Grid(domainId, grid.PositionAsVector.X, grid.PositionAsVector.Y, grid.PositionAsVector.Z, (byte)grid.LOD, (byte)grid.CellCoords.Resolution, grid._gridPacker.PackCells(grid));
			current[g.Key] = g;
		}
		var changes = new List<WaterGridsMessage.Grid>();
		int budget = WaterBudgetBytes;
		foreach (var (key, g) in current)
		{
			if (_waterSent.TryGetValue(key, out var sent) && sent.AsSpan().SequenceEqual(g.Cells)) continue;
			if (budget < g.WireBytes && changes.Count > 0)
			{
				WaterDeferred++;
				continue;
			}
			budget -= g.WireBytes;
			changes.Add(g);
			_waterSent[key] = g.Cells!;
		}
		foreach (var key in _waterSent.Keys.Where(k => !current.ContainsKey(k)).ToList())
		{
			_waterSent.Remove(key);
			changes.Add(new WaterGridsMessage.Grid(key.Item1, key.Item2, key.Item3, key.Item4, key.Item5, 16, null));
		}
		List<byte[]>? full = null;
		List<byte[]>? delta = null;
		foreach (var (peer, clientId) in peers)
		{
			bool fresh = _waterClients.Add(clientId);
			var messages = fresh ? full ??= WaterGridsMessage.Encode(_waterSent.Select(kv => new WaterGridsMessage.Grid(kv.Key.Item1, kv.Key.Item2, kv.Key.Item3, kv.Key.Item4, kv.Key.Item5, current.TryGetValue(kv.Key, out var c) ? c.Resolution : (byte)16, kv.Value)).ToList())
				: changes.Count == 0 ? null : delta ??= WaterGridsMessage.Encode(changes);
			if (messages == null) continue;
			foreach (byte[] m in messages)
			{
				host.SendWater(peer, m);
				WaterBytesSent += m.Length;
			}
		}
		if (WaterGridsSent == 0 && changes.Count > 0) _log.Info($"water sync (experimental): first {changes.Count} water grid(s) sent to {peers.Count} joiner(s)");
		WaterGridsSent += changes.Count;
	}

	internal string DescribeWater() => _host?.WaterSync != true ? "water sync off" : $"water sync on: {_waterSent.Count} grids mirrored, {WaterGridsSent} grid updates, {MpLogExtensions.Bytes(WaterBytesSent)} sent, {WaterDeferred} deferred by the cap";
}
