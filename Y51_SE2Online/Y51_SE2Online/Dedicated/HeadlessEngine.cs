using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Keen.Game2;
using Keen.Game2.Game.EngineComponents;
using Keen.Game2.Game.Helpers;
using Keen.Game2.Game.GameComponents;
using Keen.Game2.Game.Networking;
using Keen.Game2.Game.RuntimeSystems.GameSessions;
using Keen.Game2.Game.SessionComponents;
using Keen.Game2.OutOfProcess;
using Keen.Game2.Simulation.GameSystems.Missions;
using Keen.Game2.Simulation.RuntimeSystems;
using Keen.Game2.Simulation.RuntimeSystems.CoreConfigurations;
using Keen.Game2.Simulation.RuntimeSystems.CoreScenes;
using Keen.Game2.Simulation.RuntimeSystems.GameData;
using Keen.Game2.Simulation.RuntimeSystems.Saves;
using Keen.VRage.Core.EngineComponents;
using Keen.VRage.Core.Extensions;
using Keen.VRage.Core.Game.Extensions;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.Core.Platform.CrashReporting;
using Keen.VRage.Core.Project;
using Keen.VRage.Core.StreamingResources;
using Keen.VRage.Core.Worlds;
using Keen.VRage.DCS.Builders;
using Keen.VRage.DCS.Components;
using Keen.VRage.DCS.Internal;
using Keen.VRage.Library.Localization;
using Keen.VRage.Library.Utils;
using Keen.VRage.Multiplayer.Replications;
using Keen.VRage.Multiplayer.Transport;
using Keen.VRage.Multiplayer.Transport.OutOfProcess.Server;
using Y51_SE2Online.Core;
using Task = Keen.VRage.Library.Threading.Task;

namespace Y51_SE2Online.Dedicated;

/// <summary>Composes SE2's engine for a dedicated server without renderer, window, UI, input or audio device, so it runs on GPU-less hosts.</summary>
internal static class HeadlessEngine
{
    private static bool _loggedAppMetadata;
    public static void Install(Harmony harmony)
    {
        if (!DedicatedRuntime.Headless) return;
        ServerPerf.Install(harmony, MpPlugin.LaunchArgs);
        AccessTools.Property(typeof(CrashHandler), "ShowMessageBox").SetValue(Singleton<CrashHandler>.Instance, false);
        harmony.Patch(AccessTools.Method(typeof(CrashHandler), "HandlePrimaryException"),
            prefix: new HarmonyMethod(typeof(HeadlessEngine), nameof(FatalPrefix)));
        harmony.Patch(AccessTools.Method(typeof(DiagnosticReporter), "ReportIfNeeded"),
            prefix: new HarmonyMethod(typeof(HeadlessEngine), nameof(DiagnosticPrefix)));
        harmony.Patch(AccessTools.Method(typeof(RuntimeComponentInfo), nameof(RuntimeComponentInfo.For), new[] { typeof(Type) }),
            postfix: new HarmonyMethod(typeof(HeadlessEngine), nameof(ComponentInfoPostfix)));
        harmony.Patch(AccessTools.Method(typeof(GameApp), "CreateEngine"),
            prefix: new HarmonyMethod(typeof(HeadlessEngine), nameof(CreateEnginePrefix)));
        harmony.Patch(AccessTools.Method(typeof(GameAppComponent), "Init"),
            prefix: new HarmonyMethod(typeof(HeadlessEngine), nameof(AppInitPrefix)));
        harmony.Patch(AccessTools.Method(typeof(GameAppComponent), nameof(GameAppComponent.LoadDedicatedServerAsync)),
            prefix: new HarmonyMethod(typeof(HeadlessEngine), nameof(LoadPrefix)));
    }

    private static void FatalPrefix(Exception ex, ref bool userReport)
    {
        if (!DedicatedRuntime.Headless) return;
        userReport = false;
        DedicatedRuntime.ReportNativeFailure(ex);
    }

    private static bool DiagnosticPrefix() => !DedicatedRuntime.Headless;

    private static void ComponentInfoPostfix(Type __0, RuntimeComponentInfo __result)
    {
        if (!DedicatedRuntime.Headless || __result == null) return;
        if (__0.FullName == "Keen.Game2.GameAppComponent")
        {
            if (!_loggedAppMetadata)
            {
                _loggedAppMetadata = true;
                GameLog.Instance.Info("dedicated: GameApp metadata dependencies: " + string.Join(", ", __result.BuildDependencies.Select(d => d.Field.Name + "=" + d.Tag)));
            }
            if (__result.BuildDependencies.Any(d => IsPresentationDependency(d.Field)))
                __result.BuildDependencies = __result.BuildDependencies
                    .Where(d => !IsPresentationDependency(d.Field)).ToArray();
            if (__result.Services.Any(s => s.Field.Name == "_contentIntegrityReporter"))
                __result.Services = __result.Services.Where(s => s.Field.Name != "_contentIntegrityReporter").ToArray();
        }
        else if (__0.FullName == "Keen.VRage.Platform.Windows.EngineComponents.WinSystemEngineComponent"
            && __result.BuildDependencies.Any(d => d.Field.Name == "_platformWindows"))
            __result.BuildDependencies = __result.BuildDependencies.Where(d => d.Field.Name != "_platformWindows").ToArray();
        if (typeof(WorldTickComponent).IsAssignableFrom(__0))
            __result.Services = __result.Services.Where(s => s.Field.FieldType.FullName != "Keen.VRage.Render.EngineComponents.RenderEngineComponent").ToArray();
    }

    private static bool IsPresentationDependency(FieldInfo field) => field.Name == "_windows" || field.Name == "_ui" || field.Name == "_gameRender"
        || field.FieldType.FullName == "Keen.VRage.Core.Platform.IPlatformWindows"
        || field.FieldType.FullName == "Keen.VRage.UI.EngineComponents.UIEngineComponent"
        || field.FieldType.FullName == "Keen.Game2.GameRenderComponent";

    private static bool CreateEnginePrefix(GameApp __instance, string[] args)
    {
        if (!DedicatedRuntime.Headless) return true;
       
        __instance.ApplicationLoop.TargetFrequency = 60;
        __instance.ApplicationLoop.WaitForInterval = true;
        var engine = new EngineBuilder(__instance, args);
        ServerPerf.Configure(__instance.ApplicationLoop, engine);
        var scene = new GameCoreScene(new GameCoreSerializer(engine));
        __instance.CoreScene = scene;
        engine.EntitySerializer = scene.Serializer;
        engine.SceneBuilder.UserObject = scene;
        engine.SceneBuilder.AddJobsFromType(typeof(GameServerNetworkingComponent));
        engine.AddService(VersionExt.FromAssembly(typeof(GameApp).Assembly));
        var projects = ((IEnumerable<VRageProject>)AccessTools.Method(typeof(GameApp), "GetProjects")
            .Invoke(null, new object[] { args })).ToList();
        __instance._plugins?.InvokeOnBeforeProjectsLoaded(projects);
        var locator = new LocalProjectLocator
        {
            GlobalSearchPaths = (string[])AccessTools.Method(typeof(GameApp), "TryGetGlobalProjectDirs")
                .Invoke(null, new object[] { args })
        };
        projects.ForEach(locator.CacheProjectPaths);
        LocalMods.Mount(locator);
        engine.AddLibrary(new LibraryEngineComponent.ObjectBuilder
        {
            ContentSetup = GameContent.MountDefaultContent(),
            ValidateContentIntegrity = true,
            CrashReportingSetup = new CrashReportingSetup
            {
                GameID = "SpaceEngineers2", Options = CrashReportingOptions.Disabled,
                GameVersion = typeof(GameApp).Assembly.GetName().Version?.ToString()
            }
        });
        engine.AddWatchdog();
        engine.AddOptionsStorage();
        engine.AddProjectManager(new ProjectManagerEngineComponent.ObjectBuilder
        {
            ProjectLocators = new Dictionary<string, IProjectLocator> { { "", locator } }
        });
        engine.AddDefinitionSetManager();
        engine.AddEngineDataLoader(new EngineDataLoaderComponent.ObjectBuilder { Projects = projects });
        engine.Add<StreamingResourceManagerEngineComponent>(new StreamingResourceManagerEngineComponentObjectBuilder
        {
            MemoryPoolBudgets = new MemoryPoolSize(1_000_000_000L, 0L)
        });
        Extend(engine, "VRage.Animation.Client", "Keen.VRage.Animation.Client.Resources.Extensions.EngineBuilderExtensions", "AddAnimationsStreaming");
        Add(engine, "VRage.Game", "Keen.VRage.Game.ProceduralGeneration.ProceduralGeneratorEngineComponent");
        Add(engine, "Game2.Simulation", "Keen.Game2.Simulation.GameSystems.ProceduralGeneration.ProceduralGeneratorGameEngineComponent");
        Add(engine, "VRage.Game", "Keen.VRage.Game.Multiplayer.MotionParentEngineComponent");
        Add(engine, "Game2.Simulation", "Keen.Game2.Simulation.GameSystems.Water.WaterSessionServerConfiguratorComponent");
        engine.AddLocalization();
        engine.AddDebugDrawServer(new DebugDrawServerEngineComponent.ObjectBuilder
        {
            RenderMeshBuilderFactoryFunc = () => new HeadlessMeshFactory()
        });
        engine.Add<Keen.VRage.Audio.EngineComponents.MinimalAudioEngineComponent>();
        engine.AddUgcArbiterService();
        AddSteam(engine, __instance);
        engine.AddOfflineGameServiceFallbacks();
        Extend(engine, "VRage.Voxels", "Keen.VRage.Voxels.Extensions.EngineBuilderExtensions", "AddVoxelsServer", (object)args);
        Extend(engine, "VRage.Voxels", "Keen.VRage.Voxels.Extensions.EngineBuilderExtensions", "AddStreamedStorages");
        Extend(engine, "VRage.Voxels", "Keen.VRage.Voxels.Extensions.EngineBuilderExtensions", "AddPlanetTextureStreaming");
        Add(engine, "Game2.Simulation", "Keen.Game2.Simulation.GameSystems.Physicss.DefaultPhysicsSettingsComponent");
        Extend(engine, "VRage.Physics", "Keen.VRage.Physics.Extensions.EngineBuilderExtensions", "AddPhysicsServer", (object)null);
        Extend(engine, "VRage.AI", "Keen.VRage.AI.Extensions.EngineBuilderExtensions", "AddAI");
        Add(engine, "VRage.Platform.Windows", "Keen.VRage.Platform.Windows.EngineComponents.WinPlatformEngineComponent");
        Add(engine, "VRage.Platform.Windows", "Keen.VRage.Platform.Windows.EngineComponents.WinSystemEngineComponent");
        Add(engine, "VRage.Platform.Windows", "Keen.VRage.Platform.Windows.EngineComponents.WinMemoryEngineComponent");
        engine.Add<WallTimeProviderComponent>(__instance.ApplicationLoop);
        string maxPlayers = args.FirstOrDefault(a => a.StartsWith("-maxPlayers:", StringComparison.Ordinal));
        if (maxPlayers != null && int.TryParse(maxPlayers.Substring("-maxPlayers:".Length), out int max) && max >= 1)
            engine.Configure<GameCoreConfigurationObjectBuilder>().MaxPlayers = max;
        engine.Add<GameAppComponent>(new GameAppComponentObjectBuilder());
        if (args.Contains("-loadScripts")) AccessTools.Method(typeof(GameApp), "AddScripting").Invoke(null, new object[] { engine });
        __instance._plugins?.InvokeOnBeforeEngineInstantiated(engine);
        var graphical = engine.EntityBuilder.Components.Where(c => IsGraphical(c.Type)).Select(c => c.Type.FullName).ToArray();
        if (graphical.Length > 0)
            throw new InvalidOperationException("A plugin added graphics or client components to the headless server engine: " + string.Join(", ", graphical));
        foreach (var component in engine.EntityBuilder.Components)
            ComponentInfoPostfix(component.Type, RuntimeComponentInfo.For(component.Type));
        engine.EntityBuilder.CompileObjectBuilder();
        engine.EntityBuilder.OnEntityAllocatedCallback += entity => PostEngineInit(scene);
        if (__instance._plugins != null) engine.EntityBuilder.OnEntityAllocatedCallback += __instance._plugins.PostEngineInit;
        engine.Dispose();
        GameLog.Instance.Info("dedicated: headless server engine composed without renderer, window, UI, input, audio device, or game client; 60 Hz loop");
        return false;
    }

    private static void PostEngineInit(GameCoreScene scene)
    {
        scene.Dynamic().Create("Dedicated Core Game Systems",
            typeof(FailureResolutionComponent),
            typeof(LoadStatsLoggerComponent),
            typeof(TickManagementComponent),
            new EntityBuilder.ComponentBuildInfo
            {
                Type = Resolve("VRage.Game", "Keen.VRage.Game.ReplayTool.RecorderProviderComponent"),
                ObjectBuilder = Activator.CreateInstance(Resolve("VRage.Game", "Keen.VRage.Game.ReplayTool.RecorderProviderComponentObjectBuilder"))
            },
            typeof(HeadlessParallelInitComponent));
        var failure = scene.Single<FailureResolutionComponent>();
        failure.SetShouldDisplayLoadingFailure(false);
        failure.SetLoadingFailureStrategy(FailureResolutionComponent.LoadingFailureStrategy.Exit);
        scene.Dynamic().Create("Dedicated Core Game Data",
            typeof(SceneWideServiceProviderComponent),
            Resolve("Game2.Simulation", "Keen.Game2.Simulation.RuntimeSystems.Serialization.GameWorldsComponent"),
            typeof(GameDataLoaderComponent));
        scene.Single<GameDataLoaderComponent>().AdditionalWorldServiceComponents.Add(
            (ComponentType: typeof(Keen.VRage.Game.Armor.Components.ArmorMesherComponent), ObjectBuilder: new Keen.VRage.Game.Armor.Components.ArmorMesherObjectBuilder()));
        Singleton<TextManager>.Instance.InitializeBaseCulture();
    }

    // GameAppComponent.Init hides the (absent) window until the first world shows.
    private static bool AppInitPrefix(GameAppComponent __instance, GameAppComponentObjectBuilder ob)
    {
        if (!DedicatedRuntime.Headless) return true;
        __instance._instanceIndex = ob.InstanceIndex.GetValueOrDefault();
        __instance.EnableTransitionDelay = false;
        return false;
    }

    // Keen's TransitionToGame waits for UI pins and the renderer fence and shows the window, and R2R inlines it into
    // BuildAndRunDedicatedServerGame, so the whole dedicated load is replaced: same server entity, headless transition.
    private static bool LoadPrefix(GameAppComponent __instance, WorldLoadArgs args, ref Task __result)
    {
        if (!DedicatedRuntime.Headless) return true;
        __result = LoadDedicatedServer(__instance, args);
        return false;
    }

    private static async Task LoadDedicatedServer(GameAppComponent app, WorldLoadArgs args)
    {
        await Task.SetLifetime(app.Scene);
        if (!app._isTransitioning.Set()) throw new InvalidOperationException("A world transition is already running.");
        try
        {
            await app.WaitForSaveToFinish();
            bool mission = await MissionScreenSessionComponent.IsMissionWorld(args, app);
            var server = app.Scene.Dynamic();
            server.WithComponent<DedicatedServerWorldTickMockComponent>();
            server.WithComponent<ReplicationServerCoreComponent>();
            server.WithComponent<GameSessionDedicatedServerComponent>(new GameSessionDedicatedServerComponentObjectBuilder
            {
                ListenEndpoint = new System.Net.IPEndPoint(System.Net.IPAddress.Any, 27015),
                ServerName = "Dedicated server"
            }, StringId.Get("IGameSessionProvider"), StringId.Get("IServerGameSessionProvider"));
            server.WithComponent<GameServerNetworkingComponent>(new GameServerNetworkingComponentObjectBuilder());
            server.WithComponent<OutOfProcessServerTransportCoreComponent>();
            server.WithComponent<LoggingIncidentReportingCoreComponent>();
            var ob = GameWorldBuilder.CreateBaseGame(ref server, true, app._instanceIndex, mission);
            GameWorldBuilder.BuildGameServer(ref server);
            if (args.SaveId.HasValue)
                ob.WorldContainerToLoad = new WorldSessionComponentObjectBuilder.ContainerLoadData
                {
                    WorldContainerToLoad = args.SaveId, ContainerBackupId = args.SaveBackupId,
                    DetachFromContainerAfterLoad = args.PlayMission == true
                };
            if (args.OverrideModList != null) ob.OverrideModList = args.OverrideModList;
            await Transition(app, server);
            var world = app.Server.Entity.Get<WorldSessionComponent>();
            var session = world.OwnedSession;
            bool content = false;
            if (args.SaveId.HasValue)
            {
                var source = await SaveHelper.TryGetSaveAsync(args.SaveId.Value);
                content = source.HasValue && source.Value.IsExternal;
                if (content && args.SaveId.Value.ToString().StartsWith("$Content:", StringComparison.Ordinal))
                    session.SessionComponents.Get<GameSaveInfoSessionComponent>().UsedDebugMenu = false;
            }
            if (args.EnableAutosave.HasValue) session.SessionComponents.Get<SaveSessionComponent>().EnableAutosave = args.EnableAutosave.Value;
            if (args.OverrideDisplayName != null) await world.SetDisplayName(args.OverrideDisplayName);
            session.SessionComponents.TryGet<MissionSessionComponent>()?.Configure(args.PlayMission, content);
        }
        catch (Exception ex)
        {
            app.NotifySessionLoadFinished(ex);
            throw;
        }
        finally { app._isTransitioning.Clear(); }
    }

    private static async Task Transition(GameAppComponent app, EntityBuilder? server)
    {
        await Task.SetLifetime(app.Scene);
        await app.WaitForSaveToFinish();
        app.NotifySessionLoadFinished(new System.Threading.Tasks.TaskCanceledException());
        var data = app.Scene.Single<GameDataLoaderComponent>();
        if (server.HasValue)
        {
            // Pauses single player while a menu or video is open; it needs the UI and renderer.
            server.Value.Components.RemoveAll(c => c.Type == typeof(AutoPauseWorldComponent));
            var world = server.Value.OBsOfType<WorldSessionComponentObjectBuilder>().Single();
            if (world.OverrideModList != null) data.SetRequiredProjects(world.OverrideModList);
            else if (world.WorldContainerToLoad?.WorldContainerToLoad is { } save)
                data.SetRequiredProjects(await SaveHelper.TryGetModList(save, app));
        }
        if (!data.CurrentProjectsMatchRequired)
        {
            app.Scene.UnloadAllScenes();
            await data.PreloadWorldDataIfNeeded();
        }
        app.Scene.UnloadAllScenes();
        await data.LoadWorldDataIfNeeded();
        if (server.HasValue)
            foreach (var component in server.Value.Components)
                ComponentInfoPostfix(component.Type, RuntimeComponentInfo.For(component.Type));
        await app.Scene.TransitionToGame(server, null);
    }

    private static void AddSteam(EngineBuilder engine, GameApp app)
    {
        var type = Resolve("VRage.Steam", "Keen.VRage.Steam.Data.SteamServicesObjectBuilder");
        var ob = Activator.CreateInstance(type);
        void Set(string name, object value) => type.GetProperty(name).SetValue(ob, value);
        Set("IsDedicatedServer", true);
        Set("ApplicationName", "Space Engineers 2");
        Set("ApplicationId", 1133870u);
        Set("ApplicationVersion", typeof(GameApp).Assembly.GetName().Version);
        Set("ApplicationGuid", new Guid("D02B4B3F-6D9F-4F70-8F0D-7F9F4C5C6B6A"));
        Set("DlcSteamIdToGuidMap", new Dictionary<Guid, uint>());
        var achievements = type.GetProperty("Achievements").PropertyType;
        Set("Achievements", achievements.GetField("Empty").GetValue(null));
        Set("PlatformHttp", app.PlatformCore.Http);
        Extend(engine, "VRage.Steam", "Keen.VRage.Steam.Extensions.EngineBuilderExtensions", "AddSteamServices", ob);
    }

    internal static bool IsGraphical(Type type)
    {
        string name = type.FullName ?? string.Empty;
        if (type == typeof(Keen.VRage.Audio.EngineComponents.MinimalAudioEngineComponent)
            || name == "Keen.VRage.Animation.Client.Resources.AnimationStreamingManagerComponent") return false;
        return name.StartsWith("Keen.VRage.Render", StringComparison.Ordinal)
            || name.StartsWith("Keen.VRage.UI", StringComparison.Ordinal)
            || name.StartsWith("Keen.VRage.Input", StringComparison.Ordinal)
            || name.StartsWith("Keen.VRage.Audio", StringComparison.Ordinal)
            || name.StartsWith("Keen.Game2.Client", StringComparison.Ordinal)
            || name.Contains(".Client.", StringComparison.Ordinal)
            || name.EndsWith("WinWindowsEngineComponent", StringComparison.Ordinal)
            || name.EndsWith("WinInputEngineComponent", StringComparison.Ordinal)
            || name.EndsWith("WinRenderEngineComponent", StringComparison.Ordinal)
            || name.EndsWith("GameRenderComponent", StringComparison.Ordinal);
    }

    private static Type Resolve(string assembly, string name) => Assembly.Load(assembly).GetType(name, true);

    private static void Add(EngineBuilder engine, string assembly, string name) =>
        engine.Add(new EntityBuilder.ComponentBuildInfo { Type = Resolve(assembly, name) });

    private static void Extend(EngineBuilder engine, string assembly, string type, string method, params object[] values)
    {
        var arguments = new object[values.Length + 1];
        arguments[0] = engine;
        Array.Copy(values, 0, arguments, 1, values.Length);
        var selected = Resolve(assembly, type).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .SingleOrDefault(m => m.Name == method && m.GetParameters().Length == arguments.Length
                && m.GetParameters()[0].ParameterType == typeof(EngineBuilder))
            ?? throw new MissingMethodException(type, method + "(" + arguments.Length + " arguments)");
        selected.Invoke(null, arguments);
    }

    public sealed class HeadlessParallelInitComponent : CoreSceneComponent, ISessionConfigurator
    {
        public void ConfigureSession(SessionBuilder builder)
        {
            var debug = Scene.Single<DebugDrawServerEngineComponent>();
            builder.ExternalServices.Add((Keen.VRage.Core.Render.IDebugDraw)debug);
            builder.ExternalServices.Add(debug.MeshBuilderFactory);
            if (builder.IsServer) builder.SessionComponents.WithComponent<ServerParallelInitComponent>();
        }
    }

    public sealed class ServerParallelInitComponent : Component, ISingleKeyService<IParallelEntityInitSystems>, IParallelEntityInitSystems
    {
        public object OpenAsyncEntitySpawn() => null;
        public Keen.VRage.Library.Threading.Task FinishAsyncEntitySpawn(object context) => default;
        public void ActivateContextForThread(object context) { }
        public void DeactivateContextForThread(object context) { }
    }
}
