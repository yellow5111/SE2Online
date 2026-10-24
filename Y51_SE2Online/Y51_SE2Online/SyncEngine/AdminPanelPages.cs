using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Threading;
using Keen.Game2.Simulation.WorldObjects.Items;
using Keen.VRage.Core.Game.Definitions;
using Keen.VRage.Library.Definitions;
using Keen.VRage.Library.Utils;
using Keen.VRage.Multiplayer.Data;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

/// <summary>The admin panel's pages. Every control is shown only with its permission key; the server checks every action anyway.</summary>
public sealed partial class AdminScreen
{
    private sealed record Entry(Guid Id, string Name);
    private sealed record Pick(ulong SteamId, string Name, string Group, bool Online, long LastSeenUnixMs);
    private sealed record Choice(string Label, string Value);
    private sealed record BanRow(BanEntry Ban, string Remaining);
    private static Entry[] _prefabs, _items;
    private static readonly AccessPlayer Me = new(0, "Me", OnlineRole.Player, true, 0);
    private static readonly Choice[] Categories = { new("All", "all"), new("Floating items", "floating"), new("Debris", "debris"), new("NPC grids", "npc"), new("Player grids", "player") };
    private static readonly Choice[] UsageKinds = { new("Energy", "energy"), new("Hydrogen", "hydrogen"), new("Oxygen", "oxygen") };

    // Players
    private readonly TextBox _playerSearch = Box("Search players", 64);
    private readonly ListBox _playerList = List("AdminPlayers", 250);
    private readonly TextBlock _playerName = OnlineUi.Text("", "Bright", 24), _playerGroup = Info(""), _playerId = Info(""), _rankHint = Hint("");
    private readonly TextBox _reason = Box("Reason (shown to the player)", 200), _banMinutes = Box("Ban minutes, 0 = permanent", 7, 250);
    private readonly ComboBox _groupPick = Combo<GroupInfo>(g => g.Name + "  (rank " + g.Rank + ")");
    private Button _copyId;
    private string _rosterSignature, _groupsSignature;

    // Spawn
    private readonly TextBox _prefabSearch = Box("Search prefabs", 64), _itemSearch = Box("Search items", 64), _amount = Box("100", 6, 110);
    private readonly ListBox _prefabList = List("AdminPrefabs", 470), _itemList = List("AdminItems", 470);
    private readonly ComboBox _giveTo = Combo<AccessPlayer>(p => p.Name, 220);
    private readonly (Slider S, Control Row) _distance = OnlineUi.Slider(5, 500, 5, v => v + " m", 300);
    private Button _prefabTab, _itemTab;
    private Control _prefabPart, _itemPart;
    private bool _itemsMode;
    private string _prefabFilter, _itemFilter, _recipientSignature;

    // Zones
    private readonly TextBox _zoneName = Box("Safe zone name", 64);
    private readonly (Slider S, Control Row) _zoneRadius = OnlineUi.Slider(10, 2000, 10, v => v + " m", 300), _resize = OnlineUi.Slider(10, 2000, 10, v => v + " m", 300);
    private readonly ListBox _zoneList = List("AdminSafeZones", 300);
    private string _zoneSignature;

    // World
    private readonly TextBlock _pvp = Label(""), _raid = Label(""), _pcu = Label(""), _sun = Label(""), _motd = Hint("");
    private readonly TextBox _dayInput = Box("Day length (minutes)", 6, 240), _pcuInput = Box("PCU per player", 9, 200), _usageInput = Box("Multiplier, 1 = normal", 8, 220);
    private readonly TextBox _motdInput = new() { Watermark = "Message of the day ({player}, {server}, {online}, {max})", MaxLength = Core.Motd.MaxText, FontSize = 19, MinHeight = 110, AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly ComboBox _usageKind = Combo<Choice>(c => c.Label, 160);
    private readonly (Slider S, Control Row) _sunAngle = OnlineUi.Slider(0, 359, 1, v => v + " deg", 340);
    private bool _sunSyncing, _sunQueued;
    private long _sunTouchedMs;
    private double _sunStateDeg = double.NaN;

    // Me
    private StateToggle _creative, _god, _bypass;

    // Server
    private readonly TextBlock _serverInfo = Info(""), _cleanupBrief = Hint("");
    private readonly ListBox _banList = List("AdminBans", 220), _whitelistList = List("AdminWhitelist", 220);
    private readonly TextBox _whitelistId = Box("SteamID64 or profile link", 120, 330);
    private StateToggle _whitelistOnly;
    private string _accessSignature;

    // Perms
    private readonly TextBox _permSearch = Box("Search name or SteamID", 64), _permId = Box("Or a SteamID64 not in the list", 120), _permKey = Box("Permission key (empty: all granted keys)", 80);
    private readonly ListBox _permList = List("AdminKnownPlayers", 300);
    private readonly ComboBox _permGroup = Combo<GroupInfo>(g => g.Name + "  (rank " + g.Rank + ")");
    private readonly TextBlock _permTarget = Info(""), _permHint = Hint("");
    private string _knownSignature;

    // Entities and cleanup
    private readonly TextBox _entityFilter = Box("Filter by grid or owner name", 64), _transferTo = Box("Player name or SteamID", 64, 300);
    private readonly OnlineToggle _everywhere = new("Everywhere", "Off: grids within 20 km of you") { ColumnDefinitions = new ColumnDefinitions("*,12,Auto") };
    private readonly ListBox _entityList = List("AdminEntities", 320), _cleanupList = List("AdminCleanup", 260);
    private readonly TextBlock _entityText = Hint(""), _entityDetail = Info(""), _cleanupStatus = Info(""), _cleanupText = Hint("");
    private readonly ComboBox _category = Combo<Choice>(c => c.Label, 200);
    private AdminQuery _entities, _cleanup;
    private string _querySignature;

    // Cleanup settings (sent whole, starting from the defaults)
    private readonly CheckBox _cEnabled = Check("Cleanup on"), _cDry = Check("Dry run: report only, remove nothing"), _cFloat = Check("Floating items"),
        _cDebris = Check("Debris (unowned bits)"), _cNpc = Check("NPC grids"), _cPlayer = Check("Inactive players' grids"), _cFaction = Check("A faction member online keeps the grids");
    private readonly TextBox _cEvery = Box("", 5, 130), _cProtect = Box("", 7, 130), _cKeep = Box("", 32, 130), _cFloatAge = Box("", 6, 130), _cDebrisBlocks = Box("", 3, 130),
        _cNpcDays = Box("", 4, 130), _cPlayerDays = Box("", 5, 130), _cPlayerHours = Box("", 6, 130);

    private static CheckBox Check(string label) => new() { Content = label, FontSize = 19, MinHeight = 36 };

    private static Control Pair(string label, Control input)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("3*,12,2*"), Margin = new Thickness(0, 2) };
        var text = Info(label); text.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(text); Grid.SetColumn(input, 2); grid.Children.Add(input);
        return grid;
    }

    // ---- Players ----

    private AccessPlayer SelectedPlayer => _playerList.SelectedItem as AccessPlayer;
    private static string GroupOf(AccessPlayer p) => p.Group ?? (p.Role == OnlineRole.Host ? "Host" : p.Role == OnlineRole.Admin ? "Admin" : "Player");
    private bool IsMe(ulong steam) => steam != 0 && steam == MySteam;
    private bool Outranks(AccessPlayer p) => Owner ? p.Role != OnlineRole.Host : p.Rank < _state.Rank;
    private bool Targetable(bool self = false) => SelectedPlayer is { SteamId: not 0 } p && (self || !IsMe(p.SteamId));

    private Control PlayersPage()
    {
        _playerList.ItemTemplate = new FuncDataTemplate<AccessPlayer>((p, _) => p == null ? null : Row(p.Name, GroupOf(p), p.Connected && p.PingMs >= 0 ? p.PingMs + " ms" : "away"));
        _playerSearch.TextChanged += (_, _) => { _rosterSignature = null; RefreshControls(); };
        _playerList.SelectionChanged += (_, _) => RefreshActions();
        _groupPick.SelectionChanged += (_, _) => RefreshActions();
        _copyId = OnlineUi.SmallButton("Copy", () => { if (SelectedPlayer is { SteamId: not 0 } p) Copy(p.SteamId.ToString(CultureInfo.InvariantCulture)); }, "AdminCopySteamId");
        _playerName.HorizontalAlignment = HorizontalAlignment.Left;
        var card = OnlineUi.Panel(Stack(6, _playerName, _playerGroup, Flow(_playerId, _copyId), _rankHint), new Thickness(18));
        var online = (Func<bool>)(() => SelectedPlayer?.Connected == true);
        var actions = Flow(
            Act("Teleport to", Key(AdminAction.TeleportTo), () => Target(AdminAction.TeleportTo), () => Targetable() && online()),
            Act("Bring", Key(AdminAction.Bring), () => Target(AdminAction.Bring), () => Targetable() && online()),
            Act("Kill", Key(AdminAction.Kill), Kill, () => Targetable(self: true) && online() && (IsMe(SelectedPlayer.SteamId) || Outranks(SelectedPlayer))),
            Gate(OnlineUi.SmallButton("Give item", GiveItem, "AdminPlayerGive"), "admin.spawn.item_others", () => Targetable() && online()));
        bool Ruled() => Targetable() && Outranks(SelectedPlayer);
        var moderation = Gate(Section("KICK OR BAN", _reason, Flow(
                Act("Kick", Key(AdminAction.Kick), () => Moderate(AdminAction.Kick), () => Ruled() && online()),
                _banMinutes,
                Act("Ban", Key(AdminAction.Ban), () => Moderate(AdminAction.Ban), Ruled))),
            s => s.Can("admin.players.kick") || s.Can("admin.players.ban"));
        var group = Gate(Section("GROUP", Hint("Groups up to your own rank, for players ranked below you."), Flow(_groupPick,
                Act("Set group", Key(AdminAction.SetGroup), () => SelectedPlayer is { } p && _groupPick.SelectedItem is GroupInfo g ? new AdminRequest(AdminAction.SetGroup, p.SteamId, Name: g.Name) : null,
                    () => Ruled() && _groupPick.SelectedItem is GroupInfo))),
            "admin.players.set_group");
        return Page(_playerSearch, _playerList, card, actions, moderation, group);
    }

    private AdminRequest Target(AdminAction action) => SelectedPlayer is { SteamId: not 0 } p ? new AdminRequest(action, p.SteamId) : null;

    private AdminRequest Kill()
    {
        if (SelectedPlayer is not { SteamId: not 0 } p) return null;
        SocialUi.Confirm("Kill player", "Kill " + p.Name + "'s character? They respawn as after any death.", () => AdminAccess.Send(new AdminRequest(AdminAction.Kill, p.SteamId)));
        return null;
    }

    private AdminRequest Moderate(AdminAction action)
    {
        if (SelectedPlayer is not { SteamId: not 0 } p) return null;
        string reason = _reason.Text ?? "";
        int minutes = 0;
        string length = (_banMinutes.Text ?? "").Trim();
        if (action == AdminAction.Ban && length.Length > 0 && (!int.TryParse(length, NumberStyles.None, CultureInfo.InvariantCulture, out minutes) || minutes > 5256000))
            return Note("Enter the ban length in whole minutes (up to 10 years), or 0 for a permanent ban.");
        string what = action == AdminAction.Ban ? $"Ban {p.Name} {(minutes == 0 ? "permanently" : "for " + AccessList.Remaining(minutes * 60000L))}?" : $"Kick {p.Name}? They can join again.";
        SocialUi.Confirm(action == AdminAction.Ban ? "Ban player" : "Kick player", what + (string.IsNullOrWhiteSpace(reason) ? "" : "\nReason: " + reason), () =>
        {
            AdminAccess.Send(new AdminRequest(action, p.SteamId, reason, minutes));
            Dispatcher.UIThread.Post(() => _reason.Text = "");
        });
        return null;
    }

    private void GiveItem()
    {
        if (SelectedPlayer is not { SteamId: not 0 } p) return;
        _recipientSignature = null;
        RefreshRecipients();
        _giveTo.SelectedItem = (_giveTo.ItemsSource as AccessPlayer[])?.FirstOrDefault(r => r.SteamId == p.SteamId);
        _itemsMode = true;
        Select(_pages.FindIndex(x => x.Name == "Spawn"));
    }

    private void RefreshPlayers()
    {
        string filter = (_playerSearch.Text ?? "").Trim();
        var players = _state.Players.Where(p => p.Connected && (filter.Length == 0 || p.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))).ToArray();
        string roster = string.Join("|", players.Select(p => p.SteamId + p.Name + GroupOf(p) + p.Rank + p.PingMs / 10));
        if (roster != _rosterSignature)
        {
            var selected = SelectedPlayer;
            _rosterSignature = roster; _playerList.ItemsSource = players;
            _playerList.SelectedItem = players.FirstOrDefault(p => p.SteamId == selected?.SteamId && p.Name == selected?.Name);
        }
        string groups = string.Join("|", _state.Groups.Select(g => g.Name + g.Rank));
        if (groups != _groupsSignature)
        {
            _groupsSignature = groups;
            string a = (_groupPick.SelectedItem as GroupInfo)?.Name, b = (_permGroup.SelectedItem as GroupInfo)?.Name;
            _groupPick.ItemsSource = _state.Groups; _groupPick.SelectedItem = _state.Groups.FirstOrDefault(g => g.Name == a);
            _permGroup.ItemsSource = _state.Groups; _permGroup.SelectedItem = _state.Groups.FirstOrDefault(g => g.Name == b);
        }
    }

    private void RefreshPlayerCard()
    {
        var p = SelectedPlayer;
        _playerName.Text = p?.Name ?? "Select a player";
        _playerGroup.Text = p == null ? "" : GroupOf(p) + " / rank " + p.Rank + (p.Connected ? "" : " / away");
        _playerId.Text = p is { SteamId: not 0 } ? "SteamID " + p.SteamId : "";
        _playerId.IsVisible = _copyId.IsVisible = p is { SteamId: not 0 };
        _rankHint.Text = p == null ? ""
            : p.SteamId == 0 ? "Your group cannot see SteamIDs (admin.players.view_ids), so actions on other players are not available."
            : IsMe(p.SteamId) ? "This is you. Kick, ban and group changes never apply to yourself."
            : !Outranks(p) ? $"Only someone ranked above {p.Name} ({GroupOf(p)}, rank {p.Rank}) can kick, ban, kill them or change their group."
            : "";
        _rankHint.IsVisible = _rankHint.Text.Length > 0;
    }

    // ---- Spawn ----

    private Control SpawnPage()
    {
        _prefabList.ItemTemplate = new FuncDataTemplate<Entry>((e, _) => e == null ? null : Row(e.Name));
        _itemList.ItemTemplate = new FuncDataTemplate<Entry>((e, _) => e == null ? null : Row(e.Name));
        _prefabSearch.TextChanged += (_, _) => RefreshCatalogs();
        _itemSearch.TextChanged += (_, _) => RefreshCatalogs();
        _prefabList.SelectionChanged += (_, _) => RefreshActions();
        _itemList.SelectionChanged += (_, _) => RefreshActions();
        _giveTo.SelectionChanged += (_, _) => RefreshActions();
        _distance.S.Value = 25;
        _prefabTab = Gate(Nav("Prefabs", () => { _itemsMode = false; RefreshActions(); }, "AdminSpawnPrefabs"), "admin.spawn.prefab");
        _itemTab = Gate(Nav("Items", () => { _itemsMode = true; RefreshActions(); }, "AdminSpawnItems"), s => s.Can("admin.spawn.item") || s.Can("admin.spawn.item_others"));
        var spawn = Act("Spawn in front of me", Key(AdminAction.SpawnPrefab),
            () => _prefabList.SelectedItem is Entry e ? new AdminRequest(AdminAction.SpawnPrefab, Name: e.Id.ToString(), Size: _distance.S.Value) : null, () => _prefabList.SelectedItem is Entry);
        _prefabPart = Stack(10, _prefabSearch, _prefabList, Flow(Label("Distance"), _distance.Row), spawn);
        var give = Gate(OnlineUi.SmallButton("Give", () =>
        {
            if (_itemList.SelectedItem is Entry e && _giveTo.SelectedItem is AccessPlayer to) Send(new AdminRequest(AdminAction.SpawnItem, to.SteamId, Name: e.Id.ToString(), Size: Number(_amount, 1)));
        }, "AdminGiveItem"), s => s.Can("admin.spawn.item") || s.Can("admin.spawn.item_others"),
            () => _itemList.SelectedItem is Entry && _giveTo.SelectedItem is AccessPlayer to && _state.Can(Key(AdminAction.SpawnItem, to.SteamId)));
        _itemPart = Stack(10, _itemSearch, _itemList, Flow(Label("Amount"), _amount, Label("To"), _giveTo), give);
        return Page(Flow(_prefabTab, _itemTab), _prefabPart, _itemPart);
    }

    private void RefreshRecipients()
    {
        var others = _state.Can("admin.spawn.item_others") ? _state.Players.Where(p => p.Connected && p.SteamId != 0 && !IsMe(p.SteamId)).ToArray() : Array.Empty<AccessPlayer>();
        string signature = _state.Can("admin.spawn.item") + string.Join("|", others.Select(p => p.SteamId + p.Name));
        if (signature == _recipientSignature) return;
        _recipientSignature = signature;
        ulong recipient = (_giveTo.SelectedItem as AccessPlayer)?.SteamId ?? 0;
        var choices = (_state.Can("admin.spawn.item") ? new[] { Me } : Array.Empty<AccessPlayer>()).Concat(others).ToArray();
        _giveTo.ItemsSource = choices; _giveTo.SelectedItem = choices.FirstOrDefault(p => p.SteamId == recipient) ?? choices.FirstOrDefault();
    }

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

    // ---- Zones ----

    private Control ZonesPage()
    {
        _zoneList.ItemTemplate = new FuncDataTemplate<SafeZoneInfo>((z, _) => z == null ? null : Row(z.Name, z.Radius.ToString("0", CultureInfo.InvariantCulture) + " m"));
        _zoneRadius.S.Value = 100; _resize.S.Value = 100;
        _zoneList.SelectionChanged += (_, _) => { if (_zoneList.SelectedItem is SafeZoneInfo z) _resize.S.Value = Math.Clamp(z.Radius, 10, 2000); RefreshActions(); };
        bool Picked() => _zoneList.SelectedItem is SafeZoneInfo;
        var create = Gate(Section("NEW SAFE ZONE", _zoneName, Flow(Label("Radius"), _zoneRadius.Row),
            OnlineUi.SmallButton("Create at my position", () => Send(new AdminRequest(AdminAction.CreateSafeZone, Name: _zoneName.Text ?? "", Size: _zoneRadius.S.Value)), "AdminCreateSafeZone")), "admin.safezone.create");
        var resize = Gate(Flow(_resize.Row, Act("Resize", Key(AdminAction.DeleteSafeZone, size: 1),
            () => _zoneList.SelectedItem is SafeZoneInfo z ? new AdminRequest(AdminAction.DeleteSafeZone, z.NetId, Size: _resize.S.Value) : null, Picked)), "admin.safezone.edit");
        var delete = Act("Delete safe zone", Key(AdminAction.DeleteSafeZone), () =>
        {
            if (_zoneList.SelectedItem is SafeZoneInfo z) SocialUi.Confirm("Delete safe zone", "Delete '" + z.Name + "'?", () => AdminAccess.Send(new AdminRequest(AdminAction.DeleteSafeZone, z.NetId)));
            return null;
        }, Picked);
        var existing = Gate(Section("SAFE ZONES", _zoneList, resize, delete), s => s.Can("admin.safezone.edit") || s.Can("admin.safezone.delete"));
        return Page(create, existing);
    }

    private void RefreshZones()
    {
        string zones = string.Join("|", _state.SafeZones.Select(z => z.NetId + z.Name + z.Radius));
        if (zones == _zoneSignature) return;
        uint selected = (_zoneList.SelectedItem as SafeZoneInfo)?.NetId ?? 0;
        _zoneSignature = zones; _zoneList.ItemsSource = _state.SafeZones;
        _zoneList.SelectedItem = _state.SafeZones.FirstOrDefault(z => z.NetId == selected);
    }

    // ---- World ----

    private Control WorldPage()
    {
        var pvp = Gate(Section("PVP",
            Info("With PvP on, players can damage each other and turrets attack players outside their faction. With PvP off, nobody can hurt another player."),
            _pvp, Flow(OnlineUi.SmallButton("PvP on", () => Send(new AdminRequest(AdminAction.Pvp, Value: true)), "AdminPvpOn"), OnlineUi.SmallButton("PvP off", () => Send(new AdminRequest(AdminAction.Pvp, Value: false)), "AdminPvpOff"))),
            "admin.world.pvp");
        var raid = Gate(Section("OFFLINE RAID PROTECTION",
            Info("While a grid's owner and every member of their faction are offline, other players' weapons, explosives and grinders cannot damage it."),
            _raid, Flow(OnlineUi.SmallButton("On", () => Send(new AdminRequest(AdminAction.OfflineRaidProtection, Value: true)), "AdminRaidOn"), OnlineUi.SmallButton("Off", () => Send(new AdminRequest(AdminAction.OfflineRaidProtection, Value: false)), "AdminRaidOff"))),
            "admin.world.raid");
        var pcu = Gate(Section("PCU LIMIT", _pcu,
            Flow(_pcuInput, OnlineUi.SmallButton("Set", () => { if (int.TryParse(_pcuInput.Text, out int limit) && limit > 0) Send(new AdminRequest(AdminAction.PcuLimit, Size: limit)); else Note("Enter a PCU limit above 0."); }, "AdminPcuSet")),
            Flow(OnlineUi.SmallButton("No limit", () => Send(new AdminRequest(AdminAction.PcuLimit, Size: 0)), "AdminPcuOff"), OnlineUi.SmallButton("World default", () => Send(new AdminRequest(AdminAction.PcuLimit, Size: -1)), "AdminPcuDefault"))),
            "admin.world.pcu");

        // Absolute angle, so repeated sends while dragging can't add up; sent at most every 150 ms.
        _sunAngle.S.PropertyChanged += (_, e) =>
        {
            if (e.Property != Avalonia.Controls.Primitives.RangeBase.ValueProperty || _sunSyncing) return;
            _sunTouchedMs = MpClock.NowMs();
            if (_sunQueued) return;
            _sunQueued = true;
            DispatcherTimer.RunOnce(() => { _sunQueued = false; Send(new AdminRequest(AdminAction.SunSet, Size: _sunAngle.S.Value)); }, TimeSpan.FromMilliseconds(150));
        };
        var sun = Gate(Section("SUN",
            Info("The sun turns once per day. Stop it to keep the light where it is, set how long a day lasts, or move the sun for everyone. Saved with the world."),
            _sun,
            Flow(OnlineUi.SmallButton("Rotation on", () => Send(new AdminRequest(AdminAction.SunRotation, Value: true)), "AdminSunOn"), OnlineUi.SmallButton("Rotation off", () => Send(new AdminRequest(AdminAction.SunRotation, Value: false)), "AdminSunOff")),
            Flow(_dayInput, OnlineUi.SmallButton("Set day length", () => { double day = Number(_dayInput, 0); if (day >= 1 && day <= 10080) Send(new AdminRequest(AdminAction.SunPeriod, Size: day)); else Note("Day length is 1 to 10080 minutes."); }, "AdminSunDay")),
            Label("Sun position"), _sunAngle.Row),
            "admin.world.sun");
        var motd = Gate(Section("MESSAGE OF THE DAY",
            Info("Shown to every player once they spawn, as a popup and in chat. {player}, {server}, {online} and {max} are filled in."),
            _motd, _motdInput,
            Flow(OnlineUi.SmallButton("Set message", () => Send(new AdminRequest(AdminAction.Motd, Name: _motdInput.Text ?? "")), "AdminMotdSet"), OnlineUi.SmallButton("Clear", () => Send(new AdminRequest(AdminAction.Motd, Name: "")), "AdminMotdClear"))),
            "admin.world.motd");
        _usageKind.ItemsSource = UsageKinds; _usageKind.SelectedIndex = 0;
        var usage = Gate(Section("RESOURCE USAGE",
            Info("How fast blocks use energy, hydrogen and oxygen: 1 is normal, 0.5 half as fast. Applies until the server restarts."),
            Flow(_usageKind, _usageInput, OnlineUi.SmallButton("Set", () =>
            {
                double factor = Number(_usageInput, double.NaN);
                if (double.IsNaN(factor) || factor < 0) { Note("Enter a multiplier of 0 or more, 1 for normal."); return; }
                Send(new AdminRequest(AdminAction.Usage, Name: (_usageKind.SelectedItem as Choice)?.Value ?? "energy", Size: factor));
            }, "AdminUsageSet"))),
            "admin.world.usage");
        return Page(pvp, raid, pcu, sun, motd, usage);
    }

    private void RefreshWorld()
    {
        var s = _state;
        _pvp.Text = s.Pvp ? "PvP is ON" : "PvP is OFF (listed as PvE)";
        _raid.Text = "Offline raid protection is " + (s.OfflineRaidProtection ? "ON" : "OFF");
        _pcu.Text = "PCU limit per player: " + (s.PcuLimit <= 0 || s.PcuLimit == int.MaxValue ? "unlimited" : s.PcuLimit.ToString("N0"));
        _motd.Text = string.IsNullOrWhiteSpace(s.Motd) ? "No message of the day." : "Current: " + s.Motd;
        _sun.Text = "Sun rotation is " + (s.SunRotation ? "ON" : "OFF") + (s.SunPeriodMinutes > 0 ? ", one full day every " + AccessList.DayLength(s.SunPeriodMinutes) : "");
        // Follow the host only when it reports a new angle, and not right after a drag, so a reply built before the
        // world applied the change doesn't pull the slider back.
        if (s.SunDegrees != _sunStateDeg && MpClock.NowMs() - _sunTouchedMs > 2000)
        {
            _sunStateDeg = s.SunDegrees;
            _sunSyncing = true; _sunAngle.S.Value = Math.Round(s.SunDegrees) % 360; _sunSyncing = false;
        }
    }

    // ---- Me ----

    private Control MePage()
    {
        _creative = new StateToggle("Creative tools", "Build and paste without resources, weld without components, creative blocks in the block menu, delete whole grids.",
            on => Send(new AdminRequest(AdminAction.Creative, Value: on)));
        _god = new StateToggle("God mode", "No damage; health, energy and oxygen never run out.", on => Send(new AdminRequest(AdminAction.Invulnerable, Value: on)));
        _bypass = new StateToggle("Access bypass", "Use, open, sit in and build on grids you do not own or have access to.", on => Send(new AdminRequest(AdminAction.AccessBypass, Value: on)));
        return Page(Section("FOR ME ONLY",
            Gate(_creative.Toggle, "admin.creative.tools"),
            Gate(Hint("Creative tools no longer include god mode: you still take damage and need oxygen unless god mode is on too. Blocks built with them give their components back when ground."), "admin.creative.tools"),
            Gate(_god.Toggle, "admin.creative.godmode"),
            Gate(_bypass.Toggle, "admin.access_bypass"),
            Gate(Hint("Nobody, the host and admins included, can use what they do not own or have faction or everyone access to, unless the bypass is on. It lasts for this session."), "admin.access_bypass"),
            Hint("Each of these ends when your group loses its permission. The game's F12 debug menu is limited to groups that have it.")));
    }

    // ---- Server ----

    private Control ServerPage()
    {
        _banList.ItemTemplate = new FuncDataTemplate<BanRow>((b, _) => b == null ? null : Row(Display(b.Ban.Name, b.Ban.SteamId), b.Remaining, b.Ban.Reason));
        _whitelistList.ItemTemplate = new FuncDataTemplate<AccessEntry>((w, _) => w == null ? null : Row(Display(w.Name, w.SteamId), string.IsNullOrWhiteSpace(w.Name) ? null : w.SteamId.ToString(CultureInfo.InvariantCulture)));
        _banList.SelectionChanged += (_, _) => RefreshActions();
        _whitelistList.SelectionChanged += (_, _) => RefreshActions();
        _whitelistOnly = new StateToggle("Whitelist only", "Only whitelisted players, and groups allowed past it, may join.", on => Send(new AdminRequest(AdminAction.WhitelistOnly, Value: on)));
        var info = Section("SERVER", _serverInfo, _cleanupBrief,
            Gate(Hint("Saving, restarting and stopping a dedicated server run from its console or RCON, not from this panel."), s => s.Dedicated));
        var bans = Gate(Section("BANS", _banList,
            Act("Remove ban", Key(AdminAction.Unban), () => _banList.SelectedItem is BanRow b ? new AdminRequest(AdminAction.Unban, b.Ban.SteamId) : null, () => _banList.SelectedItem is BanRow)),
            "admin.players.view_bans");
        var whitelist = Gate(Section("WHITELIST", _whitelistOnly.Toggle, _whitelistList,
            OnlineUi.SmallButton("Remove from whitelist", () => { if (_whitelistList.SelectedItem is AccessEntry w) Send(new AdminRequest(AdminAction.WhitelistRemove, w.SteamId)); }, "AdminWhitelistRemove"),
            Flow(_whitelistId, OnlineUi.SmallButton("Add", AddWhitelist, "AdminWhitelistAdd"))),
            "admin.whitelist");
        return Page(info, bans, whitelist);
    }

    private static string Display(string name, ulong id) => string.IsNullOrWhiteSpace(name) ? id.ToString(CultureInfo.InvariantCulture) : name;

    private string KnownName(ulong id) => _state.Players.FirstOrDefault(p => p.SteamId == id)?.Name ?? _state.Known.FirstOrDefault(k => k.SteamId == id)?.Name;

    private void AddWhitelist()
    {
        if (!AccessList.TryParsePlayerId(_whitelistId.Text, out ulong id)) { Note("Enter a player's SteamID64 (17 digits starting 7656119) or a steamcommunity.com/profiles/ link."); return; }
        Send(new AdminRequest(AdminAction.WhitelistAdd, id, Name: KnownName(id) ?? ""));
        _whitelistId.Text = "";
    }

    private void RefreshServer()
    {
        var s = _state;
        _serverInfo.Text = (s.Dedicated ? "Dedicated server" : "Co-op session") + (s.MaxPlayers > 0 ? ", up to " + s.MaxPlayers + " players" : "") + ". "
            + (s.HasPassword ? "Password protected." : "No join password.");
        _cleanupBrief.Text = string.IsNullOrWhiteSpace(s.Cleanup) ? "" : "Cleanup: " + s.Cleanup;
        _cleanupBrief.IsVisible = _cleanupBrief.Text.Length > 0;
        _whitelistOnly.Sync(s.WhitelistOnly);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string access = s.Revision + ":" + s.Bans.Length + ":" + s.Whitelist.Length + ":" + now / 60000;
        if (access == _accessSignature) return;
        _accessSignature = access;
        ulong ban = (_banList.SelectedItem as BanRow)?.Ban.SteamId ?? 0, listed = (_whitelistList.SelectedItem as AccessEntry)?.SteamId ?? 0;
        var rows = s.Bans.Select(b => new BanRow(b, b.ExpiresUnixMs is long end ? AccessList.Remaining(end - now) : "Permanent")).ToArray();
        _banList.ItemsSource = rows; _banList.SelectedItem = rows.FirstOrDefault(b => b.Ban.SteamId == ban);
        _whitelistList.ItemsSource = s.Whitelist; _whitelistList.SelectedItem = s.Whitelist.FirstOrDefault(w => w.SteamId == listed);
    }

    // ---- Perms ----

    private Control PermsPage()
    {
        _permList.ItemTemplate = new FuncDataTemplate<Pick>((p, _) => p == null ? null : Row(p.Name, p.Group, p.Online ? "online" : Seen(p.LastSeenUnixMs)));
        _permSearch.TextChanged += (_, _) => { _knownSignature = null; RefreshControls(); };
        _permId.TextChanged += (_, _) => RefreshActions();
        _permList.SelectionChanged += (_, _) => RefreshActions();
        _permGroup.SelectionChanged += (_, _) => RefreshActions();
        bool Allowed() => PermTarget() is { } t && !IsMe(t.Id) && (Owner || RankOfGroup(t.Group) is int r && r < _state.Rank);
        var assign = Gate(Section("GROUPS",
            Hint("Online players and players seen before. Pick one, or type a SteamID64 below to reach someone not listed."),
            _permSearch, _permList, _permId, _permTarget, _permHint,
            Flow(_permGroup, Act("Put in group", Key(AdminAction.SetGroup), () => PermTarget() is { } t && _permGroup.SelectedItem is GroupInfo g ? new AdminRequest(AdminAction.SetGroup, t.Id, Name: g.Name) : null,
                () => Allowed() && _permGroup.SelectedItem is GroupInfo)),
            Act("Remove from group", Key(AdminAction.SetGroup), () => PermTarget() is { } t ? new AdminRequest(AdminAction.SetGroup, t.Id, Name: PermissionSet.DefaultGroup) : null,
                () => Allowed() && PermTarget() is { } t && t.Group != PermissionSet.DefaultGroup)),
            "admin.players.set_group");
        var check = Gate(Section("CHECK PERMISSIONS",
            Hint("Shows what the chosen player may do, for one key or all of them. The answer appears at the bottom of the panel."),
            _permKey,
            Act("Check permissions", Key(AdminAction.PermsCheck), () => PermTarget() is { } t ? new AdminRequest(AdminAction.PermsCheck, t.Id, Name: (_permKey.Text ?? "").Trim()) : Note("Choose a player or enter a SteamID64."), () => PermTarget() != null)),
            "admin.players.set_group");
        var reload = Gate(Section("PERMISSION FILES",
            Info("Reads the group files again after editing them, without a restart."),
            OnlineUi.SmallButton("Reload permission files", () => Send(new AdminRequest(AdminAction.PermsReload)), "AdminPermsReload")),
            Key(AdminAction.PermsReload));
        return Page(assign, check, reload);
    }

    private static string Seen(long unixMs) => unixMs <= 0 ? "" : AccessList.Remaining(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - unixMs) + " ago";

    private int? RankOfGroup(string group)
        => _state.Groups.FirstOrDefault(g => g.Name == group)?.Rank ?? (group == PermissionSet.DefaultGroup ? 0 : null);

    private (ulong Id, string Name, string Group)? PermTarget()
    {
        string typed = (_permId.Text ?? "").Trim();
        if (typed.Length > 0)
        {
            if (!AccessList.TryParsePlayerId(typed, out ulong id)) return null;
            var known = (_permList.ItemsSource as Pick[])?.FirstOrDefault(p => p.SteamId == id) ?? Picks().FirstOrDefault(p => p.SteamId == id);
            return (id, known?.Name ?? id.ToString(CultureInfo.InvariantCulture), known?.Group ?? PermissionSet.DefaultGroup);
        }
        return _permList.SelectedItem is Pick p ? (p.SteamId, p.Name, p.Group) : null;
    }

    private IEnumerable<Pick> Picks()
    {
        var online = _state.Players.Where(p => p.Connected && p.SteamId != 0).Select(p => new Pick(p.SteamId, p.Name, p.Group ?? PermissionSet.DefaultGroup, true, 0)).ToList();
        var ids = online.Select(p => p.SteamId).ToHashSet();
        return online.Concat(_state.Known.Where(k => !ids.Contains(k.SteamId)).OrderByDescending(k => k.LastSeenUnixMs).Select(k => new Pick(k.SteamId, k.Name, k.Group ?? PermissionSet.DefaultGroup, false, k.LastSeenUnixMs)));
    }

    private void RefreshPerms()
    {
        string filter = (_permSearch.Text ?? "").Trim();
        var picks = Picks().Where(p => filter.Length == 0 || p.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) || p.SteamId.ToString(CultureInfo.InvariantCulture).Contains(filter, StringComparison.Ordinal)).Take(300).ToArray();
        string signature = string.Join("|", picks.Select(p => p.SteamId + p.Name + p.Group + p.Online + p.LastSeenUnixMs / 60000));
        if (signature == _knownSignature) return;
        ulong selected = (_permList.SelectedItem as Pick)?.SteamId ?? 0;
        _knownSignature = signature; _permList.ItemsSource = picks;
        _permList.SelectedItem = picks.FirstOrDefault(p => p.SteamId == selected);
    }

    private void RefreshPermTarget()
    {
        bool typed = (_permId.Text ?? "").Trim().Length > 0;
        var t = PermTarget();
        _permTarget.Text = t is { } x ? $"Chosen: {x.Name} ({x.Id}), group {x.Group}" : typed ? "That is not a SteamID64 (17 digits starting 7656119)." : "Choose a player.";
        _permHint.Text = t is not { } y ? ""
            : IsMe(y.Id) ? "You cannot change your own group."
            : !Owner && !(RankOfGroup(y.Group) is int r && r < _state.Rank) ? $"Only someone ranked above the {y.Group} group can change {y.Name}'s group."
            : "";
        _permHint.IsVisible = _permHint.Text.Length > 0;
    }

    // ---- Entities ----

    private QueryRow SelectedGrid => _entityList.SelectedItem as QueryRow;

    private Control EntitiesPage()
    {
        _entityList.ItemTemplate = new FuncDataTemplate<QueryRow>((r, _) => r == null ? null : Row(r.Name, r.Owner, r.Blocks.ToString("N0") + " blocks", r.Distance >= 0 ? Distance(r.Distance) : ""));
        _entityList.SelectionChanged += (_, _) => RefreshActions();
        AdminRequest Verb(EntityVerb verb, string argument = "") => SelectedGrid is { } g ? new AdminRequest(AdminAction.EntityAction, g.NetId, Minutes: (int)verb, Name: argument) : null;
        bool Picked() => SelectedGrid != null;
        var search = Flow(Act("Refresh", Key(AdminAction.EntityQuery), () => new AdminRequest(AdminAction.EntityQuery, Name: (_entityFilter.Text ?? "").Trim(), Value: _everywhere.IsChecked)));
        var actions = Flow(
            Act("Teleport to", AdminRules.EntityKey(EntityVerb.TeleportTo), () => Verb(EntityVerb.TeleportTo), Picked),
            Act("Stop", AdminRules.EntityKey(EntityVerb.Stop), () => Verb(EntityVerb.Stop), Picked),
            Act("Power off", AdminRules.EntityKey(EntityVerb.PowerOff), () => Verb(EntityVerb.PowerOff), Picked),
            Act("Delete", AdminRules.EntityKey(EntityVerb.Delete), () =>
            {
                if (SelectedGrid is { } g) SocialUi.Confirm("Delete grid", $"Delete '{g.Name}' of {g.Owner} and every grid attached to it? This cannot be undone.",
                    () => AdminAccess.Send(new AdminRequest(AdminAction.EntityAction, g.NetId, Minutes: (int)EntityVerb.Delete)));
                return null;
            }, Picked));
        var transfer = Gate(Flow(_transferTo, Act("Transfer", AdminRules.EntityKey(EntityVerb.Transfer), () =>
            string.IsNullOrWhiteSpace(_transferTo.Text) ? Note("Enter who gets the grid: a player name or SteamID.") : Verb(EntityVerb.Transfer, _transferTo.Text.Trim()), Picked)),
            AdminRules.EntityKey(EntityVerb.Transfer));
        return Page(_entityFilter, _everywhere, search, _entityText, _entityList, _entityDetail, actions, transfer);
    }

    private static string Distance(double meters) => meters >= 10_000 ? (meters / 1000).ToString("0", CultureInfo.InvariantCulture) + " km" : meters.ToString("0", CultureInfo.InvariantCulture) + " m";

    // ---- Cleanup ----

    private Control CleanupPage()
    {
        _cleanupList.ItemTemplate = new FuncDataTemplate<QueryRow>((r, _) =>
        {
            if (r == null) return null;
            var row = Row(r.Name, r.Owner);
            if (!string.IsNullOrWhiteSpace(r.Note)) row.Children.Add(RowText(r.Note, null, 16, .8));
            return row;
        });
        _category.ItemsSource = Categories; _category.SelectedIndex = 0;
        string Category() => (_category.SelectedItem as Choice)?.Value ?? "all";
        string What() => (_category.SelectedItem as Choice)?.Label.ToLowerInvariant() ?? "all";
        AdminRequest Run(bool now)
        {
            string category = Category();
            SocialUi.Confirm("Run cleanup", now ? $"Clean up {What()} right away, without a chat warning?" : $"Start a cleanup pass for {What()}? Online players get a chat warning first.",
                () => AdminAccess.Send(new AdminRequest(AdminAction.CleanupRun, Name: category, Value: now)));
            return null;
        }
        var preview = Gate(Section("PREVIEW", _cleanupStatus, Flow(_category, Act("Preview", Key(AdminAction.CleanupPreview), () => new AdminRequest(AdminAction.CleanupPreview, Name: Category()))),
            _cleanupText, _cleanupList), "admin.cleanup");
        var run = Gate(Section("RUN",
            Info("A pass removes what the preview lists, a few things per second. The categories and their rules come from the cleanup settings."),
            Flow(OnlineUi.SmallButton("Run now with warning", () => Run(false), "AdminCleanupWarn"), OnlineUi.SmallButton("Run now", () => Run(true), "AdminCleanupNow")),
            OnlineUi.SmallButton("Cancel pass", () => Send(new AdminRequest(AdminAction.CleanupRun, Minutes: -1)), "AdminCleanupCancel")), Key(AdminAction.CleanupRun));
        LoadCleanupForm(new CleanupSettings());
        var settings = Gate(Section("SETTINGS",
            Hint("Shows the settings in force. Applies until the server restarts; the server's own settings (server.json or the Host screen) keep them."),
            _cEnabled, _cDry, Pair("Minutes between passes", _cEvery), Pair("Keep everything within (m) of a player", _cProtect), Pair("Never touch grids named with", _cKeep),
            _cFloat, Pair("Floating items older than (min)", _cFloatAge),
            _cDebris, Pair("Debris of at most (blocks)", _cDebrisBlocks),
            _cNpc, Pair("NPC grids idle for (days)", _cNpcDays),
            _cPlayer, Pair("Owners offline for (days)", _cPlayerDays), Pair("plus (hours)", _cPlayerHours), _cFaction,
            Act("Apply settings", Key(AdminAction.CleanupSettings), CleanupSettingsRequest)), Key(AdminAction.CleanupSettings));
        return Page(preview, run, settings);
    }

    private string _cleanupConfigShown;

    private void LoadCleanupForm(CleanupSettings defaults)
    {
        _cEnabled.IsChecked = defaults.Enabled; _cDry.IsChecked = defaults.DryRun; _cEvery.Text = Format(defaults.EveryMinutes); _cProtect.Text = Format(defaults.ProtectWithinMeters); _cKeep.Text = defaults.KeepTag;
        _cFloat.IsChecked = defaults.FloatingItems.Enabled; _cFloatAge.Text = Format(defaults.FloatingItems.MinAgeMinutes);
        _cDebris.IsChecked = defaults.Debris.Enabled; _cDebrisBlocks.Text = Format(defaults.Debris.MaxBlocks);
        _cNpc.IsChecked = defaults.NpcGrids.Enabled; _cNpcDays.Text = Format(defaults.NpcGrids.IdleDays);
        _cPlayer.IsChecked = defaults.PlayerGrids.Enabled; _cPlayerDays.Text = Format(defaults.PlayerGrids.InactiveDays); _cPlayerHours.Text = Format(defaults.PlayerGrids.InactiveHours); _cFaction.IsChecked = defaults.PlayerGrids.FactionShared;
    }

    private static CleanupSettings InForce(string json)
    {
        try { return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<CleanupSettings>(json); }
        catch (JsonException) { return null; }
    }

    private static string Format(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private AdminRequest CleanupSettingsRequest()
    {
        // Starts from the settings in force: fields this form does not show keep their values.
        var s = InForce(_state.CleanupConfig) ?? new CleanupSettings();
        s.Enabled = _cEnabled.IsChecked == true; s.DryRun = _cDry.IsChecked == true;
        s.EveryMinutes = (int)Math.Round(Number(_cEvery, -1)); s.ProtectWithinMeters = Number(_cProtect, double.NaN); s.KeepTag = _cKeep.Text ?? "";
        s.FloatingItems.Enabled = _cFloat.IsChecked == true; s.FloatingItems.MinAgeMinutes = Number(_cFloatAge, double.NaN);
        s.Debris.Enabled = _cDebris.IsChecked == true; s.Debris.MaxBlocks = (int)Math.Round(Number(_cDebrisBlocks, -1));
        s.NpcGrids.Enabled = _cNpc.IsChecked == true; s.NpcGrids.IdleDays = Number(_cNpcDays, double.NaN);
        s.PlayerGrids.Enabled = _cPlayer.IsChecked == true; s.PlayerGrids.InactiveDays = Number(_cPlayerDays, double.NaN); s.PlayerGrids.InactiveHours = Number(_cPlayerHours, double.NaN); s.PlayerGrids.FactionShared = _cFaction.IsChecked == true;
        if (s.Validate() is { } problem) return Note(problem);
        return new AdminRequest(AdminAction.CleanupSettings, Name: JsonSerializer.Serialize(s));
    }

    private void RefreshQueries()
    {
        _cleanupStatus.Text = string.IsNullOrWhiteSpace(_state.Cleanup) ? "No cleanup status yet." : _state.Cleanup;
        if (_state.CleanupConfig != _cleanupConfigShown && InForce(_state.CleanupConfig) is { } inForce) { _cleanupConfigShown = _state.CleanupConfig; LoadCleanupForm(inForce); }
        if (_state.Query is { } q)
        {
            string signature = q.Kind + q.Serial + ":" + q.Rows.Length + ":" + q.Text;
            if (signature != _querySignature)
            {
                _querySignature = signature;
                if (q.Kind == "entities")
                {
                    uint selected = SelectedGrid?.NetId ?? 0;
                    _entities = q; _entityList.ItemsSource = q.Rows; _entityList.SelectedItem = q.Rows.FirstOrDefault(r => r.NetId == selected && r.NetId != 0);
                }
                else if (q.Kind == "cleanup") { _cleanup = q; _cleanupList.ItemsSource = q.Rows; }
            }
        }
        _entityText.Text = _entities?.Text ?? "Refresh to list grids.";
        _cleanupText.Text = _cleanup?.Text ?? "Preview to see what a pass would remove now.";
    }

    // ---- Refresh hooks used by AdminScreen ----

    private void RefreshPages()
    {
        RefreshPlayers();
        RefreshRecipients();
        RefreshZones();
        RefreshWorld();
        RefreshServer();
        RefreshPerms();
        RefreshQueries();
        _creative.Sync(_state.Creative); _god.Sync(_state.Invulnerable); _bypass.Sync(_state.AccessBypass);
        if (_pages[_current].Name == "Spawn") RefreshCatalogs();
    }

    private void RefreshPageActions()
    {
        RefreshPlayerCard();
        RefreshPermTarget();
        bool prefabs = _state.Can("admin.spawn.prefab"), items = _state.Can("admin.spawn.item") || _state.Can("admin.spawn.item_others");
        if (_itemsMode && !items) _itemsMode = false;
        if (!_itemsMode && !prefabs && items) _itemsMode = true;
        _prefabPart.IsVisible = !_itemsMode && prefabs; _itemPart.IsVisible = _itemsMode && items;
        Mark(_prefabTab, !_itemsMode); Mark(_itemTab, _itemsMode);
        var g = SelectedGrid;
        _entityDetail.Text = g == null ? "" : $"{g.Name} / {g.Owner} / {g.Blocks:N0} blocks" + (g.Distance >= 0 ? " / " + Distance(g.Distance) : "") + (string.IsNullOrWhiteSpace(g.Note) ? "" : " / " + g.Note);
        _entityDetail.IsVisible = g != null;
    }
}
