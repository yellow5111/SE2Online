using System;
using System.Collections.Generic;
using Y51_SE2Online.Net;

namespace Y51_SE2Online.Sync;

public sealed class JoinAccept
{
	public ushort SyncVersion = SyncProtocol.Version;

	public string WorldName = "";

	public uint HostTick;

	public int SnapshotBytes;

	public int WorldBytes;

	public int WorldEntities;

	public int WorldParts = 1;

	public bool HostMissions;

	public int WorldBlobs;

	public long WorldBlobBytes;

	public ushort ClientId;

	public HostSettings Settings;

	public long IdentityId;

	public long PlayerId;

	public List<PlayerInfo> Players = new List<PlayerInfo>();

	/// <summary>The mods the host's world runs; the joiner loads exactly these before entering.</summary>
	public List<WorldMod> Mods = new List<WorldMod>();

	public string ModsError;

	public byte[] Encode()
	{
		WireWriter wireWriter = new WireWriter(96 + Players.Count * 40).U8(33).U16(SyncVersion).Str(WorldName)
			.U32(HostTick)
			.I32(SnapshotBytes)
			.I32(WorldBytes)
			.I32(WorldEntities)
			.I32(WorldParts)
			.U8(HostMissions ? ((byte)1) : ((byte)0))
			.I32(WorldBlobs)
			.I64(WorldBlobBytes)
			.U16(ClientId)
			.U8(Settings.Pack())
			.I64(IdentityId)
			.I64(PlayerId);
		PlayerTableMessage.Write(wireWriter, Players);
		WorldMods.Write(wireWriter, Mods);
		return wireWriter.ToArray();
	}

	public static JoinAccept Decode(ReadOnlySpan<byte> d)
	{
		WireReader r = new WireReader(d.Slice(1));
		JoinAccept joinAccept = new JoinAccept
		{
			SyncVersion = r.U16(),
			WorldName = r.Str(),
			HostTick = r.U32(),
			SnapshotBytes = r.I32(),
			WorldBytes = r.I32(),
			WorldEntities = r.I32(),
			WorldParts = ((r.Remaining < 4) ? 1 : r.I32()),
			HostMissions = (r.Remaining >= 1 && r.U8() != 0),
			WorldBlobs = ((r.Remaining >= 4) ? r.I32() : 0),
			WorldBlobBytes = ((r.Remaining >= 8) ? r.I64() : 0),
			ClientId = (ushort)((r.Remaining >= 2) ? r.U16() : 0)
		};
		if (r.Remaining >= 17)
		{
			joinAccept.Settings = HostSettings.Unpack(r.U8());
			joinAccept.IdentityId = r.I64();
			joinAccept.PlayerId = r.I64();
			joinAccept.Players = PlayerTableMessage.Read(ref r);
			try
			{
				joinAccept.Mods = WorldMods.Read(ref r);
			}
			catch (Exception ex) when (ex is FormatException || ex is ArgumentOutOfRangeException)
			{
				joinAccept.ModsError = "the host's mod list could not be read: " + ex.Message;
			}
		}
		return joinAccept;
	}
}
