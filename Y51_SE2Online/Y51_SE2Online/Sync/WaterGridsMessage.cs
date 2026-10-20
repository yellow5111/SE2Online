using System;
using System.Collections.Generic;
using Y51_SE2Online.Net;

namespace Y51_SE2Online.Sync;

/// <summary>
/// Host to joiner: water grid cells packed by the game's own WaterGridPackerComponent, keyed by the replicated water
/// domain's NetId and the grid's domain position and LOD. Null cells removes that grid.
/// </summary>
public static class WaterGridsMessage
{
	public const int MaxGrids = 1024;

	/// <summary>Joiner side: whether the host streams water, from the join accept.</summary>
	public static volatile bool Enabled;

	/// <summary>Joiner side: received messages, applied on the game thread by WaterMirror.</summary>
	public static readonly System.Collections.Concurrent.ConcurrentQueue<byte[]> Inbox = new();

	public static void Receive(ReadOnlySpan<byte> data)
	{
		// Not gated on Enabled: the host only sends with water sync on, and a large first batch on the bulk lane can land
		// before the accept that turns it on (it would be lost for good: the host sends the full set once).
		if (Inbox.Count < 512) Inbox.Enqueue(data.ToArray());
	}

	public const int MaxCellBytes = 1 << 20;

	/// <summary>Messages are cut at about this size; a single grid can be larger.</summary>
	public const int ChunkBytes = 256 * 1024;

	public readonly record struct Grid(uint Domain, int X, int Y, int Z, byte Lod, byte Resolution, byte[]? Cells)
	{
		public (uint, int, int, int, byte) Key => (Domain, X, Y, Z, Lod);
		public int WireBytes => 4 + 12 + 2 + 4 + (Cells?.Length ?? 0);
	}

	public static List<byte[]> Encode(IReadOnlyList<Grid> grids)
	{
		var messages = new List<byte[]>();
		int start = 0;
		while (start < grids.Count)
		{
			int end = start, bytes = 0;
			while (end < grids.Count && end - start < MaxGrids && (end == start || bytes + grids[end].WireBytes <= ChunkBytes))
			{
				bytes += grids[end].WireBytes;
				end++;
			}
			var w = new WireWriter(8 + bytes).U8((byte)SyncMsg.WaterGrids).U16((ushort)(end - start));
			for (int i = start; i < end; i++)
			{
				var g = grids[i];
				w.U32(g.Domain).I32(g.X).I32(g.Y).I32(g.Z).U8(g.Lod).U8(g.Resolution).I32(g.Cells?.Length ?? -1);
				if (g.Cells != null) w.Bytes(g.Cells);
			}
			messages.Add(w.ToArray());
			start = end;
		}
		return messages;
	}

	private const int EntryHeader = 4 + 12 + 1 + 1 + 4;

	/// <summary>The grids, or FormatException for anything malformed or truncated.</summary>
	public static List<Grid> Decode(ReadOnlySpan<byte> data)
	{
		if (data.Length < 3) throw new FormatException("short water grids message");
		var r = new WireReader(data.Slice(1));
		int count = r.U16();
		if (count > MaxGrids) throw new FormatException("too many water grids");
		var grids = new List<Grid>(count);
		for (int i = 0; i < count; i++)
		{
			if (r.Remaining < EntryHeader) throw new FormatException("truncated water grid entry");
			uint domain = r.U32();
			int x = r.I32(), y = r.I32(), z = r.I32();
			byte lod = r.U8(), resolution = r.U8();
			int length = r.I32();
			if (domain == 0 || lod > 20 || resolution is not (8 or 16) || length < -1 || length > MaxCellBytes || length > r.Remaining) throw new FormatException("bad water grid entry");
			grids.Add(new Grid(domain, x, y, z, lod, resolution, length < 0 ? null : r.Bytes(length).ToArray()));
		}
		if (r.Remaining != 0) throw new FormatException("trailing bytes after water grids");
		return grids;
	}
}
