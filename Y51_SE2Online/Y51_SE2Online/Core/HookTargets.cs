using System;

namespace Y51_SE2Online.Core;

public static class HookTargets
{
	public const string GameBuild = "4.0.0.3155";

	private const string Mp = "VRage.Multiplayer";

	private const string Cb = "Keen.VRage.Multiplayer.Sessions.ReplicationCommandBuffer";

	private const string CbBase = "Keen.VRage.DCS.Utils.CommandBufferBase";

	private const string Ipr = "Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent";

	private const string Bundle = "Keen.VRage.Multiplayer.Replications.InProcess.ServerSceneContext+ServerToClientEntityReplicationData";

	private const string SteamSvc = "Keen.VRage.Steam.EngineComponents.SteamGameServiceComponent";

	public static readonly EngineMember Replay = new EngineMember("replay", "VRage.Multiplayer", "Keen.VRage.Multiplayer.Sessions.ReplicationCommandBuffer", MemberKind.Method, "Replay", new string[2] { "System.Object", "Keen.VRage.Multiplayer.Sessions.ReplicationCommandBuffer+ReplayTable" }, "probe: command header layout + reader call (IL pinned)");

	public static readonly EngineMember CommitFrame = new EngineMember("commit", "VRage.Multiplayer", "Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent", MemberKind.Method, "CommitFrame", Array.Empty<string>(), "probe: tick counter, scene side");

	public static readonly EngineMember BuildAndReset = new EngineMember("bundle", "VRage.Multiplayer", "Keen.VRage.Multiplayer.Replications.InProcess.ServerSceneContext+ServerToClientEntityReplicationData+Builder", MemberKind.Method, "BuildAndReset", Array.Empty<string>(), "probe: entity spawn bundles");

	public static readonly EngineMember WorldInit = new EngineMember("world-init", "Game2.Game", "Keen.Game2.Game.SessionComponents.WorldSessionComponent", MemberKind.Method, "InitAsync", new string[1] { "Keen.Game2.Game.SessionComponents.WorldSessionComponentObjectBuilder" }, "canary + fallback 'Steam is up' signal");

	public static readonly EngineMember SteamInit = new EngineMember("steam-init", "VRage.Steam", "Keen.VRage.Steam.EngineComponents.SteamGameServiceComponent", MemberKind.Method, "Initialize", new string[1] { "Keen.VRage.Steam.Data.SteamServicesObjectBuilder" }, "net: start the transport once Steam is initialised");

	public static readonly EngineMember SteamShutdown = new EngineMember("steam-shutdown", "Steamworks.NET", "Steamworks.SteamAPI", MemberKind.Method, "Shutdown", Array.Empty<string>(), "net: close connections before SteamAPI shuts down");

	public static readonly EngineMember GameUpdate = new EngineMember("game-update", "SpaceEngineers2", "Keen.Game2.GameAppComponent", MemberKind.Method, "GameUpdate", Array.Empty<string>(), "client: main-thread pump that starts the client-only game");

	public static readonly EngineMember NetClientConfigure = new EngineMember("netclient-configure", "SpaceEngineers2", "Keen.Game2.OutOfProcess.NetworkGameClientCoreComponent", MemberKind.Method, "ConfigureSession", new string[1] { "Keen.VRage.Core.Game.Systems.SessionBuilder" }, "client: replication services for the client-only session")
	{
		InterfaceDispatchOnly = true
	};

	public static readonly EngineMember NetClientPull = new EngineMember("netclient-pull", "SpaceEngineers2", "Keen.Game2.OutOfProcess.NetworkGameClientComponent", MemberKind.Method, "PullDataFromServerAsync", new string[1] { "Keen.VRage.Core.Game.Systems.SessionBuilder" }, "client: session components from the host's snapshot")
	{
		InterfaceDispatchOnly = true
	};

	public static readonly EngineMember SessionUpdate = new EngineMember("session-update", "VRage.Core.Game", "Keen.VRage.Core.Game.Systems.Session", MemberKind.Method, "Update", new string[1] { "System.Boolean" }, "client: keep dead entities out of the session's close queue");

	private const string Ppd = "Keen.Game2.Simulation.GameSystems.Player.InProcessPerPlayerDataSessionComponent";

	private const string Perm = "Keen.Game2.Simulation.GameSystems.Permissions.PermissionsSessionComponent";

	private const string Ent = "Keen.VRage.DCS.Components.Entity";

	private const string Ident = "Keen.Game2.Simulation.GameSystems.Ownership.IdentityId";

	public static readonly EngineMember PerPlayerInit = new EngineMember("ppd-init", "Game2.Simulation", "Keen.Game2.Simulation.GameSystems.Player.InProcessPerPlayerDataSessionComponent", MemberKind.Method, "Init", new string[1] { "Keen.Game2.Simulation.GameSystems.Player.PerPlayerDataSessionComponentObjectBuilder" }, "phase 4 host: keep joiners' saved per-player data");

	public static readonly EngineMember NarratorMoveToClient = new EngineMember("narrator-move-to-client", "VRage.Multiplayer", "Keen.VRage.Multiplayer.Replications.InProcess.ServerSceneContext+ServerSessionComponent", MemberKind.Method, "Keen.VRage.Multiplayer.NetworkStories.INetworkStoryNarrator.MoveToClient", null, "phase 4 host: route server stories addressed to a joiner")
	{
		InterfaceDispatchOnly = true
	};

	public static readonly EngineMember PermIsOwner = new EngineMember("perm-isowner", "Game2.Simulation", "Keen.Game2.Simulation.GameSystems.Permissions.PermissionsSessionComponent", MemberKind.Method, "IsOwner", new string[2] { "Keen.VRage.DCS.Components.Entity", "Keen.Game2.Simulation.GameSystems.Ownership.IdentityId" }, "phase 4 team grids (host + joiner)");

	public static readonly EngineMember PermCanPlace = new EngineMember("perm-canplace", "Game2.Simulation", "Keen.Game2.Simulation.GameSystems.Permissions.PermissionsSessionComponent", MemberKind.Method, "CanPlaceBlock", new string[2] { "Keen.VRage.DCS.Components.Entity", "Keen.Game2.Simulation.GameSystems.Ownership.IdentityId" }, "phase 4 team grids (host + joiner)");

	public static readonly EngineMember PermCanUse = new EngineMember("perm-canuse", "Game2.Simulation", "Keen.Game2.Simulation.GameSystems.Permissions.PermissionsSessionComponent", MemberKind.Method, "CanUseBlockAction", null, "phase 4 team grids (host + joiner)");

	public static readonly EngineMember PermCanLink = new EngineMember("perm-canlink", "Game2.Simulation", "Keen.Game2.Simulation.GameSystems.Permissions.PermissionsSessionComponent", MemberKind.Method, "CanLinkGrids", null, "phase 4 team grids (host + joiner)");

	public static readonly EngineMember PermCanMerge = new EngineMember("perm-canmerge", "Game2.Simulation", "Keen.Game2.Simulation.GameSystems.Permissions.PermissionsSessionComponent", MemberKind.Method, "CanMergeGrids", null, "phase 4 team grids (host + joiner)");

	public static readonly EngineMember[] PermissionPatches = new EngineMember[5] { PermIsOwner, PermCanPlace, PermCanUse, PermCanLink, PermCanMerge };

	public static readonly EngineMember AwaitReplication = new EngineMember("await-replication", "VRage.Multiplayer", "Keen.VRage.Multiplayer.Replications.InProcess.ClientSceneContext+ClientSessionComponent", MemberKind.Method, "Keen.VRage.Multiplayer.Sessions.IEntityReplication.AwaitReplication", new string[2] { "Keen.VRage.Multiplayer.SecureIds.EntityId", "System.Object" }, "0.7.2 joiner: secure EntityIds of host models resolved by the host")
	{
		InterfaceDispatchOnly = true
	};

	public static readonly EngineMember RenderTextureDestroy = new EngineMember("render-texture-destroy", "VRage.Render12", "Keen.VRage.Render12.SceneSystem.Components.ManagedTextureStreamingComponent", MemberKind.Method, "Destroy", Array.Empty<string>(), "0.7.5: no barriers on released textures during the render engine's shutdown (exit crash)");

	public static readonly EngineMember CharacterEntityRemoved = new EngineMember("character-entity-removed", "Game2.Simulation", "Keen.Game2.Simulation.WorldObjects.Characters.CharacterMovementComponent", MemberKind.Method, "OnEntityRemoved", new string[1] { "Keen.VRage.DCS.Components.Entity" }, "0.7.5: a destroyed character's leaked removal handler is skipped (host exit crash)")
	{
		InterfaceDispatchOnly = true
	};

	private const string Spectating = "Keen.Game2.Client.WorldObjects.Spectators.SpectatingModesSessionComponent";

	public static readonly EngineMember NextCharacter = new EngineMember("spectating-next-character", "Game2.Client", "Keen.Game2.Client.WorldObjects.Spectators.SpectatingModesSessionComponent", MemberKind.Method, "GetNextCharacter", null, "0.7.5 joiner: F6 never takes another player's character");

	public static readonly EngineMember EntityTypeToPossess = new EngineMember("spectating-type-to-possess", "Game2.Client", "Keen.Game2.Client.WorldObjects.Spectators.SpectatingModesSessionComponent", MemberKind.Method, "SetEntityTypeToPossess", null, "0.7.5 joiner: 'possess a character' means the own one");

	public static readonly EngineMember NextCharacterFocus = new EngineMember("spectating-next-focus", "Game2.Client", "Keen.Game2.Client.WorldObjects.Spectators.SpectatingModesSessionComponent", MemberKind.Method, "SetNextCharacterAsFocused", Array.Empty<string>(), "0.7.6 joiner: F6 returns to the own character from a free camera");

	public static readonly EngineMember[] SpectatorKeys = new EngineMember[5]
	{
		new EngineMember("spectating-free", "Game2.Client", Spectating, MemberKind.Method, "SetFreeSpectator", Array.Empty<string>(), "joiner: free camera key is admin-only"),
		new EngineMember("spectating-static", "Game2.Client", Spectating, MemberKind.Method, "SetStatic", Array.Empty<string>(), "joiner: static spectator key is admin-only"),
		new EngineMember("spectating-delta", "Game2.Client", Spectating, MemberKind.Method, "SetDelta", Array.Empty<string>(), "joiner: delta spectator key is admin-only"),
		new EngineMember("spectating-tp-player", "Game2.Client", Spectating, MemberKind.Method, "TeleportPlayerToSpectatorAsync", Array.Empty<string>(), "joiner: teleport-to-spectator key is admin-only"),
		new EngineMember("spectating-tp-spectator", "Game2.Client", Spectating, MemberKind.Method, "TeleportSpectatorToPlayerAsync", Array.Empty<string>(), "joiner: spectator-to-player key is admin-only")
	};

	private const string ToolsRender = "Keen.Game2.Client.WorldObjects.Character.ToolsRenderAdapterComponent";

	public static readonly EngineMember ToolShadowSpawn = new EngineMember("tool-shadow-spawn", "Game2.Client", "Keen.Game2.Client.WorldObjects.Character.ToolsRenderAdapterComponent", MemberKind.Method, "TrySpawnFirstPersonToolShadowCaster", new string[1] { "Keen.VRage.DCS.Components.Entity" }, "0.7.6: which tool owns the first-person shadow caster");

	public static readonly EngineMember ToolShadowDespawn = new EngineMember("tool-shadow-despawn", "Game2.Client", "Keen.Game2.Client.WorldObjects.Character.ToolsRenderAdapterComponent", MemberKind.Method, "TryDespawnFirstPersonToolShadowCaster", new string[1] { "Keen.VRage.DCS.Components.Entity" }, "0.7.6: no dangling TransformMimicTarget (joiner crash)");

	public static readonly EngineMember ToolsUnregister = new EngineMember("tools-unregister", "Game2.Simulation", "Keen.Game2.Simulation.WorldObjects.Characters.ToolsComponent", MemberKind.Method, "Keen.Game2.Simulation.WorldObjects.Characters.IToolsHandler.UnregisterTool", new string[1] { "Keen.VRage.DCS.Components.Entity" }, "0.7.6: an older tool's removal keeps the newer tool as LocalTool")
	{
		InterfaceDispatchOnly = true
	};

	public static readonly EngineMember AudioPlay2D = new EngineMember("audio-play-2d", "VRage.Audio", "Keen.VRage.Audio.EngineComponents.AudioEngineComponent", MemberKind.Method, "Play", new string[3] { "Keen.VRage.Core.Game.GameSystems.Audio.AudioEvent", "System.Single", "System.Boolean" }, "0.7.6: another player's action never plays as this player's own (2D) sound")
	{
		InterfaceDispatchOnly = true
	};

	private const string PcuClient = "Keen.Game2.Client.GameSystems.PCU.PCUClientSessionComponent";

	public static readonly EngineMember PcuHas = new EngineMember("pcu-client-has", "Game2.Client", "Keen.Game2.Client.GameSystems.PCU.PCUClientSessionComponent", MemberKind.Method, "Keen.Game2.Simulation.GameSystems.PCU.IPCU.HasPCU", new string[2] { "Keen.Game2.Simulation.GameSystems.Ownership.IdentityId", "System.Int32" }, "0.7.6 joiner: PCU of another owner's grid answered with this player's (the host enforces the real one)")
	{
		InterfaceDispatchOnly = true
	};

	public static readonly EngineMember PcuGet = new EngineMember("pcu-client-get", "Game2.Client", "Keen.Game2.Client.GameSystems.PCU.PCUClientSessionComponent", MemberKind.Method, "Keen.Game2.Simulation.GameSystems.PCU.IPCU.GetPCU", new string[1] { "Keen.Game2.Simulation.GameSystems.Ownership.IdentityId" }, "0.7.6 joiner: as PcuHas")
	{
		InterfaceDispatchOnly = true
	};

	public static readonly EngineMember ServerValidateHit = new EngineMember("server-validate-hit", "Game2.Simulation", "Keen.Game2.Simulation.WorldObjects.Tools.InteractedEntityProviderServerComponent", MemberKind.Method, "Validate", null, "host: a player standing on a grid can weld/grind that grid");

	public static readonly EngineMember ServerTrySetEntity = new EngineMember("server-try-set-entity", "Game2.Simulation", "Keen.Game2.Simulation.WorldObjects.Tools.InteractedEntityProviderServerComponent", MemberKind.Method, "TrySetEntity", null, "0.7.6 host: why a tool target was refused");

	public static readonly EngineMember CrouchAudio = new EngineMember("crouch-audio", "Game2.Client", "Keen.Game2.Client.WorldObjects.Character.Effects.CharacterMovementEffectsComponent", MemberKind.Method, "OnCrouchingUpdate", null, "another player's crouch never plays as this player's own (2D) sound");

	private const string ControlPanel = "Keen.Game2.Simulation.StreamedUI.Terminal.ControlPanel.StreamedControlPanelSessionComponent";

	public static readonly EngineMember PanelDetachList = new EngineMember("panel-detach-list", "Game2.Simulation", ControlPanel, MemberKind.Method, "DetachControlPanelList", new string[1] { "Keen.Game2.Simulation.StreamedUI.Terminal.ControlPanel.ControlPanelListModel" }, "host: one player closing a control panel keeps the others' block detail models");

	public static readonly EngineMember PanelDetachDetails = new EngineMember("panel-detach-details", "Game2.Simulation", ControlPanel, MemberKind.Method, "DetachAllBlockDetailsFromEntity", new string[1] { "Keen.VRage.DCS.Components.Entity" }, "host: as PanelDetachList");

	public static readonly EngineMember SpawnTop = new EngineMember("topped-spawn-top", "Game2.Simulation", "Keen.Game2.Simulation.WorldObjects.CubeBlocks.MechanicalBlocks.ToppedMechanicalBlockComponent", MemberKind.Method, "RequestSpawnTop", Array.Empty<string>(), "0.8.2 joiner: never spawn a mechanical block's top on the client");

	public static readonly EngineMember RenderDestroyEntity = new EngineMember("render-destroy-entity", "VRage.Render12", "Keen.VRage.Render12.SceneSystem.SceneManager", MemberKind.Method, "Keen.VRage.Render12.Core.Contracts.IRenderContracts.DestroyEntity", new string[4] { "Keen.VRage.Library.Utils.RenderId", "System.Boolean", "System.Boolean", "System.Boolean" }, "0.8.2: a render destroy of an entity that is already gone does not crash the render thread")
	{
		InterfaceDispatchOnly = true
	};

	private const string CockpitInput = "Keen.Game2.Client.GameSystems.PlayerControl.PlayerInput.InputHandlers.CockpitInputHandlerComponent";

	private static readonly string[] InputHandlerParams = new string[2] { "System.Boolean", "Keen.VRage.Input.ControlActivation" };

	public static readonly EngineMember CockpitTogglePower = new EngineMember("cockpit-toggle-power", "Game2.Client", "Keen.Game2.Client.GameSystems.PlayerControl.PlayerInput.InputHandlers.CockpitInputHandlerComponent", MemberKind.Method, "TogglePower", InputHandlerParams, "0.8.2 host: a passenger does not switch the ship's power")
	{
		InterfaceDispatchOnly = true
	};

	public static readonly EngineMember CockpitToggleDampeners = new EngineMember("cockpit-toggle-dampeners", "Game2.Client", "Keen.Game2.Client.GameSystems.PlayerControl.PlayerInput.InputHandlers.CockpitInputHandlerComponent", MemberKind.Method, "ToggleDampeners", InputHandlerParams, "0.8.2 host: a passenger does not switch the ship's dampeners")
	{
		InterfaceDispatchOnly = true
	};

	public static readonly EngineMember CockpitToggleLights = new EngineMember("cockpit-toggle-lights", "Game2.Client", "Keen.Game2.Client.GameSystems.PlayerControl.PlayerInput.InputHandlers.CockpitInputHandlerComponent", MemberKind.Method, "ToggleLights", InputHandlerParams, "0.8.2 host: a passenger does not switch the ship's lights")
	{
		InterfaceDispatchOnly = true
	};

	public static readonly EngineMember CockpitToggleBrakes = new EngineMember("cockpit-toggle-brakes", "Game2.Client", "Keen.Game2.Client.GameSystems.PlayerControl.PlayerInput.InputHandlers.CockpitInputHandlerComponent", MemberKind.Method, "ToggleParkingBrakes", InputHandlerParams, "0.8.2 host: a passenger does not switch the ship's parking brakes")
	{
		InterfaceDispatchOnly = true
	};

	public static readonly EngineMember CockpitBoost = new EngineMember("cockpit-boost", "Game2.Client", "Keen.Game2.Client.GameSystems.PlayerControl.PlayerInput.InputHandlers.CockpitInputHandlerComponent", MemberKind.Method, "HandleBoost", InputHandlerParams, "0.8.2 host: a passenger does not boost the ship")
	{
		InterfaceDispatchOnly = true
	};

	public static readonly EngineMember GpsDrawHud = new EngineMember("gps-draw-hud", "Game2.Client", "Keen.Game2.Client.GameSystems.GPS.GPSMarkerRenderSessionComponent", MemberKind.Method, "DrawHUDMarkers", Array.Empty<string>(), "0.8.2: nametags positioned in the frame that draws them");

	public static readonly EngineMember PaintToolAdded = new EngineMember("paint-tool-added", "Game2.Client", "Keen.Game2.Client.WorldObjects.Tools.PaintToolControllableComponent", MemberKind.Method, "Keen.VRage.Core.Game.Systems.IInSceneListener.OnAddedToScene", Array.Empty<string>(), "0.8.2 joiner: another player's paint tool does not crash the joiner")
	{
		InterfaceDispatchOnly = true
	};

	public static readonly EngineMember[] Patches = new EngineMember[38]
	{
		CommitFrame, BuildAndReset, WorldInit, SteamInit, SteamShutdown, GameUpdate, NetClientConfigure, NetClientPull, SessionUpdate, PerPlayerInit,
		NarratorMoveToClient, PermIsOwner, PermCanPlace, PermCanUse, PermCanLink, PermCanMerge, AwaitReplication, RenderTextureDestroy, NextCharacter, EntityTypeToPossess,
		CharacterEntityRemoved, NextCharacterFocus, ToolShadowSpawn, ToolShadowDespawn, ToolsUnregister, AudioPlay2D, PcuHas, PcuGet, ServerTrySetEntity, SpawnTop,
		RenderDestroyEntity, CockpitTogglePower, CockpitToggleDampeners, CockpitToggleLights, CockpitToggleBrakes, CockpitBoost, GpsDrawHud, PaintToolAdded
	};

	public static readonly EngineMember[] Reflected = new EngineMember[54]
	{
		new EngineMember("cb-buffers", "VRage.DCS", "Keen.VRage.DCS.Utils.CommandBufferBase", MemberKind.Field, "_committedBuffers", null, "probe: walk committed buffers"),
		new EngineMember("cb-read", "VRage.DCS", "Keen.VRage.DCS.Utils.CommandBufferBase", MemberKind.Method, "ReadBuffer", new string[3] { "System.Int32", "System.IntPtr&", "System.IntPtr&" }, "probe: buffer extents"),
		new EngineMember("cb-objects", "VRage.DCS", "Keen.VRage.DCS.Utils.CommandBufferBase+WithObjects", MemberKind.Field, "_objects", null, "probe: object table"),
		new EngineMember("ipr-isserver", "VRage.Multiplayer", "Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent", MemberKind.Property, "IsServer", null, "probe: scene side"),
		new EngineMember("ipr-replayctx", "VRage.Multiplayer", "Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent", MemberKind.Property, "ReplayContext", null, "probe: map Replay(context) to a side"),
		new EngineMember("ipr-replaytable", "VRage.Multiplayer", "Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent", MemberKind.Field, "_replayTable", null, "probe: shared Keen.VRage.Multiplayer.Sessions.ReplicationCommandBuffer.ReplayTable whose reader slots get metered"),
		new EngineMember("sh-hash2str", "VRage.Library", "Keen.VRage.Library.Utils.StringHash", MemberKind.Field, "_hashToString", null, "0.5.1 host: StringHash names sent to joiners"),
		new EngineMember("sh-lock", "VRage.Library", "Keen.VRage.Library.Utils.StringHash", MemberKind.Field, "_lock", null, "0.5.1 host: StringHash registry lock"),
		new EngineMember("ipr-story", "VRage.Multiplayer", "Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent", MemberKind.Method, "MoveNextRemotely", null, "probe: story closures capture 'stateMachine'"),
		new EngineMember("app-transitioning", "SpaceEngineers2", "Keen.Game2.GameAppComponent", MemberKind.Field, "_isTransitioning", null, "client: wait for the game's own transition before LoadNetworkClientAsync"),
		new EngineMember("bundle-count", "VRage.Multiplayer", "Keen.VRage.Multiplayer.Replications.InProcess.ServerSceneContext+ServerToClientEntityReplicationData", MemberKind.Property, "EntityCount", null, "probe"),
		new EngineMember("bundle-skipped", "VRage.Multiplayer", "Keen.VRage.Multiplayer.Replications.InProcess.ServerSceneContext+ServerToClientEntityReplicationData", MemberKind.Property, "SkippedEntities", null, "probe"),
		new EngineMember("bundle-staged", "VRage.Multiplayer", "Keen.VRage.Multiplayer.Replications.InProcess.ServerSceneContext+ServerToClientEntityReplicationData", MemberKind.Property, "StagedEntities", null, "probe"),
		new EngineMember("bundle-server", "VRage.Multiplayer", "Keen.VRage.Multiplayer.Replications.InProcess.ServerSceneContext+ServerToClientEntityReplicationData", MemberKind.Property, "ServerBundle", null, "probe: serialized size"),
		new EngineMember("indexer-table", "VRage.Multiplayer", "Keen.VRage.Multiplayer.Replications.ReplicationCommandBufferIndexer", MemberKind.Field, "Table", null, "probe: stub index -> signal type"),
		new EngineMember("steam-active", "VRage.Steam", "Keen.VRage.Steam.EngineComponents.SteamGameServiceComponent", MemberKind.Property, "IsActive", null, "net: only start when Steam is active"),
		new EngineMember("component-entity", "VRage.DCS", "Keen.VRage.DCS.Components.Component", MemberKind.Field, "<Entity>k__BackingField", null, "client: Entity of the stand-in server replication component (ClientWorld.Configure)"),
		new EngineMember("session-pending-close", "VRage.Core.Game", "Keen.VRage.Core.Game.Systems.Session", MemberKind.Field, "PendingEntitiesForClose", null, "client: purge dead entities before Session.Update"),
		new EngineMember("session-close", "VRage.Core.Game", "Keen.VRage.Core.Game.Systems.Session", MemberKind.Field, "EntitiesForClose", null, "client: purge dead entities before Session.Update"),
		new EngineMember("ppd-npc", "Game2.Simulation", "Keen.Game2.Simulation.GameSystems.Player.InProcessPerPlayerDataSessionComponent", MemberKind.Field, "_npc", null, "phase 4 host: NPC provider swapped for one that keeps joiner identities un-folded (:141-149)"),
		new EngineMember("ppd-data", "Game2.Simulation", "Keen.Game2.Simulation.GameSystems.Player.InProcessPerPlayerDataSessionComponent", MemberKind.Field, "_perPlayerData", null, "phase 4 host: per-player data by identity"),
		new EngineMember("ppd-visible", "Game2.Simulation", "Keen.Game2.Simulation.GameSystems.Player.InProcessPerPlayerDataSessionComponent", MemberKind.Field, "_clientVisibleData", null, "phase 4 host: replicated per-player entries"),
		new EngineMember("ppd-observe", "Game2.Simulation", "Keen.Game2.Simulation.GameSystems.Player.InProcessPerPlayerDataSessionComponent", MemberKind.Method, "ObserveAndReplicateData", null, "phase 4 host: restore a joiner's saved data"),
		new EngineMember("ppd-collection", "Game2.Simulation", "Keen.Game2.Simulation.GameSystems.Player.InProcessPerPlayerDataSessionComponent", MemberKind.Method, "GetOrCreateCollectionForIdentity", null, "phase 4 host: a joiner's per-player entry"),
		new EngineMember("players-propagate", "Game2.Simulation", "Keen.Game2.Simulation.GameSystems.Player.PlayersSessionComponent", MemberKind.Method, "PropagateCreatedPlayer", null, "phase 4 host: register a joiner as Keen's own player (PlayersSessionComponent.cs:137-145)"),
		new EngineMember("players-allocate", "Game2.Simulation", "Keen.Game2.Simulation.GameSystems.Player.PlayersSessionComponent", MemberKind.Method, "AllocatePlayer", null, "phase 4 host: the joiner's PlayerIdentity"),
		new EngineMember("ownership-identities", "Game2.Simulation", "Keen.Game2.Simulation.GameSystems.Ownership.OwnershipSessionComponent", MemberKind.Field, "_identities", null, "phase 4 host: derived identities are added to the persisted identity set (allocator trap, design 1.2)"),
		new EngineMember("toolbar-config", "Game2.Simulation", "Keen.Game2.Simulation.GameSystems.Toolbars.PlayerToolbarServerSessionComponent", MemberKind.Field, "_config", null, "phase 4 host: default toolbar for a new joiner identity"),
		new EngineMember("client-players-identities", "Game2.Simulation", "Keen.Game2.Simulation.GameSystems.Player.PlayersSessionObjectBuilder", MemberKind.Field, "Identities", null, "phase 4 joiner: seeded so ClientPlayersSessionComponent.Init picks the joiner's identity (design 4)"),
		new EngineMember("gps-render-config", "Game2.Client", "Keen.Game2.Client.GameSystems.GPS.GPSMarkerRenderSessionComponent", MemberKind.Field, "_configuration", null, "nametags: HUD marker icon + full-display marker type (the renderer's own GPSMarkersConfiguration)"),
		new EngineMember("obs-client-newobject", "VRage.Multiplayer", "Keen.VRage.Multiplayer.Replications.ObservableObjectReplicationClientSessionComponent", MemberKind.Method, "OnReplicateNewObject", null, "phase 3 joiner: a host model's copy is paired with its stand-in key"),
		new EngineMember("obs-client-newlist", "VRage.Multiplayer", "Keen.VRage.Multiplayer.Replications.ObservableObjectReplicationClientSessionComponent", MemberKind.Method, "OnReplicateNewCollection", null, "phase 3 joiner: a host list's wrapper is paired"),
		new EngineMember("obs-client-release", "VRage.Multiplayer", "Keen.VRage.Multiplayer.Replications.ObservableObjectReplicationClientSessionComponent", MemberKind.Method, "OnReleaseReplicatedObject", null, "phase 3 joiner: release a copy (Keen confirms; the confirmation stays local)"),
		new EngineMember("obs-client-releaselist", "VRage.Multiplayer", "Keen.VRage.Multiplayer.Replications.ObservableObjectReplicationClientSessionComponent", MemberKind.Method, "OnReleaseReplicatedCollection", null, "phase 3 joiner: release a list wrapper"),
		new EngineMember("obs-client-listchange", "VRage.Multiplayer", "Keen.VRage.Multiplayer.Replications.ObservableObjectReplicationClientSessionComponent", MemberKind.Method, "OnReplicateCollectionChanged", null, "phase 3 joiner: list change through Keen's echo guard"),
		new EngineMember("obs-member-changed", "VRage.Multiplayer", "Keen.VRage.Multiplayer.Replications.ObservableObjectReplicationSessionComponent", MemberKind.Method, "OnReplicateMemberChanged", null, "phase 3 joiner: member change under Keen's echo guard"),
		new EngineMember("obs-queued", "VRage.Multiplayer", "Keen.VRage.Multiplayer.Replications.ObservableObjectReplicationSessionComponent", MemberKind.Field, "_queuedSignals", null, "phase 3 harness: Keen's signals of a component not in a scene yet"),
		new EngineMember("obs-adopt", "VRage.Multiplayer", "Keen.VRage.Multiplayer.Replications.ObservableObjectReplicationSessionComponent+ClientObservedCollection", MemberKind.Method, "AdoptExistingCollection", null, "phase 3 joiner: a model's constructor-made list keeps its subscriptions (SerializeAsType's adopt rule)"),
		new EngineMember("obs-collection-change", "VRage.Multiplayer", "Keen.VRage.Multiplayer.Replications.ReplicatedCollectionChange", MemberKind.Property, "RemoveCount", null, "phase 3: list change layout (kind, start, items, remove count)"),
		new EngineMember("pcu-streamed-model", "Game2.Client", "Keen.Game2.Client.GameSystems.PCU.PCUClientSessionComponent", MemberKind.Field, "_streamedModel", null, "0.5.3 stand-in / phase 3: the joiner's PCU model (the host's streamed one, or the stand-in as fallback)"),
		new EngineMember("look-control-rotation", "Game2.Simulation", "Keen.Game2.Simulation.WorldObjects.Movement.ControlData", MemberKind.Field, "Rotation", null, "look prediction: the joiner reads its character's per-tick rotation; the host rewrites it from the look totals (offset 12)"),
		new EngineMember("look-control-movement", "Game2.Simulation", "Keen.Game2.Simulation.WorldObjects.Movement.ControlData", MemberKind.Field, "Movement", null, "look prediction: the host keeps the joiner's newest Movement (offset 0) when it rewrites the Rotation"),
		new EngineMember("look-movement-state", "Game2.Simulation", "Keen.Game2.Simulation.WorldObjects.Characters.CharacterMovementComponent", MemberKind.Property, "CurrentMovementState", null, "look prediction: walking (yaw) / jetpack (pitch, yaw, roll) / seated (off), synced to joiners by CurrentMovementStateProperty"),
		new EngineMember("look-jetpack-torque", "Game2.Simulation", "Keen.Game2.Simulation.WorldObjects.Characters.CharacterMovementComponent+JetpackModeData", MemberKind.Field, "MaxTorque", null, "look prediction: jetpack turn rate (server only; the joiner falls back to the definition's 65)"),
		new EngineMember("look-offset-rotation", "Game2.Simulation", "Keen.Game2.Simulation.WorldObjects.Characters.CharacterMovementComponent", MemberKind.Method, "OffsetRotation", null, "look prediction: jetpack turns about the centre of mass (PluginHarness compares LookMath.PivotShift with it)"),
		new EngineMember("look-center-of-mass", "VRage.Physics", "Keen.VRage.Physics.Data.RigidBodyMassProperties", MemberKind.Field, "CenterOfMass", null, "look prediction: the pivot of a jetpack turn"),
		new EngineMember("ui-loading-vm", "Game2.Client", "Keen.Game2.Client.UI.Library.SharedUIComponent", MemberKind.Field, "_loadingTransitionScreenViewModel", null, "0.7.5 joiner: progress text in the loading screen's name line (SharedUIComponent.cs:706-712 sets it at load begin)"),
		new EngineMember("ui-loading-name", "Game2.Client", "Keen.Game2.Client.UI.Overlays.LoadingTransitionScreenViewModel", MemberKind.Property, "Name", null, "0.7.5 joiner: bound to PART_LoadingName (LoadingTransitionScreen.cs:804)"),
		new EngineMember("ui-notifications", "Game2.Client", "Keen.Game2.Client.UI.Library.SharedUIComponent", MemberKind.Property, "NotificationsScreenViewModel", null, "0.7.5 joiner: progress toast (main menu: MainMenuComponent.cs:209; in game: SessionInGameUISessionComponent.cs:574)"),
		new EngineMember("ui-system-message", "Game2.Client", "Keen.Game2.Client.UI.Shared.SystemNotification.NotificationsScreenViewModel", MemberKind.Method, "DisplaySystemMessage", null, "0.7.5 joiner: progress toast (timeout null = stays until disposed)"),
		new EngineMember("app-disconnect", "SpaceEngineers2", "Keen.Game2.GameAppComponent", MemberKind.Method, "Disconnect", Array.Empty<string>(), "0.7.5 joiner: back to the main menu after the host connection is lost (what the in-game menu's exit does, GameAppComponent.cs:162-179)"),
		new EngineMember("render-streaming-copylist", "VRage.Render12", "Keen.VRage.Render12.SceneSystem.Components.ManagedTextureStreamingComponent", MemberKind.Field, "_openedCopyList", null, "0.7.5 exit crash: the open 'Streaming Copy Mips' list (ManagedTextureStreamingComponent.cs:137-148)"),
		new EngineMember("render-early-exit", "VRage.Render12", "Keen.VRage.Render12.Core.CoreSystems", MemberKind.Property, "EarlyTaskExit", null, "0.7.5 exit crash: set at the start of CoreSystems.Dispose (:579); GPUDispose then releases resources immediately"),
		new EngineMember("defs-tryany", "VRage.Library", "Keen.VRage.Library.Definitions.DefinitionManager", MemberKind.Method, "TryGetAnyDefinition", new string[2] { "System.Guid", "Keen.VRage.Library.Definitions.Definition&" }, "0.7.2: a definition by Guid that may be a Configuration (TryGetDefinition asserts 'Use TryGetConfiguration' for those, :174)")
	};

	private const string CharMove = "Keen.Game2.Simulation.WorldObjects.Characters.CharacterMovementComponent";

	private const string ObsBase = "Keen.VRage.Multiplayer.Replications.ObservableObjectReplicationSessionComponent";

	private const string ObsClient = "Keen.VRage.Multiplayer.Replications.ObservableObjectReplicationClientSessionComponent";

	public const string ReplayIlSha256 = "6d1fd8c2bbcf3dcb7f2525c5260093504ed818b1d57706e8e358da1aa8feb064";

	/// <summary><see cref="IlShape.Hash"/> of the same method: its IL with metadata tokens zeroed.</summary>
	public const string ReplayIlShapeSha256 = "77a4f66af8d3d243388ad267b84ca4ceb738cf53b93ecca93226f3402775f8af";
}
