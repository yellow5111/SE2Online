using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Styling;
using Avalonia.Threading;
using Keen.Game2.Simulation.WorldObjects.Items;
using Keen.VRage.Core.Game.Definitions;
using Keen.VRage.Library.Definitions;
using Keen.VRage.Library.Utils;
using Keen.VRage.Multiplayer.Data;
using Keen.VRage.UI.AvaloniaInterface.Utilities;
using Keen.VRage.UI.Screens;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

public sealed class AdminScreenViewModel : ScreenViewModel
{
    public event Action Refresh;
    private long _next;
    public AdminScreenViewModel()
    {
        KeepsOtherScreensVisible = false; AllowsInputFromLowerScreens = false; InitializeInputContext();
        OnTick += () => { if (MpClock.NowMs() < _next) return; _next = MpClock.NowMs() + 500; Refresh?.Invoke(); };
    }
    public void Close() => SignalWantsClose();
}

public sealed class AdminScreen : ScreenView
{
    private sealed record Entry(Guid Id, string Name);
    private static Entry[] _prefabs, _items;

    private readonly TextBlock _status = OnlineUi.Text("", "SubTitle");
    private readonly TextBlock _message = OnlineUi.Text("", "SubtleNotice", 21);
    private readonly TextBlock _pvp, _raid, _pcu, _sun;
    private readonly OnlineToggle _bypass = new OnlineToggle("Access bypass for me");
    private bool _syncingBypass;
    private readonly TextBox _dayInput = OnlineUi.Input("Day length (minutes)", 6);
    private readonly TextBox _pcuInput = OnlineUi.Input("PCU per player", 9);
    private readonly TextBox _motdInput = new() { Watermark = "Message of the day ({player}, {server}, {online}, {max})", MaxLength = Core.Motd.MaxText, FontSize = 22, MinHeight = 120, AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly TextBlock _motd = OnlineUi.Text("", "SubtleNotice", 22);
    private readonly ContentControl _page = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    private readonly TextBox _prefabSearch = OnlineUi.Input("Search prefabs", 64), _itemSearch = OnlineUi.Input("Search items", 64);
    private readonly TextBox _amount = OnlineUi.Input("100", 6);
    private static readonly AccessPlayer Me = new(0, "Me", OnlineRole.Player, true, 0);
    private readonly ComboBox _giveTo = new()
    {
        ItemsSource = new[] { Me }, SelectedIndex = 0, MinHeight = 40, MinWidth = 240, FontSize = 20, VerticalAlignment = VerticalAlignment.Center,
        ItemTemplate = new FuncDataTemplate<AccessPlayer>((p, _) => p == null ? null : OnlineUi.Text(p.Name, size: 20, inheritForeground: true))
    };
    private readonly (Slider S, Control Row) _sunAngle = OnlineUi.Slider(0, 359, 1, v => v + "°", 360);
    private bool _sunSyncing, _sunQueued;
    private long _sunTouchedMs;
    private double _sunStateDeg = double.NaN;
    private readonly (Slider S, Control Row) _distance = OnlineUi.Slider(5, 500, 5, v => v + " m", 260);
    private readonly TextBox _zoneName = OnlineUi.Input("Safe zone name", 64);
    private readonly (Slider S, Control Row) _zoneRadius = OnlineUi.Slider(10, 2000, 10, v => v + " m", 260), _resize = OnlineUi.Slider(10, 2000, 10, v => v + " m", 260);
    private readonly ListBox _prefabList = OnlineUi.List("AdminPrefabs"), _itemList = OnlineUi.List("AdminItems"), _zoneList = OnlineUi.List("AdminSafeZones"), _playerList = OnlineUi.List("AdminPlayers");
    private readonly Button[] _tabs;
    private readonly Control[] _pages;
    private readonly Button _spawn, _give, _createZone, _resizeZone, _deleteZone, _teleport, _bring;
    private AdminScreenViewModel _model;
    private bool _closed;
    private string _zoneSignature, _playerSignature, _prefabFilter, _itemFilter;

    public AdminScreen()
    {
        _prefabList.ItemTemplate = new FuncDataTemplate<Entry>((e, _) => e == null ? null : OnlineUi.ListCells("*", e.Name));
        _itemList.ItemTemplate = new FuncDataTemplate<Entry>((e, _) => e == null ? null : OnlineUi.ListCells("*", e.Name));
        _zoneList.ItemTemplate = new FuncDataTemplate<SafeZoneInfo>((z, _) => z == null ? null : OnlineUi.ListCells("*,200", z.Name, z.Radius.ToString("0", CultureInfo.InvariantCulture) + " m"));
        _playerList.ItemTemplate = new FuncDataTemplate<AccessPlayer>((p, _) => p == null ? null : OnlineUi.ListCells("*,160", p.Name, p.Role.ToString()));

        var creativeOn = OnlineUi.Button("Enable creative tools", () => Send(new AdminRequest(AdminAction.Creative, Value: true)), "AdminCreativeOn", true);
        var creativeOff = OnlineUi.Button("Disable creative tools", () => Send(new AdminRequest(AdminAction.Creative, Value: false)), "AdminCreativeOff", true);
        var tools = OnlineUi.Section("CREATIVE TOOLS",
            OnlineUi.Text("Creative tools apply to you only. Blocks you place and blueprints you paste are built instantly without resources, your hand welder welds without components, creative-only blocks appear in the block menu, and whole grids can be deleted.", null, 24),
            OnlineUi.Row(creativeOn, creativeOff),
            OnlineUi.Text("Paste blueprints with the game's normal blueprint screen while creative tools are on. Welding speed is world-wide and stays survival. Creative tools end when you stop being an admin. Blocks built with them give their components back when ground, as in the game's creative mode.", "SubtleNotice", 22),
            OnlineUi.Text("The game's F12 debug menu is limited to admins on this server.", "SubtleNotice", 22));
        _bypass.IsCheckedChanged += (_, _) => { if (!_syncingBypass) Send(new AdminRequest(AdminAction.AccessBypass, Value: _bypass.IsChecked)); };
        var bypass = OnlineUi.Section("ACCESS BYPASS",
            OnlineUi.Text("Nobody, the host and admins included, can use, open, sit in or build on grids and blocks they do not own or have faction or everyone access to. Turn this on to bypass that for yourself only, for this session.", null, 24),
            _bypass);
        _pvp = OnlineUi.Text("", "SubTitle");
        _raid = OnlineUi.Text("", "SubTitle");
        _pcu = OnlineUi.Text("", "SubTitle");
        var pvp = OnlineUi.Section("PVP",
            OnlineUi.Text("With PvP on, players can damage each other and turrets attack players outside their faction. With PvP off, nobody can hurt another player. Offline players' characters cannot die either way, except to other players while PvP is on.", null, 24),
            _pvp,
            OnlineUi.Row(OnlineUi.Button("PvP on", () => Send(new AdminRequest(AdminAction.Pvp, Value: true)), "AdminPvpOn", true),
                OnlineUi.Button("PvP off", () => Send(new AdminRequest(AdminAction.Pvp, Value: false)), "AdminPvpOff", true)),
            OnlineUi.Text("Admins, the host and the server console can change these (console: pvp on|off, raid on|off).", "SubtleNotice", 22),
            _raid,
            OnlineUi.Text("Offline raid protection: while a grid's owner and every member of their faction are offline, other players' weapons, explosives and grinders cannot damage it.", null, 24),
            OnlineUi.Row(OnlineUi.Button("Raid protection on", () => Send(new AdminRequest(AdminAction.OfflineRaidProtection, Value: true)), "AdminRaidOn", true),
                OnlineUi.Button("Raid protection off", () => Send(new AdminRequest(AdminAction.OfflineRaidProtection, Value: false)), "AdminRaidOff", true)),
            _pcu,
            OnlineUi.Row(_pcuInput,
                OnlineUi.Button("Set PCU limit", () => { if (int.TryParse(_pcuInput.Text, out int limit) && limit > 0) Send(new AdminRequest(AdminAction.PcuLimit, Size: limit)); }, "AdminPcuSet", true),
                OnlineUi.Button("No limit", () => Send(new AdminRequest(AdminAction.PcuLimit, Size: 0)), "AdminPcuOff", true),
                OnlineUi.Button("World default", () => Send(new AdminRequest(AdminAction.PcuLimit, Size: -1)), "AdminPcuDefault", true)));

        var motd = OnlineUi.Section("MESSAGE OF THE DAY",
            OnlineUi.Text("Shown to every player once they spawn, as a popup and in chat. {player}, {server}, {online} and {max} are filled in.", null, 24),
            _motd, _motdInput,
            OnlineUi.Row(OnlineUi.Button("Set message", () => Send(new AdminRequest(AdminAction.Motd, Name: _motdInput.Text ?? "")), "AdminMotdSet", true),
                OnlineUi.Button("Clear", () => Send(new AdminRequest(AdminAction.Motd, Name: "")), "AdminMotdClear", true)));
        _sun = OnlineUi.Text("", "SubTitle");
        // Absolute angle, so repeated sends while dragging can't add up; sent at most every 150 ms.
        _sunAngle.S.PropertyChanged += (_, e) =>
        {
            if (e.Property != Avalonia.Controls.Primitives.RangeBase.ValueProperty || _sunSyncing) return;
            _sunTouchedMs = MpClock.NowMs();
            if (_sunQueued) return;
            _sunQueued = true;
            DispatcherTimer.RunOnce(() => { _sunQueued = false; Send(new AdminRequest(AdminAction.SunSet, Size: _sunAngle.S.Value)); }, TimeSpan.FromMilliseconds(150));
        };
        _dayInput.Width = 260;
        var sun = OnlineUi.Section("SUN",
            OnlineUi.Text("The sun turns once per day. Stop it to keep the light where it is, set how long a full day lasts, or move the sun to change the time of day for everyone. Changes are saved with the world.", null, 24),
            _sun,
            OnlineUi.Row(OnlineUi.Button("Rotation on", () => Send(new AdminRequest(AdminAction.SunRotation, Value: true)), "AdminSunOn", true),
                OnlineUi.Button("Rotation off", () => Send(new AdminRequest(AdminAction.SunRotation, Value: false)), "AdminSunOff", true)),
            OnlineUi.Row(_dayInput,
                OnlineUi.Button("Set day length", () => { double day = Number(_dayInput, 0); if (day >= 1 && day <= 10080) Send(new AdminRequest(AdminAction.SunPeriod, Size: day)); }, "AdminSunDay", true)),
            OnlineUi.Row(OnlineUi.Text("Sun position", "SubTitle"), _sunAngle.Row),
            OnlineUi.Text("Server console: sun on|off, sun day <minutes>, sun move <degrees>, sun set <0-360> (both glide smoothly). server.json: SunRotation, SunPeriodMinutes.", "SubtleNotice", 22));

        _spawn = OnlineUi.Button("Spawn in front of me", () => { if (_prefabList.SelectedItem is Entry e) Send(new AdminRequest(AdminAction.SpawnPrefab, Name: e.Id.ToString(), Size: _distance.S.Value)); }, "AdminSpawnPrefab", true);
        _prefabSearch.TextChanged += (_, _) => RefreshCatalogs();
        var spawn = Page(_prefabSearch, _prefabList, OnlineUi.Row(OnlineUi.Text("Distance", "SubTitle"), _distance.Row, _spawn));

        _give = OnlineUi.Button("Give", () => { if (_itemList.SelectedItem is Entry e) Send(new AdminRequest(AdminAction.SpawnItem, (_giveTo.SelectedItem as AccessPlayer)?.SteamId ?? 0, Name: e.Id.ToString(), Size: Number(_amount, 1))); }, "AdminGiveItem", true);
        _itemSearch.TextChanged += (_, _) => RefreshCatalogs();
        var items = Page(_itemSearch, _itemList, OnlineUi.Row(OnlineUi.Text("Amount", "SubTitle"), _amount, OnlineUi.Text("To", "SubTitle"), _giveTo, _give));

        _createZone = OnlineUi.Button("Create at my position", () => Send(new AdminRequest(AdminAction.CreateSafeZone, Name: _zoneName.Text ?? "", Size: _zoneRadius.S.Value)), "AdminCreateSafeZone", true);
        _resizeZone = OnlineUi.Button("Resize", () => { if (_zoneList.SelectedItem is SafeZoneInfo z) Send(new AdminRequest(AdminAction.DeleteSafeZone, z.NetId, Size: _resize.S.Value)); }, "AdminResizeSafeZone");
        _deleteZone = OnlineUi.Button("Delete", () => { if (_zoneList.SelectedItem is SafeZoneInfo z) SocialUi.Confirm("Delete safe zone", "Delete '" + z.Name + "'?", () => AdminAccess.Send(new AdminRequest(AdminAction.DeleteSafeZone, z.NetId))); }, "AdminDeleteSafeZone");
        _zoneName.Width = 420; _distance.S.Value = 25; _zoneRadius.S.Value = 100; _resize.S.Value = 100;
        _zoneList.SelectionChanged += (_, _) => { if (_zoneList.SelectedItem is SafeZoneInfo z) _resize.S.Value = Math.Clamp(z.Radius, 10, 2000); };
        var zones = Page(OnlineUi.Row(_zoneName, OnlineUi.Text("Radius", "SubTitle"), _zoneRadius.Row, _createZone), _zoneList, OnlineUi.Row(_resize.Row, _resizeZone, _deleteZone));

        _teleport = OnlineUi.Button("Teleport to player", () => { if (_playerList.SelectedItem is AccessPlayer p) Send(new AdminRequest(AdminAction.TeleportTo, p.SteamId)); }, "AdminTeleportTo", true);
        _bring = OnlineUi.Button("Bring player to me", () => { if (_playerList.SelectedItem is AccessPlayer p) Send(new AdminRequest(AdminAction.Bring, p.SteamId)); }, "AdminBring", true);
        var players = Page(OnlineUi.Text("Kick, ban and promote players from the Players screen (" + MpMenu.Settings.PlayersKey + ").", "SubtleNotice", 22), _playerList, OnlineUi.Row(_teleport, _bring));

        _pages = new Control[] { OnlineUi.Scroll(OnlineUi.Stack(tools, bypass, pvp, motd, sun)), spawn, items, zones, players };
        string[] names = { "Creative", "Spawn", "Items", "Safe zones", "Players" };
        _tabs = new Button[names.Length];
        for (int i = 0; i < names.Length; i++) { int index = i; _tabs[i] = OnlineUi.Button(names[i], () => Select(index), "Admin" + names[i].Replace(" ", "")); _tabs[i].Classes.Add("OnlineNavigation"); }
        Styles.Add(new Style(s => s.OfType<Button>().Class("OnlineNavigation").Class("Selected")) { Setters = { new Setter(Button.BorderThicknessProperty, new Thickness(3)), new Setter(Button.BorderBrushProperty, new VRageDynamicResource("Primary")), new Setter(Button.FontWeightProperty, Avalonia.Media.FontWeight.Bold) } });
        foreach (var list in new[] { _prefabList, _itemList, _zoneList, _playerList }) list.SelectionChanged += (_, _) => RefreshActions();
        var connection = OnlineUi.Stack(_status, _message); connection.Spacing = 6;
        Content = OnlineUi.Shell(this, "ADMIN", OnlineUi.Row(_tabs), connection, _page, OnlineUi.Row());
        OnDisposing += _ => { _closed = true; if (_model != null) _model.Refresh -= QueueRefresh; };
        Select(0);
    }

    private static Control Page(Control top, Control list, Control bottom)
    {
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        top.Margin = new Thickness(0, 0, 0, 12); grid.Children.Add(top);
        Grid.SetRow(list, 1); grid.Children.Add(list);
        bottom.Margin = new Thickness(0, 12, 0, 0); Grid.SetRow(bottom, 2); grid.Children.Add(bottom);
        return OnlineUi.Panel(grid);
    }

    private static double Number(TextBox box, double fallback)
        => double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value) ? value : fallback;

    private static void Send(AdminRequest request) => MpMenu.Admin(request);

    private void Select(int index)
    {
        _page.Content = _pages[index];
        for (int i = 0; i < _tabs.Length; i++) { if (i == index) _tabs[i].Classes.Add("Selected"); else _tabs[i].Classes.Remove("Selected"); }
        RefreshControls();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_model != null) _model.Refresh -= QueueRefresh;
        _model = DataContext as AdminScreenViewModel;
        if (_model != null) _model.Refresh += QueueRefresh;
    }

    private void QueueRefresh() => Dispatcher.UIThread.Post(() => { if (!_closed) RefreshControls(); });

    private static Entry[] Catalog<T>(Func<T, string> name, Func<IEnumerable<T>> source)
    {
        try { return source().Select(d => new Entry(((Definition)(object)d).Guid, name(d))).Where(e => !string.IsNullOrWhiteSpace(e.Name)).GroupBy(e => e.Id).Select(g => g.First()).OrderBy(e => e.Name).ToArray(); }
        catch (Exception ex) { GameLog.Instance.Warn("admin panel: listing definitions failed: " + ex.Message); return Array.Empty<Entry>(); }
    }

    private static IEnumerable<PrefabDefinition> ServerPrefabs()
    {
        var manager = Singleton<DefinitionManager>.Instance;
        var list = new List<PrefabDefinition>();
        foreach (var binding in manager.GetDefinitionsOfType<PrefabBindingDefinition>())
            if (manager.TryGetDefinition(binding.ServerComposite, out PrefabDefinition prefab)) list.Add(prefab);
        return list;
    }

    private static IEnumerable<ItemDefinition> Items()
    {
        var list = new List<ItemDefinition>();
        foreach (var item in Singleton<DefinitionManager>.Instance.GetDefinitionsOfTypeAndDescendants<ItemDefinition>()) list.Add(item);
        return list;
    }

    private static string ItemName(ItemDefinition item)
    {
        string text = null;
        try { text = item.DisplayName.EvaluateText(); } catch (Exception) { }
        return string.IsNullOrWhiteSpace(text) || text.Contains('{') ? item.DebugNameShort : text + "  (" + item.DebugNameShort + ")";
    }

    private void RefreshCatalogs()
    {
        _prefabs ??= Catalog(p => p.DebugNameShort.Replace("_Server", ""), ServerPrefabs);
        _items ??= Catalog<ItemDefinition>(ItemName, Items);
        if (_prefabFilter != _prefabSearch.Text) { _prefabFilter = _prefabSearch.Text; _prefabList.ItemsSource = Filter(_prefabs, _prefabFilter); }
        if (_itemFilter != _itemSearch.Text) { _itemFilter = _itemSearch.Text; _itemList.ItemsSource = Filter(_items, _itemFilter); }
        static Entry[] Filter(Entry[] all, string text) => all.Where(e => string.IsNullOrWhiteSpace(text) || e.Name.Contains(text.Trim(), StringComparison.OrdinalIgnoreCase)).Take(300).ToArray();
    }

    private void RefreshControls()
    {
        if (_spawn == null) return;
        var state = AdminAccess.State;
        _status.Text = AdminAccess.InSession ? (AdminAccess.Hosting ? "Hosting " : "Admin on ") + (string.IsNullOrWhiteSpace(state.ServerName) ? AdminAccess.ServerName : state.ServerName) : "Not in a multiplayer session";
        _message.Text = state.Message; _message.IsVisible = !string.IsNullOrWhiteSpace(state.Message);
        _pvp.Text = state.Pvp ? "PvP is ON" : "PvP is OFF (listed as PvE)";
        _raid.Text = "Offline raid protection is " + (state.OfflineRaidProtection ? "ON" : "OFF");
        if (_bypass.IsChecked != state.AccessBypass) { _syncingBypass = true; _bypass.IsChecked = state.AccessBypass; _syncingBypass = false; }
        _pcu.Text = "PCU limit per player: " + (state.PcuLimit <= 0 || state.PcuLimit == int.MaxValue ? "unlimited" : state.PcuLimit.ToString("N0"));
        _motd.Text = string.IsNullOrWhiteSpace(state.Motd) ? "No message of the day." : "Current: " + state.Motd;
        _sun.Text = "Sun rotation is " + (state.SunRotation ? "ON" : "OFF") + (state.SunPeriodMinutes > 0 ? ", one full day every " + AccessList.DayLength(state.SunPeriodMinutes) : "");
        // Follow the host only when it reports a new angle, and not right after a drag, so a reply built before the
        // world applied the change doesn't pull the slider back.
        if (state.SunDegrees != _sunStateDeg && MpClock.NowMs() - _sunTouchedMs > 2000)
        {
            _sunStateDeg = state.SunDegrees;
            _sunSyncing = true; _sunAngle.S.Value = Math.Round(state.SunDegrees) % 360; _sunSyncing = false;
        }
        if (_page.Content == _pages[1] || _page.Content == _pages[2]) RefreshCatalogs();
        string zones = string.Join("|", state.SafeZones.Select(z => z.NetId + z.Name + z.Radius));
        if (zones != _zoneSignature)
        {
            uint selected = (_zoneList.SelectedItem as SafeZoneInfo)?.NetId ?? 0;
            _zoneSignature = zones; _zoneList.ItemsSource = state.SafeZones;
            _zoneList.SelectedItem = state.SafeZones.FirstOrDefault(z => z.NetId == selected);
        }
        var players = state.Players.Where(p => p.Connected && p.SteamId != 0).ToArray();
        string roster = string.Join("|", players.Select(p => p.SteamId + p.Name));
        if (roster != _playerSignature)
        {
            ulong selected = (_playerList.SelectedItem as AccessPlayer)?.SteamId ?? 0;
            _playerSignature = roster; _playerList.ItemsSource = players;
            _playerList.SelectedItem = players.FirstOrDefault(p => p.SteamId == selected);
            ulong recipient = (_giveTo.SelectedItem as AccessPlayer)?.SteamId ?? 0;
            var choices = new[] { Me }.Concat(players).ToArray();
            _giveTo.ItemsSource = choices; _giveTo.SelectedItem = choices.FirstOrDefault(p => p.SteamId == recipient) ?? Me;
        }
        RefreshActions();
    }

    private void RefreshActions()
    {
        if (_spawn == null) return;
        bool admin = AdminAccess.InSession && AdminAccess.LocalRole != OnlineRole.Player;
        _spawn.IsEnabled = admin && _prefabList.SelectedItem is Entry;
        _give.IsEnabled = admin && _itemList.SelectedItem is Entry;
        _createZone.IsEnabled = admin;
        _resizeZone.IsEnabled = _deleteZone.IsEnabled = admin && _zoneList.SelectedItem is SafeZoneInfo;
        _teleport.IsEnabled = _bring.IsEnabled = admin && _playerList.SelectedItem is AccessPlayer;
    }
}
