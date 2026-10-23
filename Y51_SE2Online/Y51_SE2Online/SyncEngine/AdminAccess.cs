using System;
using HarmonyLib;
using Keen.Game2.Client.UI.Library;
using Keen.Game2.Client.UI.Library.Dialogs.OneOptionDialog;
using Keen.Game2.Client.UI.Library.Dialogs.TextInputDialog;
using Keen.Game2.Client.UI.Library.Dialogs.TwoOptionsDialog;
using Keen.Game2.Client.UI.Menu.InGameMenu;
using Keen.VRage.Library.Localization;
using Y51_SE2Online.Core;
using Y51_SE2Online.NetHost;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

/// <summary>Local view of the multiplayer role, chat and admin actions, for host and joiner alike.</summary>
internal static class AdminAccess
{
    private static ClientSession _noticeSession;
    private static long _noticeVersion;
    private static (byte Code, string Text, string HostVersion)? _pendingNotice;

    public static ClientSession Client => ClientWorld.Current?.BoundSession;
    public static HostSession Host => NetSession.Current?.Host;
    public static bool Hosting => MpPlugin.Controller?.Current?.Host == true && Host != null;
    public static bool Joined => MpPlugin.Controller?.Current?.Join == true && Client?.CurrentPhase == ClientSession.Phase.Accepted;
    public static bool InSession => Hosting || Joined;

    public static OnlineRole LocalRole => Hosting ? OnlineRole.Host : Joined ? Client.Admin.Role : OnlineRole.Player;
    /// <summary>True offline too: single player keeps the game's own free camera and tools.</summary>
    public static bool LocalIsAdmin => !Joined || LocalRole != OnlineRole.Player;

    public static AdminState State
    {
        get
        {
            if (Hosting) return Host.StateFor(OnlineRole.Host, Host.AccessPlayers(), Host.LocalAdminMessage);
            return Joined ? Client.Admin : new AdminState();
        }
    }

    public static string ServerName
    {
        get
        {
            var client = Client;
            if (client == null) return "this server";
            if (!string.IsNullOrWhiteSpace(client.Admin.ServerName)) return client.Admin.ServerName;
            return string.IsNullOrWhiteSpace(client.Accept?.WorldName) ? "this server" : client.Accept.WorldName;
        }
    }

    public static void Send(AdminRequest request)
    {
        if (Hosting)
        {
            string message = Host.LocalAdmin(request);
            if (!string.IsNullOrEmpty(message)) Host.LocalAdminMessage = message;
        }
        else Client?.SendAdmin(request);
    }

    public static void Chat(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (text.Trim().Equals("/admin", StringComparison.OrdinalIgnoreCase)) { MpMenu.OpenAdmin("/admin"); return; }
        if (VoiceCommand(text.Trim()) || TradeUi.Command(text.Trim()) || HackUi.Command(text.Trim())) return;
        if (Hosting) Host.LocalChat(text, false);
        else Client?.SendChat(text);
    }

    // "/mute <player>" toggles hearing that player's voice; stays on this machine, never sent as chat.
    private static bool VoiceCommand(string text)
    {
        if (!text.StartsWith("/mute", StringComparison.OrdinalIgnoreCase) && !text.StartsWith("/unmute", StringComparison.OrdinalIgnoreCase)) return false;
        string name = text[(text.IndexOf(' ') is int space and > 0 ? space + 1 : text.Length)..].Trim();
        bool? muted = name.Length == 0 ? null : VoiceChat.ToggleMute(name);
        string reply = name.Length == 0 ? "Usage: /mute <player name> (again to unmute)."
            : muted == null ? "No player named '" + name + "' is online."
            : muted.Value ? "You no longer hear " + name + "'s voice." : "You hear " + name + "'s voice again.";
        ChatLog.Add(new ChatLine("Voice", 0, reply, ChatKind.System, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), MpClock.NowMs());
        return true;
    }

    public static void Install(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(InGameMenuScreenViewModel), nameof(InGameMenuScreenViewModel.TransitionToMainMenu)), prefix: new HarmonyMethod(typeof(AdminAccess), nameof(LeavePrefix)));
        harmony.Patch(AccessTools.Method(typeof(InGameMenuScreenViewModel), nameof(InGameMenuScreenViewModel.ExitApplication)), prefix: new HarmonyMethod(typeof(AdminAccess), nameof(ExitPrefix)));
    }

    // The host or server owns a joiner's world, so the native "save before quitting" choice does not apply.
    private static bool LeavePrefix(InGameMenuScreenViewModel __instance) => !ConfirmLeave(__instance, () =>
    {
        ClientWorld.PlayerLeft();
        MpPlugin.Controller?.WorldExiting(MpClock.NowMs());
        __instance._gameApp.Disconnect();
    }, "Leave");

    private static bool ExitPrefix(InGameMenuScreenViewModel __instance) => !ConfirmLeave(__instance, () => __instance._gameApp.ExitGame(), "Leave and exit");

    private static bool ConfirmLeave(InGameMenuScreenViewModel menu, Action leave, string confirm)
    {
        if (MpPlugin.Controller?.Current?.Join != true || menu._sharedUI == null) return false;
        menu._sharedUI.ShowDialog(new TwoOptionsDialogViewModel(new TwoOptionsDialogDefinition
        {
            Title = OnlineUi.Raw("Leave server"),
            Content = OnlineUi.Raw("Are you sure you want to leave " + ServerName + "?"),
            ConfirmOption = OnlineUi.Raw(confirm),
            CancelOption = OnlineUi.Raw("Cancel")
        }) { ConfirmAction = leave });
        return true;
    }

    /// <summary>Shows kick/ban notices and password prompts once the client is back at the menu.</summary>
    public static void Tick(SharedUIComponent shared)
    {
        var client = Client;
        if (client != null && (client != _noticeSession || client.RejectVersion != _noticeVersion))
        {
            _noticeSession = client; _noticeVersion = client.RejectVersion;
            if (client.RejectVersion != 0) _pendingNotice = (client.RejectCode, client.RejectReason ?? "", client.RejectHostVersion);
        }
        if (_pendingNotice == null || shared == null || MpPlugin.App?.Client != null || MpPlugin.App?._isTransitioning.IsSet == true) return;
        var (code, text, hostVersion) = _pendingNotice.Value;
        _pendingNotice = null;
        // Servers before 0.13.2 refuse other builds with the generic code.
        if (code == JoinRejectCode.Version || (code == JoinRejectCode.Generic && hostVersion.Length > 0 && hostVersion != MpPlugin.Version)) UpdatePrompt.VersionRefused(shared, hostVersion, text);
        else if (code == JoinRejectCode.Password) AskPassword(shared, text);
        else
        {
            if (code == JoinRejectCode.Removed)
            {
                try { text = System.Text.Json.JsonSerializer.Deserialize<RemovalNotice>(text)?.Describe(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) ?? text; }
                catch (System.Text.Json.JsonException) { }
            }
            Notice(shared, code == JoinRejectCode.Removed ? (text.StartsWith("You are banned", StringComparison.Ordinal) ? "Banned" : "Kicked") : "Join refused", text);
        }
    }

    public static void Notice(SharedUIComponent shared, string title, string text)
        => shared.ShowDialog(new OneOptionDialogViewModel(new OneOptionDialogDefinition
        {
            Title = OnlineUi.Raw(title), Content = OnlineUi.Raw(text), ConfirmOption = OnlineUi.Raw("OK")
        }));

    private static void AskPassword(SharedUIComponent shared, string why)
    {
        var options = MpPlugin.Controller?.Current;
        if (options?.Join != true) return;
        shared.ShowDialog(new TextInputDialogViewModel(new TwoOptionsDialogDefinition
        {
            Title = OnlineUi.Raw("Server password"), Content = OnlineUi.Raw(why + "\nEnter the password to join."),
            ConfirmOption = OnlineUi.Raw("Join"), CancelOption = OnlineUi.Raw("Cancel")
        }, "", focusText: true)
        {
            InputValidator = text => text.Length > 64 ? "Passwords are at most 64 characters." : null,
            ConfirmAction = text => MpMenu.Queue(() =>
            {
                if (MpPlugin.Controller?.Current != options || MpMenu.Busy) return;
                options.JoinPassword = text;
                MpPlugin.Controller.Request(options, MpClock.NowMs());
            })
        });
    }
}
