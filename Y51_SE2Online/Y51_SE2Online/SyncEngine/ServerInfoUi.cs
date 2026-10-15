using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Keen.Game2.Client.UI.Library.Dialogs.OneOptionDialog;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

/// <summary>The Server info dialog: from the directory listing in the browser, from the host in game (F3).</summary>
internal static class ServerInfoUi
{
    private static int _awaiting = -1;
    private static long _askedMs;

    /// <summary>Settings tags the host publishes with its listing (the directory keeps tags; no new listing fields needed).</summary>
    public static string[] ListingTags(HostSession host)
    {
        var tags = new List<string>();
        if (host.HasPassword) tags.Add("Password");
        if (host.Access.WhitelistOnly) tags.Add("Whitelist");
        tags.Add(host.Pvp ? "PvP on" : "PvP off");
        tags.Add(host.OfflineRaidProtection ? "Raid protection on" : "Raid protection off");
        tags.Add(host.PcuInForce == 0 ? "PCU unlimited" : "PCU " + host.PcuInForce.ToString("N0", System.Globalization.CultureInfo.InvariantCulture));
        if (host.StreamingRadiusMeters > 0) tags.Add($"Streaming {host.StreamingRadiusMeters / 1000:0.#} km");
        var restart = Dedicated.DedicatedRuntime.Config?.AutoRestart;
        if (restart?.Times.Length > 0) tags.Add(JoinProgress.Cut("Restarts " + string.Join(" ", restart.Times), 32));
        else if (restart?.EveryHours > 0) tags.Add($"Restarts every {restart.EveryHours:0.#} h");
        return tags.Take(8).ToArray();
    }

    public static ServerInfoRow[] FromListing(DirectoryListing e)
    {
        var rows = new List<ServerInfoRow>
        {
            new("Server", "Name", e.Name),
            new("Server", "World", e.WorldName),
            new("Server", "Type", e.Kind == "dedicated" ? "Dedicated server" : "Player-hosted session"),
            new("Server", "Address", !string.IsNullOrWhiteSpace(e.Address) ? e.Address : "Steam host " + e.SteamId),
            new("Server", "Players", e.Players + " / " + e.MaxPlayers),
            new("Server", "Access", e.FriendsOnly ? "Steam friends only" : "Public"),
            new("Server", "Password", e.Tags?.Contains("Password") == true ? "Yes" : "No"),
            new("Server", "Whitelist only", e.Tags?.Contains("Whitelist") == true ? "Yes" : "No"),
            new("Server", "Version", $"Y51 SE2 Online {e.PluginVersion}, protocol {e.SyncVersion}, SE2 {e.GameVersion}"),
            new("Server", "Listed", e.UpdatedAt == default ? "-" : e.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm")),
        };
        rows.AddRange((e.Tags ?? Array.Empty<string>()).Where(t => t is not ("Password" or "Whitelist")).Take(8).Select(t => new ServerInfoRow("Settings", "Setting", t)));
        if (!string.IsNullOrWhiteSpace(e.Description)) rows.Add(new ServerInfoRow("About", "Description", e.Description.Replace('\n', ' ')));
        return rows.ToArray();
    }

    /// <summary>F3: the host shows its own settings; a joiner asks the host and the dialog opens when the answer arrives.</summary>
    public static void OpenInGame()
    {
        if (AdminAccess.Hosting) { Show(AdminAccess.ServerName, AdminAccess.Host.ServerInfoRows()); return; }
        var client = AdminAccess.Client;
        if (client == null || !AdminAccess.Joined) { MpMenu.Report("Join a multiplayer world to see its settings."); return; }
        _awaiting = client.ServerInfo.Version;
        _askedMs = MpClock.NowMs();
        client.RequestServerInfo();
    }

    /// <summary>Called while the Players screen is open.</summary>
    public static void Tick()
    {
        if (_awaiting < 0) return;
        var client = AdminAccess.Client;
        if (client == null || MpClock.NowMs() - _askedMs > 10000) { _awaiting = -1; return; }
        if (client.ServerInfo.Version == _awaiting) return;
        _awaiting = -1;
        var rows = client.ServerInfo.Rows.ToList();
        if (client.InputRttMs >= 0) rows.Insert(0, new ServerInfoRow("Connection", "Your ping", $"{client.InputRttMs:0} ms"));
        Show(AdminAccess.ServerName, rows);
    }

    private const string TitlePrefix = "Server info - ";
    private static bool _fitInstalled;

    public static void Show(string title, IEnumerable<ServerInfoRow> rows) => MpMenu.Queue(() =>
    {
        var shared = JoinStatusUi.FindShared(MpPlugin.App); if (shared == null) return;
        if (!_fitInstalled) { _fitInstalled = true; Control.LoadedEvent.AddClassHandler<OneOptionDialogControl>((dialog, _) => Fit(dialog)); }
        shared.ShowDialog(new OneOptionDialogViewModel(new OneOptionDialogDefinition
        {
            Title = OnlineUi.Raw(TitlePrefix + ServerInfo.Clean(title, 60)),
            Content = OnlineUi.Raw(ServerInfo.Text(rows)),
            ConfirmOption = OnlineUi.Raw("Close")
        }));
    });

    /// <summary>
    /// SE2's one-option dialog grows with its text and has no scrolling, so a long settings list ran off the screen.
    /// Our dialog's text goes into a scroll viewer capped to the part of the screen the dialog can use.
    /// </summary>
    private static void Fit(OneOptionDialogControl dialog)
    {
        try
        {
            if (dialog.Content is not Grid grid) return;
            var title = grid.Children.OfType<TextBlock>().FirstOrDefault(t => Grid.GetRow(t) == 0);
            var body = grid.Children.OfType<TextBlock>().FirstOrDefault(t => Grid.GetRow(t) == 1);
            if (body == null || title?.Text?.StartsWith(TitlePrefix, StringComparison.OrdinalIgnoreCase) != true) return;
            int index = grid.Children.IndexOf(body);
            grid.Children.Remove(body);
            body.TextAlignment = TextAlignment.Left; body.HorizontalAlignment = HorizontalAlignment.Left;
            var scroll = new ScrollViewer { Content = body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, AllowAutoHide = false };
            Grid.SetRow(scroll, 1);
            grid.Children.Insert(index, scroll);
            // The UI may be scaled to the window, so the screen height is measured in the dialog's own units.
            if (TopLevel.GetTopLevel(dialog) is { } top && dialog.TransformToVisual(top) is { } toScreen && toScreen.M22 > 0)
                scroll.MaxHeight = Math.Max(240, top.ClientSize.Height / toScreen.M22 * 0.5);
            else scroll.MaxHeight = 520;
        }
        catch (Exception e) { GameLog.Instance.Warn("server info: dialog layout failed: " + e.Message); }
    }
}
