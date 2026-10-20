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

public sealed partial class OnlineScreen : ScreenView
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
    private readonly TextBox _joinCode = OnlineUi.Input("ABCD-1234", 9);
    private readonly TextBox _name = OnlineUi.Input("Use Steam name", 32);
    private readonly TextBox _sessionName = OnlineUi.Input("My SE2 session", 80);
    private readonly TextBox _description = OnlineUi.Input("Describe your world", 500);
    private readonly TextBox _country = OnlineUi.Input("auto", 4);
    private readonly (Slider S, Control Row) _limit = OnlineUi.Slider(OnlineSettings.MinPlayers, OnlineSettings.PlayerLimit, 1, v => v + " players");
    private readonly TextBlock _limitWarning = OnlineUi.Warning(OnlineSettings.UnstablePlayersWarning);
    private readonly TextBox _bind = OnlineUi.Input("0.0.0.0", 100);
    private readonly TextBox _port = OnlineUi.Input("7742", 5);
    private readonly TextBox _publicAddress = OnlineUi.Input("Optional public IP address:port", 100);
    private readonly OnlineToggle _directIp = new OnlineToggle("Allow connections by IP address");
    private readonly OnlineToggle _waterSync = new OnlineToggle("Water sync (experimental)", "Synchronizes water between players.",
        "Performance heavy and uses extra bandwidth; not meant for full time use as water is not officially in the game yet!");
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
    private readonly OnlineToggle _raid = new OnlineToggle("Offline raid protection: players cannot damage the grids of players who are offline");
    private readonly OnlineToggle _offlineStats = new OnlineToggle("Pause offline players' survival stats: suit energy, oxygen and hydrogen stay as they were until the player is back");
    private readonly TextBox _pcu = OnlineUi.Input("World's limit", 9), _dayLength = OnlineUi.Input("World's setting", 6), _streaming = OnlineUi.Input("0", 7);
    private static readonly string[] SunChoices = { "World's setting", "The sun moves", "The sun is stopped" };
    private readonly ComboBox _sun = new()
    {
        MinHeight = 48, FontSize = 22, HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = SunChoices,
        ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<string>((t, _) => t == null ? null : OnlineUi.Text(t, size: 22, inheritForeground: true))
    };
    private readonly (OnlineToggle Toggle, string Key)[] _ships = RespawnShips.Catalog.Select(s => (new OnlineToggle(s.Name, s.Detail), s.Key)).ToArray();
    private readonly (Slider S, Control Row) _shipCooldown = OnlineUi.Slider(0, RespawnShips.MaxCooldownMinutes, 5, v => v == 0 ? "No wait" : v + " min");
    private readonly (Slider S, Control Row) _lockdown = OnlineUi.Slider(0, 600, 5, v => v == 0 ? "Off" : v + " s");
    private readonly (Slider S, Control Row) _lockdownCooldown = OnlineUi.Slider(0, 3600, 30, v => v < 60 ? v + " s" : (v / 60.0).ToString("0.#", CultureInfo.InvariantCulture) + " min");
    private readonly TextBox _motdTitle = OnlineUi.Input("Session name", Motd.MaxTitle);
    private readonly TextBox _motd = new() { Watermark = "No message", MaxLength = Motd.MaxText, FontSize = 22, MinHeight = 100, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
    private readonly OnlineToggle _buildGhosts = new OnlineToggle("Build previews (players see where others are about to place blocks)");
    private readonly OnlineToggle _voice = new OnlineToggle("Proximity voice chat (talk to and hear players near you)");
    private readonly OnlineToggle _openMic = new OnlineToggle("Open microphone: transmit whenever you speak instead of holding push to talk");
    private readonly (Slider S, Control Row) _voiceVolume = OnlineUi.Slider(0, 200, 5, v => v + " %");
    private readonly (Slider S, Control Row) _voiceRadius = OnlineUi.Slider(0, 500, 5, v => v == 0 ? "Off" : v + " m");
    // Tenths: the slider shows whole steps, the setting keeps one decimal.
    private readonly (Slider S, Control Row) _energyUse = Usage(), _hydrogenUse = Usage(), _oxygenUse = Usage();
    private readonly (Slider S, Control Row) _playerInventory = SizeSlider(), _cargoInventory = SizeSlider();
    private static (Slider, Control) SizeSlider() => OnlineUi.Slider(InventoryMultipliers.Min * 10, InventoryMultipliers.Max * 10, 1, v => ResourceMultipliers.Format(v / 10.0));
    private static (Slider, Control) Usage() => OnlineUi.Slider(0, ResourceMultipliers.Max * 10, 1, v => ResourceMultipliers.Format(v / 10.0));
    private readonly Button _joinAddress, _joinId, _joinByCode, _leave, _save, _players;
    private Button _host, _load, _create, _hostSave;
    private readonly Button[] _navigation;
    private readonly Button[] _sessionButtons;
    private readonly Control[] _pages;
    private OnlineScreenViewModel _model;
    private bool _closed;

    /// <summary>The open Online screen (test screenshots).</summary>
    internal static OnlineScreen Last => _last != null && _last.TryGetTarget(out var screen) && !screen._closed ? screen : null;
    private static WeakReference<OnlineScreen> _last;

    internal void Close() => (DataContext as OnlineScreenViewModel)?.Close();

    public OnlineScreen()
    {
        _last = new WeakReference<OnlineScreen>(this);
        OnDisposing += _ => { _closed = true; if (_model != null) _model.Refresh -= QueueRefresh; };
        _limit.S.PropertyChanged += (_, e) => { if (e.Property == Avalonia.Controls.Primitives.RangeBase.ValueProperty) _limitWarning.IsVisible = OnlineSettings.Unstable((int)Math.Round(_limit.S.Value)); };
        ReadSettings();
        _limitWarning.IsVisible = OnlineSettings.Unstable((int)Math.Round(_limit.S.Value));
        _joinAddress = OnlineUi.Button("Connect to address", () => MpMenu.JoinAddress(_address.Text ?? ""), "ConnectAddress", true);
        _joinId = OnlineUi.Button("Join Steam host", () => MpMenu.Activate(MpMenu.Settings.JoinOptions(MpOptions.ParseSteamId(_steamId.Text), false)), "ConnectSteam");
        _joinByCode = OnlineUi.Button("Join with code", () => MpMenu.JoinCode(_joinCode.Text ?? ""), "ConnectCode", true);
        _address.TextChanged += (_, _) => RefreshControls();
        _steamId.TextChanged += (_, _) => RefreshControls();
        _joinCode.TextChanged += (_, _) => RefreshControls();
        _joinCode.FontSize = 32; _joinCode.MinHeight = 60;
        var direct = OnlineUi.Columns(OnlineUi.Scroll(OnlineUi.Stack(
            OnlineUi.Section("DIRECT CONNECT", OnlineUi.Text("Connect directly to a dedicated server or a player's IP address."),
                OnlineUi.Text("Ask the host for their address and port. Enter IPv6 addresses in brackets.", "SubtleNotice", 22),
                _address, OnlineUi.Text("IPv4: 203.0.113.10:7742\nIPv6: [2001:db8::10]:7742", "SubtleNotice", 22), _joinAddress))),
            OnlineUi.Scroll(OnlineUi.Stack(
            OnlineUi.Section("JOIN CODE", OnlineUi.Text("Enter the 8-character code from the host's Host page."), _joinCode, _joinByCode),
            OnlineUi.Section("STEAM HOST", OnlineUi.Text("Join a player-hosted session using the host's Steam ID."), _steamId, _joinId,
                OnlineUi.Text("Both players need the same multiplayer version. Friends-only sessions still require a Steam friendship.", "SubtleNotice", 22)))), "*,24,640");
        var hosting = BuildHostPage();
        _save = OnlineUi.Button(OnlineUi.Localize("SaveSettings", "Save settings"), Save, "SaveOnlineSettings", true);
        var settings = OnlineUi.Scroll(OnlineUi.Stack(
            OnlineUi.Section("PLAYER", OnlineUi.Field("Player name", _name), _nametags, OnlineUi.Field("Name tag range", _range.Row, "How far away player name tags show"),
                OnlineUi.Text("Connection diagnostics: press F11 in game until the Space Engineers 2 Online stats page shows.", "SubtleNotice", 21)),
            OnlineUi.Section("WORLD RULES", OnlineUi.Text("New grids are private. Use Grid ownership and Factions in the pause menu to control sharing.", "SubtleNotice", 21), _team, _research, _turrets, _raid, _offlineStats, _spawn,
                OnlineUi.Field("PCU limit per player", _pcu, "Empty keeps the world's limit, 0 is unlimited"),
                OnlineUi.Field("Energy usage", _energyUse.Row, "Suit energy, jetpack and tools, and block power use. x1 is normal, x0 uses none"),
                OnlineUi.Field("Hydrogen usage", _hydrogenUse.Row, "Jetpack boost and hydrogen thrusters and engines"),
                OnlineUi.Field("Oxygen usage", _oxygenUse.Row, "Breathing and blocks that use oxygen. Changes apply live while hosting"),
                OnlineUi.Field("Player inventory size", _playerInventory.Row, "How much each player can carry. x1 is normal"),
                OnlineUi.Field("Cargo container size", _cargoInventory.Row, "Cargo containers, crates, lockers and barrels. x1 is normal"),
                _hacking, _hackPlayers, OnlineUi.Field("Hack time", _hackTime.Row, "x1 is 30 s plus 0.25 s per block, at most 5 min; powered grids take 1.5x"),
                OnlineUi.Field("Remote lockdown", _lockdown.Row, "How long an owner's lockdown freezes a hack on their grid"), OnlineUi.Field("Lockdown cooldown", _lockdownCooldown.Row, "Wait before the same grid can be locked down again")),
            OnlineUi.Section("SUN AND RESPAWN SHIPS", OnlineUi.Field("Sun", _sun), OnlineUi.Field("Day length", _dayLength, "Minutes for one full day, 1-10080; empty keeps the world's setting"),
                OnlineUi.Text("Respawn ships players may choose", "SubTitle"), OnlineUi.Stack(_ships.Select(s => (Control)s.Toggle).ToArray()),
                OnlineUi.Field("Respawn ship cooldown", _shipCooldown.Row, "Wait before a player can take another respawn ship")),
            OnlineUi.Section("KEYBINDS", OnlineUi.Text("Click a binding, then press the new key or combination. Esc cancels.", "SubtleNotice", 21),
                OnlineUi.Field("Open chat", _chatKey), OnlineUi.Field("Players list", _playersKey), OnlineUi.Field("Admin panel", _adminKey, "The F3 players screen and /admin also open the panel"), OnlineUi.Field("Suit broadcast", _broadcastKey, "Hides or shows your name tag to other players"),
                OnlineUi.Field("Push to talk", _voiceKey, "Hold to talk to players near you"), OnlineUi.Field("Emotes", _emoteKey, "Hold for the emote menu; release over an emote to play it"),
                OnlineUi.Field("Hack", _hackKey, "At a terminal or control block: hack the grid, or counter-hack one of yours; again cancels")),
            OnlineUi.Section("VOICE", _voice, _openMic, OnlineUi.Field("Voice volume", _voiceVolume.Row, "0-200 percent"),
                OnlineUi.Field("Voice range when hosting", _voiceRadius.Row, "0 turns voice off for your session"),
                OnlineUi.Text("Your microphone is Steam's (Steam > Settings > Voice). Type /mute <player> in chat to stop hearing someone.", "SubtleNotice", 21)),
            OnlineUi.Section("CONNECTION", _reconnect, _updates, _autoUpdate, OnlineUi.Field("Motion smoothing", _smoothing.Row, "0-250 milliseconds"), OnlineUi.Field("Bandwidth per player", _bandwidth.Row, "32-4096 KiB/s"),
                OnlineUi.Field("Streaming radius when hosting", _streaming, $"Metres: players only receive grids and objects this close. 0 sends the whole world, otherwise {StreamingRules.MinRadius:0}-{StreamingRules.MaxRadius:0}")),
            OnlineUi.Text("Settings apply live while hosting. Only a new listen address, port or direct IP choice restarts hosting, and you are asked first.", "SubtleNotice", 22), _save));
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

    internal void SelectPage(int index)
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
        RefreshHostPage(busy, hosting);
        _host.IsEnabled = !busy && !hosting && MpPlugin.SteamAvailable;
        _joinByCode.IsEnabled = !busy && MpPlugin.SteamAvailable && Keen.VRage.Core.Services.GameSessions.SessionJoinCode.TryParse(_joinCode.Text?.Trim(), out _);
        _load.IsEnabled = _create.IsEnabled = !busy && !MpMenu.HasWorld && hosting;
        _joinAddress.IsEnabled = !busy && !string.IsNullOrWhiteSpace(_address.Text);
        _joinId.IsEnabled = !busy && MpPlugin.SteamAvailable && ulong.TryParse(_steamId.Text, out var id) && id != 0;
        // Joiners leave with Exit to Menu / Exit to Windows; retrying lives in the join failure dialog.
        _leave.IsVisible = hosting && _page.Content != _pages?[3];
        _leave.IsEnabled = !busy;
        _players.IsEnabled = controller?.Current != null;
        _players.IsVisible = _page.Content != _pages?[3] || !hosting;
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
        _limit.S.Value = Math.Clamp(s.MaxPlayers, OnlineSettings.MinPlayers, OnlineSettings.PlayerLimit); ReadVisibility(s);
        _directIp.IsChecked = s.AllowDirectIp; _waterSync.IsChecked = s.WaterSync; _buildGhosts.IsChecked = s.BuildGhosts;
        _bind.Text = s.BindAddress; _port.Text = s.ListenPort.ToString(CultureInfo.InvariantCulture); _publicAddress.Text = s.PublicAddress;
        _team.IsChecked = s.TeamGrids; _research.IsChecked = s.SharedResearch; _turrets.IsChecked = s.Pvp; _nametags.IsChecked = s.Nametags;
        _range.S.Value = Math.Clamp(s.NametagRange, 50, 10000); _spawn.IsChecked = s.SpawnNearHost;
        _energyUse.S.Value = Math.Clamp(s.EnergyUsageMultiplier * 10, 0, ResourceMultipliers.Max * 10); _hydrogenUse.S.Value = Math.Clamp(s.HydrogenUsageMultiplier * 10, 0, ResourceMultipliers.Max * 10); _oxygenUse.S.Value = Math.Clamp(s.OxygenUsageMultiplier * 10, 0, ResourceMultipliers.Max * 10);
        _playerInventory.S.Value = Math.Clamp(s.PlayerInventoryMultiplier * 10, InventoryMultipliers.Min * 10, InventoryMultipliers.Max * 10); _cargoInventory.S.Value = Math.Clamp(s.CargoInventoryMultiplier * 10, InventoryMultipliers.Min * 10, InventoryMultipliers.Max * 10);
        _password.Text = s.Password; _chatKey.Binding = s.ChatKey; _playersKey.Binding = s.PlayersKey; _adminKey.Binding = s.AdminKey; _broadcastKey.Binding = s.BroadcastKey; _voiceKey.Binding = s.VoiceKey; _emoteKey.Binding = s.EmoteKey; _hackKey.Binding = s.HackKey;
        _hacking.IsChecked = s.Hacking; _hackPlayers.IsChecked = s.AllowHackingPlayerGrids; _hackTime.S.Value = Math.Clamp(s.HackTimeMultiplier * 10, 1, 100);
        _voice.IsChecked = s.VoiceEnabled; _openMic.IsChecked = s.VoiceOpenMic; _voiceVolume.S.Value = Math.Clamp(s.VoiceVolume, 0, 200); _voiceRadius.S.Value = Math.Clamp(s.VoiceRadiusMeters, 0, 500);
        _reconnect.IsChecked = s.AutoReconnect; _updates.IsChecked = s.CheckForUpdates; _autoUpdate.IsChecked = s.AutoUpdate; _smoothing.S.Value = Math.Clamp(s.InterpolationMs, 0, 250); _bandwidth.S.Value = Math.Clamp(s.StateBudgetKiB, 32, 4096);
        _raid.IsChecked = s.OfflineRaidProtection; _offlineStats.IsChecked = s.PauseOfflineStats; _pcu.Text = s.PcuLimit?.ToString(CultureInfo.InvariantCulture) ?? "";
        _sun.SelectedIndex = s.SunRotation switch { null => 0, true => 1, false => 2 }; _dayLength.Text = s.SunPeriodMinutes?.ToString(CultureInfo.InvariantCulture) ?? "";
        _streaming.Text = s.StreamingRadiusMeters.ToString(CultureInfo.InvariantCulture);
        foreach (var (toggle, key) in _ships) toggle.IsChecked = s.RespawnShips is not { Count: > 0 } || s.RespawnShips.Contains(key, StringComparer.OrdinalIgnoreCase);
        _shipCooldown.S.Value = Math.Clamp(s.RespawnShipCooldownMinutes, 0, RespawnShips.MaxCooldownMinutes);
        _lockdown.S.Value = Math.Clamp(s.HackLockdownSeconds, 0, 600); _lockdownCooldown.S.Value = Math.Clamp(s.HackLockdownCooldownSeconds, 0, 3600);
        _motdTitle.Text = s.MotdTitle; _motd.Text = s.Motd;
        _directIp.IsCheckedChanged += (_, _) => RefreshControls();
    }

    private OnlineSettings EditedSettings()
    {
        int limit = (int)Math.Round(_limit.S.Value), smoothing = (int)Math.Round(_smoothing.S.Value), bandwidth = (int)Math.Round(_bandwidth.S.Value);
        double range = Math.Round(_range.S.Value);
        if (!int.TryParse(_port.Text, out int port) || !Optional(_pcu.Text, out double? pcu) || pcu is double whole && whole % 1 != 0 || !Optional(_dayLength.Text, out double? day) || !Optional(_streaming.Text, out double? streaming))
        { MpMenu.Report("Enter a valid number for each setting."); return null; }
        string[] ships = _ships.Where(s => s.Toggle.IsChecked).Select(s => s.Key).ToArray();
        if (ships.Length == 0) { MpMenu.Report("Offer at least one respawn ship."); return null; }
        var settings = new OnlineSettings
        {
            ServersDirectory = MpMenu.Settings.ServersDirectory, SessionsDirectory = MpMenu.Settings.SessionsDirectory,
            PlayerName = _name.Text ?? "", SessionName = _sessionName.Text ?? "", Description = _description.Text ?? "", Country = _country.Text ?? "", MaxPlayers = limit,
            FriendsOnly = _visibility == FriendsOnly, Advertise = _visibility == Public, AdvertiseChosen = true, AllowDirectIp = _directIp.IsChecked == true, WaterSync = _waterSync.IsChecked == true, BuildGhosts = _buildGhosts.IsChecked == true,
            BindAddress = _bind.Text ?? "", ListenPort = port, PublicAddress = _publicAddress.Text ?? "",
            TeamGrids = _team.IsChecked == true, SharedResearch = _research.IsChecked == true, Pvp = _turrets.IsChecked == true, Nametags = _nametags.IsChecked == true, NametagRange = range,
            SpawnNearHost = _spawn.IsChecked == true, AutoReconnect = _reconnect.IsChecked == true, CheckForUpdates = _updates.IsChecked == true, AutoUpdate = _autoUpdate.IsChecked == true, InterpolationMs = smoothing, StateBudgetKiB = bandwidth,
            Password = _password.Text ?? "", ChatKey = _chatKey.Binding, PlayersKey = _playersKey.Binding, AdminKey = _adminKey.Binding, BroadcastKey = _broadcastKey.Binding, SuitBroadcast = MpMenu.Settings.SuitBroadcast, SkippedUpdate = MpMenu.Settings.SkippedUpdate,
            StreamingRadiusMeters = streaming ?? 0, OfflineRaidProtection = _raid.IsChecked, PauseOfflineStats = _offlineStats.IsChecked, PcuLimit = (int?)pcu,
            SunRotation = _sun.SelectedIndex switch { 1 => true, 2 => false, _ => null }, SunPeriodMinutes = day,
            // Every ship ticked is stored as "all", so ships a later version adds are offered too.
            RespawnShips = ships.Length == _ships.Length ? new() : ships.Length == 0 ? new() { RespawnShips.None } : ships.ToList(), RespawnShipCooldownMinutes = (int)Math.Round(_shipCooldown.S.Value),
            Motd = _motd.Text ?? "", MotdTitle = (_motdTitle.Text ?? "").Trim(), MaxModDownloadGiB = MpMenu.Settings.MaxModDownloadGiB,
            VoiceKey = _voiceKey.Binding, EmoteKey = _emoteKey.Binding, VoiceEnabled = _voice.IsChecked == true, VoiceOpenMic = _openMic.IsChecked == true, VoiceVolume = (int)Math.Round(_voiceVolume.S.Value), VoiceRadiusMeters = Math.Round(_voiceRadius.S.Value),
            MutedVoices = MpMenu.Settings.MutedVoices,
            Hacking = _hacking.IsChecked == true, AllowHackingPlayerGrids = _hackPlayers.IsChecked == true, HackTimeMultiplier = Math.Round(_hackTime.S.Value) / 10, HackKey = _hackKey.Binding,
            HackLockdownSeconds = (int)Math.Round(_lockdown.S.Value), HackLockdownCooldownSeconds = (int)Math.Round(_lockdownCooldown.S.Value),
            EnergyUsageMultiplier = Math.Round(_energyUse.S.Value) / 10, HydrogenUsageMultiplier = Math.Round(_hydrogenUse.S.Value) / 10, OxygenUsageMultiplier = Math.Round(_oxygenUse.S.Value) / 10, PlayerInventoryMultiplier = Math.Round(_playerInventory.S.Value) / 10, CargoInventoryMultiplier = Math.Round(_cargoInventory.S.Value) / 10
        };
        string error = settings.Validate();
        if (error != null) { MpMenu.Report(error); return null; }
        return settings;
    }
    private static bool Optional(string text, out double? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (!double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) || !double.IsFinite(parsed) || Math.Abs(parsed) > int.MaxValue) return false;
        value = parsed;
        return true;
    }
    private void Save() { var s = EditedSettings(); if (s != null) MpMenu.SaveSettings(s); }
    private void StartHost() { var s = EditedSettings(); if (s == null) return; MpMenu.SaveSettings(s); MpMenu.Activate(s.HostOptions(false)); }
}







