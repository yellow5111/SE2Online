using System;
using System.Collections.Generic;
using System.Linq;
using Y51_SE2Online.Net;

namespace Y51_SE2Online.Sync;

public static class PlayerNamesMessage
{
	public const int MaxNameChars = 32;

	public static byte[] Encode(uint version, IReadOnlyList<PlayerTag> tags)
	{
		WireWriter wireWriter = new WireWriter(8 + tags.Count * 24).U8(58).U32(version).U16((ushort)Math.Min(tags.Count, 65535));
		for (int i = 0; i < tags.Count && i < 65535; i++)
		{
			wireWriter.U16(tags[i].ClientId).U32(tags[i].NetId).Str(HostPresence.Clean(tags[i].Name, 32));
		}
		return wireWriter.ToArray();
	}

	public static string Describe(IReadOnlyList<PlayerTag> tags)
	{
		if (tags.Count != 0)
		{
			// Logged on every roster change: with hundreds of players the whole list would be tens of KB per line.
			return string.Join(", ", tags.Take(16).Select((PlayerTag t) => $"ClientId {t.ClientId} '{t.Name}' = NetworkId {t.NetId}")) + (tags.Count > 16 ? $" and {tags.Count - 16} more" : "");
		}
		return "none";
	}

	public static (uint Version, List<PlayerTag> Tags) Decode(ReadOnlySpan<byte> d)
	{
		WireReader wireReader = new WireReader(d.Slice(1));
		uint item = wireReader.U32();
		int num = wireReader.U16();
		List<PlayerTag> list = new List<PlayerTag>(num);
		for (int i = 0; i < num; i++)
		{
			list.Add(new PlayerTag(wireReader.U16(), wireReader.U32(), wireReader.Str()));
		}
		return (Version: item, Tags: list);
	}
}
