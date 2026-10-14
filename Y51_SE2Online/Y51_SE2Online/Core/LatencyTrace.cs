using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Threading;

namespace Y51_SE2Online.Core;

/// <summary>
/// Opt-in per-hop timestamps for latency measurements (off unless a test switches it on). Every line is
/// "ms,stage,type,key,bytes,channel,peer" with <see cref="MpClock"/> time, which is the machine-wide QPC clock, so two game
/// processes on one PC can be joined line by line. Sequenced lifecycle messages are keyed by their LifeSeq; everything
/// else by its per-type ordinal at that stage (in-order lanes make the nth send the peer's nth receive).
/// </summary>
public static class LatencyTrace
{
	public static volatile bool Enabled;

	public static readonly ConcurrentQueue<string> Lines = new();

	private static readonly ConcurrentDictionary<(ulong Peer, int Slot), long> Ordinals = new();

	private static readonly string[] Stages = { "q", "tx", "rx", "take", "apply", "hold", "bq", "h-apply" };

	public static bool Sequenced(byte type) => type is 49 or 50 or 51 or 55 or 56 or 57;

	public static void Mark(int stage, ReadOnlySpan<byte> message, int channel = -1, ulong peer = 0)
	{
		if (message.Length == 0) return;
		// A compressed bulk envelope keeps the original's type and LifeSeq readable after its 5-byte prefix.
		if (message[0] == 0xC5 && message.Length >= 10) message = message.Slice(5);
		byte type = message[0];
		// Ordinals count while tracing is off too, so both processes number a connection's messages from its first one.
		long key = Sequenced(type) && message.Length >= 5 ? BinaryPrimitives.ReadUInt32LittleEndian(message.Slice(1)) : Ordinals.AddOrUpdate((peer, stage * 256 + type), 1, (_, n) => n + 1);
		if (Enabled) Add(stage, type, key, message.Length, channel, peer);
	}

	public static void Mark(int stage, byte type, long key, int bytes = 0)
	{
		if (Enabled) Add(stage, type, key, bytes, -1);
	}

	/// <summary>Per-type ordinal for stages that have no message bytes at hand (host applying upstream work).</summary>
	public static void Next(int stage, byte type, ulong peer = 0)
	{
		long key = Ordinals.AddOrUpdate((peer, stage * 256 + type), 1, (_, n) => n + 1);
		if (Enabled) Add(stage, type, key, 0, -1, peer);
	}

	private static void Add(int stage, byte type, long key, int bytes, int channel, ulong peer = 0)
	{
		if (Lines.Count < 2_000_000) Lines.Enqueue(FormattableString.Invariant($"{MpClock.NowMs()},{Stages[stage]},{type},{key},{bytes},{channel},{peer}"));
	}

	public const int Queued = 0, Sent = 1, Received = 2, Taken = 3, Applied = 4, Held = 5, BulkQueued = 6, HostApplied = 7;
}
