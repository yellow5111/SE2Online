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
using Y51_SE2Online.Probe;
using Y51_SE2Online.Sync;
using Y51_SE2Online.SyncEngine;
using Y51_SE2Online.Dedicated;

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
		_log.Info($"Y51_SE2Online {Version} loaded from {(PluginPaths.InMemory ? "memory" : typeof(MpPlugin).Assembly.Location)} as {typeof(MpPlugin).Assembly.GetName().Name}, data in {PluginPaths.DataDir}; game {gameVersion} (built against {"2.4.0.95"}); {RuntimeInformation.FrameworkDescription} {RuntimeInformation.ProcessArchitecture}");
		if (gameVersion != "2.4.0.95")
		{
			_log.Warn($"game version {gameVersion} differs from the build this plugin was compiled and verified against ({"2.4.0.95"})");
		}
		RegisterMetadata();
		Instance = this;
		LaunchArgs = host.Args ?? Array.Empty<string>();
		_options = new MpOptions();
		host.OnBeforeProjectsLoaded += OnBeforeProjectsLoaded;
		host.OnBeforeEngineInstantiated += OnBeforeEngineInstantiated;
		Optional("plugin texts", () => OnlineUi.InstallRawTexts(_harmony));
		Controller = new OnlineSessionController(new OnlineRuntime(this));
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
		WorldSocial.Install(_harmony);
		TurretRelations.Install(_harmony);
		PvpRules.Install(_harmony);
		Optional("control panel names", () => NameSync.Install(_harmony));
		Optional("map guard", () => MapGuard.Install(_harmony));
		Optional("shared discoveries", () => SharedDiscoveries.Install(_harmony));
		Optional("join status", () => JoinStatusUi.Install(_harmony));
		Optional("block size names", () => BlockSizeFilter.Install(_harmony));
		Optional("autosave blob fix", () => BackupBlobFix.Install(_harmony));
		Optional("resource usage", () => ResourceMultiplierRules.Install(_harmony));
		Optional("broken mods", () => BrokenMods.Install(_harmony));
		InstallExitGuards();
		if (DedicatedRuntime.Headless)
		{
			HeadlessEngine.Install(_harmony);
			HeadlessWorldLoading.Install(_harmony);
			HeadlessReplication.Install(_harmony, _log);
		}
		else
		{
			MpMenu.Install(_harmony);
			Optional("GPS sharing", GpsShareUi.Install);
			Optional("gun HUD guard", () => GunHudGuard.Install(_harmony));
			Optional("animator guard", () => AnimatorGuard.Install(_harmony));
			Optional("sound range", () => SoundRange.Install(_harmony));
			Optional("procedural voxel handoff", () => ProceduralVoxelHandoff.Install(_harmony));
			Optional("flora sector fix", () => FloraSectorSpawnFix.Install(_harmony));
			Optional("host-only saves", () => HostOnlySaves.Install(_harmony));
			Optional("water sync", () => WaterMirror.Install(_harmony));
			Optional("passenger seats", () => PassengerSeats.Install(_harmony));
			Optional("seat restore", () => SeatRestore.Install(_harmony));
			Optional("trade UI", TradeUi.Install);
			Optional("replication race guards", () => ReplicationRaceGuards.Install(_harmony));
			Optional("permission feedback", () => PermissionFeedback.Install(_harmony));
		}
		_log.Info(DedicatedRuntime.Headless ? "Y51_SE2Online dedicated bootstrap installed" : "Y51_SE2Online ready: use Multiplayer in the main or pause menu");
	}

	// A game update that renames a member a feature patches turns off only that feature; patches it made before the throw stay until Dispose.
	private void Optional(string feature, Action install)
	{
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
		if (hosting | joining)
		{
			AssertHandler.Install();
			hooks.AddRange(ReplicationProbe.Install(_modeHarmony, _log));
		}
		if (hosting)
		{
			if (!ReplicationProbe.MeterArmed)
			{
				_log.Error("host world sync: the S2C command tap is OFF (see the probe:CommitFrame line); joining players get the world but no movement");
			}
			HostWorld hostWorld = HostWorld.Install(_log);
			hostWorld.SpawnNearHost = _options.SpawnNearHost;
			hostWorld.OnePilot = _options.OnePilot;
			hooks.Add(Patch("host:CommitFrame", HookTargets.CommitFrame, new HarmonyMethod(typeof(HostWorld), "CommitPrefix"), new HarmonyMethod(typeof(HostWorld), "CommitPostfix")));
			hooks.Add(Patch("host:BuildAndReset", HookTargets.BuildAndReset, null, new HarmonyMethod(typeof(HostWorld), "BundlePostfix")));
			HostWorld.InstallOreMarkers(_installingMode ? _modeHarmony : _harmony);
			if (!DedicatedRuntime.Headless)
			{
				// Encounters spawn through ReplicateStagedEntitiesAsync, which never passes BuildAndReset; its bundle reaches the local client here.
				(_installingMode ? _modeHarmony : _harmony).Patch(AccessTools.Method(AccessTools.Inner(typeof(Keen.VRage.Multiplayer.Replications.InProcess.ServerSceneContext), "ClientSide"), "BuildClientEntitiesStaged"), new HarmonyMethod(typeof(HostWorld), "StagedBundlePrefix"));
			}
			HostSettings settings = new HostSettings(_options.Identities, _options.Grids, _options.Research);
			HostWorld.Current.ConfigureIdentities(settings);
			if (settings.Identities)
			{
				if (!DedicatedRuntime.Headless)
					hooks.Add(Patch("host:PerPlayerData.Init", HookTargets.PerPlayerInit, new HarmonyMethod(typeof(HostIdentities), "InitPrefix"), new HarmonyMethod(typeof(HostIdentities), "InitPostfix")));
				hooks.Add(Patch("host:ServerNarrator.MoveToClient", HookTargets.NarratorMoveToClient, new HarmonyMethod(typeof(HostIdentities), "MoveToClientPrefix")));
				hooks.AddRange(PatchPermissions("host"));
			}
		}
		if (joining)
		{
			ClientWorld.Install(_log, _options);
			hooks.Add(Patch("client:GameAppComponent.GameUpdate", HookTargets.GameUpdate, new HarmonyMethod(typeof(ClientWorld), "GameUpdatePrefix")));
			hooks.Add(Patch("client:NetworkGameClientCoreComponent.ConfigureSession", HookTargets.NetClientConfigure, new HarmonyMethod(typeof(ClientWorld), "ConfigurePrefix")));
			hooks.Add(Patch("client:NetworkGameClientComponent.PullDataFromServerAsync", HookTargets.NetClientPull, new HarmonyMethod(typeof(ClientWorld), "PullPrefix")));
			hooks.Add(Patch("client:CommitFrame", HookTargets.CommitFrame, new HarmonyMethod(typeof(ClientWorld), "CommitPrefix")));
			hooks.Add(Patch("client:Session.Update", HookTargets.SessionUpdate, new HarmonyMethod(typeof(ClientWorld), "SessionUpdatePrefix"), new HarmonyMethod(typeof(ClientWorld), "SessionUpdatePostfix")));
			SlowTicks.Log = _log;
			SlowTicks.Enabled = true;
			hooks.Add(Patch("client:IEntityReplication.AwaitReplication", HookTargets.AwaitReplication, new HarmonyMethod(typeof(ClientWorld), "AwaitReplicationPrefix")));
			hooks.Add(Patch("client:Spectating.GetNextCharacter", HookTargets.NextCharacter, null, new HarmonyMethod(typeof(ClientWorld), "GetNextCharacterPostfix")));
			hooks.Add(Patch("client:Spectating.SetEntityTypeToPossess", HookTargets.EntityTypeToPossess, new HarmonyMethod(typeof(ClientWorld), "SetEntityTypeToPossessPrefix")));
			foreach (EngineMember key in HookTargets.SpectatorKeys)
			{
				hooks.Add(Patch("client:Spectating." + key.Name, key, new HarmonyMethod(typeof(ClientWorld), "SpectatorKeyPrefix")));
			}
			hooks.AddRange(PatchPermissions("client"));
		}
		if (joining || (hosting && !DedicatedRuntime.Headless))
		{
			hooks.Add(Patch("game:ToolsRenderAdapter.TrySpawnFirstPersonToolShadowCaster", HookTargets.ToolShadowSpawn, null, G(typeof(ToolShadowGuard), "SpawnPostfix")));
			hooks.Add(Patch("game:ToolsRenderAdapter.TryDespawnFirstPersonToolShadowCaster", HookTargets.ToolShadowDespawn, G(typeof(ToolShadowGuard), "DespawnPrefix")));
			hooks.Add(Patch("game:ToolsComponent.UnregisterTool", HookTargets.ToolsUnregister, G(typeof(ToolShadowGuard), "UnregisterPrefix"), G(typeof(ToolShadowGuard), "UnregisterPostfix")));
			hooks.Add(Patch("game:PCUClientSessionComponent.HasPCU", HookTargets.PcuHas, G(typeof(PcuOwnerGuard), "Prefix")));
			hooks.Add(Patch("game:PCUClientSessionComponent.GetPCU", HookTargets.PcuGet, G(typeof(PcuOwnerGuard), "Prefix")));
			if (_options.AudioFix)
			{
				var audioHook = Patch("audio:AudioEngineComponent.Play", HookTargets.AudioPlay2D, G(typeof(AudioGate), "Play2DPrefix"));
				hooks.Add(audioHook);
				AudioGate.Enabled = audioHook.Item2;
				hooks.Add(Patch("audio:CharacterMovementEffects.OnCrouchingUpdate", HookTargets.CrouchAudio, G(typeof(AudioGate), "CrouchPrefix"), null, G(typeof(AudioGate), "CrouchFinalizer")));
			}
		}
		if (hosting)
		{
			hooks.Add(Patch("host:InteractedEntityProviderServerComponent.TrySetEntity", HookTargets.ServerTrySetEntity, null, new HarmonyMethod(typeof(ToolTargetDiagnostics), "Postfix")));
			hooks.Add(Patch("host:InteractedEntityProviderServerComponent.Validate", HookTargets.ServerValidateHit, null, new HarmonyMethod(typeof(StandingToolReach), "ValidatePostfix")));
			hooks.Add(Patch("host:ControlPanel.DetachControlPanelList", HookTargets.PanelDetachList, G(typeof(ControlGuard), "PanelDetachPrefix"), null, G(typeof(ControlGuard), "PanelDetachFinalizer")));
			hooks.Add(Patch("host:ControlPanel.DetachAllBlockDetailsFromEntity", HookTargets.PanelDetachDetails, G(typeof(ControlGuard), "PanelDetailsPrefix")));
		}
		if (joining)
		{
			hooks.Add(Patch("client:Spectating.SetNextCharacterAsFocused", HookTargets.NextCharacterFocus, new HarmonyMethod(typeof(ClientWorld), "NextCharacterFocusPrefix")));
		}
		if ((hosting | joining) && _options.ControlFix)
		{
			ControlGuard.Start(_log);
			if (hosting)
			{
				hooks.Add(Patch("host:Spectating.GetNextCharacter", HookTargets.NextCharacter, null, new HarmonyMethod(typeof(ControlGuard), "HostNextCharacterPostfix")));
			}
		}
		if ((hosting | joining) && _options.Nametags && !DedicatedRuntime.Headless)
		{
			hooks.Add(Patch("game:GPSMarkerRender.DrawHUDMarkers", HookTargets.GpsDrawHud, new HarmonyMethod(typeof(NametagOverlay), "DrawPrefix")));
		}
		if (hosting && _options.OnePilot)
		{
			if (ControlGuard.Log == null)
			{
				ControlGuard.Log = _log;
			}
			ControlGuard.HostFlightGate = true;
			hooks.Add(Patch("host:Cockpit.TogglePower", HookTargets.CockpitTogglePower, CP()));
			hooks.Add(Patch("host:Cockpit.ToggleDampeners", HookTargets.CockpitToggleDampeners, CP()));
			hooks.Add(Patch("host:Cockpit.ToggleLights", HookTargets.CockpitToggleLights, CP()));
			hooks.Add(Patch("host:Cockpit.ToggleParkingBrakes", HookTargets.CockpitToggleBrakes, CP()));
			hooks.Add(Patch("host:Cockpit.HandleBoost", HookTargets.CockpitBoost, CP()));
		}
		if ((hosting | joining) && _options.BuildFix && !DedicatedRuntime.Headless)
		{
			BuildGuards.Log = _log;
			if (joining)
			{
				hooks.Add(Patch("client:ToppedMechanicalBlock.RequestSpawnTop", HookTargets.SpawnTop, new HarmonyMethod(typeof(BuildGuards), "SpawnTopPrefix")));
				hooks.Add(Patch("client:PaintToolControllable.OnAddedToScene", HookTargets.PaintToolAdded, new HarmonyMethod(typeof(BuildGuards), "PaintToolAddedPrefix")));
				try { _modeHarmony.Patch(AccessTools.PropertyGetter(typeof(Keen.Game2.Simulation.WorldObjects.Tools.PaintToolPerPlayerData), "Color"), prefix: new HarmonyMethod(typeof(BuildGuards), nameof(BuildGuards.PaintColorPrefix))); }
				catch (Exception ex) { _log.Warn("client: paint colour guard not installed: " + ex.Message); }
				try
				{
					var control = typeof(Keen.Game2.Client.WorldObjects.Character.CharacterMovementControlComponent);
					_modeHarmony.Patch(AccessTools.Method(control, "RequestSprint"), postfix: new HarmonyMethod(typeof(MpPlugin), nameof(SprintPostfix)));
					_modeHarmony.Patch(AccessTools.Method(control, "RequestCrouchToggle"), postfix: new HarmonyMethod(typeof(MpPlugin), nameof(CrouchPostfix)));
				}
				catch (Exception ex) { _log.Warn("client: sprint/crouch prediction hooks not installed: " + ex.Message); }
				try
				{
					var magnetic = typeof(Keen.Game2.Simulation.WorldObjects.CubeBlocks.MechanicalBlocks.MagneticBlockComponent);
					_modeHarmony.Patch(AccessTools.Method(magnetic, "OnUpdateReadyToLock"), prefix: new HarmonyMethod(typeof(MagneticBlockGuard), nameof(MagneticBlockGuard.ReadyToLockPrefix)));
					_modeHarmony.Patch(AccessTools.Method(magnetic, "Destroy"), finalizer: new HarmonyMethod(typeof(MagneticBlockGuard), nameof(MagneticBlockGuard.DestroyFinalizer)));
				}
				catch (Exception ex) { _log.Warn("client: landing gear guard not installed: " + ex.Message); }
				try
				{
					var weather = typeof(Keen.Game2.Client.WorldObjects.Weather.WeatherModifierRenderComponent);
					_modeHarmony.Patch(AccessTools.Method(weather, "OnAddedToScene"), prefix: new HarmonyMethod(typeof(WeatherRenderGuard), nameof(WeatherRenderGuard.AddedPrefix)));
					_modeHarmony.Patch(AccessTools.Method(weather, "OnBeforeRemovedFromScene"), prefix: new HarmonyMethod(typeof(WeatherRenderGuard), nameof(WeatherRenderGuard.RemovedPrefix)));
					_modeHarmony.Patch(AccessTools.GetDeclaredMethods(typeof(Keen.Game2.Client.WorldObjects.PlanetEnvironmentRenderComponent)).Single(m => m.Name.EndsWith(".OnAddedToScene", StringComparison.Ordinal)), postfix: new HarmonyMethod(typeof(WeatherRenderGuard), nameof(WeatherRenderGuard.PlanetAddedPostfix)));
				}
				catch (Exception ex) { _log.Warn("client: weather render guard not installed: " + ex.Message); }
			}
			hooks.Add(Patch("render:SceneManager.DestroyEntity", HookTargets.RenderDestroyEntity, null, null, new HarmonyMethod(typeof(BuildGuards), "RenderDestroyFinalizer")));
		}
		foreach (var (name, ok, detail) in hooks)
		{
			if (ok) _log.Info($"Harmony patch {name}: OK ({detail})");
			else _log.Error($"Harmony patch {name}: FAILED ({detail})");
		}
		if (hooks.Any(hook => !hook.Item2)) throw new InvalidOperationException("Multiplayer could not initialize for this game build. Check the game log for the failed hook.");
		List<string> patched = _modeHarmony.GetPatchedMethods().Select(m => m.DeclaringType?.Name + "." + m.Name).ToList();
		_log.Info($"Harmony patched {patched.Count} method(s): {string.Join(", ", patched)}");
		if (_options.Networking)
		{
			NetSession.Start(_options, _log, Version, GameVersion());
		}
		static HarmonyMethod CP() => new HarmonyMethod(typeof(ControlGuard), "HostCockpitPrefix");
		static HarmonyMethod G(Type t, string name) => new HarmonyMethod(t, name);
	}

	public static void Tick(Keen.Game2.GameAppComponent app)
	{
		App = app;
		if (app.Server == null) _observedWorld = null;
		Controller?.Tick(MpClock.NowMs());
		DedicatedRuntime.Tick(app);
		HostWorld.Current?.SyncMenuPause();
	}

	private void ResetMode()
	{
		if (!NetSession.ClearStopped()) throw new InvalidOperationException("The previous network thread is still running.");
		_modeHarmony.UnpatchAll(_modeHarmony.Id);
		ReplicationProbe.Stop();
		// Leaving a session must not fail on a cleanup step; each logs its own failure instead.
		try { RestoreHostRules(); } catch (Exception ex) { _log.Warn("host rules: restoring the game's definitions after hosting failed: " + ex); }
		try { if (!DedicatedRuntime.Requested) AssertHandler.Uninstall(); } catch (Exception ex) { _log.Warn("asserts: restoring the game's handler failed: " + ex); }
		PlayerInteraction.Reset();
		HostWorld.Reset();
		ClientWorld.Reset();
		HostIdentities.Reset();
		TeamPermissions.Enabled = false;
		TurretRelations.Reset();
		PvpRules.Reset();
		ControlGuard.Enabled = false;
		ControlGuard.HostFlightGate = false;
		ControlGuard.IsForeignCharacterHere = null;
		ControlGuard.SetJoinerCharacters(Array.Empty<Keen.VRage.DCS.Components.Entity>());
		ControlGuard.SetJoinerDriven(Array.Empty<Keen.VRage.DCS.Components.Entity>());
		SlowTicks.Enabled = false;
		AudioGate.Enabled = false;
		AudioRouting.Instance.Reset();
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
		var server = App?.ServerSession;
		var hostClient = HostWorld.Current?.HostClientSession?.SessionComponents.TryGet<Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent>();
		if (!(resources | inventories) || server == null || hostClient == null || hostClient.IsUnloading) return;
		// The world stays loaded after "stop hosting": its blocks and inventories are refreshed on its own server thread.
		hostClient.PostInvoke(_ =>
		{
			try
			{
				if (resources) ResourceMultiplierRules.Refresh(server);
				if (inventories) InventoryMultiplierRules.Refresh(server);
			}
			catch (Exception ex) { _log.Warn("host rules: could not refresh the loaded world after hosting: " + ex.Message); }
		});
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
				if (options.Host && _observedWorld != null) HostWorld.Current?.SetWorldSession(_observedWorld);
				ClientWorld.Current?.CaptureMainQueue("Multiplayer button");
				if (_steamReady) NetSession.Current?.SignalSteamReady("in-game multiplayer");
			}
			catch
			{
				NetSession.Current?.Stop("mode setup failed", 0);
				if (NetworkingStopped) _plugin.ResetMode();
				throw;
			}
		}
	}

	private static HarmonyMethod M(string name) => new HarmonyMethod(typeof(MpPlugin), name);

	private IEnumerable<(string, bool, string)> PatchPermissions(string side)
	{
		TeamPermissions.Log = _log;
		(_installingMode ? _modeHarmony : _harmony).Patch(AccessTools.Method(typeof(Keen.Game2.Simulation.GameSystems.Permissions.PermissionsSessionComponent), "IsFriendly"), postfix: P("IsFriendlyPostfix"));
		yield return Patch(side + ":Permissions.IsOwner", HookTargets.PermIsOwner, null, P("IsOwnerPostfix"));
		yield return Patch(side + ":Permissions.CanPlaceBlock", HookTargets.PermCanPlace, null, P("CanPlaceBlockPostfix"));
		yield return Patch(side + ":Permissions.CanUseBlockAction", HookTargets.PermCanUse, null, P("CanUseBlockActionPostfix"));
		yield return Patch(side + ":Permissions.CanLinkGrids", HookTargets.PermCanLink, null, P("CanLinkGridsPostfix"));
		yield return Patch(side + ":Permissions.CanMergeGrids", HookTargets.PermCanMerge, null, P("CanMergeGridsPostfix"));
		static HarmonyMethod P(string name) => new HarmonyMethod(typeof(TeamPermissions), name);
	}

	private static void SprintPostfix(bool sprint) => ClientWorld.SprintRequested(sprint);

	private static void CrouchPostfix() => ClientWorld.CrouchToggleRequested();

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
			MpMenu.Dispose();
			ReplicationProbe.Stop();
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
