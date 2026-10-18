using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.DCS.Components;
using Keen.VRage.Multiplayer.Replications;
using Keen.VRage.Multiplayer.Replications.InProcess;
using Keen.VRage.Library.Mathematics;
using Y51_SE2Online.Core;
using Y51_SE2Online.Net;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// Desync detection and repair. Each player gets a slice of the host's per-root digests every second (nearest NetId
/// ranges first), in its lifecycle order, so every spawn and despawn sent before it has been applied when it is compared.
/// The client reports what does not match; the host confirms it against a fresh digest and a second targeted check, then
/// repairs it with the streaming tools (stream the group out, the streaming update sends a fresh bundle) or removes what
/// the client should not have. A repaired root that mismatches again within ten minutes is logged as a bug and left alone.
/// All of it shares one per-frame time budget: the root scan, the digest walks (resumable across frames), report
/// validation and the checks themselves, so a frame never pays for a whole world.
/// </summary>
internal sealed partial class HostWorld
{
	private sealed class DesyncRoot
	{
		public Entity Root = null!;
		public uint Id;
		public long FirstSeenMs, ComputedMs, StableSinceMs;
		public bool Computed;
		public int Weight = 1;
		public RootDigest Digest;
	}

	private sealed class DesyncPeer
	{
		public readonly DesyncPolicy Policy = new();
		public List<(uint Lo, uint Hi)> Ranges = new();
		public int Next;
		public long NextCheckMs;
		public uint CheckId;
		public readonly Dictionary<uint, long> Rechecks = new();
	}

	private readonly record struct PendingReport(PeerId Peer, DesyncMismatch Mismatch, long AtMs);

	public static bool DesyncEnabled = true;
	public static int DesyncIntervalMs = 1000;
	public static int DesyncStartDelayMs = 20000;
	/// <summary>All desync work in one server frame stops at this.</summary>
	public static double DesyncBudgetMs = 0.5;
	private const int DesyncRootsPerCheck = 48;
	private const int DesyncWeightPerCheck = 4000;
	private const int DesyncRechecksPerFrame = 4;
	private const int DesyncWalkSteps = 16;
	private const int DesyncScanSteps = 512;
	private const long DesyncYoungMs = 5000;
	private const long DesyncSettleMs = 5000;
	private const long DesyncPendingMs = 15000;
	private const int DesyncRefreshMs = 2000;
	private const long DesyncRewalkMs = 10000;

	// Server thread only.
	private readonly Dictionary<uint, DesyncRoot> _desyncRoots = new();
	private List<uint> _desyncSorted = new();
	private uint _desyncIdLimit;
	private long _desyncRefreshAt;
	private Entity[]? _desyncScan;
	private int _desyncScanPos, _desyncScanLength;
	private HashSet<uint>? _desyncScanSeen;
	private uint _desyncScanLimit;
	private readonly Dictionary<PeerId, DesyncPeer> _desyncPeers = new();
	private readonly DesyncHash.Walk _desyncWalk = new();
	private uint _desyncWalkId, _desyncCursor;
	private readonly Queue<uint> _desyncUrgent = new();
	private readonly Dictionary<uint, List<PendingReport>> _desyncPending = new();
	private readonly Queue<(PeerId Peer, DesyncMismatch Mismatch)> _desyncInbox = new();
	private int _desyncRotate;
	private bool _desyncRepairedThisFrame;
	private double _desyncMs, _desyncMaxMs;
	private long _desyncFrames;
	private ReplayContext? _desyncReplay;
	private List<(PeerId Peer, ushort ClientId, string Name, long SyncedMs, uint CharacterNetId)> _desyncPeerList = new();
	private long _desyncPeersAt;

	private readonly double[] _desyncPhaseMax = new double[5];

	private void NotePhase(int phase, long ticks)
	{
		double ms = ticks * 1000.0 / Stopwatch.Frequency;
		if (ms > _desyncPhaseMax[phase]) _desyncPhaseMax[phase] = ms;
	}

	public string DesyncDescribe()
	{
		string line = DesyncStats.DescribeHost() + $"; {_desyncRoots.Count} roots tracked, {(_desyncFrames == 0 ? 0 : _desyncMs / _desyncFrames):0.000} ms per server frame, worst {_desyncMaxMs:0.00} ms (setup/checks/reports/scan/walk {string.Join("/", _desyncPhaseMax.Select(x => x.ToString("0.00")))})";
		_desyncMaxMs = 0;
		Array.Clear(_desyncPhaseMax);
		return line;
	}

#if !PULSAR
	/// <summary>Diagnostics: the host's last digest of a root.</summary>
	public string DesyncInfo(uint id) => _desyncRoots.TryGetValue(id, out var r)
		? $"{DesyncName(id, r.Root)}: {r.Digest}, stable for {(MpClock.NowMs() - r.StableSinceMs) / 1000.0:0} s, weight {r.Weight}"
		: $"NetworkId {id}: not a tracked root{ExplainNetId(id)}";
#endif

	private bool DesyncReplicated(Entity e) => _ids.TryGet(e, out _) && _desyncReplay?.HasPairingLocal(e) == true;

	private void UpdateDesync(InProcessReplicationSessionComponent comp)
	{
		var host = _host;
		if (host == null || !DesyncEnabled) return;
		long started = Stopwatch.GetTimestamp();
		long budget = started + (long)(DesyncBudgetMs * Stopwatch.Frequency / 1000);
		long now = MpClock.NowMs();
		// The player list takes HostSession's lock, which the net thread holds at times: refresh it a few times a second.
		if (now >= _desyncPeersAt)
		{
			_desyncPeerList = host.DesyncPeers();
			_desyncPeersAt = now + 250;
		}
		var peers = _desyncPeerList;
		if (_desyncPeers.Count > peers.Count || _desyncFrames % 64 == 0)
		{
			foreach (var gone in _desyncPeers.Keys.Where(p => !peers.Any(x => x.Peer.Equals(p))).ToList()) _desyncPeers.Remove(gone);
		}
		if (peers.Count == 0) return;
		var strategy = ((SceneContext)comp._context).Replication.ReplicationStrategy;
		_desyncReplay = comp.ReplayContext;
		_desyncRepairedThisFrame = false;
		foreach (var (peer, _, mismatches) in host.TakeDesyncReports())
		{
			foreach (var m in mismatches) _desyncInbox.Enqueue((peer, m));
		}
		// Checks first (cheap: cached digests only), then reports, then the scan and digest walks fill what is left.
		long t0 = Stopwatch.GetTimestamp();
		SendDesyncChecks(host, peers, now, budget);
		long t1 = Stopwatch.GetTimestamp();
		while (_desyncInbox.Count > 0 && Stopwatch.GetTimestamp() < budget)
		{
			var (peer, m) = _desyncInbox.Dequeue();
			OnDesyncReport(host, peers, peer, m, now);
		}
		long t2 = Stopwatch.GetTimestamp();
		ScanDesyncRoots(comp, now, budget);
		long t3 = Stopwatch.GetTimestamp();
		WalkDesyncRoots(host, peers, strategy, now, budget);
		long t4 = Stopwatch.GetTimestamp();
		NotePhase(0, t0 - started); NotePhase(1, t1 - t0); NotePhase(2, t2 - t1); NotePhase(3, t3 - t2); NotePhase(4, t4 - t3);
		double ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
		_desyncMs += ms;
		_desyncMaxMs = Math.Max(_desyncMaxMs, ms);
		_desyncFrames++;
	}

	private void SendDesyncChecks(HostSession host, List<(PeerId Peer, ushort ClientId, string Name, long SyncedMs, uint CharacterNetId)> peers, long now, long budget)
	{
		if (_desyncIdLimit == 0) return;
		_desyncRotate++;
		for (int n = 0; n < peers.Count && Stopwatch.GetTimestamp() < budget; n++)
		{
			var p = peers[(n + _desyncRotate) % peers.Count];
			if (now - p.SyncedMs < DesyncStartDelayMs) continue;
			if (!_desyncPeers.TryGetValue(p.Peer, out var state)) _desyncPeers[p.Peer] = state = new DesyncPeer { NextCheckMs = now + n * 37 % DesyncIntervalMs };
			_joiners.TryGetValue(p.ClientId, out var joiner);
			if (state.Rechecks.Count > 0)
			{
				foreach (var (id, _) in state.Rechecks.Where(kv => kv.Value <= now).Take(DesyncRechecksPerFrame).ToList())
				{
					state.Rechecks.Remove(id);
					SendDesyncCheck(host, p.Peer, p.CharacterNetId, joiner, state, id, id, now);
				}
			}
			if (now < state.NextCheckMs) continue;
			state.NextCheckMs = now + DesyncIntervalMs;
			if (state.Next >= state.Ranges.Count)
			{
				state.Ranges = OrderByDistance(DesyncDigest.Ranges(_desyncSorted, id => _desyncRoots.TryGetValue(id, out var r) ? r.Weight : 1, DesyncRootsPerCheck, DesyncWeightPerCheck), AnchorOf(joiner?.Character));
				state.Next = 0;
			}
			var (lo, hi) = state.Ranges[state.Next++];
			SendDesyncCheck(host, p.Peer, p.CharacterNetId, joiner, state, lo, hi, now);
		}
	}

	/// <summary>The root list comes from a copy of the session's entities, worked through a slice per frame.</summary>
	private void ScanDesyncRoots(InProcessReplicationSessionComponent comp, long now, long budget)
	{
		if (_desyncScan == null)
		{
			if (now < _desyncRefreshAt || Stopwatch.GetTimestamp() >= budget) return;
			_desyncScanLimit = _ids.NextId - 1;
			var entities = comp.Session.Entities;
			_desyncScan = System.Buffers.ArrayPool<Entity>.Shared.Rent(entities.Count);
			entities.CopyTo(_desyncScan, 0);
			_desyncScanLength = entities.Count;
			_desyncScanPos = 0;
			_desyncScanSeen = new HashSet<uint>();
			// The copy is this frame's share; the slices start next frame.
			return;
		}
		var replay = comp.ReplayContext;
		while (_desyncScanPos < _desyncScanLength)
		{
			if (_desyncScanPos % DesyncScanSteps == 0 && Stopwatch.GetTimestamp() >= budget) return;
			var entity = _desyncScan[_desyncScanPos++];
			if (entity == comp.Entity || entity.Data.Has<ParentData>() || !_ids.TryGet(entity, out uint id) || !replay.HasPairingLocal(entity) || EngineCodec.IsDead(entity)) continue;
			_desyncScanSeen!.Add(id);
			if (_desyncRoots.TryGetValue(id, out var root)) root.Root = entity;
			else _desyncRoots[id] = new DesyncRoot { Root = entity, Id = id, FirstSeenMs = now };
		}
		var seen = _desyncScanSeen!;
		foreach (uint id in _desyncRoots.Keys.Where(id => !seen.Contains(id)).ToList()) _desyncRoots.Remove(id);
		foreach (uint id in _desyncPending.Keys.Where(id => !seen.Contains(id)).ToList()) _desyncPending.Remove(id);
		_desyncSorted = seen.OrderBy(id => id).ToList();
		// Roots that got a NetId after the scan began are not listed yet; the client must not call them extra.
		_desyncIdLimit = _desyncScanLimit;
		Array.Clear(_desyncScan, 0, _desyncScanLength);
		System.Buffers.ArrayPool<Entity>.Shared.Return(_desyncScan);
		_desyncScan = null;
		_desyncScanSeen = null;
		_desyncRefreshAt = now + DesyncRefreshMs;
	}

	/// <summary>Recomputes digests round robin (reported roots first), resuming a big root's walk on the next frame.</summary>
	private void WalkDesyncRoots(HostSession host, List<(PeerId Peer, ushort ClientId, string Name, long SyncedMs, uint CharacterNetId)> peers, ReplicationStrategy strategy, long now, long budget)
	{
		while (Stopwatch.GetTimestamp() < budget)
		{
			if (_desyncWalkId == 0 && !NextDesyncWalk()) return;
			if (!_desyncRoots.TryGetValue(_desyncWalkId, out var r) || r.Root != _desyncWalk.Root || EngineCodec.IsDead(r.Root))
			{
				_desyncWalkId = 0;
				continue;
			}
			bool? done;
			try { done = AlwaysStreamed(r.Root) ? false : DesyncHash.Step(_desyncWalk, strategy, e => e.GetComposition(), DesyncReplicated, DesyncWalkSteps); }
			catch (Exception)
			{
				// The tree changed under a walk spread over frames; it is unsettled, start it again later.
				r.StableSinceMs = now;
				_desyncWalkId = 0;
				continue;
			}
			if (done == null) continue;
			FinishDesyncWalk(host, peers, r, done.Value, now);
			_desyncWalkId = 0;
		}
	}

	private bool NextDesyncWalk()
	{
		uint id = 0;
		while (_desyncUrgent.Count > 0 && id == 0)
		{
			uint urgent = _desyncUrgent.Dequeue();
			if (_desyncRoots.ContainsKey(urgent)) id = urgent;
		}
		if (id == 0)
		{
			if (_desyncSorted.Count == 0) return false;
			int i = _desyncSorted.BinarySearch(_desyncCursor + 1);
			if (i < 0) i = ~i;
			id = _desyncSorted[i < _desyncSorted.Count ? i : 0];
			// Everything walked recently: rest until the oldest digest is due again.
			if (_desyncRoots.TryGetValue(id, out var due) && due.Computed && MpClock.NowMs() - due.ComputedMs < DesyncRewalkMs) return false;
			_desyncCursor = id;
		}
		if (!_desyncRoots.TryGetValue(id, out var r)) return false;
		_desyncWalkId = id;
		_desyncWalk.Begin(r.Root);
		return true;
	}

	private void FinishDesyncWalk(HostSession host, List<(PeerId Peer, ushort ClientId, string Name, long SyncedMs, uint CharacterNetId)> peers, DesyncRoot r, bool hashed, long now)
	{
		var flags = DigestFlags.None;
		if (!DesyncHash.TryPosition(r.Root, out double x, out double y, out double z)) flags |= DigestFlags.NoPosition;
		if (!hashed) flags |= DigestFlags.NoDetail;
		var digest = new RootDigest(_desyncWalkId, flags, hashed ? _desyncWalk.Into.Children : 0, hashed ? _desyncWalk.Into.Hash : 0, x, y, z);
		if (!r.Computed || !digest.SameState(r.Digest)) r.StableSinceMs = now;
		r.Digest = digest;
		r.Weight = Math.Max(1, _desyncWalk.Visited);
		r.Computed = true;
		r.ComputedMs = now;
		if (!_desyncPending.Remove(_desyncWalkId, out var pending)) return;
		foreach (var report in pending)
		{
			if (now - report.AtMs > DesyncPendingMs) continue;
			var p = peers.FirstOrDefault(x => x.Peer.Equals(report.Peer));
			if (p.Peer.Equals(default(PeerId)) || !_desyncPeers.TryGetValue(report.Peer, out var state)) continue;
			_joiners.TryGetValue(p.ClientId, out var joiner);
			var m = report.Mismatch;
			string? why = StateConfirms(r, m, report.Peer, p.CharacterNetId, joiner, now);
			if (why == null)
			{
				state.Policy.Clear(m.NetId);
				DesyncStats.Count(ref DesyncStats.Discarded);
				continue;
			}
			DesyncVerdict(host, state, report.Peer, p.Name, joiner, m, r.Root, why, now);
		}
	}

	private List<(uint Lo, uint Hi)> OrderByDistance(List<(uint Lo, uint Hi)> ranges, Vector3D? anchor)
	{
		if (anchor is not { } at) return ranges;
		double Nearest((uint Lo, uint Hi) range)
		{
			double best = double.MaxValue;
			foreach (uint id in RootsIn(range.Lo, range.Hi))
			{
				if (_desyncRoots.TryGetValue(id, out var r) && r.Computed && (r.Digest.Flags & DigestFlags.NoPosition) == 0)
				{
					best = Math.Min(best, Vector3D.Distance(new Vector3D(r.Digest.X, r.Digest.Y, r.Digest.Z), at));
				}
			}
			return best;
		}
		return ranges.OrderBy(Nearest).ToList();
	}

	private IEnumerable<uint> RootsIn(uint lo, uint hi)
	{
		int i = _desyncSorted.BinarySearch(lo);
		if (i < 0) i = ~i;
		for (; i < _desyncSorted.Count && _desyncSorted[i] <= hi; i++) yield return _desyncSorted[i];
	}

	private bool StreamedOutFor(PeerId peer, uint root)
	{
		if (!_streams.TryGetValue(peer, out var stream)) return false;
		lock (_streamSync) return stream.Out.Contains(root);
	}

	private DigestFlags PeerFlags(DesyncRoot r, uint ownCharacter, Joiner? joiner, long now, DesyncPolicy? policy = null)
	{
		var flags = r.Digest.Flags;
		// Not walked yet: existence only.
		if (!r.Computed) flags |= DigestFlags.Busy | DigestFlags.NoDetail | DigestFlags.NoPosition;
		if (policy?.IsSuppressed(r.Id) == true) flags |= DigestFlags.Exempt;
		if (now - r.FirstSeenMs < DesyncYoungMs) flags |= DigestFlags.Young;
		if (now - r.StableSinceMs < DesyncSettleMs) flags |= DigestFlags.Busy;
		if (r.Id == ownCharacter || (joiner?.SeatRoot is { } seat && seat == r.Root)) flags |= DigestFlags.Exempt;
		return flags;
	}

	private void SendDesyncCheck(HostSession host, PeerId peer, uint ownCharacter, Joiner? joiner, DesyncPeer state, uint lo, uint hi, long now)
	{
		var check = new DesyncCheck { CheckId = ++state.CheckId, Lo = lo, Hi = Math.Max(lo, Math.Min(hi, _desyncIdLimit)) };
		foreach (uint id in RootsIn(lo, check.Hi))
		{
			if (!_desyncRoots.TryGetValue(id, out var r) || StreamedOutFor(peer, id)) continue;
			// Digests are walked every few seconds; a root that moved since its walk is unsettled now.
			if (r.Computed && (r.Digest.Flags & DigestFlags.NoPosition) == 0 && DesyncHash.TryPosition(r.Root, out double x, out double y, out double z)
				&& !(r.Digest with { X = x, Y = y, Z = z }).SameState(r.Digest)) r.StableSinceMs = now;
			check.Roots.Add(r.Digest with { NetId = id, Flags = PeerFlags(r, ownCharacter, joiner, now, state.Policy) });
			if (check.Roots.Count >= DesyncCheckMessage.MaxRoots) break;
		}
		if (!host.SendDesyncCheck(peer, DesyncCheckMessage.Encode(check))) return;
		DesyncStats.Count(ref DesyncStats.ChecksSent);
		DesyncStats.Count(ref DesyncStats.RootsChecked, check.Roots.Count);
	}

	private void OnDesyncReport(HostSession host, List<(PeerId Peer, ushort ClientId, string Name, long SyncedMs, uint CharacterNetId)> peers, PeerId peer, DesyncMismatch m, long now)
	{
		if (!_desyncPeers.TryGetValue(peer, out var state)) return;
		var p = peers.FirstOrDefault(x => x.Peer.Equals(peer));
		if (p.Peer.Equals(default(PeerId))) return;
		_joiners.TryGetValue(p.ClientId, out var joiner);
		Entity? root = null;
		bool tracked = _desyncRoots.TryGetValue(m.NetId, out var r) && !EngineCodec.IsDead(r.Root);
		if (tracked) root = r.Root;
		else if (_ids.TryGet(m.NetId, out Entity e) && !EngineCodec.IsDead(e)) root = e;
		string? why = null;
		bool skip = m.NetId == p.CharacterNetId || (tracked && (now - r.FirstSeenMs < DesyncYoungMs || (PeerFlags(r, p.CharacterNetId, joiner, now, state.Policy) & DigestFlags.Exempt) != 0));
		bool expected = tracked && !StreamedOutFor(peer, m.NetId);
		if (!skip && m.Kind == DesyncKind.Missing && expected) why = "the client does not have it";
		else if (!skip && m.Kind == DesyncKind.Extra && !expected)
		{
			// Alive on the host but not replicated (or not a root there): not ours to remove.
			why = tracked ? "streamed out for this player, the client still has it" : root == null ? "destroyed on the host, the client still has it" : null;
		}
		else if (!skip && expected && m.Kind is DesyncKind.Children or DesyncKind.State or DesyncKind.Position)
		{
			// Judged against a fresh digest: the walk for it runs next and finishes the report (FinishDesyncWalk).
			if (!_desyncPending.TryGetValue(m.NetId, out var list)) _desyncPending[m.NetId] = list = new List<PendingReport>();
			if (list.Count < 64) list.Add(new PendingReport(peer, m, now));
			if (list.Count == 1) _desyncUrgent.Enqueue(m.NetId);
			return;
		}
		if (why == null)
		{
			state.Policy.Clear(m.NetId);
			DesyncStats.Count(ref DesyncStats.Discarded);
			return;
		}
		DesyncVerdict(host, state, peer, p.Name, joiner, m, root, why, now);
	}

	/// <summary>Whether a fresh digest still disagrees with what the client reported; the reason when it does.</summary>
	private string? StateConfirms(DesyncRoot fresh, DesyncMismatch m, PeerId peer, uint ownCharacter, Joiner? joiner, long now)
	{
		if (StreamedOutFor(peer, m.NetId) || (PeerFlags(fresh, ownCharacter, joiner, now) & (DigestFlags.Busy | DigestFlags.Exempt | DigestFlags.Young)) != 0) return null;
		var d = fresh.Digest;
		string? why = m.Kind switch
		{
			DesyncKind.Children when (d.Flags & DigestFlags.NoDetail) == 0 && d.Children != m.Children => $"host has {d.Children} replicated child entities, the client {m.Children}",
			DesyncKind.State when (d.Flags & DigestFlags.NoDetail) == 0 && d.Hash != m.Hash => $"replicated data differs (host {d.Hash:x8}, client {m.Hash:x8})",
			DesyncKind.Position when (d.Flags & DigestFlags.NoPosition) == 0 && Distance(d, m) > DesyncDigest.PositionToleranceMeters => $"client copy is {Distance(d, m):0.0} m from the host's ({d.X:0.0},{d.Y:0.0},{d.Z:0.0})",
			_ => null
		};
		return why == null || m.Kind != DesyncKind.State ? why : why + DesyncTypeDetail(m, _desyncWalk.Into);
	}

	private void DesyncVerdict(HostSession host, DesyncPeer state, PeerId peer, string name, Joiner? joiner, DesyncMismatch m, Entity? root, string why, long now)
	{
		switch (state.Policy.Report(m.NetId, now))
		{
		case DesyncPolicy.Verdict.Recheck:
			state.Rechecks[m.NetId] = now + DesyncPolicy.RecheckDelayMs;
			DesyncStats.Count(ref DesyncStats.Rechecks);
			break;
		case DesyncPolicy.Verdict.Repair:
			if (_desyncRepairedThisFrame || !state.Policy.MayRepair(now))
			{
				state.Rechecks[m.NetId] = now + DesyncPolicy.MinRepairGapMs;
				break;
			}
			_desyncRepairedThisFrame = true;
			state.Policy.Repaired(m.NetId, now);
			DesyncRepair(host, peer, name, m, root, why);
			break;
		case DesyncPolicy.Verdict.Bug:
			DesyncStats.Count(ref DesyncStats.Bugs);
			_log.Error($"desync: BUG: {DesyncName(m.NetId, root)} for '{name}' is out of sync again {(now - state.Policy.RepairedAt(m.NetId)) / 1000.0:0} s after it was repaired ({m.Kind}: {why}); it is not repaired again for this player");
			break;
		}
	}

	private static double Distance(in RootDigest d, in DesyncMismatch m) => Math.Sqrt((d.X - m.X) * (d.X - m.X) + (d.Y - m.Y) * (d.Y - m.Y) + (d.Z - m.Z) * (d.Z - m.Z));

	private void DesyncRepair(HostSession host, PeerId peer, string name, DesyncMismatch m, Entity? root, string why)
	{
		DesyncStats.Count(ref DesyncStats.Repairs);
		if (m.Kind == DesyncKind.Extra)
		{
			DesyncStats.Count(ref DesyncStats.RepairedExtra);
			var ids = new List<uint>();
			if (root != null) CollectIds(root, ids);
			if (!ids.Contains(m.NetId)) ids.Add(m.NetId);
			host.PublishStreamOut(peer, ids);
			_log.Warn($"desync: repaired {DesyncName(m.NetId, root)} for '{name}': {why}; removed from that client");
			return;
		}
		if (m.Kind == DesyncKind.Missing) DesyncStats.Count(ref DesyncStats.RepairedMissing);
		else DesyncStats.Count(ref DesyncStats.RepairedState);
		// Out then in through the streaming update, exactly like a group leaving and re-entering the player's radius.
		var group = new List<(Entity Root, uint Id)> { (root!, m.NetId) };
		if (root!.TryGet<CubeGridComponent>()?.GridGraphHandler is { } graph)
		{
			foreach (var other in graph.GetAllConnectedEntities(root))
			{
				if (other != root && !other.Data.Has<ParentData>() && _ids.TryGet(other, out uint otherId) && !StreamedOutFor(peer, otherId)) group.Add((other, otherId));
			}
		}
		var leaving = new List<uint>();
		var stream = _streams.GetOrAdd(peer, _ => new StreamState());
		lock (_streamSync)
		{
			foreach (var (r, id) in group)
			{
				if (stream.Out.Add(id)) CollectIds(r, leaving);
				// The near-cell scan skips always-sent roots (asteroids, planets) unless asked, so without this they waited for the next full scan.
				if (AlwaysStreamed(r)) stream.AlwaysPending = true;
			}
			stream.Dirty = true;
		}
		_streamingActive = true;
		PushStreamedOut(host, peer, stream);
		host.PublishStreamOut(peer, leaving);
		_log.Warn($"desync: repairing {DesyncName(m.NetId, root)} for '{name}' ({m.Kind}): {why}; resending {group.Count} root(s) / {leaving.Count} entities to that client");
	}

	private string DesyncName(uint id, Entity? root)
	{
		string what = "?";
		try
		{
			if (root?.TryGet<CubeGridComponent>() is { } grid) what = $"grid '{grid.DisplayName}'";
			else if (root != null && DesyncHash.IsCharacter(root)) what = "character";
			else if (root != null) what = root.GetComposition()?.ToString() ?? root.ToString();
			else what = "entity" + ExplainNetId(id);
		}
		catch (Exception) { }
		return $"NetworkId {id} ({what})";
	}

	private static string DesyncTypeDetail(DesyncMismatch m, ClientRoot mine)
	{
		var theirs = m.Types.ToDictionary(t => t.Type, t => t.Sum);
		var differing = mine.Types.Keys.Union(theirs.Keys).Where(t => !mine.Types.TryGetValue(t, out uint a) || !theirs.TryGetValue(t, out uint b) || a != b)
			.Select(t => DesyncHash.NameOf(t) + (mine.Types.ContainsKey(t) ? theirs.ContainsKey(t) ? "" : " (client lacks it)" : " (host lacks it)")).ToList();
		return differing.Count == 0 ? "" : "; differing data types: " + string.Join(", ", differing.Take(12));
	}
}
