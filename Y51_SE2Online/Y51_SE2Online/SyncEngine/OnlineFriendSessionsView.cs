using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Y51_SE2Online.Net;
using Y51_SE2Online.Sync;

using Y51_SE2Online.Core;

namespace Y51_SE2Online.SyncEngine;

internal sealed class OnlineFriendSessionsView : UserControl
{
    private readonly TextBox _search = OnlineUi.Input("Search Steam friend or world");
    private readonly ListBox _hosts = OnlineUi.TableList("SteamFriendSessions");
    private readonly TextBlock _count = OnlineUi.Text("", "SubTitle");
    private readonly TextBlock _empty = OnlineUi.Text("");
    private readonly ContentControl _detail = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly Button _join;
    private FriendHost[] _last;
    private FriendHost _shown;
    private TextBlock _shownPing;
    private string _query;
    // Friend, world, ping, version, status, join.
    private const string Columns = "*,*,96,104,140,88";
    private readonly System.Collections.Generic.List<(FriendHost Friend, TextBlock Ping, Button Join, Border Holder)> _rows = new();

    public OnlineFriendSessionsView()
    {
        _hosts.ItemTemplate = new FuncDataTemplate<FriendHost>((friend, _) => friend == null ? null : Row(friend));
        _hosts.SelectionChanged += (_, _) => Detail();
        _hosts.DoubleTapped += (_, args) => { if (!(args.Source is Visual v && v.FindAncestorOfType<Button>(true) != null)) Join(_hosts.SelectedItem as FriendHost); };
        _hosts.KeyDown += (_, args) => { if (args.Key == Key.Enter && _hosts.SelectedItem is FriendHost friend) { args.Handled = true; Join(friend); } };
        _search.Classes.Add("SearchBox");
        _search.TextChanged += (_, _) => Refresh(true);
        var toolbar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,20,Auto") };
        toolbar.Children.Add(_search);
        var refresh = OnlineUi.Button("Refresh friends", MpMenu.RefreshFriends, "RefreshFriendSessions");
        Grid.SetColumn(refresh, 2); toolbar.Children.Add(refresh);
        var roster = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto") };
        roster.Children.Add(toolbar);
        _count.Margin = new Thickness(0, 14); Grid.SetRow(_count, 1); roster.Children.Add(_count);
        var headers = OnlineUi.TableHeader(Columns, OnlineUi.HeaderLabel("STEAM FRIEND", true), OnlineUi.HeaderLabel("WORLD", true), OnlineUi.HeaderLabel("PING"), OnlineUi.HeaderLabel("VERSION"), OnlineUi.HeaderLabel("STATUS"), new Control());
        Grid.SetRow(headers, 2); roster.Children.Add(headers);
        Grid.SetRow(_hosts, 3); roster.Children.Add(_hosts);
        _empty.HorizontalAlignment = HorizontalAlignment.Center; _empty.VerticalAlignment = VerticalAlignment.Center; _empty.TextAlignment = TextAlignment.Center; _empty.IsHitTestVisible = false;
        Grid.SetRow(_empty, 3); roster.Children.Add(_empty);
        var note = OnlineUi.Text("Steam presence finds friends even when they do not publish their session in the public directory.", "SubtleNotice", 22);
        note.Margin = new Thickness(0, 16, 0, 0); Grid.SetRow(note, 4); roster.Children.Add(note);
        _join = OnlineUi.Button("Join friend's world", () => Join(_hosts.SelectedItem as FriendHost), "JoinFriendSession");
        Content = OnlineUi.Columns(OnlineUi.Panel(roster), OnlineUi.Panel(OnlineUi.DetailPanel(_detail, _join), OnlineUi.DetailPadding), "*,24,400");
        Detail();
    }

    private static bool Compatible(FriendHost friend) => friend.Presence?.PluginVersion == MpPlugin.Version && friend.Presence?.SyncVersion == SyncProtocol.Version;
    private static bool CanJoin(FriendHost friend) => !MpMenu.Busy && friend != null && Compatible(friend);
    private static string Reason(FriendHost friend) => Compatible(friend) ? "Multiplayer build matches. Ready to connect." : VersionMismatch.Explain(friend.Presence?.PluginVersion ?? "?", MpPlugin.Version);
    private static string PingText(FriendHost friend) => MpMenu.Ping(friend) is { Length: > 0 } ping ? ping : "-";
    private static void Join(FriendHost friend) { if (CanJoin(friend)) MpMenu.Activate(MpMenu.Settings.JoinOptions(friend.SteamId, false, friend.Presence.VirtualPort)); }

    private Control Row(FriendHost friend)
    {
        var ping = OnlineUi.Text(PingText(friend), null, 24, true);
        var (join, holder) = OnlineUi.RowButton("Join", () => { _hosts.SelectedItem = friend; Join(friend); }, "JoinRow");
        ShowJoin(friend, join, holder);
        if (_rows.Count > 1024) _rows.RemoveRange(0, 512);
        _rows.Add((friend, ping, join, holder));
        return OnlineUi.TableRow(Columns, OnlineUi.Cell(friend.Name, true, true), OnlineUi.Cell(friend.Presence?.WorldName ?? "", true), OnlineUi.Fit(ping, true),
            OnlineUi.Cell(friend.Presence?.PluginVersion ?? "?"), OnlineUi.Cell(Compatible(friend) ? "Available" : "Other version"), holder);
    }

    private static void ShowJoin(FriendHost friend, Button join, Border holder)
    {
        join.IsEnabled = CanJoin(friend);
        ToolTip.SetTip(holder, MpMenu.Busy ? "Wait for the current connection to finish." : Reason(friend));
    }

    public void Refresh(bool force = false)
    {
        foreach (var (friend, ping, join, holder) in _rows) { ping.Text = PingText(friend); ShowJoin(friend, join, holder); }
        string query = _search.Text?.Trim() ?? "";
        if (!force && ReferenceEquals(_last, MpMenu.Hosts) && query == _query) { Detail(); return; }
        _last = MpMenu.Hosts; _query = query;
        ulong selected = (_hosts.SelectedItem as FriendHost)?.SteamId ?? 0;
        var hosts = _last.Where(friend => friend.Hosting && (query.Length == 0 || (friend.Name + " " + friend.Presence.WorldName).Contains(query, StringComparison.OrdinalIgnoreCase))).OrderBy(friend => friend.Name).ToArray();
        _rows.Clear(); _hosts.ItemsSource = hosts; _hosts.SelectedItem = hosts.FirstOrDefault(friend => friend.SteamId == selected);
        _count.Text = hosts.Length + (hosts.Length == 1 ? " friend hosting" : " friends hosting");
        _empty.IsVisible = hosts.Length == 0;
        _empty.Text = !MpPlugin.SteamAvailable ? "Connect to Steam to discover friends' worlds." : query.Length > 0 ? "No friends match your search." : "No Steam friends are hosting.\nAsk a friend to start hosting, then refresh.";
        Detail();
    }

    private void Detail()
    {
        var friend = _hosts.SelectedItem as FriendHost;
        _join.IsEnabled = CanJoin(friend);
        _join.IsVisible = friend != null;
        if (!ReferenceEquals(friend, _shown) || _detail.Content == null)
        {
            _shown = friend;
            _detail.Content = friend == null ? OnlineUi.EmptyState("No friend selected", "Your Steam friends' hosted worlds appear in this list. Select one to see its details and join it.") : Details(friend);
        }
        if (friend != null && _shownPing != null) _shownPing.Text = MpMenu.Ping(friend) is { Length: > 0 } ping ? ping + " (Steam estimate)" : "Unknown";
    }

    private Control Details(FriendHost friend)
    {
        var title = OnlineUi.Text(friend.Name, "SubTitle", 26);
        var kind = OnlineUi.Text("Steam friend hosting", "SubtleNotice", 19); kind.HorizontalAlignment = HorizontalAlignment.Left; kind.TextAlignment = TextAlignment.Left;
        var reason = OnlineUi.Text(Reason(friend), size: 20); reason.FontWeight = Compatible(friend) ? FontWeight.Normal : FontWeight.Bold;
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(title); stack.Children.Add(kind); stack.Children.Add(reason);
        _shownPing = OnlineUi.Value("Unknown");
        stack.Children.Add(OnlineUi.Heading("DETAILS"));
        stack.Children.Add(OnlineUi.Facts(
            ("World", OnlineUi.Value(friend.Presence?.WorldName ?? "")),
            ("Ping", _shownPing),
            ("Version", OnlineUi.Value((friend.Presence?.PluginVersion ?? "?") + (friend.Presence?.PluginVersion != MpPlugin.Version ? " (you have " + MpPlugin.Version + ")" : ""))),
            ("Steam host", OnlineUi.Value(friend.SteamId.ToString()))));
        var note = OnlineUi.Text("Game version and available player slots are not published through Steam presence. They are checked when connecting.", "SubtleNotice", 19);
        note.HorizontalAlignment = HorizontalAlignment.Left; note.TextAlignment = TextAlignment.Left; note.Margin = new Thickness(0, 10, 0, 0);
        stack.Children.Add(note);
        return stack;
    }
}
