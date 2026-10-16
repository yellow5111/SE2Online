namespace Y51_SE2Online.Net;

public sealed record FriendHost(ulong SteamId, string Name, HostPresence? Presence, string? RawPresence = null, string PingLocation = "")
{
	public bool Hosting => Presence != null;

	public string Describe()
	{
		if (Presence == null)
		{
			return $"'{Name}' ({SteamId})";
		}
		return $"'{Name}' ({SteamId}, world '{Presence.WorldName}', plugin {Presence.PluginVersion}, sync v{Presence.SyncVersion})";
	}
}
