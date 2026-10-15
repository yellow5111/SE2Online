using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using HarmonyLib;
using Keen.VRage.Core.Project;
using Keen.VRage.Core.Services;
using Keen.VRage.Core.Worlds;
using Keen.VRage.Library.Filesystem;
using Keen.VRage.Library.Filesystem.StorageManagers;
using Steamworks;
using Version = System.Version;
using Y51_SE2Online.Core;
using Y51_SE2Online.Steam;
using Y51_SE2Online.Sync;
using Y51_SE2Online.SyncEngine;

namespace Y51_SE2Online.Dedicated;

/// <summary>
/// Workshop mods on the headless server. The game's own Steam mod loader needs a signed-in Steam user, which a
/// dedicated server does not have, so the server downloads items through the anonymous game-server Workshop API
/// before the world loads and serves them to the game's Steam project locator from that folder.
/// </summary>
internal static class DedicatedMods
{
	private const ulong MaxItemBytes = 4UL << 30;

	private const int StallMs = 120000;

	private const int TotalMs = 30 * 60000;

	private static readonly ConcurrentDictionary<ulong, string> Folders = new ConcurrentDictionary<ulong, string>();

	private static readonly ConcurrentDictionary<ulong, WorldMod> Mods = new ConcurrentDictionary<ulong, WorldMod>();

	private static readonly List<string> Problems = new List<string>();

	private static IReadOnlyList<WorldMod> _loaded = Array.Empty<WorldMod>();

	// StartWorkshop when it is a mod: it stays in the world's mod list, so the saved world and every player load it.
	private static WorldMod _startMod;

	internal static void Install(Harmony harmony)
	{
		Type locator = AccessTools.TypeByName("Keen.VRage.Steam.EngineComponents.SteamProjectLocatorComponent")
			?? throw new MissingMemberException("SteamProjectLocatorComponent");
		harmony.Patch(AccessTools.Method(locator, "LoadProjectAsync"), prefix: new HarmonyMethod(typeof(DedicatedMods), nameof(LoadPrefix)));
		harmony.Patch(AccessTools.Method(locator, "UnloadProjectAsync"), prefix: new HarmonyMethod(typeof(DedicatedMods), nameof(UnloadPrefix)));
	}

#pragma warning disable CS0618 // VRageProjectID.ModId is how the game itself addresses a Workshop project.
	private static bool LoadPrefix(VRageProjectID project, ref Keen.VRage.Library.Threading.Task<VRageProject> __result)
	{
		ulong id = project.ModId is { } mod ? UGCHelper.ULongFromGuid(mod.Id) : 0UL;
		if (!Folders.TryGetValue(id, out string folder))
			throw new InvalidOperationException($"Workshop mod {id} ({project}) was not downloaded at startup; add it to Mods in the server config.");
		VRageProject loaded = LocalProjectLocator.GetProject(new FileInfo(ProjectFile(folder)));
		loaded.Data.ProjectID = project;
		__result = new Keen.VRage.Library.Threading.Task<VRageProject>(loaded);
		return false;
	}
#pragma warning restore CS0618

	private static bool UnloadPrefix(ref Keen.VRage.Library.Threading.Task __result)
	{
		__result = default;
		return false;
	}

	/// <summary>The world's own mods (from the config's OverrideModList or the save) plus the configured Workshop ids, all downloaded.</summary>
	internal static async Keen.VRage.Library.Threading.Task<ModList> PrepareAsync(Keen.Game2.GameAppComponent app, DedicatedConfig config, ContainerId? save, ModList configured)
	{
		await Keen.VRage.Library.Threading.Task.SetLifetime(app.Scene);
		ModList baseList = configured;
		if (baseList == null && save.HasValue && await SaveHelper.TryGetSaveAsync(save.Value) is { } container)
			baseList = await SaveHelper.TryGetModList(container, app);
		List<WorldMod> world = WorkshopMods.From(baseList);
		if (world.Count == 0 && config.WorkshopIds.Length == 0 && _startMod == null) return configured;
		ulong[] wanted = config.WorkshopIds.Concat(world.Where(m => m.WorkshopId != 0).Select(m => m.WorkshopId)).Distinct().ToArray();
		await System.Threading.Tasks.Task.Run(() => Download(config, wanted));
		foreach (ulong id in wanted)
			if (Folders.ContainsKey(id) && AsMod(id) == null) Problem($"Workshop item {id}: not a Space Engineers 2 mod (no valid .vrgproj of type Mod)");
		var added = config.WorkshopIds.Select(AsMod).Where(m => m != null).ToList();
		if (_startMod != null) added.Insert(0, _startMod);
		var kept = world.Where(m => m.WorkshopId == 0 || AsMod(m.WorkshopId) != null);
		_loaded = WorldMods.Merge(kept, added);
		GameLog.Instance.Info("dedicated: world mods: " + WorldMods.Summary(_loaded));
		return WorkshopMods.ToModList(_loaded);
	}

	/// <summary>Downloads the items not downloaded yet into <see cref="Folders"/>; failures go to <see cref="Problems"/>.</summary>
	private static void Download(DedicatedConfig config, IEnumerable<ulong> wanted)
	{
		ulong[] ids = wanted.Where(id => !Folders.ContainsKey(id)).ToArray();
		if (ids.Length == 0) return;
		string root = Path.Combine(config.Directory, "Workshop");
		Directory.CreateDirectory(root);
		MpOptions options = config.Network.HostOptions(false);
		options.Dedicated = true;
		long deadline = MpClock.NowMs() + TotalMs;
		// The network signs the game server in again once the world is up.
		using var steam = new SteamDedicatedSession();
		try
		{
			steam.Initialize(options, MpPlugin.CurrentGameVersion, () => MpClock.NowMs() > deadline);
		}
		catch (Exception ex)
		{
			Problem("Steam game-server sign-in for Workshop downloads failed: " + ex.Message);
			return;
		}
		if (!SteamGameServerUGC.BInitWorkshopForGameServer(new DepotId_t(WorldMods.SteamAppId), root))
		{
			Problem("Steam refused the Workshop folder " + root);
			return;
		}
		foreach (ulong id in ids)
		{
			try
			{
				if (Fetch(new PublishedFileId_t(id), deadline) is { } error)
				{
					Problem($"Workshop item {id}: {error}");
					continue;
				}
				if (!SteamGameServerUGC.GetItemInstallInfo(new PublishedFileId_t(id), out _, out string folder, 1024u, out _) || !Directory.Exists(folder))
				{
					Problem($"Workshop item {id}: Steam reports no install folder");
					continue;
				}
				Folders[id] = folder;
				GameLog.Instance.Info($"dedicated: Workshop item {id} ready in {folder}");
			}
			catch (Exception ex)
			{
				Problem($"Workshop item {id}: {ex.GetType().Name}: {ex.Message}");
			}
		}
	}

	/// <summary>The downloaded item as a world mod, or null when it is not a Space Engineers 2 mod project.</summary>
	private static WorldMod AsMod(ulong id)
	{
		if (Mods.TryGetValue(id, out WorldMod known)) return known;
		if (!Folders.TryGetValue(id, out string folder) || LoadProject(folder) is not { } project) return null;
		var mod = new WorldMod(project.Data.ProjectID.Guid, id, project.Data.Name ?? "", project.Data.Version?.ToString() ?? "");
		GameLog.Instance.Info($"dedicated: Workshop mod {mod.Describe()} ready");
		return Mods[id] = mod;
	}

	private static VRageProject LoadProject(string folder)
	{
		string file = ProjectFile(folder);
		VRageProject project = file == null ? null : LocalProjectLocator.GetProject(new FileInfo(file));
		return project == null || project.Exception != null || project.Data.Type != ProjectType.Mod ? null : project;
	}

	/// <summary>
	/// The world StartWorkshop names. A Workshop world is mounted read-only the way the game mounts one it downloaded
	/// (container steam_ugc_&lt;id&gt;); a mod's world is the "$Content:&lt;folder&gt;" container the game makes for each folder in the
	/// mod's Worlds. Either way the first save copies the world into the profile, like a vanilla StartContent world.
	/// </summary>
	internal static async Keen.VRage.Library.Threading.Task<ContainerId> StartWorldAsync(Keen.Game2.GameAppComponent app, DedicatedConfig config)
	{
		await Keen.VRage.Library.Threading.Task.SetLifetime(app.Scene);
		ulong id = config.StartWorkshopId;
		await System.Threading.Tasks.Task.Run(() => Download(config, new[] { id }));
		if (!Folders.TryGetValue(id, out string folder))
		{
			string why;
			lock (Problems) why = Problems.LastOrDefault() ?? "unknown error";
			throw new InvalidOperationException($"StartWorkshop: Workshop item {id} did not download ({why}).");
		}
		string key = config.StartContent?.Trim() ?? "";
		if (AsMod(id) is { } mod)
		{
			string[] worlds = ModWorlds(folder);
			if (worlds.Length == 0)
				throw new InvalidOperationException($"StartWorkshop: {mod.Describe()} is a mod without worlds (no saved world in its Worlds folder). List it under Mods instead and start from StartContent or SaveId.");
			if (key.Length == 0 && worlds.Length > 1)
				throw new InvalidOperationException($"StartWorkshop: {mod.Describe()} ships {worlds.Length} worlds; set StartContent to one of: {string.Join(", ", worlds)}");
			string world = key.Length == 0 ? worlds[0] : worlds.FirstOrDefault(w => w.Equals(key, StringComparison.OrdinalIgnoreCase))
				?? throw new InvalidOperationException($"StartWorkshop: {mod.Describe()} has no world '{key}'. Its worlds: {string.Join(", ", worlds)}");
			ContainerId container = SaveHelper.GetContentSaveId(world);
			if ((await SaveHelper.TryGetSaveAsync(container)).HasValue)
				throw new InvalidOperationException($"StartWorkshop: the world '{world}' in {mod.Describe()} has the name of a base game world, which takes precedence; rename the mod's world folder.");
			// The game registers a mod's worlds when the world load pushes the mod. Registering them now, under the project id the
			// mod list loads it with, makes the container exist before the load, and the later push skips the project.
			VRageProject project = LoadProject(folder);
			project.Data.ProjectID = WorkshopMods.ToRecord(mod).Project;
			AccessTools.Method(typeof(DefaultWorldsComponent), "RegisterWorldsFromProjects").Invoke(app.Scene.Single<DefaultWorldsComponent>(), new object[] { new[] { project } });
			if (!(await SaveHelper.TryGetSaveAsync(container)).HasValue)
				throw new InvalidOperationException($"StartWorkshop: the game could not open the world '{world}' in {mod.Describe()}; see the log above.");
			_startMod = mod;
			GameLog.Instance.Info($"dedicated: starting from world '{world}' of Workshop mod {mod.Describe()}");
			return container;
		}
		if (!File.Exists(Path.Combine(folder, "savegame")) && !File.Exists(Path.Combine(folder, "savegame.vrb")))
		{
			string kind = File.Exists(Path.Combine(folder, "grid.json")) || File.Exists(Path.Combine(folder, "grid.json.vrb")) ? "a blueprint"
				: ProjectFile(folder) != null ? "a project that is not a mod" : "not a world";
			throw new InvalidOperationException($"StartWorkshop: Workshop item {id} is {kind}. Use a Workshop world, or a mod that ships worlds in a Worlds folder.");
		}
		if (key.Length > 0)
			throw new InvalidOperationException($"StartWorkshop: Workshop item {id} is a single world; StartContent only picks a world inside a mod, so leave it empty.");
		var save = new ContainerId("steam_ugc_" + id);
		if (!(await SaveHelper.TryGetSaveAsync(save)).HasValue)
		{
			// The game writes this from the item's Steam metadata when it mounts a Workshop world; an anonymous server has no such query.
			string info = Path.Combine(folder, ".container-info");
			if (!File.Exists(info)) File.WriteAllText(info, ContainerInfoJson(config.WorldName, DateTime.UtcNow.Ticks));
			if (!(await AppDataStorageManager.Instance.TryMountContainerAsync<WorldSessionSaveGameMetaData>(FolderReader(folder), save)).HasValue)
				throw new InvalidOperationException($"StartWorkshop: the game could not open Workshop world {id} in {folder}.");
		}
		GameLog.Instance.Info($"dedicated: starting from Workshop world {id}");
		return save;
	}

	/// <summary>A Workshop world saved by another game version fails deep in deserialization; say so in the one-line error.</summary>
	internal static Exception ExplainStartFailure(Exception ex, ulong id)
	{
		for (Exception e = ex; e != null; e = e.InnerException)
		{
			if (e is not Keen.VRage.Library.Serialization.SerializationException serialization) continue;
			bool newer = serialization.BundleState is { } state
				&& (state.AggregateBundleVersionComparison & Keen.VRage.Library.Reflection.BundleVersionComparisonResult.HigherVersionThanReference) != 0;
			GameLog.Instance.Error($"dedicated: StartWorkshop item {id} failed to load: {ex}");
			return new InvalidOperationException($"StartWorkshop: Workshop world {id} was saved by {(newer ? "a newer" : "a different")} Space Engineers 2 version than this server's {MpPlugin.CurrentGameVersion} and cannot be read ({serialization.Message})");
		}
		return ex;
	}

	/// <summary>World folders a mod ships: Worlds\&lt;name&gt; with a .container-info, as in GameData\Vanilla\Worlds.</summary>
	internal static string[] ModWorlds(string folder)
	{
		string worlds = Path.Combine(folder, "Worlds");
		return Directory.Exists(worlds)
			? Directory.GetDirectories(worlds).Where(d => File.Exists(Path.Combine(d, ".container-info"))).Select(Path.GetFileName).Order(StringComparer.OrdinalIgnoreCase).ToArray()
			: Array.Empty<string>();
	}

	/// <summary>A world container's metadata file, in the shape SE2 ships in GameData\Vanilla\Worlds\*\.container-info.</summary>
	internal static string ContainerInfoJson(string displayName, long createdTicks) => System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
	{
		["$Bundles"] = new Dictionary<string, string> { ["System.Runtime"] = "1.0.0.0", ["VRage"] = "2.3.0.2219" },
		["$Type"] = "VRage:Keen.VRage.Library.Filesystem.StorageManagers.ContainerInfo`1<VRage:Keen.VRage.Core.Worlds.WorldSessionSaveGameMetaData>",
		["$Value"] = new Dictionary<string, object>
		{
			["Meta"] = new Dictionary<string, object>
			{
				["BaseMetadata"] = null, ["DisplayName"] = displayName, ["Description"] = "", ["GameVersion"] = 0, ["GameBuildNumber"] = 0,
				["SaveGameCreationTimeInTicks"] = createdTicks, ["PCU"] = 0, ["UsedDebugMenu"] = false
			},
			["AdditionalData"] = new Dictionary<string, object>()
		}
	}, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

	// SE2's absolute-path file system is internal; LocalProjectLocator roots project readers on it the same way.
	private static IFileReader FolderReader(string folder)
	{
		Type native = AccessTools.TypeByName("Keen.VRage.Library.Filesystem.NativeFileSystem") ?? throw new MissingMemberException("NativeFileSystem");
		var root = (IFileSystem)AccessTools.Property(typeof(Keen.VRage.Library.Utils.Singleton<>).MakeGenericType(native), "Instance").GetValue(null);
		return new LocalFileSystem(folder, root);
	}

	private static string Fetch(PublishedFileId_t id, long deadline)
	{
		if (Installed(id)) return null;
		if (!SteamGameServerUGC.DownloadItem(id, bHighPriority: true)) return "Steam would not start the download (bad id, or not a Space Engineers 2 item)";
		ulong last = ulong.MaxValue;
		long lastChange = MpClock.NowMs();
		long nextLog = 0;
		while (!Installed(id))
		{
			GameServer.RunCallbacks();
			SteamGameServerUGC.GetItemDownloadInfo(id, out ulong got, out ulong total);
			if (total > MaxItemBytes) return $"{total >> 20} MiB is over the {MaxItemBytes >> 20} MiB limit";
			long now = MpClock.NowMs();
			if (got != last)
			{
				last = got;
				lastChange = now;
			}
			// A bad or hidden id never even reaches the download queue.
			if (now - lastChange > 15000 && got == 0 && SteamGameServerUGC.GetItemState(id) == (uint)EItemState.k_EItemStateNone)
				return "Steam never queued the download (no such public Space Engineers 2 Workshop item?)";
			if (now - lastChange > StallMs) return $"no download progress for {StallMs / 1000} s (private, removed, or not public?)";
			if (now > deadline) return "the 30 minute Workshop download limit ran out";
			if (now >= nextLog)
			{
				nextLog = now + 5000;
				GameLog.Instance.Info($"dedicated: downloading Workshop item {id.m_PublishedFileId}: {got >> 20} of {total >> 20} MiB");
			}
			Thread.Sleep(100);
		}
		return null;
	}

	private static bool Installed(PublishedFileId_t id)
	{
		var state = (EItemState)SteamGameServerUGC.GetItemState(id);
		return (state & EItemState.k_EItemStateInstalled) != 0
			&& (state & (EItemState.k_EItemStateNeedsUpdate | EItemState.k_EItemStateDownloading | EItemState.k_EItemStateDownloadPending)) == 0;
	}

	private static string ProjectFile(string folder) => Directory.EnumerateFiles(folder, "*.vrgproj").FirstOrDefault();

	// Info, not a warning: a missing Workshop item is a configuration matter, not a crash to report.
	private static void Problem(string text)
	{
		lock (Problems) Problems.Add(text);
		GameLog.Instance.Info("dedicated: mods: " + text + "; the world loads without it");
	}

	internal static string Describe()
	{
		string text = "World mods: " + WorldMods.Summary(_loaded);
		lock (Problems)
			if (Problems.Count > 0) text += "\nNot loaded:\n  " + string.Join("\n  ", Problems);
		return text;
	}
}
