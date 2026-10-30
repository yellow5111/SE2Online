using System;
using System.Linq;
using System.IO;
using System.Text.Json;
using HarmonyLib;
using Keen.Game2;
using Keen.Game2.Game.SessionComponents;
using Keen.Game2.Simulation.RuntimeSystems;
using Keen.Game2.Simulation.RuntimeSystems.Saves;
using Keen.VRage.Core.Worlds;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Definitions;
using Keen.VRage.Library.Filesystem.StorageManagers;
using Keen.VRage.Library.Localization;
using Keen.VRage.Library.Serialization;
using Keen.VRage.Library.Threading;
using Keen.VRage.Library.Utils;
using Y51_SE2Online.Core;
using Y51_SE2Online.KeenNetcode;
using Y51_SE2Online.Sync;
using Y51_SE2Online.NetHost;
using Y51_SE2Online.SyncEngine;
using Task = Keen.VRage.Library.Threading.Task;

namespace Y51_SE2Online.Dedicated;

internal static class DedicatedRuntime
{
    internal const string Argument = "-y51dedicated:";
    public static DedicatedConfig Config { get; private set; }
    public static bool Requested { get; private set; }
    public static bool Headless { get; private set; }
    public static bool Active => Requested && Config != null && Error == null;
    public static string Error { get; private set; }
    public static string ErrorDetail { get; private set; }
    public static string Status { get; private set; } = "Inactive";
    private static bool _loading;
    private static bool _loaded;
    private static bool _networkRequested;
    private static bool _saving;
    private static bool _exiting;
    private static bool _allowExit;
    private static bool _forceSaveOnExit;
    private static long _shutdownRequests;
    private static long _shutdownAtMs;
    private static int _shutdownAnnounced;
    private static readonly DateTime StartedLocal = DateTime.Now;
    private static DateTime? _restartAt;
    /// <summary>Next scheduled restart in server local time, if any.</summary>
    public static DateTime? NextRestart => _restartAt;
    private static int _restartWarned = int.MaxValue;
    // Set when this stop is a restart: the runner relaunches the server once the final save is done.
    private static bool _restartRequested;
    private static bool _lastSaveFailed;
    private static DateTime? _lastSaveUtc;
    private static long _nextSave;
    // SE2's own autosave restarts its timer after every server save; this is the other half, so the two never run back to back.
    private const long GameSaveGapMs = 60_000;
    private static DateTime? _seenGameSave;
    private static long _gameSaveMs = long.MinValue / 2;
    private static string _statusPath;
    private static long _lastStatus;
    private static string _savedId;
    private sealed record ResumeState(string SaveId, string SourceSaveId, string SourceContent, string WorldName, string SourceWorkshop = "");

    public static void Initialize(string[] args, Harmony harmony)
    {
        string selected = args.FirstOrDefault(a => a.StartsWith(Argument, StringComparison.OrdinalIgnoreCase));
        if (selected == null) return;
        Requested = true;
        _harmony = harmony;
        Headless = args.Contains("-y51headless");
        // A blocking full collection stops the server frame, and every joiner sees the world pop when it resumes.
        System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
        try
        {
            _statusPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(selected.Substring(Argument.Length))), "dedicated-status.json");
            DedicatedConsoleLog.Initialize(Path.GetDirectoryName(_statusPath));
            // SE2 4.0: the game's own dedicated server (Steam game server + network replication) needs this switch.
            if (!args.Contains("-localDedicatedServer"))
                throw new ArgumentException("SE2 4.0 dedicated hosting runs on the game's own dedicated server; start the game with -localDedicatedServer (the runner adds it).");
            Config = DedicatedConfig.Read(selected.Substring(Argument.Length));
            try
            {
                var added = DedicatedConfig.AddNewOptions(Config);
                if (added.Count > 0) GameLog.Instance.Info($"dedicated: added {added.Count} new option(s) to {Path.GetFileName(Config.ConfigPath)} with their defaults, your settings untouched (previous file kept as {Path.GetFileName(Config.ConfigPath)}.bak): {string.Join(", ", added)}");
            }
            catch (Exception ex) { GameLog.Instance.Warn($"dedicated: could not add new options to {Path.GetFileName(Config.ConfigPath)} ({ex.GetType().Name}: {ex.Message}); the server runs on it as it is"); }
            // Steam runs as a game server on every 4.0 dedicated server, so the Workshop is fetched through it either way.
            DedicatedMods.Install(harmony);
            if (Headless) HeadlessEngine.Install(harmony);
            NoCrashDialog();
            NativeSessionSettings.Install(harmony);
            NativeDefinitionSettings.Install(harmony);
            harmony.Patch(AccessTools.Method(typeof(GameApp), "StartPlayerExperienceAsync"),
                prefix: new HarmonyMethod(typeof(DedicatedRuntime), nameof(StartupPrefix)));
            harmony.Patch(AccessTools.Method(typeof(GameApp), nameof(GameApp.Exit)),
                prefix: new HarmonyMethod(typeof(DedicatedRuntime), nameof(ExitPrefix)));
            harmony.Patch(AccessTools.Method(typeof(GameAppComponent), "GameUpdate"),
                postfix: new HarmonyMethod(typeof(DedicatedRuntime), nameof(GameUpdatePostfix)));
            ServerProfiler.Enabled = true;
            if (!string.IsNullOrWhiteSpace(Config.ProfileFile))
                ServerProfiler.StartRecording(Path.Combine(Config.Directory, Config.ProfileFile), GameLog.Instance);
            Status = "Waiting for engine";
            GameLog.Instance.Info("dedicated: configuration loaded from " + Config.ConfigPath + (Headless ? "; headless simulation" : "; unattended server retains native client/renderer"));
            if (OnlineSettings.Unstable(Config.Network.MaxPlayers)) GameLog.Instance.Warn($"dedicated: Network.MaxPlayers is {Config.Network.MaxPlayers}. {OnlineSettings.UnstablePlayersWarning}");
            // Up before the world loads, so status and the log are there to watch a long load.
            DedicatedRcon.Start(Config);
            WriteStatus(true);
        }
        catch (Exception ex) { Fail("configuration", ex); }
    }

    private static bool StartupPrefix(ref Task __result)
    {
        if (!Requested) return true;
        Singleton<TextManager>.Instance.InitializeBaseCulture();
        __result = Task.FromResult(true).StripResult();
        return false;
    }

    private static long _lastSlowLogMs, _statusCostMs;
    private static int _slowFramesLoaded;

    // Job timing switched on because a whole window ran behind (not for slow frames): switched off when it keeps up again.
    private static bool _jobsForBehind;
    private static Harmony _harmony;
    private static readonly object StatusFileLock = new();

    private static int _statusFailures;

    private static void GameUpdatePostfix() => ServerProfiler.TickEnd();

    // After a crash SE2 waits up to 5 minutes for someone to close its crash message box before its crash report goes out
    // and the process ends; nobody sees that box on an unattended server, so the runner could not restart it for 5 minutes.
    // The report is still written and sent.
    private static void NoCrashDialog()
    {
        try
        {
            var handler = Keen.VRage.Library.Utils.Singleton<Keen.VRage.Core.Platform.CrashReporting.CrashHandler>.Instance;
            AccessTools.Property(handler.GetType(), "ShowMessageBox")!.SetValue(handler, false);
            GameLog.Instance.Info("dedicated: no crash message box (a crash reports and ends at once, so the runner can restart it)");
        }
        catch (Exception ex) { GameLog.Instance.Warn("dedicated: the crash message box stays on (" + ex.Message + ")"); }
    }

#if !PULSAR
    private static long _crashAtMs;

    // Test rig (-y51online:selftest-crash): the loaded server throws on its main thread 60 s later, a crash the game's crash
    // handler takes, to time how soon the process ends and the runner can restart it.
    private static void SelfTestCrash()
    {
        if (!_loaded || !MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "selftest-crash")) return;
        long now = MpClock.NowMs();
        if (_crashAtMs == 0) { _crashAtMs = now + 60_000; GameLog.Instance.Info("selftest: crash: the server throws in 60 s"); return; }
        if (now < _crashAtMs || _crashAtMs < 0) return;
        _crashAtMs = -1;
        throw new InvalidOperationException("selftest: crash (-y51online:selftest-crash)");
    }
#endif

    public static void Tick(GameAppComponent app)
    {
#if !PULSAR
        SelfTestCrash();
#endif
        var frame = Requested ? ServerProfiler.BeginFrame(_saving) : null;
        long started = ServerProfiler.Now;
        // A stalled server frame pauses every player's and grid's updates; joiners see it as a pop when they resume.
        bool slow = frame != null && _loaded && frame.FrameMs > ServerProfiler.SlowFrameMs;
        string jobs = JobProfiler.TakeFrame(slow || ServerPerf.TraceJobs(frame));
        if (slow && MpClock.NowMs() - _lastSlowLogMs > 2000)
        {
            _lastSlowLogMs = MpClock.NowMs();
            GameLog.Instance.Warn($"dedicated: slow server frame {frame!.Describe()}; last status write {_statusCostMs} ms, command {_commandCostMs} ms, save start {_saveStartCostMs} ms{(jobs.Length > 0 ? "; slowest game jobs (ms): " + jobs : "")}");
        }
        if (JobProfiler.Installed && _jobsForBehind && !ServerPerf.Behind)
        {
            try
            {
                JobProfiler.Uninstall(_harmony);
                _jobsForBehind = false;
                GameLog.Instance.Info("dedicated: the world keeps up again; no longer timing the game's jobs");
            }
            catch (Exception ex) { GameLog.Instance.WarnOnce("job-profiler-off", "dedicated: could not stop timing the game's jobs: " + ex.Message); _jobsForBehind = false; }
        }
        bool slowInstall = slow && !JobProfiler.Installed && ++_slowFramesLoaded >= 3;
        if (slow && JobProfiler.Installed && _jobsForBehind && ++_slowFramesLoaded >= 3) _jobsForBehind = false;
        if (!JobProfiler.Installed && (slowInstall || ServerPerf.Behind))
        {
            _jobsForBehind = !slowInstall;
            try
            {
                JobProfiler.Install(_harmony);
                GameLog.Instance.Info("dedicated: slow frames keep coming; timing the game's jobs so the next warnings name the slowest");
            }
            catch (Exception ex) { GameLog.Instance.Warn("dedicated: could not time the game's jobs: " + ex); }
        }
        if (Headless && Active) ServerPerf.Tick(app, frame, jobs);
        try { TickCore(app); }
        finally
        {
            ServerProfiler.Lap(ServerSection.Dedicated, started);
            ServerProfiler.TickStart();
        }
    }

    private static long _commandCostMs, _saveStartCostMs;

    private static void TickCore(GameAppComponent app)
    {
        if (Requested) WriteStatus();
        if (Requested && Config == null && Error != null) { app.ExitGame(); return; }
        if (Requested && Config != null) DedicatedRcon.Pump(app);
        if (Requested && Config != null && !_exiting)
        {
            long commandStarted = MpClock.NowMs();
            try { CheckCommand(app); }
            catch (Exception ex) { GameLog.Instance.Error("dedicated: command failed: " + ex); }
            if (!Busy) RunChatCommands(app);
            _commandCostMs = MpClock.NowMs() - commandStarted;
            if (_shutdownAtMs != 0 && !_exiting) TickCountdown(app);
            else if (_loaded && !_exiting) TickRestart(app);
            if (_loaded && !_exiting) TickModUpdates();
        }
        if (!Active || _exiting) return;
        try
        {
            if (!_loading && !_loaded && !app._isTransitioning.IsSet)
            {
                _loading = true;
                Status = "Loading world";
                Load(app).SkipWait();
                return;
            }
            if (!_loaded || app.Server == null || app._isTransitioning.IsSet) return;
            if (!_networkRequested)
            {
                NativeSessionSettings.Finish();
                NativeDefinitionSettings.Finish();
                var options = Config.Network.HostOptions(false);
                options.Dedicated = true;
                MpPlugin.Controller.Request(options, MpClock.NowMs());
                _networkRequested = true;
                System.Threading.Tasks.Task.Run(async () =>
                {
                    try
                    {
                        string latest = await UpdatePrompt.LatestAsync();
                        if (VersionMismatch.IsNewer(latest, MpPlugin.Version))
                            GameLog.Instance.Warn($"update: Y51_SE2Online {latest} is available; this server runs {MpPlugin.Version}. Players only join on the server's exact version. Download: {VersionMismatch.DownloadPage}");
                    }
                    catch (Exception ex) { GameLog.Instance.Info("update check: " + ex.GetType().Name + ": " + ex.Message); }
                });
                Status = "Starting network";
                GameLog.Instance.Info("dedicated: native setting schema written to " + Config.DumpPath);
            }
            if (MpPlugin.Controller.State == OnlineSessionState.Failed)
                throw new InvalidOperationException(MpPlugin.Controller.Error);
            if (MpPlugin.Controller.State == OnlineSessionState.Hosting)
            {
                var network = NetSession.Current;
                if (!string.IsNullOrEmpty(network?.Failure) || network?.HasStopped == true)
                    Status = "Network offline: " + (network.Failure ?? "network thread stopped") + "; world saves continue";
                else if (network?.TransportReady != true) Status = "Starting Steam network";
                else Status = HostWorld.Current?.WorldReady == true ? "Hosting " + Config.WorldName : "Waiting for replication";
            }
            var ticks = app.Server._tickManagement;
            ticks.PauseWhenInMenu = false;
            ticks.Pause = false;
            ticks.Tick = true;
            var world = app.Server.Entity.Get<WorldSessionComponent>();
            // Set by SaveSessionComponent.OnSavePerformed after any completed save, the game's autosave included.
            var gameSave = world.OwnedSession?.SessionComponents?.TryGet<SaveSessionComponent>()?._lastEntryAutosaveTime;
            if (gameSave != _seenGameSave)
            {
                _seenGameSave = gameSave;
                if (!_saving) _gameSaveMs = MpClock.NowMs();
            }
            long now = MpClock.NowMs();
            if (!_saving && Config.SaveIntervalSeconds > 0 && now >= _nextSave && now - _gameSaveMs >= GameSaveGapMs)
            {
                // The game serializes the world on this thread before the save goes async: a big world's save start is a
                // frame of several hundred ms (the slow-frame warning names it), which no plugin code can move off it.
                Save(world, false).SkipWait();
                _saveStartCostMs = MpClock.NowMs() - now;
            }
        }
        catch (Exception ex) { Fail("runtime", ex); }
    }

    private static async Task Load(GameAppComponent app)
    {
        await Task.SetLifetime(app.Scene);
        bool resumed = false;
        try
        {
            ContainerId? save = null;
            if (File.Exists(Config.ResumePath))
            {
                var state = JsonSerializer.Deserialize<ResumeState>(File.ReadAllText(Config.ResumePath), DedicatedConfig.Json);
                if (state != null && state.SourceSaveId == Config.SaveId && state.SourceContent == Config.StartContent && state.WorldName == Config.WorldName
                    && (state.SourceWorkshop ?? "") == Config.StartWorkshop)
                {
                    save = new ContainerId(state.SaveId);
                    resumed = true;
                }
            }
            if (resumed && !(await SaveHelper.TryGetSaveAsync(save.Value)).HasValue)
                throw new InvalidOperationException($"The save {save} that {Config.ResumeFile} resumes is missing from {SavesFolder()}. Put it back, or delete {Config.ResumeFile} to start from the configured world again.");
            if (!resumed && Config.StartWorkshopId == 0 && string.IsNullOrWhiteSpace(Config.StartContent))
            {
                var saves = new System.Collections.Generic.List<WorldPick.Save>();
                await foreach (var container in SaveHelper.IterateAllSavesAsync())
                    if (!container.IsExternal && !WorldPick.IsTempAutosave(container.Id.ToString())) saves.Add(new WorldPick.Save(container.Id.ToString(), container.Meta.DisplayName));
                string gameFolder = GameSavesFolder();
                var pick = WorldPick.Resolve(Config.SaveId, Config.WorldName, saves, SavesFolder(), gameFolder == null ? null : FolderSaves(gameFolder), gameFolder);
                if (pick.Error != null) throw new InvalidOperationException(pick.Error);
                if (pick.Note != null) GameLog.Instance.Warn("dedicated: " + pick.Note);
                if (pick.Id != null)
                {
                    save = new ContainerId(pick.Id);
                    GameLog.Instance.Info($"dedicated: loading save '{pick.Id}' ({(string.IsNullOrWhiteSpace(Config.SaveId) ? "WorldName" : "SaveId")})");
                }
            }
            if (!resumed && Config.StartWorkshopId != 0)
            {
                if (!Headless) throw new InvalidOperationException("StartWorkshop needs the headless server (runner.json Headless: true).");
                Status = "Downloading Workshop world";
                save = await DedicatedMods.StartWorldAsync(app, Config);
            }
            else if (!resumed && !string.IsNullOrWhiteSpace(Config.StartContent))
                save = app.Scene.Single<DefaultWorldsComponent>().TryFindDefaultWorldContainerId(Config.StartContent)
                    ?? new ContainerId(SaveHelper.GetContentContainerIdString(Config.StartContent));
            ModList mods = Config.OverrideModList.HasValue
                ? SerializationHelper.DeserializeFromJson<ModList>(Config.OverrideModList.Value.GetRawText()) : null;
            // SE2 4.0 dedicated servers run Steam as a game server, windowed or not: the Workshop comes through it.
            Status = "Downloading Workshop mods";
            mods = await DedicatedMods.PrepareAsync(app, Config, save, mods);
            bool inProfile = save.HasValue && (resumed || (string.IsNullOrWhiteSpace(Config.StartContent) && Config.StartWorkshopId == 0));
            var load = new WorldLoadArgs
            {
                SaveId = save, OverrideDisplayName = inProfile ? null : Config.WorldName, EnableAutosave = Config.EnableAutosave,
                PlayMission = Config.PlayMission, SkipDebugMenuWarning = true, SkipNewerSaveWarning = true,
                OverrideModList = mods
            };
            // SE2 4.0: Keen's own dedicated server session (Steam game server listener + network replication).
            try { await app.LoadDedicatedServerAsync(load); }
            catch (Exception ex) when (inProfile)
            {
                if (!await RestoreNewestAutosave(app, save.Value, ex)) throw;
                // The windowed server's failure handler is still returning to the main menu.
                for (int i = 0; i < 300 && app._isTransitioning.IsSet; i++) await Task.Delay(TimeSpan.FromMilliseconds(100));
                await app.LoadDedicatedServerAsync(load);
            }
            var world = app.Server.Entity.Get<WorldSessionComponent>();
            NativeSessionSettings.Finish();
            NativeDefinitionSettings.Finish();
            // Content has to be saved into the profile once. A profile save is already durable, and saving it straight back
            // can deadlock SE2: the commit holds the save collection, a world area spawning its blocking bundle on the main
            // thread waits for a voxel blob from that collection, and the commit's continuation needs the main thread.
            if (inProfile)
            {
                _savedId = (world.SaveContainerId ?? save.Value).ToString();
                // SE2 saves into the folder named after the shown name, so a shown name that is another folder's name would overwrite that save.
                if (world.SaveContainerId.HasValue && world.DisplayName != _savedId)
                {
                    GameLog.Instance.Info($"dedicated: save '{_savedId}' shows the name '{world.DisplayName}'; it is saved back into its own folder under its folder name");
                    await world.SetDisplayName(_savedId);
                }
                _nextSave = MpClock.NowMs() + Config.SaveIntervalSeconds * 1000L;
            }
            else await Save(world, false);
            if (Error == null)
            {
                _loaded = true;
                Status = "World loaded";
                PlanRestart(DateTime.Now);
                GameLog.Instance.Info("dedicated: ordinary native world loaded" + (inProfile ? "" : " and saved") + "; ContainerId " + world.SaveContainerId);
            }
        }
        catch (Exception ex) { Fail("world load", !resumed && Config.StartWorkshopId != 0 ? DedicatedMods.ExplainStartFailure(ex, Config.StartWorkshopId) : ex); }
    }

    // SE2 commits a save in place (TransactionalFileSystem.ApplyChangesAsync copies each file over the old one, then rewrites
    // .container-info), so a process killed mid-commit leaves files from two saves. Its autosave slots are separate copies under
    // .autosaves; Keen lists only the intact ones. ponytail: one attempt; blobs are shared by name and are not kept aside.
    private static async Task<bool> RestoreNewestAutosave(GameAppComponent app, ContainerId saveId, Exception error)
    {
        await Task.SetLifetime(app.Scene);
        var slots = new System.Collections.Generic.List<(int Slot, DateTime SavedUtc)>();
        var backups = new System.Collections.Generic.Dictionary<int, Backup<WorldSessionSaveGameMetaData>>();
        if (await SaveHelper.TryGetSaveAsync(saveId) is { } container)
        {
            await foreach (var backup in AutosaveSlots.GetAutosavesAsync(container))
            {
                int slot = AutosaveSlots.TryGetSlotIndex(backup.Id);
                if (slot < 0) continue;
                backups[slot] = backup;
                slots.Add((slot, new DateTime(backup.Meta.SaveGameCreationTimeInTicks, DateTimeKind.Utc)));
            }
        }
        var (pick, message) = WorldPick.Recovery(saveId.ToString(), error.GetBaseException().Message, slots);
        GameLog.Instance.Error("dedicated: " + message);
        Status = pick is null ? "Failed: " + message : "Restoring autosave " + pick;
        WriteStatus(true);
        if (pick is not int chosen) return false;
        string dir = Path.Combine(SavesFolder(), saveId.ToString());
        string broken = Path.Combine(dir, ".broken-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(broken);
        foreach (string file in Directory.EnumerateFiles(dir)) File.Copy(file, Path.Combine(broken, Path.GetFileName(file)), true);
        try { await backups[chosen].RestoreAsync(); }
        // Restore copies the slot in and then updates the save list, but the save list's folder watcher drops the entry while
        // the folder is emptied; the files are in place and the watcher adds the save back once .container-info is there again.
        catch (System.Collections.Generic.KeyNotFoundException) when (File.Exists(Path.Combine(dir, ".container-info"))) { }
        for (int i = 0; i < 100 && !(await SaveHelper.TryGetSaveAsync(saveId)).HasValue; i++) await Task.Delay(TimeSpan.FromMilliseconds(100));
        GameLog.Instance.Info($"dedicated: autosave .autosaves/{chosen} restored over '{saveId}'; loading it");
        return true;
    }

    private static string SavesFolder() => Path.GetFullPath(Singleton<Keen.VRage.Library.Filesystem.FileSystem>.Instance.GetAppDataFiles().GetAbsolutePath("SaveGames"));

    /// <summary>The game's own saves on this PC, when the server runs from a separate profile; only used to say where a missing save is.</summary>
    private static string GameSavesFolder()
    {
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpaceEngineers2", "AppData", "SaveGames");
        return Directory.Exists(folder) && !string.Equals(Path.GetFullPath(folder).TrimEnd('\\'), SavesFolder().TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ? folder : null;
    }

    private static System.Collections.Generic.List<WorldPick.Save> FolderSaves(string folder)
    {
        var saves = new System.Collections.Generic.List<WorldPick.Save>();
        try
        {
            foreach (string dir in Directory.EnumerateDirectories(folder))
            {
                string name = null;
                try
                {
                    using var info = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, ".container-info")));
                    name = info.RootElement.GetProperty("$Value").GetProperty("Meta").GetProperty("DisplayName").GetString();
                }
                catch (Exception) { }
                saves.Add(new WorldPick.Save(Path.GetFileName(dir), name));
            }
        }
        catch (Exception ex) { GameLog.Instance.Info("dedicated: could not list " + folder + ": " + ex.Message); }
        return saves;
    }

    /// <summary>Saves now for the cleanup (SaveBefore) and reports whether it worked; false at once while a save runs.</summary>
    internal static void SaveNow(Action<bool> done)
    {
        var world = MpPlugin.App?.Server?.Entity.Get<WorldSessionComponent>();
        if (world == null || _saving || _exiting) { done(false); return; }
        SaveAndReport(world, done).SkipWait();
    }

    private static async Keen.VRage.Library.Threading.Task SaveAndReport(WorldSessionComponent world, Action<bool> done)
    {
        await Task.SetLifetime(world);
        bool ok = false;
        try { ok = await Save(world, false); }
        finally { done(ok); }
    }

    private static async Keen.VRage.Library.Threading.Task<bool> Save(WorldSessionComponent world, bool exiting)
    {
        await Task.SetLifetime(world);
        _saving = true;
        try
        {
            SaveResults result = SaveResults.FailureSaveInProgress;
            // The game's own autosave can start between the wait and our save; wait for it and try again. The game ends a save
            // by completing its saving task before it clears the guard (WorldSessionComponent.RunGuardedSaveAsync), and Keen
            // tasks run their continuations inline: right after WaitForSavingToFinish the guard is still set, and every retry
            // came back FailureSaveInProgress within that same call. A delay lets the game's save finish first.
            for (int attempt = 0; attempt < 20 && result == SaveResults.FailureSaveInProgress; attempt++)
            {
                await world.WaitForSavingToFinish();
                if (world.IsSaving) await Task.Delay(TimeSpan.FromMilliseconds(100));
                result = await world.TrySaveWorldSession(WorldPick.SaveName(world.SaveContainerId.HasValue ? _savedId : null, world.SaveContainerId.HasValue, Config.WorldName), variant: null);
            }
            if (result == SaveResults.FailureSaveInProgress && !exiting && _loaded)
            {
                // The game kept saving the world itself (its autosave writes a slot under .autosaves, not the save the
                // resume state names, so it does not count as this save): not a failure, try again soon.
                _nextSave = MpClock.NowMs() + SaveRetry.After(busy: true, _lastSaveFailed).RetryMs;
                GameLog.Instance.Info($"dedicated: the game is saving the world itself; saving again in {SaveRetry.BusyRetryMs / 1000} s");
                return false;
            }
            if (result != SaveResults.Success) throw new InvalidOperationException("Native world save returned " + result);
            var state = new ResumeState(world.SaveContainerId.Value.ToString(), Config.SaveId, Config.StartContent, Config.WorldName, Config.StartWorkshop);
            Directory.CreateDirectory(Path.GetDirectoryName(Config.ResumePath));
            string temp = Config.ResumePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(state, DedicatedConfig.Json));
            AutoUpdate.ReplaceFile(temp, Config.ResumePath);
            _savedId = state.SaveId;
            _lastSaveUtc = DateTime.UtcNow;
            _lastSaveFailed = false;
            _nextSave = MpClock.NowMs() + Config.SaveIntervalSeconds * 1000L;
            GameLog.Instance.Info("dedicated: durable native save completed " + world.SaveContainerId);
            if (Error?.StartsWith("final save:", StringComparison.Ordinal) == true || Error?.StartsWith("saving:", StringComparison.Ordinal) == true)
            {
                Error = null;
                ErrorDetail = null;
            }
            WriteStatus(true);
            return true;
        }
        catch (Exception ex)
        {
            if (exiting)
            {
                Error = "final save: " + ex.GetBaseException().Message;
                Status = "Exit cancelled; final save failed. Retry save or stop.";
                GameLog.Instance.Error("dedicated: " + Status + "\n" + ex);
                WriteStatus(true);
            }
            else if (_loaded)
            {
                // A failed periodic save must not stop the server's tick (and every later save) like a load failure does.
                var retry = SaveRetry.After(busy: false, _lastSaveFailed);
                _nextSave = MpClock.NowMs() + retry.RetryMs;
                // Error would stop the server (Active); players are told once per run of failures instead.
                if (retry.Warn) Say("The server could not save the world and keeps retrying. Admins: check the server log before restarting.");
                _lastSaveFailed = retry.Failure;
                GameLog.Instance.Error("dedicated: periodic save failed, retrying in 60 s: " + ex);
            }
            else Fail("saving", ex);
            return false;
        }
        finally { _saving = false; WriteStatus(true); }
    }

    private static readonly System.Collections.Concurrent.ConcurrentQueue<string> Commands = new();
    private static System.Threading.Timer _commandPoll;
    private static int _commandPolling;

    // A slow or busy disk (an HDD under antivirus, a panel's network share) made the per-frame File.Exists cost up to
    // 750 ms of server frame; the command file is read off the game thread four times a second instead.
    private static void StartCommandPoll() => _commandPoll ??= new System.Threading.Timer(_ =>
    {
        if (System.Threading.Interlocked.Exchange(ref _commandPolling, 1) == 1) return;
        try
        {
            if (!File.Exists(Config.CommandPath)) return;
            string command = File.ReadAllText(Config.CommandPath).Trim();
            File.Delete(Config.CommandPath);
            Commands.Enqueue(command);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        finally { System.Threading.Volatile.Write(ref _commandPolling, 0); }
    }, null, 250, 250);

    /// <summary>A save or the world load is running: console commands wait (the command file) or are refused (RCON).</summary>
    internal static bool Busy => _saving || (_loading && !_loaded && Error == null);
    internal static bool Exiting => _exiting;

    internal sealed record Snapshot(string Status, string Error, bool WorldLoaded, bool Saving, bool Exiting, string SaveId,
        DateTime? LastSaveUtc, DateTime? NextRestartUtc, DateTime StartedUtc, bool RestartRequested, bool LastSaveFailed);

    internal static Snapshot State() => new(Status, Error, _loaded, _saving, _exiting, _savedId, _lastSaveUtc, _restartAt?.ToUniversalTime(),
        StartedLocal.ToUniversalTime(), _restartRequested, _lastSaveFailed);

    private static void CheckCommand(GameAppComponent app)
    {
        StartCommandPoll();
        if (Busy || !Commands.TryDequeue(out string command)) return;
        RunCommand(app, command);
    }

    /// <summary>One console command, on the game thread: from the command file (the runner, scripts) or RCON. Results are logged.</summary>
    internal static void RunCommand(GameAppComponent app, string command)
    {
        if (command.Equals("stop", StringComparison.OrdinalIgnoreCase))
        {
            _restartRequested = false;
            if (!StartCountdown()) StopNow(app);
        }
        else if (command.Equals("stop now", StringComparison.OrdinalIgnoreCase)) { _restartRequested = false; StopNow(app); }
        else if (command.Equals("restart", StringComparison.OrdinalIgnoreCase)) BeginRestart(app, "console");
        else if (command.Equals("restart now", StringComparison.OrdinalIgnoreCase)) { _restartRequested = true; GameLog.Instance.Info("dedicated: restarting now (console)"); StopNow(app); }
        else if (command.Equals("restart cancel", StringComparison.OrdinalIgnoreCase)) CancelRestart();
        else if (command.StartsWith("restart update", StringComparison.OrdinalIgnoreCase)) UpdateRestart(command.Substring("restart update".Length).Trim());
        else if (command.StartsWith("motd", StringComparison.OrdinalIgnoreCase) && (command.Length == 4 || command[4] == ' ')) SetMotd(command.Substring(4).Trim());
        else if (command.Equals("save", StringComparison.OrdinalIgnoreCase))
        {
            var world = app.Server?.Entity.Get<WorldSessionComponent>();
            if (world == null) throw new InvalidOperationException("World has not loaded yet.");
            Save(world, false).SkipWait();
        }
        else if (string.Join(' ', command.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Equals("mods check", StringComparison.OrdinalIgnoreCase))
        {
            // Admins may run it (RCON): at most once a minute, so it cannot hammer Steam's Web API.
            if (MpClock.NowMs() - _lastForcedCheckMs < 60_000) GameLog.Instance.Info("console: the Workshop was asked less than a minute ago; try again shortly");
            else { _lastForcedCheckMs = MpClock.NowMs(); _nextModCheckMs = 0; _modCheckForced = true; GameLog.Instance.Info("console: checking the Workshop for newer versions of this world's mods now"); }
        }
        else if (command.Equals("mods", StringComparison.OrdinalIgnoreCase)) GameLog.Instance.Info("console: " + DedicatedMods.Describe() + (SyncEngine.BrokenMods.Skipped is { Count: > 0 } skipped ? "; skipped as broken: " + string.Join(", ", skipped) : ""));
        else if (AdminCommand(command)) { }
        else throw new ArgumentException("Unknown dedicated command: " + command);
    }

    private static int OnlinePlayers() => NetSession.Current?.Host?.PlayerTable().Count(p => p.Connected) ?? 0;

    private static void Say(string text) => NetSession.Current?.Host?.LocalChat(text, true);

    // Players online get chat warnings first; with nobody online (or the countdown set to 0) the server stops at once.
    private static bool StartCountdown()
    {
        if (_shutdownAtMs != 0) return true;
        int seconds = Config.ShutdownCountdownSeconds;
        if (seconds <= 0 || OnlinePlayers() == 0) return false;
        _shutdownAtMs = MpClock.NowMs() + seconds * 1000L;
        _shutdownAnnounced = seconds;
        Say(ShutdownNotice.Text(seconds, _restartRequested));
        Status = _restartRequested ? $"Restarting in {seconds} s: players were warned in chat (restart now skips the wait, restart cancel stops it)"
            : $"Stopping in {seconds} s: players were warned in chat (stop now skips the wait)";
        WriteStatus(true);
        return true;
    }

    private static void TickCountdown(GameAppComponent app)
    {
        int left = (int)Math.Ceiling((_shutdownAtMs - MpClock.NowMs()) / 1000.0);
        if (left <= 0 || OnlinePlayers() == 0) { StopNow(app); return; }
        if (!ShutdownNotice.Due(_shutdownAnnounced, left)) return;
        _shutdownAnnounced = left;
        Say(ShutdownNotice.Text(left, _restartRequested));
    }

    /// <summary>Picks the next scheduled restart after <paramref name="after"/>; none when the schedule is off.</summary>
    private static void PlanRestart(DateTime after)
    {
        var settings = Config.AutoRestart;
        _restartAt = settings.Scheduled ? RestartSchedule.Next(after, StartedLocal, settings.Times, settings.EveryHours, settings.MinUptimeMinutes) : null;
        _restartWarned = int.MaxValue;
        if (_restartAt is DateTime at) GameLog.Instance.Info("dedicated: next scheduled restart " + at.ToString("yyyy-MM-dd HH:mm") + " (server local time)");
    }

    // Warnings at the configured lead times while players are online; the stop countdown then ends on the scheduled time.
    private static void TickRestart(GameAppComponent app)
    {
        if (_restartRequested || _restartAt is not DateTime at) return;
        double left = (at - DateTime.Now).TotalSeconds;
        bool online = OnlinePlayers() > 0;
        int countdown = online ? Config.ShutdownCountdownSeconds : 0;
        if (left > countdown)
        {
            int warn = online ? RestartSchedule.WarnDue(Config.AutoRestart.WarnMinutes, _restartWarned, left) : 0;
            if (warn > 0 && warn * 60 > countdown) { _restartWarned = warn; Say(_updateWhat != null ? $"Server restart in {warn} min to update {_updateWhat} - finish up and get somewhere safe." : RestartSchedule.WarnText(warn)); }
            return;
        }
        if (_lastSaveFailed || _saving)
        {
            // A restart drops whatever the last good save missed; wait for a save to succeed instead of looping.
            _restartAt = DateTime.Now.AddMinutes(_saving ? 1 : 10);
            GameLog.Instance.Warn("dedicated: scheduled restart postponed to " + _restartAt.Value.ToString("HH:mm") + (_saving ? ": a save is running" : ": the last save failed"));
            return;
        }
        BeginRestart(app, _updateWhat != null ? "update " + _updateWhat : "scheduled");
    }

    // Queued by the runner's live console once it has downloaded and verified a server update: the same warnings as a
    // scheduled restart (AutoRestart.WarnMinutes while players are online, then the countdown), and the same
    // postponement while a save is running or the last one failed.
    // What a pending update restart installs ("Space Engineers 2 Online to 0.17.6", "Workshop mod Foo"), or null.
    private static string _updateWhat;
    private static void UpdateRestart(string version)
    {
        _updateWhat = "Space Engineers 2 Online to " + (version.Length == 0 ? "a new version" : version);
        int lead = OnlinePlayers() > 0 ? Config.AutoRestart.WarnMinutes.DefaultIfEmpty(0).Max() : 0;
        _restartAt = DateTime.Now.AddMinutes(lead);
        _restartWarned = int.MaxValue;
        GameLog.Instance.Info($"dedicated: update of {_updateWhat} is verified and staged by the runner; restarting to install it at {_restartAt.Value:HH:mm}");
        WriteStatus(true);
    }

    // Workshop mods: a dedicated server downloads them only when it starts, while players joining get the newest version.
    // Every ModUpdates.CheckMinutes the Web API is asked off the game thread; a newer version gets the same warned restart
    // as a server update (or, with Restart false, a log line and a chat note). workshop-updates.json remembers what the
    // server restarted for, so a stale copy from Steam cannot restart it in a loop.
    private static long _nextModCheckMs;
    private static bool _modCheckForced;
    private static System.Threading.Tasks.Task<string> _modCheck;
    private static readonly System.Net.Http.HttpClient WorkshopApi = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly System.Collections.Generic.HashSet<string> ModNotes = new();
    // The content manifest of each installed item, learned at the first check that shows no update since the install.
    private static readonly System.Collections.Generic.Dictionary<ulong, string> InstalledContent = new();
    private static long _lastForcedCheckMs = long.MinValue / 2;

    private static void TickModUpdates()
    {
        int minutes = Config.ModUpdates.CheckMinutes;
        long now = MpClock.NowMs();
        if (_modCheck is { IsCompleted: true } done)
        {
            _modCheck = null;
            string error = done.IsCompletedSuccessfully ? done.Result : done.Exception?.GetBaseException().Message;
            if (error != null) GameLog.Instance.Info("dedicated: Workshop update check failed (" + error + "); trying again at the next check");
            else ApplyModUpdates();
        }
        if (_modCheck != null || (minutes <= 0 && !_modCheckForced)) return;
        if (_nextModCheckMs == 0 && !_modCheckForced) { _nextModCheckMs = now + Math.Max(1, minutes) * 60_000L; return; }
        if (now < _nextModCheckMs) return;
        _modCheckForced = false;
        _nextModCheckMs = now + Math.Max(5, minutes) * 60_000L;
        var installed = DedicatedMods.Installed();
        if (installed.Count == 0) { GameLog.Instance.Info("dedicated: no Workshop mods to check for updates"); return; }
        var ids = installed.Keys.Take(WorkshopUpdates.MaxIds).ToList();
        _modCheck = System.Threading.Tasks.Task.Run(async () =>
        {
            using var response = await WorkshopApi.PostAsync(WorkshopUpdates.DetailsUrl, new System.Net.Http.FormUrlEncodedContent(WorkshopUpdates.Form(ids))).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return "HTTP " + (int)response.StatusCode;
            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (body.Length > 4 << 20 || WorkshopUpdates.Parse(body) is not { } details) return "the answer was not Steam's Workshop details";
            DedicatedMods.Latest = details;
            return null;
        });
    }

    private static void ApplyModUpdates()
    {
        string guard = Path.Combine(Config.Directory, "workshop-updates.json");
        var restarted = WorkshopUpdates.LoadRestarted(guard);
        var installed = DedicatedMods.Installed();
        var latest = DedicatedMods.Latest;
        WorkshopUpdates.Learn(installed, latest, InstalledContent);
        foreach (ulong id in WorkshopUpdates.Stale(installed, latest, restarted, InstalledContent))
            if (ModNotes.Add("stale:" + id + ":" + latest[id].TimeUpdated))
                GameLog.Instance.Warn($"dedicated: Workshop mod {DedicatedMods.NameOf(id)} ({id}) still has its older version after a restart for the {WorkshopUpdates.Time(latest[id].TimeUpdated)} update; Steam served an old copy (or only the mod's page changed), so the server does not restart for it again");
        var changed = WorkshopUpdates.Changed(installed, latest, restarted, InstalledContent);
        if (changed.Count == 0) return;
        string names = string.Join(", ", changed.Select(DedicatedMods.NameOf));
        string what = changed.Count == 1 ? "Workshop mod " + names : "Workshop mods " + names;
        if (!Config.ModUpdates.Restart)
        {
            if (ModNotes.Add("note:" + string.Join(",", changed.Select(id => id + "@" + latest[id].TimeUpdated))))
            {
                GameLog.Instance.Info($"dedicated: {what} updated on the Workshop; ModUpdates.Restart is off, so the next restart loads it");
                Say($"{what} updated on the Workshop; the next server restart loads it.");
            }
            return;
        }
        if (_restartRequested) return;
        int lead = OnlinePlayers() > 0 ? Config.AutoRestart.WarnMinutes.DefaultIfEmpty(0).Max() : 0;
        DateTime at = DateTime.Now.AddMinutes(lead);
        foreach (ulong id in changed) restarted[id] = latest[id].TimeUpdated;
        try { WorkshopUpdates.SaveRestarted(guard, restarted); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            GameLog.Instance.Warn("dedicated: could not write " + guard + " (" + ex.Message + "); not restarting for the mod update, so a stale copy cannot loop");
            return;
        }
        if (_restartAt is DateTime planned && planned <= at)
        {
            GameLog.Instance.Info($"dedicated: {what} updated on the Workshop; the restart already planned for {planned:HH:mm} loads it");
            return;
        }
        _updateWhat = what;
        _restartAt = at;
        _restartWarned = int.MaxValue;
        GameLog.Instance.Info($"dedicated: {what} updated on the Workshop; restarting at {at:HH:mm} to load it (restart cancel keeps the old version until the next restart)");
        WriteStatus(true);
    }

    private static void BeginRestart(GameAppComponent app, string why)
    {
        if (_restartRequested && _shutdownAtMs != 0) return;
        _restartRequested = true;
        GameLog.Instance.Info("dedicated: restarting (" + why + ")");
        if (!StartCountdown()) StopNow(app);
        WriteStatus(true);
    }

    private static void CancelRestart()
    {
        bool counting = _restartRequested && _shutdownAtMs != 0;
        _restartRequested = false;
        _updateWhat = null;
        _shutdownAtMs = 0;
        if (counting) Say("Server restart cancelled.");
        // Skips the restart that was due (or counting down) and plans the one after it.
        PlanRestart((_restartAt ?? DateTime.Now).AddMinutes(1));
        Status = "Restart cancelled";
        GameLog.Instance.Info("console: restart cancelled" + (_restartAt is DateTime at ? "; next scheduled " + at.ToString("yyyy-MM-dd HH:mm") : ""));
        WriteStatus(true);
    }

    private static void SetMotd(string text)
    {
        var host = NetSession.Current?.Host ?? throw new InvalidOperationException("Multiplayer is not running yet.");
        string result = text.Length == 0 ? "Message of the day: " + (host.MotdTemplate.Length == 0 ? "(none)" : host.MotdTemplate) + ". Use motd <text> or motd clear."
            : host.LocalAdmin(new AdminRequest(AdminAction.Motd, Name: text.Equals("clear", StringComparison.OrdinalIgnoreCase) ? "" : text));
        GameLog.Instance.Info("console: " + result);
    }

    private static void StopNow(GameAppComponent app)
    {
        _shutdownAtMs = 0;
        if (OnlinePlayers() > 0) Say(_restartRequested ? ShutdownNotice.RestartNow : ShutdownNotice.Now);
        _shutdownRequests++;
        _forceSaveOnExit = true;
        app.ExitGame();
    }

    /// <summary>Console moderation shared with RCON and chat (<see cref="HostCommands"/>): say, kick, ban, unban, bans,
    /// admin, deadmin, admins, group, whitelist, pvp, raid, pcu, sun, usage, perms, cleanup.</summary>
    internal static bool AdminCommand(string command)
    {
        string result = HostCommands.Run(NetSession.Current?.Host, command);
        if (result == null) return false;
        if (result.Length > 0) GameLog.Instance.Info("console: " + result);
        return true;
    }

    // Players' /commands in chat, a few per tick, each run as its player (permissions and rank checked like the panel).
    private static void RunChatCommands(GameAppComponent app)
    {
        var host = NetSession.Current?.Host;
        if (host == null) return;
        for (int i = 0; i < 4 && host.ChatCommands.TryDequeue(out var command); i++)
            host.RunChatCommand(command, line =>
            {
                var captured = new System.Text.StringBuilder();
                GameLog.Capture = captured;
                string reply;
                try
                {
                    string verb = Core.RconAccess.Verb(line);
                    reply = verb switch
                    {
                        "status" => Status + " | Players: " + OnlinePlayers() + "/" + Config.Network.MaxPlayers + " | SE2 Online " + MpPlugin.Version,
                        "players" when !HostCommands.IsPlayerInfo(line) => "Online: " + string.Join(", ", host.AccessPlayers().Where(p => p.Connected).Select(p => p.Name)),
                        _ => null
                    };
                    if (reply == null && verb == "motd") SetMotd(line.Length > 4 ? line[4..].Trim() : "");
                    else if (reply == null && verb == "mods") RunCommand(app, string.Join(' ', line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2)).ToLowerInvariant());
                    else if (reply == null && !AdminCommand(line)) reply = "That command runs on the server console only.";
                }
                finally { GameLog.Capture = null; }
                return reply ?? captured.ToString().Replace("console: ", "").TrimEnd('\n');
            });
    }

    private static bool ExitPrefix(GameApp __instance)
    {
        if (!Requested || Config == null || _allowExit || !(Config.SaveOnExit || _forceSaveOnExit)) return true;
        if (_exiting) return false;
        var world = MpPlugin.App?.Server?.Entity.Get<WorldSessionComponent>();
        if (world == null) return true;
        _exiting = true;
        if (Error?.StartsWith("final save:", StringComparison.Ordinal) == true) { Error = null; ErrorDetail = null; }
        Status = "Saving before exit";
        WriteStatus(true);
        FinishExit(__instance, world).SkipWait();
        return false;
    }

    private static async Task FinishExit(GameApp app, WorldSessionComponent world)
    {
        await Task.SetLifetime(world);
        try
        {
            await world.WaitForSavingToFinish();
            if (!(await Save(world, true)))
            {
                _exiting = false;
                return;
            }
            _allowExit = true;
            Status = "Shutdown complete";
            WriteStatus(true);
            // The world is saved; never leave a headless SE2 process behind if the native exit stalls.
            new System.Threading.Thread(() => { System.Threading.Thread.Sleep(30000); Environment.Exit(0); }) { IsBackground = true, Name = "Y51_SE2Online exit watchdog" }.Start();
            app.Exit();
        }
        catch (Exception ex)
        {
            _exiting = false;
            Error = "final save: " + ex.GetBaseException().Message;
            Status = "Exit cancelled; final save failed. Retry save or stop.";
            GameLog.Instance.Error("dedicated: " + Status + "\n" + ex);
            WriteStatus(true);
        }
    }

    private static void Fail(string operation, Exception ex)
    {
        Error = operation + ": " + ex.GetBaseException().Message;
        ErrorDetail = ex.ToString();
        Status = "Failed: " + Error;
        GameLog.Instance.Error("dedicated: " + Status + "\n" + ex);
        WriteStatus(true);
    }

    internal static void ReportNativeFailure(Exception exception) => Fail("native runtime", exception);

    private static void WriteStatus(bool force = false)
    {
        if (_statusPath == null || (!force && MpClock.NowMs() - _lastStatus < 1000)) return;
        _lastStatus = MpClock.NowMs();
        long started = _lastStatus;
        try
        {
            var network = NetSession.Current;
            object identity = network?.GetType().GetProperty("LocalIdentity")?.GetValue(network);
            var state = new
            {
                UpdatedUtc = DateTime.UtcNow, ProcessId = Environment.ProcessId, Status, Error, ErrorDetail,
                SaveId = _savedId, WorldLoaded = _loaded, SaveInProgress = _saving, Exiting = _exiting,
                LastSaveUtc = _lastSaveUtc, ShutdownRequests = _shutdownRequests, FinalSaveCompleted = _allowExit,
                RestartRequested = _restartRequested, NextRestartUtc = _restartAt?.ToUniversalTime(),
                Players = network?.Players.Where(p => p.Peer.HasValue).Select(p => new
                {
                    SteamId = p.Peer.Value.Value, p.Name, State = p.CharacterNetId > 0 ? "Playing" : "Joining"
                }).ToArray() ?? [],
                Headless, NativeClientPresent = MpPlugin.App?.Client != null,
                NativePlayerClientIds = _loaded ? MpPlugin.App?.Server?.Entity.Get<WorldSessionComponent>()?.OwnedSession?.SessionComponents?
                    .Get<Keen.Game2.Simulation.GameSystems.Identities.IdentitiesSessionComponent>().GetClientIds().Select(id => id.Id).ToArray() : null,
                PresentationComponents = Headless ? MpPlugin.App?.Scene.Engine?.Components
                    .Where(c => HeadlessEngine.IsGraphical(c.GetType())).Select(c => c.GetType().FullName).ToArray() : null,
                WorldSync = HostWorld.Current?.Stats(),
                ControllerState = MpPlugin.Controller?.State.ToString(),
                NetworkFailure = network?.Failure, DirectoryError = network?.DirectoryError,
                NetworkSteamIdentity = identity?.ToString(), NetworkStatus = network?.DescribeStatus()
            };
            string temp = _statusPath + ".tmp";
            string json = JsonSerializer.Serialize(state, DedicatedConfig.Json);
            string path = _statusPath;
            void Write()
            {
                lock (StatusFileLock)
                {
                    try
                    {
                        File.WriteAllText(temp, json);
                        AutoUpdate.ReplaceFile(temp, path);
                        if (_statusFailures > 0)
                        {
                            GameLog.Instance.Info($"dedicated: status file written again after {_statusFailures} failed write(s)");
                            _statusFailures = 0;
                        }
                    }
                    catch (Exception ex)
                    {
                        // Something reading the file (an SMB poll) blocks every once-a-second write: one line per outage.
                        if (_statusFailures++ == 0)
                        {
                            GameLog.Instance.Warn("dedicated: status write failed: " + ex.Message + " (further failures are counted until it recovers)");
                        }
                    }
                }
            }
            // The once-a-second write goes off the game thread (a slow or locked file stalled the server frame); state
            // changes stay synchronous so the runner sees them, the final save above all, before the process exits.
            if (force) Write();
            else System.Threading.Tasks.Task.Run(Write);
        }
        catch (Exception ex) { GameLog.Instance.Warn("dedicated: status write failed: " + ex.Message); }
        _statusCostMs = MpClock.NowMs() - started;
    }
}
