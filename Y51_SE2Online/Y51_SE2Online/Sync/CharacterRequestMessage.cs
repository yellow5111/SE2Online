using System;

namespace Y51_SE2Online.Sync;

public static class CharacterRequestMessage
{
	public const byte Initial = 0;

	public const byte Respawn = 1;

	/// <summary>SE2 4.0: the client possesses the character the host gave it.</summary>
	public const byte Possessed = 2;

	/// <summary>Choice is the index in the host's last spawn menu, or -1 to ask for the menu.</summary>
	public static byte[] Encode(byte reason, int choice = -1)
	{
		ushort wire = (ushort)(choice + 1);
		return new byte[4] { 38, reason, (byte)wire, (byte)(wire >> 8) };
	}

	public static (byte Reason, int Choice) Decode(ReadOnlySpan<byte> d)
	{
		byte reason = d.Length > 1 ? d[1] : (byte)0;
		int choice = d.Length > 3 ? (d[2] | d[3] << 8) - 1 : -1;
		return (reason, choice);
	}
}
