using System;
using System.Collections.Generic;
using Keen.VRage.Core.Services.GameSessions;
using Y51_SE2Online.Core;
using Y51_SE2Online.KeenNetcode;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// The joining side on SE2 4.0: Keen's netcode loads and replicates the host's world. This starts Keen's network
/// client game for the chosen session on the main thread, tracks join progress for the on-screen status, and carries
/// the SE2 Online session (chat, players, admin, social) that rides the same connection.
/// </summary>
internal sealed class ClientWorld
{
	private static JoinStatusUi? s_ui;

	private readonly IMpLog _log;

	private readonly MpOptions _o;

	private ClientSession? _session;

	private volatile GameSessionID? _pendingJoin;

	private Keen.VRage.Library.Threading.Task? _loading;

	private System.Threading.Tasks.Task<(string? Error, List<WorldMod>? Mods)>? _preparing;

	private GameSessionID? _preparedFor;

	// The server's mod list could not be read for the join under way, so it loads with this game's own mods.
	private bool _modsUnread;

	private readonly System.Threading.CancellationTokenSource _stop = new();

	/// <summary>Set while this plugin itself calls into Keen's join (its prefix lets that one through).</summary>
	[ThreadStatic] internal static bool Joining;

	private string? _lostReason;

	private long _lostAtMs;

	private long _rejoinDueMs, _rejoinTryMs;

	private long _playingSinceMs;

	// Automatic rejoins after lost connections (Sync/RejoinPolicy.cs). A loss that came back on every join reloaded a big
	// world again and again until the game crashed (F20). Static: every rejoin is a new ClientWorld.
	private static readonly RejoinPolicy s_rejoin = new();

	private bool _left;

	private bool _retired;

	private JoinStage _loggedStage;

	public static ClientWorld? Current { get; private set; }

	/// <summary>Joins started in this game run (a rejoin counts): what the loading screen's once-per-join wait keys on.</summary>
	internal static int Joins;

	public static JoinProgress Progress { get; } = new JoinProgress();

	public static event Action<string>? RejoinRequested;

	internal ClientSession? BoundSession => _session;

	public IReadOnlyList<PlayerInfo> SessionPlayers => _session?.Players ?? Array.Empty<PlayerInfo>();

	public bool IdleAfterJoin => !_retired && (_left || _lostReason != null || Progress.Stage is JoinStage.Rejected or JoinStage.GaveUp or JoinStage.Failed);

	private ClientWorld(IMpLog log, MpOptions o)
	{
		_log = log;
		_o = o;
	}

	public static ClientWorld Install(IMpLog log, MpOptions o)
	{
		if (Current == null)
		{
			// Only an automatic rejoin keeps counting; a join the player started (or Retry) begins again at zero.
			s_rejoin.JoinStarting();
			s_orphanJoinEnded = false;
			Progress.SetRejoin(o.Rejoin && !o.KeenAttached, o.Rejoin && !o.KeenAttached);
			Joins++;
			s_ui = new JoinStatusUi(log);
			Current = new ClientWorld(log, o);
		}
		return Current;
	}

	public static void Reset()
	{
		s_orphanJoinEnded = false;
		// Never keep pinning a character or probing a world the player is leaving (a hold not let go yet ends with it).
		KeenNetcode.LoadReadiness.Active = false;
		if (Current != null)
		{
			Current._retired = true;
			Current._stop.Cancel();
			Current._session?.Detach();
			// Back at the main menu nothing may keep the last world reachable into the next load: these statics can hold
			// its session or entities until the next world replaces them, and a rejoin then loads a big world while the old
			// one is still in memory.
			if (MpPlugin.App is not { } app || app.Client == null && app.Server == null)
			{
				// Each on its own: one that throws must not keep the others, or leaving the mode, from happening.
				foreach (var (what, reset) in new (string, Action)[]
				{
					("client clock", ClientClock.Reset), ("animation timing", AnimPlayout.Reset), ("motion smoothing", MotionSmoothing.Reset),
					("own motion", KeenNetcode.OwnMotion.ResetClient), ("replication transport", Y51_SE2Online.Engine.Y51Replication.ResetClient),
					("flora colliders", FloraPhysicsRecovery.Reset),
					("loading screen wait", KeenNetcode.LoadReadiness.Reset),
				})
				{
					try { reset(); }
					catch (Exception ex) { GameLog.Instance.Warn($"client: clearing the last world's {what} failed ({ex.GetType().Name}: {ex.Message})"); }
				}
				GameLog.Instance.Info($"client: left the server's world ({MemoryInUse(collect: true)})");
			}
		}
		Current = null;
		// Water sync belongs to the session that turned it on (the next world, or single player, starts without it).
		WaterGridsMessage.Enabled = false;
		WaterMirror.Reset();
		Progress.Set(JoinStage.Idle, MpClock.NowMs());
	}

	public void Bind(ClientSession session) => _session = session;

	private SessionIds? _ids;

	private Keen.VRage.Core.Game.Systems.Session? _idsFor;

	/// <summary>The id the host's messages use for this replicated entity (Keen's network id), or null if not replicated.</summary>
	internal uint? NetIdOf(Keen.VRage.DCS.Components.Entity entity) => Ids?.TryGet(entity, out uint id) == true ? id : null;

	internal Keen.VRage.DCS.Components.Entity? EntityOf(uint netId) => Ids?.TryGet(netId, out var entity) == true ? entity : null;

	private SessionIds? Ids
	{
		get
		{
			var session = MpPlugin.App?.ClientSession;
			if (session == null) return null;
			if (!ReferenceEquals(session, _idsFor)) (_ids, _idsFor) = (new SessionIds(session, server: false), session);
			return _ids;
		}
	}

	public void CaptureMainQueue(string where) { }

	public void NetTick(long now) { }

	public void AskRejoin(string why) => RejoinRequested?.Invoke(why);

	/// <summary>Join this Keen session (a Steam lobby or a dedicated server's address); called from any thread.</summary>
	public void Join(GameSessionID session)
	{
		_log.Info(session is OwnSessionID ? $"client: building the world of server {session}" : $"client: joining {session} with the game's own netcode");
		_lostReason = null;
		System.Threading.Volatile.Write(ref _lossReported, null);
		System.Threading.Volatile.Write(ref _joinEnded, null);
		_lossSeen = false;
		_left = false;
		_leftAfterReject = false;
		_pendingJoin = session;
		Progress.Set(JoinStage.Connecting, MpClock.NowMs());
	}

	/// <summary>The join never reached a host (net thread).</summary>
	public void FailJoin(string why)
	{
		_log.Warn("client: " + why);
		Progress.Set(JoinStage.Failed, MpClock.NowMs(), why);
	}

	/// <summary>Net thread: only records the loss (the first one counts); MainTick decides what it does.</summary>
	public static void ConnectionLost(ClientSession session, string reason)
	{
		var current = Current;
		if (current == null || current._session != session) return;
		System.Threading.Interlocked.CompareExchange(ref current._lossReported, string.IsNullOrWhiteSpace(reason) ? "connection closed" : reason, null);
	}

	/// <summary>Main thread, every frame (GameAppComponent.GameUpdate prefix).</summary>
	private bool _backToMenu;

	// Written by other threads, read and acted on only in MainTick: a loss, a failed load and a late load are decided one
	// after another on the main thread instead of racing (the net thread's loss, the game's ended join).
	private string? _lossReported, _joinEnded;

	private bool _lossSeen;

	// An automatic rejoin was handed to the session controller and this world is still the current one.
	private bool _rejoinGone;

	// The connect timeout already left the world; going back to the menu needs no second Disconnect.
	private bool _menuAsked;

	private bool _leftAfterReject;


	private long _barLogMs;

	private long _joinBytesAtStart;

	private long _connectingSinceMs;

	private const long ConnectTimeoutMs = 60000;

	private bool _ownJoinStarted;

	public void MainTick(Keen.Game2.GameAppComponent app)
	{
		long now = MpClock.NowMs();
		// SE2 Online's own netcode: the server accepted this player on SE2 Online's link; the game builds its world now.
		if (!_ownJoinStarted && !_left && _session is { CurrentPhase: ClientSession.Phase.Accepted, Accept: not null, Host: { } host })
		{
			_ownJoinStarted = true;
			Join(new OwnSessionID(host.Value, _o.JoinIp?.ToString() ?? host.Value.ToString()));
		}
		if (_pendingJoin is { } join && _preparing == null)
		{
			_preparedFor = join;
			_preparing = PrepareMods(join);
		}
		if (_preparing is { IsCompleted: true } prepared && _preparedFor is { } target && !app._isTransitioning.IsSet)
		{
			_preparing = null;
			_pendingJoin = null;
			var (error, mods) = prepared.IsCompletedSuccessfully ? prepared.Result
				: ("Could not read the server's mod list (" + prepared.Exception?.GetBaseException().Message + ").", null);
			if (error == WorkshopMods.Cancelled) Progress.Set(JoinStage.Left, now, error);
			else if (error != null) Progress.Set(JoinStage.Failed, now, error);
			else
			{
				_modsUnread = mods == null;
				// Without this the client keeps whatever mods the last single-player world loaded.
				app.GameData.SetRequiredProjects(WorkshopMods.ToModList(mods ?? new List<WorldMod>()));
				Joining = true;
				// Timed from here: a Workshop mod download before it can take minutes.
				_connectingSinceMs = now;
				try { _loading = app.LoadNetworkClientAsync(target); }
				finally { Joining = false; }
			}
		}
		if (_loading is { IsCompleted: true } loading)
		{
			_loading = null;
			try
			{
				loading.GetAwaiter().GetResult();
				string? lost = System.Threading.Volatile.Read(ref _lossReported);
				if (Progress.Stage < JoinStage.Loading) Progress.Set(JoinStage.Loading, now);
				// The connect timeout can call a join failed (or a rejoin lost) whose load then finishes: the world is up, so
				// it is a join again, unless its connection was lost meanwhile.
				else if (app.Client != null && RejoinPolicy.AfterLateLoad(Progress.Stage, lost != null) is { } late)
				{
					_backToMenu = false;
					if (late == JoinStage.Lost)
					{
						_lossSeen = true;
						SetLost(lost!, now);
					}
					else
					{
						_lostAtMs = 0;
						_rejoinDueMs = 0;
						Progress.Set(JoinStage.Loading, now);
					}
				}
			}
			catch (Exception ex)
			{
				string? refusal = Refusal();
				string why = refusal
					?? (_modsUnread && ex.Message.Contains("Cannot locate value for definition")
						? "This server uses a mod your game did not load: its mod list could not be read before joining. Press Retry to read it again."
						: "Joining failed: " + ex.Message);
				_log.Warn("client: Keen join ended: " + ex.Message);
				// The game unloads the main menu before it loads the host's world; a join that fails after that left the
				// player on a frozen 0% loading screen.
				_backToMenu = true;
				Decide(JoinEvent.LoadFault, now, app.Client != null, refusal != null, why, "the rejoin failed: " + ex.GetBaseException().Message);
			}
		}
		if (System.Threading.Interlocked.Exchange(ref _joinEnded, null) is { } ended)
		{
			_backToMenu = true;
			string? refusal = Refusal();
			Decide(JoinEvent.JoinEnded, now, app.Client != null, refusal != null, refusal ?? "Joining failed: " + ended, "the rejoin failed: " + ended);
		}
		if (!_lossSeen && System.Threading.Volatile.Read(ref _lossReported) is { } loss)
		{
			_lossSeen = true;
			string? refusal = Refusal();
			Decide(JoinEvent.Loss, now, app.Client != null, refusal != null, refusal ?? loss, loss);
		}
		// The session change of an automatic rejoin failed (the old session never closed): networking is stopped, so the
		// player decides; the rejoin never started and does not count.
		if (_rejoinGone && MpPlugin.Controller?.State == OnlineSessionState.Failed)
		{
			_rejoinGone = false;
			s_rejoin.RejoinFailed();
			_log.Warn("client: the automatic rejoin could not start: " + MpPlugin.Controller.Error);
			Progress.Set(JoinStage.Failed, now, "Rejoining failed: " + MpPlugin.Controller.Error + " Press Retry to join again.");
		}
		// The server dropped this player (a long server hitch outlasts the ping timeout): its game session cannot resume, so
		// leave the world and join again, as the loss notice says. Nothing did, and the player sat on "Rejoining..." forever.
		if (_lostAtMs > 0 && RejoinsByItself && Progress.Stage == JoinStage.Lost)
		{
			if (_rejoinDueMs == 0) ScheduleRejoin(now);
			// Not while the game is still loading or unloading a world: stopping SE2 Online's session in the middle of the
			// game's own join made the server refuse it, and that refusal closed the game (F20).
			else if (now >= _rejoinTryMs && RejoinPolicy.Next(now, _rejoinDueMs, app._isTransitioning.IsSet) is var step && step != RejoinStep.Wait)
			{
				if (step == RejoinStep.GiveUp)
				{
					_lostAtMs = 0;
					_log.Warn($"client: the game's world transition did not end within {RejoinPolicy.TransitionWaitMs / 1000} s after the connection was lost; not rejoining by itself");
					Progress.Set(JoinStage.Failed, now, $"{_lostReason ?? "connection closed"}\nThe game did not finish loading or leaving the world, so it cannot rejoin by itself. Press Retry, or leave and join again.");
				}
				else
				{
					// The menu drops a retry it cannot run now (a session still busy): tried again shortly, not given up.
					_rejoinTryMs = now + 2000;
					var world = this;
					var log = _log;
					// Marked when the rejoin really goes (the menu drops a retry it cannot run now); counted when its join starts.
					MpMenu.RetryThen(() =>
					{
						world._lostAtMs = 0;
						world._rejoinGone = true;
						s_rejoin.Rejoining();
						log.Info($"client: the connection to the server is lost; leaving its world and joining again (rejoin {s_rejoin.Rejoins + 1} of {RejoinPolicy.Attempts}, {MemoryInUse(collect: false)})");
					});
				}
			}
		}
		if (Progress.Stage == JoinStage.Playing && _playingSinceMs == 0)
		{
			_playingSinceMs = now;
			_log.Info($"client: in the server's world{(s_rejoin.Rejoins > 0 ? $" again after rejoin {s_rejoin.Rejoins}" : "")} ({MemoryInUse(collect: false)})");
		}
		if (Progress.Stage == JoinStage.Playing && _playingSinceMs > 0 && s_rejoin.Stable(now - _playingSinceMs))
			_log.Info($"client: in the world for {RejoinPolicy.StableMs / 60000} min without a loss; automatic rejoins start again at zero");
		if (_backToMenu && !app._isTransitioning.IsSet && app.Client == null && app.Server == null)
		{
			_backToMenu = false;
			if (!_menuAsked)
			{
				_log.Info("client: join failed; back to the main menu");
				app.Disconnect();
			}
		}
		// The server ended this player's session while in its world (kicked, banned, refused after a settings change): leave
		// the world the way the Leave button does, which also stops SE2 Online's link from dialling the server again. The
		// kick/ban notice shows at the main menu (AdminAccess.Tick).
		if (Progress.Stage == JoinStage.Rejected && !_leftAfterReject && app.Client != null && !app._isTransitioning.IsSet)
		{
			_leftAfterReject = true;
			_log.Info("client: the server ended this session (" + (_session?.RejectReason ?? "refused") + "); leaving its world");
			MpPlugin.Controller?.WorldExiting(now);
			app.Disconnect();
		}
		// Nothing answered (a server still starting, offline or firewalled): the join used to wait on the loading screen forever.
		if (Progress.Stage == JoinStage.Connecting && _session?.Accept == null && _connectingSinceMs > 0 && now - _connectingSinceMs > ConnectTimeoutMs)
		{
			_connectingSinceMs = 0;
			string timeout = $"No answer from the server within {ConnectTimeoutMs / 1000} s. It may still be starting, be offline, or be blocked by a firewall.";
			_log.Warn("client: " + timeout);
			// Set either way: a loss reported after this keeps the timeout's reason.
			_backToMenu = true;
			Decide(JoinEvent.Timeout, now, app.Client != null, refused: false, timeout, $"the rejoin failed: no answer from the server within {ConnectTimeoutMs / 1000} s");
			if (!app._isTransitioning.IsSet && (app.Client != null || app.Server != null))
			{
				_menuAsked = true;
				app.Disconnect();
			}
		}
		// Keen loads the host's world itself: the bar follows the bytes it receives, then Keen building the world (its
		// session counts as loaded after the first full tick), then the player's character arriving.
		if (_session?.Accept != null && Progress.Stage is JoinStage.Connecting or JoinStage.Receiving or JoinStage.Loading)
		{
			if (Progress.Stage == JoinStage.Connecting) Progress.Set(JoinStage.Receiving, now);
			if (app.Client != null && Progress.Stage == JoinStage.Receiving) Progress.Set(JoinStage.Loading, now);
		}
		// Keen's session is loaded: the player is in, with a body or at Keen's own respawn screen (a new survival player),
		// which a "waiting for your character" overlay would only cover.
		if (Progress.Stage is JoinStage.Loading or JoinStage.WaitingForCharacter && app.Client != null && !app._isTransitioning.IsSet && app._sessionLoadingFinished == null)
			Progress.Set(JoinStage.Playing, now);
		if (Progress.Stage != _loggedStage)
		{
			_loggedStage = Progress.Stage;
			var text = Progress.Describe(now);
			if (_loggedStage != JoinStage.Idle) _log.Info($"client: join progress ({_loggedStage}): {text.Toast ?? text.Loading ?? "-"}");
		}
#if !PULSAR
		if (SelfTest.Requested && now >= _barLogMs && Progress.LoadingScreen(now) is { } bar)
		{
			_barLogMs = now + 2000;
			_log.Info($"selftest: join bar {Progress.Stage} {bar.Status}: {bar.Detail.Split('\n')[2]}");
		}
#endif
		if (Progress.Stage == JoinStage.Playing) ClientClock.Tick(app.ClientSession, now);
		s_ui?.Update(app, Progress, now);
		UpdateNametags();
#if !PULSAR
		if (_session != null && Progress.Stage == JoinStage.Playing) SelfTest.Client(_session, _log);
		if (Progress.Stage == JoinStage.Playing) SelfTest.Leave(app, _log);
#endif
	}

	/// <summary>
	/// The game's own join, started without anyone awaiting it (a Steam invite, a rejoin), ends when the server refuses
	/// the connection; the game's monitor for such tasks rethrows that into the crash handler and the game closes (F20,
	/// a refusal in the middle of an automatic rejoin). A refused join goes back to the main menu with its reason instead.
	/// </summary>
	internal static void InstallJoinGuard(HarmonyLib.Harmony harmony)
	{
		var monitor = HarmonyLib.AccessTools.TypeByName("Keen.VRage.Library.Threading.SkipWaitMonitor") ?? throw new MissingMemberException("SkipWaitMonitor");
		harmony.Patch(HarmonyLib.AccessTools.Method(monitor, "ProcessException"), prefix: new HarmonyLib.HarmonyMethod(typeof(ClientWorld), nameof(ProcessExceptionPrefix)));
		harmony.Patch(HarmonyLib.AccessTools.Method(monitor, "ThrowAndDiscard"), prefix: new HarmonyLib.HarmonyMethod(typeof(ClientWorld), nameof(ThrowAndDiscardPrefix)));
	}

	private static void ProcessExceptionPrefix(ref System.Runtime.ExceptionServices.ExceptionDispatchInfo? ____exception)
	{
		if (IsRefusedJoin(____exception)) ____exception = null;
	}

	private static void ThrowAndDiscardPrefix(ref System.Runtime.ExceptionServices.ExceptionDispatchInfo? exception)
	{
		if (IsRefusedJoin(exception)) exception = null;
	}

	private static bool IsRefusedJoin(System.Runtime.ExceptionServices.ExceptionDispatchInfo? info)
	{
		// The game's own join (GameAppComponent.LoadNetworkClientAsync) ended: refused, the server shutting down, the
		// connection failing or its hello timing out. Any of them reached the crash handler from that unawaited task.
		if (info?.SourceException is not { } ex || ex.StackTrace?.Contains("GameAppComponent.LoadNetworkClientAsync", StringComparison.Ordinal) != true) return false;
		// Only a join SE2 Online runs or is leaving (a rejoin stops the session first); a vanilla join keeps the game's own handling.
		if (Current == null && MpPlugin.Controller?.Busy != true && !MpPlugin.Online) return false;
		string why = ex.GetBaseException().Message;
		GameLog.Instance.Warn($"client: the game's join ended ({ex.GetType().Name}: {why}); back to the main menu instead of closing the game");
		// Only a join the game started and SE2 Online rides along with (Keen's own task, watched by this monitor) belongs to
		// the current join session; SE2 Online's own joins are awaited in MainTick. Anything else (a join Keen started while
		// a session change was under way) is no join of ours: MainTick decides for the first, ReturnOrphanJoin for the rest.
		if (Current is { _o.KeenAttached: true } world) System.Threading.Interlocked.CompareExchange(ref world._joinEnded, why, null);
		else s_orphanJoinEnded = true;
		return true;
	}

	private static volatile bool s_orphanJoinEnded;

	/// <summary>Main thread (MpPlugin.Tick): a join that ended with no join session of ours goes back to the main menu.</summary>
	internal static void ReturnOrphanJoin(Keen.Game2.GameAppComponent app)
	{
		if (!s_orphanJoinEnded || Current != null || MpPlugin.Controller?.Busy == true || app._isTransitioning.IsSet) return;
		s_orphanJoinEnded = false;
		if (app.Client != null || app.Server != null) return;
		GameLog.Instance.Info("client: a join that was not ours to finish ended; back to the main menu");
		app.Disconnect();
	}

	/// <summary>The player left (Leave, or exit to the menu): an automatic rejoin asked for and not yet started is dropped,
	/// so the player's next join counts from zero.</summary>
	internal static void PlayerLeft() => s_rejoin.RejoinFailed();

	/// <summary>A loss of this player's connection is reported and not handled yet (an exit to the menu then is not a leave).</summary>
	internal bool LossPending => System.Threading.Volatile.Read(ref _lossReported) != null;

	// A join the game started by itself (Steam invite, Join Game) that SE2 Online rides along with cannot be dialled again by
	// SE2 Online (its session never issues a join of its own), so the game's own reconnect prompt handles its losses.
	private bool RejoinsByItself => _o.Rejoin && !_o.KeenAttached;

	// The server's own reason when it refused this player (a refusal or kick never rejoins by itself).
	private string? Refusal() => _session?.RejectReason ?? CustomRulesHost.Refusal;

	/// <summary>Main thread: applies <see cref="RejoinPolicy.Decide"/> for one event. Lost schedules the next automatic rejoin
	/// (waiting longer each time, up to the cap); Failed waits for the player.</summary>
	private void Decide(JoinEvent e, long now, bool worldUp, bool refused, string failed, string lost)
	{
		var stage = Progress.Stage;
		var to = RejoinPolicy.Decide(e, stage, worldUp, _backToMenu, refused, RejoinsByItself, s_rejoin.Rejoins);
		if (to == JoinStage.Lost)
		{
			if (stage != JoinStage.Lost) SetLost(lost, now);
		}
		else if (to == JoinStage.Failed && (e != JoinEvent.Loss || stage != JoinStage.Failed))
		{
			Progress.Set(JoinStage.Failed, now, failed);
			// No automatic rejoin of a failed join (a loss reported just before it may have scheduled one).
			_lostAtMs = 0;
		}
		else if (e == JoinEvent.Loss) _log.Info($"client: connection lost ({lost}) after the join {(stage == JoinStage.Rejected ? "was refused" : stage == JoinStage.Failed ? "failed" : "ended")}; no rejoin");
	}

	private void SetLost(string reason, long now)
	{
		_lostReason = reason;
		_lostAtMs = now;
		_rejoinDueMs = 0;
		Progress.Set(JoinStage.Lost, now, reason);
	}

	private void ScheduleRejoin(long now)
	{
		string reason = _lostReason ?? "connection closed";
		if (s_rejoin.DelayAfterLoss() is not long delay)
		{
			_lostAtMs = 0;
			_log.Warn($"client: lost the connection {s_rejoin.Rejoins + 1} times in a row ({reason}); not rejoining by itself ({MemoryInUse(collect: false)})");
			Progress.SetRejoin(true, false);
			Progress.Set(JoinStage.Lost, now, $"Lost {s_rejoin.Rejoins + 1} times in a row (last: {reason}). Retry joins again. If this keeps happening, send your game log to the server's admin.");
			return;
		}
		_rejoinDueMs = now + delay;
		_log.Info($"client: connection lost ({reason}); rejoining in {delay / 1000} s (attempt {s_rejoin.Rejoins + 1} of {RejoinPolicy.Attempts})");
		Progress.Set(JoinStage.Lost, now, $"{reason}\nRejoining in {delay / 1000} s (attempt {s_rejoin.Rejoins + 1} of {RejoinPolicy.Attempts}).");
	}

	// For the log: a rejoin that does not give the last world's memory back shows here.
	private static string MemoryInUse(bool collect)
	{
		using var me = System.Diagnostics.Process.GetCurrentProcess();
		return $"game memory {me.PrivateMemorySize64 / 1073741824.0:0.0} GB, managed {GC.GetTotalMemory(collect) / 1073741824.0:0.0} GB";
	}

	/// <summary>The world being joined runs missions (read from the server before joining).</summary>
	internal static volatile bool JoinMissions;

	// SE2 builds a joined player's world as a non-mission world without its mission client parts (GameAppComponent's
	// network join passes isMission false and BuildGameClient(isDedicatedClient: true) skips ClientMissionCoreComponent),
	// so players had no contracts screen, quest log, objectives or contract GPS; only an in-process host gets them.
	internal static void InstallMissions(HarmonyLib.Harmony harmony)
	{
		harmony.Patch(HarmonyLib.AccessTools.Method(typeof(Keen.Game2.Client.GameComponents.GameClientWorldBuilder), "BuildGameClient"),
			postfix: new HarmonyLib.HarmonyMethod(typeof(ClientWorld), nameof(BuildClientPostfix)));
		harmony.Patch(HarmonyLib.AccessTools.Method(typeof(Keen.Game2.Client.GameSystems.Missions.ClientMissionCoreComponent), "ConfigureSession"),
			postfix: new HarmonyLib.HarmonyMethod(typeof(ClientWorld), nameof(MissionPartsPostfix)));
	}

	private static bool _joinedMissionWorld;

	private static void BuildClientPostfix(ref Keen.VRage.DCS.Builders.EntityBuilder builder, bool isDedicatedClient)
	{
		_joinedMissionWorld = isDedicatedClient && JoinMissions && builder.Components != null;
		if (!_joinedMissionWorld) return;
		foreach (var c in builder.Components!)
			if (c.ObjectBuilder is Keen.Game2.Game.SessionComponents.WorldSessionComponentObjectBuilder world) world.IsMission = true;
		builder.WithComponents(typeof(Keen.Game2.Client.GameSystems.Missions.ClientMissionCoreComponent));
		GameLog.Instance.Info("client: this world is built with missions and contracts");
	}

	// The mission log overlay is a debug menu screen, and a joined player's game has no debug menu (its world fails to load
	// with the overlay in it). Missions only ever ask for it with TryGet.
	private static void MissionPartsPostfix(Keen.VRage.Core.Game.Systems.SessionBuilder sessionBuilder)
	{
		if (_joinedMissionWorld)
			sessionBuilder.SessionComponents.Components?.RemoveAll(c => c.Type == typeof(Keen.Game2.Client.GameSystems.Missions.MissionLogDisplaySessionComponent));
	}

	private async System.Threading.Tasks.Task<(string? Error, List<WorldMod>? Mods)> PrepareMods(GameSessionID session)
	{
		try { return await PrepareModsCore(session); }
		catch (OperationCanceledException) { return (WorkshopMods.Cancelled, null); }
		catch (Exception ex) { return ("Could not read the server's mod list (" + ex.GetBaseException().Message + ").", null); }
	}

	private async System.Threading.Tasks.Task<(string? Error, List<WorldMod>? Mods)> PrepareModsCore(GameSessionID session)
	{
		// The server's accept carries its mod list and missions flag.
		var accept = _session?.Accept;
		if (accept == null) return ("The connection to the server closed before joining.", null);
		if (accept.ModsError != null) return ("Could not read the server's mod list (" + accept.ModsError + ").", null);
		List<WorldMod>? mods = accept.Mods;
		JoinMissions = accept.HostMissions;
		if (JoinMissions) _log.Info("client: the server's world runs missions and contracts");
		if (mods == null)
		{
			_log.Info($"client: {session} publishes no SE2 Online mod list; joining with this game's own mods");
			return (null, null);
		}
		if (mods.Count == 0) return (null, mods);
		_log.Info("client: the host's world runs mods: " + WorldMods.Summary(mods));
		int maxGiB = MpMenu.Settings?.MaxModDownloadGiB ?? ModDownloads.DefaultMaxGiB;
		string error = await WorkshopMods.PrepareAsync(mods, session.ToString() ?? "the server", maxGiB, _log, Progress.SetMods, _stop.Token);
		return (error, mods);
	}

	private NametagOverlay? _tagOverlay;

	private readonly List<(ushort ClientId, string Name, Keen.VRage.DCS.Components.Entity? Target)> _tagList = new();

	private long _tagChanges = -1;

	private int _tagTick;

	// Other players' name tags: the host's roster names their characters by Keen network id.
	private void UpdateNametags()
	{
		var session = _session;
		var game = MpPlugin.App?.ClientSession;
		if (session == null || game == null || !NametagOverlay.Enabled || Progress.Stage != JoinStage.Playing) return;
		try
		{
			long changes = session.Names.Changes;
			if (changes != _tagChanges || _tagTick++ % 30 == 0)
			{
				_tagChanges = changes;
				_tagList.Clear();
				foreach (var tag in session.Names.Others(session.ClientId)) _tagList.Add((tag.ClientId, tag.Name, tag.NetId == 0 ? null : EntityOf(tag.NetId)));
			}
			var sessionEntity = game.SessionComponents.Get<Keen.Game2.Client.GameSystems.PlayerControl.ClientPlayersSessionComponent>().Entity;
			(_tagOverlay ??= new NametagOverlay(_log, "client")).Update(game, sessionEntity, _tagList);
		}
		catch (Exception ex)
		{
			_log.WarnOnce("client-nametags", $"nametags(client): update failed ({ex.GetType().Name}: {ex.Message})");
		}
	}

	/// <summary>The client session's own update thread: changes to its entities belong here.</summary>
	internal void ClientSessionTick(Keen.VRage.Core.Game.Systems.Session session)
	{
		if (WaterGridsMessage.Enabled) WaterMirror.Apply(id => EntityOf(id));
	}

	public void Leave()
	{
		_left = true;
		_session?.Leave("left the game");
	}

	internal static string UiDescribe() => s_ui?.Describe() ?? "on-screen progress: none";
}
