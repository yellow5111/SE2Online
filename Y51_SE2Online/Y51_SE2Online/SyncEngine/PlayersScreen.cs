using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.Styling;
using Keen.VRage.UI.Screens;
using Y51_SE2Online.Core;
using Y51_SE2Online.Net;
using Y51_SE2Online.NetHost;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

public sealed class PlayersScreenViewModel : ScreenViewModel
{
    public event Action Refresh;
    private long _nextRefresh;
    public PlayersScreenViewModel()
    {
        KeepsOtherScreensVisible = false;
        AllowsInputFromLowerScreens = false;
        InitializeInputContext();
        OnTick += () => { if (MpClock.NowMs() < _nextRefresh) return; _nextRefresh = MpClock.NowMs() + 500; Refresh?.Invoke(); };
    }
    public void Close() => SignalWantsClose();
}

public sealed class PlayersScreen : ScreenView
{
    private sealed record Duration(string Label, int Minutes) { public override string ToString() => Label; }
    private sealed record BanRow(BanEntry Ban, string Remaining);
    private static readonly Duration[] Durations = { new("Permanent", 0), new("15 minutes", 15), new("1 hour", 60), new("1 day", 1440), new("7 days", 10080), new("30 days", 43200) };
    private readonly ListBox _players = OnlineUi.List("ConnectedPlayers");
    private readonly ListBox _friends = OnlineUi.List("InviteFriends");
    private readonly ListBox _bans = OnlineUi.List("BannedPlayers");
    private readonly ListBox _whitelist = OnlineUi.List("WhitelistedPlayers");
    private readonly TextBlock _status = OnlineUi.Text("", "SubTitle");
    private readonly TextBlock _message = OnlineUi.Text("", "SubtleNotice", 21);
    private readonly TextBlock _count = OnlineUi.Text("", "SubTitle", 30);
    private readonly TextBlock _emptyPlayers = OnlineUi.Text("No multiplayer session is active.");
    private readonly TextBlock _emptyFriends = OnlineUi.Text("No Steam friends found. Refresh to try again.");
    private readonly TextBlock _selected = OnlineUi.Text("Select a player.");
    private readonly TextBlock _moderationNote = OnlineUi.Text("", "SubtleNotice", 22);
    private readonly TextBlock _accessNote = OnlineUi.Text("", "SubtleNotice", 22);
    private readonly TextBox _reason = OnlineUi.Input("Reason (shown to the player)", 200);
    private readonly ComboBox _duration = new() { ItemsSource = Durations, SelectedIndex = 0, MinHeight = 48, MinWidth = 260, FontSize = 24 };
    private readonly OnlineToggle _whitelistOnly = new("Only whitelisted players and admins may join");
    private readonly Button _kick, _ban, _admin, _whitelistButton, _unban, _unlist, _invite, _refreshFriends;
    private readonly Button[] _tabs;
    private readonly Control[] _pages;
    private readonly ContentControl _page = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    private PlayersScreenViewModel _model;
    private bool _closed, _syncingToggle;
    private FriendHost[] _lastFriends;
    private string _signature, _accessSignature;
    private AdminState _state = new();

    public PlayersScreen()
    {
        OnDisposing += _ => { _closed = true; if (_model != null) _model.Refresh -= QueueRefresh; };
        _players.ItemTemplate = new FuncDataTemplate<AccessPlayer>((p, _) => p == null ? null : OnlineUi.ListCells("*,150,110,200", p.Name, RoleText(p.Role), p.PingMs >= 0 && p.Connected ? p.PingMs + " ms" : "", p.Connected ? "Connected" : "Away"));
        _friends.ItemTemplate = new FuncDataTemplate<FriendHost>((p, _) => p == null ? null : OnlineUi.ListCells("*,230", p.Name, p.Presence != null ? "Hosting" : "Steam friend"));
        _bans.ItemTemplate = new FuncDataTemplate<BanRow>((b, _) => b == null ? null : OnlineUi.ListCells("*,190,*", Display(b.Ban.Name, b.Ban.SteamId), b.Remaining, b.Ban.Reason));
        _whitelist.ItemTemplate = new FuncDataTemplate<AccessEntry>((w, _) => w == null ? null : OnlineUi.ListCells("*,300", Display(w.Name, w.SteamId), w.SteamId.ToString()));
        _kick = OnlineUi.Button("Kick", () => Moderate(AdminAction.Kick), "KickPlayer");
        _ban = OnlineUi.Button("Ban", () => Moderate(AdminAction.Ban), "BanPlayer");
        _admin = OnlineUi.Button("Make admin", () => { if (Selected is { } p) MpMenu.Admin(new AdminRequest(p.Role == OnlineRole.Admin ? AdminAction.Demote : AdminAction.Promote, p.SteamId)); }, "TogglePlayerAdmin");
        _whitelistButton = OnlineUi.Button("Whitelist", () => { if (Selected is { } p) MpMenu.Admin(new AdminRequest(Listed(p.SteamId) ? AdminAction.WhitelistRemove : AdminAction.WhitelistAdd, p.SteamId)); }, "TogglePlayerWhitelist");
        _unban = OnlineUi.Button("Remove ban", () => { if (_bans.SelectedItem is BanRow b) MpMenu.Admin(new AdminRequest(AdminAction.Unban, b.Ban.SteamId)); }, "UnbanPlayer");
        _unlist = OnlineUi.Button("Remove from whitelist", () => { if (_whitelist.SelectedItem is AccessEntry w) MpMenu.Admin(new AdminRequest(AdminAction.WhitelistRemove, w.SteamId)); }, "UnlistPlayer");
        _invite = OnlineUi.Button("Invite selected friend", () => { if (_friends.SelectedItem is FriendHost friend) MpMenu.Invite(friend.SteamId); }, "InviteFriend", true);
        _refreshFriends = OnlineUi.Button("Refresh friends", MpMenu.RefreshFriends, "RefreshInviteFriends");
        _whitelistOnly.IsCheckedChanged += (_, _) => { if (!_syncingToggle) MpMenu.Admin(new AdminRequest(AdminAction.WhitelistOnly, Value: _whitelistOnly.IsChecked)); };
        _players.SelectionChanged += (_, _) => RefreshActions();
        _friends.SelectionChanged += (_, _) => RefreshActions();
        _bans.SelectionChanged += (_, _) => RefreshActions();
        _whitelist.SelectionChanged += (_, _) => RefreshActions();

        var roster = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*") };
        roster.Children.Add(_count);
        var headers = OnlineUi.Cells("*,150,110,200", "PLAYER", "ROLE", "PING", "CONNECTION"); Grid.SetRow(headers, 1); roster.Children.Add(headers);
        Grid.SetRow(_players, 2); roster.Children.Add(_players);
        _emptyPlayers.VerticalAlignment = VerticalAlignment.Center; _emptyPlayers.HorizontalAlignment = HorizontalAlignment.Center; _emptyPlayers.IsHitTestVisible = false;
        Grid.SetRow(_emptyPlayers, 2); roster.Children.Add(_emptyPlayers);

        var moderation = OnlineUi.Stack(OnlineUi.Text("MODERATION", "SubTitle", 30), _selected, _reason, OnlineUi.Row(OnlineUi.Text("Ban length", "SubTitle"), _duration),
            OnlineUi.Row(_kick, _ban), OnlineUi.Row(_admin, _whitelistButton), _moderationNote);
        moderation.Spacing = 14;
        var banGrid = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto,Auto,Auto,*,Auto,Auto") };
        void Add(Control c, int row) { Grid.SetRow(c, row); banGrid.Children.Add(c); }
        Add(_whitelistOnly, 0); Add(_accessNote, 1);
        Add(OnlineUi.Cells("*,190,*", "BANNED", "REMAINING", "REASON"), 2); Add(_bans, 3); Add(_unban, 4);
        var whitelistTitle = OnlineUi.Text("WHITELIST", "SubTitle", 26); whitelistTitle.Margin = new Thickness(0, 16, 0, 0);
        Add(whitelistTitle, 5); Add(OnlineUi.Cells("*,300", "PLAYER", "STEAMID"), 6); Add(_whitelist, 7); Add(_unlist, 8);
        var friends = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto") };
        friends.Children.Add(OnlineUi.Row(OnlineUi.Text("INVITE FRIENDS", "SubTitle", 30), _refreshFriends));
        var friendHeaders = OnlineUi.Cells("*,230", "FRIEND", "STATUS"); Grid.SetRow(friendHeaders, 1); friends.Children.Add(friendHeaders);
        Grid.SetRow(_friends, 2); friends.Children.Add(_friends);
        _emptyFriends.VerticalAlignment = VerticalAlignment.Center; _emptyFriends.HorizontalAlignment = HorizontalAlignment.Center; _emptyFriends.IsHitTestVisible = false;
        Grid.SetRow(_emptyFriends, 2); friends.Children.Add(_emptyFriends);
        var inviteInfo = OnlineUi.Text("Select a Steam friend, then send an invitation to your hosted world.", "SubtleNotice", 22); inviteInfo.Margin = new Thickness(0, 14);
        Grid.SetRow(inviteInfo, 3); friends.Children.Add(inviteInfo);
        Grid.SetRow(_invite, 4); friends.Children.Add(_invite);

        _pages = new Control[] { moderation, banGrid, friends };
        string[] names = { "Moderation", "Bans & whitelist", "Invite friends" };
        _tabs = new Button[names.Length];
        for (int i = 0; i < names.Length; i++) { int index = i; _tabs[i] = OnlineUi.Button(names[i], () => Select(index), "Players" + names[i].Replace(" ", "").Replace("&", "")); _tabs[i].Classes.Add("OnlineNavigation"); }
        Styles.Add(new Avalonia.Styling.Style(s => s.OfType<Button>().Class("OnlineNavigation").Class("Selected")) { Setters = { new Avalonia.Styling.Setter(Button.BorderThicknessProperty, new Thickness(3)), new Avalonia.Styling.Setter(Button.BorderBrushProperty, new Keen.VRage.UI.AvaloniaInterface.Utilities.VRageDynamicResource("Primary")), new Avalonia.Styling.Setter(Button.FontWeightProperty, Avalonia.Media.FontWeight.Bold) } });
        var body = OnlineUi.Columns(OnlineUi.Panel(roster), OnlineUi.Panel(_page), "*,24,760");
        var connection = OnlineUi.Stack(_status, _message); connection.Spacing = 6;
        Content = OnlineUi.Shell(this, "PLAYERS", OnlineUi.Row(_tabs), connection, body, OnlineUi.Row(OnlineUi.Button("Server info", ServerInfoUi.OpenInGame, "ServerInfo")));
        Select(0);
    }

    private AccessPlayer Selected => _players.SelectedItem as AccessPlayer;
    private bool Admin => AdminAccess.LocalRole != OnlineRole.Player && AdminAccess.InSession;
    private bool Listed(ulong id) => _state.Whitelist.Any(w => w.SteamId == id);
    private static string Display(string name, ulong id) => string.IsNullOrWhiteSpace(name) ? id.ToString() : name;
    private static string RoleText(OnlineRole role) => role switch { OnlineRole.Host => "Host", OnlineRole.Admin => "Admin", _ => "Player" };

    private void Select(int index)
    {
        _page.Content = _pages[index];
        for (int i = 0; i < _tabs.Length; i++) { if (i == index) _tabs[i].Classes.Add("Selected"); else _tabs[i].Classes.Remove("Selected"); }
        RefreshControls();
    }

    private void Moderate(AdminAction action)
    {
        if (Selected is not { } p) return;
        string reason = _reason.Text ?? "";
        var duration = _duration.SelectedItem as Duration ?? Durations[0];
        string verb = action == AdminAction.Ban ? "Ban" : "Kick";
        string detail = action == AdminAction.Ban ? $"Ban {p.Name} {(duration.Minutes == 0 ? "permanently" : "for " + duration.Label)}?" : $"Kick {p.Name} from the session? They can join again.";
        SocialUi.Confirm(verb + " player", detail + (string.IsNullOrWhiteSpace(reason) ? "" : "\nReason: " + reason), () =>
        {
            AdminAccess.Send(new AdminRequest(action, p.SteamId, reason, action == AdminAction.Ban ? duration.Minutes : 0));
            Dispatcher.UIThread.Post(() => _reason.Text = "");
        });
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_model != null) _model.Refresh -= QueueRefresh;
        _model = DataContext as PlayersScreenViewModel;
        if (_model != null) _model.Refresh += QueueRefresh;
    }
    private void QueueRefresh() => Dispatcher.UIThread.Post(() => { if (!_closed) { RefreshControls(); ServerInfoUi.Tick(); } });

    private void RefreshControls()
    {
        if (_kick == null) return;
        _state = AdminAccess.State;
        bool session = AdminAccess.InSession, hosting = AdminAccess.Hosting;
        _status.Text = !session ? OnlineUi.SessionStatus()
            : hosting ? $"You are hosting {_state.ServerName}" + (NetSession.Current?.IsDedicated == true ? " (dedicated)" : "")
            : $"{(_state.Dedicated ? "Dedicated server" : "Session")} {AdminAccess.ServerName}  /  you are {(AdminAccess.LocalRole == OnlineRole.Admin ? "an admin" : "a player")}";
        if (session) _status.Text += "  /  PvP " + (_state.Pvp ? "on" : "off");
        _message.Text = new[] { MpPlugin.Controller?.Error, NetSession.Current?.Failure, _state.Message, MpMenu.Message }.FirstOrDefault(m => !string.IsNullOrWhiteSpace(m)) ?? "";
        _message.IsVisible = !string.IsNullOrWhiteSpace(_message.Text);
        var players = session ? _state.Players : ClientWorld.Current?.SessionPlayers.Select(p => new AccessPlayer(0, p.Name, OnlineRole.Player, p.Connected, -1)).ToArray() ?? Array.Empty<AccessPlayer>();
        string signature = string.Join("|", players.Select(p => p.SteamId + ":" + p.Name + ":" + p.Role + ":" + p.Connected + ":" + p.PingMs / 10));
        if (signature != _signature)
        {
            var selected = Selected;
            _signature = signature; _players.ItemsSource = players;
            _players.SelectedItem = players.FirstOrDefault(p => p.SteamId == selected?.SteamId && p.Name == selected?.Name);
        }
        _count.Text = "PLAYERS  " + players.Count(p => p.Connected) + " / " + (session && !hosting && _state.MaxPlayers > 0 ? _state.MaxPlayers : MpPlugin.Controller?.Current?.MaxPlayers ?? MpMenu.Settings.MaxPlayers);
        _emptyPlayers.IsVisible = players.Length == 0;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string access = _state.Revision + ":" + _state.Bans.Length + ":" + _state.Whitelist.Length + ":" + now / 60000;
        if (access != _accessSignature)
        {
            _accessSignature = access;
            ulong ban = (_bans.SelectedItem as BanRow)?.Ban.SteamId ?? 0, listed = (_whitelist.SelectedItem as AccessEntry)?.SteamId ?? 0;
            var bans = _state.Bans.Select(b => new BanRow(b, b.ExpiresUnixMs is long end ? AccessList.Remaining(end - now) : "Permanent")).ToArray();
            _bans.ItemsSource = bans; _bans.SelectedItem = bans.FirstOrDefault(b => b.Ban.SteamId == ban);
            _whitelist.ItemsSource = _state.Whitelist; _whitelist.SelectedItem = _state.Whitelist.FirstOrDefault(w => w.SteamId == listed);
        }
        _syncingToggle = true; _whitelistOnly.IsChecked = _state.WhitelistOnly; _syncingToggle = false;
        _accessNote.Text = (_state.HasPassword ? "This server is password protected." : "No join password is set.") + " Set the password in Multiplayer > Host" + (_state.Dedicated ? " (server.json Network.Password on dedicated servers)." : ".");
        if (!ReferenceEquals(_lastFriends, MpMenu.Friends))
        {
            ulong selected = (_friends.SelectedItem as FriendHost)?.SteamId ?? 0;
            _lastFriends = MpMenu.Friends; _friends.ItemsSource = _lastFriends;
            _friends.SelectedItem = _lastFriends.FirstOrDefault(p => p.SteamId == selected);
        }
        _emptyFriends.IsVisible = MpMenu.Friends.Length == 0;
        _emptyFriends.Text = MpPlugin.SteamAvailable ? "No Steam friends found.\nRefresh to try again." : "Steam is unavailable.\nConnect to Steam to invite friends.";
        RefreshActions();
    }

    private void RefreshActions()
    {
        if (_kick == null) return;
        bool busy = MpMenu.Busy, hosting = AdminAccess.Hosting, admin = Admin;
        var p = Selected;
        bool target = admin && p != null && p.SteamId != 0 && p.Role != OnlineRole.Host;
        bool canTouchAdmin = hosting || p?.Role != OnlineRole.Admin;
        _kick.IsEnabled = _ban.IsEnabled = !busy && target && p.Connected && canTouchAdmin;
        _ban.IsEnabled = !busy && target && canTouchAdmin;
        _admin.IsEnabled = !busy && target && canTouchAdmin;
        _admin.Content = p?.Role == OnlineRole.Admin ? "Remove admin" : "Make admin";
        _whitelistButton.IsEnabled = !busy && target;
        _whitelistButton.Content = p != null && Listed(p.SteamId) ? "Remove from whitelist" : "Add to whitelist";
        _reason.IsEnabled = _duration.IsEnabled = admin;
        _whitelistOnly.IsEnabled = admin && !busy;
        _unban.IsEnabled = admin && !busy && _bans.SelectedItem is BanRow;
        _unlist.IsEnabled = admin && !busy && _whitelist.SelectedItem is AccessEntry;
        _invite.IsEnabled = !busy && hosting && NetSession.Current?.IsDedicated != true && MpPlugin.SteamAvailable && MpPlugin.Controller?.Current?.HostIp == null && _friends.SelectedItem is FriendHost;
        _selected.Text = p == null ? "Select a player in the roster." : p.Name + "  /  " + RoleText(p.Role) + (p.SteamId != 0 ? "  /  " + p.SteamId : "");
        _moderationNote.Text = !AdminAccess.InSession ? "Moderation is available in a multiplayer session."
            : !admin ? "Only the host and admins can kick, ban or promote players."
            : p?.Role == OnlineRole.Admin && !hosting ? "Only the host can demote, kick or ban an admin."
            : "Kicked players can rejoin. Banned players see the reason and remaining time when they try to connect.";
    }
}
