using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Mathematics;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

/// <summary>Client half of the desync checks (see HostWorld.Desync): compare a host digest slice with this world, report what differs.</summary>
internal sealed partial class ClientWorld
{
	private int _desyncLogged;

	private void CheckDesync(DesyncCheck? check)
	{
		var comp = _comp;
		var session = _session;
		var replication = _replication;
		if (check == null || comp == null || session == null || replication == null || !_worldApplied) return;
		DesyncStats.Count(ref DesyncStats.ChecksIn);
		long started = System.Diagnostics.Stopwatch.GetTimestamp();
#if !PULSAR
		while (DesyncDiagQueue.TryDequeue(out var diag)) _log.Info("desync diag: " + diag(this));
#endif
		var strategy = replication.ReplicationStrategy;
		bool Replicated(Entity e) => NetIdOf(e) is uint id && _proxies.IsLive(id);
		Entity? Live(uint id) => ClientEntity(id) is { } e && !EngineCodec.IsClosing(comp.Session, e) ? e : null;
		ClientRoot? Lookup(uint id, DigestFlags flags)
		{
			if (Live(id) is not { } e) return null;
			var mine = new ClientRoot();
			if ((flags & DigestFlags.NoPosition) == 0) mine.HasPosition = DesyncHash.TryPosition(e, out mine.X, out mine.Y, out mine.Z);
			if ((flags & (DigestFlags.NoDetail | DigestFlags.Busy)) == 0) DesyncHash.Fill(e, strategy, x => DesyncHash.ClientToServer(strategy, x), Replicated, mine, out _);
			return mine;
		}
		var roots = _proxies.LiveIn(check.Lo, check.Hi).Where(id => id != 1 && Live(id) is { } e && !e.Data.Has<ParentData>());
		var mismatches = DesyncDigest.Compare(check, Lookup, roots, _ownNetId);
		DesyncStats.NoteClientMs(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
		if (mismatches.Count == 0) return;
		DesyncStats.Count(ref DesyncStats.ReportedOut, mismatches.Count);
		session.SendDesyncReport(check.CheckId, mismatches);
		if (_desyncLogged++ < 200)
		{
			_log.Info($"desync: host check {check.CheckId} [{check.Lo}..{check.Hi}] ({check.Roots.Count} roots): " + string.Join(", ", mismatches.Take(8).Select(m => $"NetworkId {m.NetId} {m.Kind}")) + (mismatches.Count > 8 ? $" and {mismatches.Count - 8} more" : "") + " reported");
		}
	}

#if !PULSAR
	internal static readonly ConcurrentQueue<Func<ClientWorld, string>> DesyncDiagQueue = new();

	/// <summary>Test corruption, run on the replay thread before the next host check: "drop [netId]" closes a grid on this
	/// client only, "move [netId] dx dy dz" shifts it, "state [netId]" changes one byte of its replicated data; without a NetworkId, the nearest grid to this player.</summary>
	internal static string DesyncDiag(string command)
	{
		var words = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
		if (words.Length == 0 || words[0] == "status") return DesyncStats.DescribeClient();
		DesyncDiagQueue.Enqueue(world =>
		{
			uint id = words.Length > 1 && uint.TryParse(words[1], out uint given) && given != 0 ? given : world.NearestGrid();
			var e = world.ClientEntity(id);
			if (e == null) return $"NetworkId {id}: not on this client";
			if (words[0] == "info")
			{
				DesyncHash.TryPosition(e, out double x, out double y, out double z);
				return $"NetworkId {id}: {e.GetComposition()}, parent {e.Data.Has<ParentData>()}, grid {e.TryGet<CubeGridComponent>() != null}, at ({x:0.0},{y:0.0},{z:0.0}), closing {EngineCodec.IsClosing(world._comp!.Session, e)}";
			}
			if (words[0] == "drop")
			{
				world._comp!.Session.MarkEntityForClose(e);
				return $"closed NetworkId {id} on this client only";
			}
			if (words[0] == "move" && words.Length >= 5 && e.Data.TryGet<WorldTransform>(out var t))
			{
				var by = new Vector3D(double.Parse(words[2], System.Globalization.CultureInfo.InvariantCulture), double.Parse(words[3], System.Globalization.CultureInfo.InvariantCulture), double.Parse(words[4], System.Globalization.CultureInfo.InvariantCulture));
				e.Data.Set(new WorldTransform(t.Position + by, t.Orientation));
				return $"moved NetworkId {id} by {by} on this client only";
			}
			if (words[0] == "state" && world._replication != null)
			{
				return DesyncHash.Corrupt(e, world._replication.ReplicationStrategy, x => DesyncHash.ClientToServer(world._replication.ReplicationStrategy, x)) is { } type
					? $"flipped a byte of {type} on NetworkId {id} on this client only" : $"NetworkId {id}: no hashed data to change";
			}
			return "unknown desync diag: " + command;
		});
		return "queued for the next host check: " + command;
	}

	private uint NearestGrid()
	{
		var own = _ownNetId != 0 ? ClientEntity(_ownNetId) : null;
		if (own == null || !DesyncHash.TryPosition(own, out double x, out double y, out double z)) return 0;
		uint best = 0;
		double bestDistance = double.MaxValue;
		foreach (uint id in _proxies.LiveIn(2, uint.MaxValue))
		{
			if (ClientEntity(id) is not { } e || e.Data.Has<ParentData>() || e.TryGet<CubeGridComponent>() == null || !DesyncHash.TryPosition(e, out double gx, out double gy, out double gz)) continue;
			double d = (gx - x) * (gx - x) + (gy - y) * (gy - y) + (gz - z) * (gz - z);
			if (d < bestDistance) (best, bestDistance) = (id, d);
		}
		return best;
	}
#endif
}
