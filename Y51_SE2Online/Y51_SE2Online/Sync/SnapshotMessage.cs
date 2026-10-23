using System;
using System.Collections.Generic;
using Y51_SE2Online.Net;

namespace Y51_SE2Online.Sync;

public static class SnapshotMessage
{
	/// <summary>A player's request for the world's session snapshot (sent once its game builds the server's world).</summary>
	public static byte[] Request() => new[] { (byte)SyncMsg.Snapshot };

	public static bool IsRequest(ReadOnlySpan<byte> d) => d.Length == 1 && d[0] == (byte)SyncMsg.Snapshot;

	/// <param name="user">The game user the server registered this player under: its identity is the one bound to it.</param>
	public static byte[] Encode(ReadOnlySpan<byte> snapshot, IReadOnlyList<Guid>? requiredBlobs = null, Guid user = default)
	{
		WireWriter wireWriter = new WireWriter(28 + snapshot.Length + (requiredBlobs?.Count ?? 0) * 16).U8(48).I32(snapshot.Length).Bytes(snapshot);
		WireGuids.Write(wireWriter, requiredBlobs);
		if (user != Guid.Empty)
		{
			Span<byte> id = stackalloc byte[16];
			user.TryWriteBytes(id);
			wireWriter.Bytes(id);
		}
		return wireWriter.ToArray();
	}

	public static byte[] Decode(ReadOnlySpan<byte> d)
	{
		return Decode(d, out _, out _);
	}

	public static byte[] Decode(ReadOnlySpan<byte> d, out List<Guid> requiredBlobs) => Decode(d, out requiredBlobs, out _);

	public static byte[] Decode(ReadOnlySpan<byte> d, out List<Guid> requiredBlobs, out Guid user)
	{
		WireReader r = new WireReader(d.Slice(1));
		int n = r.I32();
		byte[] result = r.Bytes(n).ToArray();
		requiredBlobs = WireGuids.Read(ref r);
		user = r.Remaining >= 16 ? new Guid(r.Bytes(16)) : Guid.Empty;
		return result;
	}
}
