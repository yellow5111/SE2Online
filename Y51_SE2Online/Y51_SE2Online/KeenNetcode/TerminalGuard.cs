using HarmonyLib;
using Keen.Game2.Client.GameSystems.Interaction;
using Keen.Game2.Client.GameSystems.PlayerControl;
using Keen.Game2.Client.UI.InGame;
using Keen.Game2.Client.UI.TerminalScreen;
using Keen.Game2.Client.WorldObjects.Shared;
using Keen.Game2.Simulation.GameSystems.Economy;
using Keen.Game2.Simulation.GameSystems.Player;
using Keen.Game2.Simulation.StreamedUI.Terminal;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Utils;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>
/// The terminal's character screen reads this player's per-player data (name and credits), which SE2 asks the server for
/// once when the world loads (InProcessPerPlayerDataClientSessionComponent: Attach); later changes come as signals. Without
/// that data GetPerPlayerData returns null inside the screen's constructor and the async-void key handler closed the game
/// (0.16.6, Xero). Until this player's own entry is there the terminal does not open: the use action and the keybind do
/// nothing (a cancelled open would leave the panel's F dead until the player looked away), and the screen itself is the
/// backstop. A notice tells the player to rejoin if the data does not come.
/// </summary>
internal static class TerminalGuard
{
	public static long Blocked;

	// Per client session (weak: a world left behind is not kept by it): when the terminal was first held back, and whether
	// the log line and the notice went out.
	private static System.WeakReference<object>? _session;
	private static long _firstBlockedMs;
	private static bool _logged, _noticed;

	private const long NoticeAfterMs = 15000;

	public static void Install(Harmony harmony)
	{
		// The use key opens the terminal and then marks a loot crate looted (and its rare-loot notice shown): held back whole,
		// so a crate the player could not open stays unlooted.
		harmony.Patch(AccessTools.Method(typeof(UseTerminalAction), nameof(UseTerminalAction.InteractPrimaryStart))
			?? throw new System.MissingMethodException("UseTerminalAction.InteractPrimaryStart"),
			prefix: new HarmonyMethod(typeof(TerminalGuard), nameof(UsePrefix)));
		harmony.Patch(AccessTools.Method(typeof(UseTerminalAction), "OpenTerminal", new[] { typeof(PlayerKeybindsInputHandlerDefinition.IScreenSelection) })
			?? throw new System.MissingMethodException("UseTerminalAction.OpenTerminal(IScreenSelection)"),
			prefix: new HarmonyMethod(typeof(TerminalGuard), nameof(UsePrefix)));
		harmony.Patch(AccessTools.Method(typeof(PlayerKeybindsInputHandlerComponent), "ToggleScreen", new[] { typeof(PlayerKeybindsInputHandlerDefinition.IScreenSelection) })
			?? throw new System.MissingMethodException("PlayerKeybindsInputHandlerComponent.ToggleScreen(IScreenSelection)"),
			prefix: new HarmonyMethod(typeof(TerminalGuard), nameof(KeybindPrefix)));
		harmony.Patch(AccessTools.Method(typeof(SessionInGameUISessionComponent), nameof(SessionInGameUISessionComponent.OpenTerminalScreen), new[] { typeof(InteractionInfo), typeof(TerminalScreenId?) })
			?? throw new System.MissingMethodException("SessionInGameUISessionComponent.OpenTerminalScreen(InteractionInfo, TerminalScreenId?)"),
			prefix: new HarmonyMethod(typeof(TerminalGuard), nameof(OpenPrefix)));
	}

	private static bool UsePrefix(UseTerminalAction __instance) => !Blocks(__instance.Entity);

	private static bool KeybindPrefix(PlayerKeybindsInputHandlerComponent __instance) => !Blocks(__instance.Entity);

	private static bool OpenPrefix(SessionInGameUISessionComponent __instance,
		ref Keen.VRage.Library.Threading.Task<(IObservableDisposable, TerminalScreenViewModel)> __result)
	{
		if (!Blocks(__instance.Entity)) return true;
		__result = Keen.VRage.Library.Threading.Task.FromException<(IObservableDisposable, TerminalScreenViewModel)>(
			new System.Threading.Tasks.TaskCanceledException("SE2 Online: this player's data has not arrived yet"));
		return false;
	}

	private static bool Blocks(Entity? entity)
	{
		if (!MpPlugin.Online || entity == null) return false;
		Keen.VRage.Core.Game.Systems.Session session;
		bool missing;
		try
		{
			session = entity.GetSession();
			missing = Missing(session);
		}
		catch (System.Exception) { return false; }
		if (!missing) return false;
		// Called from the game's async-void input handlers: nothing may escape.
		try { Note(session); }
		catch (System.Exception) { }
		return true;
	}

	/// <summary>This player's own per-player data (what the character screen reads) has not arrived. Main thread.</summary>
	internal static bool Missing(Keen.VRage.Core.Game.Systems.Session session)
	{
		var data = session.SessionComponents.TryGet<InProcessPerPlayerDataClientSessionComponent>();
		// A co-op host's own game reads the server's data directly.
		if (data == null) return false;
		if (!data.IsReplicated || data._data == null) return true;
		var me = session.SessionComponents.TryGet<ClientPlayersSessionComponent>()?.LocalPlayerIdentity ?? default;
		// No local player yet: nothing to check against, and nothing of it to show.
		if ((long)me == 0) return false;
		foreach (var entry in data._data.Data)
			if ((long)entry.IdentityId == (long)me)
				return !entry.DataByType.ContainsKey(typeof(PlayerCharacterData)) || !entry.DataByType.ContainsKey(typeof(PlayerCreditsData));
		return true;
	}

	private static void Note(object session)
	{
		long now = MpClock.NowMs();
		if (_session == null || !_session.TryGetTarget(out var known) || !ReferenceEquals(session, known))
		{
			_session = new System.WeakReference<object>(session);
			_firstBlockedMs = now;
			_logged = _noticed = false;
		}
		Blocked++;
		if (!_logged)
		{
			_logged = true;
			GameLog.Instance.Warn("terminal: this player's data has not arrived from the server yet, so the terminal does not open (it opened into a crash before)");
		}
		// Once it actually showed (another notice on screen holds it back to the next try).
		if (!_noticed && now - _firstBlockedMs >= NoticeAfterMs && SyncEngine.PermissionFeedback.Notify("Terminal unavailable", "Your player data from the server did not arrive. Rejoin to use the terminal."))
		{
			_noticed = true;
			GameLog.Instance.Warn($"terminal: this player's data still has not arrived after {(now - _firstBlockedMs) / 1000} s; told the player to rejoin");
		}
	}
}
