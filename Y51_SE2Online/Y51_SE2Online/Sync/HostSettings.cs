namespace Y51_SE2Online.Sync;

public readonly record struct HostSettings(bool Identities, GridPermissionMode Grids, ResearchMode Research, bool WaterSync = false)
{
	public byte Pack()
	{
		return (byte)((Identities ? 1u : 0u) | (uint)((Grids == GridPermissionMode.Strict) ? 2 : 0) | (uint)((Research == ResearchMode.Own) ? 4 : 0) | (WaterSync ? 8u : 0u));
	}

	public static HostSettings Unpack(byte b)
	{
		return new HostSettings((b & 1) != 0, ((b & 2) != 0) ? GridPermissionMode.Strict : GridPermissionMode.Team, ((b & 4) != 0) ? ResearchMode.Own : ResearchMode.Shared, (b & 8) != 0);
	}

	public override string ToString()
	{
		return $"identities {(Identities ? "per player" : "shared (off)")}, grids {((Grids == GridPermissionMode.Team) ? "team" : "strict")}, research {((Research == ResearchMode.Shared) ? "shared" : "own")}, water sync {(WaterSync ? "on (experimental)" : "off")}";
	}
}
