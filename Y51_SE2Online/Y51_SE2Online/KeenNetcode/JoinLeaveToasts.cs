using System;
using System.Linq;
using HarmonyLib;
using Keen.Game2.Client.GameSystems.ConnectedUsers;
using Keen.Game2.Simulation.GameSystems.Identities;
using Keen.VRage.Core.Services.Data;
using Y51_SE2Online.NetHost;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>
/// SE2 4.0 shows a "Player joined" / "Player left" toast with the player's Steam name. Players SE2 Online adds (same-PC
/// instances) have none, and a host's toast for a new player could name nobody. The toasts now use the name the chat's
/// join and leave lines use: the player's SE2 Online name, by their identity.
/// </summary>
internal static class JoinLeaveToasts
{
	public static void Install(Harmony harmony)
		=> harmony.Patch(AccessTools.Method(typeof(ConnectedUsersClientSessionComponent), "GetUserName"),
			prefix: new HarmonyMethod(typeof(JoinLeaveToasts), nameof(GetUserNamePrefix)));

	private static bool GetUserNamePrefix(ConnectedUsersClientSessionComponent __instance, GameServiceUser user, ref string __result)
	{
		if (NameOf(__instance, user) is not { Length: > 0 } name) return true;
		__result = name;
		return false;
	}

	private static string? NameOf(ConnectedUsersClientSessionComponent component, GameServiceUser user)
	{
		try
		{
			if (MpPlugin.App?.ClientSession?.SessionComponents.TryGet<IdentitiesSessionComponent>()?.TryGetIdentityOf(user) is not { } identity) return null;
			long id = (long)identity;
			var joined = SyncEngine.ClientWorld.Current?.SessionPlayers.FirstOrDefault(p => p.Identity == id);
			if (joined is { Name.Length: > 0 } player) return player.Name;
			return NetSession.Current?.Players.FirstOrDefault(p => p.IdentityId == id)?.Name;
		}
		catch (Exception)
		{
			return null;
		}
	}
}
