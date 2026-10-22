using System;
using System.Collections.Generic;

namespace Y51_SE2Online.Sync;

/// <summary>
/// Changes received for remote entities, held per entity in arrival order until that entity's played-back motion
/// reaches the server time the change was sent at. A change is stamped when the server's motion samples of its frame
/// arrive (<see cref="Stamp"/>), and released by <see cref="Release"/> once the entity's playout cursor reaches the stamp,
/// or after <see cref="MaxHoldMs"/> whatever the cursor says, so nothing waits on a stream that stopped.
/// </summary>
public sealed class PlayoutQueue<TKey, T> where TKey : notnull
{
	public const long MaxHoldMs = 1000;

	private const long Unstamped = long.MinValue;

	private sealed class Entry(T value, long receivedMs)
	{
		public readonly T Value = value;
		public readonly long ReceivedMs = receivedMs;
		public long Stamp = Unstamped;
	}

	private readonly Dictionary<TKey, Queue<Entry>> _byKey = new();
	private readonly List<Entry> _unstamped = new();

	public int Count { get; private set; }

	public IEnumerable<TKey> Keys => _byKey.Keys;

	/// <summary>Whether something of this entity is still held: a newer change must queue behind it.</summary>
	public bool Holds(TKey key) => _byKey.ContainsKey(key);

	public void Add(TKey key, T value, long nowMs)
	{
		if (!_byKey.TryGetValue(key, out var queue)) _byKey[key] = queue = new Queue<Entry>();
		var entry = new Entry(value, nowMs);
		queue.Enqueue(entry);
		_unstamped.Add(entry);
		Count++;
	}

	/// <summary>Everything received since the last stamp belongs to the server frame at <paramref name="serverTime"/>.</summary>
	public void Stamp(long serverTime)
	{
		foreach (var entry in _unstamped) entry.Stamp = serverTime;
		_unstamped.Clear();
	}

	/// <summary>Hands this entity's due changes and their stamps to <paramref name="apply"/>, oldest first, stopping at the
	/// first one not due.</summary>
	public int Release(TKey key, long cursor, long nowMs, Action<T, long> apply)
	{
		if (!_byKey.TryGetValue(key, out var queue)) return 0;
		int released = 0;
		while (queue.Count > 0)
		{
			var head = queue.Peek();
			bool due = head.Stamp != Unstamped && head.Stamp <= cursor || nowMs - head.ReceivedMs >= MaxHoldMs;
			if (!due) break;
			queue.Dequeue();
			_unstamped.Remove(head);
			Count--;
			released++;
			apply(head.Value, head.Stamp);
		}
		if (queue.Count == 0) _byKey.Remove(key);
		return released;
	}

	/// <summary>Hands everything held for this entity to <paramref name="apply"/> at once (its playback ended).</summary>
	public int Flush(TKey key, Action<T, long> apply) => Release(key, long.MaxValue, long.MaxValue, apply);

	/// <summary>Forgets an entity that is gone; its changes have nothing left to animate.</summary>
	public void Drop(TKey key)
	{
		if (!_byKey.Remove(key, out var queue)) return;
		foreach (var entry in queue) _unstamped.Remove(entry);
		Count -= queue.Count;
	}

	public void Clear()
	{
		_byKey.Clear();
		_unstamped.Clear();
		Count = 0;
	}
}
