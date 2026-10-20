using System;
using System.Collections.Generic;
using Y51_SE2Online.Net;

namespace Y51_SE2Online.Sync;

public sealed class ClientStateTable
{
	private sealed class Entry
	{
		public byte[] Bytes = Array.Empty<byte>();

		public uint Version;

		public bool Dirty;

		public long UpdatedMs;

		public uint HostMs;
	}

	private readonly object _sync = new object();

	private readonly Dictionary<(uint, ushort), Entry> _entries = new Dictionary<(uint, ushort), Entry>();

	private readonly HashSet<(uint, ushort)> _dirty = new HashSet<(uint, ushort)>();

	private readonly Dictionary<uint, long> _tombstones = new Dictionary<uint, long>();

	private readonly Dictionary<uint, List<ushort>> _keysOf = new Dictionary<uint, List<ushort>>();

	private long _nextExpireMs;

	public long Received;

	public long Stale;

	public long Tombstoned;

	public long Applied;

	public long Expired;

	public long Overflow;

	public int PendingTtlMs { get; set; } = 30000;

	public int TombstoneTtlMs { get; set; } = 120000;

	public int MaxEntries { get; set; } = 500000;

	public int DirtyCount
	{
		get
		{
			lock (_sync)
			{
				return _dirty.Count;
			}
		}
	}

	public void Put(in StateEntry e, long nowMs)
	{
		lock (_sync)
		{
			Received++;
			if (_tombstones.ContainsKey(e.NetId))
			{
				Tombstoned++;
				return;
			}
			(uint, ushort) tuple = (e.NetId, e.Key);
			if (_entries.TryGetValue(tuple, out Entry value))
			{
				if (!Seq.Newer(e.Version, value.Version) && value.Version != 0)
				{
					Stale++;
					return;
				}
			}
			else
			{
				if (_entries.Count >= MaxEntries)
				{
					Overflow++;
					return;
				}
				value = new Entry();
				_entries[tuple] = value;
				if (!_keysOf.TryGetValue(e.NetId, out var keys)) _keysOf[e.NetId] = keys = new List<ushort>(4);
				keys.Add(e.Key);
			}
			value.Bytes = e.Bytes;
			value.Version = e.Version;
			value.UpdatedMs = nowMs;
			value.HostMs = e.HostMs;
			value.Dirty = true;
			_dirty.Add(tuple);
		}
	}

	public bool IsTombstoned(uint netId)
	{
		lock (_sync)
		{
			return _tombstones.ContainsKey(netId);
		}
	}

	/// <summary>A despawned id the host spawned again (an entity taken out of the scene and put back keeps its id):
	/// without this its state was dropped as stale for the whole tombstone time and the grid froze for up to two minutes.</summary>
	public void Revive(uint netId)
	{
		lock (_sync)
		{
			_tombstones.Remove(netId);
		}
	}

	public void Despawn(uint netId, long nowMs)
	{
		lock (_sync)
		{
			_tombstones[netId] = nowMs;
			if (!_keysOf.Remove(netId, out var keys)) return;
			foreach (ushort key in keys)
			{
				_entries.Remove((netId, key));
				_dirty.Remove((netId, key));
			}
		}
	}

	public void Drain(Func<uint, bool> entityKnown, Func<ushort, bool?> keyUsable, List<StateEntry> output, long nowMs)
	{
		lock (_sync)
		{
			if (_dirty.Count == 0)
			{
				ExpireTombstones(nowMs);
				return;
			}
			List<(uint, ushort)> list = new List<(uint, ushort)>();
			foreach (var item in _dirty)
			{
				Entry entry = _entries[item];
				bool? flag = keyUsable(item.Item2);
				if (flag == false)
				{
					list.Add(item);
					entry.Dirty = false;
				}
				else if (flag == true && entityKnown(item.Item1))
				{
					output.Add(new StateEntry(item.Item1, item.Item2, entry.Version, entry.Bytes, entry.HostMs));
					entry.Dirty = false;
					list.Add(item);
					Applied++;
				}
				else if (nowMs - entry.UpdatedMs > PendingTtlMs)
				{
					entry.Dirty = false;
					list.Add(item);
					Expired++;
				}
			}
			foreach (var expired in list)
			{
				_dirty.Remove(expired);
			}
			ExpireTombstones(nowMs);
		}
	}

	private void ExpireTombstones(long nowMs)
	{
		if (_tombstones.Count == 0 || nowMs < _nextExpireMs)
		{
			return;
		}
		_nextExpireMs = nowMs + 1000;
		List<uint> list = null;
		foreach (KeyValuePair<uint, long> tombstone in _tombstones)
		{
			if (nowMs - tombstone.Value > TombstoneTtlMs)
			{
				(list ?? (list = new List<uint>())).Add(tombstone.Key);
			}
		}
		if (list == null)
		{
			return;
		}
		foreach (uint item in list)
		{
			_tombstones.Remove(item);
		}
	}

	public void Touch(uint netId)
	{
		lock (_sync)
		{
			if (!_keysOf.TryGetValue(netId, out var keys)) return;
			foreach (ushort key in keys)
			{
				_entries[(netId, key)].Dirty = true;
				_dirty.Add((netId, key));
			}
		}
	}
}
