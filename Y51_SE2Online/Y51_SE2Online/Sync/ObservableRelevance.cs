using System;
using System.Collections.Generic;
using System.Linq;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.Sync;

public sealed class ObservableRelevance<TPeer> where TPeer : notnull
{
	private sealed class PeerState
	{
		public readonly HashSet<uint> Has = new HashSet<uint>();

		public readonly HashSet<uint> Roots = new HashSet<uint>();

		public readonly Dictionary<uint, long> PendingRoots = new Dictionary<uint, long>();

		public List<ObservableOp> Out = new List<ObservableOp>();

		public long Snapshots;

		public long Ops;

		public long Releases;

		public long Deferred;

		public readonly Dictionary<(uint Oid, string Member), long> LastSent = new Dictionary<(uint, string), long>();

		public readonly Dictionary<(uint Oid, string Member), ObservableOp> Held = new Dictionary<(uint, string), ObservableOp>();
	}

	public delegate (ObservableOp Op, IReadOnlyList<uint> Children)? SnapshotFn(uint oid);

	private readonly SnapshotFn _snapshot;

	private readonly Func<long> _clock;

	private readonly Dictionary<TPeer, PeerState> _peers = new Dictionary<TPeer, PeerState>();

	private readonly HashSet<uint> _inProgress = new HashSet<uint>();

	private readonly Dictionary<uint, int> _holders = new Dictionary<uint, int>();

	public long RootsAdded;

	public long RootsExpired;

	public long SnapshotsMissing;

	public long Coalesced;

	public const int RootWaitMs = 10000;

	public const int MemberIntervalMs = 50;

	public int PeerCount => _peers.Count;

	public ObservableRelevance(SnapshotFn snapshot, Func<long>? clock = null)
	{
		_snapshot = snapshot;
		_clock = clock ?? new Func<long>(MpClock.NowMs);
	}

	public bool AnyHas(uint oid) => _holders.ContainsKey(oid);

	public bool Has(TPeer peer, uint oid)
	{
		if (_peers.TryGetValue(peer, out PeerState? state))
		{
			return state.Has.Contains(oid);
		}
		return false;
	}

	public int CountFor(TPeer peer)
	{
		if (!_peers.TryGetValue(peer, out PeerState? state))
		{
			return 0;
		}
		return state.Has.Count;
	}

	private PeerState State(TPeer peer)
	{
		if (!_peers.TryGetValue(peer, out PeerState? state))
		{
			state = _peers[peer] = new PeerState();
		}
		return state;
	}

	public void AddRoot(TPeer peer, uint oid, long nowMs)
	{
		PeerState state = State(peer);
		if (!state.Roots.Contains(oid) || !state.Has.Contains(oid))
		{
			state.PendingRoots.TryAdd(oid, nowMs);
		}
	}

	public int ProcessPendingRoots(long nowMs)
	{
		int added = 0;
		foreach (var (peer, state) in _peers)
		{
			if (state.PendingRoots.Count == 0)
			{
				continue;
			}
			foreach (var (oid, since) in state.PendingRoots.ToList())
			{
				if (EnsureSent(peer, state, oid))
				{
					state.PendingRoots.Remove(oid);
					state.Roots.Add(oid);
					RootsAdded++;
					added++;
				}
				else if (nowMs - since > 10000)
				{
					state.PendingRoots.Remove(oid);
					RootsExpired++;
				}
			}
		}
		return added;
	}

	private bool EnsureSent(TPeer peer, PeerState p, uint oid)
	{
		if (p.Has.Contains(oid))
		{
			return true;
		}
		if (!_inProgress.Add(oid))
		{
			return false;
		}
		try
		{
			var snapshot = _snapshot(oid);
			if (!snapshot.HasValue)
			{
				SnapshotsMissing++;
				return false;
			}
			foreach (uint child in snapshot.Value.Children)
			{
				if (child != oid)
				{
					EnsureSent(peer, p, child);
				}
			}
			p.Out.Add(snapshot.Value.Op);
			p.Has.Add(oid);
			_holders[oid] = !_holders.TryGetValue(oid, out var holders) ? 1 : holders + 1;
			p.Snapshots++;
			return true;
		}
		finally
		{
			_inProgress.Remove(oid);
		}
	}

	/// <param name="everyChange">An event member (a fresh value per occurrence, e.g. a transfer toast): every change is sent in order, never rate-capped.</param>
	public void OnChange(ObservableOp op, TPeer? origin = default, bool hasOrigin = false, bool everyChange = false)
	{
		if (!_holders.ContainsKey(op.Oid))
		{
			return;
		}
		List<uint>? refs = null;
		long now = 0L;
		foreach (var (peer, state) in _peers)
		{
			if (!state.Has.Contains(op.Oid) || (hasOrigin && EqualityComparer<TPeer>.Default.Equals(peer, origin)))
			{
				continue;
			}
			refs ??= op.Refs();
			if (op.Kind == ObservableOpKind.Member && refs.Count == 0 && !everyChange)
			{
				(uint, string) key = (op.Oid, op.Text);
				if (now == 0L)
				{
					now = _clock();
				}
				if (state.LastSent.TryGetValue(key, out var lastSent) && now - lastSent < 50)
				{
					state.Held[key] = op;
					state.Deferred++;
					continue;
				}
				state.LastSent[key] = now;
				state.Held.Remove(key);
			}
			foreach (uint oid in refs)
			{
				EnsureSent(peer, state, oid);
			}
			Append(state, op, everyChange);
		}
	}

	private void Append(PeerState p, ObservableOp op, bool everyChange = false)
	{
		if (op.Kind == ObservableOpKind.Member && p.Out.Count > 0 && !everyChange)
		{
			ObservableOp last = p.Out[^1];
			if (last != null && last.Kind == ObservableOpKind.Member && last.Oid == op.Oid && last.Text == op.Text)
			{
				p.Out[^1] = op;
				Coalesced++;
				return;
			}
		}
		p.Out.Add(op);
		p.Ops++;
	}

	public void OnRelease(uint oid)
	{
		foreach (var (_, state) in _peers)
		{
			state.PendingRoots.Remove(oid);
			state.Roots.Remove(oid);
			foreach (var key in state.Held.Keys.Where(k => k.Oid == oid).ToList())
			{
				state.Held.Remove(key);
			}
			if (state.Has.Remove(oid))
			{
				state.Out.Add(ObservableOp.Release(oid));
				state.Releases++;
			}
		}
		_holders.Remove(oid);
	}

	public void Forget(TPeer peer)
	{
		if (!_peers.Remove(peer, out PeerState? state))
		{
			return;
		}
		foreach (uint oid in state.Has)
		{
			if (_holders.TryGetValue(oid, out var holders) && holders > 1)
			{
				_holders[oid] = holders - 1;
			}
			else
			{
				_holders.Remove(oid);
			}
		}
	}

	public List<(TPeer Peer, List<ObservableOp> Ops)> TakeOutput()
	{
		var output = new List<(TPeer, List<ObservableOp>)>();
		long now = 0L;
		foreach (var (peer, state) in _peers)
		{
			if (state.Held.Count > 0)
			{
				if (now == 0L)
				{
					now = _clock();
				}
				foreach (var (key, op) in state.Held.ToList())
				{
					if (!state.LastSent.TryGetValue(key, out var lastSent) || now - lastSent >= 50)
					{
						state.Held.Remove(key);
						state.LastSent[key] = now;
						Append(state, op);
					}
				}
			}
			if (state.Out.Count != 0)
			{
				output.Add((peer, state.Out));
				state.Out = new List<ObservableOp>();
			}
		}
		return output;
	}

	public string Describe()
	{
		return $"{_holders.Count} observables held by {_peers.Count} joiner(s) (" + string.Join(", ", _peers.Select(kv => $"{kv.Key}: {kv.Value.Has.Count} held, {kv.Value.Roots.Count} roots, {kv.Value.PendingRoots.Count} pending, {kv.Value.Snapshots} snapshots / {kv.Value.Ops} changes / {kv.Value.Releases} releases sent, {kv.Value.Deferred} changes rate-capped")) + $"), roots added {RootsAdded}, expired {RootsExpired}, snapshots unavailable {SnapshotsMissing}, coalesced {Coalesced}";
	}
}
