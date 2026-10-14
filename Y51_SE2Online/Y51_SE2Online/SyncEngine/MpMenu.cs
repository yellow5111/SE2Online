using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Media;
using HarmonyLib;
using Keen.Game2.Client.UI.Library;
using Keen.Game2.Client.UI.Library.Dialogs.TwoOptionsDialog;
using Keen.Game2.Client.UI.Menu;
using Keen.Game2.Client.UI.Menu.MainMenu;
using Keen.Game2.Client.UI.Menu.InGameMenu;
using Keen.VRage.Library.Localization;
using Keen.VRage.Library.Utils;
using Keen.VRage.UI.Screens;
using Keen.VRage.UI.Shared.Helpers;
using Steamworks;
using Y51_SE2Online.Core;
using Y51_SE2Online.Net;
using Y51_SE2Online.NetHost;
using Y51_SE2Online.Steam;

namespace Y51_SE2Online.SyncEngine;

internal static class MpMenu
{
    private static readonly ConcurrentQueue<Action> Commands = new();
    private static readonly string SettingsPath = PluginPaths.Data("Y51_SE2Online.settings.json");
    private static IObservableDisposable _screen;
    private static IObservableDisposable _playersScreen;
    private static long _nextDirectory;
    private static readonly string FavoritesPath = PluginPaths.Data("Y51_SE2Online.favorites.json");
    private static readonly HashSet<string> Favorites = LoadFavorites();
    public static DirectoryListing[] Servers { get; private set; } = Array.Empty<DirectoryListing>();
    public static DirectoryListing[] Sessions { get; private set; } = Array.Empty<DirectoryListing>();
    public static bool DirectoryBusy { get; private set; }
    public static string DirectoryError { get; private set; } = "";
    public static string GameVersion => MpPlugin.CurrentGameVersion;
    private static SharedUIComponent _shared;
    private static MainMenuScreenViewModel _mainMenu;
    private static FriendFinder _finder;
    private static long _nextScan;
    private static bool _chatDown, _playersDown, _adminDown;
    private static long _nextSharedFind;
    private static IObservableDisposable _adminScreen;
    public static OnlineSettings Settings { get; private set; } = LoadSettings();
    public static string Message { get; private set; } = "";
    public static FriendHost[] Hosts { get; private set; } = Array.Empty<FriendHost>();
    public static FriendHost[] Friends { get; private set; } = Array.Empty<FriendHost>();
    public static bool HasWorld => MpPlugin.App?.Client != null || MpPlugin.App?.Server != null;
    public static bool Busy => MpPlugin.Controller?.Busy == true;

    public static void Install(Harmony harmony)
    {
        SocialUi.Install(harmony);
        AdminAccess.Install(harmony);
        harmony.Patch(AccessTools.Method(typeof(GameMenu), "UpdateButtons"), postfix: new HarmonyMethod(typeof(MpMenu), nameof(MenuButtonsPostfix)));
        var returnToMenu = AccessTools.GetDeclaredMethods(typeof(InGameMenuScreenViewModel))
            .Single(m => m.Name.Contains("g__RequestReturnToMainMenu|", StringComparison.Ordinal));
        harmony.Patch(returnToMenu, prefix: new HarmonyMethod(typeof(MpMenu), nameof(WorldExitPrefix)));
        harmony.Patch(AccessTools.Method(typeof(Keen.Game2.GameAppComponent), "ExitGame"), prefix: new HarmonyMethod(typeof(MpMenu), nameof(ApplicationExitPrefix)));
        foreach (string hostOnly in new[] { nameof(InGameMenuScreenViewModel.Save), nameof(InGameMenuScreenViewModel.SaveAs), nameof(InGameMenuScreenViewModel.Publish), nameof(InGameMenuScreenViewModel.Load) })
            harmony.Patch(AccessTools.Method(typeof(InGameMenuScreenViewModel), hostOnly), prefix: new HarmonyMethod(typeof(MpMenu), nameof(HostOnlyPrefix)));
    }

    /// <summary>Playing in someone else's world: a dedicated server or another player's session.</summary>
    public static bool Joined => MpPlugin.Controller?.Current?.Join == true && HasWorld;
    private static bool HostOnlyPrefix() => !Joined;

    private static void WorldExitPrefix() => MpPlugin.Controller?.WorldExiting(MpClock.NowMs());
    private static void ApplicationExitPrefix()
    {
        NetSession.Current?.Stop("game exiting");
        WorldExitPrefix();
    }

    public static void MenuButtonsPostfix(GameMenu __instance, StackPanel ____buttonsPanel)
    {
        if (____buttonsPanel == null) return;
        if (__instance.DataContext is MainMenuScreenViewModel main) _mainMenu = main;
        if (__instance.DataContext is not MainMenuScreenViewModel && __instance.DataContext is not InGameMenuScreenViewModel) return;
        if (__instance.DataContext is InGameMenuScreenViewModel && Joined)
        {
            // The world belongs to the host. GameMenu names its buttons after their text ids; HostOnlyPrefix blocks the actions too.
            foreach (var hostOnly in ____buttonsPanel.Children.OfType<Button>().Where(b => b.Name is "MenuSave" or "MenuSaveAs" or "MenuPublish" or "MenuLoad"))
                hostOnly.IsEnabled = false;
        }
        var button = ____buttonsPanel.Children.OfType<Button>().FirstOrDefault(b => b.Name == "MenuMultiplayer");
        if (button == null)
        {
            button = __instance.CreateButton(OnlineUi.Raw("MenuMultiplayer"), (Action)Open);
            button.Name = "MenuMultiplayer";
            ____buttonsPanel.Children.Insert(1, button);
        }
        button.IsEnabled = true;
        button.Command = SimpleCommand.Create((Action)Open);
        if (__instance.DataContext is InGameMenuScreenViewModel && !____buttonsPanel.Children.OfType<Button>().Any(b => b.Name == "OnlinePlayers"))
        {
            var players = __instance.CreateButton(OnlineUi.Raw("Players"), (Action)OpenPlayers);
            players.Name = "OnlinePlayers";
            ____buttonsPanel.Children.Insert(2, players);
        }
        if (__instance.DataContext is InGameMenuScreenViewModel)
        {
            Add("OnlineFactions", "Factions", SocialUi.OpenFactions);
            Add("OnlineOwnership", "Grid ownership", SocialUi.OpenGrids);
        }
        if (____buttonsPanel.Parent is Grid parent)
        {
            int index = parent.Children.IndexOf(____buttonsPanel);
            parent.Children.RemoveAt(index);
            var fit = new Viewbox { Name = "OnlineMenuFit", Child = ____buttonsPanel, Stretch = Stretch.Uniform,
                StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Left };
            // SE2's template reserves 193 for the logo, 20 clearance and 40 below the buttons.
            fit.Bind(Layoutable.MaxHeightProperty, new Binding(nameof(GameMenu.Bounds)) { Source = __instance,
                Converter = new FuncValueConverter<Rect, double>(bounds => Math.Max(1, bounds.Height - 253)) });
            parent.Children.Insert(index, fit);
        }
        void Add(string name, string label, Action action)
        {
            if (____buttonsPanel.Children.OfType<Button>().Any(b => b.Name == name)) return;
            var extra = __instance.CreateButton(OnlineUi.Raw(label), action); extra.Name = name;
            ____buttonsPanel.Children.Insert(Math.Min(3, ____buttonsPanel.Children.Count), extra);
        }
    }

    public static void Queue(Action action) => Commands.Enqueue(action);
    public static void Open() => Queue(OpenNow);
    public static void OpenPlayers() => Queue(() =>
    {
        if (_playersScreen != null) return;
        var shared = JoinStatusUi.FindShared(MpPlugin.App);
        if (shared == null) return;
        _playersScreen = shared.CreateScreen<PlayersScreen>(new PlayersScreenViewModel(), showCursor: true, ScreenLayer.Top);
        _playersScreen.OnDisposed += _ => _playersScreen = null;
    });
    public static void JoinAddress(string address) => Activate(Settings.JoinAddressOptions(address));
    public static void JoinListing(DirectoryListing listing)
    {
        listing.Validate();
        if (!listing.MatchesBuild(MpPlugin.Version, GameVersion)) { Report(listing.PluginVersion != MpPlugin.Version ? VersionMismatch.Explain(listing.PluginVersion, MpPlugin.Version) : "This host runs SE2 " + listing.GameVersion + "; you have " + GameVersion + "."); return; }
        if (listing.Players >= listing.MaxPlayers) { Report("The session is full."); return; }
        if (listing.Kind == "dedicated")
        {
            if (string.IsNullOrWhiteSpace(listing.Address)) { Report("This dedicated server has not published its IP address and port."); return; }
            JoinAddress(listing.Address);
        }
        else if (ulong.TryParse(listing.SteamId, out ulong id) && id != 0) Activate(Settings.JoinOptions(id, false, listing.VirtualPort));
        else JoinAddress(listing.Address);
    }
    public static bool IsFavorite(DirectoryListing listing) { lock (Favorites) return Favorites.Contains(listing.FavoriteKey); }
    public static void ToggleFavorite(DirectoryListing listing)
    {
        listing.Validate();
        lock (Favorites)
        {
            if (!Favorites.Remove(listing.FavoriteKey)) Favorites.Add(listing.FavoriteKey);
            var tmp = FavoritesPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Favorites));
            File.Move(tmp, FavoritesPath, true);
        }
    }
    private static HashSet<string> LoadFavorites()
    {
        try { return File.Exists(FavoritesPath) ? JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(FavoritesPath)) ?? new() : new(); }
        catch { return new(); }
    }
    public static void RefreshDirectory() => Queue(() =>
    {
        if (DirectoryBusy) return;
        DirectoryBusy = true;
        _nextDirectory = MpClock.NowMs() + 30000;
        string servers = Settings.ServersDirectory, sessions = Settings.SessionsDirectory;
        _ = Task.Run(async () =>
        {
            var errors = new List<string>();
            using var serverClient = new DirectoryClient(servers);
            using var sessionClient = new DirectoryClient(sessions);
            Task<DirectoryListing[]> Fetch(DirectoryClient client, string kind) => client.BrowseAsync(kind);
            var serverFetch = Fetch(serverClient, "dedicated");
            var sessionFetch = Fetch(sessionClient, "session");
            DirectoryListing[] serverRows = null, sessionRows = null;
            try { serverRows = await serverFetch.ConfigureAwait(false); } catch { errors.Add("Servers directory unavailable."); }
            try { sessionRows = await sessionFetch.ConfigureAwait(false); } catch { errors.Add("Sessions directory unavailable."); }
            Queue(() =>
            {
                if (serverRows != null) Servers = serverRows;
                if (sessionRows != null) Sessions = sessionRows;
                DirectoryError = string.Join(" ", errors);
                DirectoryBusy = false;
            });
        });
    });

    private static void OpenNow()
    {
        if (_screen != null) return;
        _shared = JoinStatusUi.FindShared(MpPlugin.App);
        if (_shared == null) { Message = "The game's menu is still loading."; return; }
        _screen = _shared.CreateScreen<OnlineScreen>(new OnlineScreenViewModel(), showCursor: true, ScreenLayer.Top);
        _screen.OnDisposed += _ => { _screen = null; };
        _nextScan = 0;
        RefreshDirectory();
    }

    public static void Tick(Keen.Game2.GameAppComponent app)
    {
        KeyboardTextInput.Tick();
        while (Commands.TryDequeue(out var action))
        {
            try { action(); }
            catch (Exception ex) { Message = ex.Message; GameLog.Instance.Error("multiplayer UI: " + ex); }
        }
        Hotkeys(app);
        BlockSizeFilter.Sync(Joined);
        RespawnScreen.Tick();
        TradeUi.Tick();
        MotdUi.Tick();
        PlayerInteraction.Tick();
        PassengerSeats.Tick();
        FactionPopups.Tick();
        JoinPresence.Tick(Joined && ClientWorld.Progress.Stage == Sync.JoinStage.Playing);
        if (MpPlugin.SteamAvailable && _finder == null) { _finder = new FriendFinder(GameLog.Instance); _finder.Start(); }
        if (_finder != null)
        {
            if (_finder.TakePresenceChanged()) _nextScan = 0;
            while (_finder.TryTakeJoinRequest(out _, out var connect))
            {
                if (MpPlugin.Controller?.Current?.Join == true || Busy) continue;
                if (ConnectString.TryParse(connect, out var id, out var port)) Activate(Settings.JoinOptions(id, false, port ?? MpOptions.DefaultVirtualPort));
                else if (ConnectString.TryParseAddress(connect, out var address)) JoinAddress(address);
            }
        }
        long now = MpClock.NowMs();
        if (_screen != null && now >= _nextDirectory && !DirectoryBusy) RefreshDirectory();
        if ((_screen == null && _playersScreen == null) || now < _nextScan) return;
        _nextScan = now + 3000;
        try
        {
            if (!MpPlugin.SteamAvailable) { Message = "Start the game through Steam to use online multiplayer."; return; }
            if (_finder == null) { _finder = new FriendFinder(GameLog.Instance); _finder.Start(); }
            Hosts = _finder.Scan(now, out _, out _).Where(f => f.Hosting).OrderBy(f => f.Name).ToArray();
            var friends = new List<FriendHost>();
            for (int i = 0, count = SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate); i < count; i++)
            {
                var id = SteamFriends.GetFriendByIndex(i, EFriendFlags.k_EFriendFlagImmediate);
                if (SteamFriends.GetFriendPersonaState(id) == EPersonaState.k_EPersonaStateOffline) continue;
                friends.Add(new FriendHost(id.m_SteamID, SteamFriends.GetFriendPersonaName(id), null));
            }
            Friends = friends.OrderBy(f => f.Name).ToArray();
        }
        catch (Exception ex) { Message = "Could not refresh Steam friends: " + ex.Message; }
    }

    private static SharedUIComponent Shared(Keen.Game2.GameAppComponent app)
    {
        if (_shared == null && MpClock.NowMs() >= _nextSharedFind) { _nextSharedFind = MpClock.NowMs() + 2000; _shared = JoinStatusUi.FindShared(app); }
        return _shared;
    }

    private static bool Pressed(string binding, ref bool down)
    {
        bool now = KeyBinding.TryParse(binding, out var key) && key.IsDown(GameKeys.IsDown) && GameKeys.OwnWindowInFront();
        bool edge = now && !down;
        down = now;
        return edge;
    }

    private static void Hotkeys(Keen.Game2.GameAppComponent app)
    {
        var shared = Shared(app);
        bool chat = Pressed(Settings.ChatKey, ref _chatDown), players = Pressed(Settings.PlayersKey, ref _playersDown), admin = Pressed(Settings.AdminKey, ref _adminDown), broadcast = Pressed(Settings.BroadcastKey, ref _broadcastDown);
        bool inWorld = app.Client != null && !app._isTransitioning.IsSet && shared != null;
        ChatUi.TickHud(shared, inWorld && AdminAccess.InSession);
        AdminAccess.Tick(shared);
        BrokenMods.Tick(app, shared);
        UpdatePrompt.Tick(shared, _mainMenu != null && app.Client == null && app.Server == null && !app._isTransitioning.IsSet);
        VoiceChat.Tick(inWorld && AdminAccess.InSession, inWorld && shared.TryGetTopScreenNeedingInput() == null);
        if (!inWorld) return;
        bool free = shared.TryGetTopScreenNeedingInput() == null;
        FightLock.Tick();
        EmoteMenu.Tick(AdminAccess.InSession && !ChatUi.InputOpen && (free || EmoteMenu.IsOpen));
        if (chat && free && AdminAccess.InSession && !ChatUi.InputOpen) ChatUi.OpenInput(shared);
        if (AdminAccess.InSession) SuitBroadcast(app, broadcast && free && !ChatUi.InputOpen);
        if (players)
        {
            if (_playersScreen != null) _playersScreen.Dispose();
            else if (free) OpenPlayers();
        }
        if (admin)
        {
            if (_adminScreen != null) _adminScreen.Dispose();
            else if (!free) { }
            else if (!AdminAccess.InSession || AdminAccess.LocalRole == Sync.OnlineRole.Player)
                AdminAccess.Notice(shared, "Admin panel", AdminAccess.InSession ? "You are not an admin on " + AdminAccess.ServerName + ", so you can't use the admin panel." : "The admin panel is available while you host a multiplayer session or are an admin on a server.");
            else
            {
                _adminScreen = shared.CreateScreen<AdminScreen>(new AdminScreenViewModel(), showCursor: true, ScreenLayer.Top);
                _adminScreen.OnDisposed += _ => _adminScreen = null;
            }
        }
    }

    private static bool _broadcastDown;
    private static long _nextBroadcastSend;

    /// <summary>Toggles the suit broadcast and keeps the host told: the state is resent every few seconds, so a
    /// rejoin, a host restart or a rate-limited request all settle without extra bookkeeping.</summary>
    private static void SuitBroadcast(Keen.Game2.GameAppComponent app, bool toggle)
    {
        long now = MpClock.NowMs();
        if (toggle)
        {
            var settings = Settings;
            settings.SuitBroadcast = !settings.SuitBroadcast;
            try { File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true })); }
            catch (IOException ex) { GameLog.Instance.Warn("suit broadcast: saving the setting failed: " + ex.Message); }
            try
            {
                app.ClientSession?.SessionComponents.TryGet<Keen.Game2.Client.UI.InGame.SessionInGameUISessionComponent>()?.ShowNotification(
                    Keen.Game2.Client.UI.HUD.Notification.HudNotification.CreateTextNotification(default, OnlineUi.Raw(settings.SuitBroadcast ? "Broadcast on: name visible" : "Broadcast off: name hidden")));
            }
            catch (Exception ex) { GameLog.Instance.WarnOnce("suit-broadcast-toast", "suit broadcast: the HUD notice failed: " + ex.Message); }
            _nextBroadcastSend = 0;
        }
        if (now < _nextBroadcastSend) return;
        _nextBroadcastSend = now + 5000;
        SocialUi.Send(new Sync.SocialRequest(Sync.SocialAction.Broadcast, Offset: Settings.SuitBroadcast ? 1 : 0));
    }

    public static void Report(string message) => Queue(() => Message = message);

    public static void RefreshFriends() => Queue(() => _nextScan = 0);

    public static void Activate(MpOptions options)
    {
        Queue(() =>
        {
            if (Busy) return;
            if (options.Join && HasWorld)
            {
                _shared = JoinStatusUi.FindShared(MpPlugin.App);
                _shared.ShowDialog(new TwoOptionsDialogViewModel(new TwoOptionsDialogDefinition
                {
                    Title = OnlineUi.Raw("Join multiplayer"),
                    Content = OnlineUi.Raw("Joining leaves the current world and returns to the main menu first. Save your world before continuing."),
                    ConfirmOption = OnlineUi.Raw("Leave and join"),
                    CancelOption = OnlineUi.Raw("Cancel")
                }) { ConfirmAction = () => Queue(() => Request(options)) });
                return;
            }
            Request(options);
        });
    }

    private static void Request(MpOptions options)
    {
        Message = "";
        MpPlugin.Controller.Request(options, MpClock.NowMs());
    }

    public static void Leave() => Queue(() => { if (!Busy) Request(null); });
    public static void Retry() => Queue(() =>
    {
        var options = MpPlugin.Controller?.Current;
        if (!Busy && options?.Join == true && ClientWorld.Current?.IdleAfterJoin == true) Request(options);
    });
    public static void Admin(Sync.AdminRequest request) => Queue(() => AdminAccess.Send(request));

    public static void ChooseWorld(bool create)
    {
        Queue(() =>
        {
            if (HasWorld || Busy) { Message = "A world is already open. Use the game's Load or New Game menu to change worlds."; return; }
            if (_mainMenu == null) { Message = "Wait for the main menu to finish loading."; return; }
            _screen?.Dispose();
            if (create) _mainMenu.OpenNewGameScreen(); else _mainMenu.OpenSavesScreen();
        });
    }

    public static void Invite(ulong id) => Queue(() =>
    {
        if (NetSession.Current?.IsHost != true || MpPlugin.Controller.Current?.HostIp != null) return;
        var hostId = NetSession.Current.LocalIdentity;
        bool sent = SteamFriends.InviteUserToGame(new CSteamID(id), ConnectString.Build(hostId, MpPlugin.Controller.Current.VirtualPort));
        Message = sent ? "Invitation sent." : "Steam could not send the invitation.";
    });

    public static void SaveSettings(OnlineSettings settings)
    {
        Queue(() =>
        {
            string error = settings.Validate();
            if (error != null) { Message = error; return; }
            var temporary = SettingsPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, SettingsPath, true);
            var current = MpPlugin.Controller.Current;
            var before = Settings;
            Settings = settings;
            Message = "Settings saved.";
            if (current?.Host != true || current.Dedicated || Busy) return;
            // Restarting hosting drops every player (0.14.4 restarted on every save, which players saw as the world dying);
            // only a change to the listen socket needs it, and the host is asked first.
            var change = HostSettingsChange.Compare(before, settings);
            NetSession.Current?.ApplyHostSettings(before, settings, change.Live);
            if (change.NextSession.Count > 0) Message = "Settings saved. " + string.Join(", ", change.NextSession) + " apply the next time you start hosting.";
            if (change.Restart)
            {
                bool local = current.HostIp != null;
                SocialUi.Confirm("Restart hosting", "Changing the listen address, port or direct IP connections restarts hosting. Connected players reconnect automatically. Restart now?",
                    () => { if (MpPlugin.Controller.Current?.Host == true && !Busy) Request(Settings.HostOptions(local)); });
                Message = "Settings saved. The listen address and port change when hosting restarts.";
            }
        });
    }

    /// <summary>"Skip this version" in the update dialog; written directly so it doesn't touch the session or the status line.</summary>
    public static void RememberSkippedUpdate(string version) => Queue(() =>
    {
        Settings.SkippedUpdate = version;
        var temporary = SettingsPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(Settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, SettingsPath, true);
    });

    private static OnlineSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var settings = JsonSerializer.Deserialize<OnlineSettings>(File.ReadAllText(SettingsPath));
                // 256 KiB/s was the default before 0.13.3; nobody picked it, so move saved settings to the new default.
                if (settings != null && settings.StateBudgetKiB == 256) settings.StateBudgetKiB = 1024;
                // Before 0.14 listing was on by default; nobody chose it, so it starts off until the host turns it on.
                if (settings != null && !settings.AdvertiseChosen) settings.Advertise = false;
                if (settings != null && settings.Validate() == null) return settings;
            }
        }
        catch (Exception ex) { GameLog.Instance.Warn("Could not load multiplayer settings: " + ex.Message); }
        return new OnlineSettings();
    }

    public static void Dispose()
    {
        _finder?.Dispose();
        _finder = null;
        _screen?.Dispose();
        _screen = null;
        _playersScreen?.Dispose();
        _playersScreen = null;
        _adminScreen?.Dispose();
        _adminScreen = null;
        ChatUi.Close();
    }
}
