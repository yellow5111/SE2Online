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
using Y51_SE2Online.Sync;

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
    private readonly TextBlock _version = new()
    {
        FontSize = 18, Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(255, 210, 60)),
        HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 24, 4), IsHitTestVisible = false
    };
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
    private readonly TextBox _country = OnlineUi.Input("auto", 4);
    private readonly (Slider S, Control Row) _limit = OnlineUi.Slider(2, 32, 1, v => v + " players");
    private readonly TextBox _bind = OnlineUi.Input("0.0.0.0", 100);
    private readonly TextBox _port = OnlineUi.Input("7742", 5);
    private readonly TextBox _publicAddress = OnlineUi.Input("Optional public IP address:port", 100);
    private readonly OnlineToggle _advertise = new OnlineToggle("List this session in the public directory");
    private readonly OnlineToggle _directIp = new OnlineToggle("Allow connections by IP address");
    private readonly OnlineToggle _waterSync = new OnlineToggle("Water sync (experimental)", "Synchronizes water between players.",
        "Performance heavy and uses extra bandwidth; not meant for full time use as water is not officially in the game yet!");
    private readonly OnlineToggle _friendsOnly = new OnlineToggle("Only Steam friends may join");
    private readonly OnlineToggle _team = new OnlineToggle("Keep existing grids shared when upgrading an older world");
    private readonly OnlineToggle _research = new OnlineToggle("Share research progression");
    private readonly OnlineToggle _turrets = new OnlineToggle("PvP: players can damage each other; turrets attack players outside your faction");
    private readonly OnlineToggle _nametags = new OnlineToggle("Show player name tags");
    private readonly (Slider S, Control Row) _range = OnlineUi.Slider(50, 10000, 50, v => v + " m");
    private readonly OnlineToggle _spawn = new OnlineToggle("Spawn new players near the host");
    private readonly OnlineToggle _hacking = new OnlineToggle("Hacking (opt-in): players take over grids at a terminal or control block, never in safe zones");
    private readonly OnlineToggle _hackPlayers = new OnlineToggle("Players' grids can be hacked too (opt-in; only with PvP on)");
    private readonly (Slider S, Control Row) _hackTime = OnlineUi.Slider(1, 100, 1, v => "x" + (v / 10.0).ToString("0.0", CultureInfo.InvariantCulture));
    private readonly KeyBindButton _hackKey = new();
    private readonly OnlineToggle _reconnect = new OnlineToggle("Automatically reconnect after connection loss");
    private readonly OnlineToggle _updates = new OnlineToggle("Check se2-online.yellow51.dev for plugin updates when the game starts");
    private readonly OnlineToggle _autoUpdate = new OnlineToggle("Install updates automatically (signed downloads, restart to apply; not for Pulsar installs)");
    private readonly (Slider S, Control Row) _smoothing = OnlineUi.Slider(0, 250, 5, v => v + " ms");
    private readonly (Slider S, Control Row) _bandwidth = OnlineUi.Slider(32, 4096, 32, v => v + " KiB/s");
    private readonly TextBox _password = new() { Watermark = "No password", MaxLength = 64, FontSize = 24, MinHeight = 50, PasswordChar = '*' };
    private readonly KeyBindButton _chatKey = new(), _playersKey = new(), _adminKey = new(), _broadcastKey = new(), _voiceKey = new(), _emoteKey = new();
    private readonly OnlineToggle _voice = new OnlineToggle("Proximity voice chat (talk to and hear players near you)");
    private readonly OnlineToggle _openMic = new OnlineToggle("Open microphone: transmit whenever you speak instead of holding push to talk");
    private readonly (Slider S, Control Row) _voiceVolume = OnlineUi.Slider(0, 200, 5, v => v + " %");
    private readonly (Slider S, Control Row) _voiceRadius = OnlineUi.Slider(0, 500, 5, v => v == 0 ? "Off" : v + " m");
    // Tenths: the slider shows whole steps, the setting keeps one decimal.
    private readonly (Slider S, Control Row) _energyUse = Usage(), _hydrogenUse = Usage(), _oxygenUse = Usage();
    private readonly (Slider S, Control Row) _playerInventory = SizeSlider(), _cargoInventory = SizeSlider();
    private static (Slider, Control) SizeSlider() => OnlineUi.Slider(InventoryMultipliers.Min * 10, InventoryMultipliers.Max * 10, 1, v => ResourceMultipliers.Format(v / 10.0));
    private static (Slider, Control) Usage() => OnlineUi.Slider(0, ResourceMultipliers.Max * 10, 1, v => ResourceMultipliers.Format(v / 10.0));
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
                OnlineUi.Row(_host, _load, _create), OnlineUi.Field("Session name", _sessionName), OnlineUi.Field("Description", _description), OnlineUi.Field("Country", _country, "auto finds it from your IP, or a 2-letter code like US, GB, DE"),
                OnlineUi.Field("Maximum players", _limit.Row, "2-32 players, including you"), OnlineUi.Field("Join password", _password, "Optional. Admins can join without it."), _friendsOnly, _advertise,
                _waterSync),
            OnlineUi.Section("DIRECT IP CONNECTIONS", _directIp, OnlineUi.Field("Bind address", _bind, "0.0.0.0 listens on all IPv4 interfaces"),
                OnlineUi.Field("Listen port", _port), OnlineUi.Field("Public address", _publicAddress, "Shown in the directory for direct connections"),
                OnlineUi.Text("Remote players need a reachable IP address and the listen port open on your router and firewall.", "SubtleNotice", 22), _hostSave)));
        _save = OnlineUi.Button(OnlineUi.Localize("SaveSettings", "Save settings"), Save, "SaveOnlineSettings", true);
        var settings = OnlineUi.Scroll(OnlineUi.Stack(
            OnlineUi.Section("PLAYER", OnlineUi.Field("Player name", _name), _nametags, OnlineUi.Field("Name tag range", _range.Row, "How far away player name tags show"),
                OnlineUi.Text("Connection diagnostics: press F11 in game until the Y51_SE2Online stats page shows.", "SubtleNotice", 21)),
            OnlineUi.Section("WORLD RULES", OnlineUi.Text("New grids are private. Use Grid ownership and Factions in the pause menu to control sharing.", "SubtleNotice", 21), _team, _research, _turrets, _spawn,
                OnlineUi.Field("Energy usage", _energyUse.Row, "Suit energy, jetpack and tools, and block power use. x1 is normal, x0 uses none"),
                OnlineUi.Field("Hydrogen usage", _hydrogenUse.Row, "Jetpack boost and hydrogen thrusters and engines"),
                OnlineUi.Field("Oxygen usage", _oxygenUse.Row, "Breathing and blocks that use oxygen. Changes apply live while hosting"),
                OnlineUi.Field("Player inventory size", _playerInventory.Row, "How much each player can carry. x1 is normal"),
                OnlineUi.Field("Cargo container size", _cargoInventory.Row, "Cargo containers, crates, lockers and barrels. x1 is normal"),
                _hacking, _hackPlayers, OnlineUi.Field("Hack time", _hackTime.Row, "x1 is 30 s plus 0.25 s per block, at most 5 min; powered grids take 1.5x")),
            OnlineUi.Section("KEYBINDS", OnlineUi.Text("Click a binding, then press the new key or combination. Esc cancels.", "SubtleNotice", 21),
                OnlineUi.Field("Open chat", _chatKey), OnlineUi.Field("Players list", _playersKey), OnlineUi.Field("Admin panel", _adminKey), OnlineUi.Field("Suit broadcast", _broadcastKey, "Hides or shows your name tag to other players"),
                OnlineUi.Field("Push to talk", _voiceKey, "Hold to talk to players near you"), OnlineUi.Field("Emotes", _emoteKey, "Hold for the emote menu; release over an emote to play it"),
                OnlineUi.Field("Hack", _hackKey, "At a terminal or control block: hack the grid, or counter-hack one of yours; again cancels")),
            OnlineUi.Section("VOICE", _voice, _openMic, OnlineUi.Field("Voice volume", _voiceVolume.Row, "0-200 percent"),
                OnlineUi.Field("Voice range when hosting", _voiceRadius.Row, "0 turns voice off for your session"),
                OnlineUi.Text("Your microphone is Steam's (Steam > Settings > Voice). Type /mute <player> in chat to stop hearing someone.", "SubtleNotice", 21)),
            OnlineUi.Section("CONNECTION", _reconnect, _updates, _autoUpdate, OnlineUi.Field("Motion smoothing", _smoothing.Row, "0-250 milliseconds"), OnlineUi.Field("Bandwidth per player", _bandwidth.Row, "32-4096 KiB/s")),
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
        // Bottom-right corner of the frame, below the footer buttons (which move up a little to make room).
        if (Content is Viewbox { Child: Grid frame })
        {
            foreach (var row in frame.Children.OfType<Grid>().Where(g => Grid.GetRow(g) == 4)) row.Margin = new Thickness(row.Margin.Left, row.Margin.Top, row.Margin.Right, 36);
            Grid.SetRow(_version, 4);
            frame.Children.Add(_version);
        }
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
        _version.Text = UpdatePrompt.VersionLine();
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
        _name.Text = s.PlayerName; _sessionName.Text = s.SessionName; _description.Text = s.Description; _country.Text = s.Country;
        _limit.S.Value = Math.Clamp(s.MaxPlayers, 2, 32); _friendsOnly.IsChecked = s.FriendsOnly;
        _advertise.IsChecked = s.Advertise; _directIp.IsChecked = s.AllowDirectIp; _waterSync.IsChecked = s.WaterSync;
        _bind.Text = s.BindAddress; _port.Text = s.ListenPort.ToString(CultureInfo.InvariantCulture); _publicAddress.Text = s.PublicAddress;
        _team.IsChecked = s.TeamGrids; _research.IsChecked = s.SharedResearch; _turrets.IsChecked = s.Pvp; _nametags.IsChecked = s.Nametags;
        _range.S.Value = Math.Clamp(s.NametagRange, 50, 10000); _spawn.IsChecked = s.SpawnNearHost;
        _energyUse.S.Value = Math.Clamp(s.EnergyUsageMultiplier * 10, 0, ResourceMultipliers.Max * 10); _hydrogenUse.S.Value = Math.Clamp(s.HydrogenUsageMultiplier * 10, 0, ResourceMultipliers.Max * 10); _oxygenUse.S.Value = Math.Clamp(s.OxygenUsageMultiplier * 10, 0, ResourceMultipliers.Max * 10);
        _playerInventory.S.Value = Math.Clamp(s.PlayerInventoryMultiplier * 10, InventoryMultipliers.Min * 10, InventoryMultipliers.Max * 10); _cargoInventory.S.Value = Math.Clamp(s.CargoInventoryMultiplier * 10, InventoryMultipliers.Min * 10, InventoryMultipliers.Max * 10);
        _password.Text = s.Password; _chatKey.Binding = s.ChatKey; _playersKey.Binding = s.PlayersKey; _adminKey.Binding = s.AdminKey; _broadcastKey.Binding = s.BroadcastKey; _voiceKey.Binding = s.VoiceKey; _emoteKey.Binding = s.EmoteKey; _hackKey.Binding = s.HackKey;
        _hacking.IsChecked = s.Hacking; _hackPlayers.IsChecked = s.AllowHackingPlayerGrids; _hackTime.S.Value = Math.Clamp(s.HackTimeMultiplier * 10, 1, 100);
        _voice.IsChecked = s.VoiceEnabled; _openMic.IsChecked = s.VoiceOpenMic; _voiceVolume.S.Value = Math.Clamp(s.VoiceVolume, 0, 200); _voiceRadius.S.Value = Math.Clamp(s.VoiceRadiusMeters, 0, 500);
        _reconnect.IsChecked = s.AutoReconnect; _updates.IsChecked = s.CheckForUpdates; _autoUpdate.IsChecked = s.AutoUpdate; _smoothing.S.Value = Math.Clamp(s.InterpolationMs, 0, 250); _bandwidth.S.Value = Math.Clamp(s.StateBudgetKiB, 32, 4096);
        _directIp.IsCheckedChanged += (_, _) => RefreshControls();
    }

    private OnlineSettings EditedSettings()
    {
        int limit = (int)Math.Round(_limit.S.Value), smoothing = (int)Math.Round(_smoothing.S.Value), bandwidth = (int)Math.Round(_bandwidth.S.Value);
        double range = Math.Round(_range.S.Value);
        if (!int.TryParse(_port.Text, out int port))
        { MpMenu.Report("Enter a valid number for each setting."); return null; }
        var settings = new OnlineSettings
        {
            ServersDirectory = MpMenu.Settings.ServersDirectory, SessionsDirectory = MpMenu.Settings.SessionsDirectory,
            PlayerName = _name.Text ?? "", SessionName = _sessionName.Text ?? "", Description = _description.Text ?? "", Country = _country.Text ?? "", MaxPlayers = limit,
            FriendsOnly = _friendsOnly.IsChecked == true, Advertise = _advertise.IsChecked == true, AdvertiseChosen = true, AllowDirectIp = _directIp.IsChecked == true, WaterSync = _waterSync.IsChecked == true,
            BindAddress = _bind.Text ?? "", ListenPort = port, PublicAddress = _publicAddress.Text ?? "",
            TeamGrids = _team.IsChecked == true, SharedResearch = _research.IsChecked == true, Pvp = _turrets.IsChecked == true, Nametags = _nametags.IsChecked == true, NametagRange = range,
            SpawnNearHost = _spawn.IsChecked == true, AutoReconnect = _reconnect.IsChecked == true, CheckForUpdates = _updates.IsChecked == true, AutoUpdate = _autoUpdate.IsChecked == true, InterpolationMs = smoothing, StateBudgetKiB = bandwidth,
            Password = _password.Text ?? "", ChatKey = _chatKey.Binding, PlayersKey = _playersKey.Binding, AdminKey = _adminKey.Binding, BroadcastKey = _broadcastKey.Binding, SuitBroadcast = MpMenu.Settings.SuitBroadcast, SkippedUpdate = MpMenu.Settings.SkippedUpdate,
            // Settings without a control here (server.json-style edits and admin choices) must survive a save.
            StreamingRadiusMeters = MpMenu.Settings.StreamingRadiusMeters, OfflineRaidProtection = MpMenu.Settings.OfflineRaidProtection, PcuLimit = MpMenu.Settings.PcuLimit,
            SunRotation = MpMenu.Settings.SunRotation, SunPeriodMinutes = MpMenu.Settings.SunPeriodMinutes, RespawnShips = MpMenu.Settings.RespawnShips, RespawnShipCooldownMinutes = MpMenu.Settings.RespawnShipCooldownMinutes,
            VoiceKey = _voiceKey.Binding, EmoteKey = _emoteKey.Binding, VoiceEnabled = _voice.IsChecked == true, VoiceOpenMic = _openMic.IsChecked == true, VoiceVolume = (int)Math.Round(_voiceVolume.S.Value), VoiceRadiusMeters = Math.Round(_voiceRadius.S.Value),
            MutedVoices = MpMenu.Settings.MutedVoices,
            Hacking = _hacking.IsChecked == true, AllowHackingPlayerGrids = _hackPlayers.IsChecked == true, HackTimeMultiplier = Math.Round(_hackTime.S.Value) / 10, HackKey = _hackKey.Binding,
            HackLockdownSeconds = MpMenu.Settings.HackLockdownSeconds, HackLockdownCooldownSeconds = MpMenu.Settings.HackLockdownCooldownSeconds,
            EnergyUsageMultiplier = Math.Round(_energyUse.S.Value) / 10, HydrogenUsageMultiplier = Math.Round(_hydrogenUse.S.Value) / 10, OxygenUsageMultiplier = Math.Round(_oxygenUse.S.Value) / 10, PlayerInventoryMultiplier = Math.Round(_playerInventory.S.Value) / 10, CargoInventoryMultiplier = Math.Round(_cargoInventory.S.Value) / 10
        };
        string error = settings.Validate();
        if (error != null) { MpMenu.Report(error); return null; }
        return settings;
    }
    private void Save() { var s = EditedSettings(); if (s != null) MpMenu.SaveSettings(s); }
    private void StartHost() { var s = EditedSettings(); if (s == null) return; MpMenu.SaveSettings(s); MpMenu.Activate(s.HostOptions(false)); }
}







