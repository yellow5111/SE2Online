using System;
using System.Collections.Generic;

namespace Y51_SE2Online.Engine;

/// <summary>
/// One peer's outbound entity data for the engine transport. The game re-sends many data types every server frame
/// whether they changed or not (wind, thrust limits, physics state): the newest value per entity and type within a
/// dispatch is all that goes, a value equal to the last one this peer got is not sent again, and types the
/// <see cref="Rates"/> list as cosmetic go at most at their rate with the newest value always delivered in the end.
/// Removals keep their place: a value set after a removal in the same frame is sent after it.
/// </summary>
internal sealed class DataLane
{
	public enum Kind : byte { Set, Remove, SampledSet, SampledRemove, Dropped }

	public struct Op
	{
		public Kind Kind;
		public uint NetId;
		public ushort Key;
		public byte[]? Bytes;
	}

	private readonly List<Op> _ops = new();
	private readonly Dictionary<(uint, ushort), int> _pendingSet = new();
	private readonly Dictionary<(uint, ushort), byte[]> _lastSent = new();
	private readonly Dictionary<(uint, ushort), (byte[] Bytes, bool Sampled, long DueMs)> _deferred = new();
	private readonly Dictionary<(uint, ushort), long> _nextAllowed = new();
	private readonly Dictionary<(uint, ushort), int> _rateOf = new();
	// The keys each entity has had a value of, so forgetting one entity touches only its own entries.
	private readonly Dictionary<uint, HashSet<ushort>> _keysOf = new();

	/// <summary>Minimum milliseconds between values of one entity for a type key; absent = every dispatch.</summary>
	public readonly Dictionary<ushort, int> Rates = new();

	public long Coalesced, Unchanged, Deferred;

	/// <summary>Test rig (-y51online:lane:raw): every value goes as the game hands it over, for before/after measurements.</summary>
	public bool Raw;

	/// <param name="rateMs">Minimum milliseconds since this entity's last value of this type, overriding <see cref="Rates"/>
	/// (0: every dispatch); -1 uses <see cref="Rates"/>.</param>
	public void Set(uint netId, ushort key, byte[] bytes, bool sampled, long now, int rateMs = -1, bool exact = false)
	{
		var id = (netId, key);
		if (Raw || exact)
		{
			_ops.Add(new Op { Kind = sampled ? Kind.SampledSet : Kind.Set, NetId = netId, Key = key, Bytes = bytes });
			return;
		}
		if (!_keysOf.TryGetValue(netId, out var keys)) _keysOf[netId] = keys = new HashSet<ushort>();
		keys.Add(key);
		int rate = rateMs >= 0 ? rateMs : Rates.GetValueOrDefault(key);
		if (rate > 0) _rateOf[id] = rate;
		else _rateOf.Remove(id);
		if (rate > 0 && _nextAllowed.TryGetValue(id, out long allowed) && now < allowed)
		{
			if (_deferred.ContainsKey(id)) Coalesced++; else Deferred++;
			_deferred[id] = (bytes, sampled, allowed);
			return;
		}
		// An older value still waiting must not follow this one.
		_deferred.Remove(id);
		Enqueue(id, bytes, sampled);
	}

	private void Enqueue((uint NetId, ushort Key) id, byte[] bytes, bool sampled)
	{
		if (_pendingSet.TryGetValue(id, out int at) && _ops[at].Kind == (sampled ? Kind.SampledSet : Kind.Set))
		{
			var op = _ops[at];
			op.Bytes = bytes;
			_ops[at] = op;
			Coalesced++;
			return;
		}
		_pendingSet[id] = _ops.Count;
		_ops.Add(new Op { Kind = sampled ? Kind.SampledSet : Kind.Set, NetId = id.NetId, Key = id.Key, Bytes = bytes });
	}

	public void Remove(uint netId, ushort key, bool sampled)
	{
		var id = (netId, key);
		// A value set earlier in this dispatch and removed again never needs to go.
		if (_pendingSet.Remove(id, out int at))
		{
			var dropped = _ops[at];
			dropped.Kind = Kind.Dropped;
			_ops[at] = dropped;
		}
		_deferred.Remove(id);
		_ops.Add(new Op { Kind = sampled ? Kind.SampledRemove : Kind.Remove, NetId = netId, Key = key });
	}

	/// <summary>This dispatch's operations in order, after due deferred values, skipping values the peer already has.</summary>
	public void Drain(long now, List<Op> output)
	{
		if (_deferred.Count > 0)
		{
			List<(uint, ushort)>? due = null;
			foreach (var (id, value) in _deferred)
				if (now >= value.DueMs) (due ??= new()).Add(id);
			if (due != null)
				foreach (var id in due)
				{
					var value = _deferred[id];
					_deferred.Remove(id);
					Enqueue(id, value.Bytes, value.Sampled);
				}
		}
		foreach (var op in _ops)
		{
			if (op.Kind == Kind.Dropped) continue;
			if (Raw) { output.Add(op); continue; }
			var id = (op.NetId, op.Key);
			if (op.Kind is Kind.Set or Kind.SampledSet)
			{
				if (_lastSent.TryGetValue(id, out var last) && last.AsSpan().SequenceEqual(op.Bytes)) { Unchanged++; continue; }
				_lastSent[id] = op.Bytes!;
				if (_rateOf.TryGetValue(id, out int rate)) _nextAllowed[id] = now + rate;
				else _nextAllowed.Remove(id);
			}
			else
			{
				_lastSent.Remove(id);
				_nextAllowed.Remove(id);
			}
			output.Add(op);
		}
		_ops.Clear();
		_pendingSet.Clear();
	}

	/// <summary>An entity this peer no longer has: forget what it was sent.</summary>
	public void Forget(uint netId)
	{
		if (!_keysOf.Remove(netId, out var keys)) return;
		foreach (ushort key in keys)
		{
			var id = (netId, key);
			_lastSent.Remove(id);
			_nextAllowed.Remove(id);
			_deferred.Remove(id);
			_rateOf.Remove(id);
		}
	}
}
