using System;
using HarmonyLib;
using Keen.Game2.Client.UI.InGame;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// Quick save and quick load belong to whoever owns the world. A joiner's save is already refused (ClientStories
/// keeps TrySaveGame local and answers FailureNoLocalSave); this replaces SE2's misleading autosave message with a
/// plain one, and stops quick load, whose "reload" would reconnect the joiner's own client session.
/// </summary>
internal static class HostOnlySaves
{
	internal static void Install(Harmony harmony)
	{
		harmony.Patch(AccessTools.Method(typeof(SessionInGameUISessionComponent), nameof(SessionInGameUISessionComponent.OpenQuickLoadDialog)), prefix: new HarmonyMethod(typeof(HostOnlySaves), nameof(QuickLoadPrefix)));
		harmony.Patch(AccessTools.Method(typeof(Keen.Game2.Game.SessionComponents.WorldSessionComponent).Assembly.GetType("Keen.Game2.Game.SessionComponents.SaveNotificationsSessionComponent", true), "DisplaySaveResultMessage"), prefix: new HarmonyMethod(typeof(HostOnlySaves), nameof(SaveMessagePrefix)));
	}

	private static bool QuickLoadPrefix()
	{
		if (!AdminAccess.Joined) return true;
		PlayerOptions.Notice("Quick load is only available to the host in multiplayer.");
		return false;
	}

	private static bool SaveMessagePrefix(ref IDisposable __result)
	{
		if (!AdminAccess.Joined) return true;
		PlayerOptions.Notice("Only the host can save the world in multiplayer.");
		__result = null;
		return false;
	}
}
