#if !PULSAR
using System;
using System.Linq;
using Keen.Game2;
using Keen.Game2.Simulation.RuntimeSystems;
using Keen.VRage.Library.Filesystem.StorageManagers;
using Y51_SE2Online.Core;
using Y51_SE2Online.SyncEngine;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>
/// Test rig only: `-y51online:uishot` screenshots the Host and Direct connect pages into the profile's Temp folder;
/// with `-y51online:autohost:&lt;save folder&gt;` it then hosts that save and screenshots the live Host page.
/// </summary>
internal static class UiShots
{
	private static int _step = MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "uishot") ? 0 : -1;

	private static long _atMs;

	private static string? Save => MpPlugin.LaunchArgs.FirstOrDefault(a => a.StartsWith(MpOptions.Prefix + "autohost:", StringComparison.OrdinalIgnoreCase))?[(MpOptions.Prefix + "autohost:").Length..];

	private static int _rulesStep = MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "uishot-rules") ? 0 : -1;

	private static long _rulesAtMs;

	// `-y51online:uishot-rules`: once a world runs, screenshots the Rules page, then a search for the jetpack.
	private static void RulesTick(GameAppComponent app)
	{
		long now = MpClock.NowMs();
		if (_rulesStep < 0 || app._isTransitioning.IsSet || !MpMenu.HasWorld) return;
		if (_rulesAtMs == 0) { _rulesAtMs = now + 30000; return; }
		if (now < _rulesAtMs) return;
		switch (_rulesStep++)
		{
		case 0: MpMenu.Open(); _rulesAtMs = now + 3000; break;
		case 1: Page(5); _rulesAtMs = now + 15000; break;
		case 2: Shot("y51-rules"); _rulesAtMs = now + 3000; break;
		case 3: Avalonia.Threading.Dispatcher.UIThread.Post(() => OnlineScreen.Last?.SearchRules("jetpack")); _rulesAtMs = now + 4000; break;
		case 4: Shot("y51-rules-jetpack"); _rulesAtMs = now + 3000; break;
		case 5: Avalonia.Threading.Dispatcher.UIThread.Post(() => OnlineScreen.Last?.SearchRules("inventory maxmass")); _rulesAtMs = now + 4000; break;
		case 6: Shot("y51-rules-inventory"); GameLog.Instance.Info("uishot: rules done"); _rulesStep = -1; break;
		}
	}

	public static void Tick(GameAppComponent app)
	{
		RulesTick(app);
		if (_step < 0) return;
		long now = MpClock.NowMs();
		if (_atMs == 0) _atMs = now + 15000;
		if (now < _atMs || app._isTransitioning.IsSet) return;
		var log = GameLog.Instance;
		switch (_step)
		{
		case 0:
			MpMenu.Open();
			Next(now, 3000);
			break;
		case 1:
			Page(3);
			Next(now, 2000);
			break;
		case 2:
			Shot("y51-host-idle");
			Page(2);
			Next(now, 2000);
			break;
		case 3:
			Shot("y51-direct");
			if (Save == null) { _step = -1; log.Info("uishot: done"); return; }
			MpMenu.Activate(MpMenu.Settings.HostOptions(false));
			Next(now, 3000);
			break;
		case 4:
			if (MpPlugin.Controller?.Current?.Host != true) return;
			OnlineScreen.Last?.Close();
			log.Info($"uishot: hosting the save '{Save}'");
			_ = app.LoadGameAsync(new WorldLoadArgs { SaveId = new ContainerId(Save!), SkipDebugMenuWarning = true, SkipNewerSaveWarning = true });
			Next(now, 5000);
			break;
		case 5:
			if (!MpMenu.HasWorld || KeenNet.JoinCode == null) return;
			Next(now, 8000);
			break;
		case 6:
			MpMenu.Open();
			Next(now, 3000);
			break;
		case 7:
			Page(3);
			Next(now, 2000);
			break;
		case 8:
			Shot("y51-host-live");
			Avalonia.Threading.Dispatcher.UIThread.Post(() => OnlineScreen.Last?.ScrollHost(1));
			Next(now, 2000);
			break;
		case 9:
			Shot("y51-host-live-bottom");
			log.Info($"uishot: done (join code {KeenNet.JoinCode?.ToDisplayString()})");
			_step = -1;
			break;
		}
	}

	private static void Next(long now, int delayMs)
	{
		_step++;
		_atMs = now + delayMs;
	}

	private static void Page(int index) => Avalonia.Threading.Dispatcher.UIThread.Post(() => OnlineScreen.Last?.SelectPage(index));

	private static void Shot(string name)
	{
		new Keen.VRage.Render.Contracts.MainRenderTarget().TakeScreenshotAsync(Keen.VRage.Library.Filesystem.FileSystem.Temp.GetFileHandleWritable(name + ".png"));
		GameLog.Instance.Info("uishot: " + name);
	}
}
#endif
