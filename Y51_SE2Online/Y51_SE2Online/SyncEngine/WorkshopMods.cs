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
/// the wire carries the plain item id. A player's own game downloads missing mods while it loads, but with a 60 s
/// limit per item, no progress and no question, so the plugin asks first and fetches them while the world streams in.
/// </summary>
internal static class WorkshopMods
{
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

	/// <summary>Returned when the player declined or left; the join is already going away, so it is not an error to show.</summary>
	public const string Cancelled = "The Workshop mod download was cancelled.";

	/// <summary>A download question waiting for the player, shown by <see cref="JoinStatusUi"/>.</summary>
	internal sealed class Prompt
	{
		public string Text { get; init; }

		public System.Threading.Tasks.TaskCompletionSource<bool> Answer { get; } = new(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
	}

	private static volatile Prompt s_pending;

	internal static Prompt Pending => s_pending;

	private static readonly object ConsentLock = new();

	private static ModConsent s_consent;

	private static string ConsentPath => PluginPaths.Data("Y51_SE2Online.modconsent.json");

	/// <summary>
	/// Downloads the host's Workshop mods this game lacks, after asking the player (once per server and mod set), within
	/// the per-item and per-server size caps. Null when all are in, <see cref="Cancelled"/> when declined or left, else why the join cannot continue.
	/// </summary>
	public static async System.Threading.Tasks.Task<string> PrepareAsync(IReadOnlyList<WorldMod> mods, string server, int maxGiB, IMpLog log, Action<string> progress, CancellationToken stop)
	{
		Prompt prompt = null;
		try
		{
			await RefreshAsync(mods.Where(m => m.WorkshopId != 0 && Installed(new PublishedFileId_t(m.WorkshopId))).ToList(), log, progress, stop);
			var installed = new List<string>();
			var missing = new List<WorldMod>();
			foreach (WorldMod mod in mods)
			{
				if (mod.WorkshopId == 0) log.Info($"mods: the host runs local mod {mod.Describe()}; it loads only if this game has the same local mod");
				else if (Installed(new PublishedFileId_t(mod.WorkshopId))) { log.Info($"mods: {mod.Describe()} is already downloaded"); installed.Add(Title(mod.Name, mod)); }
				else missing.Add(mod);
			}
			if (missing.Count == 0) return null;
			progress?.Invoke("Asking Steam about the host's Workshop mods...");
			var (needed, gone) = await DetailsAsync(missing, log, stop);
			if (gone != null) return $"The host's Workshop mod {gone.Describe()} is not available on the Workshop (private or removed).";
			if (ModDownloads.Refusal(needed, maxGiB) is string refusal) return refusal;
			ulong[] ids = missing.Select(m => m.WorkshopId).ToArray();
			if (server != null && Consent(c => c.Covers(server, ids)))
			{
				log.Info($"mods: downloading {needed.Count} Workshop mod(s), {ModDownloads.Size(ModDownloads.TotalBytes(needed))}, already approved for {server}");
			}
			else
			{
				prompt = new Prompt { Text = ModDownloads.Prompt(needed, installed) };
				s_pending = prompt;
				progress?.Invoke("Waiting for you to approve the Workshop download...");
				log.Info($"mods: asking the player to download {needed.Count} Workshop mod(s), {ModDownloads.Size(ModDownloads.TotalBytes(needed))}");
				await WaitAsync(prompt.Answer.Task, int.MaxValue, stop);
				s_pending = null;
				if (!prompt.Answer.Task.Result)
				{
					log.Info("mods: the player declined the Workshop download; leaving the server");
					return Cancelled;
				}
				if (server != null) Consent(c => { c.Approve(server, ids); c.Save(ConsentPath); return true; });
			}
			return await DownloadAsync(needed, ModDownloads.CapBytes(maxGiB), log, progress, stop);
		}
		catch (OperationCanceledException)
		{
			log.Info("mods: the Workshop download stopped (the join ended)");
			return Cancelled;
		}
		finally
		{
			if (prompt != null && s_pending == prompt) s_pending = null;
			progress?.Invoke(null);
		}
	}

	/// <summary>Waits for a task while the join lasts; false on timeout.</summary>
	private static async System.Threading.Tasks.Task<bool> WaitAsync(System.Threading.Tasks.Task task, int timeoutMs, CancellationToken stop)
	{
		long until = timeoutMs == int.MaxValue ? long.MaxValue : MpClock.NowMs() + timeoutMs;
		while (!task.IsCompleted)
		{
			ThrowIfLeft(stop);
			if (MpClock.NowMs() > until) return false;
			await System.Threading.Tasks.Task.WhenAny(task, System.Threading.Tasks.Task.Delay(250, stop));
		}
		return true;
	}

	private static void ThrowIfLeft(CancellationToken stop)
	{
		stop.ThrowIfCancellationRequested();
		// Leaving takes the controller out of joining before the session is torn down, while this may await inside the game's transition.
		if (MpPlugin.Controller is not { Busy: false, Current.Join: true }) throw new OperationCanceledException();
	}

	private static bool Consent(Func<ModConsent, bool> use)
	{
		try
		{
			lock (ConsentLock) return use(s_consent ??= ModConsent.Load(ConsentPath));
		}
		catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
		{
			GameLog.Instance.Warn("mods: could not save the Workshop download approvals: " + ex.Message);
			return false;
		}
	}

	private static string Title(string name, WorldMod mod) => string.IsNullOrWhiteSpace(name) ? mod.WorkshopId.ToString() : name.Trim();

	/// <summary>Steam's title and size for each item; sizes are 0 when Steam does not answer in time. Gone is an item the Workshop no longer has.</summary>
	private static async System.Threading.Tasks.Task<(List<ModDownload> Found, WorldMod Gone)> DetailsAsync(List<WorldMod> mods, IMpLog log, CancellationToken stop)
	{
		var result = mods.Select(m => new ModDownload(m, Title(m.Name, m), 0)).ToList();
		var ids = mods.Select(m => new PublishedFileId_t(m.WorkshopId)).ToArray();
		var done = new System.Threading.Tasks.TaskCompletionSource<bool>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
		UGCQueryHandle_t query = SteamUGC.CreateQueryUGCDetailsRequest(ids, (uint)ids.Length);
		var call = CallResult<SteamUGCQueryCompleted_t>.Create();
		call.Set(SteamUGC.SendQueryUGCRequest(query), (r, ioError) => done.TrySetResult(!ioError && r.m_eResult == EResult.k_EResultOK));
		try
		{
			if (!await WaitAsync(done.Task, 20000, stop) || !done.Task.Result)
			{
				log.Warn("mods: Steam did not return the Workshop details of the host's mods; asking without their sizes");
				return (result, null);
			}
			for (int i = 0; i < ids.Length; i++)
			{
				if (!SteamUGC.GetQueryUGCResult(query, (uint)i, out SteamUGCDetails_t d)) continue;
				int at = mods.FindIndex(m => m.WorkshopId == d.m_nPublishedFileId.m_PublishedFileId);
				if (at < 0) continue;
				if (d.m_eResult is EResult.k_EResultFileNotFound or EResult.k_EResultAccessDenied)
				{
					log.Warn($"mods: the Workshop says {mods[at].Describe()} is {d.m_eResult}");
					return (result, mods[at]);
				}
				if (d.m_eResult != EResult.k_EResultOK) continue;
				ulong bytes = Math.Max(d.m_ulTotalFilesSize, (ulong)(uint)d.m_nFileSize);
				result[at] = new ModDownload(mods[at], Title(d.m_rgchTitle, mods[at]), bytes);
			}
			return (result, null);
		}
		finally
		{
			call.Dispose();
			SteamUGC.ReleaseQueryUGCRequest(query);
		}
	}

	private static async System.Threading.Tasks.Task<string> DownloadAsync(List<ModDownload> needed, ulong capBytes, IMpLog log, Action<string> progress, CancellationToken stop)
	{
		ulong doneBytes = 0;
		for (int i = 0; i < needed.Count; i++)
		{
			WorldMod mod = needed[i].Mod;
			var id = new PublishedFileId_t(mod.WorkshopId);
			if (Installed(id)) continue;
			if (!SteamUGC.DownloadItem(id, bHighPriority: true)) return $"Steam would not download the host's Workshop mod {mod.Describe()}.";
			log.Info($"mods: downloading {mod.Describe()} from the Workshop");
			ulong lastBytes = ulong.MaxValue;
			ulong itemBytes = needed[i].Bytes;
			long lastChange = MpClock.NowMs();
			while (!Installed(id))
			{
				ThrowIfLeft(stop);
				SteamUGC.GetItemDownloadInfo(id, out ulong got, out ulong total);
				if (total > 0) itemBytes = total;
				if (itemBytes > ModDownloads.MaxItemBytes) return $"The host's Workshop mod {mod.Describe()} is {ModDownloads.Size(itemBytes)}; at most {ModDownloads.Size(ModDownloads.MaxItemBytes)} per mod is downloaded.";
				if (doneBytes + itemBytes > capBytes) return $"The host's Workshop mods need more than {ModDownloads.Size(capBytes)}, the most this game downloads per server (MaxModDownloadGiB in Y51_SE2Online.settings.json).";
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
				progress?.Invoke($"Workshop mod {i + 1} of {needed.Count}: {needed[i].Title}  /  {got / 1048576.0:0.0} of {total / 1048576.0:0.0} MiB");
				await System.Threading.Tasks.Task.Delay(250, stop);
			}
			doneBytes += itemBytes;
			log.Info($"mods: {mod.Describe()} downloaded");
		}
		return null;
	}

	/// <summary>
	/// Steam calls a copy on disk current until it has asked the Workshop, so a mod downloaded once stayed at that version
	/// (a 2.4 build after 4.0 broke it) while the server ran the new one. DownloadItem asks, answers at once for a current
	/// copy and downloads an update; updates of mods the player already has are not asked about again.
	/// </summary>
	private static async System.Threading.Tasks.Task RefreshAsync(List<WorldMod> mods, IMpLog log, Action<string> progress, CancellationToken stop)
	{
		if (mods.Count == 0) return;
		var waiting = new HashSet<ulong>();
		using var answered = Callback<DownloadItemResult_t>.Create(r => { lock (waiting) waiting.Remove(r.m_nPublishedFileId.m_PublishedFileId); });
		foreach (WorldMod mod in mods)
		{
			lock (waiting) waiting.Add(mod.WorkshopId);
			if (!SteamUGC.DownloadItem(new PublishedFileId_t(mod.WorkshopId), bHighPriority: true)) lock (waiting) waiting.Remove(mod.WorkshopId);
		}
		progress?.Invoke("Checking the host's Workshop mods for updates...");
		ulong lastBytes = 0;
		long lastChange = MpClock.NowMs();
		while (true)
		{
			ThrowIfLeft(stop);
			ulong[] left;
			lock (waiting) left = waiting.ToArray();
			if (left.Length == 0) break;
			ulong got = 0, total = 0;
			foreach (ulong id in left)
			{
				SteamUGC.GetItemDownloadInfo(new PublishedFileId_t(id), out ulong g, out ulong t);
				got += g;
				total += t;
			}
			long now = MpClock.NowMs();
			if (got != lastBytes) (lastBytes, lastChange) = (got, now);
			else if (now - lastChange > 30000)
			{
				log.Warn($"mods: Steam did not say whether {left.Length} downloaded Workshop mod(s) are current; using the copies on disk");
				return;
			}
			if (total > 0) progress?.Invoke($"Updating the host's Workshop mods  /  {got / 1048576.0:0.0} of {total / 1048576.0:0.0} MiB");
			await System.Threading.Tasks.Task.Delay(250, stop);
		}
		log.Info($"mods: {mods.Count} downloaded Workshop mod(s) are the Workshop's current version");
	}

	private static bool Installed(PublishedFileId_t id)
	{
		var state = (EItemState)SteamUGC.GetItemState(id);
		return (state & EItemState.k_EItemStateInstalled) != 0
			&& (state & (EItemState.k_EItemStateNeedsUpdate | EItemState.k_EItemStateDownloading | EItemState.k_EItemStateDownloadPending)) == 0;
	}
}
