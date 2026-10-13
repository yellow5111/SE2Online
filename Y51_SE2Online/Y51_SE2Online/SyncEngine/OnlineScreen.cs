using System;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Styling;
using Keen.VRage.UI.AvaloniaInterface.Utilities;
using Keen.VRage.UI.Screens;
using Y51_SE2Online.Core;
using Y51_SE2Online.NetHost;

namespace Y51_SE2Online.SyncEngine;

public sealed class OnlineScreenViewModel : ScreenViewModel
{
    public event Action Refresh;
    private long _nextRefresh;
    public OnlineScreenViewModel()
    {
        KeepsOtherScreensVisible = false;
        AllowsInputFromLowerScreens = false;
        InitializeInputContext();
        OnTick += () => { if (MpClock.NowMs() < _nextRefresh) return; _nextRefresh = MpClock.NowMs() + 500; Refresh?.Invoke(); };
    }
    public void Close() => SignalWantsClose();
    // Esc while rebinding a key cancels the rebind (KeyBindButton) instead of closing the screen.
    protected override void OnCloseRequested() { if (!KeyBindButton.Listening) base.OnCloseRequested(); }
}

public sealed class OnlineScreen : ScreenView
{
    private readonly TextBlock _status = OnlineUi.Text("Offline", "SubTitle");
    private readonly TextBlock _message = OnlineUi.Text("", "SubtleNotice", 21);
    private readonly ContentControl _page = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    private readonly OnlineDirectoryView _servers = new(true);
    private readonly OnlineDirectoryView _sessions = new(false);
    private readonly OnlineFriendSessionsView _friendSessions = new();
    private readonly ContentControl _sessionSource = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    private readonly Grid _sessionPage = new() { RowDefinitions = new RowDefinitions("Auto,*") };
    private readonly TextBox _address = OnlineUi.Input("IP address:port", 100);
    private readonly TextBox _steamId = OnlineUi.Input("Host's SteamID64", 17);
    private readonly TextBox _name = OnlineUi.Input("Use Steam name", 32);
    private readonly TextBox _sessionName = OnlineUi.Input("My SE2 session", 80);
    private readonly TextBox _description = OnlineUi.Input("Describe your world", 500);
    private readonly TextBox _limit = OnlineUi.Input("2-32", 2);
    private readonly TextBox _bind = OnlineUi.Input("0.0.0.0", 100);
    private readonly TextBox _port = OnlineUi.Input("7742", 5);
    private readonly TextBox _publicAddress = OnlineUi.Input("Optional public IP address:port", 100);
    private readonly OnlineToggle _advertise = new OnlineToggle("List this session in the public directory");
    private readonly OnlineToggle _directIp = new OnlineToggle("Allow connections by IP address");
    private readonly OnlineToggle _friendsOnly = new OnlineToggle("Only Steam friends may join");
    private readonly OnlineToggle _team = new OnlineToggle("Keep existing grids shared when upgrading an older world");
    private readonly OnlineToggle _research = new OnlineToggle("Share research progression");
    private readonly OnlineToggle _turrets = new OnlineToggle("PvP: players can damage each other; turrets attack players outside your faction");
    private readonly OnlineToggle _nametags = new OnlineToggle("Show player name tags");
    private readonly TextBox _range = OnlineUi.Input("200", 8);
    private readonly OnlineToggle _spawn = new OnlineToggle("Spawn new players near the host");
    private readonly OnlineToggle _reconnect = new OnlineToggle("Automatically reconnect after connection loss");
    private readonly OnlineToggle _updates = new OnlineToggle("Check se2-online.yellow51.dev for plugin updates when the game starts");
    private readonly TextBox _smoothing = OnlineUi.Input("60", 3);
    private readonly TextBox _bandwidth = OnlineUi.Input("256", 4);
    private readonly TextBox _password = new() { Watermark = "No password", MaxLength = 64, FontSize = 24, MinHeight = 50, PasswordChar = '*' };
    private readonly KeyBindButton _chatKey = new(), _playersKey = new(), _adminKey = new(), _broadcastKey = new();
    private readonly Button _host, _joinAddress, _joinId, _leave, _load, _create, _save, _hostSave, _players;
    private readonly Button[] _navigation;
    private readonly Button[] _sessionButtons;
    private readonly Control[] _pages;
    private OnlineScreenViewModel _model;
    private bool _closed;

    public OnlineScreen()
    {
        OnDisposing += _ => { _closed = true; if (_model != null) _model.Refresh -= QueueRefresh; };
        ReadSettings();
        _joinAddress = OnlineUi.Button("Connect to address", () => MpMenu.JoinAddress(_address.Text ?? ""), "ConnectAddress", true);
        _joinId = OnlineUi.Button("Join Steam host", () => MpMenu.Activate(MpMenu.Settings.JoinOptions(MpOptions.ParseSteamId(_steamId.Text), false)), "ConnectSteam");
        _address.TextChanged += (_, _) => RefreshControls();
        _steamId.TextChanged += (_, _) => RefreshControls();
        var direct = OnlineUi.Columns(
            OnlineUi.Section("DIRECT CONNECT", OnlineUi.Text("Connect directly to a dedicated server or a player's IP address."),
                OnlineUi.Text("Ask the host for their address and port. Enter IPv6 addresses in brackets.", "SubtleNotice", 22),
                _address, OnlineUi.Text("IPv4: 203.0.113.10:7742\nIPv6: [2001:db8::10]:7742", "SubtleNotice", 22), _joinAddress),
            OnlineUi.Section("STEAM HOST", OnlineUi.Text("Join a player-hosted session using the host's Steam ID."), _steamId, _joinId,
                OnlineUi.Text("Both players need the same multiplayer version. Friends-only sessions still require a Steam friendship.", "SubtleNotice", 22)), "*,24,600");
        _host = OnlineUi.Button("Start hosting", StartHost, "StartHosting", true);
        _load = OnlineUi.Button(OnlineUi.Localize("MenuLoadGame", "Load world"), () => MpMenu.ChooseWorld(false), "LoadWorld", true);
        _create = OnlineUi.Button(OnlineUi.Localize("MenuNewGame", "New world"), () => MpMenu.ChooseWorld(true), "CreateWorld", true);
        _hostSave = OnlineUi.Button("Apply host settings", Save, "SaveHostSettings");
        var hosting = OnlineUi.Scroll(OnlineUi.Stack(
            OnlineUi.Section("HOST A SESSION", OnlineUi.Text("Host your current world, or start hosting and then load or create a world."),
                OnlineUi.Row(_host, _load, _create), OnlineUi.Field("Session name", _sessionName), OnlineUi.Field("Description", _description),
                OnlineUi.Field("Maximum players", _limit, "2-32 players, including you"), OnlineUi.Field("Join password", _password, "Optional. Admins can join without it."), _friendsOnly, _advertise),
            OnlineUi.Section("DIRECT IP CONNECTIONS", _directIp, OnlineUi.Field("Bind address", _bind, "0.0.0.0 listens on all IPv4 interfaces"),
                OnlineUi.Field("Listen port", _port), OnlineUi.Field("Public address", _publicAddress, "Shown in the directory for direct connections"),
                OnlineUi.Text("Remote players need a reachable IP address and the listen port open on your router and firewall.", "SubtleNotice", 22), _hostSave)));
        _save = OnlineUi.Button(OnlineUi.Localize("SaveSettings", "Save settings"), Save, "SaveOnlineSettings", true);
        var settings = OnlineUi.Scroll(OnlineUi.Stack(
            OnlineUi.Section("PLAYER", OnlineUi.Field("Player name", _name), _nametags, OnlineUi.Field("Name tag range", _range, "Metres"),
                OnlineUi.Text("Connection diagnostics: press F11 in game until the Y51_SE2Online stats page shows.", "SubtleNotice", 21)),
            OnlineUi.Section("WORLD RULES", OnlineUi.Text("New grids are private. Use Grid ownership and Factions in the pause menu to control sharing.", "SubtleNotice", 21), _team, _research, _turrets, _spawn),
            OnlineUi.Section("KEYBINDS", OnlineUi.Text("Click a binding, then press the new key or combination. Esc cancels.", "SubtleNotice", 21),
                OnlineUi.Field("Open chat", _chatKey), OnlineUi.Field("Players list", _playersKey), OnlineUi.Field("Admin panel", _adminKey), OnlineUi.Field("Suit broadcast", _broadcastKey, "Hides or shows your name tag to other players")),
            OnlineUi.Section("CONNECTION", _reconnect, _updates, OnlineUi.Field("Motion smoothing", _smoothing, "0-250 milliseconds"), OnlineUi.Field("Bandwidth per player", _bandwidth, "32-4096 KiB/s")),
            OnlineUi.Text("Applying settings while hosting restarts the session and disconnects other players. Your local world stays open.", "SubtleNotice", 22), _save));
        _players = OnlineUi.Button("Players", MpMenu.OpenPlayers, "OpenPlayers", true);
        _leave = OnlineUi.Button("Stop hosting", MpMenu.Leave, "LeaveSession");
        var publicSessions = OnlineUi.Button("Public sessions", () => { _sessionSource.Content = _sessions; RefreshControls(); }, "PublicSessionsSource");
        var friendSessions = OnlineUi.Button("Steam friends hosting", () => { _sessionSource.Content = _friendSessions; RefreshControls(); }, "FriendSessionsSource");
        _sessionButtons = new[] { publicSessions, friendSessions };
        foreach (var button in _sessionButtons) button.Classes.Add("OnlineNavigation");
        var sessionNavigation = OnlineUi.Row(publicSessions, friendSessions, OnlineUi.Text("Player-hosted worlds", "SubtleNotice", 22));
        sessionNavigation.Margin = new Thickness(0, 0, 0, 16); _sessionPage.Children.Add(sessionNavigation);
        _sessionSource.Content = _sessions; Grid.SetRow(_sessionSource, 1); _sessionPage.Children.Add(_sessionSource);
        _pages = new Control[] { _servers, _sessionPage, direct, hosting, settings };
        string[] names = { "Servers", "Sessions", "Direct connect", "Host", "Settings" };
        _navigation = new Button[names.Length];
        for (int i = 0; i < names.Length; i++) { int index = i; _navigation[i] = OnlineUi.Button(names[i], () => SelectPage(index), "Online" + names[i].Replace(" ", "")); _navigation[i].MinWidth = 236; _navigation[i].Classes.Add("OnlineNavigation"); }
        Styles.Add(new Style(s => s.OfType<Button>().Class("OnlineNavigation").Class("Selected")) { Setters = { new Setter(Button.BorderThicknessProperty, new Thickness(3)), new Setter(Button.BorderBrushProperty, new VRageDynamicResource("Primary")), new Setter(Button.FontWeightProperty, FontWeight.Bold) } });
        var navigation = OnlineUi.Row(_navigation);
        var connection = OnlineUi.Stack(_status, _message); connection.Spacing = 6; _message.MaxHeight = 72;
        Content = OnlineUi.Shell(this, OnlineUi.Localize("MenuMultiplayer", "MULTIPLAYER"), navigation, connection, _page, OnlineUi.Row(_leave, _players));
        SelectPage(0);
        RefreshControls();
    }

    private void SelectPage(int index)
    {
        _page.Content = _pages[index];
        for (int i = 0; i < _navigation.Length; i++) { if (i == index) _navigation[i].Classes.Add("Selected"); else _navigation[i].Classes.Remove("Selected"); }
        RefreshControls();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_model != null) _model.Refresh -= QueueRefresh;
        _model = DataContext as OnlineScreenViewModel;
        if (_model != null) _model.Refresh += QueueRefresh;
    }
    private void QueueRefresh() => Dispatcher.UIThread.Post(() => { if (!_closed) RefreshControls(); });

    private void RefreshControls()
    {
        if (_host == null) return;
        var controller = MpPlugin.Controller;
        bool busy = controller?.Busy == true, hosting = controller?.Current?.Host == true;
        _status.Text = OnlineUi.SessionStatus();
        string actionMessage = controller?.Error ?? NetSession.Current?.Failure ?? MpMenu.Message;
        string directoryMessage = hosting ? NetSession.Current?.DirectoryError : (_page.Content is OnlineDirectoryView || _page.Content == _sessionPage && _sessionSource.Content == _sessions) ? MpMenu.DirectoryError : "";
        _message.Text = string.Join("  /  ", new[] { actionMessage, directoryMessage }.Where(m => !string.IsNullOrWhiteSpace(m)).Distinct());
        _message.IsVisible = !string.IsNullOrWhiteSpace(_message.Text);
        _host.IsEnabled = !busy && !hosting && MpPlugin.SteamAvailable;
        _load.IsEnabled = _create.IsEnabled = !busy && !MpMenu.HasWorld && hosting;
        _joinAddress.IsEnabled = !busy && !string.IsNullOrWhiteSpace(_address.Text);
        _joinId.IsEnabled = !busy && MpPlugin.SteamAvailable && ulong.TryParse(_steamId.Text, out var id) && id != 0;
        // Joiners leave with Exit to Menu / Exit to Windows; retrying lives in the join failure dialog.
        _leave.IsVisible = hosting;
        _leave.IsEnabled = !busy;
        _players.IsEnabled = controller?.Current != null;
        _save.IsEnabled = _hostSave.IsEnabled = !busy;
        _bind.IsEnabled = _port.IsEnabled = _publicAddress.IsEnabled = _directIp.IsChecked == true;
        if (_page.Content is OnlineDirectoryView browser) browser.Refresh();
        if (_sessionButtons != null) for (int i = 0; i < _sessionButtons.Length; i++) { bool selected = i == 0 ? _sessionSource.Content == _sessions : _sessionSource.Content == _friendSessions; if (selected) _sessionButtons[i].Classes.Add("Selected"); else _sessionButtons[i].Classes.Remove("Selected"); }
        if (_page.Content == _sessionPage) { if (_sessionSource.Content == _sessions) _sessions.Refresh(); else _friendSessions.Refresh(); }
    }

    private void ReadSettings()
    {
        var s = MpMenu.Settings;
        _name.Text = s.PlayerName; _sessionName.Text = s.SessionName; _description.Text = s.Description;
        _limit.Text = s.MaxPlayers.ToString(CultureInfo.InvariantCulture); _friendsOnly.IsChecked = s.FriendsOnly;
        _advertise.IsChecked = s.Advertise; _directIp.IsChecked = s.AllowDirectIp;
        _bind.Text = s.BindAddress; _port.Text = s.ListenPort.ToString(CultureInfo.InvariantCulture); _publicAddress.Text = s.PublicAddress;
        _team.IsChecked = s.TeamGrids; _research.IsChecked = s.SharedResearch; _turrets.IsChecked = s.Pvp; _nametags.IsChecked = s.Nametags;
        _range.Text = s.NametagRange.ToString(CultureInfo.InvariantCulture); _spawn.IsChecked = s.SpawnNearHost;
        _password.Text = s.Password; _chatKey.Binding = s.ChatKey; _playersKey.Binding = s.PlayersKey; _adminKey.Binding = s.AdminKey; _broadcastKey.Binding = s.BroadcastKey;
        _reconnect.IsChecked = s.AutoReconnect; _updates.IsChecked = s.CheckForUpdates; _smoothing.Text = s.InterpolationMs.ToString(CultureInfo.InvariantCulture); _bandwidth.Text = s.StateBudgetKiB.ToString(CultureInfo.InvariantCulture);
        _directIp.IsCheckedChanged += (_, _) => RefreshControls();
    }

    private OnlineSettings EditedSettings()
    {
        if (!int.TryParse(_limit.Text, out int limit) || !int.TryParse(_smoothing.Text, out int smoothing) || !int.TryParse(_port.Text, out int port) || !int.TryParse(_bandwidth.Text, out int bandwidth) || !double.TryParse(_range.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double range))
        { MpMenu.Report("Enter a valid number for each setting."); return null; }
        var settings = new OnlineSettings
        {
            ServersDirectory = MpMenu.Settings.ServersDirectory, SessionsDirectory = MpMenu.Settings.SessionsDirectory,
            PlayerName = _name.Text ?? "", SessionName = _sessionName.Text ?? "", Description = _description.Text ?? "", MaxPlayers = limit,
            FriendsOnly = _friendsOnly.IsChecked == true, Advertise = _advertise.IsChecked == true, AdvertiseChosen = true, AllowDirectIp = _directIp.IsChecked == true,
            BindAddress = _bind.Text ?? "", ListenPort = port, PublicAddress = _publicAddress.Text ?? "",
            TeamGrids = _team.IsChecked == true, SharedResearch = _research.IsChecked == true, Pvp = _turrets.IsChecked == true, Nametags = _nametags.IsChecked == true, NametagRange = range,
            SpawnNearHost = _spawn.IsChecked == true, AutoReconnect = _reconnect.IsChecked == true, CheckForUpdates = _updates.IsChecked == true, InterpolationMs = smoothing, StateBudgetKiB = bandwidth,
            Password = _password.Text ?? "", ChatKey = _chatKey.Binding, PlayersKey = _playersKey.Binding, AdminKey = _adminKey.Binding, BroadcastKey = _broadcastKey.Binding, SuitBroadcast = MpMenu.Settings.SuitBroadcast, SkippedUpdate = MpMenu.Settings.SkippedUpdate
        };
        string error = settings.Validate();
        if (error != null) { MpMenu.Report(error); return null; }
        return settings;
    }
    private void Save() { var s = EditedSettings(); if (s != null) MpMenu.SaveSettings(s); }
    private void StartHost() { var s = EditedSettings(); if (s == null) return; MpMenu.SaveSettings(s); MpMenu.Activate(s.HostOptions(false)); }
}







