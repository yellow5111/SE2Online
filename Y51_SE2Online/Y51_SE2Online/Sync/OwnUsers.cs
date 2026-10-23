using Y51_SE2Online.Net;

namespace Y51_SE2Online.Sync;

/// <summary>
/// Which game user a joined player is registered under on the server (SE2 4.0 keeps every player's identity, and with it
/// their grids and faction place, under that user in the save). A Steam account plays as the game's own user for its
/// Steam id, so a player keeps their identity across server restarts, and across worlds hosted by 0.16 builds.
/// Another game instance of an account (a PeerAlias, or the co-op host's own account) and a local TCP test player are
/// not that Steam user: they get a user made from their SE2 Online player key, which is the same on every join.
/// </summary>
public static class OwnUsers
{
	public static bool UsesSteamUser(ulong peer, ulong hostSteamId) =>
		peer != 0 && !TcpLink.IsSyntheticId(peer) && !PeerAlias.IsAlias(peer) && peer != hostSteamId;

	/// <summary>The service data a non-Steam player's user is made from (stable per player key).</summary>
	public static string InstanceKey(string playerKey) => "instance:" + playerKey;
}
