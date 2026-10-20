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

	private readonly System.Threading.CancellationTokenSource _stop = new();

	/// <summary>Set while this plugin itself calls into Keen's join (its prefix lets that one through).</summary>
	[ThreadStatic] internal static bool Joining;

	private string? _lostReason;

	private bool _left;

	private bool _retired;

	private JoinStage _loggedStage;

	public static ClientWorld? Current { get; private set; }

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
			Progress.SetRejoin(o.Rejoin, o.Rejoin);
			s_ui = new JoinStatusUi(log);
			Current = new ClientWorld(log, o);
		}
		return Current;
	}

	public static void Reset()
	{
		if (Current != null)
		{
			Current._retired = true;
			Current._stop.Cancel();
			Current._session?.Detach();
		}
		Current = null;
		// Water sync belongs to the session that turned it on (the next world, or single player, starts without it).
		WaterGridsMessage.Enabled = false;
		WaterMirror.Reset();
		Progress.Set(JoinStage.Idle, MpClock.NowMs());
	}

	public void Bind(ClientSession session) => _session = session;

	private KeenIds? _ids;

	private Keen.VRage.Core.Game.Systems.Session? _idsFor;

	/// <summary>The id the host's messages use for this replicated entity (Keen's network id), or null if not replicated.</summary>
	internal uint? NetIdOf(Keen.VRage.DCS.Components.Entity entity) => Ids?.TryGet(entity, out uint id) == true ? id : null;

	internal Keen.VRage.DCS.Components.Entity? EntityOf(uint netId) => Ids?.TryGet(netId, out var entity) == true ? entity : null;

	private KeenIds? Ids
	{
		get
		{
			var session = MpPlugin.App?.ClientSession;
			if (session == null) return null;
			if (!ReferenceEquals(session, _idsFor)) (_ids, _idsFor) = (new KeenIds(session, server: false), session);
			return _ids;
		}
	}

	public void CaptureMainQueue(string where) { }

	public void NetTick(long now) { }

	public void AskRejoin(string why) => RejoinRequested?.Invoke(why);

	/// <summary>Join this Keen session (a Steam lobby or a dedicated server's address); called from any thread.</summary>
	public void Join(GameSessionID session)
	{
		_log.Info($"client: joining {session} with the game's own netcode");
		_lostReason = null;
		_left = false;
		_pendingJoin = session;
		Progress.Set(JoinStage.Connecting, MpClock.NowMs());
		_joinBytesAtStart = System.Threading.Interlocked.Read(ref JoinBytes.Received);
	}

	/// <summary>The join never reached a host (net thread).</summary>
	public void FailJoin(string why)
	{
		_log.Warn("client: " + why);
		Progress.Set(JoinStage.Failed, MpClock.NowMs(), why);
	}

	public static void ConnectionLost(ClientSession session, string reason)
	{
		var current = Current;
		if (current == null || current._session != session) return;
		current._lostReason = string.IsNullOrWhiteSpace(reason) ? "connection closed" : reason;
		Progress.Set(JoinStage.Lost, MpClock.NowMs(), current._lostReason);
	}

	/// <summary>Main thread, every frame (GameAppComponent.GameUpdate prefix).</summary>
	private bool _backToMenu;


	private long _barLogMs;

	private long _joinBytesAtStart;

	public void MainTick(Keen.Game2.GameAppComponent app)
	{
		long now = MpClock.NowMs();
		if (_pendingJoin is { } join && _preparing == null)
		{
			_preparedFor = join;
			_preparing = PrepareMods(join);
		}
		if (_preparing is { IsCompleted: true } prepared && _preparedFor is { } target && !app._isTransitioning.IsSet)
		{
			_preparing = null;
			_pendingJoin = null;
			var (error, mods) = prepared.Result;
			if (error == WorkshopMods.Cancelled) Progress.Set(JoinStage.Left, now, error);
			else if (error != null) Progress.Set(JoinStage.Failed, now, error);
			else
			{
				// Without this the client keeps whatever mods the last single-player world loaded.
				app.GameData.SetRequiredProjects(WorkshopMods.ToModList(mods ?? new List<WorldMod>()));
				Joining = true;
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
				if (Progress.Stage < JoinStage.Loading) Progress.Set(JoinStage.Loading, now);
			}
			catch (Exception ex)
			{
				string why = _session?.RejectReason
					?? (KeenNet.ClientRejectReason is { } keen ? "The server refused the connection (" + keen + ")." : "Joining failed: " + ex.Message);
				_log.Warn("client: Keen join ended: " + ex.Message);
				if (Progress.Stage != JoinStage.Rejected) Progress.Set(JoinStage.Failed, now, why);
				// The game unloads the main menu before it loads the host's world; a join that fails after that left the
				// player on a frozen 0% loading screen.
				_backToMenu = true;
			}
		}
		if (_backToMenu && !app._isTransitioning.IsSet && app.Client == null && app.Server == null)
		{
			_backToMenu = false;
			_log.Info("client: join failed; back to the main menu");
			app.Disconnect();
		}
		// Keen loads the host's world itself: the bar follows the bytes it receives, then Keen building the world (its
		// session counts as loaded after the first full tick), then the player's character arriving.
		if (_session?.Accept != null && Progress.Stage is JoinStage.Connecting or JoinStage.Receiving or JoinStage.Loading)
		{
			if (Progress.Stage == JoinStage.Connecting) Progress.Set(JoinStage.Receiving, now);
			Progress.ReceivedSinceJoin(System.Threading.Interlocked.Read(ref JoinBytes.Received) - _joinBytesAtStart);
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
		s_ui?.Update(app, Progress, now);
		UpdateNametags();
#if !PULSAR
		if (_session != null && Progress.Stage == JoinStage.Playing) SelfTest.Client(_session, _log);
		if (Progress.Stage == JoinStage.Playing) SelfTest.Leave(app, _log);
#endif
	}

	private async System.Threading.Tasks.Task<(string? Error, List<WorldMod>? Mods)> PrepareMods(GameSessionID session)
	{
		var mods = await KeenMods.QueryAsync(session, _stop.Token);
		if (mods == null)
		{
			_log.Info($"client: {session} publishes no SE2 Online mod list; joining with this game's own mods");
			return (null, null);
		}
		if (mods.Count == 0) return (null, mods);
		_log.Info("client: the host's world runs mods: " + WorldMods.Summary(mods));
		int maxGiB = MpMenu.Settings?.MaxModDownloadGiB ?? ModDownloads.DefaultMaxGiB;
		string error = await WorkshopMods.PrepareAsync(mods, KeenMods.Label(session), maxGiB, _log, Progress.SetMods, _stop.Token);
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
