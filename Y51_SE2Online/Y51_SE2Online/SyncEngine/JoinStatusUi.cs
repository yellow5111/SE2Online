using System;
using System.Linq;
using Keen.Game2;
using Keen.Game2.Client.UI.Library;
using Keen.Game2.Client.UI.Library.Dialogs.LoadingDialog;
using Keen.Game2.Client.UI.Library.Dialogs.OneOptionDialog;
using Keen.Game2.Client.UI.Overlays;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Localization;
using Keen.VRage.Library.Utils;
using Y51_SE2Online.Core;
using Y51_SE2Online.KeenNetcode;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// Join progress in SE2's own UI: details on the native loading screen, a cancellable native loading dialog while
/// connecting at the menu, and a native OK dialog when a join fails.
/// </summary>
internal sealed class JoinStatusUi
{
	private const int RefreshMs = 250;

	private const int FindSharedEveryMs = 2000;

	private readonly IMpLog _log;

	private SharedUIComponent? _shared;

	private long _nextMs;

	private long _nextFindMs;

	private bool _failed;

	private LoadingTransitionScreenViewModel? _screen;

	private string? _lastDetail;

	private IObservableDisposable? _dialog;

	private string? _dialogText;

	private LoadingDialogViewModel? _download;

	private bool _downloadAbortable;

	private WorkshopMods.Prompt? _prompt;

	private static readonly System.Reflection.MethodInfo NotifyChanged = HarmonyLib.AccessTools.Method(typeof(LoadingDialogViewModel), "OnPropertyChanged", new[] { typeof(string) });

	private string? _noticeKey;

	public long LoadingUpdates;

	public long DialogsShown;

	public bool Failed => _failed;

	public JoinStatusUi(IMpLog log)
	{
		_log = log;
	}

	public void Update(GameAppComponent app, JoinProgress progress, long now)
	{
		if (now >= _nextMs)
		{
			_nextMs = now + RefreshMs;
			// The held screen is let go even after the on-screen progress failed: a hold that is never released is a stuck join.
			SharedUIComponent shared = _failed ? _shared ?? SafeFind(app) : Shared(app, now);
			if (shared != null)
			{
				if (_failed) ReleaseSafely(shared, progress, now);
				else Apply(shared, progress, now);
			}
		}
	}

	private static SharedUIComponent? SafeFind(GameAppComponent app)
	{
		try { return FindShared(app); }
		catch (Exception) { return null; }
	}

	private static void ReleaseSafely(SharedUIComponent shared, JoinProgress progress, long now)
	{
		try { ReleaseHold(shared, progress, now); }
		catch (Exception ex)
		{
			GameLog.Instance.WarnOnce("client-loading-release", $"client: waiting for the world failed ({ex.GetType().Name}: {ex.Message}); the loading screen goes now");
			ForceRelease(shared, now, "the wait failed");
		}
	}

	private static long _holdSinceMs = -1;

	// The join the screen is held for: a hold left from an earlier join (its world reset before the hold was let go) goes at once.
	private static int _holdJoin;

	private static bool _releasing;

	/// <summary>The join's loading screen is being held up by SE2 Online (popups and chat lines wait for it to go).</summary>
	internal static bool ScreenHeld => _holdSinceMs >= 0;

	// The wait for the world around the player once the game's own load ended (Playing), and the join it finished for:
	// the screen is held once per join (a rejoin is a new ClientWorld).
	private static WorldReadiness? _readiness;

	private static int _doneFor = -1;

	// Bisect switch: -y51online:loadwait:off lets the screen go when the game's own load ends, as 0.17.2 did.
	private static readonly bool WaitOff = MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "loadwait:off");

	private static readonly WorldReadiness.Probe[] NotLookedAt = MakeNotLookedAt();

	private static WorldReadiness.Probe[] MakeNotLookedAt()
	{
		var probes = new WorldReadiness.Probe[WorldReadiness.Parts];
		for (int i = 0; i < probes.Length; i++) probes[i] = new WorldReadiness.Probe(false, 0, "waiting for the world to start");
		return probes;
	}

	/// <summary>
	/// Keeps SE2's loading screen up after its own load ends, until the player is in their character: SE2 4.0 downloads
	/// the world after that point. Every close goes through CloseLoadingTransitionScreenIfNeeded (the transition end,
	/// and the initial possession, which used to drop the screen and show the half-loaded world under our dialog).
	/// </summary>
	internal static void Install(HarmonyLib.Harmony harmony)
	{
		harmony.Patch(HarmonyLib.AccessTools.Method(typeof(SharedUIComponent), nameof(SharedUIComponent.CloseLoadingTransitionScreenIfNeeded)),
			prefix: new HarmonyLib.HarmonyMethod(typeof(JoinStatusUi), nameof(ClosePrefix)));
	}

	private static bool ClosePrefix(SharedUIComponent __instance)
	{
		if (_releasing || __instance._loadingTransitionScreen == null || !Holding(ClientWorld.Progress.Stage)) return true;
		if (_holdSinceMs < 0)
		{
			_holdSinceMs = MpClock.NowMs();
			_holdJoin = ClientWorld.Joins;
			_readiness = null;
			GameLog.Instance.Info($"client: loading screen kept up while the world arrives ({ClientWorld.Progress.Stage})");
		}
		return false;
	}

	/// <summary>Whether the loading screen stays up: while the world arrives and the game builds it, then (Playing) until the
	/// world around the player is ready (<see cref="WorldReadiness"/>), once per join.</summary>
	private static bool Holding(JoinStage stage) => MpPlugin.Controller?.Current?.Join == true
		&& (stage is >= JoinStage.Receiving and < JoinStage.Playing || stage == JoinStage.Playing && !WaitOff && ClientWorld.Current != null && ClientWorld.Joins != _doneFor);

	private static void ReleaseHold(SharedUIComponent shared, JoinProgress progress, long now)
	{
		if (_holdSinceMs < 0) return;
		JoinStage stage = progress.Stage;
		string why;
		if (_holdJoin != ClientWorld.Joins) why = "held for an earlier join";
		else if (!Holding(stage)) why = stage == JoinStage.Playing ? "the game's load ended" : stage.ToString();
		else if (KeenRespawn.Menu != null) why = "the spawn menu is up";
		else if (stage < JoinStage.Playing)
		{
			if (now - _holdSinceMs < 180_000) return;
			why = "the game's own load took over 3 min";
		}
		else
		{
			if (_readiness == null)
			{
				_readiness = new WorldReadiness();
				LoadReadiness.Active = true;
				GameLog.Instance.Info($"client: the game's load ended after {(now - _holdSinceMs) / 1000.0:0.0} s; the loading screen waits for the world around the player");
			}
			if (!_readiness.Step(now, LoadReadiness.Probes ?? NotLookedAt)) return;
			why = $"{_readiness.Outcome}; waited {_readiness.WaitedMs(now) / 1000.0:0.0} s after the game's load";
		}
		ForceRelease(shared, now, $"{stage}: {why}");
	}

	private static void ForceRelease(SharedUIComponent shared, long now, string why)
	{
		if (_holdSinceMs < 0) return;
		_releasing = true;
		try { shared.CloseLoadingTransitionScreenIfNeeded(); }
		catch (Exception ex)
		{
			GameLog.Instance.WarnOnce("client-loading-close", $"client: closing the loading screen failed ({ex.GetType().Name}: {ex.Message})");
			// Still up: tried again on the next refresh (the game's own close was already swallowed). Gone: released.
			if (shared._loadingTransitionScreen != null) return;
		}
		finally { _releasing = false; }
		GameLog.Instance.Info($"client: loading screen released after {(now - _holdSinceMs) / 1000.0:0.0} s ({why})");
		_holdSinceMs = -1;
		_readiness = null;
		LoadReadiness.Active = false;
		// Once per join: a later close in this join (released early by a cap or the spawn menu) is not held again.
		if (ClientWorld.Current != null && _holdJoin == ClientWorld.Joins) _doneFor = ClientWorld.Joins;
	}

	/// <summary>What SE2 Online's loading screen shows now (the game's own screen gets the same as text).</summary>
	private static LoadScreenLayout.View? Overlay(JoinProgress progress, long now)
	{
		JoinStage stage = progress.Stage;
		if (stage is < JoinStage.Receiving or > JoinStage.Playing) return null;
		var info = progress.LoadInfo(now);
		string line, detail;
		double readiness = 0;
		if (stage == JoinStage.Playing)
		{
			if (_readiness is not { } wait) return null;
			line = wait.Title;
			detail = wait.Detail ?? "";
			readiness = wait.Fraction;
		}
		else
		{
			line = LoadScreenLayout.StageLine(stage);
			detail = info.What;
		}
		var tip = LoadScreenLayout.Tip(info.Seed, now);
		return new LoadScreenLayout.View(info.Server, info.World, line, detail, LoadScreenLayout.Percent(stage, info.Percent, readiness), tip.Key, tip.Title + ": " + tip.Text);
	}

	internal void Apply(SharedUIComponent shared, JoinProgress progress, long now)
	{
		if (_failed)
		{
			return;
		}
		try
		{
			ReleaseSafely(shared, progress, now);
			var overlay = Overlay(progress, now);
			if (overlay is { } shown) LoadScreenOverlay.Update(shown);
			var native = progress.LoadingScreen(now);
			// Held past the game's own load: its screen (behind SE2 Online's, or alone if that could not be made) says what is awaited.
			if (native == null && overlay is { } waiting && progress.Stage == JoinStage.Playing)
			{
				int filled = (int)Math.Round(Math.Clamp(waiting.Percent, 0, 100) / 5);
				native = (string.IsNullOrEmpty(waiting.World) ? waiting.Server : waiting.World,
					$"{waiting.Stage}\n[{new string('|', filled)}{new string('.', 20 - filled)}]   {(int)waiting.Percent}%\n{waiting.Detail}\n\nTip: {waiting.TipKey} {waiting.TipText}", $"{(int)waiting.Percent}%");
			}
			bool loading = UpdateLoading(shared, native);
			// Cancelling leaves the join; never reopen a dialog for a session that is going away.
			bool joining = MpPlugin.Controller?.Current?.Join == true;
			if (joining && WorkshopMods.Pending is { Answer.Task.IsCompleted: false } ask)
			{
				ShowModPrompt(shared, ask);
				return;
			}
			UpdateDialog(shared, loading || !joining ? default : progress.Dialog(now), progress.ModsActive);
		}
		catch (Exception ex)
		{
			_failed = true;
			CloseDialog();
			_log.Warn($"client: showing the join progress on screen failed and is switched off ({ex.GetType().Name}: {ex.Message}); the log still has every step");
		}
	}

	private SharedUIComponent? Shared(GameAppComponent app, long now)
	{
		try
		{
			if (_shared != null || now < _nextFindMs) return _shared;
			_nextFindMs = now + FindSharedEveryMs;
			return _shared = FindShared(app);
		}
		catch (Exception ex)
		{
			_failed = true;
			_log.Warn($"client: finding the game's UI for the join progress failed ({ex.GetType().Name}: {ex.Message}); the log still has every step");
			return null;
		}
	}

	internal static SharedUIComponent? FindShared(GameAppComponent app)
	{
		return app.Scene.Entities.Select((Entity x) => x.TryGet<SharedUIComponent>()).FirstOrDefault((SharedUIComponent x) => x != null);
	}

	/// <summary>Returns true while the native loading screen is up.</summary>
	private bool UpdateLoading(SharedUIComponent shared, (string Title, string Detail, string Status)? view)
	{
		LoadingTransitionScreenViewModel screen = shared._loadingTransitionScreenViewModel;
		if (screen != _screen)
		{
			_screen = screen;
			_lastDetail = null;
		}
		if (screen == null || view == null)
		{
			return screen != null;
		}
		var (title, detail, status) = view.Value;
		if (detail == _lastDetail)
		{
			return true;
		}
		_lastDetail = detail;
		screen.Name = new LocalizableText(JoinProgress.Sanitize(title, 60));
		screen.FrontText = OnlineUi.Raw(JoinProgress.Sanitize(detail, 400));
		screen.LoadingText = OnlineUi.Raw(JoinProgress.Sanitize(status, 20));
		if (LoadingUpdates++ == 0) _log.Info($"client: join progress shown on the loading screen ({title}: {detail.Split('\n')[0]})");
		return true;
	}

	private void UpdateDialog(SharedUIComponent shared, (JoinProgress.DialogKind Kind, string Title, string Text, string Key) view, bool modsActive)
	{
		if (view.Kind == JoinProgress.DialogKind.Download)
		{
			ShowDownload(shared, view.Title, view.Text, modsActive);
			return;
		}
		if (view.Kind != JoinProgress.DialogKind.Progress)
		{
			CloseDialog();
		}
		if (view.Kind == JoinProgress.DialogKind.Retry)
		{
			if (view.Key == _noticeKey)
			{
				return;
			}
			_noticeKey = view.Key;
			shared.ShowDialog(new Keen.Game2.Client.UI.Library.Dialogs.TwoOptionsDialog.TwoOptionsDialogViewModel(new Keen.Game2.Client.UI.Library.Dialogs.TwoOptionsDialog.TwoOptionsDialogDefinition
			{
				Title = OnlineUi.Raw(view.Title),
				Content = OnlineUi.Raw(JoinProgress.Sanitize(view.Text, 400)),
				ConfirmOption = OnlineUi.Raw("Retry"),
				CancelOption = OnlineUi.Raw("Close")
			}) { ConfirmAction = MpMenu.Retry });
			DialogsShown++;
		}
		else if (view.Kind == JoinProgress.DialogKind.Notice)
		{
			if (view.Key == _noticeKey)
			{
				return;
			}
			_noticeKey = view.Key;
			shared.ShowDialog(new OneOptionDialogViewModel(new OneOptionDialogDefinition
			{
				Title = OnlineUi.Raw(view.Title),
				Content = OnlineUi.Raw(JoinProgress.Sanitize(view.Text, 400)),
				ConfirmOption = OnlineUi.Raw("OK")
			}));
			DialogsShown++;
		}
		else if (view.Kind == JoinProgress.DialogKind.Progress)
		{
			string text = view.Title + "\n" + view.Text;
			if (_dialog != null && text == _dialogText)
			{
				return;
			}
			CloseDialog();
			_dialogText = text;
			_dialog = shared.ShowDialog(new LoadingDialogViewModel(new LoadingDialogDefinition
			{
				Title = OnlineUi.Raw(view.Title),
				Content = OnlineUi.Raw(JoinProgress.Sanitize(view.Text, 400)),
				AllowAbort = true,
				AbortOption = OnlineUi.Raw("Cancel")
			})
			{
				AbortAction = MpMenu.Leave
			});
			IObservableDisposable dialog = _dialog;
			dialog.OnDisposed += _ =>
			{
				if (_dialog == dialog)
				{
					_dialog = null;
				}
			};
			DialogsShown++;
		}
	}

	/// <summary>
	/// The world download at the menu: a native modal dialog, so nothing else can be clicked until the loading screen
	/// takes over. It can be cancelled only while the host's Workshop mods download. Its text changes in place instead of the dialog being reopened.
	/// </summary>
	private void ShowDownload(SharedUIComponent shared, string title, string text, bool abortable)
	{
		text = JoinProgress.Sanitize(text, 400);
		if (_download != null && _dialog != null && _downloadAbortable == abortable)
		{
			if (text == _dialogText) return;
			_dialogText = text;
			((LoadingDialogDefinition)_download.GetDefinition()).Content = OnlineUi.Raw(text);
			NotifyChanged.Invoke(_download, new object[] { nameof(LoadingDialogViewModel.Content) });
			return;
		}
		CloseDialog();
		_dialogText = text;
		_downloadAbortable = abortable;
		_download = new LoadingDialogViewModel(new LoadingDialogDefinition
		{
			Title = OnlineUi.Raw(JoinProgress.Sanitize(title, 80)),
			Content = OnlineUi.Raw(text),
			AllowAbort = abortable,
			AbortOption = OnlineUi.Raw("Cancel")
		}) { AbortAction = abortable ? MpMenu.Leave : null };
		_dialog = shared.ShowDialog(_download);
		IObservableDisposable dialog = _dialog;
		dialog.OnDisposed += _ =>
		{
			if (_dialog == dialog)
			{
				_dialog = null;
				_download = null;
			}
		};
		DialogsShown++;
	}

	/// <summary>Asks before the host's Workshop mods download; Cancel (or Escape) leaves the server.</summary>
	private void ShowModPrompt(SharedUIComponent shared, WorkshopMods.Prompt ask)
	{
		if (ask == _prompt && _dialog != null) return;
		CloseDialog();
		_prompt = ask;
		_dialog = shared.ShowDialog(new Keen.Game2.Client.UI.Library.Dialogs.TwoOptionsDialog.TwoOptionsDialogViewModel(new Keen.Game2.Client.UI.Library.Dialogs.TwoOptionsDialog.TwoOptionsDialogDefinition
		{
			Title = OnlineUi.Raw("Download Workshop mods"),
			Content = OnlineUi.Raw(JoinProgress.Sanitize(ask.Text, 1200)),
			ConfirmOption = OnlineUi.Raw("Download and join"),
			CancelOption = OnlineUi.Raw("Cancel")
		})
		{
			ConfirmAction = () => ask.Answer.TrySetResult(true),
			CancelAction = () => { if (ask.Answer.TrySetResult(false)) MpMenu.Leave(); }
		});
		IObservableDisposable dialog = _dialog;
		dialog.OnDisposed += _ =>
		{
			if (_dialog == dialog) _dialog = null;
		};
		DialogsShown++;
	}

	private void CloseDialog()
	{
		IObservableDisposable dialog = _dialog;
		_dialog = null;
		_dialogText = null;
		_download = null;
		try
		{
			dialog?.Dispose();
		}
		catch (Exception ex)
		{
			_log.WarnOnce("client-dialog-dispose", "client: closing the join dialog threw " + ex.GetType().Name + ": " + ex.Message);
		}
	}

	public string Describe()
	{
		if (!_failed)
		{
			return $"on-screen progress: loading screen {LoadingUpdates} updates, {DialogsShown} native dialogs";
		}
		return "on-screen progress off (failed)";
	}
}
