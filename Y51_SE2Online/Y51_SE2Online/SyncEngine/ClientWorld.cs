using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using Keen.Game2;
using Keen.Game2.Client.GameSystems.PCU;
using Keen.Game2.Client.GameSystems.PlayerControl;
using Keen.Game2.Client.RuntimeSystems;
using Keen.Game2.Client.WorldObjects.Character;
using Keen.Game2.Client.WorldObjects.Character.Effects;
using Keen.Game2.Client.WorldObjects.Spectators;
using Keen.Game2.OutOfProcess;
using Keen.Game2.Simulation.GameSystems.Control;
using Keen.Game2.Simulation.GameSystems.Ownership;
using Keen.Game2.Simulation.GameSystems.PCU;
using Keen.Game2.Simulation.GameSystems.Player;
using Keen.Game2.Simulation.GameSystems.Toolbars;
using Keen.Game2.Simulation.RuntimeSystems.Saves;
using Keen.Game2.Simulation.Utils;
using Keen.Game2.Simulation.WorldObjects.Characters;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.Pilotable;
using Keen.Game2.Simulation.WorldObjects.Movement;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.Builders;
using Keen.VRage.DCS.Components;
using Keen.VRage.DCS.ObjectBuilders;
using Keen.VRage.DCS.Signals;
using Keen.VRage.Library.Collections;
using Keen.VRage.Library.Definitions;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Library.Threading;
using Keen.VRage.Library.Utils;
using Keen.VRage.Multiplayer;
using Keen.VRage.Multiplayer.Data;
using Keen.VRage.Multiplayer.NetworkStories;
using Keen.VRage.Multiplayer.Replications;
using Keen.VRage.Multiplayer.Replications.FailureStrategies;
using Keen.VRage.Multiplayer.Replications.InProcess;
using Keen.VRage.Multiplayer.SecureIds;
using Keen.VRage.Multiplayer.Sessions;
using Keen.VRage.Physics.Data;
using Y51_SE2Online.Core;
using Y51_SE2Online.Probe;
using Y51_SE2Online.Sync;
using VRage.Multiplayer.Replication.CB;

using Component = Keen.VRage.DCS.Components.Component;

namespace Y51_SE2Online.SyncEngine;

internal sealed class ClientWorld
{
	private sealed class Attempt
	{
		public bool Built;

		public string Summary = "";

		public int Paired;

		public int Missing;

		public int Duplicates;

		public int DataSet;

		public int DataSkipped;

		public int BuiltRoots;

		public int Excluded;

		public Exception? Error;

		public List<Entity> FailedRoots = new List<Entity>();

		public string FailureDetail = "";
	}

	private long _foreignRefused;

	private int _f6Logged;

	private readonly IMpLog _log;

	private JoinAccept _modsFor;

	private System.Threading.Tasks.Task<string> _modsReady;

	private readonly MpOptions _o;

	private readonly ProxyMap _proxies = new ProxyMap();

	private readonly List<StateEntry> _drain = new List<StateEntry>();

	private volatile ClientSession? _session;

	private volatile bool _startRequested;

	private volatile bool _startIssued;

	private volatile bool _configured;

	private bool _mainThreadSeen;

	private InProcessReplication? _replication;

	private InProcessReplicationSessionComponent? _comp;

	private Entity? _sessionProxy;

	private bool _dcsLogged;

	private bool _worldApplied;

	private bool _spectatorQueued;

	private bool _failed;

	private bool _left;

	private int _worldPartsApplied;

	private int _worldEntitiesBuilt;

	private SessionBuilder? _configuredBuilder;

	private bool _snapshotApplied;

	private long _ticks;

	private long _commands;

	private long _stateApplied;

	private long _stateBadSize;

	private long _lifecycleApplied;

	private long _c2sDropped;

	private long _lastStatusMs;

	private long _startRequestedMs;

	private int _entitiesBuilt;

	private object? _blobTarget;

	private long _blobsRegistered;

	private long _blobBytesRegistered;

	private uint _gateSeq;

	private long _gateSinceMs;

	private long _purgedDead;

	private long _despawnSkipped;

	private long _rootsDropped;

	private long _bundlesRetried;

	private ClientUpstream? _upstream;

	private bool _upstreamFailed;

	private bool _charRequested;

	private bool _charFailed;

	private bool _characterFlowFailed;

	private bool _smoothingFailed;

	private long _charRequestMs;

	private long _assignSeen;

	private long _respawnAtMs;

	private long _smoothApplied;

	private uint _pendingNetId;

	private uint _ownNetId;

	private double? _ownHostLocalMs;


	private long _bodyDetailMs;

	// Test diagnostics lower it to exercise the worker path with everyday spawns.
	internal static int BackgroundBuildEntities = 2000;

	private sealed class BackgroundBuild
	{
		public volatile bool IsCompleted;

		public Entity[]? Result;

		public Exception? Error;
	}

	private (BackgroundBuild Job, Action<Entity[]> Finish, Action<Exception> Fail, string Summary, Stopwatch Watch)? _backgroundBuild;

	// SE2's own tasks: a thread-pool GetResult() does not wait for them. This is what its entity spawner does:
	// hop onto an engine worker, build there (Scene.OpenNoSyncSection lets the game tick meanwhile), report back.
	private static async void BuildOnWorker(GameEntitySerializer serializer, GameEntitySerializer.BundleInitArgs args, BackgroundBuild job)
	{
		try
		{
			await Keen.VRage.Library.Threading.Parallel.MoveToIfNeeded();
			job.Result = await serializer.BuildFromBundleAsync(args, allowAsyncSpawn: true);
		}
		catch (Exception ex)
		{
			job.Error = ex;
		}
		finally
		{
			job.IsCompleted = true;
		}
	}

	private void FinishBackgroundBuild((BackgroundBuild Job, Action<Entity[]> Finish, Action<Exception> Fail, string Summary, Stopwatch Watch) pending)
	{
		long built = pending.Watch.ElapsedMilliseconds;
		try
		{
			if (pending.Job.Error != null) throw new InvalidOperationException("the worker build threw", pending.Job.Error);
			pending.Finish(pending.Job.Result ?? throw new InvalidOperationException("the worker build returned nothing"));
			_log.Info($"client: {pending.Summary}; worker build done after {built} ms, added to the scene in {pending.Watch.ElapsedMilliseconds - built} ms");
		}
		catch (Exception ex)
		{
			pending.Fail(ex);
			_log.Error($"client: {pending.Summary}: the worker-thread build failed and was rolled back: {ex}");
			Report(ClientPhase.Error, "background bundle build failed: " + ex.GetType().Name + ": " + ex.Message);
		}
	}

	private const double StallLeadMs = 100.0;

	private static volatile bool _sprintIntent;

	private static long _sprintIntentMs;

	private static long _crouchToggleMs;

	private long _crouchToggleSeen;

	private bool _crouchIntent;

	// Postfixes on the local character's requests (CharacterMovementControlComponent), installed for joiners.
	internal static void SprintRequested(bool sprint)
	{
		_sprintIntent = sprint;
		Interlocked.Exchange(ref _sprintIntentMs, MpClock.NowMs());
	}

	internal static void CrouchToggleRequested() => Interlocked.Exchange(ref _crouchToggleMs, MpClock.NowMs());

	private Entity? _ownCharacter;

	private IController? _controller;

	private readonly HashSet<Entity> _controlled = new HashSet<Entity>(ReferenceEqualityComparer.Instance);

	private readonly List<(uint NetId, byte[] Value)> _smoothOut = new List<(uint, byte[])>();

	private const int CharacterWaitMs = 60000;

	private const int RespawnDelayMs = 5000;

	private const int PossessRetryMs = 1000;

	private uint _assignedNetId;

	private bool _possessQueued;

	private long _possessRetryAtMs;

	private int _possessFailures;

	private Entity? _spectator;

	private ClientStories? _stories;

	private readonly List<(int, Segment)> _sigScratch = new List<(int, Segment)>();

	private readonly Dictionary<string, long> _sigDrops = new Dictionary<string, long>();

	private readonly HashSet<string> _sigFirst = new HashSet<string>(StringComparer.Ordinal);

	private long _signalsApplied;

	private long _signalsDropped;

	private long _namesRegistered;

	private long _namesKnown;

	private long _namesBad;

	private static readonly FieldInfo EntityBackingField = EngineMembers.Field(HookTargets.Reflected.Single(m => m.Id == "component-entity"));

	private readonly StringIdTable _sids = new StringIdTable();

	private readonly ClientObservables _observables;

	private TimeSpan? _hostGameTime0;

	private uint _hostTick0;

	private long _gameTimeCheckMs;

	private long _gameTimeSyncs;

	// Positive: this game clock is ahead of the host's and runs slow; negative: behind, and runs fast.
	private TimeSpan _clockSlewBack;

	private readonly List<(Delegate Move, InProcessReplicationSessionComponent Comp)> _earlyStories = new List<(Delegate, InProcessReplicationSessionComponent)>();

	private bool _earlyStoriesSent;

	public const int MaxEarlyStories = 32;

	private ContinuationQueue? _mainQueue;

	private bool _waitingForMenuLogged;

	private bool _waitingForTransitionLogged;

	private long _menuSettledSinceMs;

	private long _startAttemptMs;

	private int _startAttempts;

	private const int MaxStartAttempts = 5;

	private volatile bool _startFailed;

	private static readonly FieldInfo? s_isTransitioning = typeof(Keen.Game2.GameAppComponent).GetField("_isTransitioning", BindingFlags.Instance | BindingFlags.NonPublic);

	private SessionComponentsSnapshot? _snapshot;

	private byte[]? _snapshotRaw;

	private int _snapshotBytes;

	private int _snapshotRefs;

	private PlayersSessionObjectBuilder? _playersOb;

	private bool _playersRepaired;

	private long _playersVersionSeen = -1L;

	private long _statusSeen;

	private Session? _sessionStandIn;

	private readonly Dictionary<Entity, long> _recentlyDriven = new Dictionary<Entity, long>(ReferenceEqualityComparer.Instance);

	private const long RecentlyDrivenMs = 2000L;

	private static readonly FieldInfo? PcuModelField = typeof(PCUClientSessionComponent).GetField("_streamedModel", BindingFlags.Instance | BindingFlags.NonPublic);

	private bool _pcuDone;

	private bool _pcuStreamedLogged;

	private PlayerPcuModel? _pcuStandIn;

	private LookPredictor? _look;

	private bool _lookFailed;

	private bool _lookDirty;

	private long _lookSentMs;

	private long _lookIntercepted;

	private (uint NetId, ushort Epoch, double X, double Y, double Z) _lookLastSent;

	public const int LookResendMs = 100;

	private OwnBodyFilter? _body;

	private MovePredictor? _move;

	/// <summary>Test hook (latency rig): each frame's newest host position of the own body and the lead added to it.</summary>
	internal static Action<long, double, double, double, System.Numerics.Vector3, double>? OwnLeadProbe;

	private Keen.VRage.Physics.Components.CharacterPhysicsComponent? _movePhys;

	private (float X, float Z, bool Sprint, bool Crouch) _moveInput;

	private string? _bodyWhy;

	private System.Numerics.Vector3? _ownVel;

	private bool _ownVelLogged;

	private byte[]? _lastWritten;

	private long _lastWrittenMs;

	private long _otherWriterLogMs = -4611686018427387904L;

	private long _otherWriterCount;

	private long _otherWriterSuppressed;

	private double _otherWriterMaxM;

	private bool _ownSmoothed;

	private int _ownSmoothedLogged;

	public const float FastFlightOnMs = 15f;

	public const float FastFlightOffMs = 8f;

	public const int FastFlightHoldMs = 750;

	private long _fastUntilMs;

	private long _deckUntilMs;

	private uint _deckRootId;

	private bool _deckFrame;

	// Look snaps in the deck's frame (the host's orientation 120-170 deg off, sample after sample) switched look
	// prediction off for good in a 245 ms session; the deck frame is dropped for a while instead.
	private long _deckFrameOffUntil;

	private long _deckSnapsSeen;

	private long _deckSnapWindowMs;

	private long _deckSnaps;

	private int _jetLogged;

	private System.Numerics.Quaternion _deckQ = System.Numerics.Quaternion.Identity;

	private uint _deckCheckId;

	private long _deckCheckMs;

	private long _deckJumpLogMs = -4611686018427387904L;

	private long _deckJumps;

	private double _dx;

	private double _dy;

	private double _dz;

	private double _dvx;

	private double _dvy;

	private double _dvz;

	private Entity? _seatDiagLast;

	private int _seatDiagCount;

	private const int TagResolveTicks = 30;

	private NametagOverlay? _tagOverlay;

	private long _tagChanges = -1L;

	private int _tagTick;

	private readonly List<(ushort ClientId, string Name, Entity? Target)> _tagList = new List<(ushort, string, Entity)>();

	private static JoinStatusUi? s_ui;

	public const int LostToMenuDelayMs = 4000;

	public const int RejoinDelayMs = 3000;

	private int _generation = 1;

	private volatile string? _lostReason;

	private volatile string? _rejoinAsk;

	private bool _lostHandled;

	private bool _menuReturnIssued;

	private bool _retired;

	private long _lostAtMs;

	private JoinStage _loggedStage = (JoinStage)byte.MaxValue;

	private bool _lostMenuSeen;

	private long _secureAsked;

	private long _secureResolved;

	private long _secureNull;

	private long _secureFailed;

	public static ClientWorld? Current { get; private set; }

	internal Session? GameSession => _comp?.Session;

	// Test diagnostics: the last smoothed transform written per NetId, while set.
	internal Dictionary<uint, byte[]>? WatchWritten;

	public IReadOnlyList<PlayerInfo> SessionPlayers => _session?.Players ?? Array.Empty<PlayerInfo>();

	public static JoinProgress Progress { get; } = new JoinProgress();

	public int Generation => _generation;

	internal ClientSession? BoundSession => _session;

	public bool IdleAfterJoin
	{
		get => !_retired && (_left || _startFailed || _lostReason != null || Progress.Stage is JoinStage.Rejected or JoinStage.GaveUp);
	}

	public static event Action<string>? RejoinRequested;

	private Entity? AssignedEntity(uint netId)
	{
		if (netId != 0)
		{
			return ClientEntity(netId);
		}
		return null;
	}

	private bool OwnCharacterControlled(uint netId)
	{
		if (_ownNetId != netId || _ownCharacter == null)
		{
			return false;
		}
		IController controller = _controller ?? LocalController();
		if (controller == null || controller.ControlledEntities.Count == 0)
		{
			return false;
		}
		foreach (var controlledEntity in controller.ControlledEntities)
		{
			if (controlledEntity.Controllable is Component component && (component.Entity == _spectator || IsForeignCharacter(component.Entity)))
			{
				return false;
			}
		}
		return true;
	}

	private PlayerControllerComponent? LocalController()
	{
		try
		{
			return _comp?.Session.SessionComponents.TryGet<ClientPlayersSessionComponent>()?.LocalPlayerController;
		}
		catch (Exception)
		{
			return null;
		}
	}

	private void CloseSpectator(string why)
	{
		var spectator = _spectator;
		if (spectator == null)
		{
			return;
		}
		_spectator = null;
		_spectatorQueued = false;
		try
		{
			if (!EngineCodec.IsDead(spectator) && !EngineCodec.IsClosing(_comp.Session, spectator))
			{
				_comp.Session.MarkEntityForClose(spectator);
			}
			_log.Info("client: free-camera spectator closed (" + why + ")");
		}
		catch (Exception ex)
		{
			_log.WarnOnce("client-spectator-close", $"client: closing the free-camera spectator failed ({ex.GetType().Name}: {ex.Message})");
		}
	}

	internal bool IsForeignCharacter(Entity? e)
	{
		if (e == null || e.TryGet<CharacterControllableComponent>() == null)
		{
			return false;
		}
		var own = AssignedEntity(_assignedNetId) ?? _ownCharacter;
		return e != own;
	}

	internal bool IsOwnCharacterProxy(object? o)
	{
		if (o is Entity e && _ownNetId != 0 && _proxies.TryGetId(e, out var id))
		{
			return id == _ownNetId;
		}
		return false;
	}

	public static void GetNextCharacterPostfix(PlayerControllerComponent controller, ref IControllable? __result)
	{
		var current = Current;
		if (current?._comp == null || __result == null || AdminAccess.LocalIsAdmin)
		{
			return;
		}
		try
		{
			if (current.IsForeignCharacter(__result.Entity))
			{
				IControllable controllable = current.AssignedEntity(current._assignedNetId)?.TryGet<CharacterControllableComponent>();
				if (controllable != null && controllable.Controller != null && controllable.Controller != controller)
				{
					controllable = null;
				}
				current._foreignRefused++;
				if (current._foreignRefused <= 3)
				{
					current._log.Info($"client: F6 / spectator keys would take another player's character ({__result.Entity}); " + ((controllable != null) ? "staying on (or going back to) this player's own character" : "ignored (no own character yet)"));
				}
				__result = controllable;
			}
		}
		catch (Exception ex)
		{
			current._log.WarnOnce("client-next-character", $"client: F6 filter failed ({ex.GetType().Name}: {ex.Message})");
		}
	}

	public static bool NextCharacterFocusPrefix(SpectatingModesSessionComponent __instance)
	{
		var current = Current;
		if (current?._comp == null || AdminAccess.LocalIsAdmin)
		{
			return true;
		}
		try
		{
			var ownControllable = (current.AssignedEntity(current._assignedNetId) ?? current._ownCharacter)?.TryGet<CharacterControllableComponent>();
			var localPlayerController = __instance.LocalPlayerController;
			if (ownControllable == null || localPlayerController == null)
			{
				// The game's F6 would pick a foreign character or ask for a debug-character spawn.
				return false;
			}
			var topControllable = localPlayerController.GetTopControllable();
			if (topControllable != null && topControllable is not SpectatorComponent && ((IControllable)ownControllable).Controller == localPlayerController)
			{
				// Already on the own character; the game's F6 would RequestUnpossessionAll and drop a cockpit.
				return false;
			}
			IControllable controllable = ownControllable;
			while (true)
			{
				var seat = controllable.Entity.AsInterface<ISeatable>()?.CurrentSeat?.Entity.AsInterface<IControllable>();
				if (seat == null)
				{
					break;
				}
				localPlayerController.CameraSystem.ResetCameraModeForEntity(controllable.Entity);
				controllable = seat;
			}
			localPlayerController.CameraSystem.ResetCameraModeForEntity(controllable.Entity);
			localPlayerController.RequestUnpossessionAll();
			localPlayerController.RequestPossession(ownControllable);
			current._possessRetryAtMs = 0L;
			if (current._f6Logged++ < 3)
			{
				current._log.Info($"client: F6 from the free camera: back to this player's own character (NetworkId {current._assignedNetId})");
			}
			return false;
		}
		catch (Exception ex)
		{
			current._log.WarnOnce("client-f6-focus", $"client: F6 handling failed ({ex.GetType().Name}: {ex.Message}); the game's own F6 runs");
			return true;
		}
	}

	public static bool SetEntityTypeToPossessPrefix(InitialEntityOptions toPossess)
	{
		var current = Current;
		if (current?._comp == null || toPossess != InitialEntityOptions.Character)
		{
			return true;
		}
		try
		{
			current._log.Info("client: the game asked to possess 'a character'; on a joiner that is only this player's own (retried every second until it is built)");
			current._possessRetryAtMs = 0L;
			return false;
		}
		catch (Exception)
		{
			return true;
		}
	}

	public static bool SpectatorKeyPrefix() => Current?._comp == null || AdminAccess.LocalIsAdmin;

	private ClientWorld(IMpLog log, MpOptions o)
	{
		_log = log;
		_o = o;
		_observables = new ClientObservables(log);
		_observables.Adopt = AdoptOwnPerPlayerData;
		StringIdTable.Client = _sids;
	}

	public static void Reset()
	{
		if (Current != null) { Current._retired = true; Current._session?.Detach(); }
		Current = null;
		StringIdTable.Client = null;
		Progress.Set(JoinStage.Idle, MpClock.NowMs());
	}

	public static ClientWorld Install(IMpLog log, MpOptions o)
	{
		if (Current == null)
		{
			Progress.SetRejoin(o.Rejoin, o.Rejoin);
			s_ui = new JoinStatusUi(log);
			Current = new ClientWorld(log, o);
			if (o.BuildFix)
			{
				DataTypeRegistry.Start(log);
			}
			ControlGuard.IsForeignCharacterHere = e => Current?.IsForeignCharacter(e) ?? false;
		}
		return Current;
	}

	public void Bind(ClientSession session)
	{
		_session = session;
		session.SnapshotReady += () =>
		{
			if (!_o.ClientGame)
			{
				_log.Info("client: snapshot received; -y51online:noclientgame set, not starting the client game");
			}
			else
			{
				_startRequestedMs = MpClock.NowMs();
				_startRequested = true;
				_log.Info("client: snapshot received; the client-only game starts on the next main-thread frame");
			}
		};
	}

	public void NetTick(long now)
	{
		// Workshop mods download while the world streams in; the client game waits for them before it loads.
		if (_session?.Accept is { } accept && !ReferenceEquals(accept, _modsFor))
		{
			_modsFor = accept;
			if (accept.Mods.Count > 0) _log.Info("client: the host's world runs mods: " + WorldMods.Summary(accept.Mods));
			_modsReady = accept.ModsError != null ? System.Threading.Tasks.Task.FromResult(accept.ModsError)
				: accept.Mods.Count == 0 ? System.Threading.Tasks.Task.FromResult<string>(null)
				: System.Threading.Tasks.Task.Run(() => WorkshopMods.DownloadAsync(accept.Mods, _log, Progress.SetMods, CancellationToken.None));
		}
		if (_startRequested && !_startIssued && !_mainThreadSeen && now - _startRequestedMs > 10000 && _startRequestedMs > 0)
		{
			_startRequestedMs = 0L;
			_log.Error("client: the client game was requested 10 s ago but GameAppComponent.GameUpdate never ran our prefix; the main-thread hook does not fire on this build (see the Harmony patch lines at startup)");
			_session?.Report(ClientPhase.Error, "main-thread hook (GameAppComponent.GameUpdate prefix) never fired; client game not started");
		}
	}

	private void Report(ClientPhase phase, string text)
	{
		_session?.Report(phase, text);
	}

	public static void GameUpdatePrefix(Keen.Game2.GameAppComponent __instance)
	{
		var current = Current;
		if (current == null)
		{
			return;
		}
		try
		{
			current.OnMainThread(__instance);
		}
		catch (Exception ex)
		{
			current._log.WarnOnce("client-main-thread", $"client: main-thread hook failed: {ex}");
		}
	}

	private bool IsTransitioning(Keen.Game2.GameAppComponent app)
	{
		try
		{
			return s_isTransitioning?.GetValue(app) is AtomicFlag flag && flag.IsSet;
		}
		catch (Exception ex)
		{
			_log.WarnOnce("client-transition-flag", "client: could not read GameAppComponent._isTransitioning (" + ex.GetType().Name + "); relying on retries");
			return false;
		}
	}

	public void CaptureMainQueue(string where)
	{
		if (_mainQueue == null)
		{
			var queue = Keen.VRage.Library.Threading.SynchronizationContext.Current?.Queue;
			if (queue != null)
			{
				_mainQueue = queue;
				_log.Info("client: main-thread continuation queue captured (" + where + ")");
			}
		}
	}

	private void OnMainThread(Keen.Game2.GameAppComponent app)
	{
		bool onMainThread = Environment.CurrentManagedThreadId == MpPlugin.MainThreadId;
		if (onMainThread && _mainQueue == null)
		{
			_mainQueue = Keen.VRage.Library.Threading.SynchronizationContext.Current?.Queue;
		}
		if (!_mainThreadSeen)
		{
			_mainThreadSeen = true;
			_log.Info($"client: frame hook active (GameAppComponent.GameUpdate) on thread '{Thread.CurrentThread.Name}' #{Environment.CurrentManagedThreadId} ({(onMainThread ? "the main thread" : $"NOT the main thread #{MpPlugin.MainThreadId}")}); Keen sync context {((Keen.VRage.Library.Threading.SynchronizationContext.Current != null) ? "present" : "MISSING")}, main queue {((_mainQueue != null) ? "known" : "unknown")}");
		}
		long now = MpClock.NowMs();
		bool atMenu = app.MainMenu != null;
		bool transitioning = IsTransitioning(app);
		bool clientUp = app.Client != null;
		bool menuHandled = MenuStep(atMenu, transitioning, clientUp, now, () => ReturnToMenu(app));
		ShowProgress(app, now);
		if (menuHandled || !_startRequested || _startFailed)
		{
			return;
		}
		if (_startIssued)
		{
			if (_configured || app.MainMenu == null || IsTransitioning(app) || now - _startAttemptMs < 3000)
			{
				return;
			}
			if (_startAttempts >= 5)
			{
				if (_startAttempts++ == 5)
				{
					_log.Error($"client: the client-only game did not start after {5} attempts; giving up (see the 'starting the client game' lines above)");
					Report(ClientPhase.Error, $"client game did not start after {5} attempts");
				}
				return;
			}
			_log.Warn($"client: still at the main menu {now - _startAttemptMs} ms after starting the client game; retrying");
			_startIssued = false;
		}
		if (!atMenu)
		{
			if (!_waitingForMenuLogged)
			{
				_waitingForMenuLogged = true;
				_log.Warn("client: the host's world is ready, but this game is not at the main menu; it joins as soon as you return to the main menu");
				Report(ClientPhase.Warning, "client is not at the main menu; waiting");
			}
			_menuSettledSinceMs = 0L;
			return;
		}
		if (transitioning)
		{
			if (!_waitingForTransitionLogged)
			{
				_waitingForTransitionLogged = true;
				_log.Info("client: the game is still in its own menu transition; waiting for it to finish");
			}
			_menuSettledSinceMs = 0L;
			return;
		}
		if (_menuSettledSinceMs == 0L)
		{
			_menuSettledSinceMs = now;
		}
		if (now - _menuSettledSinceMs >= 1000)
		{
			_startIssued = true;
			_startAttempts++;
			_startAttemptMs = now;
			var queue = _mainQueue ?? (!onMainThread ? null : Keen.VRage.Library.Threading.SynchronizationContext.Current?.Queue);
			if (queue != null)
			{
				queue.Enqueue(Start);
				return;
			}
			_log.Warn($"client: no main-thread continuation queue known; starting the client game directly on thread #{Environment.CurrentManagedThreadId}");
			Start();
		}
		void Start()
		{
			try
			{
				bool missions = _o.ClientMissions ?? (_session?.Accept?.HostMissions == true);
				_log.Info($"client: starting the client game (attempt {_startAttempts}/{5}; single player's client recipe, host missions {(missions ? "on" : "off")})");
				Report(ClientPhase.ClientGameStarting, $"attempt {_startAttempts}, missions {(missions ? "on" : "off")}");
				Progress.SetInGame(inGame: false);
				Progress.Set(JoinStage.Loading, MpClock.NowMs());
				WaterMirror.Reset();
				WaterGridsMessage.Enabled = _session?.Accept?.Settings.WaterSync == true;
				if (WaterGridsMessage.Enabled) _log.Info("client: the host streams water (experimental water sync); this client session gets the water renderer");
				RemoteClientGame.LoadAsync(app, missions, _modsFor?.Mods, _modsReady, _log, Report, ok =>
				{
					if (!ok)
					{
						_startAttemptMs = MpClock.NowMs() - 2000;
					}
				}, e =>
				{
					_startFailed = true;
					Progress.Set(JoinStage.Failed, MpClock.NowMs(), RemoteClientGame.Describe(e));
				}).SkipWait();
			}
			catch (Exception ex)
			{
				_log.Error($"client: starting the client game threw: {ex}");
				Report(ClientPhase.Error, "client game start threw " + ex.GetType().Name + ": " + ex.Message);
			}
		}
	}

	public static bool ConfigurePrefix(NetworkGameClientCoreComponent __instance, SessionBuilder sessionBuilder)
	{
		var current = Current;
		if (current == null || !current._startIssued)
		{
			return true;
		}
		try
		{
			return !current.Configure(__instance, sessionBuilder);
		}
		catch (Exception ex)
		{
			current._log.Error($"client: configuring the client session failed, falling back to Keen's empty replication (you will see nothing): {ex}");
			current.Report(ClientPhase.Error, "session configure failed: " + ex.GetType().Name + ": " + ex.Message);
			return true;
		}
	}

	private bool Configure(NetworkGameClientCoreComponent core, SessionBuilder sb)
	{
		var client = core._client;
		if (client == null || sb.IsServer)
		{
			return false;
		}
		if (_configuredBuilder == sb)
		{
			_log.Warn("client: ConfigureSession called twice for the same session; second call skipped");
			return true;
		}
		_configuredBuilder = sb;
		PassengerSeats.Apply();
		var bindings = Singleton<DefinitionManager>.Instance.GetDefinitionsOfType<PrefabBindingDefinition>().ToImmutableArray();
		var replication = new InProcessReplication(bindings);
		sb.ExternalServices.Add((IGameClient)client);
		((ISessionConfigurator)replication.Client).ConfigureSession(sb);
		sb.ExternalServices.Add<IReplicationPairingProvider>(new InProcessReplicationComponent.SimpleReplicationPairingProvider(replication.Server.ReplayContext));
		var serverComponent = (InProcessReplicationSessionComponent)RuntimeHelpers.GetUninitializedObject(typeof(ServerSceneContext.ServerSessionComponent));
		EntityBackingField.SetValue(serverComponent, _proxies.GetOrCreate(1u));
		replication.Server.SessionComponent = serverComponent;
		sb.SessionComponents.WithComponent((ComponentType: typeof(SimpleReplicationMappingSessionComponent), ObjectBuilder: new SimpleReplicationMappingSessionComponentObjectBuilder
		{
			InProcessReplication = replication
		}));
		_replication = replication;
		_comp = null;
		_worldApplied = _spectatorQueued = _failed = false;
		_worldPartsApplied = _worldEntitiesBuilt = 0;
		_configured = true;
		_log.Info($"client: client session configured with Keen's in-process client replication ({bindings.Length} prefab bindings, replay table {replication.ReplayTable.Stubs?.Length ?? 0} commands) instead of the Empty* services");
		return true;
	}

	public static bool PullPrefix(NetworkGameClientComponent __instance, SessionBuilder clientSession, ref Task __result)
	{
		var current = Current;
		if (current == null || !current._configured)
		{
			return true;
		}
		__result = default;
		try
		{
			current.ApplySnapshot(__instance, clientSession);
		}
		catch (Exception ex)
		{
			current._log.Error($"client: applying the session snapshot failed (session starts with default session components): {ex}");
			current.Report(ClientPhase.Error, "snapshot apply failed: " + ex.GetType().Name + ": " + ex.Message);
		}
		return false;
	}

	private SessionComponentsSnapshot EnsureSnapshot()
	{
		if (_snapshot != null)
		{
			return _snapshot;
		}
		byte[] raw = _snapshotRaw ??= _session?.TryTakeSnapshot() ?? throw new InvalidOperationException("no snapshot received");
		var context = new NetIdProxyContext(null, id => _proxies.GetOrCreate((uint)id));
		_snapshot = EngineCodec.DeserializeSnapshot(raw, context);
		_snapshotBytes = raw.Length;
		_snapshotRefs = context.Read;
		return _snapshot;
	}

	private void ApplySnapshot(NetworkGameClientComponent client, SessionBuilder clientSession)
	{
		var stopwatch = Stopwatch.StartNew();
		string blobNote = "no blob service";
		try
		{
			RegisterBlobs(clientSession.ExternalServices.TryGet<IStreamedOutBlobs>(), "session load");
			IReadOnlyList<Guid> blobs = _session?.SnapshotBlobs ?? Array.Empty<Guid>();
			int missing = blobs.Count(id =>
			{
				var session = _session;
				return session == null || !session.Blobs.Has(id);
			});
			blobNote = $"{blobs.Count} offloaded blobs referenced ({missing} not here), {_blobsRegistered} registered";
			if (missing > 0)
			{
				_log.Warn($"client: the snapshot references {missing} offloaded blob(s) this PC does not have; session components using them may misbehave");
			}
		}
		catch (Exception ex)
		{
			blobNote = "blob registration failed: " + ex.GetType().Name;
			_log.Error($"client: registering offloaded blobs at session load failed: {ex}");
		}
		var snapshot = EnsureSnapshot();
		snapshot.Apply(ref clientSession.SessionComponents);
		var accept = _session?.Accept;
		long identityId = accept?.IdentityId ?? 0;
		var hostTime = snapshot.Components?.Select(c => c.ObjectBuilder).OfType<Keen.Game2.Simulation.GameSystems.GameTimes.GameTimeSessionComponentObjectBuilder>().FirstOrDefault();
		_hostGameTime0 = (hostTime != null && hostTime.Valid && accept != null) ? hostTime.ElapsedTime : null;
		_hostTick0 = accept?.HostTick ?? 0;
		TeamPermissions.Enabled = accept != null && accept.Settings.Identities && accept.Settings.Grids == GridPermissionMode.Team;
		string players = "shared identity (identities off on the host)";
		string summary = "no per-player data component";
		foreach (var info in clientSession.SessionComponents.Components ?? new List<EntityBuilder.ComponentBuildInfo>())
		{
			if (info.ObjectBuilder is InProcessPerPlayerDataClientSessionComponentObjectBuilder perPlayerOb)
			{
				perPlayerOb.ManuallyTranslatedData = PerPlayerDataFromSnapshot(snapshot, identityId, out summary);
			}
			else if (identityId != 0L && info.Type == typeof(ClientPlayersSessionComponent))
			{
				if (info.ObjectBuilder is PlayersSessionObjectBuilder playersOb)
				{
					players = SeedPlayers(playersOb, _session.Players, accept.ClientId, identityId, accept.PlayerId);
					_playersOb = playersOb;
				}
			}
		}
		if (identityId != 0L && _playersOb == null)
		{
			_log.Error("client: the client session has no ClientPlayersSessionComponent builder to seed; this player will act as the host's identity locally");
		}
		_snapshotApplied = true;
		string status = $"session snapshot applied: {snapshot.Components?.Length ?? 0} session components, {MpLogExtensions.Bytes(_snapshotBytes)}, {_snapshotRefs} entity refs (proxies; {SnapshotRefOwners(snapshot)}), players: {players}, per-player data: {summary}, {blobNote}, {stopwatch.ElapsedMilliseconds} ms";
		_log.Info("client: " + status);
		Report(ClientPhase.Progress, status);
	}

	private object EmptyClientVisibleData(out string note)
	{
		note = "";
		try
		{
			return InProcessPerPlayerDataSessionComponent.CreateEmptyClientVisibleData();
		}
		catch (Exception ex)
		{
			note = " (Keen's defaults unavailable: " + ex.GetType().Name + ")";
			var baseType = typeof(InProcessPerPlayerDataBaseSessionComponent);
			var dataType = baseType.GetNestedType("ClientVisibleData", BindingFlags.NonPublic);
			var perPlayerType = baseType.GetNestedType("ClientVisiblePerPlayerData", BindingFlags.NonPublic);
			object data = Activator.CreateInstance(dataType, nonPublic: true);
			object perPlayer = Activator.CreateInstance(perPlayerType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new object[]
			{
				InProcessPerPlayerDataSessionComponent.HotfixIdentity,
				true
			}, null);
			((IList)dataType.GetProperty("Data").GetValue(data)).Add(perPlayer);
			return data;
		}
	}

	private object PerPlayerDataFromSnapshot(SessionComponentsSnapshot snapshot, long own, out string summary)
	{
		try
		{
			return BuildPerPlayerData(snapshot, own, out summary);
		}
		catch (Exception ex)
		{
			_log.Warn($"client: building per-player data from the host's snapshot failed ({ex.GetType().Name}: {ex.Message}); using empty defaults");
			object result = EmptyClientVisibleData(out string note);
			summary = "empty defaults (copy failed)" + note;
			return result;
		}
	}

	internal static object BuildPerPlayerData(SessionComponentsSnapshot snapshot, long own, out string summary)
	{
		long hotfix = PlayerIdentities.HotfixIdentity;
		long local = own != 0L ? own : hotfix;
		var hostData = new Dictionary<long, List<PerPlayerData>>();
		var components = snapshot.Components ?? Array.Empty<SessionComponentsSnapshot.ComponentInfo>();
		for (int i = 0; i < components.Length; i++)
		{
			if (components[i].ObjectBuilder is not PerPlayerDataSessionComponentObjectBuilder perPlayerOb)
			{
				continue;
			}
			foreach (var entry in (CollectionDictionary<IdentityId, List<object>, object>)(object)perPlayerOb.PerPlayerData)
			{
				long identity = entry.Key;
				if (own != 0L || identity == hotfix)
				{
					if (!hostData.TryGetValue(identity, out var list))
					{
						list = hostData[identity] = new List<PerPlayerData>();
					}
					list.AddRange(entry.Value.OfType<PerPlayerData>());
				}
			}
		}
		var ids = new List<long> { local };
		if (!ids.Contains(hotfix))
		{
			ids.Add(hotfix);
		}
		ids.AddRange(hostData.Keys.Where(k => !ids.Contains(k) && !PlayerIdentities.IsNpc(k)));
		var result = new InProcessPerPlayerDataBaseSessionComponent.ClientVisibleData();
		var lines = new List<string>();
		string defaultsNote = null;
		foreach (long id in ids)
		{
			var perPlayer = new InProcessPerPlayerDataBaseSessionComponent.ClientVisiblePerPlayerData(new IdentityId(id), true);
			try
			{
				var defaults = (InProcessPerPlayerDataBaseSessionComponent.ClientVisibleData)InProcessPerPlayerDataSessionComponent.CreateEmptyClientVisibleData();
				var defaultItems = defaults.Data[0].Data.ToList();
				defaults.Data[0].Data.Clear();
				foreach (var item in defaultItems)
				{
					perPlayer.Data.Add(item);
				}
			}
			catch (Exception ex)
			{
				if (defaultsNote == null)
				{
					defaultsNote = " (Keen's defaults unavailable: " + ex.GetType().Name + ")";
				}
			}
			int fromHost = 0;
			var names = new List<string>();
			if (hostData.TryGetValue(id, out var hostItems))
			{
				foreach (var p in hostItems)
				{
					var existing = perPlayer.Data.FirstOrDefault(x => x.GetType() == p.GetType());
					if (existing != null)
					{
						perPlayer.Data.Remove(existing);
					}
					perPlayer.Data.Add(p);
					fromHost++;
					names.Add(p.GetType().Name);
				}
			}
			int defaultsAdded = 0;
			if (id == local && id != hotfix && hostData.TryGetValue(hotfix, out var sharedItems))
			{
				foreach (var t in sharedItems.Select(r => r.GetType()).Distinct())
				{
					if (!perPlayer.Data.Any(x => t.IsInstanceOfType(x)) && t.GetConstructor(Type.EmptyTypes) != null)
					{
						try
						{
							perPlayer.Data.Add((PerPlayerData)Activator.CreateInstance(t));
							defaultsAdded++;
						}
						catch (Exception)
						{
						}
					}
				}
			}
			PadToolbar(perPlayer.Data.OfType<ToolbarPerPlayerData>().FirstOrDefault());
			result.Data.Add(perPlayer);
			string role;
			if (id == local)
			{
				role = own != 0L ? "own" : "host player (shared)";
			}
			else
			{
				role = id == hotfix ? "host player" : "other player";
			}
			lines.Add($"{id} ({role}): {fromHost} from the host ({string.Join(", ", names)}){(defaultsAdded > 0 ? $", {defaultsAdded} defaults added" : "")}");
		}
		summary = $"{ids.Count} identit{(ids.Count == 1 ? "y" : "ies")}, local {local}; " + string.Join("; ", lines) + defaultsNote;
		return result;
	}

	private static void PadToolbar(ToolbarPerPlayerData? toolbar)
	{
		if (toolbar == null)
		{
			return;
		}
		var selection = toolbar.Selection.Selection;
		int toolbars = Math.Max(1, selection.ToolbarIndexIndex + 1);
		int tiles = Math.Max(10, selection.TileIndex + 1);
		while (toolbar.ToolbarTiles.Count < toolbars)
		{
			toolbar.ToolbarTiles.Add(new ObservableCollection<ToolbarTile>());
		}
		foreach (var row in toolbar.ToolbarTiles)
		{
			while (row.Count < tiles)
			{
				row.Add(null);
			}
		}
	}

	internal static string SeedPlayers(PlayersSessionObjectBuilder ob, IReadOnlyList<PlayerInfo> table, ushort ownClientId, long own, long ownPlayer)
	{
		ob.PlayerData.Clear();
		ob.Identities.Clear();
		ob.ClientIds.Clear();
		var others = (from r in table
			where r.ClientId != 1 && r.Identity != own
			orderby r.ClientId
			select r).ToList();
		if (table.Any(p => p.ClientId == 1)) Add(1, 1559595546L, -1559595548L);
		Add(ownClientId, ownPlayer, own);
		foreach (var other in others)
		{
			Add(other.ClientId, other.Player, other.Identity);
		}
		ob.Identities[InProcessPerPlayerDataSessionComponent.HotfixIdentity] = new PlayerIdentity(ownPlayer);
		return $"local player = IdentityId {own} (PlayerIdentity {ownPlayer}, ClientId {ownClientId}); {ob.PlayerData.Count} players seeded";
		void Add(ushort clientId, long player, long identity)
		{
			var playerId = new PlayerIdentity(player);
			var data = new PlayerData
			{
				ClientId = new ClientId(clientId),
				Player = playerId,
				Identity = new IdentityId(identity)
			};
			ob.PlayerData.TryAdd(playerId, data);
			ob.ClientIds.TryAdd(data.ClientId, playerId);
			ob.Identities.TryAdd(data.Identity, playerId);
		}
	}

	internal static void RepairPlayers(PlayersSessionObjectBuilder ob)
	{
		if (ob.ClientIds.TryGetValue(new ClientId(1), out var host)) ob.Identities[InProcessPerPlayerDataSessionComponent.HotfixIdentity] = host;
	}

	private void UpdatePlayers(InProcessReplicationSessionComponent comp, ClientSession session)
	{
		var playersOb = _playersOb;
		if (playersOb != null && !_playersRepaired)
		{
			_playersRepaired = true;
			var players = comp.Session.SessionComponents.TryGet<ClientPlayersSessionComponent>();
			RepairPlayers(playersOb);
			long localIdentity = players != null ? (long)players.LocalPlayerIdentity : 0;
			string line = $"LocalPlayerIdentity = {localIdentity}" + (localIdentity == session.IdentityId ? " (this player's own identity)" : $" (EXPECTED {session.IdentityId}: the client acts as another identity)");
			if (localIdentity == session.IdentityId)
			{
				_log.Info("client: " + line);
			}
			else
			{
				_log.Error("client: " + line);
			}
			Report(localIdentity == session.IdentityId ? ClientPhase.Progress : ClientPhase.Warning, line);
		}
		if (playersOb != null && session.PlayersVersion != _playersVersionSeen)
		{
			_playersVersionSeen = session.PlayersVersion;
			int added = 0;
			foreach (var player in session.Players)
			{
				var playerId = new PlayerIdentity(player.Player);
				var data = new PlayerData
				{
					ClientId = new ClientId(player.ClientId),
					Player = playerId,
					Identity = new IdentityId(player.Identity)
				};
				if (playersOb.PlayerData.TryAdd(playerId, data))
				{
					added++;
				}
				playersOb.ClientIds.TryAdd(data.ClientId, playerId);
				playersOb.Identities.TryAdd(data.Identity, playerId);
			}
			if (added > 0)
			{
				_log.Info($"client: {added} player(s) added to this game's player table ({ClientSession.DescribePlayers(session.Players)})");
			}
		}
		var status = session.Status;
		if (!_pcuStreamedLogged && _ticks % 30 == 0L)
		{
			var pcu = comp.Session.SessionComponents.TryGet<PCUClientSessionComponent>();
			if (pcu != null && PcuModelField?.GetValue(pcu) is PlayerPcuModel model && model != _pcuStandIn && _observables.IsReplica(model))
			{
				_pcuStreamedLogged = true;
				_log.Info($"client: PCU readout is the host's streamed model (observable #{_observables.IdOf(model)}): {model.PCU} / {model.MaxPCU}" + (_pcuStandIn != null ? "; the stand-in is retired" : ""));
			}
		}
		if (status.Item3 == _statusSeen || !_pcuDone)
		{
			return;
		}
		_statusSeen = status.Item3;
		try
		{
			var pcu = comp.Session.SessionComponents.TryGet<PCUClientSessionComponent>();
			if (pcu != null && PcuModelField?.GetValue(pcu) is PlayerPcuModel model && model == _pcuStandIn)
			{
				bool firstLive = model.MaxPCU == 1073741823;
				(model.PCU, model.MaxPCU, _) = status;
				if (firstLive)
				{
					_log.Info($"client: PCU readout is live from the host's server: {status.Item1} / {status.Item2}");
				}
			}
		}
		catch (Exception ex)
		{
			_log.WarnOnce("client-pcu-live", $"client: applying the host's PCU numbers failed ({ex.GetType().Name}: {ex.Message})");
		}
	}

	private string SnapshotRefOwners(SessionComponentsSnapshot snapshot)
	{
		try
		{
			var owners = new List<string>();
			foreach (var info in snapshot.Components ?? Array.Empty<SessionComponentsSnapshot.ComponentInfo>())
			{
				int count = CountEntities(info.ObjectBuilder, 0);
				if (count > 0)
				{
					owners.Add($"{info.Type?.Name ?? "?"} {count}");
				}
			}
			return owners.Count == 0 ? "none found in fields" : "in " + string.Join(", ", owners);
		}
		catch (Exception ex)
		{
			return "owner scan failed: " + ex.GetType().Name;
		}
	}

	private static int CountEntities(object? o, int depth)
	{
		if (o == null || depth > 3)
		{
			return 0;
		}
		if (o is Entity)
		{
			return 1;
		}
		if (o is string || o.GetType().IsPrimitive || o.GetType().IsEnum)
		{
			return 0;
		}
		int count = 0;
		if (o is IEnumerable enumerable && depth < 3)
		{
			int seen = 0;
			foreach (object item in enumerable)
			{
				count += CountEntities(item, depth + 1);
				if (++seen > 2000)
				{
					break;
				}
			}
			return count;
		}
		if (depth >= 2)
		{
			return 0;
		}
		foreach (var field in o.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
		{
			if (!field.FieldType.IsPrimitive && !field.FieldType.IsEnum && field.FieldType != typeof(string))
			{
				count += CountEntities(field.GetValue(o), depth + 1);
			}
		}
		return count;
	}

	public static bool CommitPrefix(object __instance)
	{
		var current = Current;
		var replication = current?._replication;
		if (replication == null || __instance is not InProcessReplicationSessionComponent comp || comp._context != replication.Client)
		{
			return true;
		}
		if (current._failed)
		{
			return false;
		}
		try
		{
			current.OnClientCommit(comp);
		}
		catch (Exception ex)
		{
			current._failed = true;
			current._log.Error($"client: per-tick exchange failed, world sync stops (the game keeps running): {ex}");
			current.Report(ClientPhase.Error, "client tick failed: " + ex.GetType().Name + ": " + ex.Message);
		}
		return false;
	}

	private void OnClientCommit(InProcessReplicationSessionComponent comp)
	{
		_ticks++;
		if (_comp != comp)
		{
			_comp = comp;
			_sessionProxy = _proxies.GetOrCreate(1u);
			if (!comp.ReplayContext.HasPairingRemote(_sessionProxy))
			{
				comp.ReplayContext.AddObjectPairing(_sessionProxy, comp.Entity);
			}
			_proxies.MarkLive(1u);
			_log.Info("client: client scene is ticking; host session entity paired (NetworkId 1)");
			Report(ClientPhase.SessionReady, "client scene ticking, replication attached");
			Progress.SetInGame(inGame: true);
			if (!_worldApplied)
			{
				Progress.Set(JoinStage.Building, MpClock.NowMs());
			}
			if (!_snapshotApplied)
			{
				_log.Warn("client: the session snapshot was never applied (WorldSessionComponent did not call IServerDataProvider.PullDataFromServerAsync); session components run with defaults");
				Report(ClientPhase.Warning, "snapshot not applied: PullDataFromServerAsync was not called");
			}
		}
		var blobSession = _session;
		if (blobSession != null && blobSession.Blobs.HasFresh)
		{
			RegisterBlobs(BlobTarget(comp.Session), "tick");
		}
		var session = _session;
		if (session != null && session.IdentityId != 0L)
		{
			try
			{
				UpdatePlayers(comp, session);
			}
			catch (Exception ex)
			{
				_log.WarnOnce("client-players", "client: updating the player table failed: " + ex.GetType().Name + ": " + ex.Message);
			}
		}
		((IReplicationContext)comp._context).CommitFrame();
		var fresh = InProcessReplicationSessionComponent.AllocateCB();
		object exchangeSync = comp._exchangeSync;
		ReplicationCommandBuffer outgoing;
		if (exchangeSync != null)
		{
			lock (exchangeSync)
			{
				outgoing = comp.Buffer;
				comp.Buffer = fresh;
			}
		}
		else
		{
			outgoing = comp.Buffer;
			comp.Buffer = fresh;
		}
		if (_worldApplied)
		{
			var lookSession = _session;
			if (lookSession != null)
			{
				LookTick(lookSession, MpClock.NowMs());
			}
		}
		if (_worldApplied && !_upstreamFailed)
		{
			try
			{
				SendUpstream(comp, outgoing);
			}
			catch (Exception ex)
			{
				_upstreamFailed = true;
				_log.Error($"client: input upstream failed and is switched off (you can still watch): {ex}");
				Report(ClientPhase.Error, "input upstream failed: " + ex.GetType().Name + ": " + ex.Message);
			}
		}
		else if (outgoing.GetBuffersCount() > 0)
		{
			_c2sDropped++;
			HoldEarlyStories(outgoing, comp);
		}
		InProcessReplicationSessionComponent.ReturnCB(outgoing);
		BuildInbound(comp);
		UpdateNametags(comp);
		if (WaterGridsMessage.Enabled) WaterMirror.Apply(ClientEntity);
		long now = MpClock.NowMs();
		if (now - _lastStatusMs < LogFormat.StatusIntervalMs)
		{
			return;
		}
		_lastStatusMs = now;
		var statusSession = _session;
		_log.Info($"client sync: ticks {_ticks}, commands injected {_commands}, lifecycle applied {_lifecycleApplied}, entities built {_entitiesBuilt}, state applied {_stateApplied} (bad size {_stateBadSize}), proxies {_proxies.LiveCount}/{_proxies.Count}, " + ((statusSession != null) ? $"pending state {statusSession.State.DirtyCount} (stale {statusSession.State.Stale}, expired {statusSession.State.Expired}), lifecycle buffered {statusSession.Lifecycle.Buffered} ready {statusSession.Lifecycle.Ready}, blobs {statusSession.Blobs.Describe()} / {_blobsRegistered} registered, " : "") + $"roots dropped {_rootsDropped} (bundles retried {_bundlesRetried}), despawns skipped {_despawnSkipped}, dead entities purged {_purgedDead}, C2S ticks dropped {_c2sDropped}");
		if (statusSession != null)
		{
			_log.Info($"client sync: character {(_ownNetId != 0 ? $"NetworkId {_ownNetId} possessed" : _pendingNetId != 0 ? $"NetworkId {_pendingNetId} assigned, not built yet" : _charRequested ? "requested" : "none")}; {_upstream?.Describe() ?? "upstream: idle"}, sent {statusSession.InputSent} input values / {statusSession.CommandsSent} commands; {statusSession.Smoother.Describe()}, applied {_smoothApplied}; {LookDescribe()}");
		}
		if (statusSession != null)
		{
			_log.Info($"client sync: host signals applied {_signalsApplied}, dropped {_signalsDropped}, names registered {_namesRegistered} (known {_namesKnown}, bad {_namesBad})" + ((_sigDrops.Count == 0) ? "" : (" (" + string.Join(", ", from kv in _sigDrops.OrderByDescending(kv => kv.Value).Take(6) select $"{kv.Key} x{kv.Value}") + ")")) + $"; {_stories?.Describe() ?? "stories: none"}{DescribeSecure()}; {ReplayGuard.Describe()}; 0.7.6: {AudioGate.Describe()}; {ToolShadowGuard.Describe()}; {BuildGuards.Describe()}; {ControlGuard.Describe()}; data types: {DataTypeRegistry.State}; 0.8.3: {_sids.Describe()}, {FractureGuard.Describe()}; {SlowTicks.Describe()}");
		}
		if (statusSession != null && (_observables.Count > 0 || statusSession.ObservableOpsIn > 0))
		{
			_log.Info($"client sync: {_observables.Describe()}; {statusSession.ObservableOpsIn} steps received, {statusSession.ObservableEditsOut} edits sent");
		}
	}

	private void RegisterHostStrings(List<string> names)
	{
		int registered = 0;
		int known = 0;
		int failed = 0;
		string firstError = null;
		foreach (string name in names)
		{
			try
			{
				if (!_sids.TryRegister(name))
				{
					if (StringHash.TryGet(name, out _))
					{
						known++;
						continue;
					}
					StringHash.GetOrCompute(name);
					registered++;
				}
			}
			catch (Exception ex)
			{
				failed++;
				if (firstError == null)
				{
					firstError = $"'{name}': {ex.GetType().Name}: {ex.Message}";
				}
			}
		}
		_namesRegistered += registered;
		_namesKnown += known;
		_namesBad += failed;
		if (_namesRegistered + _namesKnown == registered + known)
		{
			_log.Info($"client: host StringHash names: {registered} registered, {known} already known, {failed} failed (first table)");
		}
		if (failed > 0)
		{
			_log.WarnOnce("client-names-bad", $"client: {failed} host StringHash name(s) could not be registered (first: {firstError})");
		}
	}

	private ObjectSpace ClientSpace()
	{
		return _observables.Attach(new ObjectSpace
		{
			Name = "client",
			IdOf = NetIdOf,
			Resolve = (id, translated) =>
			{
				if (!translated)
				{
					return ClientEntity(id);
				}
				return (ClientEntity(id) == null || !_proxies.TryGet(id, out var e)) ? null : e;
			},
			Session = _comp?.Session,
			TranslatedSession = SessionStandIn()
		});
	}

	private void HoldEarlyStories(ReplicationCommandBuffer old, InProcessReplicationSessionComponent comp)
	{
		if (_earlyStoriesSent || _earlyStories.Count >= 32)
		{
			return;
		}
		try
		{
			var moves = new List<Delegate>();
			ClientUpstream.CollectStoryMoves(old, moves, 32 - _earlyStories.Count);
			foreach (var move in moves)
			{
				_earlyStories.Add((move, comp));
			}
			if (moves.Count > 0)
			{
				_log.Info($"client: holding {moves.Count} story move(s) made during the load until the world is applied ({_earlyStories.Count} held)");
			}
		}
		catch (Exception ex)
		{
			_log.WarnOnce("client-early-stories", $"client: reading story moves made during the load failed ({ex.GetType().Name}: {ex.Message}); they are dropped");
		}
	}

	private void SendEarlyStories(ClientSession session)
	{
		if (_earlyStoriesSent)
		{
			return;
		}
		_earlyStoriesSent = true;
		if (_earlyStories.Count == 0)
		{
			return;
		}
		var names = new List<string>();
		foreach (var (d, comp) in _earlyStories)
		{
			try
			{
				if (ClientStories.ReadClosure(d, out NetworkStoryContext _, out object box, out InProcessReplicationSessionComponent _) && box != null)
				{
					names.Add(StoryState.Name(ObjectCodec.TypeName(box.GetType())));
				}
				_stories.OnMove(d, session, ClientSpace(), comp);
			}
			catch (Exception ex)
			{
				_log.WarnOnce("client-early-story", $"client: sending a story held during the load failed ({ex.GetType().Name}: {ex.Message})");
			}
		}
		_log.Info($"client: {_earlyStories.Count} story move(s) held during the load sent to the host now: {string.Join(", ", names)}");
		_earlyStories.Clear();
	}

	private Keen.VRage.Library.UI.ObservableObject? AdoptOwnPerPlayerData(Type type)
	{
		long own = _session?.IdentityId ?? 0;
		if (own == 0 || !typeof(PerPlayerData).IsAssignableFrom(type))
		{
			return null;
		}
		var mine = _comp?.Session.SessionComponents.TryGet<InProcessPerPlayerDataClientSessionComponent>()?._data?.Data.FirstOrDefault(d => (long)d.IdentityId == own);
		return mine?.Data.FirstOrDefault(p => type.IsInstanceOfType(p) && !_observables.IsReplica(p));
	}

	private void SyncGameTime()
	{
		var gameTime = _comp?.Session.SessionComponents.TryGet<Keen.Game2.Simulation.GameSystems.GameTimes.GameTimeSessionComponent>();
		if (_hostGameTime0 is not TimeSpan hostTime0 || gameTime?._timeProvider == null || _session?.NewestHostTick is not { } mark)
		{
			return;
		}
		// Measure against the newest tick on the wire: a replayed batch can be one buffered during the join, many seconds old.
		// CommitFrame is [DoNotPause]: host ticks include the host's own paused frames (video screens), which show here as a lead.
		long sinceMs = Math.Clamp(MpClock.NowMs() - mark.AtMs, 0L, 1000L);
		TimeSpan drift = hostTime0 + Keen.Game2.Simulation.UpdateTime.TIME_PER_FRAME * (mark.Tick - _hostTick0) + TimeSpan.FromMilliseconds(sinceMs) - gameTime.FrameTime;
		if (drift > TimeSpan.FromSeconds(2.0))
		{
			gameTime._elapsedTimeOffset += drift;
			_clockSlewBack = TimeSpan.Zero;
			if (++_gameTimeSyncs <= 5 || _gameTimeSyncs % 60 == 0)
			{
				_log.Info($"client: game clock moved {drift.TotalSeconds:+0.0} s to the host's ({_gameTimeSyncs} corrections; weather and other timed events follow the host)");
			}
		}
		else if (drift > TimeSpan.FromSeconds(0.25) || (drift > TimeSpan.FromSeconds(0.05) && _clockSlewBack < TimeSpan.Zero))
		{
			// A client whose frames hitch loses game time steadily (about 1 s a minute on a loaded PC); stepping it forward every
			// minute made weather fades and animations jump, so it runs slightly fast until it catches up instead.
			if (_clockSlewBack >= TimeSpan.Zero && (++_gameTimeSyncs <= 5 || _gameTimeSyncs % 60 == 0))
			{
				_log.Info($"client: game clock {drift.TotalSeconds:0.00} s behind the host's; running it at {1.0 + ClockSlewRate:P0} until it matches ({_gameTimeSyncs} corrections)");
			}
			_clockSlewBack = -drift;
		}
		else if (drift < TimeSpan.FromSeconds(-1.0) || (drift < TimeSpan.FromSeconds(-0.1) && _clockSlewBack > TimeSpan.Zero))
		{
			// Never step the clock back: animation controllers advance by CurrentGameTime - previous, and a negative delta turns every bone NaN.
			if (_clockSlewBack == TimeSpan.Zero && (++_gameTimeSyncs <= 5 || _gameTimeSyncs % 60 == 0))
			{
				_log.Info($"client: game clock {(-drift).TotalSeconds:0.0} s ahead of the host's; running it at {1.0 - ClockSlewRate:P0} until it matches ({_gameTimeSyncs} corrections)");
			}
			_clockSlewBack = -drift;
		}
		else
		{
			_clockSlewBack = TimeSpan.Zero;
		}
	}

	private const double ClockSlewRate = 0.05;

	private void SlewGameTime()
	{
		var gameTime = _comp?.Session.SessionComponents.TryGet<Keen.Game2.Simulation.GameSystems.GameTimes.GameTimeSessionComponent>();
		if (gameTime?._timeProvider == null)
		{
			_clockSlewBack = TimeSpan.Zero;
			return;
		}
		TimeSpan step = TimeSpan.FromTicks(Math.Sign(_clockSlewBack.Ticks) * Math.Min(Math.Abs(_clockSlewBack.Ticks), (long)(gameTime.DeltaTime.Ticks * ClockSlewRate)));
		gameTime._elapsedTimeOffset -= step;
		_clockSlewBack -= step;
	}

	private void ApplyObservables(List<ObservableOp> ops)
	{
		try
		{
			var comp = _comp?.Session.SessionComponents.TryGet<ObservableObjectReplicationClientSessionComponent>();
			_observables.Apply(comp, ops, ClientSpace());
		}
		catch (Exception ex)
		{
			_log.WarnOnce("client-observables", $"client: applying the host's observable steps failed ({ex.GetType().Name}: {ex.Message})");
		}
	}

	private Session? SessionStandIn()
	{
		if (_sessionStandIn != null || _sessionProxy == null)
		{
			return _sessionStandIn;
		}
		try
		{
			var session = (Session)RuntimeHelpers.GetUninitializedObject(typeof(Session));
			typeof(Session).GetField("<SessionComponents>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(session, _sessionProxy);
			_sessionStandIn = session;
		}
		catch (Exception ex)
		{
			_log.WarnOnce("client-session-standin", $"client: no session stand-in ({ex.GetType().Name}: {ex.Message}); host signals with a Session argument are dropped");
		}
		return _sessionStandIn;
	}

	private readonly Dictionary<uint, (bool Ragdoll, long Until)> _ragdolls = new Dictionary<uint, (bool, long)>();

	internal long RagdollWritesSkipped;

	/// <summary>
	/// SE2 simulates a dead character's ragdoll on each side and lets it drive the transform; writing the host's
	/// transform as well made the body fight between two positions every frame.
	/// </summary>
	private bool Ragdolling(uint netId, long now)
	{
		if (_ragdolls.TryGetValue(netId, out var cached) && now < cached.Until)
		{
			return cached.Ragdoll;
		}
		bool ragdoll = ClientEntity(netId) is Entity entity && RagdollGuard.DrivesTransform(entity);
		_ragdolls[netId] = (ragdoll, now + 250);
		if (_ragdolls.Count > 4096)
		{
			_ragdolls.Clear();
		}
		return ragdoll;
	}

	private Entity? ClientEntity(uint id)
	{
		if (_comp == null || !_proxies.IsLive(id) || !_proxies.TryGet(id, out Entity e))
		{
			return null;
		}
		bool skip = false;
		if (_comp.ReplayContext.TranslateObject(e, ReplicationFailureStrategy.NullOnAnyFail, ref skip) is Entity entity && !EngineCodec.IsDead(entity))
		{
			return entity;
		}
		return null;
	}

	private void WriteSignals(ClientSession session, ReplicationCommandBuffer cb, List<WireCommand> cmds)
	{
		var space = ClientSpace();
		bool guarded = _comp != null && ReplicationProbe.IsGuarded(_comp);
		foreach (var cmd in cmds)
		{
			string name = $"key {cmd.Key}";
			string fail = null;
			SignalKeyDef def;
			if (!guarded)
			{
				fail = "replay guard not armed (see the probe:CommitFrame line)";
			}
			else if (!session.RemoteSignals.TryGet(cmd.Key, out def))
			{
				fail = session.RemoteSignals.IsKnown(cmd.Key) ? "unusable here" : "unknown key";
			}
			else
			{
				name = def.Name;
				var layout = CommandLayouts.ByName(def.Name);
				if (layout == null)
				{
					fail = "not in this build";
				}
				else
				{
					uint target = (cmd.Refs.Count > 0 && cmd.Refs[0].Kind == WireKind.Entity) ? cmd.Refs[0].Id : 0u;
					if (target == 0 || !_proxies.IsLive(target))
					{
						fail = "target not live here";
					}
					else
					{
						var entity = ClientEntity(target);
						if (entity == null || !EntityInternalSignalsApi.Constructed(entity))
						{
							fail = "target destroyed here";
						}
						else
						{
							var c = cmd;
							if (layout.StringIdOffsets.Length != 0)
							{
								byte[] payload = _sids.Map(cmd.Payload, layout.StringIdOffsets, toLocal: true);
								if (payload == null)
								{
									fail = "StringId the host did not announce";
								}
								else
								{
									c = new WireCommand
									{
										Key = cmd.Key,
										Payload = payload,
										Refs = cmd.Refs
									};
									fail = FractureGuard.Check(layout, c, entity);
								}
							}
							if (fail == null)
							{
								space.ClearLocalIds();
								SignalCodec.Write(cb, layout, c, space, _sigScratch, ref fail);
							}
						}
					}
				}
			}
			if (fail == null)
			{
				_signalsApplied++;
				if (_sigFirst.Count < 80 && _sigFirst.Add(name))
				{
					_log.Info("client: host signal " + ClientUpstream.Short(name) + " written for replay (first one; the replay guard reports handler errors)");
				}
			}
			else
			{
				_signalsDropped++;
				string key = ClientUpstream.Short(name) + ": " + fail;
				_sigDrops[key] = _sigDrops.TryGetValue(key, out var drops) ? drops + 1 : 1;
			}
		}
	}

	internal uint? NetIdOf(Entity? e)
	{
		if (e == null || _comp == null)
		{
			return null;
		}
		var context = _comp.ReplayContext;
		if (!context.HasPairingLocal(e))
		{
			return null;
		}
		if (context.TranslateLocalToRemote(e) is not Entity remote || !_proxies.TryGetId(remote, out var id))
		{
			return null;
		}
		return id;
	}

	private void SendUpstream(InProcessReplicationSessionComponent comp, ReplicationCommandBuffer old)
	{
		var session = _session;
		var ownCharacter = _ownCharacter;
		if (session == null)
		{
			return;
		}
		_upstream ??= new ClientUpstream(_log);
		var upstream = _upstream;
		if (upstream.OnObservableEdit == null)
		{
			upstream.OnObservableEdit = (replica, name, value) => _observables.OnLocalEdit(replica, name, value, ClientSpace());
		}
		_stories ??= new ClientStories(_log);
		_stories.PerPlayer = session.IdentityId != 0;
		SendEarlyStories(session);
		long nowMs = MpClock.NowMs();
		if (old.GetBuffersCount() > 0)
		{
			_controlled.Clear();
			if (ownCharacter != null)
			{
				Collect(ownCharacter, 0);
			}
			try
			{
				var controller = _controller;
				if (controller != null && ownCharacter != null)
				{
					foreach (var (controllable, entity) in controller.ControlledEntities)
					{
						if (controllable is Component foreign && IsForeignCharacter(foreign.Entity))
						{
							continue;
						}
						if (controllable is Component component && component.Entity != ownCharacter && !_controlled.Contains(component.Entity))
						{
							Collect(component.Entity, 0);
							var root = component.Entity;
							_recentlyDriven[root] = nowMs;
							for (int depth = 0; depth < 8; depth++)
							{
								var parent = root.TryGet<HierarchyComponent>()?.Parent;
								if (parent == null)
								{
									break;
								}
								root = parent;
								_recentlyDriven[root] = nowMs;
							}
							_controlled.Add(root);
						}
						if (entity != null && !_controlled.Contains(entity))
						{
							Collect(entity, 0);
						}
					}
				}
			}
			catch (Exception ex)
			{
				_log.WarnOnce("client-controlled", $"client: reading what the player controls failed ({ex.GetType().Name}: {ex.Message}); only the character's input is sent");
			}
			ObjectSpace space = ClientSpace();
			if (_recentlyDriven.Count > 0)
			{
				foreach (var stale in (from kv in _recentlyDriven where nowMs - kv.Value > 2000 select kv.Key).ToList())
				{
					_recentlyDriven.Remove(stale);
				}
			}
			_upstream.Capture(old, e => (e == null || !_controlled.Contains(e)) ? null : NetIdOf(e), space, d => _stories.OnMove(d, session, ClientSpace(), comp), e => (e == null || (!_controlled.Contains(e) && !_recentlyDriven.ContainsKey(e))) ? null : NetIdOf(e));
		}
		_upstream.Flush(session, MpClock.NowMs());
		var edits = _observables.TakeEdits();
		if (edits != null)
		{
			session.SendObservableEdits(edits);
		}
		void Collect(Entity e, int depth)
		{
			_controlled.Add(e);
			if (depth <= 8)
			{
				var children = e.TryGet<HierarchyComponent>()?.Children;
				if (children != null)
				{
					foreach (var child in children)
					{
						Collect(child, depth + 1);
					}
				}
			}
		}
	}

	/// <summary>
	/// A component that throws while its entity is added leaves the entity in the scene half-initialised, and its
	/// jobs then throw every tick (a paint tool did that and crashed the game). Back to staging and closed instead.
	/// </summary>
	private int CloseHalfAdded(Session session, Entity[] built)
	{
		int closed = 0;
		foreach (Entity entity in built)
		{
			if (entity == null || EngineCodec.IsDead(entity)) continue;
			try
			{
				if (!session.IsEntityInScene(entity)) continue;
				session.MarkEntityForClose(entity);
				closed++;
			}
			catch (Exception ex)
			{
				// Its own removal listeners threw too; staging alone keeps its jobs from running.
				try { entity.Data.Set<Keen.VRage.DCS.CoreData.StagingTag>(default); } catch (Exception) { }
				_log.WarnOnce("client-half-added", "client: closing a half-added entity threw " + ex.GetType().Name + ": " + ex.Message + "; left in staging");
			}
		}
		return closed;
	}

	private void CharacterFlow(ClientSession session, VRage.Multiplayer.Replication.CB.ReplicationCommandBufferWriterReader w, int baseIndex, ref int cmds, long now)
	{
		if (!_charRequested)
		{
			_charRequested = true;
			_charRequestMs = now;
			session.RequestCharacter(0);
			_log.Info($"client: asking the host for a character (ClientId {session.ClientId})");
			Report(ClientPhase.Progress, $"character requested (ClientId {session.ClientId})");
		}
		(byte, uint, string, long) assignment = session.Assignment;
		if (assignment.Item4 != _assignSeen)
		{
			_assignSeen = assignment.Item4;
			switch (assignment.Item1)
			{
			case 0:
				_pendingNetId = assignment.Item2;
				_assignedNetId = assignment.Item2;
				_possessQueued = false;
				_possessFailures = 0;
				_possessRetryAtMs = 0L;
				_charFailed = false;
				Progress.Set(JoinStage.WaitingForCharacter, now);
				break;
			case 1:
				_charFailed = true;
				_log.Warn("client: the host could not give us a character (" + assignment.Item3 + "); free camera instead");
				Report(ClientPhase.Warning, "no character: " + assignment.Item3);
				break;
			case 2:
				if (assignment.Item2 == _ownNetId)
				{
					ClearOwnCharacter("lost on the host: " + assignment.Item3);
				}
				_pendingNetId = 0u;
				if (assignment.Item2 == _assignedNetId)
				{
					_assignedNetId = 0u;
				}
				_respawnAtMs = now + 5000;
				_charRequestMs = now;
				_log.Info($"client: our character is gone ({assignment.Item3}); asking for a new one in {5} s");
				break;
			}
		}
		if (_respawnAtMs != 0L && now >= _respawnAtMs)
		{
			_respawnAtMs = 0L;
			session.RequestCharacter(1);
			_log.Info("client: asking the host for a new character");
		}
		uint assigned = _assignedNetId;
		if (assigned != 0 && !_possessQueued && now >= _possessRetryAtMs && _proxies.IsLive(assigned) && !OwnCharacterControlled(assigned))
		{
			_pendingNetId = 0u;
			_possessQueued = true;
			w.Write_Keen_VRage_Multiplayer_Replications_InProcess_InProcessReplicationSessionComponent_PostInvokeInternalSignal(baseIndex, _sessionProxy, (Action<SceneContext>)(_ => Possess(assigned)));
			cmds++;
		}
		if (_o.Spectator && !_spectatorQueued && _ownCharacter == null && _assignedNetId == 0 && session.SpawnMenu == null && (_charFailed || now - _charRequestMs > 60000))
		{
			_spectatorQueued = true;
			_log.Info("client: " + (_charFailed ? "no character" : $"no character assigned after {60} s") + "; using the free camera until one arrives");
			w.Write_Keen_VRage_Multiplayer_Replications_InProcess_InProcessReplicationSessionComponent_PostInvokeInternalSignal(baseIndex, _sessionProxy, (Action<SceneContext>)(_ => Spectate()));
			cmds++;
		}
	}

	private void Possess(uint netId)
	{
		_possessQueued = false;
		if (netId != _assignedNetId)
		{
			return;
		}
		try
		{
			var comp = _comp;
			if (!_proxies.TryGet(netId, out Entity e))
			{
				throw new InvalidOperationException($"NetworkId {netId} unknown");
			}
			bool skip = false;
			if (comp.ReplayContext.TranslateObject(e, ReplicationFailureStrategy.NullOnAnyFail, ref skip) is not Entity entity || EngineCodec.IsDead(entity))
			{
				throw new InvalidOperationException($"NetworkId {netId} has no live client entity");
			}
			var controllable = entity.Components.OfType<IControllable>().FirstOrDefault() ?? throw new InvalidOperationException($"entity {entity} has no IControllable component");
			var controller = (comp.Session.SessionComponents.TryGet<ClientPlayersSessionComponent>() ?? throw new InvalidOperationException("no ClientPlayersSessionComponent")).LocalPlayerController ?? throw new InvalidOperationException("no local player controller");
			bool alreadyOwn = _ownNetId == netId;
			controller.RequestUnpossessionAll();
			controller.RequestPossession(controllable);
			_controller = controller;
			_ownCharacter = entity;
			_ownNetId = netId;
			_session?.Smoother.SetRaw(netId, raw: true);
			_ownSmoothed = false;
			_upstream?.Reset();
			if (!alreadyOwn)
			{
				LookBegin(netId);
			}
			CloseSpectator("own character possessed");
			_possessRetryAtMs = MpClock.NowMs() + 1000;
			if (alreadyOwn)
			{
				_log.Info($"client: control back on own character NetworkId {netId} (this game's controller had left it)");
				return;
			}
			string line = $"POSSESSED own character NetworkId {netId} ({entity.GetComposition()?.Guid.ToString() ?? "?"}, {controllable.GetType().Name}); input goes to the host from now on" + (_possessFailures > 0 ? $" (after {_possessFailures} failed attempt(s))" : "");
			_log.Info("client: " + line);
			Report(ClientPhase.CharacterReady, line);
			Progress.Set(JoinStage.Playing, MpClock.NowMs());
			EnsureLocalPcuModel(comp);
		}
		catch (Exception ex)
		{
			_possessFailures++;
			_possessRetryAtMs = MpClock.NowMs() + 1000;
			if (_possessFailures <= 3 || _possessFailures % 30 == 0)
			{
				_log.Warn($"client: possessing our character (NetworkId {netId}) failed (attempt {_possessFailures}): {ex.GetType().Name}: {ex.Message}; retrying in {1} s");
				Report(ClientPhase.Warning, $"possess attempt {_possessFailures} failed: {ex.GetType().Name}: {ex.Message}");
			}
		}
	}

	private void EnsureLocalPcuModel(InProcessReplicationSessionComponent comp)
	{
		if (_pcuDone)
		{
			return;
		}
		_pcuDone = true;
		try
		{
			var pcu = comp.Session.SessionComponents.TryGet<PCUClientSessionComponent>();
			if (pcu == null || PcuModelField == null)
			{
				_log.Warn("client: no PCU session component (" + (pcu == null ? "component" : "field") + " missing); block placement may be refused locally");
				return;
			}
			if (PcuModelField.GetValue(pcu) is PlayerPcuModel local)
			{
				_log.Info("client: PCU model already attached (" + (_observables.IsReplica(local) ? "the host's streamed model" : "local") + "); no stand-in needed");
				return;
			}
			var standIn = new PlayerPcuModel
			{
				PCU = 0,
				MaxPCU = 1073741823
			};
			PcuModelField.SetValue(pcu, standIn);
			_pcuStandIn = standIn;
			try
			{
				var method = typeof(PCUClientSessionComponent).GetMethod("OnStreamedModelDataChanged", BindingFlags.Instance | BindingFlags.NonPublic);
				if (method != null)
				{
					standIn.PropertyChanged += (PropertyChangedEventHandler)Delegate.CreateDelegate(typeof(PropertyChangedEventHandler), pcu, method);
				}
				else
				{
					_log.Warn("client: PCUClientSessionComponent.OnStreamedModelDataChanged not found; the PCU readout may not refresh");
				}
			}
			catch (Exception ex)
			{
				_log.Warn($"client: PCU readout refresh hook failed ({ex.GetType().Name}: {ex.Message})");
			}
			_statusSeen = 0L;
			_log.Info(_session is not { IdentityId: 0 } ? "client: PCU model stand-in installed (this player's live PCU on the host's server follows every second)" : "client: PCU model stand-in installed (the host's server enforces the real limit; the PCU readout here is not live)");
		}
		catch (Exception ex)
		{
			_log.Warn($"client: PCU model stand-in failed ({ex.GetType().Name}: {ex.Message}); block placement may be refused locally");
		}
	}

	private void ClearOwnCharacter(string why)
	{
		if (_ownCharacter != null)
		{
			_log.Info($"client: our character NetworkId {_ownNetId} is no longer controlled ({why})");
			_session?.Smoother.SetRaw(_ownNetId, raw: false);
			LookEnd(why);
			_ownHostLocalMs = null;
			_ownCharacter = null;
			_ownNetId = 0u;
			_upstream?.Reset();
		}
	}

	private void ApplySmoothed(ClientSession session, VRage.Multiplayer.Replication.CB.ReplicationCommandBufferWriterReader w, int baseIndex, ref int cmds, long now)
	{
		if (!session.Smoother.Enabled || !session.TryGetTransformType(out var typeId, out var size))
		{
			return;
		}
		_smoothOut.Clear();
		session.Smoother.Sample(now, _smoothOut);
		NoteDeckSample(_smoothOut);
		foreach (var (netId, value) in _smoothOut)
		{
			if (value.Length == size && _proxies.IsLive(netId) && _proxies.TryGet(netId, out Entity e) && !LookTakeSmoothed(netId, value, now))
			{
				if (Ragdolling(netId, now))
				{
					RagdollWritesSkipped++;
					continue;
				}
				NoteOwnWrittenElsewhere(netId, value, now);
				if (WatchWritten != null) WatchWritten[netId] = value;
				w.Write_Keen_VRage_Multiplayer_Replications_InProcess_InProcessReplicationSessionComponent_PostDataSignal(baseIndex + 1, _sessionProxy, e, new InProcessReplicationSessionComponent.InProcessPayload(typeId, (ReadOnlySpan<byte>)value));
				_smoothApplied++;
				cmds++;
			}
		}
	}

	// Signals resolve their entities when written, so after a bundle is queued in this frame a batch may still go now
	// if everything it names already exists here; one that names the bundle's new entities waits for the next frame.
	private bool TargetsLive(List<WireCommand>? signals)
	{
		if (signals == null) return true;
		foreach (var cmd in signals)
		{
			foreach (var r in cmd.Refs)
			{
				if (r.Kind == WireKind.Entity && !_proxies.IsLive(r.Id)) return false;
			}
		}
		return true;
	}

	private const long ConnectionHoldMs = 3000;
	private uint _connectionHoldSeq;
	private long _connectionHoldSince;
	private long _connectionHoldsReleased;

	private static bool IsConnectionSignal(string name) =>
		name.Contains("CubeGridComponent+GridsConnectionChangedSignal", StringComparison.Ordinal)
		|| name.Contains("MechanicalBlockComponent+ConnectionCreatedSignal", StringComparison.Ordinal)
		|| name.Contains("MechanicalBlockComponent+ConnectionBrokenSignal", StringComparison.Ordinal)
		|| name.Contains("MechanicalBlockComponent+IsConnectedChangedSignal", StringComparison.Ordinal)
		|| name.Contains("MechanicalBlockComponent+UpdateConnectionWithCounterpartSignal", StringComparison.Ordinal)
		|| name.Contains("ConveyorSystemComponent+SystemConnectionsChangedSignal", StringComparison.Ordinal)
		|| name.Contains("ConveyorSystemComponent+LinkToConveyorSystemSignal", StringComparison.Ordinal);

	// Connecting two grids before both exist here leaves the connector/conveyor graph half-linked ("already an active
	// connection", "Node already has link attached") and later throws in ConveyorSystemComponent.TraverseSystems. Such a
	// batch waits until every grid it names (the partner included, via component refs) is built and in the scene, for
	// up to ConnectionHoldMs; a partner that never arrives must not stall everything after it.
	private bool ConnectionTargetsLive(ClientSession session, uint seq, List<WireCommand>? signals, long now)
	{
		if (signals == null) return true;
		bool waiting = false;
		foreach (var cmd in signals)
		{
			if (!session.RemoteSignals.TryGet(cmd.Key, out SignalKeyDef def) || !IsConnectionSignal(def.Name)) continue;
			foreach (var r in cmd.Refs)
			{
				if (r.Kind is WireKind.Entity or WireKind.Component && !_proxies.IsLive(r.Id)) { waiting = true; break; }
			}
			if (waiting) break;
		}
		if (!waiting) return true;
		if (_connectionHoldSeq != seq)
		{
			_connectionHoldSeq = seq;
			_connectionHoldSince = now;
			return false;
		}
		if (now - _connectionHoldSince < ConnectionHoldMs) return false;
		if (++_connectionHoldsReleased <= 5)
		{
			_log.Warn($"client: a grid connection signal (lifecycle #{seq}) still names a grid that is not here after {ConnectionHoldMs} ms; applying it anyway");
		}
		return true;
	}

	private void BuildInbound(InProcessReplicationSessionComponent comp)
	{
		var session = _session;
		if (session == null || _sessionProxy == null)
		{
			return;
		}
		var cb = InProcessReplicationSessionComponent.AllocateCB();
		var w = new VRage.Multiplayer.Replication.CB.ReplicationCommandBufferWriterReader(cb);
		int cmds = 0;
		int baseIndex = VRage.Multiplayer.Replication.CB.ReplicationCommandBufferWriterReader.AssemblyOffset;
		int taken = 0;
		int applies = 0;
		long now = MpClock.NowMs();
		uint seq;
		LifecycleItem item;
		if (_clockSlewBack != TimeSpan.Zero)
		{
			w.Write_Keen_VRage_Multiplayer_Replications_InProcess_InProcessReplicationSessionComponent_PostInvokeInternalSignal(baseIndex, _sessionProxy, (Action<SceneContext>)(_ => SlewGameTime()));
			cmds++;
		}
		if (_backgroundBuild is { } pending)
		{
			if (!pending.Job.IsCompleted)
			{
				// Later updates may touch the entities still being built: hold them until it is done.
				taken = int.MaxValue;
			}
			else
			{
				_backgroundBuild = null;
				w.Write_Keen_VRage_Multiplayer_Replications_InProcess_InProcessReplicationSessionComponent_PostInvokeInternalSignal(baseIndex, _sessionProxy, (Action<SceneContext>)(_ => FinishBackgroundBuild(pending)));
				cmds++;
			}
		}
		while (taken < 64 && session.Lifecycle.TryPeek(out seq, out item))
		{
			if ((item.Type == SyncMsg.EntityBundle && item.Bundle.RequiredBlobs.Count > 0 && !BlobsReady(session, seq, item.Bundle, now)) || (item.Type == SyncMsg.SignalBatch && ((applies > 0 && !TargetsLive(item.Signals)) || !ConnectionTargetsLive(session, seq, item.Signals, now))) || !session.Lifecycle.TryTake(out uint takenSeq, out LifecycleItem next))
			{
				break;
			}
			Core.LatencyTrace.Mark(Core.LatencyTrace.Taken, (byte)next.Type, takenSeq);
			if (next.Type == SyncMsg.StringTable)
			{
				RegisterHostStrings(next.Strings);
				_lifecycleApplied++;
				taken++;
			}
			else if (next.Type == SyncMsg.ObservableBatch)
			{
				var ops = next.Observables;
				w.Write_Keen_VRage_Multiplayer_Replications_InProcess_InProcessReplicationSessionComponent_PostInvokeInternalSignal(baseIndex, _sessionProxy, (Action<SceneContext>)(_ => ApplyObservables(ops)));
				_lifecycleApplied++;
				taken++;
				cmds++;
			}
			else if (next.Type == SyncMsg.SignalBatch)
			{
				if (_hostGameTime0.HasValue && now - _gameTimeCheckMs >= 1000)
				{
					_gameTimeCheckMs = now;
					w.Write_Keen_VRage_Multiplayer_Replications_InProcess_InProcessReplicationSessionComponent_PostInvokeInternalSignal(baseIndex, _sessionProxy, (Action<SceneContext>)(_ => SyncGameTime()));
					cmds++;
				}
				try
				{
					WriteSignals(session, cb, next.Signals);
					Core.LatencyTrace.Mark(Core.LatencyTrace.Applied, (byte)SyncMsg.SignalBatch, takenSeq);
				}
				catch (Exception ex)
				{
					_log.WarnOnce("client-signals", $"client: writing host signals failed ({ex.GetType().Name}: {ex.Message}); this batch is skipped");
				}
				_lifecycleApplied++;
				taken++;
				cmds++;
			}
			else
			{
				var it = next;
				w.Write_Keen_VRage_Multiplayer_Replications_InProcess_InProcessReplicationSessionComponent_PostInvokeInternalSignal(baseIndex, _sessionProxy, (Action<SceneContext>)(_ => Apply(it)));
				cmds++;
				// Only a bundle creates entities later signals may target; despawns and data removals hold nothing back.
				if (it.Type == SyncMsg.EntityBundle) applies++;
				taken++;
				if (it.Type == SyncMsg.EntityBundle && it.Bundle.Entities.Count >= 200)
				{
					break;
				}
			}
		}
		if (_worldApplied && _o.Character && !_characterFlowFailed)
		{
			try
			{
				CharacterFlow(session, w, baseIndex, ref cmds, now);
			}
			catch (Exception ex)
			{
				_characterFlowFailed = true;
				_log.Error($"client: the character flow failed and is switched off (world sync continues, free camera): {ex}");
				Report(ClientPhase.Error, "character flow failed: " + ex.GetType().Name + ": " + ex.Message);
				if (_o.Spectator && !_spectatorQueued)
				{
					_spectatorQueued = true;
					w.Write_Keen_VRage_Multiplayer_Replications_InProcess_InProcessReplicationSessionComponent_PostInvokeInternalSignal(baseIndex, _sessionProxy, (Action<SceneContext>)(_ => Spectate()));
					cmds++;
				}
			}
		}
		else if (_worldApplied && !_spectatorQueued && _o.Spectator)
		{
			_spectatorQueued = true;
			w.Write_Keen_VRage_Multiplayer_Replications_InProcess_InProcessReplicationSessionComponent_PostInvokeInternalSignal(baseIndex, _sessionProxy, (Action<SceneContext>)(_ => Spectate()));
			cmds++;
		}
		if (_stories != null)
		{
			try
			{
				Entity proxy = _sessionProxy;
				_stories.ProcessInbox(session.StoryInbox, ClientSpace(), action =>
				{
					w.Write_Keen_VRage_Multiplayer_Replications_InProcess_InProcessReplicationSessionComponent_PostInvokeInternalSignal(baseIndex, proxy, (Action<SceneContext>)(_ => action()));
					cmds++;
				});
			}
			catch (Exception ex)
			{
				_log.WarnOnce("client-stories", "client: processing the host's story replies failed: " + ex.GetType().Name + ": " + ex.Message);
			}
		}
		if (!_smoothingFailed)
		{
			try
			{
				ApplySmoothed(session, w, baseIndex, ref cmds, now);
			}
			catch (Exception ex)
			{
				_smoothingFailed = true;
				session.Smoother.DelayMs = 0;
				_log.Error($"client: motion smoothing failed and is switched off (transforms are applied raw): {ex}");
			}
		}
		_drain.Clear();
		session.State.Drain(id => _proxies.IsLive(id), key => !session.Types.IsKnown(key) ? null : session.Types.TryResolve(key, out _, out _), _drain, MpClock.NowMs());
		foreach (var entry in _drain)
		{
			if (!session.Types.TryResolve(entry.Key, out var localTypeId, out var size) || entry.Bytes.Length != size || !_proxies.TryGet(entry.NetId, out Entity e))
			{
				_stateBadSize++;
			}
			else if (!LookIntercept(session, entry, now))
			{
				if (session.IsTransformKey(entry.Key) && Ragdolling(entry.NetId, now))
				{
					RagdollWritesSkipped++;
					continue;
				}
				if (session.IsTransformKey(entry.Key))
				{
					NoteOwnWrittenElsewhere(entry.NetId, entry.Bytes, now);
				}
				w.Write_Keen_VRage_Multiplayer_Replications_InProcess_InProcessReplicationSessionComponent_PostDataSignal(baseIndex + 1, _sessionProxy, e, new InProcessReplicationSessionComponent.InProcessPayload(localTypeId, (ReadOnlySpan<byte>)entry.Bytes));
				_stateApplied++;
				cmds++;
			}
		}
		LookWrite(session, w, baseIndex, ref cmds);
		if (cmds == 0)
		{
			InProcessReplicationSessionComponent.ReturnCB(cb);
			return;
		}
		_commands += cmds;
		comp.ScheduleForReplay(cb);
	}

	private void Apply(LifecycleItem item)
	{
		try
		{
			switch (item.Type)
			{
			case SyncMsg.EntityBundle:
				ReplicateBundle(item.Bundle);
				Core.LatencyTrace.Mark(Core.LatencyTrace.Applied, (byte)SyncMsg.EntityBundle, item.Bundle.LifeSeq);
				break;
			case SyncMsg.Despawn:
				Despawn(item.NetIds, item.DespawnMode);
				break;
			case SyncMsg.DataRemove:
				RemoveData(item.Removes);
				break;
			}
			_lifecycleApplied++;
		}
		catch (Exception ex)
		{
			_log.Error($"client: applying {item} failed: {ex}");
			Report(ClientPhase.Error, $"{item} failed: {ex.GetType().Name}: {ex.Message}");
			if (item.Type == SyncMsg.EntityBundle && item.Bundle.Kind == 0)
			{
				WorldPartDone(0, $"bundle #{item.Bundle.LifeSeq} FAILED ({ex.GetType().Name})");
			}
		}
	}

	private IStreamedOutBlobs? BlobTarget(Session session)
	{
		try
		{
			return session.TryGet<IStreamedOutBlobs>() ?? session.ExternalServices?.TryGet<IStreamedOutBlobs>();
		}
		catch (Exception)
		{
			return null;
		}
	}

	private void RegisterBlobs(IStreamedOutBlobs? target, string where)
	{
		var store = _session?.Blobs;
		if (store == null)
		{
			return;
		}
		if (target == null)
		{
			_log.WarnOnce("client-no-blob-target", "client: the client game has no IStreamedOutBlobs (" + where + "); planets and asteroids cannot load their voxel data");
			return;
		}
		List<(Guid, byte[])> blobs;
		if (target != _blobTarget)
		{
			_blobTarget = target;
			store.TakeFresh();
			blobs = (from x in store.All()
				select (Id: x.Id, x.Data)).ToList();
		}
		else
		{
			blobs = store.TakeFresh();
		}
		int registered = 0;
		int unavailable = 0;
		long bytes = 0L;
		foreach (var (id, data) in blobs)
		{
			if (data == null)
			{
				unavailable++;
				continue;
			}
			EngineCodec.Register(target, id, data);
			registered++;
			bytes += data.Length;
		}
		_blobsRegistered += registered;
		_blobBytesRegistered += bytes;
		if (registered > 0 || unavailable > 0)
		{
			_log.Info($"client: {registered} offloaded blob(s) ({MpLogExtensions.Bytes(bytes)}) registered with the client game ({where}); {_blobsRegistered} in total ({MpLogExtensions.Bytes(_blobBytesRegistered)})" + (unavailable > 0 ? $"; {unavailable} the host could not load (entities using them will be missing)" : ""));
		}
	}

	private bool BlobsReady(ClientSession session, uint seq, EntityBundleMessage b, long now)
	{
		var gate = session.Blobs.Check(b.RequiredBlobs, now, out var absent);
		if (gate == ClientBlobStore.Gate.Ready)
		{
			if (_gateSeq == seq)
			{
				_log.Info($"client: bundle #{seq}: its {b.RequiredBlobs.Count} offloaded blob(s) arrived after {now - _gateSinceMs} ms");
				_gateSeq = 0u;
			}
			return true;
		}
		if (_gateSeq != seq)
		{
			_gateSeq = seq;
			_gateSinceMs = now;
			session.Blobs.Touch(now);
			_log.Info($"client: holding bundle #{seq} until {absent} of its {b.RequiredBlobs.Count} offloaded blob(s) arrive");
			Core.LatencyTrace.Mark(Core.LatencyTrace.Held, (byte)SyncMsg.EntityBundle, seq, absent);
			return false;
		}
		if (gate == ClientBlobStore.Gate.Stalled)
		{
			_log.Warn($"client: bundle #{seq}: {absent} offloaded blob(s) still missing and no blob data for {session.Blobs.StallMs / 1000} s; " + "building it anyway (entities that need them will be skipped)");
			Report(ClientPhase.Warning, $"bundle #{seq}: {absent} blobs never arrived");
			_gateSeq = 0u;
			return true;
		}
		return false;
	}

	private void ReplicateBundle(EntityBundleMessage m)
	{
		var sw = Stopwatch.StartNew();
		var session = _comp.Session;
		if (_o.BuildFix)
		{
			DataTypeRegistry.EnsureDone(_log);
		}
		RegisterBlobs(BlobTarget(session), $"bundle #{m.LifeSeq}");
		bool world = m.Kind == 0;
		if (world)
		{
			SlowTicks.Note = $"world part {_worldPartsApplied + 1} build ({m.Entities.Count} entities)";
		}
		var excluded = new HashSet<Entity>(ReferenceEqualityComparer.Instance);
		var failures = new List<string>();
		int tries = 1;
		Attempt attempt;
		while (true)
		{
			attempt = TryReplicate(m, excluded, tries, sw);
			if (attempt.Built || attempt.Error == null)
			{
				return;
			}
			failures.Add(attempt.FailureDetail);
			int newlyExcluded = attempt.FailedRoots.Count(r => excluded.Add(r));
			if (tries >= 3 || newlyExcluded == 0)
			{
				break;
			}
			_bundlesRetried++;
			_rootsDropped += newlyExcluded;
			_log.Warn($"client: bundle #{m.LifeSeq}: {attempt.FailureDetail}; building it again without {newlyExcluded} failed root(s)");
			tries++;
		}
		string line = $"bundle #{m.LifeSeq} ({(world ? "world" : "spawn")}, {m.Entities.Count} entities) could not be built after {tries} attempt(s): " + string.Join(" | ", failures);
		_log.Error("client: " + line + $"\n{attempt.Error}");
		Report(ClientPhase.Error, line);
		if (world)
		{
			WorldPartDone(0, $"bundle #{m.LifeSeq} FAILED ({attempt.Error.GetType().Name})");
		}
	}

	private Attempt TryReplicate(EntityBundleMessage m, HashSet<Entity> excludedRoots, int attempt, Stopwatch sw)
	{
		var a = new Attempt();
		var comp = _comp;
		var session = comp.Session;
		var replay = comp.ReplayContext;
		var strategy = _replication.ReplicationStrategy;
		var types = _session.Types;
		bool world = m.Kind == 0;
		double startMs = sw.Elapsed.TotalMilliseconds;
		var proxies = new NetIdProxyContext(null, netId => _proxies.GetOrCreate((uint)netId));
		var referenced = new HashSet<Blob>();
		var bundle = m.Bundle.Length != 0 ? EngineCodec.DeserializeBundle(m.Bundle, proxies, referenced) : new EntityBundle();
		double deserializeMs = sw.Elapsed.TotalMilliseconds - startMs;
		if (attempt == 1 && referenced.Count > 0)
		{
			var missing = referenced.Select(EngineCodec.Id).Where(blobId =>
			{
				var clientSession = _session;
				return clientSession == null || !clientSession.Blobs.Has(blobId);
			}).ToList();
			if (missing.Count > 0)
			{
				_log.Warn($"client: bundle #{m.LifeSeq} references {referenced.Count} offloaded blob(s), {missing.Count} not on this PC (first {missing[0]}; host listed {m.RequiredBlobs.Count}); entities using them will fail to build");
			}
		}
		var builders = new Dictionary<Entity, EntityObjectBuilder>(ReferenceEqualityComparer.Instance);
		foreach (var locator in m.Entities)
		{
			var ob = EngineCodec.Resolve(bundle, locator);
			if (ob == null)
			{
				a.Missing++;
			}
			else
			{
				builders[_proxies.GetOrCreate(locator.NetId)] = ob;
			}
		}
		for (int i = 0; i < bundle.Entities.Count && i < bundle.Builders.Count; i++)
		{
			var proxy = bundle.Entities[i];
			if (proxy != null && !builders.ContainsKey(proxy))
			{
				builders[proxy] = bundle.Builders[i];
			}
		}
		var orphans = new HashSet<Entity>(ReferenceEqualityComparer.Instance);
		foreach (var (child, parent) in m.Parents)
		{
			var parentProxy = _proxies.GetOrCreate(parent);
			if (!_proxies.IsLive(parent) && !builders.ContainsKey(parentProxy))
			{
				orphans.Add(_proxies.GetOrCreate(child));
			}
		}
		for (int i = Math.Min(bundle.Roots, bundle.Entities.Count) - 1; i >= 0; i--)
		{
			var root = bundle.Entities[i];
			if (root != null)
			{
				if (replay.HasPairingRemote(root))
				{
					bundle.RemoveAt(i);
					a.Duplicates++;
				}
				else if (excludedRoots.Contains(root) || orphans.Contains(root))
				{
					bundle.RemoveAt(i);
					a.Excluded++;
				}
			}
		}
		if (orphans.Count > 0 && attempt == 1)
		{
			_log.Warn($"client: bundle #{m.LifeSeq}: {orphans.Count} root(s) skipped, their parent entity is not on this PC");
		}
		if (bundle.Builders.Count == 0 || bundle.Roots == 0)
		{
			string line = $"bundle #{m.LifeSeq}: nothing to build ({a.Duplicates} already present, {a.Excluded} excluded, {a.Missing} unlocated)";
			if (world)
			{
				WorldPartDone(0, line);
			}
			else if (a.Excluded > 0)
			{
				_log.Info("client: " + line);
			}
			return a;
		}
		var proxyOf = new Dictionary<EntityObjectBuilder, Entity>(ReferenceEqualityComparer.Instance);
		foreach (var pair in builders)
		{
			proxyOf[pair.Value] = pair.Key;
		}
		var data = new Dictionary<Entity, DEntityBuilder>(ReferenceEqualityComparer.Instance);
		foreach (var (netId, blobs) in m.Data)
		{
			DEntityBuilder builder = default;
			foreach (var blob in blobs)
			{
				if (blob.Bytes.Length != 0 && types.TryResolve(blob.Key, out var localTypeId, out var size) && blob.Bytes.Length == size)
				{
					builder.Set(localTypeId, in blob.Bytes[0], size);
					a.DataSet++;
				}
				else
				{
					a.DataSkipped++;
				}
			}
			data[_proxies.GetOrCreate(netId)] = builder;
		}
		var staged = new HashSet<Entity>(m.Staged.Select(netId => _proxies.GetOrCreate(netId)), ReferenceEqualityComparer.Instance);
		var skipped = new HashSet<Entity>(m.Skipped.Select(netId => _proxies.GetOrCreate(netId)), ReferenceEqualityComparer.Instance);
		var tctx = new EntityTranslationContext(replay);
		var clientBundle = bundle.CloneBundle(tctx);
		var clientBuilders = new Dictionary<Entity, DEntityBuilder>();
		strategy.TranslateBundle(clientBundle, bundle, tctx, proxyOf, clientBuilders, skipped, data, staged, false);
		var parents = new Dictionary<Entity, Entity>();
		foreach (var (child, parent) in m.Parents)
		{
			var childProxy = _proxies.GetOrCreate(child);
			if (!orphans.Contains(childProxy) && !excludedRoots.Contains(childProxy))
			{
				parents[tctx.AddEntity(childProxy)] = tctx.AddEntity(_proxies.GetOrCreate(parent));
			}
		}
		var pairedProxies = new List<Entity>();
		var paired = new List<uint>();
		var clientToServer = new Dictionary<Entity, Entity>(ReferenceEqualityComparer.Instance);
		foreach (var translation in tctx.Translations)
		{
			var proxy = translation.Key;
			var (clientEntity, translated) = translation.Value;
			clientToServer[clientEntity] = proxy;
			if (!translated && builders.ContainsKey(proxy) && !skipped.Contains(proxy) && !replay.HasPairingRemote(proxy))
			{
				replay.AddObjectPairing(proxy, clientEntity);
				pairedProxies.Add(proxy);
				if (_proxies.TryGetId(proxy, out var id))
				{
					paired.Add(id);
				}
			}
		}
		double translateMs = sw.Elapsed.TotalMilliseconds - startMs - deserializeMs;
		a.Paired = paired.Count;
		a.Summary = $"bundle #{m.LifeSeq} ({(world ? "world" : "spawn")}): {m.Entities.Count} entities sent, {paired.Count} paired, {a.Missing} unlocated, {a.Duplicates} duplicates, {a.Excluded} roots excluded, data {a.DataSet} set / {a.DataSkipped} skipped, {referenced.Count} blobs; deserialize {deserializeMs:0} ms, translate {translateMs:0} ms" + (attempt > 1 ? $", attempt {attempt}" : "");
		// A big pasted or spawned grid (40k blocks took 3.1 s) is built on a worker thread while the game keeps
		// running: Scene.OpenNoSyncSection exists for allocating entities concurrently with Tick, and SE2's own
		// spawner does the same. Only adding it to the scene stays on this thread; later updates wait for it.
		bool background = !world && m.Entities.Count >= BackgroundBuildEntities && parents.Count == 0 && _sessionProxy != null;
		if (_o.BuildFix && parents.Count == 0 && _sessionProxy != null && !background)
		{
			parents[_sessionProxy] = _sessionProxy;
		}
		if (!_dcsLogged)
		{
			_dcsLogged = true;
			bool dcs = false;
			try
			{
				dcs = session.Scene.IsCurrentThreadDCS();
			}
			catch (Exception)
			{
			}
			_log.Info($"client: entity bundles are built on thread '{Thread.CurrentThread.Name}' (scene-synchronized: {(dcs ? "yes" : "no")}); " + (_o.BuildFix ? "roots one by one (0.8.2 build fix)" : "roots in parallel (buildfix off)"));
		}
		var args = new GameEntitySerializer.BundleInitArgs
		{
			Bundle = clientBundle,
			EntityParents = parents,
			EntityBuilders = clientBuilders
		};
		Task<Entity[]> task;
		Entity[] halfAdded = null;
		if (background)
		{
			args.EntityParents = null;
			var job = new BackgroundBuild();
			_backgroundBuild = (job, Finish, Fail, a.Summary, sw);
			BuildOnWorker(session.EntitySerializer, args, job);
			_log.Info($"client: {a.Summary}; building {m.Entities.Count} entities on a worker thread, the game keeps running");
			a.Built = true;
			return a;
		}
		try
		{
			task = session.EntitySerializer.BuildFromBundleAsync(args, allowAsyncSpawn: false);
		}
		catch (Exception ex)
		{
			Fail(ex);
			return a;
		}
		if (!task.IsCompleted)
		{
			_log.Warn("client: " + a.Summary + ": BuildFromBundleAsync did not complete synchronously; adding to scene when it does");
			task.GetAwaiter().OnCompleted(() =>
			{
				try
				{
					Finish(task.GetAwaiter().GetResult());
				}
				catch (Exception ex)
				{
					Fail(ex);
					_log.Error($"client: async {a.Summary} failed and was rolled back: {a.FailureDetail}\n{ex}");
					Report(ClientPhase.Error, $"bundle #{m.LifeSeq} build failed: {a.FailureDetail}");
					if (world)
					{
						WorldPartDone(0, $"bundle #{m.LifeSeq} FAILED ({ex.GetType().Name})");
					}
				}
			});
			a.Built = true;
			return a;
		}
		try
		{
			Finish(task.GetAwaiter().GetResult());
			a.Built = true;
		}
		catch (Exception ex)
		{
			Fail(ex);
		}
		return a;
		void Fail(Exception error)
		{
			a.Error = error;
			int rolledBack = RollBackPairings(replay, pairedProxies, out var firstError);
			if (firstError != null)
			{
				_log.WarnOnce("client-unpair", "client: removing a pairing after a failed bundle threw " + firstError);
			}
			int purged = 0;
			try
			{
				var entities = (from v in tctx.Translations.Values
					where !v.IsTranslated
					select v.Entity).ToList();
				purged = EngineCodec.RemoveFromCloseQueues(session, entities);
				if (purged > 0)
				{
					_purgedDead += purged;
					_log.Warn($"client: bundle #{m.LifeSeq}: {purged} of its unrolled entities were queued for close; removed from the session's close queue");
				}
			}
			catch (Exception ex)
			{
				_log.WarnOnce("client-unqueue", "client: clearing a failed bundle's entities from the close queue threw " + ex.GetType().Name + ": " + ex.Message);
			}
			purged += PurgeDead(session, $"failed bundle #{m.LifeSeq}");
			var initError = Unwrap<GameEntitySerializer.EntityBundleInitException>(error);
			var rootErrors = new List<string>();
			if (initError?.Failures != null && initError.Bundle != null)
			{
				for (int i = 0; i < initError.Failures.Length && i < initError.Bundle.Entities.Count; i++)
				{
					var failure = initError.Failures[i];
					if (failure != null)
					{
						var clientEntity = initError.Bundle.Entities[i];
						if (clientEntity != null && clientToServer.TryGetValue(clientEntity, out Entity serverProxy))
						{
							a.FailedRoots.Add(serverProxy);
						}
						if (rootErrors.Count < 3)
						{
							rootErrors.Add($"{Innermost(failure).GetType().Name} '{Short(Innermost(failure).Message)}' in {Short(failure.Message)}");
						}
					}
				}
			}
			a.FailureDetail = (initError != null ? $"{a.FailedRoots.Count} of {clientBundle.Roots} root(s) failed ({string.Join("; ", rootErrors)})" : (error.GetType().Name + ": " + Short(error.Message))) + $"; rolled back {rolledBack} pairing(s), purged {purged} dead entities";
			if (halfAdded != null)
			{
				a.FailureDetail += $"; closed {CloseHalfAdded(session, halfAdded)} half-added root(s)";
				halfAdded = null;
			}
		}
		void Finish(Entity[] built)
		{
			try
			{
				session.AddEntityBundleToScene(built);
			}
			catch
			{
				halfAdded = built;
				throw;
			}
			foreach (var netId in paired)
			{
				_proxies.MarkLive(netId);
			}
			_entitiesBuilt += built.Length;
			a.BuiltRoots = built.Length;
			string line = $"{a.Summary}; built {built.Length} root entities in {sw.ElapsedMilliseconds} ms total";
			if (world)
			{
				WorldPartDone(paired.Count, line);
			}
			else if (m.Entities.Count >= 20 || _lifecycleApplied < 5 || attempt > 1 || referenced.Count > 0)
			{
				_log.Info("client: " + line);
			}
		}
	}

	internal static int RollBackPairings(ReplayContext replay, IEnumerable<Entity> proxies, out string? firstError)
	{
		int removed = 0;
		firstError = null;
		foreach (Entity proxy in proxies)
		{
			try
			{
				if (replay.HasPairingRemote(proxy))
				{
					replay.RemoveObjectPairing(proxy);
					removed++;
				}
			}
			catch (Exception ex)
			{
				if (firstError == null)
				{
					firstError = ex.GetType().Name + ": " + ex.Message;
				}
			}
		}
		return removed;
	}

	private static T? Unwrap<T>(Exception e) where T : Exception
	{
		for (Exception ex = e; ex != null; ex = ex.InnerException)
		{
			if (ex is T result)
			{
				return result;
			}
			if (ex is AggregateException aggregate && aggregate is not T)
			{
				foreach (var inner in aggregate.InnerExceptions)
				{
					var found = Unwrap<T>(inner);
					if (found != null)
					{
						return found;
					}
				}
			}
		}
		return null;
	}

	private static Exception Innermost(Exception e)
	{
		while (e.InnerException != null)
		{
			e = e.InnerException;
		}
		return e;
	}

	private static string Short(string s)
	{
		if (s.Length > 160)
		{
			return s.Substring(0, 157) + "...";
		}
		return s;
	}

	private int PurgeDead(Session session, string where)
	{
		try
		{
			int purged = EngineCodec.PurgeDeadFromCloseQueues(session, out var sample);
			if (purged > 0)
			{
				_purgedDead += purged;
				_log.Warn($"client: dropped {purged} dead entit{(purged == 1 ? "y" : "ies")} from the session's close queue ({where}; first: {sample}); " + "Session.Update would have crashed on them");
			}
			return purged;
		}
		catch (Exception ex)
		{
			_log.WarnOnce("client-purge", $"client: checking the session's close queue failed ({where}): {ex.GetType().Name}: {ex.Message}");
			return 0;
		}
	}

	public static void SessionUpdatePrefix(Session __instance, bool doEntityLifetimeUpdates)
	{
		var current = Current;
		var comp = current?._comp;
		if (comp == null || !doEntityLifetimeUpdates)
		{
			return;
		}
		try
		{
			if (comp.Session == __instance)
			{
				SlowTicks.Begin();
				current.PurgeDead(__instance, "Session.Update");
			}
		}
		catch (Exception ex)
		{
			current._log.WarnOnce("client-session-update", "client: Session.Update prefix failed: " + ex.GetType().Name + ": " + ex.Message);
		}
	}

	public static void SessionUpdatePostfix()
	{
		try
		{
			SlowTicks.End();
		}
		catch (Exception)
		{
			SlowTicks.Enabled = false;
		}
	}

	private void WorldPartDone(int entities, string detail)
	{
		_worldPartsApplied++;
		_worldEntitiesBuilt += entities;
		Progress.PartBuilt(entities);
		int parts = Math.Max(1, _session?.Accept?.WorldParts ?? 1);
		_log.Info($"client: world part {_worldPartsApplied}/{parts}: {detail}");
		Report(ClientPhase.Progress, $"world part {_worldPartsApplied}/{parts}: {detail}");
		if (_worldPartsApplied >= parts && !_worldApplied)
		{
			_worldApplied = true;
			string line = $"WORLD APPLIED: {_worldEntitiesBuilt} entities paired in {parts} part(s), {_proxies.LiveCount} live NetworkIds";
			_log.Info("client: " + line);
			Report(ClientPhase.WorldApplied, line);
			Progress.Set(_o.Character ? JoinStage.WaitingForCharacter : JoinStage.Playing, MpClock.NowMs(), _o.Character ? null : "Free camera (no character).");
		}
	}

	private void Despawn(List<uint> ids, byte mode)
	{
		var comp = _comp;
		var replay = comp.ReplayContext;
		foreach (uint id in ids)
		{
			if (!_proxies.TryGet(id, out Entity e))
			{
				_despawnSkipped++;
				continue;
			}
			_session?.Smoother.Remove(id);
			if (id == _ownNetId)
			{
				ClearOwnCharacter(mode == 0 ? "removed on the host" : "forgotten by the host");
			}
			if (mode == 0)
			{
				_session?.State.Despawn(id, MpClock.NowMs());
				bool wasLive = _proxies.IsLive(id);
				_proxies.MarkDead(id);
				if (!wasLive || !replay.HasPairingRemote(e))
				{
					_despawnSkipped++;
					continue;
				}
				bool skip = false;
				if (replay.TranslateObject(e, ReplicationFailureStrategy.NullOnAnyFail, ref skip) is not Entity entity || EngineCodec.IsDead(entity) || EngineCodec.IsClosing(comp.Session, entity))
				{
					_despawnSkipped++;
					continue;
				}
				try
				{
					comp.Session.MarkEntityForClose(entity);
				}
				catch (Exception ex)
				{
					_log.WarnOnce("client-despawn", $"client: closing despawned entity (NetworkId {id}) threw {ex.GetType().Name}: {ex.Message}");
				}
				continue;
			}
			try
			{
				if (replay.HasPairingRemote(e))
				{
					replay.RemoveObjectPairing(e);
				}
			}
			catch (Exception ex)
			{
				_log.WarnOnce("client-forget", $"client: forgetting NetworkId {id} threw {ex.GetType().Name}: {ex.Message}");
			}
			_proxies.Forget(id);
		}
	}

	private void RemoveData(List<(uint NetId, ushort Key)> items)
	{
		var replay = _comp.ReplayContext;
		var types = _session.Types;
		foreach (var (id, key) in items)
		{
			if (_proxies.IsLive(id) && _proxies.TryGet(id, out Entity e) && types.TryResolve(key, out var localTypeId, out _))
			{
				bool skip = false;
				if (replay.TranslateObject(e, ReplicationFailureStrategy.NullOnAnyFail, ref skip) is Entity entity && !EngineCodec.IsDead(entity))
				{
					entity.Scene.TryRemoveData(entity.Data, localTypeId);
				}
			}
		}
	}

	private void Spectate()
	{
		if (_ownCharacter != null)
		{
			_log.Info("client: free camera not needed any more (own character possessed)");
			return;
		}
		try
		{
			var session = _comp.Session;
			var serverOb = (Singleton<DefinitionManager>.Instance.GetDefinitionsOfType<SpectatingModesComponentDefinition>().FirstOrDefault() ?? throw new InvalidOperationException("no SpectatingModesComponentDefinition")).SpectatorPrefab.Get();
			var clientOb = _replication.ReplicationStrategy.GetClientObjectBuilder(serverOb);
			var players = session.SessionComponents.TryGet<ClientPlayersSessionComponent>();
			if (players != null)
			{
				OwnershipSessionComponent.ChangeOwnership(clientOb, players.LocalPlayerIdentity);
			}
			string where = "at the origin";
			var characters = new List<Entity>();
			if (session.TryFillAliveCharactersHotfix(characters, preserveOrder: true) && characters.Count > 0 && characters[0].Data.TryGet<WorldTransform>(out var data))
			{
				var position = data.Position + Vector3D.Up * 3.0 + Vector3D.Backward * 6.0;
				clientOb.OB<WorldTransformComponent, WorldTransformComponentObjectBuilder>().Transform = new WorldTransform(position, data.Orientation);
				where = $"next to character {characters[0]} ({characters.Count} characters in the client world)";
			}
			var spectator = _spectator = (session.SessionComponents.TryGet<SimpleEntitySpawnerSessionComponent>() ?? throw new InvalidOperationException("no entity spawner in the client session")).SpawnEntity(clientOb);
			var spectating = session.SessionComponents.TryGet<SpectatingModesSessionComponent>();
			spectating?.SetEntityTypeToPossess(InitialEntityOptions.Spectator);
			string line = $"free camera: local spectator {spectator} spawned {where}; possession requested via {(spectating != null ? "SpectatingModesSessionComponent" : "nothing (component missing)")}";
			_log.Info("client: " + line);
			Report(ClientPhase.CameraReady, line);
			if (Progress.Stage is JoinStage.Building or JoinStage.WaitingForCharacter)
			{
				Progress.Set(JoinStage.Playing, MpClock.NowMs(), "No character from the host: free camera.");
			}
		}
		catch (Exception ex)
		{
			_log.Error($"client: free camera failed (world is still synced; camera may stay at the origin): {ex}");
			Report(ClientPhase.Warning, "free camera failed: " + ex.GetType().Name + ": " + ex.Message);
		}
	}

	private void LookBegin(uint netId)
	{
		if (_o.Predict && !_lookFailed)
		{
			_look ??= new LookPredictor(_log);
			_look.Begin(netId, MpClock.NowMs());
			_deckFrame = false;
			_look.ExtraLagMs = 0.0;
			_fastUntilMs = 0L;
			_lookDirty = false;
			_lookLastSent = default;
			_body ??= new OwnBodyFilter(_log);
			_body.Enabled = _o.BodyFilter;
			_body.Reset();
			(_move ??= new MovePredictor()).Reset();
			_movePhys = null;
			_lastWritten = null;
			_bodyWhy = null;
		}
	}

	private void NoteBodyWhy(string what)
	{
		_bodyWhy = _bodyWhy == null ? what : _bodyWhy + "; " + what;
	}

	private void NoteOwnWrittenElsewhere(uint netId, byte[] value, long now)
	{
		if (_body != null && netId != 0 && netId == _ownNetId && value.Length == 40)
		{
			var (x, y, z, quaternion) = LookMath.ReadTransform(value);
			if (_deckFrame)
			{
				quaternion = System.Numerics.Quaternion.Conjugate(_deckQ) * quaternion;
			}
			_body.NoteShown(now, x, y, z, quaternion);
			_lastWritten = null;
		}
	}

	private void CheckOwnSecondWriter(Entity own, long now)
	{
		byte[] lastWritten = _lastWritten;
		if (lastWritten == null || now - _lastWrittenMs > 200 || !own.Data.TryGet<WorldTransform>(out var data))
		{
			return;
		}
		byte[] shown = new byte[40];
		Unsafe.WriteUnaligned(ref shown[0], data);
		var (sx, sy, sz, shownRot) = LookMath.ReadTransform(shown);
		var (wx, wy, wz, writtenRot) = LookMath.ReadTransform(lastWritten);
		double offM = Math.Sqrt((sx - wx) * (sx - wx) + (sy - wy) * (sy - wy) + (sz - wz) * (sz - wz));
		float offDeg = LookMath.AngleDeg(shownRot, writtenRot);
		if (!(offM < 0.05) || !(offDeg < 1f))
		{
			_otherWriterCount++;
			if (offM > _otherWriterMaxM)
			{
				_otherWriterMaxM = offM;
			}
			if (now - _otherWriterLogMs < 2000)
			{
				_otherWriterSuppressed++;
				return;
			}
			_log.Info($"client: own body: the game shows a transform Y51_SE2Online did not write ({offM:0.00} m / {offDeg:0.0} deg off the one written {now - _lastWrittenMs} ms ago): a second writer (Keen's replay of host data / client physics){(_otherWriterSuppressed > 0 ? $" (+{_otherWriterSuppressed} more)" : "")}");
			_otherWriterSuppressed = 0L;
			_otherWriterLogMs = now;
		}
	}

	private void LookEnd(string why)
	{
		_look?.End(why);
		_move?.Reset();
		_ownSmoothed = false;
	}

	private void LookTick(ClientSession session, long now)
	{
		var look = _look;
		var ownCharacter = _ownCharacter;
		if (look == null || _lookFailed || ownCharacter == null || look.NetId == 0 || look.NetId != _ownNetId)
		{
			return;
		}
		try
		{
			CheckOwnSecondWriter(ownCharacter, now);
			_ownVel = ownCharacter.Data.TryGet<RigidBodyData>(out var data) ? new System.Numerics.Vector3(data.LinearVelocity.X, data.LinearVelocity.Y, data.LinearVelocity.Z) : null;
			if (!_ownVelLogged)
			{
				_ownVelLogged = true;
				_log.Info("client: own-body blend " + (_o.BodyFilter ? "on" : "off (-y51online:bodyfilter:off)") + ": velocity from " + (_ownVel.HasValue ? "the character's RigidBodyData" : "the host samples (no RigidBodyData on the own character)") + "; fast-jetpack smoothing " + (_o.FastSmooth ? "ON (-y51online:fastsmooth:on)" : "off (0.8.2 behaviour)") + ", deck frame " + (_o.DeckFrame ? "on" : "off"));
			}
			// Host samples are stamped at the end of the tick the input was applied in, the ack at its start.
			look.MeasuredLagMs = session.InputRttMs >= 0 ? session.InputRttMs + 8.0 : null;
			LookMode mode = LookModeOf(ownCharacter, out string detail);
			if (mode != look.Mode)
			{
				NoteBodyWhy($"movement mode {look.Mode} -> {mode}");
			}
			look.SetMode(mode, now, detail);
			bool seated = mode == LookMode.Off && detail.StartsWith("controlling", StringComparison.Ordinal);
			bool onDeck = mode == LookMode.Ground && OnMovingGrid(ownCharacter, session, now);
			bool fastFlight = _o.FastSmooth && mode == LookMode.Jetpack && FastOwnFlight(ownCharacter, now);
			bool smoothed = seated || onDeck || fastFlight;
			if (look.Snapped != _deckSnapsSeen)
			{
				if (_deckFrame)
				{
					if (now - _deckSnapWindowMs > 3000) { _deckSnapWindowMs = now; _deckSnaps = 0; }
					_deckSnaps += look.Snapped - _deckSnapsSeen;
					if (_deckSnaps >= 2)
					{
						_deckFrameOffUntil = now + 30000;
						_deckSnaps = 0;
						_log.Info("client: look prediction keeps snapping in the deck's frame; predicting in the world frame for 30 s");
					}
				}
				_deckSnapsSeen = look.Snapped;
			}
			bool deckFrame = onDeck && session.Smoother.Enabled && _o.DeckFrame && now >= _deckFrameOffUntil;
			if (deckFrame != _deckFrame)
			{
				_deckFrame = deckFrame;
				var delta = deckFrame ? System.Numerics.Quaternion.Conjugate(_deckQ) : _deckQ;
				look.Rebase(delta);
				_body?.Rebase(delta);
				NoteBodyWhy(deckFrame ? "prediction now in the deck's frame" : "prediction back in the world frame");
			}
			if (smoothed != _ownSmoothed && session.Smoother.Enabled)
			{
				_ownSmoothed = smoothed;
				NoteBodyWhy(smoothed ? "own body source: raw -> smoothed timeline" : "own body source: smoothed timeline -> raw");
				session.Smoother.SetRaw(look.NetId, !smoothed);
				if (_ownSmoothedLogged++ < 12)
				{
					_log.Info(seated ? "client: seated: the own character follows the ship's smoothed timeline (no shaking in the seat)"
						: onDeck ? "client: standing on a moving grid: the own character follows that grid's smoothed timeline (no sliding on the deck)"
						: fastFlight ? "client: fast jetpack flight: the own character follows the smoothed timeline (no shaking at speed)"
						: "client: on foot again: the own character is shown raw (look prediction)");
				}
			}
			look.ExtraLagMs = (_deckFrame || (_o.FastSmooth && fastFlight && _ownSmoothed)) && session.Smoother.Enabled ? session.Smoother.CurrentDelayMs : 0.0;
			var body = _body;
			if (body != null && body.Enabled && mode != LookMode.Off && look.Intercepts)
			{
				_lookDirty = true;
			}
			if (mode == LookMode.Off)
			{
				if (look.Intercepts)
				{
					_lookDirty = true;
				}
			}
			else
			{
				var rotation = ownCharacter.Data.TryGet<ControlData>(out var control) ? new System.Numerics.Vector3(control.Rotation.X, control.Rotation.Y, control.Rotation.Z) : System.Numerics.Vector3.Zero;
				float maxTorque = ownCharacter.Data.TryGet<CharacterMovementComponent.JetpackModeData>(out var jetpack) && jetpack.MaxTorque > 0f ? jetpack.MaxTorque : 65f;
				var centerOfMass = ownCharacter.Data.TryGet<RigidBodyMassProperties>(out var mass) ? new System.Numerics.Vector3(mass.CenterOfMass.X, mass.CenterOfMass.Y, mass.CenterOfMass.Z) : System.Numerics.Vector3.Zero;
				if (look.Step(now, rotation, maxTorque, centerOfMass))
				{
					_lookDirty = true;
				}
			}
			MoveStep(ownCharacter, mode, look, now);
			if (_move != null && _move.Active && look.Intercepts)
			{
				_lookDirty = true;
			}
			(uint, ushort, double, double, double) totals = (look.NetId, look.Epoch, look.TotalX, look.TotalY, look.TotalZ);
			if (totals != _lookLastSent || now - _lookSentMs >= 100)
			{
				session.SendLook(look.NetId, look.Epoch, look.TotalX, look.TotalY, look.TotalZ);
				_lookLastSent = totals;
				_lookSentMs = now;
			}
		}
		catch (Exception e)
		{
			LookFail(e);
		}
	}

	private void MoveStep(Entity own, LookMode mode, LookPredictor look, long now)
	{
		var move = _move;
		if (move == null)
		{
			return;
		}
		if (_ownSmoothed || mode == LookMode.Off || look.Disabled || (look.LagSamples == 0 && !look.MeasuredLagMs.HasValue))
		{
			move.Reset();
			return;
		}
		var hostVel = _ownVel.GetValueOrDefault();
		if (mode == LookMode.Jetpack && JetInputs(own, look.Shown) is var (thrust, brake, limit))
		{
			if (_jetLogged < 3 && thrust.LengthSquared() > 0.01f)
			{
				_jetLogged++;
				own.Data.TryGet<ControlData>(out var jc);
				_log.Info($"client: jetpack lead models the inputs: thrust {thrust.Length():0.00} m/s^2 for input {jc.Movement.X:0.##},{jc.Movement.Y:0.##},{jc.Movement.Z:0.##}, dampener braking {brake:0.0} m/s^2, speed limit {limit:0} m/s");
			}
			move.StepJet(now, hostVel, thrust, brake, limit);
			return;
		}
		if (mode != LookMode.Ground || !own.Data.TryGet<ControlData>(out var control) || !own.Data.TryGet<CharacterMovementComponent.GroundModeData>(out var ground))
		{
			move.StepFree(now, hostVel);
			return;
		}
		var phys = _movePhys ??= own.Components.OfType<Keen.VRage.Physics.Components.CharacterPhysicsComponent>().FirstOrDefault();
		bool sprint = own.Data.TryGet<AdvancedCharacterControlData>(out var advanced) && advanced.IsSprinting;
		bool crouch = own.Data.TryGet<CharacterCrouchTag>(out _);
		// Sprint and crouch are requests the host answers: its state reaches us a round trip after the key, and the
		// model ran at the old speed for that long, a lead error that snapped back (0.25-0.45 m jumps while walking).
		// For one round trip after a request the model uses what was pressed; then the host's state rules again
		// (stamina, no room to stand up).
		double window = look.LagMs + 250.0;
		if (_sprintIntentMs != 0 && now - _sprintIntentMs < window) sprint = _sprintIntent;
		if (_crouchToggleMs != _crouchToggleSeen)
		{
			_crouchToggleSeen = _crouchToggleMs;
			_crouchIntent = !crouch;
		}
		if (_crouchToggleMs != 0 && now - _crouchToggleMs < window) crouch = _crouchIntent;
		var shown = look.Shown;
		var up = System.Numerics.Vector3.Transform(LookMath.Up, shown);
		var hostVertical = up * System.Numerics.Vector3.Dot(hostVel, up);
		var target = MovePredictor.GroundVelocity(shown, control.Movement.X, control.Movement.Z, sprint, crouch, ground.NonSprintingSpeedMultiplier, ground.CrouchSpeedMultiplier, phys?.CharacterPhysicsDefinition.BaseGroundVelocity ?? 10f);
		(float, float, bool, bool) input = (control.Movement.X, control.Movement.Z, sprint, crouch);
		move.StepGround(now, target, hostVel - hostVertical, hostVertical, phys?.CharacterPhysicsDefinition.MaxAcceleration ?? 200f, input != _moveInput);
		_moveInput = input;
	}

	// This frame's jetpack input as an acceleration (the character's own regular thrust per direction over its mass),
	// the dampeners' braking deceleration (0 when off) and the soft speed limit.
	private static (System.Numerics.Vector3 Thrust, float Brake, float Limit)? JetInputs(Entity own, System.Numerics.Quaternion shown)
	{
		if (!own.Data.TryGet<ControlData>(out var control) || !own.Data.TryGet<Keen.Game2.Simulation.WorldObjects.Shared.Movement.MaxThrustData>(out var thrust)
			|| !own.Data.TryGet<RigidBodyMassProperties>(out var mass) || !(mass.Mass > 0f))
		{
			return null;
		}
		var m = control.Movement;
		var regular = thrust.Regular;
		float Axis(float input, float positive, float negative) => input >= 0f ? input * positive : input * negative;
		var local = new System.Numerics.Vector3(Axis(m.X, regular.Positive.X, regular.Negative.X), Axis(m.Y, regular.Positive.Y, regular.Negative.Y), Axis(m.Z, regular.Positive.Z, regular.Negative.Z)) / mass.Mass;
		var braking = thrust.Braking;
		float brake = own.Data.TryGet<DampeningData>(out _) ? (braking.Positive.X + braking.Positive.Y + braking.Positive.Z + braking.Negative.X + braking.Negative.Y + braking.Negative.Z) / 6f / mass.Mass : 0f;
		float limit = own.Data.TryGet<SoftSpeedLimitData>(out var soft) ? soft.Speed : 0f;
		return (System.Numerics.Vector3.Transform(local, shown), brake, limit);
	}

	private bool FastOwnFlight(Entity own, long now)
	{
		if (own.Data.TryGet<RigidBodyData>(out var data))
		{
			var v = data.LinearVelocity;
			double speed = Math.Sqrt((double)v.X * (double)v.X + (double)v.Y * (double)v.Y + (double)v.Z * (double)v.Z);
			bool wasFast = now < _fastUntilMs;
			if (speed >= 15.0 || (wasFast && speed >= 8.0))
			{
				_fastUntilMs = now + 750;
			}
		}
		return now < _fastUntilMs;
	}

	private void NoteDeckSample(List<(uint NetId, byte[] Value)> samples)
	{
		if (_deckRootId == 0)
		{
			return;
		}
		foreach (var (netId, value) in samples)
		{
			if (netId == _deckRootId && value.Length == 40)
			{
				var transform = LookMath.ReadTransform(value);
				_deckQ = System.Numerics.Quaternion.Normalize(transform.O);
				CheckDeckContinuity(netId, transform.X, transform.Y, transform.Z);
				break;
			}
		}
	}

	private void CheckDeckContinuity(uint id, double x, double y, double z)
	{
		long now = MpClock.NowMs();
		double dt = (now - _deckCheckMs) / 1000.0;
		if (id == _deckCheckId && dt > 0.002 && dt < 0.2)
		{
			double px = _dx + _dvx * dt;
			double py = _dy + _dvy * dt;
			double pz = _dz + _dvz * dt;
			double jump = Math.Sqrt((x - px) * (x - px) + (y - py) * (y - py) + (z - pz) * (z - pz));
			double speed = Math.Sqrt(_dvx * _dvx + _dvy * _dvy + _dvz * _dvz);
			if (jump > 0.3 + 0.05 * speed * dt * 60.0)
			{
				_deckJumps++;
				if (now - _deckJumpLogMs > 2000)
				{
					_deckJumpLogMs = now;
					_log.Info($"client: deck NetworkId {id}: smoothed track jumped {jump:0.00} m against its velocity ({speed:0} m/s) this frame (diagnostic, {_deckJumps} so far; {_session?.Smoother.Describe()})");
				}
			}
			_dvx = (x - _dx) / dt;
			_dvy = (y - _dy) / dt;
			_dvz = (z - _dz) / dt;
		}
		else
		{
			_dvx = _dvy = _dvz = 0.0;
		}
		_deckCheckId = id;
		_deckCheckMs = now;
		_dx = x;
		_dy = y;
		_dz = z;
	}

	private bool OnMovingGrid(Entity own, ClientSession session, long now)
	{
		var support = own.TryGet<CharacterMovementEffectsComponent>()?._supportingEntity?.Entity;
		if (support != null && !EngineCodec.IsDead(support))
		{
			var root = support;
			for (int i = 0; i < 16; i++)
			{
				var parent = root.TryGet<HierarchyComponent>()?.Parent;
				if (parent == null)
				{
					break;
				}
				root = parent;
			}
			if (NetIdOf(root) is uint rootId)
			{
				if (session.Smoother.SpeedOf(rootId) > 1.0 || session.Smoother.TurnRateOf(rootId) > 1.0)
				{
					_deckUntilMs = now + 500;
					_deckRootId = rootId;
				}
			}
		}
		return now < _deckUntilMs;
	}

	private bool LookTakeSmoothed(uint netId, byte[] value, long now)
	{
		var look = _look;
		if (look == null || _lookFailed || !_ownSmoothed || netId == 0 || netId != look.NetId || !look.Intercepts || value.Length != 40)
		{
			return false;
		}
		try
		{
			var (x, y, z, quaternion) = LookMath.ReadTransform(value);
			if (_deckFrame)
			{
				quaternion = System.Numerics.Quaternion.Conjugate(_deckQ) * quaternion;
			}
			look.OnHost(now, x, y, z, quaternion);
			_lookDirty = true;
			_lookIntercepted++;
			return true;
		}
		catch (Exception e)
		{
			LookFail(e);
			return false;
		}
	}

	private LookMode LookModeOf(Entity own, out string detail)
	{
		var movement = own.TryGet<CharacterMovementComponent>();
		if (movement == null)
		{
			detail = "the character has no CharacterMovementComponent here";
			return LookMode.Off;
		}
		// The secondary actor of a two-character emote is parented to the other character by the host; predicting it
		// locally would pull it out of the scene.
		if (FightLock.Active || own.Data.TryGet<Keen.VRage.Core.Game.Components.EntityParentData>(out _))
		{
			detail = "in a two-character emote";
			return LookMode.Off;
		}
		var controller = _controller;
		if (controller != null)
		{
			foreach (var controlledEntity in controller.ControlledEntities)
			{
				if (controlledEntity.Controllable is Component component && !IsSelfOrBelow(component.Entity, own))
				{
					SeatDiagnostics(component.Entity, own);
					detail = "controlling " + component.GetType().Name;
					return LookMode.Off;
				}
			}
		}
		var state = movement.CurrentMovementState;
		detail = $"movement state {state}";
		return state switch
		{
			CharacterMovementState.Ground => LookMode.Ground,
			CharacterMovementState.Jetpack => LookMode.Jetpack,
			_ => LookMode.Off,
		};
	}

	private void SeatDiagnostics(Entity controlled, Entity own)
	{
		if (controlled == _seatDiagLast || _seatDiagCount >= 20)
		{
			return;
		}
		_seatDiagLast = controlled;
		_seatDiagCount++;
		try
		{
			var seat = controlled.TryGet<SeatComponent>();
			var block = controlled.TryGet<CubeBlockComponent>();
			string pilot;
			if (seat == null)
			{
				pilot = "no SeatComponent";
			}
			else if (seat.Pilot == null)
			{
				pilot = "Pilot NULL";
			}
			else
			{
				pilot = seat.Pilot == own ? "Pilot = own character" : $"Pilot = {seat.Pilot}";
			}
			string grid;
			if (block == null)
			{
				grid = "no CubeBlockComponent";
			}
			else
			{
				grid = block.Grid == null ? "CubeBlockComponent.Grid NULL" : $"grid {block.Grid.Entity}";
			}
			_log.Info($"client: seated in {controlled} ({pilot}; {grid}) - the control panel from this seat needs both (diagnostic)");
		}
		catch (Exception ex)
		{
			_log.Info("client: seat diagnostic failed: " + ex.GetType().Name + ": " + ex.Message);
		}
	}

	private static bool IsSelfOrBelow(Entity e, Entity own)
	{
		var current = e;
		for (int i = 0; i < 8; i++)
		{
			if (current == null)
			{
				break;
			}
			if (current == own)
			{
				return true;
			}
			current = current.TryGet<HierarchyComponent>()?.Parent;
		}
		return false;
	}

	private bool LookIntercept(ClientSession session, StateEntry e, long now)
	{
		var look = _look;
		if (look == null || _lookFailed || e.NetId == 0 || e.NetId != look.NetId || !look.Intercepts || e.Bytes.Length != 40 || !session.IsTransformKey(e.Key))
		{
			return false;
		}
		try
		{
			var (x, y, z, o) = LookMath.ReadTransform(e.Bytes);
			if (look.OnHost(now, x, y, z, o))
			{
				_lookDirty = true;
			}
			_ownHostLocalMs = session.Smoother.LocalOf(e.HostMs);
			var move = _move;
			if (move != null)
			{
				move.OnHost(now, e.HostMs);
				if (look.Mode == LookMode.Ground && _ownVel.HasValue)
				{
					var hostVel = _ownVel.GetValueOrDefault();
					var up = System.Numerics.Vector3.Transform(LookMath.Up, look.Shown);
					move.Observe(hostVel - up * System.Numerics.Vector3.Dot(hostVel, up), look.LagMs);
				}
			}
			_lookIntercepted++;
			return true;
		}
		catch (Exception ex)
		{
			LookFail(ex);
			return false;
		}
	}

	private void LookWrite(ClientSession session, VRage.Multiplayer.Replication.CB.ReplicationCommandBufferWriterReader w, int baseIndex, ref int cmds)
	{
		var look = _look;
		if (look == null || _lookFailed || !_lookDirty)
		{
			return;
		}
		_lookDirty = false;
		try
		{
			if (Ragdolling(look.NetId, MpClock.NowMs()))
			{
				RagdollWritesSkipped++;
				return;
			}
			byte[] transform = look.Display();
			if (transform == null || !session.TryGetTransformType(out var typeId, out var size) || size != transform.Length || !_proxies.IsLive(look.NetId) || !_proxies.TryGet(look.NetId, out Entity e))
			{
				return;
			}
			string lastEvent = look.LastEvent;
			if (lastEvent != null)
			{
				NoteBodyWhy("prediction " + lastEvent);
				look.LastEvent = null;
			}
			var move = _move;
			bool leading = move != null && move.Active && !_ownSmoothed;
			if (_body != null || _deckFrame || leading)
			{
				var (x, y, z, quaternion) = LookMath.ReadTransform(transform);
				System.Numerics.Vector3 leadShown = default;
				double ageShown = -1;
				if (leading)
				{
					// A server stall pauses the host's simulation and never catches up: predicting through it put the body
					// ahead of where the host resumed, and it was pulled back (the pop). Past 100 ms without a sample the lead
					// stops growing, so the body waits with the server and carries on from there.
					long leadNow = MpClock.NowMs();
					if (_ownHostLocalMs is double sampleAt && leadNow - sampleAt > StallLeadMs) leadNow = (long)(sampleAt + StallLeadMs);
					var lead = move.Lead(leadNow, look.LagMs);
					OwnLeadProbe?.Invoke(leadNow, x, y, z, lead, look.LagMs);
					leadShown = lead;
					if (_ownHostLocalMs is double sampleLocal) ageShown = MpClock.NowMs() - sampleLocal;
					x += lead.X;
					y += lead.Y;
					z += lead.Z;
				}
				else if (!_ownSmoothed && !_deckFrame && _ownVel is System.Numerics.Vector3 hostVel && _ownHostLocalMs is double hostLocal)
				{
					// Nothing local predicts this motion (drifting, pushed, carried): carry the newest host sample to now
					// along the host's velocity, or the body stands still on frames without a sample and doubles up after.
					// Never with the input lead: its window already runs to now, and adding the sample's age on top
					// wobbled the body by speed x 0-16 ms (0.8 m at 100 m/s).
					double age = Math.Clamp(MpClock.NowMs() - hostLocal, 0.0, 100.0) / 1000.0;
					ageShown = age * 1000.0;
					double seconds = age;
					x += hostVel.X * seconds;
					y += hostVel.Y * seconds;
					z += hostVel.Z * seconds;
				}
				if (_body != null)
				{
					string source;
					if (_ownSmoothed)
					{
						source = _deckFrame ? "deck's smoothed timeline" : "smoothed timeline";
					}
					else
					{
						source = leading ? "host's newest sample + movement lead" : "host's newest sample (raw)";
					}
					long bigBefore = _body.BlendedBig;
					(x, y, z, quaternion) = _body.Filter(MpClock.NowMs(), x, y, z, quaternion, leading ? move.Velocity : _ownVel, look.LastStep, source, _bodyWhy, _ownSmoothed);
					_bodyWhy = null;
					long nowMs = MpClock.NowMs();
					if (_body.BlendedBig != bigBefore && nowMs - _bodyDetailMs >= 1000)
					{
						// What the jump was made of, so rubberbanding in a real session can be traced to its cause.
						_bodyDetailMs = nowMs;
						var hostSpeed = _ownVel is System.Numerics.Vector3 hv ? hv.Length() : -1f;
						_log.Info($"client: own body jump detail: lead {leadShown.Length():0.00} m ({(look.MeasuredLagMs.HasValue ? "measured" : "estimated")} lag {look.LagMs:0} ms), newest host sample {ageShown:0} ms old, model speed {(move?.Velocity.Length() ?? 0f):0.0} m/s, host speed {hostSpeed:0.0} m/s, last input applied by the host {(session.InputAck.Seq == 0 ? "(no ack yet)" : $"#{session.InputAck.Seq}, {session.InputAck.RoundTripMs} ms from sending to its ack (worst of the last 5 s {session.InputAckWorstMs} ms)")}, mode {look.Mode}");
					}
				}
				transform = LookMath.WriteTransform(x, y, z, System.Numerics.Quaternion.Normalize(_deckFrame ? _deckQ * quaternion : quaternion));
			}
			w.Write_Keen_VRage_Multiplayer_Replications_InProcess_InProcessReplicationSessionComponent_PostDataSignal(baseIndex + 1, _sessionProxy, e, new InProcessReplicationSessionComponent.InProcessPayload(typeId, (ReadOnlySpan<byte>)transform));
			cmds++;
			_lastWritten = transform;
			_lastWrittenMs = MpClock.NowMs();
		}
		catch (Exception ex)
		{
			LookFail(ex);
		}
	}

	private void LookFail(Exception e)
	{
		if (!_lookFailed)
		{
			_lookFailed = true;
			_log.Error($"client: look prediction failed and is switched off (the host's orientation is applied as is from now on): {e}");
			Report(ClientPhase.Warning, "look prediction failed: " + e.GetType().Name + ": " + e.Message);
		}
	}

	private string LookDescribe()
	{
		if (_o.Predict)
		{
			if (!_lookFailed)
			{
				if (_look != null)
				{
					return $"{_look.Describe()}, host transforms taken {_lookIntercepted}, totals sent {_session?.LookSent ?? 0}, movement lead {(_move != null && _move.Active ? $"on (wall gain {_move.Gain:0.00}, dampener braking x{_move.BrakeScale:0.00})" : "off")}, input round trip {_session?.InputAck.RoundTripMs ?? 0} ms (worst of 5 s {_session?.InputAckWorstMs ?? 0} ms)" + (_body != null ? $"; {_body.Describe()}; second writer on the own body {_otherWriterCount}x (largest {_otherWriterMaxM:0.00} m)" : "");
				}
				return "look prediction: no character yet";
			}
			return "look prediction FAILED (off)";
		}
		return "look prediction off (-y51online:predict:off)";
	}

	private void UpdateNametags(InProcessReplicationSessionComponent comp)
	{
		var session = _session;
		if (session == null || !_worldApplied || !NametagOverlay.Enabled)
		{
			return;
		}
		try
		{
			long changes = session.Names.Changes;
			if (changes != _tagChanges || _tagTick++ % 30 == 0)
			{
				_tagChanges = changes;
				_tagList.Clear();
				foreach (var tag in session.Names.Others(session.ClientId))
				{
					if (tag.NetId != _ownNetId)
					{
						Entity target = null;
						if (_proxies.IsLive(tag.NetId) && _proxies.TryGet(tag.NetId, out Entity e))
						{
							bool skip = false;
							target = comp.ReplayContext.TranslateObject(e, ReplicationFailureStrategy.NullOnAnyFail, ref skip) as Entity;
						}
						_tagList.Add((tag.ClientId, tag.Name, target));
					}
				}
			}
			(_tagOverlay ??= new NametagOverlay(_log, "client")).Update(comp.Session, comp.Entity, _tagList);
		}
		catch (Exception ex)
		{
			_log.WarnOnce("client-nametags", $"nametags(client): update failed ({ex.GetType().Name}: {ex.Message})");
		}
	}

	public void OnConnectionLost(string reason)
	{
		if (!(_left || _retired || _lostReason != null || Progress.Stage == JoinStage.Rejected))
		{
			_lostReason = string.IsNullOrWhiteSpace(reason) ? "connection closed" : reason;
		}
	}

	public static void ConnectionLost(ClientSession session, string reason)
	{
		var current = Current;
		if (current != null && current._session == session)
		{
			current.OnConnectionLost(reason);
		}
	}

	public void AskRejoin(string why)
	{
		_rejoinAsk = why;
	}

	internal bool MenuStep(bool atMenu, bool transitioning, bool clientUp, long now, Action? returnToMenu)
	{
		if (_retired)
		{
			return true;
		}
		if (Progress.Stage == JoinStage.Rejected && clientUp)
		{
			// Kicked or banned while playing: leave the host's world without trying to rejoin.
			if (!transitioning && !_menuReturnIssued)
			{
				_menuReturnIssued = true;
				_log.Info("client: removed by the host; returning to the main menu");
				returnToMenu?.Invoke();
			}
			return true;
		}
		string lostReason = _lostReason;
		if (lostReason != null && !_left)
		{
			if (!_lostHandled)
			{
				_lostHandled = true;
				_lostAtMs = now;
				Progress.SetInGame(clientUp);
				Progress.Set(JoinStage.Lost, now, lostReason);
				string next;
				if (_o.Rejoin)
				{
					next = clientUp || transitioning ? $"back to the main menu in {4} s, then joining again (fresh world sync)" : $"joining again in {3} s";
				}
				else
				{
					next = "the join dialog offers Retry";
				}
				_log.Warn("client: LOST THE HOST (" + lostReason + "); " + next);
				Report(ClientPhase.Warning, "lost the host: " + lostReason);
			}
			if (!_o.Rejoin)
			{
				return true;
			}
			if (clientUp)
			{
				if (!transitioning && !_menuReturnIssued && now - _lostAtMs >= 4000)
				{
					_menuReturnIssued = true;
					_log.Info("client: returning to the main menu (the host's world is gone from this game)");
					returnToMenu?.Invoke();
				}
				return true;
			}
			if (atMenu && !transitioning)
			{
				if (!_lostMenuSeen)
				{
					_lostMenuSeen = true;
					if (_menuReturnIssued || _configured)
					{
						_lostAtMs = now;
					}
					Progress.SetInGame(inGame: false);
					Progress.Set(JoinStage.Lost, now, lostReason);
				}
				if (now - _lostAtMs >= 3000)
				{
					Rejoin("the connection was lost (" + lostReason + ")", now);
				}
			}
			return true;
		}
		if (!_left && _configured && atMenu && !transitioning && !clientUp)
		{
			_left = true;
			if (_startFailed)
			{
				_log.Warn("client: the client game failed to load and the game is back at the main menu; " + (_o.Rejoin ? "press F3 to join again" : "restart the game to join again"));
			}
			else
			{
				_log.Info("client: back at the main menu: leaving the host's world" + (_o.Rejoin ? " (press F3 at the main menu, or 'Join Game' in Steam, to join again)" : " (restart the game to join again)"));
				Report(ClientPhase.Warning, "left the world (quit to the main menu)");
				Progress.SetInGame(inGame: false);
				Progress.Set(JoinStage.Left, now);
			}
			_session?.Leave("quit to the main menu");
		}
		bool done = _left || _startFailed || Progress.Stage is JoinStage.Rejected or JoinStage.GaveUp;
		if (done && _o.Rejoin && atMenu && !transitioning && !clientUp)
		{
			string why = Interlocked.Exchange(ref _rejoinAsk, null);
			if (why == null && GameKeys.MenuJoinPressed())
			{
				why = "F3 pressed at the main menu";
			}
			if (why != null)
			{
				Rejoin(why, now);
			}
			return true;
		}
		if (!done)
		{
			_rejoinAsk = null;
		}
		return done;
	}

	private void Rejoin(string why, long now)
	{
		if (_retired)
		{
			return;
		}
		_retired = true;
		var next = Current = new ClientWorld(_log, _o)
		{
			_mainQueue = _mainQueue,
			_mainThreadSeen = true,
			_generation = _generation + 1
		};
		AudioRouting.Instance.Reset();
		Progress.NewJoin(now);
		_log.Info($"client: JOINING AGAIN (join #{next._generation} in this game): {why}; the world sync starts from scratch " + "(NetworkIds, proxies, pairings, lifecycle, state, blobs, signal/type keys, stories, observables, look prediction, nametags)");
		try
		{
			RejoinRequested?.Invoke(why);
		}
		catch (Exception ex)
		{
			_log.Error($"client: asking the net thread to reconnect failed: {ex}");
		}
	}

	private void ReturnToMenu(Keen.Game2.GameAppComponent app)
	{
		if (_mainQueue != null)
		{
			_mainQueue.Enqueue(Go);
		}
		else
		{
			Go();
		}
		void Go()
		{
			try
			{
				if (!IsTransitioning(app) && app.Client != null)
				{
					app.Disconnect();
				}
			}
			catch (Exception ex)
			{
				_log.Error($"client: returning to the main menu failed ({ex.GetType().Name}: {ex.Message}); use ESC > exit to the main menu");
			}
		}
	}

	private void ShowProgress(Keen.Game2.GameAppComponent app, long now)
	{
		var stage = Progress.Stage;
		if (stage != _loggedStage)
		{
			_loggedStage = stage;
			var text = Progress.Describe(now);
			if (stage != JoinStage.Idle)
			{
				_log.Info($"client: join progress ({stage}): {text.Toast ?? text.Loading ?? "-"}");
			}
		}
		s_ui?.Update(app, Progress, now);
	}

	internal static string UiDescribe() => s_ui?.Describe() ?? "on-screen progress: none";

	public static bool AwaitReplicationPrefix(object __instance, EntityId __0, ref Task<Entity> __result)
	{
		var current = Current;
		var replication = current?._replication;
		if (replication == null || __instance is not InProcessReplicationSessionComponent comp || comp._context != replication.Client)
		{
			return true;
		}
		try
		{
			__result = current.AwaitSecureEntity(comp, __0);
			return false;
		}
		catch (Exception ex)
		{
			current._secureFailed++;
			current._log.WarnOnce("client-await-replication", $"client: resolving a host EntityId failed ({ex.GetType().Name}: {ex.Message}); Keen's own call runs (it never completes on a joiner)");
			return true;
		}
	}

	private Task<Entity> AwaitSecureEntity(InProcessReplicationSessionComponent comp, EntityId id)
	{
		var tcs = TaskCompletionSource<Entity>.Create("Y51_SE2Online.ClientWorld.AwaitSecureEntity");
		if (id == default)
		{
			tcs.SetResult(null);
			return tcs.Task;
		}
		long n = ++_secureAsked;
		var awaiter = SecureEntities.ResolveSecureEntity(comp.Session, id).Task.GetAwaiter();
		awaiter.OnCompleted(() =>
		{
			Entity entity = null;
			string error = null;
			try
			{
				var exception = awaiter.GetExceptionResult();
				if (exception != null)
				{
					error = exception.SourceException.GetType().Name;
				}
				else
				{
					entity = awaiter.GetResult();
				}
			}
			catch (Exception ex)
			{
				error = ex.GetType().Name;
			}
			if (entity != null)
			{
				_secureResolved++;
			}
			else
			{
				_secureNull++;
			}
			if (n <= 3 || (entity == null && _secureNull == 1))
			{
				_log.Info($"client: AwaitReplication #{n} (a host model's EntityId {id.EntityGuid}) -> " + (entity != null ? "NetworkId " + (NetIdOf(entity)?.ToString() ?? "?") + " via the host" : "nothing (" + (error ?? "the host does not know it, or it never arrived here") + ")"));
			}
			tcs.SetResult(entity);
		});
		return tcs.Task;
	}

	private string DescribeSecure()
	{
		if (_secureAsked != 0L)
		{
			return $"; host EntityIds asked {_secureAsked}: resolved {_secureResolved}, none {_secureNull}, failed {_secureFailed}";
		}
		return "";
	}
}
