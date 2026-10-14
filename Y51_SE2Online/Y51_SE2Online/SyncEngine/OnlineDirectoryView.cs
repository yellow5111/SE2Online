using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

internal sealed class OnlineDirectoryView : UserControl
{
    private readonly bool _servers;
    private readonly TextBox _search = OnlineUi.Input("Search name, world or address");
    private readonly CheckBox _compatible = OnlineUi.Check("Compatible builds");
    private readonly CheckBox _notFull = OnlineUi.Check("Available slots");
    private readonly CheckBox _favorites = OnlineUi.Check("Favorites");
    private readonly ComboBox _sort = new() { ItemsSource = new[] { "Name", "Most players", "Newest" }, SelectedIndex = 0, MinWidth = 210, FontSize = 24 };
    private readonly ListBox _list;
    private readonly TextBlock _count = OnlineUi.Text("");
    private readonly TextBlock _empty = OnlineUi.Text("");
    private readonly TextBlock _title = OnlineUi.Text("Select a server", "SubTitle", 34);
    private readonly TextBlock _world = OnlineUi.Text("");
    private readonly TextBlock _description = OnlineUi.Text("");
    private readonly TextBlock _info = OnlineUi.Text("");
    private readonly TextBlock _build = OnlineUi.Text("");
    private readonly TextBlock _reason = OnlineUi.Text("");
    private readonly TextBox _endpoint = OnlineUi.Input();
    private readonly Button _join;
    private readonly Button _favorite;
    private readonly Button _refresh;
    private readonly Button _serverInfo;
    private DirectoryListing[] _last;
    private string _filter;
    private const string Columns = "*,330,126,214";

    public OnlineDirectoryView(bool servers)
    {
        _servers = servers; Name = servers ? "ServerBrowser" : "SessionBrowser";
        _list = OnlineUi.List(servers ? "ServerList" : "SessionList");
        _list.ItemTemplate = new FuncDataTemplate<DirectoryListing>((entry, _) => entry == null ? null : OnlineUi.ListCells(Columns, entry.Name, entry.WorldName, entry.Players + " / " + entry.MaxPlayers, Availability(entry)));
        _list.SelectionChanged += (_, _) => Detail();
        _list.DoubleTapped += (_, _) => Join();
        _search.Classes.Add("SearchBox");
        _compatible.IsChecked = true;
        _search.TextChanged += (_, _) => Refresh(true);
        foreach (var check in new[] { _compatible, _notFull, _favorites }) check.IsCheckedChanged += (_, _) => Refresh(true);
        _sort.SelectionChanged += (_, _) => Refresh(true);
        _refresh = OnlineUi.Button("Refresh", MpMenu.RefreshDirectory, servers ? "RefreshServers" : "RefreshSessions");
        var toolbar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,20,Auto") };
        toolbar.Children.Add(_search); Grid.SetColumn(_refresh, 2); toolbar.Children.Add(_refresh);
        var filters = OnlineUi.Row(_compatible, _notFull, _favorites, _sort);
        var header = OnlineUi.Cells(Columns, servers ? "SERVER" : "SESSION", "WORLD", "PLAYERS", "STATUS");
        header.Classes.Add("SubTitle");
        var listings = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,*,Auto") };
        listings.Children.Add(toolbar);
        filters.Margin = new Thickness(0, 16); Grid.SetRow(filters, 1); listings.Children.Add(filters);
        Grid.SetRow(_count, 2); listings.Children.Add(_count);
        Grid.SetRow(header, 3); listings.Children.Add(header);
        Grid.SetRow(_list, 4); listings.Children.Add(_list);
        _empty.Margin = new Thickness(20); _empty.IsHitTestVisible = false;
        _empty.HorizontalAlignment = HorizontalAlignment.Center; _empty.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetRow(_empty, 4); listings.Children.Add(_empty);
        var source = OnlineUi.Text(servers ? "Dedicated servers stay online independently of a player." : "Player-hosted sessions. Steam friends can also join through an invitation.", "SubtleNotice", 21);
        source.Margin = new Thickness(0, 12, 0, 0); Grid.SetRow(source, 5); listings.Children.Add(source);
        _endpoint.IsReadOnly = true;
        _join = OnlineUi.Button("Join selected", Join, servers ? "JoinServer" : "JoinSession", true);
        _favorite = OnlineUi.Button("Add favorite", () => { if (_list.SelectedItem is DirectoryListing e) { MpMenu.ToggleFavorite(e); Refresh(true); Detail(); } }, servers ? "FavoriteServer" : "FavoriteSession");
        _serverInfo = OnlineUi.Button("Server info", () => { if (_list.SelectedItem is DirectoryListing e) ServerInfoUi.Show(e.Name, ServerInfoUi.FromListing(e)); }, servers ? "ServerInfoListing" : "SessionInfoListing");
        var detail = OnlineUi.Stack(_title, _world, _description, _info, _build, _endpoint, _reason, _join, _favorite, _serverInfo);
        Content = OnlineUi.Columns(OnlineUi.Panel(listings), OnlineUi.Panel(OnlineUi.Scroll(detail)), "*,24,500");
        Detail();
    }

    private static bool Compatible(DirectoryListing e) => e.MatchesBuild(MpPlugin.Version, MpMenu.GameVersion);
    private static string Availability(DirectoryListing e) => e.PluginVersion != MpPlugin.Version ? "Version " + e.PluginVersion : !Compatible(e) ? "SE2 " + e.GameVersion : e.Players >= e.MaxPlayers ? "Full" : e.FriendsOnly ? "Friends only" : e.Tags?.Contains("Password") == true ? "Password" : e.Tags?.Contains("Whitelist") == true ? "Whitelist" : "Available";
    private void Join() { if (_join.IsEnabled && _list.SelectedItem is DirectoryListing e) MpMenu.JoinListing(e); }

    public void Refresh(bool force = false)
    {
        var source = _servers ? MpMenu.Servers : MpMenu.Sessions;
        _refresh.IsEnabled = !MpMenu.DirectoryBusy;
        _refresh.Content = MpMenu.DirectoryBusy ? "Refreshing..." : "Refresh";
        string query = _search.Text?.Trim() ?? "";
        string filter = query + _compatible.IsChecked + _notFull.IsChecked + _favorites.IsChecked + _sort.SelectedIndex;
        if (!force && ReferenceEquals(source, _last) && filter == _filter) { Detail(); return; }
        _last = source; _filter = filter;
        string selected = (_list.SelectedItem as DirectoryListing)?.Id;
        var filtered = source.Where(e => (query.Length == 0 || (e.Name + " " + e.WorldName + " " + e.Address).Contains(query, StringComparison.OrdinalIgnoreCase))
            && (_compatible.IsChecked != true || Compatible(e)) && (_notFull.IsChecked != true || e.Players < e.MaxPlayers) && (_favorites.IsChecked != true || MpMenu.IsFavorite(e)));
        filtered = _sort.SelectedIndex switch { 1 => filtered.OrderByDescending(e => e.Players).ThenBy(e => e.Name), 2 => filtered.OrderByDescending(e => e.UpdatedAt), _ => filtered.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase) };
        var rows = filtered.ToArray();
        _list.ItemsSource = rows; _list.SelectedItem = rows.FirstOrDefault(e => e.Id == selected);
        _count.Text = rows.Length + (rows.Length == 1 ? " result" : " results") + "   /   " + source.Length + (_servers ? " servers" : " sessions");
        _empty.IsVisible = rows.Length == 0;
        _empty.Text = MpMenu.DirectoryBusy ? "Searching the directory..." : source.Length == 0 ? (_servers ? "No dedicated servers found.\nRefresh or use Direct connect." : "No public sessions found.\nRefresh or ask a friend for an invitation.") : "No results match your filters.\nTry another search or disable a filter.";
        Detail();
    }

    private void Detail()
    {
        var entry = _list.SelectedItem as DirectoryListing;
        _join.IsEnabled = !MpMenu.Busy && entry != null && Compatible(entry) && entry.Players < entry.MaxPlayers;
        _favorite.IsEnabled = entry != null;
        _serverInfo.IsEnabled = entry != null;
        _title.Text = entry?.Name ?? (_servers ? "Select a server" : "Select a session");
        _world.Text = entry == null ? "World details and connection options appear here." : entry.WorldName;
        _description.Text = entry?.Description ?? "";
        _info.Text = entry == null ? "" : "Players: " + entry.Players + " / " + entry.MaxPlayers + "\nAccess: " + (entry.FriendsOnly ? "Steam friends only" : "Public") + (entry.Tags?.Length > 0 ? "\nTags: " + string.Join(", ", entry.Tags) : "");
        _build.Text = entry == null ? "" : "SE2 " + entry.GameVersion + "\nY51_SE2Online " + entry.PluginVersion + (entry.PluginVersion != MpPlugin.Version ? " (you have " + MpPlugin.Version + ")" : "");
        _endpoint.Text = entry == null ? "" : !string.IsNullOrWhiteSpace(entry.Address) ? entry.Address : "Steam host: " + entry.SteamId;
        _endpoint.IsVisible = entry != null;
        _reason.Text = entry == null ? "Select a row to join or save a favorite." : entry.PluginVersion != MpPlugin.Version ? VersionMismatch.Explain(entry.PluginVersion, MpPlugin.Version) : !Compatible(entry) ? "This host runs SE2 " + entry.GameVersion + "; you have " + MpMenu.GameVersion + ". Both need the same game version." : entry.Players >= entry.MaxPlayers ? "This world is full. Refresh to check for an available slot." : entry.FriendsOnly ? "The host accepts Steam friends only." : "Ready to join this world.";
        _favorite.Content = entry != null && MpMenu.IsFavorite(entry) ? "Remove favorite" : "Add favorite";
    }
}

