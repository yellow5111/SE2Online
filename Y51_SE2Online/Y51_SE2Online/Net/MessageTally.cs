using System.Collections.Generic;
using System.Threading;

namespace Y51_SE2Online.Net;

/// <summary>Messages and bytes per message type (the first payload byte), counted from any thread.</summary>
public sealed class MessageTally
{
	private readonly long[] _count = new long[256];

	private readonly long[] _bytes = new long[256];

	public void Add(byte type, int bytes)
	{
		Interlocked.Increment(ref _count[type]);
		Interlocked.Add(ref _bytes[type], bytes);
	}

	public (long Count, long Bytes) this[byte type] => (Volatile.Read(ref _count[type]), Volatile.Read(ref _bytes[type]));

	/// <summary>A copy of every counter, for computing rates between two snapshots.</summary>
	public (long[] Count, long[] Bytes) Snapshot()
	{
		var count = new long[256];
		var bytes = new long[256];
		for (int i = 0; i < 256; i++)
		{
			count[i] = Volatile.Read(ref _count[i]);
			bytes[i] = Volatile.Read(ref _bytes[i]);
		}
		return (count, bytes);
	}

	/// <summary>The <paramref name="top"/> types by bytes between two snapshots.</summary>
	public static List<(byte Type, long Count, long Bytes)> Top((long[] Count, long[] Bytes) before, (long[] Count, long[] Bytes) after, int top)
	{
		var list = new List<(byte, long, long)>();
		for (int i = 0; i < 256; i++)
		{
			long count = after.Count[i] - (before.Count?[i] ?? 0);
			if (count > 0) list.Add(((byte)i, count, after.Bytes[i] - (before.Bytes?[i] ?? 0)));
		}
		list.Sort((a, b) => b.Item3.CompareTo(a.Item3));
		if (list.Count > top) list.RemoveRange(top, list.Count - top);
		return list;
	}
}
