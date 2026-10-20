using HarmonyLib;
using Keen.Game2.Simulation.GameSystems.Identities;
using Keen.Game2.Simulation.GameSystems.Ownership;
using Keen.VRage.Core.Services.Data;
using Keen.VRage.Multiplayer;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>
/// Worlds hosted with SE2 Online on SE2 2.4 own each player's grids by an identity derived from their Steam id
/// (<see cref="PlayerIdentities.Derive"/>). SE2 4.0 binds a player to the identity it already knows for their
/// Steam user, else a new one; a player from a 2.4 world has none yet, so they get their derived identity back.
/// </summary>
internal static class IdentityCarryOver
{
	private static IMpLog _log = GameLog.Instance;

	public static void Install(Harmony harmony, IMpLog log)
	{
		_log = log;
		var identities = typeof(IdentitiesSessionComponent);
		harmony.Patch(AccessTools.Method(identities, "EnsureRemotePlayer"), prefix: new HarmonyMethod(typeof(IdentityCarryOver), nameof(RemotePrefix)));
		harmony.Patch(AccessTools.Method(identities, nameof(IdentitiesSessionComponent.EnsureLocalPlayer)), prefix: new HarmonyMethod(typeof(IdentityCarryOver), nameof(LocalPrefix)));
	}

	private static bool RemotePrefix(IdentitiesSessionComponent __instance, ClientId clientId, GameServiceUser user)
	{
		if (Legacy(__instance, user) is not { } identity) return true;
		__instance.BindPlayer(identity, clientId, user);
		return false;
	}

	// A host whose world was on a 2.4 dedicated server played there as their derived identity, not the save's local one.
	private static void LocalPrefix(IdentitiesSessionComponent __instance, GameServiceUser user)
	{
		if (Legacy(__instance, user) is { } identity) __instance.LocalIdentity = identity;
	}

	private static IdentityId? Legacy(IdentitiesSessionComponent identities, GameServiceUser user)
	{
		if (user == GameServiceUser.Invalid || identities.TryGetIdentityOf(user) != null || KeenNet.SteamIdOf(user) is not ulong steamId) return null;
		var legacy = new IdentityId(PlayerIdentities.Derive("steam:" + steamId));
		if (!identities._identities.Contains(legacy) || identities.TryGetUserOf(legacy) != null) return null;
		_log.Info($"identities: Steam user {steamId} gets back identity {(long)legacy} from this world's SE2 2.4 days");
		return legacy;
	}
}
