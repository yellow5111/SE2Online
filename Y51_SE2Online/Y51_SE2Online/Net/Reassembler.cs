using System;
using System.Collections.Generic;

namespace Y51_SE2Online.Net;

public sealed class Reassembler
{
	private sealed class Pending
	{
		public FragmentHeader First;

		public byte[] Buffer;

		public uint Received;

		public long LastActivityMs;

		public long StartedMs;

		public long Bytes;
	}

	private readonly Dictionary<uint, Pending> _pending = new();

	private readonly HashSet<uint> _recentlyCompleted = new();

	private readonly Queue<uint> _recentOrder = new();

	// Senders always use TransportOptions.FragmentPayload (32 KiB); tiny sizes only inflate per-fragment overhead.
	public const uint MinFragmentSize = 1024;

	public int MaxMessageBytes { get; }

	public int MaxConcurrent { get; }

	public long MaxBufferedBytes { get; }

	public int TimeoutMs { get; }

	public long BufferedBytes { get; private set; }

	public int InProgress => _pending.Count;

	/// <summary>Bytes in of the messages still being reassembled, and those messages' declared lengths (join progress).</summary>
	public long PendingBytes { get; private set; }

	public long PendingTotalBytes { get; private set; }

	public long Completed { get; private set; }

	public long Rejected { get; private set; }

	public long Duplicates { get; private set; }

	public long TimedOut { get; private set; }

	public string? LastError { get; private set; }

	public Reassembler(int maxMessageBytes = 268435456, int maxConcurrent = 8, long maxBufferedBytes = 536870912L, int timeoutMs = 120000)
	{
		MaxMessageBytes = maxMessageBytes;
		MaxConcurrent = maxConcurrent;
		MaxBufferedBytes = maxBufferedBytes;
		TimeoutMs = timeoutMs;
	}

	public bool TryGetProgress(uint transferId, out int received, out int count, out long startedMs)
	{
		if (_pending.TryGetValue(transferId, out var pending))
		{
			received = (int)pending.Received;
			count = (int)pending.First.Count;
			startedMs = pending.StartedMs;
			return true;
		}
		received = count = 0;
		startedMs = 0;
		return false;
	}

	public ReassemblyResult Add(in FragmentHeader h, ReadOnlySpan<byte> payload, long nowMs, out byte[]? complete)
	{
		complete = null;
		string error = h.Validate(payload.Length);
		if (error is null && h.FragmentSize < MinFragmentSize)
		{
			error = $"fragment size {h.FragmentSize} below {MinFragmentSize}";
		}
		if (error is null && h.TotalLength > MaxMessageBytes)
		{
			error = $"message of {h.TotalLength} B exceeds limit {MaxMessageBytes}";
		}
		if (error is not null)
		{
			return Reject(error);
		}
		if (_recentlyCompleted.Contains(h.TransferId))
		{
			Duplicates++;
			return ReassemblyResult.Duplicate;
		}
		if (h.Count == 1)
		{
			complete = payload.ToArray();
			Remember(h.TransferId);
			Completed++;
			return ReassemblyResult.Complete;
		}
		if (!_pending.TryGetValue(h.TransferId, out var pending))
		{
			if (_pending.Count >= MaxConcurrent)
			{
				return Reject($"too many concurrent transfers ({_pending.Count})");
			}
			pending = new Pending
			{
				First = h,
				Buffer = Array.Empty<byte>(),
				StartedMs = nowMs
			};
			_pending.Add(h.TransferId, pending);
			PendingTotalBytes += h.TotalLength;
		}
		else if (pending.First.Count != h.Count || pending.First.TotalLength != h.TotalLength || pending.First.FragmentSize != h.FragmentSize)
		{
			Drop(h.TransferId, pending);
			return Reject($"transfer {h.TransferId}: geometry changed mid-transfer");
		}
		pending.LastActivityMs = nowMs;
		if (h.Index < pending.Received)
		{
			Duplicates++;
			return ReassemblyResult.Duplicate;
		}
		// Bulk frames ride one reliable ordered lane, so fragments arrive in order; the buffer grows with
		// the bytes actually received instead of trusting the declared TotalLength up front.
		if (h.Index != pending.Received)
		{
			Drop(h.TransferId, pending);
			return Reject($"transfer {h.TransferId}: fragment {h.Index} arrived before {pending.Received}");
		}
		long offset = (long)h.Index * h.FragmentSize;
		long needed = offset + payload.Length;
		if (needed > pending.Buffer.Length)
		{
			long size = Math.Min(h.TotalLength, Math.Max(needed, (long)pending.Buffer.Length * 2));
			if (BufferedBytes - pending.Buffer.Length + size > MaxBufferedBytes)
			{
				Drop(h.TransferId, pending);
				return Reject($"buffer limit: {BufferedBytes} + {size - pending.Buffer.Length} > {MaxBufferedBytes}");
			}
			byte[] grown = new byte[size];
			pending.Buffer.AsSpan(0, (int)offset).CopyTo(grown);
			BufferedBytes += size - pending.Buffer.Length;
			pending.Buffer = grown;
		}
		payload.CopyTo(pending.Buffer.AsSpan((int)offset));
		pending.Received++;
		pending.Bytes += payload.Length;
		PendingBytes += payload.Length;
		if (pending.Received < h.Count)
		{
			return ReassemblyResult.Partial;
		}
		Drop(h.TransferId, pending);
		Remember(h.TransferId);
		Completed++;
		complete = pending.Buffer;
		return ReassemblyResult.Complete;
	}

	public int Expire(long nowMs)
	{
		List<uint> expired = null;
		foreach (var pair in _pending)
		{
			if (nowMs - pair.Value.LastActivityMs > TimeoutMs)
			{
				(expired ??= new List<uint>()).Add(pair.Key);
			}
		}
		if (expired is null)
		{
			return 0;
		}
		foreach (uint id in expired)
		{
			Drop(id, _pending[id]);
			TimedOut++;
			LastError = $"transfer {id} timed out";
		}
		return expired.Count;
	}

	private void Drop(uint id, Pending p)
	{
		_pending.Remove(id);
		BufferedBytes -= p.Buffer.Length;
		PendingBytes -= p.Bytes;
		PendingTotalBytes -= p.First.TotalLength;
	}

	private void Remember(uint id)
	{
		if (_recentlyCompleted.Add(id))
		{
			_recentOrder.Enqueue(id);
			if (_recentOrder.Count > 1024)
			{
				_recentlyCompleted.Remove(_recentOrder.Dequeue());
			}
		}
	}

	private ReassemblyResult Reject(string error)
	{
		Rejected++;
		LastError = error;
		return ReassemblyResult.Rejected;
	}
}
