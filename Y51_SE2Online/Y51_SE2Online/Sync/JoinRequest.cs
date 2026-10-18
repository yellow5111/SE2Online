using System;
using Y51_SE2Online.Net;

namespace Y51_SE2Online.Sync;

public sealed class JoinRequest
{
	public ushort SyncVersion = SyncProtocol.Version;

	public string PluginVersion = "";

	public string GameVersion = "";

	public string PlayerName = "";

	public string Password = "";

	/// <summary>Bit 1: takes <see cref="SyncMsg.RosterSync"/> patches. Bit 2: <see cref="TradeView"/>. Trailing, so older hosts never read it.</summary>
	public byte Features;

	public const byte RosterPatches = 1;

	/// <summary>Shows the trade partner's inventory and sends item requests (<see cref="TradeVerb.Request"/>).</summary>
	public const byte TradeView = 2;

	public const byte BuildGhosts = 4;

	public byte[] Encode()
	{
		return new WireWriter().U8(32).U16(SyncVersion).Str(PluginVersion)
			.Str(GameVersion)
			.Str(PlayerName)
			.Str(Password)
			.U8(Features)
			.ToArray();
	}

	public static JoinRequest Decode(ReadOnlySpan<byte> d)
	{
		WireReader wireReader = new WireReader(d.Slice(1));
		return new JoinRequest
		{
			SyncVersion = wireReader.U16(),
			PluginVersion = wireReader.Str(),
			GameVersion = wireReader.Str(),
			PlayerName = wireReader.Str(),
			Password = wireReader.Remaining > 0 ? wireReader.Str() : "",
			Features = wireReader.Remaining > 0 ? wireReader.U8() : (byte)0
		};
	}
}
