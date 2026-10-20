using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Y51_SE2Online.Net;

namespace Y51_SE2Online.Sync;

/// <summary>Per-player send rate of a root entity's state, from its distance (and how crowded the player's view is).</summary>
public static class StateBands
{
	/// <summary>The player's own character and the grid it sits in: full rate, sent before everything else.</summary>
	public const byte Own = 0;
	public const byte Full = 1;
	public const byte Half = 2;
	public const byte Quarter = 3;
	public const byte Far = 4;

	/// <summary>Minimum time between two sends of one root, by band (60 / 30 / 15 / 5 Hz, on the 900/Hz scale of the key caps).</summary>
	public static readonly int[] IntervalMs = { 0, 0, 30, 60, 180 };

	/// <summary>Added to a root's due time when the budget is short: own first, far last; waiting counts up to <see cref="StateFanout.MaxAgeMs"/>, so far still goes out.</summary>
	public static readonly int[] PenaltyMs = { -5000, 0, 40, 100, 300 };

	public const double FullMeters = 150;
	public const double HalfMeters = 600;
	public const double QuarterMeters = 2000;

	/// <summary>Characters and loose objects past this many nearer ones drop to <see cref="Half"/> even when close (crowds).</summary>
	public const int CrowdFull = 32;

	public static byte ForDistance(double meters) => meters < FullMeters ? Full : meters < HalfMeters ? Half : meters < QuarterMeters ? Quarter : Far;
}

/// <summary>
/// One player's bands: the roots near it listed one by one, every other root of a shared set (built once for all
/// players) <see cref="StateBands.Far"/>, anything else unlisted. Building it costs what is near the player, not the world.
/// </summary>
public sealed class BandMap : IReadOnlyDictionary<uint, byte>
{
	private readonly Dictionary<uint, byte> _near;
	private readonly IReadOnlySet<uint> _far;

	public BandMap(Dictionary<uint, byte> near, IReadOnlySet<uint> far)
	{
		_near = near;
		_far = far;
	}

	public bool TryGetValue(uint key, out byte value)
	{
		if (_near.TryGetValue(key, out value)) return true;
		value = StateBands.Far;
		return _far.Contains(key);
	}

	public byte this[uint key] => TryGetValue(key, out byte v) ? v : throw new KeyNotFoundException();

	public bool ContainsKey(uint key) => _near.ContainsKey(key) || _far.Contains(key);

	/// <summary>Roots below full rate are not counted one by one; this is the near list plus the shared set.</summary>
	public int Count => _near.Count + _far.Count;

	public IEnumerable<uint> Keys { get { foreach (var kv in this) yield return kv.Key; } }

	public IEnumerable<byte> Values { get { foreach (var kv in this) yield return kv.Value; } }

	public IEnumerator<KeyValuePair<uint, byte>> GetEnumerator()
	{
		foreach (var kv in _near) yield return kv;
		foreach (uint id in _far)
			if (!_near.ContainsKey(id)) yield return new KeyValuePair<uint, byte>(id, StateBands.Far);
	}

	System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// Host state fan-out. One shared table holds the newest encoded value per (NetId, key), grouped by root entity; the
/// per-key rate caps apply there once for everyone, and a value that did not change goes out again at most once a
/// second. Each client only keeps, per root, what it was last sent and when that root is due again (its band sets the
/// interval), and spends a byte budget in priority order (own character and seat grid, then near, then far), so an
/// overload delays far objects instead of everything. Net thread only; <see cref="Client.Bands"/> and
/// <see cref="Client.StreamedOut"/> are replaced whole from other threads.
/// </summary>
public sealed class StateFanout
{
	public const int RefreshMs = 1000;

	public const int MaxAgeMs = 1000;

	private sealed class Slot
	{
		public uint NetId;
		public ushort Key;
		public uint Version;
		public byte[] Wire = Array.Empty<byte>();
		public long Seq;
		public long BumpMs = long.MinValue / 2;
		public int CapMs;
		public bool Held, Dead;
		public Root? Owner;
		public LinkedListNode<Slot>? Node;
	}

	private sealed class Root
	{
		public uint Id;
		public int Index = -1;
		public int Gen;
		public long Seq;
		public long ListedTick = -1;
		/// <summary>Oldest send first: a client's unsent values are a tail of this list, so a big grid with one changed
		/// block costs that block, not all of its values, per client and emit.</summary>
		public readonly LinkedList<Slot> Slots = new();
	}

	public sealed class Client
	{
		public volatile HashSet<uint>? StreamedOut;
		/// <summary>Root NetId to band; roots not listed are <see cref="StateBands.Full"/>.</summary>
		public volatile IReadOnlyDictionary<uint, byte>? Bands;
		private readonly int _budgetBytesPerSec;
		private double _tokens;
		private long _lastEmitMs = -1;
		internal long[] SentSeq = Array.Empty<long>(), DueMs = Array.Empty<long>();
		internal int[] Gen = Array.Empty<int>(), BandEpochAt = Array.Empty<int>();
		internal byte[] Band = Array.Empty<byte>();
		internal bool[] IsPending = Array.Empty<bool>();
		internal readonly List<int> Pending = new();
		internal IReadOnlyDictionary<uint, byte>? BandsSeen;
		internal int BandEpoch = 1;
		public long Sent, SentBytes, Messages, DeferredByBudget, DeferredByRate;
		public long[] SentByKey = new long[64];

		public Client(int budgetBytesPerSec) => _budgetBytesPerSec = budgetBytesPerSec;

		public int PendingCount => Pending.Count;

		internal void Grow(int n)
		{
			if (n <= SentSeq.Length) return;
			int size = Math.Max(n, Math.Max(64, SentSeq.Length * 2));
			Array.Resize(ref SentSeq, size);
			Array.Resize(ref DueMs, size);
			Array.Resize(ref Gen, size);
			Array.Resize(ref BandEpochAt, size);
			Array.Resize(ref Band, size);
			Array.Resize(ref IsPending, size);
		}

		internal double Refill(long now, int maxMessageBytes)
		{
			double elapsedSec = _lastEmitMs < 0 ? 0.05 : Math.Max(0.0, (now - _lastEmitMs) / 1000.0);
			_lastEmitMs = now;
			return _tokens = Math.Min(_tokens + elapsedSec * _budgetBytesPerSec, _budgetBytesPerSec / 4.0 + maxMessageBytes);
		}

		internal void Spend(int bytes) => _tokens -= bytes;
	}

	private readonly Dictionary<(uint, ushort), Slot> _slots = new();
	private readonly Dictionary<uint, List<Slot>> _byNet = new();
	private readonly Dictionary<uint, Root> _rootById = new();
	private readonly List<Root?> _roots = new();
	private readonly Stack<int> _freeIndices = new();
	private readonly List<Root> _changed = new();
	private readonly List<Slot> _held = new();
	private readonly Func<ushort, int> _intervalForKey;
	private readonly List<(long Key, int Index, int Cost)> _due = new();
	private readonly byte[] _buffer = new byte[StateBatchMessage.HeaderBytes + StateEntry.Overhead + ushort.MaxValue];
	private long _seq, _tick;
	private int _gen;

	public long Puts, Unchanged, Stale, DeferredByCap, DroppedByCap;

	public int EntryCount => _slots.Count;

	public int RootCount => _rootById.Count;

	public StateFanout(Func<ushort, int> intervalForKey) => _intervalForKey = intervalForKey;

	public void Put(in StateEntry e, long nowMs)
	{
		Puts++;
		if (e.NetId == StateCoalescer.WatchNetId && StateCoalescer.WatchLog.Count < 20000) StateCoalescer.WatchLog.Enqueue($"put,{nowMs},{e.Key},{e.HostMs}");
		if (!_slots.TryGetValue((e.NetId, e.Key), out var slot))
		{
			slot = new Slot { NetId = e.NetId, Key = e.Key, CapMs = _intervalForKey(e.Key) };
			_slots[(e.NetId, e.Key)] = slot;
			if (!_byNet.TryGetValue(e.NetId, out var list)) _byNet[e.NetId] = list = new List<Slot>(4);
			list.Add(slot);
		}
		if (slot.CapMs < 0)
		{
			DroppedByCap++;
			return;
		}
		if (slot.Wire.Length > 0 && Seq.Newer(slot.Version, e.Version))
		{
			Stale++;
			return;
		}
		uint rootId = e.Root != 0 ? e.Root : e.NetId;
		bool moved = slot.Owner == null || slot.Owner.Id != rootId;
		if (moved) MoveTo(slot, rootId);
		bool same = slot.Wire.Length == StateEntry.Overhead + e.Bytes.Length && slot.Wire.AsSpan(StateEntry.Overhead).SequenceEqual(e.Bytes);
		// A slot that joined another root is news to that root's clients even when its value did not change.
		if (same && !moved && nowMs - slot.BumpMs < RefreshMs)
		{
			Unchanged++;
			return;
		}
		slot.Wire = Encode(in e);
		slot.Version = e.Version;
		if (nowMs - slot.BumpMs < slot.CapMs)
		{
			DeferredByCap++;
			if (!slot.Held)
			{
				slot.Held = true;
				_held.Add(slot);
			}
			return;
		}
		Bump(slot, nowMs);
	}

	public void Remove(uint netId)
	{
		if (!_byNet.Remove(netId, out var list)) return;
		foreach (var slot in list)
		{
			_slots.Remove((slot.NetId, slot.Key));
			slot.Dead = true;
			Detach(slot);
		}
	}

	/// <summary>The host removed that Data: its last value must never go out again (a player who joined later got it back
	/// and showed e.g. dampeners on while the host had them off).</summary>
	public void Forget(uint netId, ushort key)
	{
		if (!_slots.Remove((netId, key), out var slot)) return;
		slot.Dead = true;
		if (_byNet.TryGetValue(netId, out var list) && list.Remove(slot) && list.Count == 0) _byNet.Remove(netId);
		Detach(slot);
	}

	/// <summary>Values held back by their key's rate cap go out once the cap allows.</summary>
	public void Flush(long nowMs)
	{
		int kept = 0;
		for (int i = 0; i < _held.Count; i++)
		{
			var slot = _held[i];
			if (slot.Dead || !slot.Held) continue;
			if (nowMs - slot.BumpMs >= slot.CapMs) Bump(slot, nowMs);
			else _held[kept++] = slot;
		}
		_held.RemoveRange(kept, _held.Count - kept);
	}

	/// <summary>Marks the roots that changed since the last call as pending for each of these clients.</summary>
	public void Distribute(IReadOnlyList<Client> clients)
	{
		foreach (var client in clients) client.Grow(_roots.Count);
		foreach (var root in _changed)
		{
			if (root.Index < 0) continue;
			int i = root.Index;
			foreach (var c in clients)
			{
				if (c.Gen[i] != root.Gen) Reset(c, i, root.Gen);
				var streamedOut = c.StreamedOut;
				if (streamedOut != null && streamedOut.Count > 0 && streamedOut.Contains(root.Id)) continue;
				if (!c.IsPending[i])
				{
					c.IsPending[i] = true;
					c.Pending.Add(i);
				}
			}
		}
		_changed.Clear();
		_tick++;
	}

	/// <summary>The client's due roots, highest priority first, packed into state batches while its budget lasts.</summary>
	public void Emit(Client c, long nowMs, int maxMessageBytes, List<byte[]> output)
	{
		double tokens = c.Refill(nowMs, maxMessageBytes);
		if (c.Pending.Count == 0) return;
		var bands = c.Bands;
		if (!ReferenceEquals(bands, c.BandsSeen))
		{
			c.BandsSeen = bands;
			c.BandEpoch++;
		}
		var streamedOut = c.StreamedOut;
		bool anyOut = streamedOut != null && streamedOut.Count > 0;
		_due.Clear();
		long total = 0;
		int kept = 0;
		for (int n = 0; n < c.Pending.Count; n++)
		{
			int i = c.Pending[n];
			var root = i < _roots.Count ? _roots[i] : null;
			if (root == null || c.Gen[i] != root.Gen || (anyOut && streamedOut!.Contains(root.Id)))
			{
				c.IsPending[i] = false;
				continue;
			}
			c.Pending[kept++] = i;
			if (nowMs < c.DueMs[i])
			{
				c.DeferredByRate++;
				continue;
			}
			int cost = 0;
			long sentSeq = c.SentSeq[i];
			for (var node = root.Slots.Last; node != null && node.Value.Seq > sentSeq; node = node.Previous) cost += node.Value.Wire.Length;
			if (cost == 0)
			{
				c.IsPending[i] = false;
				kept--;
				continue;
			}
			_due.Add((Math.Max(c.DueMs[i], nowMs - MaxAgeMs) + StateBands.PenaltyMs[BandOf(c, i, root, bands)], i, cost));
			total += cost;
		}
		c.Pending.RemoveRange(kept, c.Pending.Count - kept);
		if (_due.Count == 0) return;
		if (total > tokens) _due.Sort((a, b) => a.Key.CompareTo(b.Key));
		int pos = StateBatchMessage.HeaderBytes, count = 0;
		for (int d = 0; d < _due.Count; d++)
		{
			var (_, i, cost) = _due[d];
			if (tokens < cost)
			{
				c.DeferredByBudget += _due.Count - d;
				break;
			}
			var root = _roots[i]!;
			long sent = c.SentSeq[i];
			var first = root.Slots.Last;
			while (first?.Previous != null && first.Previous.Value.Seq > sent) first = first.Previous;
			for (var node = first; node != null; node = node.Next)
			{
				var slot = node.Value;
				if (slot.Seq <= sent) continue;
				if (pos + slot.Wire.Length > maxMessageBytes && pos > StateBatchMessage.HeaderBytes)
				{
					output.Add(Pack(pos, count));
					c.Messages++;
					pos = StateBatchMessage.HeaderBytes;
					count = 0;
				}
				slot.Wire.CopyTo(_buffer, pos);
				pos += slot.Wire.Length;
				count++;
				c.Sent++;
				if (slot.Key >= c.SentByKey.Length) Array.Resize(ref c.SentByKey, Math.Max(slot.Key + 1, c.SentByKey.Length * 2));
				c.SentByKey[slot.Key]++;
				if (slot.NetId == StateCoalescer.WatchNetId && StateCoalescer.WatchLog.Count < 20000) StateCoalescer.WatchLog.Enqueue($"sent,{nowMs},{slot.Key},{slot.Version}");
			}
			tokens -= cost;
			c.Spend(cost);
			c.SentBytes += cost;
			c.SentSeq[i] = root.Seq;
			c.DueMs[i] = nowMs + StateBands.IntervalMs[c.Band[i]];
			c.IsPending[i] = false;
		}
		if (count > 0)
		{
			output.Add(Pack(pos, count));
			c.Messages++;
		}
		kept = 0;
		for (int n = 0; n < c.Pending.Count; n++)
		{
			if (c.IsPending[c.Pending[n]]) c.Pending[kept++] = c.Pending[n];
		}
		c.Pending.RemoveRange(kept, c.Pending.Count - kept);
	}

	private static byte BandOf(Client c, int i, Root root, IReadOnlyDictionary<uint, byte>? bands)
	{
		if (c.BandEpochAt[i] != c.BandEpoch)
		{
			c.BandEpochAt[i] = c.BandEpoch;
			c.Band[i] = bands != null && bands.TryGetValue(root.Id, out byte band) && band <= StateBands.Far ? band : StateBands.Full;
		}
		return c.Band[i];
	}

	private static void Reset(Client c, int i, int gen)
	{
		c.Gen[i] = gen;
		c.SentSeq[i] = 0;
		c.DueMs[i] = long.MinValue / 2;
		c.BandEpochAt[i] = 0;
	}

	private byte[] Pack(int length, int count)
	{
		var msg = new byte[length];
		_buffer.AsSpan(0, length).CopyTo(msg);
		msg[0] = (byte)SyncMsg.StateBatch;
		BinaryPrimitives.WriteUInt16LittleEndian(msg.AsSpan(1), (ushort)count);
		return msg;
	}

	private static byte[] Encode(in StateEntry e) =>
		new WireWriter(e.WireSize).U32(e.NetId).U16(e.Key).U32(e.Version).U32(e.HostMs).U16((ushort)e.Bytes.Length).Bytes(e.Bytes).ToArray();

	private void Bump(Slot slot, long nowMs)
	{
		slot.Seq = ++_seq;
		slot.BumpMs = nowMs;
		slot.Held = false;
		var root = slot.Owner!;
		root.Seq = slot.Seq;
		if (slot.Node != null && slot.Node != root.Slots.Last)
		{
			root.Slots.Remove(slot.Node);
			root.Slots.AddLast(slot.Node);
		}
		if (root.ListedTick != _tick)
		{
			root.ListedTick = _tick;
			_changed.Add(root);
		}
	}

	private void MoveTo(Slot slot, uint rootId)
	{
		Detach(slot);
		if (!_rootById.TryGetValue(rootId, out var root))
		{
			root = new Root { Id = rootId, Gen = ++_gen };
			if (_freeIndices.Count > 0)
			{
				root.Index = _freeIndices.Pop();
				_roots[root.Index] = root;
			}
			else
			{
				root.Index = _roots.Count;
				_roots.Add(root);
			}
			_rootById[rootId] = root;
		}
		slot.Owner = root;
		// In send order: a slot moving over keeps its old Seq until its next bump.
		var after = root.Slots.Last;
		while (after != null && after.Value.Seq > slot.Seq) after = after.Previous;
		slot.Node = after == null ? root.Slots.AddFirst(slot) : root.Slots.AddAfter(after, slot);
		root.Seq = Math.Max(root.Seq, slot.Seq);
	}

	private void Detach(Slot slot)
	{
		var root = slot.Owner;
		if (root == null) return;
		slot.Owner = null;
		if (slot.Node != null) root.Slots.Remove(slot.Node);
		slot.Node = null;
		if (root.Slots.Count > 0) return;
		_rootById.Remove(root.Id);
		_roots[root.Index] = null;
		_freeIndices.Push(root.Index);
		root.Index = -1;
	}
}
