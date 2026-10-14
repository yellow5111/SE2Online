using System;
using System.Collections.Generic;
using Y51_SE2Online.Net;

namespace Y51_SE2Online.Sync;

public static class DespawnMessage
{
	public const byte ModeRemove = 0;

	public const byte ModeForget = 1;

	public static byte[] Encode(uint lifeSeq, byte mode, IReadOnlyList<uint> netIds)
	{
		WireWriter wireWriter = new WireWriter(13 + netIds.Count * 4).U8(50).U32(lifeSeq).U8(mode)
			.I32(netIds.Count);
		foreach (uint netId in netIds)
		{
			wireWriter.U32(netId);
		}
		return wireWriter.ToArray();
	}

	public static (uint LifeSeq, byte Mode, List<uint> NetIds) Decode(ReadOnlySpan<byte> d)
	{
		WireReader r = new WireReader(d.Slice(1));
		uint lifeSeq = r.U32();
		byte mode = r.U8();
		int count = r.I32();
		if (count < 0 || count > (d.Length - 10) / 4)
		{
			throw new ArgumentException("Despawn list longer than its message.");
		}
		List<uint> netIds = new List<uint>(count);
		for (int i = 0; i < count; i++)
		{
			netIds.Add(r.U32());
		}
		return (LifeSeq: lifeSeq, Mode: mode, NetIds: netIds);
	}
}
