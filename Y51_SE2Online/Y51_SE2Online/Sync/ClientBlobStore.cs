using System;
using System.Collections.Generic;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.Sync;

public sealed class ClientBlobStore
{
	public enum Gate
	{
		Ready,
		Waiting,
		Stalled
	}

	private sealed class Partial
	{
		public byte[] Data;

		public int Total;

		public int Received;
	}

	private readonly object _sync = new object();

	private readonly Dictionary<Guid, byte[]> _complete = new Dictionary<Guid, byte[]>();

	private readonly HashSet<Guid> _missingOnHost = new HashSet<Guid>();

	private readonly Dictionary<Guid, Partial> _partial = new Dictionary<Guid, Partial>();

	private readonly List<Guid> _fresh = new List<Guid>();

	private long _lastProgressMs;

	public long Pieces;

	public long BytesReceived;

	public long Rejected;

	public long StallMs { get; set; } = 30000L;

	public int MaxBlobBytes { get; set; } = 536870912;

	/// <summary>Cap on the buffers of all blobs still being received; the host streams them one at a time.</summary>
	public long MaxPartialBytes { get; set; } = 1073741824L;

	public int MaxPartialBlobs { get; set; } = 64;

	private long _partialBytes;

	public int Count
	{
		get
		{
			lock (_sync)
			{
				return _complete.Count;
			}
		}
	}

	public long CompleteBytes
	{
		get
		{
			lock (_sync)
			{
				long num = 0L;
				foreach (byte[] value in _complete.Values)
				{
					num += value.Length;
				}
				return num;
			}
		}
	}

	public int MissingOnHostCount
	{
		get
		{
			lock (_sync)
			{
				return _missingOnHost.Count;
			}
		}
	}

	public bool HasFresh
	{
		get
		{
			lock (_sync)
			{
				return _fresh.Count > 0;
			}
		}
	}

	public bool Accept(BlobPiece p, long now)
	{
		lock (_sync)
		{
			Pieces++;
			_lastProgressMs = now;
			if (_complete.ContainsKey(p.Id))
			{
				return true;
			}
			if (p.MissingOnHost)
			{
				Forget(p.Id);
				if (_missingOnHost.Add(p.Id))
				{
					_fresh.Add(p.Id);
				}
				return true;
			}
			if (p.Total > MaxBlobBytes || p.Offset < 0 || (long)p.Offset + (long)p.Bytes.Length > p.Total)
			{
				Rejected++;
				return false;
			}
			if (p.Offset == 0 && p.Bytes.Length == p.Total)
			{
				Forget(p.Id);
				BytesReceived += p.Bytes.Length;
				Complete(p.Id, p.Bytes);
				return true;
			}
			// The host packs each blob's pieces in offset order on the ordered bulk channel, so the buffer
			// only grows with bytes that actually arrived, never to a declared Total up front.
			_partial.TryGetValue(p.Id, out Partial value);
			if (p.Offset == 0)
			{
				Forget(p.Id);
				if (_partial.Count >= MaxPartialBlobs)
				{
					Rejected++;
					return false;
				}
				value = new Partial { Data = Array.Empty<byte>(), Total = p.Total };
				_partial[p.Id] = value;
			}
			else if (value == null || value.Total != p.Total || value.Received != p.Offset)
			{
				Forget(p.Id);
				Rejected++;
				return false;
			}
			int needed = p.Offset + p.Bytes.Length;
			if (needed > value.Data.Length)
			{
				int size = (int)Math.Min(p.Total, Math.Max(needed, (long)value.Data.Length * 2));
				if (_partialBytes - value.Data.Length + size > MaxPartialBytes)
				{
					Forget(p.Id);
					Rejected++;
					return false;
				}
				byte[] grown = new byte[size];
				value.Data.AsSpan(0, value.Received).CopyTo(grown);
				_partialBytes += size - value.Data.Length;
				value.Data = grown;
			}
			p.Bytes.CopyTo(value.Data, p.Offset);
			value.Received = needed;
			BytesReceived += p.Bytes.Length;
			if (value.Received == p.Total)
			{
				Forget(p.Id);
				Complete(p.Id, value.Data);
			}
			return true;
		}
	}

	private void Forget(Guid id)
	{
		if (_partial.Remove(id, out Partial value))
		{
			_partialBytes -= value.Data.Length;
		}
	}

	private void Complete(Guid id, byte[] data)
	{
		_complete[id] = data;
		_missingOnHost.Remove(id);
		_fresh.Add(id);
	}

	public bool Has(Guid id)
	{
		lock (_sync)
		{
			return _complete.ContainsKey(id);
		}
	}

	public bool IsMissingOnHost(Guid id)
	{
		lock (_sync)
		{
			return _missingOnHost.Contains(id);
		}
	}

	public Gate Check(IReadOnlyList<Guid> required, long now, out int absent)
	{
		absent = 0;
		if (required.Count == 0)
		{
			return Gate.Ready;
		}
		lock (_sync)
		{
			int num = 0;
			foreach (Guid item in required)
			{
				if (!_complete.ContainsKey(item))
				{
					absent++;
					if (!_missingOnHost.Contains(item))
					{
						num++;
					}
				}
			}
			if (num == 0)
			{
				return Gate.Ready;
			}
			return (now - _lastProgressMs < StallMs) ? Gate.Waiting : Gate.Stalled;
		}
	}

	/// <summary>A bundle starts waiting: it gets the full stall window from now, however long the blob stream was quiet before.</summary>
	public void Touch(long now)
	{
		lock (_sync)
		{
			if (_lastProgressMs < now)
			{
				_lastProgressMs = now;
			}
		}
	}

	public List<(Guid Id, byte[]? Data)> TakeFresh()
	{
		lock (_sync)
		{
			List<(Guid, byte[])> list = new List<(Guid, byte[])>(_fresh.Count);
			foreach (Guid item in _fresh)
			{
				list.Add((item, _complete.TryGetValue(item, out byte[] value) ? value : null));
			}
			_fresh.Clear();
			return list;
		}
	}

	public List<(Guid Id, byte[] Data)> All()
	{
		lock (_sync)
		{
			List<(Guid, byte[])> list = new List<(Guid, byte[])>(_complete.Count);
			foreach (KeyValuePair<Guid, byte[]> item in _complete)
			{
				list.Add((item.Key, item.Value));
			}
			return list;
		}
	}

	public string Describe()
	{
		return $"{Count} blobs ({MpLogExtensions.Bytes(CompleteBytes)}), {MissingOnHostCount} missing on the host, {Pieces} pieces";
	}
}
