using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
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
    private readonly ComboBox _sort = new() { ItemsSource = new[] { "Name", "Most players", "Newest", "Country", "Lowest ping", "PvE first", "PvP first" }, SelectedIndex = 0, MinWidth = 210, FontSize = 24 };
    private readonly ListBox _list;
    private readonly TextBlock _count = OnlineUi.Text("");
    private readonly TextBlock _empty = OnlineUi.Text("");
    private readonly ContentControl _detail = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly Button _join;
    private readonly Button _refresh;
    private readonly Button _serverInfo;
    private DirectoryListing[] _last;
    private DirectoryListing _shown;
    private TextBlock _shownPing;
    private long _nextPingSort;
    private string _filter;
    // Star, country, name, world, mode, players, ping, version (plugin over SE2), password (lock and whitelist shield icons), status, join.
    // Narrow fixed columns leave the names room for the directory's longest (100 characters) on three lines.
    private const string Columns = "36,80,*,*,60,76,66,82,84,96,80";
    private readonly System.Collections.Generic.List<(DirectoryListing Entry, TextBlock Ping, Button Join, Border Holder)> _rows = new();

    public OnlineDirectoryView(bool servers)
    {
        _servers = servers; Name = servers ? "ServerBrowser" : "SessionBrowser";
        _list = OnlineUi.TableList(servers ? "ServerList" : "SessionList");
        _list.ItemTemplate = new FuncDataTemplate<DirectoryListing>((entry, _) => entry == null ? null : Row(entry));
        _list.SelectionChanged += (_, _) => Detail();
        _list.DoubleTapped += (_, args) => { if (!FromButton(args.Source)) Join(_list.SelectedItem as DirectoryListing); };
        _list.KeyDown += (_, args) => { if (args.Key == Key.Enter && _list.SelectedItem is DirectoryListing e) { args.Handled = true; Join(e); } };
        _search.Classes.Add("SearchBox");
        _compatible.IsChecked = true;
        _search.TextChanged += (_, _) => Refresh(true);
        foreach (var check in new[] { _compatible, _notFull, _favorites }) check.IsCheckedChanged += (_, _) => Refresh(true);
        _sort.SelectionChanged += (_, _) => Refresh(true);
        _refresh = OnlineUi.Button("Refresh", MpMenu.RefreshDirectory, servers ? "RefreshServers" : "RefreshSessions");
        var toolbar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,20,Auto") };
        toolbar.Children.Add(_search); Grid.SetColumn(_refresh, 2); toolbar.Children.Add(_refresh);
        var filters = OnlineUi.Row(_compatible, _notFull, _favorites, _sort);
        var header = OnlineUi.TableHeader(Columns, new Control(), OnlineUi.HeaderLabel("COUNTRY"), OnlineUi.HeaderLabel(servers ? "SERVER" : "SESSION", true), OnlineUi.HeaderLabel("WORLD", true),
            OnlineUi.HeaderLabel("MODE"), OnlineUi.HeaderLabel("PLAYERS"), OnlineUi.HeaderLabel("PING"), OnlineUi.HeaderLabel("VERSION"), OnlineUi.HeaderLabel("PASSWORD"), OnlineUi.HeaderLabel("STATUS"), new Control());
        var listings = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,*,Auto") };
        listings.Children.Add(toolbar);
        filters.Margin = new Thickness(0, 16); Grid.SetRow(filters, 1); listings.Children.Add(filters);
        _count.Margin = new Thickness(0, 0, 0, 12); Grid.SetRow(_count, 2); listings.Children.Add(_count);
        Grid.SetRow(header, 3); listings.Children.Add(header);
        Grid.SetRow(_list, 4); listings.Children.Add(_list);
        _empty.Margin = new Thickness(20); _empty.IsHitTestVisible = false;
        _empty.HorizontalAlignment = HorizontalAlignment.Center; _empty.VerticalAlignment = VerticalAlignment.Center; _empty.TextAlignment = TextAlignment.Center;
        Grid.SetRow(_empty, 4); listings.Children.Add(_empty);
        var source = OnlineUi.Text(servers ? "Dedicated servers stay online independently of a player." : "Player-hosted sessions. Steam friends can also join through an invitation.", "SubtleNotice", 21);
        source.Margin = new Thickness(0, 12, 0, 0); Grid.SetRow(source, 5); listings.Children.Add(source);
        _join = OnlineUi.Button(servers ? "Join server" : "Join session", () => Join(_list.SelectedItem as DirectoryListing), servers ? "JoinServer" : "JoinSession");
        _serverInfo = OnlineUi.Button("Server info", () => { if (_list.SelectedItem is DirectoryListing e) ServerInfoUi.Show(e.Name, ServerInfoUi.FromListing(e)); }, servers ? "ServerInfoListing" : "SessionInfoListing");
        Content = OnlineUi.Columns(OnlineUi.Panel(listings), OnlineUi.Panel(OnlineUi.DetailPanel(_detail, _join, _serverInfo), OnlineUi.DetailPadding), "*,24,400");
        Detail();
    }

    private static bool FromButton(object source) => source is Visual v && v.FindAncestorOfType<Button>(true) != null;
    private static bool Compatible(DirectoryListing e) => e.MatchesBuild(MpPlugin.Version, MpMenu.GameVersion);
    private static bool Tagged(DirectoryListing e, string tag) => e.Tags?.Contains(tag) == true;
    // The chosen mode first, then the other, then listings too old to say.
    private static int ModeRank(DirectoryListing e, bool pvpFirst) => DirectoryListing.Mode(e.Tags) switch { "PvP" => pvpFirst ? 0 : 1, "PvE" => pvpFirst ? 1 : 0, _ => 2 };
    // Version, game build, password and whitelist have their own columns; status says whether this player can get in.
    private static string Status(DirectoryListing e) => !Compatible(e) ? "Other version" : e.Players >= e.MaxPlayers ? "Full" : e.FriendsOnly ? "Friends only" : "Available";
    private static bool CanJoin(DirectoryListing e) => !MpMenu.Busy && e != null && Compatible(e) && e.Players < e.MaxPlayers;
    private static string Reason(DirectoryListing e) => e.PluginVersion != MpPlugin.Version ? VersionMismatch.Explain(e.PluginVersion, MpPlugin.Version)
        : !Compatible(e) ? "This host runs SE2 " + e.GameVersion + "; you have " + MpMenu.GameVersion + ". Both need the same game version."
        : e.Players >= e.MaxPlayers ? "This world is full. Refresh to check for an available slot."
        : e.FriendsOnly ? "The host accepts Steam friends only." : "Ready to join this world.";
    private static string PingText(DirectoryListing e) => MpMenu.Ping(e) is { Length: > 0 } p ? p : "-";

    private Control Row(DirectoryListing e)
    {
        var ping = OnlineUi.Text(PingText(e), null, 24, true);
        var (join, holder) = OnlineUi.RowButton("Join", () => { _list.SelectedItem = e; Join(e); }, "JoinRow");
        ShowJoin(e, join, holder);
        // ponytail: rows rebuilt while scrolling add again; trimmed, not tracked per row.
        if (_rows.Count > 1024) _rows.RemoveRange(0, 512);
        _rows.Add((e, ping, join, holder));
        var country = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        country.Children.Add(CountryFlags.Icon(e.Country));
        if (!string.IsNullOrEmpty(e.Country)) country.Children.Add(OnlineUi.Text(e.Country, null, 20, true));
        var version = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        version.Children.Add(OnlineUi.Cell(e.PluginVersion));
        version.Children.Add(OnlineUi.Cell("SE2 " + e.GameVersion, size: 16));
        return OnlineUi.TableRow(Columns, Star(e), country, OnlineUi.Cell(e.Name, true, true), OnlineUi.Cell(e.WorldName, true), OnlineUi.Cell(DirectoryListing.Mode(e.Tags)), OnlineUi.Cell(e.Players + " / " + e.MaxPlayers),
            OnlineUi.Fit(ping, true), version, Access(e), OnlineUi.Cell(Status(e)), holder);
    }

    private static Control Access(DirectoryListing e)
    {
        var icons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        if (Tagged(e, "Password")) icons.Children.Add(OnlineUi.Icon(OnlineUi.LockShape, 26, "Password required"));
        if (Tagged(e, "Whitelist")) icons.Children.Add(OnlineUi.Icon(OnlineUi.ShieldShape, 28, "Whitelisted players only"));
        return icons;
    }

    private static void ShowJoin(DirectoryListing e, Button join, Border holder)
    {
        join.IsEnabled = CanJoin(e);
        ToolTip.SetTip(holder, MpMenu.Busy ? "Wait for the current connection to finish." : Reason(e));
    }

    private static readonly Geometry StarShape = Geometry.Parse("M12,2 L14.9,8.6 L22,9.3 L16.6,14 L18.2,21 L12,17.3 L5.8,21 L7.4,14 L2,9.3 L9.1,8.6 Z");
    private static readonly IBrush StarOn = new SolidColorBrush(Color.FromRgb(0xE5, 0x8C, 0x12)), StarOff = new SolidColorBrush(Color.FromRgb(0x8E, 0xA3, 0xB0));

    /// <summary>The favorite toggle at the start of a row: a filled amber star when saved, an outline when not.</summary>
    private Button Star(DirectoryListing e)
    {
        var shape = new Path { Data = StarShape, Stretch = Stretch.Uniform, Width = 28, Height = 28, StrokeThickness = 1.8 };
        var star = new Button { Name = "FavoriteStar", Content = shape, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(4), MinWidth = 0, MinHeight = 0, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Cursor = new Cursor(StandardCursorType.Hand) };
        void Show()
        {
            bool on = MpMenu.IsFavorite(e);
            shape.Fill = on ? StarOn : Brushes.Transparent;
            shape.Stroke = on ? StarOn : StarOff;
            ToolTip.SetTip(star, on ? "Remove from favorites" : "Add to favorites");
        }
        star.Click += (_, args) =>
        {
            args.Handled = true;
            MpMenu.ToggleFavorite(e);
            Show();
            if (_favorites.IsChecked == true) Refresh(true);
        };
        Show();
        return star;
    }

    private static void Join(DirectoryListing e) { if (CanJoin(e)) MpMenu.JoinListing(e); }

    public void Refresh(bool force = false)
    {
        var source = _servers ? MpMenu.Servers : MpMenu.Sessions;
        _refresh.IsEnabled = !MpMenu.DirectoryBusy;
        _refresh.Content = MpMenu.DirectoryBusy ? "Refreshing..." : "Refresh";
        string query = _search.Text?.Trim() ?? "";
        string filter = query + _compatible.IsChecked + _notFull.IsChecked + _favorites.IsChecked + _sort.SelectedIndex;
        // Pings and whether joining is possible change every few seconds; only those cells update, so the list keeps its scroll position and selection.
        foreach (var (entry, ping, join, holder) in _rows) { ping.Text = PingText(entry); ShowJoin(entry, join, holder); }
        bool resort = _sort.SelectedIndex == 4 && MpClock.NowMs() >= _nextPingSort;
        if (!force && ReferenceEquals(source, _last) && filter == _filter && !resort) { Detail(); return; }
        _nextPingSort = MpClock.NowMs() + 5000;
        _last = source; _filter = filter;
        string selected = (_list.SelectedItem as DirectoryListing)?.Id;
        var filtered = source.Where(e => (query.Length == 0 || (e.Name + " " + e.WorldName + " " + DirectoryListing.Mode(e.Tags) + " " + e.Address + " " + e.Country + " " + Countries.Name(e.Country)).Contains(query, StringComparison.OrdinalIgnoreCase))
            && (_compatible.IsChecked != true || Compatible(e)) && (_notFull.IsChecked != true || e.Players < e.MaxPlayers) && (_favorites.IsChecked != true || MpMenu.IsFavorite(e)));
        filtered = _sort.SelectedIndex switch { 1 => filtered.OrderByDescending(e => e.Players).ThenBy(e => e.Name), 2 => filtered.OrderByDescending(e => e.UpdatedAt), 3 => filtered.OrderBy(e => string.IsNullOrEmpty(e.Country) ? "~" : Countries.Name(e.Country), StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase), 4 => filtered.OrderBy(MpMenu.PingSort).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase), 5 or 6 => filtered.OrderBy(e => ModeRank(e, _sort.SelectedIndex == 6)).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase), _ => filtered.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase) };
        var rows = filtered.ToArray();
        if (!rows.SequenceEqual(_list.ItemsSource as DirectoryListing[] ?? Array.Empty<DirectoryListing>())) { _rows.Clear(); _list.ItemsSource = rows; }
        _list.SelectedItem = rows.FirstOrDefault(e => e.Id == selected);
        _count.Text = rows.Length + (rows.Length == 1 ? " result" : " results") + "   /   " + source.Length + (_servers ? " servers" : " sessions");
        _empty.IsVisible = rows.Length == 0;
        _empty.Text = MpMenu.DirectoryBusy ? "Searching the directory..." : source.Length == 0 ? (_servers ? "No dedicated servers found.\nRefresh or use Direct connect." : "No public sessions found.\nRefresh or ask a friend for an invitation.") : "No results match your filters.\nTry another search or disable a filter.";
        Detail();
    }

    private void Detail()
    {
        var entry = _list.SelectedItem as DirectoryListing;
        _join.IsEnabled = CanJoin(entry);
        _join.IsVisible = _serverInfo.IsVisible = entry != null;
        _serverInfo.IsEnabled = entry != null;
        if (!ReferenceEquals(entry, _shown) || _detail.Content == null)
        {
            _shown = entry;
            _detail.Content = entry == null ? OnlineUi.EmptyState(_servers ? "No server selected" : "No session selected", "Select a row to see its details, join it or save it as a favorite.") : Details(entry);
        }
        if (entry != null && _shownPing != null) _shownPing.Text = MpMenu.Ping(entry) is { Length: > 0 } ping ? ping : "Unknown";
    }

    private Control Details(DirectoryListing e)
    {
        var title = OnlineUi.Text(e.Name, "SubTitle", 26);
        var kind = OnlineUi.Text(e.Kind == "dedicated" ? "Dedicated server" : "Player-hosted session", "SubtleNotice", 19);
        kind.HorizontalAlignment = HorizontalAlignment.Left; kind.TextAlignment = TextAlignment.Left;
        var reason = OnlineUi.Text(Reason(e), size: 20); reason.FontWeight = CanJoin(e) ? FontWeight.Normal : FontWeight.Bold;
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(title); stack.Children.Add(kind); stack.Children.Add(reason);
        if (!string.IsNullOrWhiteSpace(e.Description)) { var about = OnlineUi.Value(e.Description, 20); about.Margin = new Thickness(0, 8, 0, 0); stack.Children.Add(about); }

        _shownPing = OnlineUi.Value(PingText(e));
        // A grid, not a horizontal StackPanel: a long country name wraps beside the flag instead of running off the panel.
        var country = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        var countryName = OnlineUi.Value(string.IsNullOrEmpty(e.Country) ? "Unknown" : Countries.Name(e.Country));
        if (!string.IsNullOrEmpty(e.Country)) { var flag = CountryFlags.Icon(e.Country, 22); flag.Margin = new Thickness(0, 0, 10, 0); flag.VerticalAlignment = VerticalAlignment.Top; country.Children.Add(flag); }
        Grid.SetColumn(countryName, 1); country.Children.Add(countryName);
        string mode = DirectoryListing.Mode(e.Tags) switch { "PvP" => "PvP: players can hurt each other", "PvE" => "PvE: players can't hurt each other", _ => "Not published" };
        var settings = (e.Tags ?? Array.Empty<string>()).Where(t => t is not ("Password" or "Whitelist") && DirectoryListing.ModeOf(t) == null).ToArray();
        stack.Children.Add(OnlineUi.Heading("DETAILS"));
        stack.Children.Add(OnlineUi.Facts(
            ("Players", OnlineUi.Value(e.Players + " / " + e.MaxPlayers)),
            ("Ping", _shownPing),
            ("Mode", OnlineUi.Value(mode)),
            ("World", OnlineUi.Value(e.WorldName)),
            ("Country", country),
            ("Access", OnlineUi.Value(e.FriendsOnly ? "Steam friends only" : "Public")),
            ("Password", OnlineUi.Value(Tagged(e, "Password") ? "Required" : "None")),
            ("Whitelist", OnlineUi.Value(Tagged(e, "Whitelist") ? "Whitelisted players only" : "Off")),
            ("Version", OnlineUi.Value(e.PluginVersion + (e.PluginVersion != MpPlugin.Version ? " (you have " + MpPlugin.Version + ")" : ""))),
            ("SE2", OnlineUi.Value(e.GameVersion + (e.GameVersion != MpMenu.GameVersion ? " (you have " + MpMenu.GameVersion + ")" : ""))),
            ("Address", OnlineUi.Value(!string.IsNullOrWhiteSpace(e.Address) ? e.Address : "Steam host " + e.SteamId)),
            ("Settings", settings.Length == 0 ? null : OnlineUi.Value(string.Join("\n", settings)))));

        stack.Children.Add(OnlineUi.Heading("MODS  /  " + ListingMod.Total(e.Mods)));
        if (e.Mods == null || e.Mods.Length == 0) stack.Children.Add(OnlineUi.Value("This world runs without mods.", 20));
        foreach (var mod in e.Mods ?? Array.Empty<ListingMod>())
        {
            var line = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var name = new StackPanel();
            name.Children.Add(OnlineUi.Value(mod.Name, 20));
            var id = OnlineUi.Value(mod.Id == "0" ? "Local mod" : "Workshop " + mod.Id, 16); id.Opacity = 0.65; name.Children.Add(id);
            line.Children.Add(name);
            if (mod.Size > 0) { var size = OnlineUi.Text(ModDownloads.Size((ulong)mod.Size), size: 20); size.VerticalAlignment = VerticalAlignment.Top; size.Margin = new Thickness(12, 0, 0, 0); Grid.SetColumn(size, 1); line.Children.Add(size); }
            stack.Children.Add(line);
        }
        return stack;
    }
}
