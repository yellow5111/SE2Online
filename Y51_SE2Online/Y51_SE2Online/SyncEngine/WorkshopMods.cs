using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Keen.VRage.Core.Project;
using Keen.VRage.Core.Services;
using Keen.VRage.Core.Services.Data;
using Keen.VRage.Core.Worlds;
using Steamworks;
using Version = System.Version;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// The game names a Workshop mod by its project GUID plus a Steam item id packed into a GUID (VRageProjectID.ModId);
/// the wire carries the plain item id. A joiner's own game downloads missing mods while it loads, but with a 60 s
/// limit per item and no progress, so the plugin fetches them first while the world streams in.
/// </summary>
internal static class WorkshopMods
{
	private const ulong MaxItemBytes = 4UL << 30;

	private const int StallMs = 60000;

#pragma warning disable CS0618 // VRageProjectID.ModId is how the game itself addresses a Workshop project.
	public static List<WorldMod> From(ModList list) =>
		list?.Records == null ? new List<WorldMod>() : list.Records.Select(From).GroupBy(m => m.Project).Select(g => g.First()).ToList();

	public static WorldMod From(ModList.ModRecord r) => new WorldMod(r.Project.Guid,
		r.Project.ServiceId == GameServiceNames.ServiceSteam.String && r.Project.ModId is { } id ? UGCHelper.ULongFromGuid(id.Id) : 0UL,
		r.DisplayName ?? "", r.Version?.ToString() ?? "");

	public static ModList.ModRecord ToRecord(WorldMod m) => new ModList.ModRecord
	{
		Project = m.WorkshopId == 0
			? new VRageProjectID { Guid = m.Project, ServiceId = "" }
			: new VRageProjectID { Guid = m.Project, ServiceId = GameServiceNames.ServiceSteam.String, ModId = new UGCItemId(UGCHelper.GuidFromULong(m.WorkshopId), GameServiceNames.ServiceSteam) },
		Version = m.Version.Length == 0 ? null : Version.Parse(m.Version),
		DisplayName = m.Name
	};
#pragma warning restore CS0618

	public static ModList ToModList(IReadOnlyList<WorldMod> mods) =>
		mods == null || mods.Count == 0 ? null : new ModList { Records = mods.Select(ToRecord).ToArray() };

	/// <summary>Downloads every Workshop mod the host runs without subscribing; null when all are installed, else why the join cannot continue.</summary>
	public static async System.Threading.Tasks.Task<string> DownloadAsync(IReadOnlyList<WorldMod> mods, IMpLog log, Action<string> progress, CancellationToken stop)
	{
		for (int i = 0; i < mods.Count; i++)
		{
			WorldMod mod = mods[i];
			if (mod.WorkshopId == 0)
			{
				log.Info($"mods: the host runs local mod {mod.Describe()}; it loads only if this game has the same local mod");
				continue;
			}
			var id = new PublishedFileId_t(mod.WorkshopId);
			if (Installed(id))
			{
				log.Info($"mods: {mod.Describe()} is already downloaded");
				continue;
			}
			if (!SteamUGC.DownloadItem(id, bHighPriority: true)) return $"Steam would not download the host's Workshop mod {mod.Describe()}.";
			log.Info($"mods: downloading {mod.Describe()} from the Workshop");
			ulong lastBytes = ulong.MaxValue;
			long lastChange = MpClock.NowMs();
			while (!Installed(id))
			{
				stop.ThrowIfCancellationRequested();
				SteamUGC.GetItemDownloadInfo(id, out ulong got, out ulong total);
				if (total > MaxItemBytes) return $"The host's Workshop mod {mod.Describe()} is {total >> 20} MiB; the limit is {MaxItemBytes >> 20} MiB.";
				long now = MpClock.NowMs();
				if (got != lastBytes)
				{
					lastBytes = got;
					lastChange = now;
				}
				else if (now - lastChange > StallMs)
				{
					return $"The host's Workshop mod {mod.Describe()} did not download for {StallMs / 1000} s. It may be private, removed, or not a Space Engineers 2 item.";
				}
				progress?.Invoke($"Workshop mod {i + 1} of {mods.Count}: {mod.Name}  /  {got / 1048576.0:0.0} of {total / 1048576.0:0.0} MiB");
				await System.Threading.Tasks.Task.Delay(250, stop);
			}
			log.Info($"mods: {mod.Describe()} downloaded");
		}
		progress?.Invoke(null);
		return null;
	}

	private static bool Installed(PublishedFileId_t id)
	{
		var state = (EItemState)SteamUGC.GetItemState(id);
		return (state & EItemState.k_EItemStateInstalled) != 0
			&& (state & (EItemState.k_EItemStateNeedsUpdate | EItemState.k_EItemStateDownloading | EItemState.k_EItemStateDownloadPending)) == 0;
	}
}
