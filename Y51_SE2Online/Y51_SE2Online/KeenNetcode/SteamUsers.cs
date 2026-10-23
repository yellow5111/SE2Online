using HarmonyLib;
using Keen.VRage.Core.Services.Data;
using Keen.VRage.Steam.Core;
using Keen.VRage.Steam.Utils;
using Steamworks;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>The game's user for a Steam account and back (identities, factions and grids in a save name these users).</summary>
internal static class SteamUsers
{
	private static SteamIdServiceEngineComponent? _ids;

	public static void Install(Harmony harmony)
		=> harmony.Patch(AccessTools.Method(typeof(SteamIdServiceEngineComponent), "Initialize"), postfix: new HarmonyMethod(typeof(SteamUsers), nameof(IdsPostfix)));

	private static void IdsPostfix(SteamIdServiceEngineComponent __instance) => _ids = __instance;

	/// <summary>The Steam account a game user plays as: SE2 Online's own registration first (another instance of an account
	/// has a user of its own), then the game's Steam id service.</summary>
	public static ulong? SteamIdOf(GameServiceUser user)
	{
		if (OwnNet.PeerOf(user) is ulong own) return own;
		CSteamID? id = _ids?.TryGetSteamId(user);
		return id.HasValue ? id.Value.m_SteamID : null;
	}

	public static GameServiceUser UserOf(ulong steamId) => _ids?.GetServiceUser(new CSteamID(steamId)) ?? IdGenerator.GetGameServiceUserFromUserSteamId(new CSteamID(steamId));
}
