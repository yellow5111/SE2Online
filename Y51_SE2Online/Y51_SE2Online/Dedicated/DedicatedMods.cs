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

	private static readonly List<string> Problems = new List<string>();

	private static IReadOnlyList<WorldMod> _loaded = Array.Empty<WorldMod>();

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
		if (world.Count == 0 && config.WorkshopIds.Length == 0) return configured;
		ulong[] wanted = config.WorkshopIds.Concat(world.Where(m => m.WorkshopId != 0).Select(m => m.WorkshopId)).Distinct().ToArray();
		Dictionary<ulong, WorldMod> got = await System.Threading.Tasks.Task.Run(() => Download(config, wanted));
		var added = config.WorkshopIds.Where(got.ContainsKey).Select(id => got[id]);
		var kept = world.Where(m => m.WorkshopId == 0 || got.ContainsKey(m.WorkshopId));
		_loaded = WorldMods.Merge(kept, added);
		GameLog.Instance.Info("dedicated: world mods: " + WorldMods.Summary(_loaded));
		return WorkshopMods.ToModList(_loaded);
	}

	private static Dictionary<ulong, WorldMod> Download(DedicatedConfig config, ulong[] ids)
	{
		var result = new Dictionary<ulong, WorldMod>();
		lock (Problems) Problems.Clear();
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
			return result;
		}
		if (!SteamGameServerUGC.BInitWorkshopForGameServer(new DepotId_t(WorldMods.SteamAppId), root))
		{
			Problem("Steam refused the Workshop folder " + root);
			return result;
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
				string file = ProjectFile(folder);
				VRageProject project = file == null ? null : LocalProjectLocator.GetProject(new FileInfo(file));
				if (project == null || project.Exception != null || project.Data.Type != ProjectType.Mod)
				{
					Problem($"Workshop item {id}: not a Space Engineers 2 mod (no valid .vrgproj of type Mod)");
					continue;
				}
				Folders[id] = folder;
				result[id] = new WorldMod(project.Data.ProjectID.Guid, id, project.Data.Name ?? "", project.Data.Version?.ToString() ?? "");
				GameLog.Instance.Info($"dedicated: Workshop mod {result[id].Describe()} ready in {folder}");
			}
			catch (Exception ex)
			{
				Problem($"Workshop item {id}: {ex.GetType().Name}: {ex.Message}");
			}
		}
		return result;
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
