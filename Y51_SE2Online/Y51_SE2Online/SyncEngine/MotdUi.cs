using System;
using Keen.Game2.Client.UI.Library.Dialogs.OneOptionDialog;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

/// <summary>Shows the server's message of the day once the joiner has spawned: a native popup plus the same lines in chat.</summary>
internal static class MotdUi
{
    internal static void Tick()
    {
        var client = AdminAccess.Client;
        if (client?.Motd is not { } motd || ClientWorld.Progress.Stage != JoinStage.Playing || client.SpawnMenu != null) return;
        var shared = JoinStatusUi.FindShared(MpPlugin.App);
        if (shared == null) return;
        client.Motd = null;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (string line in motd.Text.Split('\n'))
            if (!string.IsNullOrWhiteSpace(line)) ChatLog.Add(new ChatLine("Server", 0, SocialState.Clean(line, ChatLog.MaxText), ChatKind.System, now), MpClock.NowMs());
        shared.ShowDialog(new OneOptionDialogViewModel(new OneOptionDialogDefinition
        {
            Title = OnlineUi.Raw(string.IsNullOrWhiteSpace(motd.Title) ? "Message of the day" : motd.Title),
            Content = OnlineUi.Raw(motd.Text),
            ConfirmOption = OnlineUi.Raw("OK")
        }));
    }
}
