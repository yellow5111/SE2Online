using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;

namespace Y51_SE2Online.Sync;

/// <summary>
/// Host side, per entity: S2C signals whose client handlers keep only the newest value. Every character sent three of
/// them every frame to every client, parked offline characters included (64 of them made 593 KiB/s for one client).
/// A value equal to the one last sent is dropped (forwarded again after <see cref="RepeatMs"/> so a client that missed
/// one heals); a changed value goes out at most once per <see cref="Policy.CapMs"/>, the newest one winning.
/// </summary>
public sealed class SignalCoalescer
{
	public const int RepeatMs = 2000;

	/// <param name="CapMs">Minimum spacing of changed values; 0 sends every change at once (edge-triggered handlers).</param>
	/// <param name="Echo">Forward the first unchanged value after a change too (the handler compares with the previous value).</param>
	/// <param name="LiveRepeat">When true for a payload, unchanged values are forwarded every <paramref name="CapMs"/>
	/// (or <see cref="LiveRepeatMs"/>) instead of every <see cref="RepeatMs"/>.</param>
	/// <param name="KeyOffsets">The payload is AnimationBlackboardComponent's four (key, value) spans, merged per key; this
	/// is where in each 8-byte element the 4-byte key sits (0 or 4: ValueTuple's auto layout puts a float before it).</param>
	public sealed record Policy(string Name, int CapMs, bool Echo = false, Func<byte[], bool>? LiveRepeat = null, int[]? KeyOffsets = null)
	{
		public bool Blackboard => KeyOffsets != null;
	}

	public const int LiveRepeatMs = 100;

	/// <summary>Blackboard spans: floats, ints, strings, bools. Every element is an 8-byte (StringHash, value) tuple.</summary>
	public const int Spans = 4;

	public const int ElementBytes = 8;

	// Value bytes per span (float, int, StringHash, bool); the rest of the value half of a bool element is padding.
	private static readonly int[] ValueBytes = { 4, 4, 4, 1 };

	private sealed class Entry
	{
		public required Policy Policy;
		public ushort Key;
		public uint Target;
		public WireCommand? Sent;
		public long SentMs = long.MinValue / 2;
		public WireCommand? Pending;
		public bool Echoed;
		public bool Queued;
		// Blackboard: key -> element last sent / waiting, per span.
		public Dictionary<uint, ulong>[]? SentValues;
		public Dictionary<uint, ulong>[]? PendingValues;
		public WireValue? EntityRef;
		public long RepeatedMs = long.MinValue / 2;
	}

	private readonly Dictionary<ushort, Policy?> _policies = new();
	private readonly Dictionary<(ushort Key, uint Target), Entry> _entries = new();
	private readonly Dictionary<uint, List<ushort>> _keysOf = new();
	private readonly List<Entry> _queued = new();
	private readonly Dictionary<string, (long In, long Out)> _byName = new();

	public long Offered, Forwarded, Coalesced;

	public int Tracked => _entries.Count;

	/// <summary>A null policy records that the key was looked at and is sent as it is.</summary>
	public void Register(ushort key, Policy? policy) => _policies[key] = policy;

	public bool Registered(ushort key) => _policies.ContainsKey(key);

	/// <summary>False when <paramref name="c"/> is not coalesced and the caller sends it as usual. Otherwise whatever
	/// is due now (possibly nothing) is appended to <paramref name="output"/>.</summary>
	public bool Offer(WireCommand c, long now, List<WireCommand> output)
	{
		if (!_policies.TryGetValue(c.Key, out var policy) || policy == null || c.Refs.Count == 0 || c.Refs[0].Kind != WireKind.Entity)
		{
			return false;
		}
		uint target = c.Refs[0].Id;
		if (!_entries.TryGetValue((c.Key, target), out var e))
		{
			_entries[(c.Key, target)] = e = new Entry { Policy = policy, Key = c.Key, Target = target };
			if (!_keysOf.TryGetValue(target, out var keys)) _keysOf[target] = keys = new List<ushort>(2);
			keys.Add(c.Key);
		}
		Offered++;
		_byName[policy.Name] = (_byName.GetValueOrDefault(policy.Name).In + 1, _byName.GetValueOrDefault(policy.Name).Out);
		if (policy.Blackboard)
		{
			if (!OfferBlackboard(e, c, now, output))
			{
				// Unparseable: send it as it is and forget what this client-side blackboard holds.
				e.SentValues = null;
				e.PendingValues = null;
				return false;
			}
			return true;
		}
		if (e.Sent != null && Same(c, e.Sent))
		{
			int repeat = policy.LiveRepeat?.Invoke(c.Payload) == true ? Math.Max(policy.CapMs, LiveRepeatMs) : RepeatMs;
			if (policy.Echo && !e.Echoed)
			{
				e.Echoed = true;
			}
			else if (now - e.SentMs < repeat)
			{
				// Back to what the client already has: whatever was waiting is moot.
				if (e.Pending != null) Coalesced++;
				e.Pending = null;
				Coalesced++;
				return true;
			}
		}
		else
		{
			e.Echoed = false;
		}
		if (e.Pending != null) Coalesced++;
		if (now - e.SentMs >= policy.CapMs)
		{
			Send(e, c, now, output);
		}
		else
		{
			e.Pending = c;
			Queue(e);
		}
		return true;
	}

	/// <summary>Appends every waiting value whose cap has run out.</summary>
	public void Flush(long now, List<WireCommand> output)
	{
		if (_queued.Count == 0) return;
		for (int i = _queued.Count - 1; i >= 0; i--)
		{
			var e = _queued[i];
			if (e.Pending == null && !HasPendingValues(e))
			{
				e.Queued = false;
				_queued.RemoveAt(i);
				continue;
			}
			if (now - e.SentMs < e.Policy.CapMs) continue;
			if (e.Pending != null) Send(e, e.Pending, now, output);
			else EmitBlackboard(e, now, output);
			e.Queued = false;
			_queued.RemoveAt(i);
		}
	}

	/// <summary>The entity is gone for every client: drop what waits for it and what was last sent.</summary>
	public void Forget(uint target)
	{
		if (!_keysOf.Remove(target, out var keys)) return;
		// A queued entry with nothing pending leaves the queue at the next Flush.
		foreach (ushort key in keys)
		{
			if (!_entries.Remove((key, target), out var e)) continue;
			e.Pending = null;
			e.PendingValues = null;
		}
	}

	public string Describe() => $"coalesced {Coalesced} of {Offered} ({Forwarded} forwarded, {_entries.Count} tracked): "
		+ string.Join(", ", _byName.Select(kv => $"{Short(kv.Key)} {kv.Value.Out}/{kv.Value.In}"));

	private static string Short(string name) => name[(name.LastIndexOf('.') + 1)..];

	private void Counted(Entry e)
	{
		Forwarded++;
		var n = _byName.GetValueOrDefault(e.Policy.Name);
		_byName[e.Policy.Name] = (n.In, n.Out + 1);
	}

	private void Send(Entry e, WireCommand c, long now, List<WireCommand> output)
	{
		e.Sent = c;
		e.SentMs = now;
		e.Pending = null;
		output.Add(c);
		Counted(e);
	}

	private void Queue(Entry e)
	{
		if (e.Queued) return;
		e.Queued = true;
		_queued.Add(e);
	}

	private static bool HasPendingValues(Entry e)
	{
		if (e.PendingValues == null) return false;
		foreach (var d in e.PendingValues) if (d.Count > 0) return true;
		return false;
	}

	// Floats (span 0) are continuous drivers: the newest one wins and goes out at the cap. Ints, strings and bools are
	// discrete (tool equipped, shot fired then reset the next frame): a change goes out at once so no pulse is merged
	// away. Within one command a key may repeat; the handler applies them in order, so the last one is what counts.
	private bool OfferBlackboard(Entry e, WireCommand c, long now, List<WireCommand> output)
	{
		var p = c.Payload;
		int offset = 4;
		Span<(int Start, int Count)> spans = stackalloc (int, int)[Spans];
		for (int s = 0; s < Spans; s++)
		{
			if (offset + 4 > p.Length) return false;
			int bytes = BinaryPrimitives.ReadInt32LittleEndian(p.AsSpan(offset));
			if (bytes < 0 || bytes % ElementBytes != 0 || offset + 4 + bytes > p.Length) return false;
			spans[s] = (offset + 4, bytes / ElementBytes);
			offset += 4 + bytes;
		}
		if (offset != p.Length) return false;
		e.SentValues ??= NewMaps();
		e.PendingValues ??= NewMaps();
		e.EntityRef = c.Refs[0];
		bool repeat = now - e.RepeatedMs >= RepeatMs;
		if (repeat) e.RepeatedMs = now;
		bool discrete = false;
		bool changed = false;
		for (int s = 0; s < Spans; s++)
		{
			var sent = e.SentValues[s];
			var pending = e.PendingValues[s];
			for (int i = 0; i < spans[s].Count; i++)
			{
				ulong element = BinaryPrimitives.ReadUInt64LittleEndian(p.AsSpan(spans[s].Start + i * ElementBytes));
				int keyAt = e.Policy.KeyOffsets![s] * 8;
				uint key = (uint)(element >> keyAt);
				if (!repeat && sent.TryGetValue(key, out ulong was) && ((was ^ element) & ValueMask(keyAt, s)) == 0)
				{
					pending.Remove(key);
					continue;
				}
				pending[key] = element;
				changed = true;
				if (s != 0) discrete = true;
			}
		}
		if (!changed)
		{
			Coalesced++;
			return true;
		}
		if (discrete || now - e.SentMs >= e.Policy.CapMs)
		{
			EmitBlackboard(e, now, output);
		}
		else
		{
			Coalesced++;
			Queue(e);
		}
		return true;
	}

	private static ulong ValueMask(int keyAt, int span) => (ValueBytes[span] == 4 ? 0xFFFF_FFFFUL : 0xFFUL) << (32 - keyAt);

	private static Dictionary<uint, ulong>[] NewMaps()
	{
		var maps = new Dictionary<uint, ulong>[Spans];
		for (int s = 0; s < Spans; s++) maps[s] = new Dictionary<uint, ulong>();
		return maps;
	}

	private void EmitBlackboard(Entry e, long now, List<WireCommand> output)
	{
		int count = 0;
		foreach (var d in e.PendingValues!) count += d.Count;
		var payload = new byte[4 + Spans * 4 + count * ElementBytes];
		int offset = 4;
		for (int s = 0; s < Spans; s++)
		{
			var pending = e.PendingValues[s];
			BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(offset), pending.Count * ElementBytes);
			offset += 4;
			foreach (var (key, element) in pending)
			{
				BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(offset), element);
				offset += ElementBytes;
				e.SentValues![s][key] = element;
			}
			pending.Clear();
		}
		var command = new WireCommand { Key = e.Key, Payload = payload };
		command.Refs.Add(e.EntityRef!);
		e.SentMs = now;
		output.Add(command);
		Counted(e);
	}

	internal static bool Same(WireCommand a, WireCommand b)
	{
		if (a.Key != b.Key || a.Refs.Count != b.Refs.Count || !a.Payload.AsSpan().SequenceEqual(b.Payload)) return false;
		for (int i = 0; i < a.Refs.Count; i++)
		{
			if (!Same(a.Refs[i], b.Refs[i], 0)) return false;
		}
		return true;
	}

	private static bool Same(WireValue a, WireValue b, int depth)
	{
		if (ReferenceEquals(a, b)) return true;
		if (depth > WireValue.MaxDepth || a.Kind != b.Kind || a.Id != b.Id || a.Guid != b.Guid || a.Text != b.Text) return false;
		if (a.Bytes != null || b.Bytes != null)
		{
			if (a.Bytes == null || b.Bytes == null || !a.Bytes.AsSpan().SequenceEqual(b.Bytes)) return false;
		}
		if (a.Fields != null || b.Fields != null)
		{
			if (a.Fields == null || b.Fields == null || a.Fields.Count != b.Fields.Count) return false;
			for (int i = 0; i < a.Fields.Count; i++)
			{
				if (a.Fields[i].Name != b.Fields[i].Name || !Same(a.Fields[i].Value, b.Fields[i].Value, depth + 1)) return false;
			}
		}
		if (a.Items != null || b.Items != null)
		{
			if (a.Items == null || b.Items == null || a.Items.Count != b.Items.Count) return false;
			for (int i = 0; i < a.Items.Count; i++)
			{
				if (!Same(a.Items[i], b.Items[i], depth + 1)) return false;
			}
		}
		return true;
	}
}
