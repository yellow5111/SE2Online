using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Y51_SE2Online.Net;
using Y51_SE2Online.Sync;

using Y51_SE2Online.Core;

namespace Y51_SE2Online.SyncEngine;

internal sealed class OnlineFriendSessionsView : UserControl
{
    private readonly TextBox _search = OnlineUi.Input("Search Steam friend or world");
    private readonly ListBox _hosts = OnlineUi.List("SteamFriendSessions");
    private readonly TextBlock _count = OnlineUi.Text("", "SubTitle");
    private readonly TextBlock _empty = OnlineUi.Text("");
    private readonly TextBlock _title = OnlineUi.Text("Select a friend", "SubTitle", 34);
    private readonly TextBlock _world = OnlineUi.Text("");
    private readonly TextBlock _build = OnlineUi.Text("");
    private readonly TextBlock _reason = OnlineUi.Text("");
    private readonly Button _join;
    private FriendHost[] _last;
    private string _query;
    private const string Columns = "*,*,140,320";

    public OnlineFriendSessionsView()
    {
        _hosts.ItemTemplate = new FuncDataTemplate<FriendHost>((friend, _) => friend == null ? null : OnlineUi.ListCells(Columns, friend.Name, friend.Presence?.WorldName ?? "", MpMenu.Ping(friend) is { Length: > 0 } ping ? ping : "-", Compatible(friend) ? "Multiplayer build matches" : "Version " + (friend.Presence?.PluginVersion ?? "?")));
        _hosts.SelectionChanged += (_, _) => Detail();
        _hosts.DoubleTapped += (_, _) => Join();
        _search.Classes.Add("SearchBox");
        _search.TextChanged += (_, _) => Refresh(true);
        var toolbar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,20,Auto") };
        toolbar.Children.Add(_search);
        var refresh = OnlineUi.Button("Refresh friends", MpMenu.RefreshFriends, "RefreshFriendSessions");
        Grid.SetColumn(refresh, 2); toolbar.Children.Add(refresh);
        var roster = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto") };
        roster.Children.Add(toolbar);
        _count.Margin = new Thickness(0, 14); Grid.SetRow(_count, 1); roster.Children.Add(_count);
        var headers = OnlineUi.Cells(Columns, "STEAM FRIEND", "WORLD", "PING", "MULTIPLAYER BUILD"); Grid.SetRow(headers, 2); roster.Children.Add(headers);
        Grid.SetRow(_hosts, 3); roster.Children.Add(_hosts);
        _empty.HorizontalAlignment = HorizontalAlignment.Center; _empty.VerticalAlignment = VerticalAlignment.Center; _empty.IsHitTestVisible = false;
        Grid.SetRow(_empty, 3); roster.Children.Add(_empty);
        var note = OnlineUi.Text("Steam presence finds friends even when they do not publish their session in the public directory.", "SubtleNotice", 22);
        note.Margin = new Thickness(0, 16, 0, 0); Grid.SetRow(note, 4); roster.Children.Add(note);
        _join = OnlineUi.Button("Join friend's world", Join, "JoinFriendSession", true);
        var details = OnlineUi.Stack(OnlineUi.Fit(_title), OnlineUi.Fit(_world), OnlineUi.Fit(_build),
            OnlineUi.Text("Game version and available player slots are not published through Steam presence. They are checked when connecting.", "SubtleNotice", 22), _reason, _join);
        Content = OnlineUi.Columns(OnlineUi.Panel(roster), OnlineUi.Panel(OnlineUi.Scroll(details)), "*,24,500");
        Detail();
    }
    private static bool Compatible(FriendHost friend) => friend.Presence?.PluginVersion == MpPlugin.Version && friend.Presence?.SyncVersion == SyncProtocol.Version;
    private void Join() { if (_join.IsEnabled && _hosts.SelectedItem is FriendHost friend) MpMenu.Activate(MpMenu.Settings.JoinOptions(friend.SteamId, false, friend.Presence.VirtualPort)); }
    public void Refresh(bool force = false)
    {
        string query = _search.Text?.Trim() ?? "";
        if (!force && ReferenceEquals(_last, MpMenu.Hosts) && query == _query) { Detail(); return; }
        _last = MpMenu.Hosts; _query = query;
        ulong selected = (_hosts.SelectedItem as FriendHost)?.SteamId ?? 0;
        var hosts = _last.Where(friend => friend.Hosting && (query.Length == 0 || (friend.Name + " " + friend.Presence.WorldName).Contains(query, StringComparison.OrdinalIgnoreCase))).OrderBy(friend => friend.Name).ToArray();
        _hosts.ItemsSource = hosts; _hosts.SelectedItem = hosts.FirstOrDefault(friend => friend.SteamId == selected);
        _count.Text = hosts.Length + (hosts.Length == 1 ? " friend hosting" : " friends hosting");
        _empty.IsVisible = hosts.Length == 0;
        _empty.Text = !MpPlugin.SteamAvailable ? "Connect to Steam to discover friends' worlds." : query.Length > 0 ? "No friends match your search." : "No Steam friends are hosting.\nAsk a friend to start hosting, then refresh.";
        Detail();
    }
    private void Detail()
    {
        var friend = _hosts.SelectedItem as FriendHost;
        _join.IsEnabled = !MpMenu.Busy && friend != null && Compatible(friend);
        _title.Text = friend?.Name ?? "Select a friend";
        _world.Text = friend?.Presence.WorldName ?? "Your friends' hosted worlds appear in this list.";
        _build.Text = friend == null ? "" : "Y51_SE2Online " + friend.Presence.PluginVersion + (friend.Presence.PluginVersion != MpPlugin.Version ? " (you have " + MpPlugin.Version + ")" : "") + "\nSteam host: " + friend.SteamId + "\nPing: " + (MpMenu.Ping(friend) is { Length: > 0 } ping ? ping + " (Steam estimate)" : "Unknown");
        _reason.Text = friend == null ? "Select a friend to join their world." : Compatible(friend) ? "Multiplayer build matches. Ready to connect." : VersionMismatch.Explain(friend.Presence?.PluginVersion ?? "?", MpPlugin.Version);
    }
}
