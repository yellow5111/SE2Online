using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using HarmonyLib;
using Keen.Game2.Game.SessionComponents;
using Keen.VRage.Core.EngineComponents;
using Keen.VRage.Core.Plugins;
using Keen.VRage.Core.Project;
using Y51_SE2Online.Core;
using Y51_SE2Online.NetHost;
using Y51_SE2Online.Sync;
using Y51_SE2Online.SyncEngine;
using Y51_SE2Online.Dedicated;
using Y51_SE2Online.KeenNetcode;

namespace Y51_SE2Online;

public sealed class MpPlugin : IPlugin, IDisposable
{
	public static readonly string Version = typeof(MpPlugin).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? typeof(MpPlugin).Assembly.GetName().Version?.ToString() ?? "?";

	private readonly Harmony _harmony = new("y51.se2online");

	private readonly Harmony _modeHarmony = new("y51.se2online.session");
	private bool _installingMode;
	private static bool _steamReady;
	private static WorldSessionComponent _observedWorld;
	public static MpPlugin Instance { get; private set; }
	public static OnlineSessionController Controller { get; private set; }
	public static Keen.Game2.GameAppComponent App { get; private set; }
	public static bool SteamAvailable => _steamReady;
	/// <summary>Hosting, joined or joining, or a dedicated server: patches that change the game check this first.</summary>
	public static bool Online => DedicatedRuntime.Requested || Controller?.Current is { } current && (current.Host || current.Join);
	public static string CurrentGameVersion => GameVersion();


	private readonly IMpLog _log = GameLog.Instance;

	private MpOptions _options = new();

	private bool _disposed;

	private static int _worldInits;

	private static Func<object, bool>? _steamActive;

	public static int MainThreadId { get; private set; } = -1;

	public static string[] LaunchArgs { get; private set; } = Array.Empty<string>();

	// Injected by Pulsar before the plugin is constructed; null on a plain -plugins: load.
#pragma warning disable CS0649
	private static Func<string, string, string>? GetConfigPath;
#pragma warning restore CS0649

	internal static string? PulsarDataDir => GetConfigPath?.Invoke("Y51_SE2Online", null);

	internal static string? PulsarAssetDir { get; private set; }

	/// <summary>Called by Pulsar with its copy of the registration's AssetFolder.</summary>
	public void LoadAssets(string folder) => PulsarAssetDir = folder;

	/// <summary>Pulsar's plugin config button opens the multiplayer screen.</summary>
	public void OpenConfigDialog() { if (!_disposed) MpMenu.Open(); }

	public MpPlugin(PluginHost host)
	{
		MainThreadId = Environment.CurrentManagedThreadId;
		// Pulsar and a -plugins: launch option can both load the plugin; a second copy would patch the game twice.
		string self = PluginPaths.InMemory ? "memory (Pulsar)" : typeof(MpPlugin).Assembly.Location;
		if (AppDomain.CurrentDomain.GetData(LoadedKey) is string first)
		{
			_disposed = true;
			_log.Warn($"Y51_SE2Online is already loaded from {first}; this copy from {self} stays inactive. Remove one of them (Pulsar's plugin list or the -plugins: launch option).");
			return;
		}
		AppDomain.CurrentDomain.SetData(LoadedKey, self);
		try
		{
			Init(host);
		}
		catch (Exception ex)
		{
			_log.Error($"plugin init failed, plugin inactive (game unaffected): {ex}");
			if ((host.Args ?? Array.Empty<string>()).Contains("-y51headless")) throw;
		}
	}

	private const string LoadedKey = "Y51_SE2Online.LoadedFrom";

	// The game's -plugins: loader adds a plugin assembly to the engine's type index before constructing it; Pulsar does
	// not. Without the index, every world that builds one of this plugin's components fails to load.
	private void RegisterMetadata()
	{
		var assembly = typeof(MpPlugin).Assembly;
		var metadata = Keen.VRage.Library.Utils.Singleton<Keen.VRage.Library.Reflection.MetadataManager>.Instance;
		if (metadata.ActiveContext?.Assemblies.Contains(assembly) == true)
		{
			return;
		}
		metadata.PushContext(assembly);
		_log.Info("registered with the game's type index (the plugin loader did not)");
	}

	private void Init(PluginHost host)
	{
		string gameVersion = GameVersion();
		_log.Info($"Y51_SE2Online {Version} loaded from {(PluginPaths.InMemory ? "memory" : typeof(MpPlugin).Assembly.Location)} as {typeof(MpPlugin).Assembly.GetName().Name}, data in {PluginPaths.DataDir}; game {gameVersion} (built against {HookTargets.GameBuild}); {RuntimeInformation.FrameworkDescription} {RuntimeInformation.ProcessArchitecture}");
		if (gameVersion != HookTargets.GameBuild)
		{
			_log.Warn($"game version {gameVersion} differs from the build this plugin was compiled and verified against ({HookTargets.GameBuild})");
		}
		RegisterMetadata();
		Instance = this;
		LaunchArgs = host.Args ?? Array.Empty<string>();
		_options = new MpOptions();
		host.OnBeforeProjectsLoaded += OnBeforeProjectsLoaded;
		host.OnBeforeEngineInstantiated += OnBeforeEngineInstantiated;
		Optional("plugin texts", () => OnlineUi.InstallRawTexts(_harmony));
		Controller = new OnlineSessionController(new OnlineRuntime(this));
		SteamUsers.Install(_harmony);
		Engine.KeenOff.InstallNetworking(_harmony);
		OwnNet.Install(_harmony);
		IdentityCarryOver.Install(_harmony, _log);
		_harmony.Patch(AccessTools.Method(typeof(Keen.Game2.GameAppComponent), nameof(Keen.Game2.GameAppComponent.LoadNetworkClientAsync)), prefix: M(nameof(KeenJoinPrefix)));
		_harmony.Patch(AccessTools.Method(typeof(Keen.VRage.Core.Game.Systems.Session), "Update"), postfix: new HarmonyMethod(typeof(HostWorld), nameof(HostWorld.SessionUpdatePostfix)));
		var bootstrap = new[]
		{
			Patch("world observer", HookTargets.WorldInit, M("WorldInitPrefix")),
			Patch("Steam ready", HookTargets.SteamInit, null, M("SteamInitPostfix")),
			Patch("Steam shutdown", HookTargets.SteamShutdown, M("SteamShutdownPrefix")),
			Patch("multiplayer UI", HookTargets.GameUpdate, new HarmonyMethod(typeof(StatusOverlay), "GameUpdatePrefix"))
		};
		foreach (var hook in bootstrap)
		{
			if (!hook.Item2) throw new InvalidOperationException(hook.Item1 + ": " + hook.Item3);
		}
		DedicatedRuntime.Initialize(LaunchArgs, _harmony);
		// A dedicated server loads its world before any session starts; clients only change assertions while online.
		if (DedicatedRuntime.Requested) AssertHandler.Install();
		AutoUpdateClient.CleanUp();
		PassengerSeats.Hook();
		// Ownership, PvP and turret relations enforce the server's rules: without them the plugin stays off.
		var ruleFailures = new List<string>();
		void Rule(string name, Action install)
		{
			try { install(); }
			catch (Exception ex) { ruleFailures.Add($"{name}: {ex.GetType().Name}: {ex.Message}"); _log.Error($"{name}: not installed: {ex}"); }
		}
		Rule("ownership", () => WorldSocial.Install(_harmony));
		Rule("turret relations", () => TurretRelations.Install(_harmony));
		Rule("PvP rules", () => PvpRules.Install(_harmony));
		if (ruleFailures.Count > 0) throw new InvalidOperationException("server rules could not be installed for this game build: " + string.Join("; ", ruleFailures));
		Optional("offline survival stats", () => OfflineStatsGuard.Install(_harmony));
		Optional("shared discoveries", () => SharedDiscoveries.Install(_harmony));
		Optional("motion playback", () => MotionSmoothing.Install(_harmony));
		Optional("flora collider recovery", () => KeenNetcode.FloraPhysicsRecovery.Install(_harmony));
#if !PULSAR
		Optional("hand drill claims log", () => KeenNetcode.DrillProbe.Install(_harmony));
		Optional("swapped story answer probe", () => KeenNetcode.SwapStoryProbe.Install(_harmony));
		Optional("paste twice probe", () => KeenNetcode.PasteTwiceProbe.Install(_harmony));
#endif
		Optional("fresh grids kept from trash removal", () => KeenNetcode.FreshGridGuard.Install(_harmony));
		Optional("stuck fast travel cancelled", () => KeenNetcode.FastTravelGuard.Install(_harmony));
		Optional("turrets look past targets their own grid blocks", () => TurretRelations.InstallRetarget(_harmony));
		Optional("missions and contracts for players", () => ClientWorld.InstallMissions(_harmony));
		Optional("4.0 crash guards", () => KeenGuards.Install(_harmony));
		Optional("refused joins go back to the menu", () => ClientWorld.InstallJoinGuard(_harmony));
		Optional("projection block references", () => ProjectionRefs.Install(_harmony));
		Optional("ghost block delete ownership", () => GhostBlocks.Install(_harmony));
		Optional("resilient replication", () => ResilientReplication.Install(_harmony));
		Optional("cockpit toolbars for players", () => KeenNetcode.ToolbarRequests.Install(_harmony));
		Optional("terminal waits for this player's data", () => KeenNetcode.TerminalGuard.Install(_harmony));
		Optional("jetpack flames after a repeated state", () => KeenNetcode.JetpackFlames.Install(_harmony));
		Optional("world saves survive a reference to an unsaved entity", () => KeenNetcode.SaveGuard.Install(_harmony));
		Optional("log noise: repeated not-serializable notes", () => KeenNetcode.LogNoise.Install(_harmony));
		Optional("SE2 Online replication engine", () => Engine.Y51Replication.Install(_harmony));
		Optional("death ragdolls follow the server", () => Engine.ClientRagdolls.Install(_harmony));
		Optional("corpses follow the server", () => Engine.CorpseMotion.Install(_harmony));
		Optional("server sync properties reach players", () => Engine.SyncProperties.Install(_harmony));
		Optional("spawned ships for players who join later", () => StagedReplication.Install(_harmony));
		Optional("suit colour and skin sync", () => SuitSync.Install(_harmony));
		Optional("join and leave toasts", () => JoinLeaveToasts.Install(_harmony));
		Optional("leaving a joined world", () => LeaveDetect.Install(_harmony));
		Optional("server-side access checks", () => ServerAccess.Install(_harmony));
		Optional("tool reach for remote players", () => ToolReach.Install(_harmony));
		Optional("hand drill on planet boulders for remote players", () => DrillReach.Install(_harmony));
		Optional("streaming radius", () => Streaming.Install(_harmony));
		Optional("seat guard (sitting while seated)", () => SeatGuard.Install(_harmony));
		Optional("control panel group names", () => NameSync.Install(_harmony));
		Optional("join status", () => JoinStatusUi.Install(_harmony));
		Optional("SE2 Online loading screen", () => LoadScreenOverlay.Install(_harmony));
		Optional("client clock", () => KeenNetcode.ClientClock.Install(_harmony));
		Optional("own character movement", () => KeenNetcode.OwnMotion.Install(_harmony));
		Optional("animation timing", () => KeenNetcode.AnimPlayout.Install(_harmony));
		Optional("own character animation", () => KeenNetcode.OwnAnimation.Install(_harmony));
#if !PULSAR
		Optional("animation timing probe", () => KeenNetcode.AnimSyncProbe.Install(_harmony));
		Optional("projection probe", () => KeenNetcode.ProjectionProbe.Install(_harmony));
#endif
		Optional("block size names", () => BlockSizeFilter.Install(_harmony));
		Optional("autosave blob fix", () => BackupBlobFix.Install(_harmony));
		Optional("resource usage", () => ResourceMultiplierRules.Install(_harmony));
		Optional("broken mods", () => BrokenMods.Install(_harmony));
		Optional("custom rules", () => CustomRulesHost.Install(_harmony));
		InstallExitGuards();
		Optional("build previews", () => BuildGhosts.Install(_harmony));
		Optional("paste once", () => PasteOnce.Install(_harmony));
		if (!DedicatedRuntime.Headless)
		{
			MpMenu.Install(_harmony);
			Optional("GPS sharing", GpsShareUi.Install);
			Optional("sound range", () => SoundRange.Install(_harmony));
			Optional("full antenna and player names on the HUD", () => HudNameWidth.Install(_harmony));
			Optional("flora sector fix", () => FloraSectorSpawnFix.Install(_harmony));
			Optional("host-only saves", () => HostOnlySaves.Install(_harmony));
			Optional("trade UI", TradeUi.Install);
			Optional("third-person camera memory", () => CameraMemory.Install(_harmony));
			Optional("passenger seats", () => PassengerSeats.Install(_harmony));
			Optional("water sync", () => WaterMirror.Install(_harmony));
			Optional("cockpit passengers", () => CockpitPassenger.Install(_harmony));
			Optional("permission feedback", () => PermissionFeedback.Install(_harmony));
			Optional("spawn menu", () => KeenRespawn.Install(_harmony));
			Optional("procedural asteroid hand-over", () => ProceduralVoxelHandoff.Install(_harmony));
			Optional("weapon HUD guard", () => GunHudGuard.Install(_harmony));
			Optional("map guard", () => MapGuard.Install(_harmony));
			Optional("turret direct control", () => TurretControl.Install(_harmony));
			Optional("build sounds at the block", () => BuildAudio.Install(_harmony));
			Optional("other players' cockpit and landing gear sounds", () => PlayerAudio.Install(_harmony));
		}
		// SE2 4.0 ships the game's own multiplayer behind -enableMultiplayer. SE2 Online's own netcode leaves it off: no
		// joinable game session, no lobby join, no Steam join requests to the game.
		Keen.Game2.Simulation.GameFeatureConfig.MultiplayerEnabled = false;
		_log.Info(DedicatedRuntime.Headless ? "Y51_SE2Online dedicated bootstrap installed" : "Y51_SE2Online ready: use Multiplayer in the main or pause menu");
	}

	// A game update that renames a member a feature patches turns off only that feature; patches it made before the throw stay until Dispose.
	private void Optional(string feature, Action install)
	{
#if !PULSAR
		// Test rig: -y51online:skip:<feature> leaves an optional feature out (bisecting a crash); "all" skips every one.
		if (feature != "4.0 crash guards" && feature != "resilient replication" && LaunchArgs.Any(a => a.StartsWith(MpOptions.Prefix + "skip:", StringComparison.OrdinalIgnoreCase)
			&& a[(MpOptions.Prefix.Length + 5)..] is var skip && (skip == "all" || feature.Contains(skip, StringComparison.OrdinalIgnoreCase))))
		{
			_log.Info(feature + ": skipped (launch option)");
			return;
		}
#endif
		try { install(); }
		catch (Exception ex) { _log.Warn($"{feature}: not installed for this game build, feature off ({ex.GetType().Name}: {ex.Message})"); }
	}

	// Installed once: leaving to the menu resets mode patches, and quitting from the menu afterwards still needs them.
	private void InstallExitGuards()
	{
		var hooks = new List<(string, bool, string)>();
		try
		{
			if (!DedicatedRuntime.Headless)
			{
				RenderExitGuard.Prepare();
				hooks.Add(Patch("exit:ManagedTextureStreamingComponent.Destroy", HookTargets.RenderTextureDestroy, new HarmonyMethod(typeof(RenderExitGuard), "DestroyPrefix")));
			}
			hooks.Add(Patch("exit:CharacterMovementComponent.OnEntityRemoved", HookTargets.CharacterEntityRemoved, new HarmonyMethod(typeof(CharacterRemovalGuard), "OnEntityRemovedPrefix")));
		}
		catch (Exception ex)
		{
			hooks.Add(("exit guards", false, ex.Message));
		}
		foreach (var (name, ok, why) in hooks)
		{
			if (ok) _log.Info($"Harmony patch {name}: OK ({why})");
			else _log.Warn($"Harmony patch {name}: FAILED ({why}); exit crash workaround off");
		}
	}

	private void InstallMode(MpOptions options)
	{
		_options = options;
		_installingMode = true;
		PvpRules.Active = options.Host;
		PvpRules.Pvp = TurretRelations.PlayersHostile = options.Host && options.Pvp;
		PvpRules.OfflineRaid = options.OfflineRaidProtection;
		NametagOverlay.Configure(options);
		var hooks = new List<(string, bool, string)>();
		bool hosting = _options.Networking && _options.Host && _options.Sync;
		bool joining = _options.Networking && _options.Join && _options.Sync;
		if (hosting)
		{
			HostWorld hostWorld = HostWorld.Install(_log);
			hostWorld.SpawnNearHost = _options.SpawnNearHost;
			hostWorld.OnePilot = _options.OnePilot;
			hostWorld.DesyncChecks = _options.DesyncChecks;
			HostSettings settings = new HostSettings(_options.Identities, _options.Grids, _options.Research);
			hostWorld.ConfigureIdentities(settings);
			if (settings.Identities) hooks.AddRange(PatchPermissions("host"));
		}
		if (joining)
		{
			ClientWorld.Install(_log, _options);
			hooks.AddRange(PatchPermissions("client"));
		}
		if ((hosting | joining) && _options.Nametags && !DedicatedRuntime.Headless)
		{
			hooks.Add(Patch("game:GPSMarkerRender.DrawHUDMarkers", HookTargets.GpsDrawHud, new HarmonyMethod(typeof(NametagOverlay), "DrawPrefix")));
		}
		foreach (var (name, ok, detail) in hooks)
		{
			if (ok) _log.Info($"Harmony patch {name}: OK ({detail})");
			else _log.Error($"Harmony patch {name}: FAILED ({detail})");
		}
		if (hooks.Any(hook => !hook.Item2)) throw new InvalidOperationException("Multiplayer could not initialize for this game build. Check the game log for the failed hook.");
		if (_options.Networking)
		{
			NetSession.Start(_options, _log, Version, GameVersion());
		}
	}

	public static void Tick(Keen.Game2.GameAppComponent app)
	{
		App = app;
		OwnNet.KeepGameMultiplayerOff();
		long started = ServerProfiler.Now;
		KytheraVeh.Tick();
		if (app.Server == null) _observedWorld = null;
		Controller?.Tick(MpClock.NowMs());
		ServerProfiler.Lap(ServerSection.Online, started);
		DedicatedRuntime.Tick(app);
		started = ServerProfiler.Now;
		NetSession.Current?.Client?.ApplyRulesOffers();
		ClientWorld.Current?.MainTick(app);
		ClientWorld.ReturnOrphanJoin(app);
		HostWorld.Current?.MainTick(app);
		Y51_SE2Online.Engine.Y51Replication.Watchdog();
		ServerProfiler.Lap(ServerSection.Online, started);
#if !PULSAR
		if (_steamReady || DedicatedRuntime.Requested) OwnMotionProbe.FakeLag();
#endif
		Instance?.AutoJoin(app);
		Y51_SE2Online.Steam.SteamJoins.Tick();
		RunParkedJoin();
#if !PULSAR
		if (_steamReady) UiShots.Tick(app);
		MotionProbe.Tick(app);
		KeenNetcode.BuildProbe.Client(GameLog.Instance, MpClock.NowMs());
#endif
	}

	private void ResetMode()
	{
		if (!NetSession.ClearStopped()) throw new InvalidOperationException("The previous network thread is still running.");
		_modeHarmony.UnpatchAll(_modeHarmony.Id);
		// Leaving a session must not fail on a cleanup step; each logs its own failure instead.
		try { RestoreHostRules(); } catch (Exception ex) { _log.Warn("host rules: restoring the game's definitions after hosting failed: " + ex); }
		try { if (!DedicatedRuntime.Requested) AssertHandler.Uninstall(); } catch (Exception ex) { _log.Warn("asserts: restoring the game's handler failed: " + ex); }
		PlayerInteraction.Reset();
		HostWorld.Reset();
		ClientWorld.Reset();
		TeamPermissions.Enabled = false;
		TurretRelations.Reset();
		PvpRules.Reset();
		Sync.OfflineStats.Reset();
		NametagOverlay.Enabled = false;
		NametagOverlay.ResetAll();
		_installingMode = false;
	}

	// The usage and inventory rules rewrite global definitions, which outlive hosting: without this a single-player world
	// played after hosting ran with the host's x0/x10 until the game restarted.
	private void RestoreHostRules()
	{
		bool resources = ResourceMultiplierRules.Restore();
		bool inventories = InventoryMultiplierRules.Restore();
		if (!(resources | inventories) || App?.Server == null) return;
		// The world stays loaded after "stop hosting": its blocks and inventories are refreshed on its own server thread.
		HostWorld.PostToServer(server =>
		{
			try
			{
				if (resources) ResourceMultiplierRules.Refresh(server);
				if (inventories) InventoryMultiplierRules.Refresh(server);
			}
			catch (Exception ex) { _log.Warn("host rules: could not refresh the loaded world after hosting: " + ex.Message); }
		});
	}

	private bool _autoJoined;

	// -y51online:autojoin:<address[:port]> joins that server once the main menu is up (desktop shortcuts, tests).
	private bool AutoJoinPending => !_autoJoined && LaunchArgs.Any(a => a.StartsWith(MpOptions.Prefix + "autojoin:", StringComparison.OrdinalIgnoreCase));

	private void AutoJoin(Keen.Game2.GameAppComponent app)
	{
#if !PULSAR
		// Rig: -y51online:selftest-rejoin joins again after the self-test left, in the same game run.
		if (_autoJoined && KeenNetcode.SelfTest.TakeRejoin()) _autoJoined = false;
#endif
		if (_autoJoined || Controller == null || Controller.Busy || !_steamReady || app.Client != null || app.Server != null || app._isTransitioning.IsSet) return;
		string arg = LaunchArgs.FirstOrDefault(a => a.StartsWith(MpOptions.Prefix + "autojoin:", StringComparison.OrdinalIgnoreCase));
		_autoJoined = true;
		// Steam started the game from a friend's "Join Game" (their connect line is the launch options).
		if (arg == null && Y51_SE2Online.Steam.SteamJoins.LaunchJoin(LaunchArgs) is { } launch)
		{
			_log.Info("session: joining from Steam's launch options");
			MpMenu.Activate(launch);
			return;
		}
		if (arg == null) return;
		string address = arg.Substring((MpOptions.Prefix + "autojoin:").Length);
		_log.Info("session: joining " + address + " (launch option)");
		if (address.StartsWith("local", StringComparison.OrdinalIgnoreCase))
		{
			// -y51online:autojoin:local[:port]: a game this Steam account hosts on this PC (test play).
			int port = address.Length > 6 && int.TryParse(address.Substring(6), out int p) ? p : MpMenu.Settings?.ListenPort ?? MpOptions.DefaultIpPort;
			Controller.Request(MpOptions.Parse(new[] { MpOptions.Prefix + "join-address:127.0.0.1:" + port }), MpClock.NowMs());
			return;
		}
		Controller.Request(MpOptions.Parse(new[] { MpOptions.Prefix + "join-address:" + address }), MpClock.NowMs());
	}

	// Every SE2 4.0 join (join code screen, Steam invite, -localDedicatedClient) goes through SE2 Online first: it reads and
	// downloads the host's mods, which the game's own join never does, and its join session must exist for the host to admit us.
	private static bool KeenJoinPrefix(Keen.VRage.Core.Services.GameSessions.GameSessionID sessionID, ref Keen.VRage.Library.Threading.Task __result)
	{
		if (ClientWorld.Joining || Instance == null || Controller == null) return true;
		// While a session change is under way the game's own join used to run unwatched, and its end (refused, failed) went
		// to the game's crash monitor; it waits for the change to finish and goes through SE2 Online like any other.
		if (Controller.Busy)
		{
			_parkedJoin = sessionID;
			GameLog.Instance.Info($"session: the game asked to join {sessionID} while a session change is under way; it joins once that is done");
		}
		else RequestKeenJoin(sessionID);
		__result = Keen.VRage.Library.Threading.TaskExtensions.StripResult(Keen.VRage.Library.Threading.Task.FromResult(true));
		return false;
	}

	private static Keen.VRage.Core.Services.GameSessions.GameSessionID? _parkedJoin;

	private static void RequestKeenJoin(Keen.VRage.Core.Services.GameSessions.GameSessionID sessionID)
	{
		// SE2 Online joins over its own link: a server address is joined by address; nothing else maps.
		if (sessionID is not Keen.VRage.Core.Services.GameSessions.GameSessionIPEndpoint { IPEndpoint: { } address })
		{
			GameLog.Instance.Warn($"session: the game asked to join {sessionID}; SE2 Online joins servers by Steam friend, invite, server list or address, so this join is dropped");
			return;
		}
		GameLog.Instance.Info($"session: the game asked to join {sessionID}; SE2 Online joins {address}");
		Controller!.Request(MpOptions.Parse(new[] { MpOptions.Prefix + "join-address:" + address }), MpClock.NowMs());
	}

	/// <summary>Main thread: a game join parked while the controller was busy goes once it is free.</summary>
	private static void RunParkedJoin()
	{
		if (System.Threading.Volatile.Read(ref _parkedJoin) == null || Controller == null || Controller.Busy) return;
		// A failed change keeps its error on screen: the parked join is dropped, not run over it.
		if (Controller.State == OnlineSessionState.Failed)
		{
			if (System.Threading.Interlocked.Exchange(ref _parkedJoin, null) is { } dropped) GameLog.Instance.Info($"session: the game's join of {dropped} is dropped (the session change failed: {Controller.Error})");
			return;
		}
		// A launch-option join goes first.
		if (Instance?.AutoJoinPending == true) return;
		if (System.Threading.Interlocked.Exchange(ref _parkedJoin, null) is { } parked) RequestKeenJoin(parked);
	}

	private sealed class OnlineRuntime : IOnlineRuntime
	{
		private readonly MpPlugin _plugin;
		public OnlineRuntime(MpPlugin plugin) => _plugin = plugin;
		public bool HasWorld => App?.Client != null || App?.Server != null;
		public bool IsTransitioning => App?._isTransitioning.IsSet == true;
		public bool NetworkingStopped => (NetSession.Current == null || NetSession.Current.HasStopped) && (!HasWorld || !HostWorld.CleanupPending);
		public void StopNetworking() => NetSession.Current?.Stop("multiplayer mode changed", 0);
		public void ReturnToMenu() => App.Disconnect();
		public void ResetMode()
		{
			try { _plugin.ResetMode(); }
			catch (Exception ex) { _plugin._log.Warn("session: leaving the multiplayer mode failed: " + ex); throw; }
		}
		public void StartMode(MpOptions options)
		{
			try
			{
				_plugin.InstallMode(options);
				// _observedWorld is cleared on any tick without a server, which a load can have after the world's init: a world
				// loaded in single player and hosted from the pause menu is then taken from the running server.
				var world = _observedWorld ?? App?.Server?.Entity.TryGet<WorldSessionComponent>();
				if (options.Host && world != null) HostWorld.Current?.SetWorldSession(world);
				ClientWorld.Current?.CaptureMainQueue("Multiplayer button");
				if (_steamReady) NetSession.Current?.SignalSteamReady("in-game multiplayer");
			}
			catch
			{
				NetSession.Current?.Stop("mode setup failed", 0);
				if (NetworkingStopped) _plugin.ResetMode();
				// An automatic rejoin whose join never started does not count; the player's next join starts from zero.
				ClientWorld.PlayerLeft();
				throw;
			}
		}
	}

	private static HarmonyMethod M(string name) => new HarmonyMethod(typeof(MpPlugin), name);

	private IEnumerable<(string, bool, string)> PatchPermissions(string side)
	{
		TeamPermissions.Log = _log;
		(_installingMode ? _modeHarmony : _harmony).Patch(AccessTools.Method(typeof(Keen.Game2.Simulation.GameSystems.Permissions.PermissionsSessionComponent), "IsFriendly"), postfix: P("IsFriendlyPostfix"));
		(_installingMode ? _modeHarmony : _harmony).Patch(AccessTools.Method(typeof(Keen.Game2.Simulation.GameSystems.Factions.FactionsSessionComponent), "InSameFaction"), prefix: P("InSameFactionPrefix"));
		(_installingMode ? _modeHarmony : _harmony).Patch(AccessTools.Method(typeof(Keen.Game2.Simulation.GameSystems.PCU.PCUSessionComponent), "GetPCUPool"), prefix: P("PcuPoolPrefix"));
		yield return Patch(side + ":Permissions.IsOwner", HookTargets.PermIsOwner, null, P("IsOwnerPostfix"));
		yield return Patch(side + ":Permissions.CanPlaceBlock", HookTargets.PermCanPlace, null, P("CanPlaceBlockPostfix"));
		yield return Patch(side + ":Permissions.CanUseBlockAction", HookTargets.PermCanUse, null, P("CanUseBlockActionPostfix"));
		yield return Patch(side + ":Permissions.CanLinkGrids", HookTargets.PermCanLink, null, P("CanLinkGridsPostfix"));
		yield return Patch(side + ":Permissions.CanMergeGrids", HookTargets.PermCanMerge, null, P("CanMergeGridsPostfix"));
		static HarmonyMethod P(string name) => new HarmonyMethod(typeof(TeamPermissions), name);
	}

	private (string, bool, string) Patch(string name, EngineMember target, HarmonyMethod? prefix = null, HarmonyMethod? postfix = null, HarmonyMethod? finalizer = null)
	{
		try
		{
			MethodInfo original = EngineMembers.Method(target);
			(_installingMode ? _modeHarmony : _harmony).Patch(original, prefix, postfix, null, finalizer);
			return (name, true, "ok");
		}
		catch (Exception ex)
		{
			return (name, false, (ex is MissingMemberException) ? ex.Message : ex.ToString());
		}
	}

	private static string GameVersion()
	{
		try
		{
			Assembly assembly = typeof(WorldSessionComponent).Assembly;
			return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? assembly.GetName().Version?.ToString() ?? "?";
		}
		catch (Exception)
		{
			return "?";
		}
	}

	private void OnBeforeProjectsLoaded(List<VRageProject> projects) => _log.Info($"OnBeforeProjectsLoaded: {projects.Count} project(s)");

	private void OnBeforeEngineInstantiated(EngineBuilder engine)
	{
		_log.Info("OnBeforeEngineInstantiated (engine about to be built)");
		try
		{
			ClientWorld.Current?.CaptureMainQueue("OnBeforeEngineInstantiated");
		}
		catch (Exception)
		{
		}
	}

	private static void WorldInitPrefix(WorldSessionComponent __instance, WorldSessionComponentObjectBuilder ob)
	{
		try
		{
			int initCount = Interlocked.Increment(ref _worldInits);
			GameLog.Instance.Info($"canary: WorldSessionComponent.InitAsync #{initCount} IsServer={ob.IsServer} (Harmony prefix on R2R code works)");
			PassengerSeats.Apply();
			if (ob.IsServer)
			{
				_observedWorld = __instance;
				HostWorld.Current?.SetWorldSession(__instance);
			}
			NetSession.Current?.SignalSteamReady("world session init");
		}
		catch (Exception)
		{
		}
	}

	private static void SteamInitPostfix(object __instance)
	{
		if (DedicatedRuntime.Headless) return;
		try
		{
			ClientWorld.Current?.CaptureMainQueue("SteamGameServiceComponent.Initialize");
			if (_steamActive == null)
			{
				_steamActive = EngineMembers.Getter<bool>(EngineMembers.Property(HookTargets.Reflected.Single(m => m.Id == "steam-active")));
			}
			if (_steamActive(__instance))
			{
				_steamReady = true;
				NetSession.Current?.SignalSteamReady("SteamGameServiceComponent.Initialize");
				Y51_SE2Online.Steam.SteamJoins.Start();
			}
			else
			{
				GameLog.Instance.Error("net: the game reports Steam as NOT active (offline mode or not launched through Steam); networking disabled");
			}
		}
		catch (Exception ex)
		{
			GameLog.Instance.Warn("net: Steam init postfix failed (" + ex.Message + "); will start on world load instead");
		}
	}

	private static void SteamShutdownPrefix()
	{
		try
		{
			NetSession.Current?.Stop("game shutting down Steam");
		}
		catch (Exception)
		{
		}
	}

	public void Dispose()
	{
		if (_disposed) return;
		_disposed = true;
		try
		{
			NetSession.Current?.Stop("plugin disposed");
			Keen.Game2.Simulation.GameFeatureConfig.MultiplayerEnabled = LaunchArgs.Contains("-enableMultiplayer");
			MpMenu.Dispose();
			_modeHarmony.UnpatchAll(_modeHarmony.Id);
			_harmony.UnpatchAll(_harmony.Id);
			AssertHandler.Uninstall();
		}
		catch (Exception ex)
		{
			_log.Warn("dispose: " + ex.Message);
		}
	}
}
