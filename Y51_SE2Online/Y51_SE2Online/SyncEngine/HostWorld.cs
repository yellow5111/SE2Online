using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Keen.Game2;
using Keen.Game2.Client.GameSystems.PlayerControl;
using Keen.Game2.Client.WorldObjects.Character;
using Keen.Game2.Game.SessionComponents;
using Keen.Game2.Simulation.GameSystems.Control;
using Keen.Game2.Simulation.GameSystems.Ownership;
using Keen.Game2.Simulation.GameSystems.Permissions;
using Keen.Game2.Simulation.GameSystems.Player;
using Keen.Game2.Simulation.GameSystems.Respawn;
using Keen.Game2.Simulation.RuntimeSystems.Saves;
using Keen.Game2.Simulation.Utils;
using Keen.Game2.Simulation.WorldObjects.Characters;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.Pilotable;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.Weapons;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.Annotations.Signals;
using Keen.VRage.DCS.Builders;
using Keen.VRage.DCS.Components;
using Keen.VRage.DCS.ObjectBuilders;
using Keen.VRage.Library.Collections;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Library.Utils;
using Keen.VRage.Multiplayer.Replications.FailureStrategies;
using Keen.VRage.Multiplayer.Replications.InProcess;
using Keen.VRage.Multiplayer.Sessions;
using Y51_SE2Online.Core;
using Y51_SE2Online.Dedicated;
using Y51_SE2Online.Net;
using Y51_SE2Online.Probe;
using Y51_SE2Online.Sync;
using VRage.Multiplayer.Replication.CB;

namespace Y51_SE2Online.SyncEngine;

internal sealed partial class HostWorld : IHostWorld, IS2CTap
{
	private sealed class Joiner
	{
		public PeerId Peer;

		public ushort ClientId;

		public string Name;

		public Entity? Character;

		public uint NetId;

		public int Choice = -1;

		public bool Respawn;

		public List<SpawnTarget>? Menu;

		public volatile bool ShipPending;

		public volatile Entity[]? ShipRoots;

		public volatile List<Entity>? ShipSettle;

		public long ShipSettleUntil;

		public List<(Entity Grid, WorldTransform At)>? ShipPins;

		public int ShipPinHeld, ShipPinFixes;

		public bool ShipPinsDynamic;

		public long ShipDeadline;

		public volatile bool ShipQueued;

		public bool SeatLogged;

		public readonly ConcurrentQueue<uint> Candidates = new();

		public Entity? Seat;

		/// <summary>The seat a rejoining player's saved character still sat in when it was taken over.</summary>
		public Entity? RestoredSeat;

		public bool SeatMismatchLogged;

		public readonly Dictionary<Entity, long> ScannedRoots = new(ReferenceEqualityComparer.Instance);

		public Entity? SeatRoot;

		public long SeatedAtMs;

		public bool Driving = true;

		public volatile uint[]? LeftGridIds;

		public long LeftAtMs;

		public uint StopGridId;
	}

	private sealed record Registration(PeerId Peer, ushort ClientId, string Key, string Name);

	private sealed class Part
	{
		public readonly EntityBundle Bundle = new();

		public readonly Dictionary<Entity, EntityObjectBuilder> Eobs = new();

		public int Roots;
	}

	private readonly IMpLog _log;

	private readonly NetIdRegistry<Entity> _ids = new();

	/// <summary>Tag Data (no fields, e.g. DampeningData) the clients were last told is present, by NetId and server type id.</summary>
	private readonly HashSet<(uint, int)> _tags = new();

	private static readonly ConcurrentDictionary<int, bool> TagTypes = new();

	private readonly ConcurrentQueue<PeerId> _captureRequests = new();

	private readonly HashSet<PeerId> _cancelled = new();

	private volatile HostSession? _host;

	private object? _serverComp;

	private long _lastServerTickMs;

	private long _version;

	private uint _hostTick;

	private bool _disabled;

	private readonly ConcurrentDictionary<Type, FieldInfo?> _closureEntityField = new();

	private volatile IReadOnlyDataOffloading? _offloading;

	private readonly ConcurrentDictionary<ushort, Joiner> _joiners = new();

	private readonly ConcurrentDictionary<ushort, HashSet<uint>> _allowed = new();

	private readonly ConcurrentQueue<Joiner> _spawnRequests = new();

	private readonly ConcurrentQueue<Joiner> _releaseRequests = new();
	private int _pendingReleases;

	private volatile InProcessReplicationSessionComponent? _hostClientComp;

	private bool _upstreamFailed;

	private readonly List<(int, Segment)> _scratch = new();

	private readonly List<(int, Segment)> _tapScratch = new();

	private readonly object _signalSync = new();

	private static readonly FieldInfo? HashToStringField = typeof(StringHash).GetField(Name("sh-hash2str"), BindingFlags.Static | BindingFlags.NonPublic);

	private static readonly FieldInfo? HashLockField = typeof(StringHash).GetField(Name("sh-lock"), BindingFlags.Static | BindingFlags.NonPublic);

	private readonly HashSet<int> _sentHashes = new();

	private int _lastHashCount = -1;

	private bool _namesFailed;

	private List<WireCommand> _pendingSignals = new();

	private readonly ConcurrentDictionary<string, long> _s2cDrops = new();

	private readonly ConcurrentDictionary<string, long> _c2sDrops = new();

	private readonly ConcurrentDictionary<string, long> _s2cSent = new();

	private readonly ConcurrentDictionary<string, long> _c2sApplied = new();

	private HostStories? _stories;

	private int _serverStoryIds;

	public long SignalsForwarded;

	public long SignalsDropped;

	private long _lastAllowedMs;

	public bool SpawnNearHost;

	public bool OnePilot = true;

	public long InputDataApplied;

	public long InputCommandsApplied;

	public long InputRejected;

	public long CharactersSpawned;

	public long CharactersReleased;

	public long CharactersLost;

	public long StateUnknownEntity;

	public long SpawnBundles;

	public long SpawnBytes;

	public long Despawns;

	public long Forgets;

	public long SpawnBlobs;

	public long SpawnBlobBytes;

	public long InputRemovals;

	private readonly HashSet<int> _removalLogged = new();

	private static readonly TimeSpan BlobLoadTimeout = TimeSpan.FromSeconds(60L);

	private readonly HostObservables _observables;

	private HostIdentities? _identities;

	private PermissionGate? _gate;
	internal WorldSocial Social => _worldSession == null ? null : WorldSocial.For(_worldSession);
	internal long IdentityOf(ushort clientId) => _identityOf.TryGetValue(clientId, out var identity) ? identity : 0;
	private readonly ConcurrentQueue<(PeerId? Peer, ushort ClientId, SocialRequest Request)> _socialRequests = new();
	private readonly Dictionary<string, (int Count, long Bytes)> _frameSignalBytes = new();
	private long _heavyLoggedMs = long.MinValue / 2;
	private readonly Dictionary<long, SocialRequest> _socialViews = new();
	private readonly Dictionary<long, string> _socialMessages = new();
	private readonly ConcurrentDictionary<ushort, (long Start, int Count)> _socialRate = new();

	private long _socialNextPublish;
	private string Tagged(long identity, string name)
	{
		string tag = FactionTagOf(identity);
		return FactionTag.Prefix(tag, name);
	}

	// Read from the net thread while HostSession holds its own lock, so it must not take social.Sync.
	private volatile Dictionary<long, string> _factionTags = new();

	internal string? FactionTagOf(long identity) => identity != 0 && _factionTags.TryGetValue(identity, out string tag) ? tag : null;

	internal void QueueSocial(SocialRequest request) => _socialRequests.Enqueue((null, 1, request));
	public void OnSocialRequest(PeerId peer, ushort clientId, SocialRequest request)
	{
		long now = MpClock.NowMs();
		// A burst of five a second: a single 200 ms slot dropped clicks that landed next to a periodic request (suit broadcast, terminal open).
		var (start, count) = _socialRate.TryGetValue(clientId, out var rate) && now - rate.Start < 1000 ? rate : (now, 0);
		if (_socialRequests.Count >= 1024 || count >= 5) return;
		_socialRate[clientId] = (start, count + 1);
		_socialRequests.Enqueue((peer, clientId, request));
	}

	private void ApplyPcuLimit(Session session)
	{
		var host = _host;
		var pcu = session.SessionComponents.TryGet<Keen.Game2.Simulation.GameSystems.PCU.PCUSessionComponent>();
		if (host == null || pcu == null) return;
		host.Access.RememberWorldPcu(pcu.MaxPerPlayerPCU);
		int want = host.PcuLimit is int limit ? (limit == 0 ? int.MaxValue : limit) : host.Access.WorldPcu ?? pcu.MaxPerPlayerPCU;
		if (pcu.MaxPerPlayerPCU != want)
		{
			pcu.MaxPerPlayerPCU = want;
			_log.Info($"host world sync: per-player PCU limit {(want == int.MaxValue ? "removed" : want.ToString("N0"))}");
		}
		host.PcuInForce = want;
	}

	private double _sunGlide, _sunGlideDone, _sunGlideMs;
	private long _sunGlideStartMs;

	private void ApplySun(Session session)
	{
		var host = _host;
		var sun = session.SessionComponents.TryGet<Keen.Game2.Simulation.GameSystems.Sun.SunSessionComponent>();
		if (host == null || sun == null) return;
		bool rotate = host.SunRotation ?? sun.SunRotation;
		var period = host.SunPeriodMinutes is double minutes ? TimeSpan.FromMinutes(minutes) : sun.SunPeriod;
		double nudge = host.TakeSunNudge() / 360.0;
		if (host.TakeSunTarget() is double target) { nudge += target / 360.0 - Angle(); _sunGlideMs = 0; }
		if (host.TakeSunGlide() is (bool absolute, double deg))
		{
			double turns = absolute ? deg / 360.0 - Angle() : deg / 360.0;
			if (absolute) turns -= Math.Round(turns);
			// About 30 degrees a second, eased in and out; a new move starts from wherever the sun is now.
			_sunGlide = turns; _sunGlideDone = 0; _sunGlideStartMs = MpClock.NowMs();
			_sunGlideMs = Math.Clamp(Math.Abs(turns) * 12000, 1500, 12000);
		}
		if (_sunGlideMs > 0)
		{
			double t = Math.Min(1, (MpClock.NowMs() - _sunGlideStartMs) / _sunGlideMs), eased = t * t * (3 - 2 * t);
			nudge += _sunGlide * (eased - _sunGlideDone);
			_sunGlideDone = eased;
			if (t >= 1) _sunGlideMs = 0;
		}
		if (rotate != sun.SunRotation || period != sun.SunPeriod || nudge != 0)
		{
			// The sun's angle is (game time of day / period + offset) turns, with the time term dropped while it is
			// stopped: carry the change into the offset so stopping or changing the day length leaves the sun put.
			double offset = sun.TimeOfDayOffset + Phase(sun.SunRotation, sun.SunPeriod) - Phase(rotate, period) + nudge;
			sun.TimeOfDayOffset = (float)(offset - Math.Floor(offset));
			sun.SunRotation = rotate;
			sun.SunPeriod = period;
			if (_sunGlideMs == 0) _log.Info($"host world sync: sun rotation {(rotate ? "on" : "off")}, one turn every {period.TotalMinutes:0.#} min, offset {sun.TimeOfDayOffset:0.###}");
		}
		host.SunInForce = (sun.SunRotation, sun.SunPeriod.TotalMinutes);
		host.SunDegrees = Angle() * 360;

		double Angle() { double turns = sun.TimeOfDayOffset + Phase(sun.SunRotation, sun.SunPeriod); return turns - Math.Floor(turns); }

		double Phase(bool rotating, TimeSpan turn) => rotating && turn.TotalSeconds > 0 ? sun._time.CurrentGameTime.TimeOfDay.TotalSeconds / turn.TotalSeconds % 1.0 : 0;
	}

	private long _socialTableVersion = -1;

	private void UpdateSocial(Session session)
	{
		long socialStarted = Stopwatch.GetTimestamp();
		try { UpdateSocialCore(session); }
		finally { _socialMs += Stopwatch.GetElapsedTime(socialStarted).TotalMilliseconds; }
	}

	private void UpdateSocialCore(Session session)
	{
		ApplyPcuLimit(session);
		ApplySun(session);
		ResourceMultiplierRules.Apply(session);
		InventoryMultiplierRules.Apply(session);
		BlockSizeFilter.EnsureHost();
		var social = Social;
		if (social == null || _host == null) return;
		lock (social.Sync)
		{
			_factionTags = social.State.Factions.SelectMany(f => f.Members.Select(m => (m, f.Tag))).GroupBy(x => x.m).ToDictionary(g => g.Key, g => g.First().Tag);
			PvpRules.SetPlayers(social.State.Players.Select(p => p.Identity),
				_joiners.Values.Select(j => IdentityOf(j.ClientId)).Append(Dedicated.DedicatedRuntime.Headless ? 0 : PlayerIdentities.HotfixIdentity),
				social.State.Factions.Select(f => (IReadOnlyCollection<long>)f.Members));
			if (TurretRelations.SetRelations(social.State.Factions, social.State.Pacts) | TurretRelations.TakeRefresh())
				_log.Info($"turrets: factions or PvP changed; {TurretRelations.Refresh(session)} turret grid(s) re-evaluated their targets; {PvpRules.Describe()}");
			social.InitializeGrids(_worldSession);
			var roster = _host.PlayerTable();
			long now = MpClock.NowMs();
			// Registering is O(players^2); the roster only changes with the player table, and once a second catches the rest.
			long tableVersion = Interlocked.Read(ref _host.PlayerTableVersion);
			if (tableVersion != _socialTableVersion || now >= _socialNextPublish)
			{
				_socialTableVersion = tableVersion;
				foreach (var player in roster) social.State.Register(player.Identity, player.Name, player.Identity == PlayerIdentities.HotfixIdentity ? _host.HostKey : null);
			}
			bool changed = false;
			while (_socialRequests.TryDequeue(out var command))
			{
				long actor = command.Peer.HasValue ? IdentityOf(command.ClientId) : PlayerIdentities.HotfixIdentity;
				if (command.Peer.HasValue && !_host.Players().Any(p => p.ClientId == command.ClientId && p.Peer == command.Peer)) continue;
				if (command.Request.Action == SocialAction.Broadcast)
				{
					// Kept per identity in the world's side file, so a parked character's tag follows its owner's last choice.
					if (PlayerIdentities.IsHuman(actor) && (command.Request.Offset == 0 ? social.State.Silent.Add(actor) : social.State.Silent.Remove(actor))) _rosterSilentChanged = true;
					continue;
				}
				if (!roster.Any(p => p.Identity == actor)) continue;
				if (command.Request.Action is SocialAction.Hack or SocialAction.Lockdown) { HackCommand(session, actor, command.ClientId, command.Request); continue; }
				var request = WithSecureTarget(session, actor, command.Request);
				if (request.Action == SocialAction.Refresh) _socialViews[actor] = request;
				else _socialMessages[actor] = social.Apply(actor, request, id => _ids.TryGet(id, out Entity entity) && !EngineCodec.IsDead(entity) ? entity : null);
				changed = true;
			}
			if (!changed && now < _socialNextPublish) return;
			_socialNextPublish = now + 1000;
			social.TagAntennas(session, FactionTagOf);
			foreach (var (clientId, text) in social.DueFactionMotds(roster)) _host.GpsNotice(clientId, text);
			var grids = session.Entities.Where(e => e.TryGet<CubeGridComponent>() != null && e.TryGet<OwnershipComponent>() != null && !EngineCodec.IsDead(e))
				.Select(e => new SocialGrid(_ids.GetOrAssign(e), SocialState.Clean(e.TryGet<CubeGridComponent>().DisplayName.ToString(), 100), TeamPermissions.OwnerOf(e), WorldSocial.SharingOf(e)))
				.OrderBy(g => g.Name).ThenBy(g => g.NetId).ToArray();
			var (listed, open) = BlockLists(session);
			// Everything every player sees alike is built and serialized once here, not once per player.
			var common = social.Common(open);
			var commonDoc = JsonDoc.Of(common);
			var pages = new Dictionary<(string, int), JsonDoc>();
			var peers = new Dictionary<ushort, PeerId>();
			foreach (var p in _host.Players()) if (p.Peer is PeerId id) peers.TryAdd(p.ClientId, id);
			var docs = new List<(PeerId, JsonDoc)>(peers.Count);
			foreach (var player in roster)
			{
				bool remote = peers.TryGetValue(player.ClientId, out var peer);
				if (!remote && player.Identity != PlayerIdentities.HotfixIdentity) continue;
				var view = _socialViews.TryGetValue(player.Identity, out var current) ? current : new SocialRequest(SocialAction.Refresh);
				var message = _socialMessages.TryGetValue(player.Identity, out var text) ? text : "";
				var snapshot = social.Snapshot(player.Identity, grids, view, message, common);
				snapshot.FocusBlock = FocusBlockOf(player.Identity);
				snapshot.AllowedBlocks = listed.Where(b => b.Acl.Allow.Contains(player.Identity)).Select(b => b.Id).ToArray();
				snapshot.DeniedBlocks = listed.Where(b => b.Acl.Deny.Contains(player.Identity)).Select(b => b.Id).ToArray();
				snapshot.Hacks = HackViewsFor(player.Identity);
				if (!remote) continue;
				var pageKey = (view.Name?.Trim() ?? "", snapshot.Offset);
				if (!pages.TryGetValue(pageKey, out var page)) pages[pageKey] = page = JsonDoc.Of(new SocialSnapshot { Grids = snapshot.Grids });
				docs.Add((peer, JsonDoc.Of(snapshot.OwnPart()).With(commonDoc, SocialSnapshot.Common).With(page, new[] { nameof(SocialSnapshot.Grids) })));
			}
			_host.SendSocial(docs);
		}
	}

	private readonly ConcurrentQueue<Registration> _registrations = new();

	private readonly ConcurrentDictionary<PeerId, (long Since, ushort ClientId)> _pendingRegistration = new();

	private readonly ConcurrentDictionary<ushort, long> _identityOf = new();

	private long _lastStatusMs;

	public const int RegistrationWaitMs = 30000;

	// Server ticks a capture has waited for its player's registration. Wall time alone is not enough: while the host is
	// still loading (or stalled) the server does not tick, and a joiner timed out that way played as the host's identity.
	private const int RegistrationWaitTicks = 300;
	private readonly System.Collections.Concurrent.ConcurrentDictionary<PeerId, int> _registrationWaitTicks = new();

	private volatile WorldSessionComponent? _worldSession;

	// Not const: the live rig shrinks it to put a seat and its pilot into different parts.
	internal static int WorldPartEntities = 1500;

	private static readonly HashSet<string> ShipActionStories = new(StringComparer.Ordinal) { "CubeGridComponent.TogglePower", "CubeGridComponent.ToggleDampeners", "CubeGridComponent.ToggleLights", "CubeGridComponent.ToggleParkingBrakes", "GridSpeedLimitComponent.SetBoost", "BlockToolbarServerComponent.RequestActivateTile", "BlockToolsComponent.RequestEquipTool" };

	public long PassengerActionsRefused;

	private readonly Dictionary<Entity, long> _hostSeatedSince = new(ReferenceEqualityComparer.Instance);

	private readonly HashSet<Entity> _hostDriveLogged = new(ReferenceEqualityComparer.Instance);

	private readonly Dictionary<Entity, (long Ms, bool HostPilots)> _hostPilotScan = new(ReferenceEqualityComparer.Instance);

	private bool _menuPauseOff;

	private bool _menuPauseFailed;

	private int _joinerCharTick;

	private int _joinerCharCount = -1;

	private readonly ConcurrentDictionary<string, long> _echoedProps = new();

	private static readonly ConcurrentDictionary<Type, bool> ClientSettable = new();

	private static readonly int[] DataRefOffsets = { 0, 4 };

	private static readonly MethodInfo? CreateCharacterMethod = typeof(RespawnSessionComponent).GetMethod("CreateCharacter", BindingFlags.Instance | BindingFlags.NonPublic);

	private readonly ConditionalWeakTable<ReplicationCommandBuffer, StrongBox<uint>> _bufferStamp = new();

	private readonly TickClock _tickClock = new(paced: true);

	public long StampedBuffers;

	public long UnstampedPostData;

	private readonly StringIdTable _sids = new();

	public const int ContactCapMs = 100;

	private readonly Dictionary<(int Stub, object Target), long> _contactLast = new();

	public long ContactCapped;

	private static readonly string[] HostPlayerEventPrefixes = { "ClientRespawnSessionComponent+", "ClientPlayersSessionComponent+", "CameraAnalyticsSessionComponent+", "SessionInGameUISessionComponent+", "DatapadClientSessionComponent+", "FastTravelClientSessionComponent+", "TradeNotificationsSessionComponent+", "TargetingSessionComponent+", "PlayersSessionComponent+PropagateCreatedPlayerSignal", "Streamed" };

	private ObjectSpace? _tapSpace;

	private readonly object _despawnSync = new();

	private List<(uint Id, byte Mode, Entity? Forgotten, uint Root)> _pendingDespawns = new();

	private readonly Dictionary<uint, (long Ms, string What)> _forgotten = new();

	private const int RosterIntervalMs = 500;

	private const int HostTagResolveTicks = 30;

	private long _lastRosterMs;

	private bool _rosterFailed;

	private NametagOverlay? _hostTags;

	private int _hostTagTick;

	private readonly List<(ushort ClientId, string Name, Entity? Target)> _hostTagList = new();

	private uint _hostTagVersion;

	private volatile bool _rosterSilentChanged;

	private volatile Entity? _hostCharacterServer;

	private bool _hostCharacterLogged;

	public static HostWorld? Current { get; private set; }

	public TypeKeyTable Keys { get; } = new();

	public bool WorldReady => !_disabled && Volatile.Read(ref _serverComp) != null && MpClock.NowMs() - Volatile.Read(ref _lastServerTickMs) < 3000;

	public string WorldName
	{
		get
		{
			try
			{
				return _worldSession?.DisplayName ?? "?";
			}
			catch (Exception)
			{
				return "?";
			}
		}
	}

	/// <summary>Stable per-world key for files that belong to one hosted world: the save container, else the world name.</summary>
	public string WorldKey
	{
		get
		{
			try { return _worldSession?.SaveContainerId?.ToString() ?? WorldName; }
			catch (Exception) { return WorldName; }
		}
	}

	internal Session? HostClientSession => _hostClientComp?.Session;

	public bool WorldLoaded => !_disabled && Volatile.Read(ref _serverComp) is InProcessReplicationSessionComponent server && !server.IsUnloading;

	private static string Name(string id) => HookTargets.Reflected.Single(m => m.Id == id).Name;

	private HostWorld(IMpLog log)
	{
		_log = log;
		_observables = new HostObservables(log);
	}

	public static bool CleanupPending => Current != null && Volatile.Read(ref Current._pendingReleases) != 0;

	public static void Reset()
	{
		if (Current != null)
		{
			Current._disabled = true;
			var ticks = Current._worldSession?.Entity?.TryGet<Keen.Game2.WorldTickComponent>()?._tickManagement;
			if (ticks != null) ticks.PauseWhenInMenu = true;
		}
		Current = null;
		ReplicationProbe.Tap = null;
		if (!Y51_SE2Online.Dedicated.DedicatedRuntime.Requested) ServerProfiler.Enabled = false;
		ServerProfiler.Reset();
	}

	public static HostWorld Install(IMpLog log)
	{
		if (Current == null)
		{
			Current = new HostWorld(log);
		}
		ServerProfiler.Enabled = true;
		ReplicationProbe.Tap = Current;
		return Current;
	}

	public void ConfigureIdentities(HostSettings settings)
	{
		if (settings.Identities)
		{
			_identities = HostIdentities.Install(_log, settings);
		}
		TeamPermissions.Enabled = settings.Identities && settings.Grids == GridPermissionMode.Team;
		_gate = settings.Identities ? new PermissionGate() : null;
		_log.Info($"host world sync: {settings}" + (settings.Identities ? "" : " (every joiner plays as the host's identity, as in 0.5)"));
	}

	private IdentityId IdentityFor(ushort clientId)
	{
		if (!_identityOf.TryGetValue(clientId, out var identity))
		{
			return InProcessPerPlayerDataSessionComponent.HotfixIdentity;
		}
		return new IdentityId(identity);
	}

	private ushort SourceFor(ushort clientId) => _identityOf.ContainsKey(clientId) ? clientId : (ushort)1;

	public void RegisterPlayer(PeerId client, ushort clientId, string identityKey, string name)
	{
		if (_identities != null)
		{
			_pendingRegistration[client] = (MpClock.NowMs(), clientId);
			_registrations.Enqueue(new Registration(client, clientId, identityKey, name));
		}
	}

	private void ServerRegister(InProcessReplicationSessionComponent server, Registration r)
	{
		var host = _host;
		try
		{
			if (_identities != null && host != null)
			{
				if (!_pendingRegistration.ContainsKey(r.Peer))
				{
					_log.Warn($"host world sync: registration of ClientId {r.ClientId} '{r.Name}' came after its world capture; it plays as the host's identity until it rejoins");
					return;
				}
				var result = _identities.Register(server.Session, r.ClientId, r.Key, r.Name);
				_identityOf[r.ClientId] = result.Identity;
				_log.Info($"host world sync: PLAYER REGISTERED: ClientId {r.ClientId} '{r.Name}' -> IdentityId {result.Identity} (PlayerIdentity {result.Player}, key {r.Key}; {(result.NewPlayer ? "new in this world session" : "already known in this world session")}; {result.Detail})");
				host.PlayerIdentified(r.ClientId, result.Identity, result.Player);
			}
		}
		catch (Exception ex)
		{
			_identityOf.TryRemove(r.ClientId, out _);
			_log.Error($"host world sync: registering ClientId {r.ClientId} '{r.Name}' as its own player failed; it plays as the host's identity (as in 0.5): {ex}");
			host?.PlayerIdentified(r.ClientId, 0L, 0L);
		}
		finally
		{
			_pendingRegistration.TryRemove(r.Peer, out _);
			_registrationWaitTicks.TryRemove(r.Peer, out _);
		}
	}

	private void PublishPlayerStatus(InProcessReplicationSessionComponent comp)
	{
		var host = _host;
		long now = MpClock.NowMs();
		if (host == null || _identityOf.IsEmpty || now - _lastStatusMs < 1000)
		{
			return;
		}
		_lastStatusMs = now;
		try
		{
			foreach (var (clientId, identity) in _identityOf)
			{
				(int Pcu, int Max)? pcu = HostIdentities.ReadPcu(comp.Session, identity);
				if (pcu.HasValue)
				{
					var value = pcu.GetValueOrDefault();
					host.PublishPlayerStatus(clientId, value.Pcu, value.Max);
				}
			}
		}
		catch (Exception ex)
		{
			_log.WarnOnce("host-pcu-status", $"host world sync: reading joiners' PCU failed ({ex.GetType().Name}: {ex.Message}); their PCU readout stays as it is");
		}
	}

	private bool IsAdminClient(ushort clientId) => _host?.IsAdminClient(clientId) ?? clientId == SyncProtocol.HostClientId;

	private string? StoryGate(ushort clientId, object box, Session? server)
	{
		string? emote = EmoteGate(clientId, box);
		if (emote is { Length: > 0 }) return emote;
		if (emote == null && !IsAdminClient(clientId) && AdminGuard.IsAdminStory(box.GetType()))
		{
			Interlocked.Increment(ref AdminGuard.Refused);
			return "Only admins can do that on this server.";
		}
		if (!AdminGuard.IsAdminStory(box.GetType()) && ForeignCharacterGate(clientId, box) is string foreign) return foreign;
		if (box.GetType().DeclaringType?.Namespace?.Contains(".Ownership", StringComparison.Ordinal) == true || box.GetType().DeclaringType == typeof(GridPermissionsComponent))
			return "Use the grid ownership controls to change ownership or sharing.";
		if (_identityOf.TryGetValue(clientId, out long actor))
		{
			string denied = SocialGuard.CheckObject(box, actor);
			if (denied != null) return denied;
		}
		if (PassengerGate(clientId, box) is string passenger)
		{
			return passenger;
		}
		var gate = _gate;
		if (gate == null || server == null || !_identityOf.TryGetValue(clientId, out var identity))
		{
			return PasteOnce.HostGate(clientId, box);
		}
		string? validator = gate.StoryValidator(box.GetType());
		if (validator == null)
		{
			return PasteOnce.HostGate(clientId, box);
		}
		// Stories are compiler closures over their component; "<>4__this" is that captured component.
		// The paste guard goes last: it remembers what it lets through.
		return gate.Check(validator, (ObjectCodec.Fields(box.GetType()).FirstOrDefault(f => f.Name == "<>4__this")?.GetValue(box) as Component)?.Entity, identity, server.SessionComponents.TryGet<PermissionsSessionComponent>()) ?? PasteOnce.HostGate(clientId, box);
	}

	public void Bind(HostSession host) => _host = host;

	public void SetWorldSession(WorldSessionComponent world) => _worldSession = world;

	public void RequestCapture(PeerId client)
	{
		lock (_cancelled)
		{
			_cancelled.Remove(client);
		}
		_captureRequests.Enqueue(client);
	}

	public void CancelCapture(PeerId client)
	{
		lock (_cancelled)
		{
			_cancelled.Add(client);
		}
	}

	private void Fail(string where, Exception e)
	{
		_disabled = true;
		_log.Error($"host world sync: {where} failed, world sync disabled for this session (game unaffected): {e}");
	}

	public static void CommitPostfix(object __instance)
	{
		var current = Current;
		if (current == null || current._disabled)
		{
			return;
		}
		try
		{
			var comp = (InProcessReplicationSessionComponent)__instance;
			if (comp.IsServer)
			{
				long started = Stopwatch.GetTimestamp();
				current._socialMs = 0;
				current.OnServerCommit(comp);
				current.NoteFrameWork(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
				// A listen host's server may tick on its own thread; its commit is the only frame edge there.
				if (!Y51_SE2Online.Dedicated.DedicatedRuntime.Requested) ServerProfiler.BeginFrame(false);
			}
		}
		catch (Exception e)
		{
			current.Fail("CommitFrame postfix", e);
		}
	}

	private double _socialMs;
	private long _slowWorkLogMs;

	// Joiners see a stalled server frame as everyone popping; this says whether the plugin's own frame work was it.
	private void NoteFrameWork(double ms)
	{
		if (ms < 25.0) return;
		long now = MpClock.NowMs();
		if (now - _slowWorkLogMs < 2000) return;
		_slowWorkLogMs = now;
		_log.Warn($"host world sync: slow frame work {ms:0} ms (factions/ownership refresh {_socialMs:0} ms)");
	}

	private volatile List<WorldMod> _mods = new List<WorldMod>();

	public IReadOnlyList<WorldMod> Mods => _mods;

	private void OnServerCommit(InProcessReplicationSessionComponent comp)
	{
		if (comp.IsUnloading)
		{
			if (_serverComp == comp)
			{
				Volatile.Write(ref _serverComp, null);
				_log.Info("host world sync: server scene unloading; joined clients are disconnected");
				_host?.WorldChanged("the host left the world");
			}
			return;
		}
		if (_serverComp != comp)
		{
			bool hadWorld = _serverComp != null;
			_identityOf.Clear();
			_ids.Clear();
			_tags.Clear();
			_ids.Bind(comp.Entity, 1u);
			try
			{
				_offloading = comp.Session.TryGet<IReadOnlyDataOffloading>() ?? comp.Session.ExternalServices?.TryGet<IReadOnlyDataOffloading>();
			}
			catch (Exception ex)
			{
				_offloading = null;
				_log.Warn($"host world sync: no data offloading service ({ex.GetType().Name}: {ex.Message}); voxel data cannot be sent");
			}
			if (_offloading == null)
			{
				_log.Warn("host world sync: the server session has no IReadOnlyDataOffloading; planets/asteroids will fail on clients");
			}
			try
			{
				_mods = WorkshopMods.From(comp.Session.ExternalServices?.TryGet<Keen.VRage.Core.Worlds.ModList>());
				if (_mods.Count > 0) _log.Info("host world sync: the world runs mods; joiners download them: " + WorldMods.Summary(_mods));
			}
			catch (Exception ex)
			{
				_mods = new List<WorldMod>();
				_log.Warn($"host world sync: could not read the world's mod list ({ex.GetType().Name}: {ex.Message}); joiners load no mods");
			}
			Volatile.Write(ref _serverComp, comp);
			_log.Info($"host world sync: server scene attached (world '{WorldName}', data offloading {_offloading?.GetType().Name ?? "MISSING"}); friends can join now");
			if (hadWorld)
			{
				_host?.WorldChanged("the host loaded another world");
			}
		}
		Volatile.Write(ref _lastServerTickMs, MpClock.NowMs());
		_hostTick++;
		if (_joiners.IsEmpty && _menuPauseOff)
		{
			SyncMenuPause();
		}
		long lap = ServerProfiler.Now;
		if (!_joiners.IsEmpty && !_upstreamFailed)
		{
			try
			{
				UpdateJoiners(comp);
			}
			catch (Exception ex)
			{
				_upstreamFailed = true;
				_log.Error($"host world sync: joining players' characters failed and are switched off for this session (world sync continues): {ex}");
			}
		}
		if (!_identityOf.IsEmpty)
		{
			PublishPlayerStatus(comp);
		}
		lap = ServerProfiler.Lap(ServerSection.Joiners, lap);
		if (!_joiners.IsEmpty)
		{
			try
			{
				UpdateStreaming(comp);
			}
			catch (Exception ex)
			{
				_log.WarnOnce("host-streaming", $"streaming: update failed ({ex.GetType().Name}: {ex.Message}); retrying next update");
			}
			lap = ServerProfiler.Lap(ServerSection.Streaming, lap);
			try
			{
				UpdateDesync(comp);
			}
			catch (Exception ex)
			{
				_log.WarnOnce("host-desync", $"desync: update failed ({ex.GetType().Name}: {ex.Message}); retrying next frame\n{ex.StackTrace}");
			}
		}
		lap = ServerProfiler.Lap(ServerSection.Desync, lap);
		UpdateNametagRoster(comp);
		lap = ServerProfiler.Lap(ServerSection.Joiners, lap);
		List<PeerId>? waiting = null;
		PeerId peer;
		while (_captureRequests.TryDequeue(out peer))
		{
			lock (_cancelled)
			{
				if (_cancelled.Remove(peer))
				{
					continue;
				}
			}
			if (_pendingRegistration.TryGetValue(peer, out var pending))
			{
				// Hold the capture until the player's identity exists, so the snapshot already carries it.
				int ticks = _registrationWaitTicks.TryGetValue(peer, out int waited) ? waited + 1 : 1;
				if (MpClock.NowMs() - pending.Since < RegistrationWaitMs || ticks < RegistrationWaitTicks)
				{
					_registrationWaitTicks[peer] = ticks;
					(waiting ??= new List<PeerId>()).Add(peer);
					continue;
				}
				_registrationWaitTicks.TryRemove(peer, out _);
				_pendingRegistration.TryRemove(peer, out _);
				_identityOf.TryRemove(pending.ClientId, out _);
				_log.Warn($"host world sync: {peer}'s player registration did not run within {RegistrationWaitMs / 1000} s and {RegistrationWaitTicks} server ticks; capturing its world anyway (it plays as the host's identity)");
			}
			Capture(comp, peer);
		}
		ServerProfiler.Lap(ServerSection.Capture, lap);
		if (waiting == null)
		{
			return;
		}
		foreach (var later in waiting)
		{
			_captureRequests.Enqueue(later);
		}
	}

	internal static bool HasMissions(SessionComponentsSnapshot snapshot) => snapshot.Components?.Any(c => c.Type?.Name == "MissionSessionComponent") ?? false;

	private void Capture(InProcessReplicationSessionComponent comp, PeerId peer)
	{
		var host = _host;
		if (host == null || !host.WantsCapture(peer))
		{
			return;
		}
		var stopwatch = Stopwatch.StartNew();
		try
		{
			var session = comp.Session;
			var context = (ServerSceneContext)comp._context;
			var strategy = ((SceneContext)context).Replication.ReplicationStrategy;
			var replay = comp.ReplayContext;
			var snapshot = SessionComponentsSnapshot.Capture(session, (Entity _, bool _) => false, null);
			bool missions = HasMissions(snapshot);
			HashSet<Type>? clientObTypes = null;
			try
			{
				var clientSession = ((SceneContext)context).Other?.SessionComponent?.Session;
				if (clientSession != null)
				{
					clientObTypes = EngineCodec.SessionObjectBuilderTypes(clientSession);
				}
			}
			catch (Exception ex)
			{
				_log.Warn("host world sync: reading the local client's session components failed (" + ex.GetType().Name + "); sending every blob the snapshot references");
			}
			var offloading = _offloading;
			var parts = new List<Part> { new Part() };
			int roots = 0;
			int notOnClient = 0;
			int children = 0;
			int failedRoots = 0;
			int entities = 0;
			var seen = new HashSet<Entity>(ReferenceEqualityComparer.Instance);
			var outOfRange = CaptureExclusions(comp, peer);
			int streamedOut = 0;
			foreach (var entity in session.Entities)
			{
				if (entity == comp.Entity)
				{
					continue;
				}
				if (outOfRange != null && outOfRange.Contains(entity))
				{
					streamedOut++;
					continue;
				}
				if (entity.Data.Has<ParentData>())
				{
					children++;
				}
				else if (!replay.HasPairingLocal(entity))
				{
					notOnClient++;
				}
				else
				{
					if (!seen.Add(entity))
					{
						continue;
					}
					var part = parts[^1];
					if (part.Eobs.Count >= WorldPartEntities)
					{
						parts.Add(part = new Part());
					}
					int before = part.Eobs.Count;
					try
					{
						strategy.SerializeForClientAsBundle(entity, part.Bundle, part.Eobs);
						part.Roots++;
						roots++;
						entities += part.Eobs.Count - before;
					}
					catch (Exception ex)
					{
						if (++failedRoots <= 3)
						{
							_log.Warn($"host world sync: serializing root entity {entity} failed: {ex.GetType().Name}: {ex.Message}");
						}
					}
				}
			}
			if (parts[^1].Eobs.Count == 0 && parts.Count > 1)
			{
				parts.RemoveAt(parts.Count - 1);
			}
			var msgs = new List<EntityBundleMessage>(parts.Count);
			int blobCount = 0;
			int unlocatedCount = 0;
			foreach (var part in parts)
			{
				foreach (var key in part.Eobs.Keys)
				{
					_ids.GetOrAssign(key);
				}
				var msg = new EntityBundleMessage
				{
					Kind = 0,
					HostTick = _hostTick
				};
				foreach (var eob in part.Eobs)
				{
					DEntityBuilder dataStorage = default;
					strategy.CaptureEntityData(eob.Key, eob.Value.Definition, ref dataStorage);
					var blobs = EngineCodec.Blobs(ref dataStorage, Keys);
					if (blobs.Count > 0)
					{
						msg.Data.Add((_ids.GetOrAssign(eob.Key), blobs));
						blobCount += blobs.Count;
					}
				}
				msg.Entities = EngineCodec.Locate(part.Bundle, part.Eobs, e => _ids.GetOrAssign(e), out var unlocated);
				unlocatedCount += unlocated;
				msgs.Add(msg);
			}
			if (!host.BeginCapture(peer, msgs.Count, out var firstLifeSeq))
			{
				_log.Info($"host world sync: {peer} left before its world was captured");
				return;
			}
			for (int i = 0; i < msgs.Count; i++)
			{
				msgs[i].LifeSeq = firstLifeSeq + (uint)i;
			}
			double captureMs = stopwatch.Elapsed.TotalMilliseconds;
			string pre = $"captured on the server thread in {captureMs:0} ms: {roots} roots / {entities} entities in {msgs.Count} part(s) ({children} child entities in scene, {notOnClient} roots not on the local client skipped, {streamedOut} roots beyond the streaming radius, {failedRoots} roots failed, {unlocatedCount} unlocated), {blobCount} data blobs";
			_log.Info($"host world sync: world for {peer} {pre}");
			// Serializing and loading offloaded blobs is slow; only the scene walk above needs the server thread.
			Task.Run(() =>
			{
				var serializeWatch = Stopwatch.StartNew();
				try
				{
					var proxy = new NetIdProxyContext(e => (int)_ids.GetOrAssign(e), null);
					HashSet<Blob> snapshotBlobs;
					byte[] snapshotBytes;
					string snapshotBlobInfo;
					if (clientObTypes != null)
					{
						snapshotBlobs = EngineCodec.SnapshotBlobs(snapshot, t => clientObTypes.Contains(t), out _, out var owners);
						snapshotBytes = EngineCodec.SerializeSnapshot(snapshot, proxy);
						snapshotBlobInfo = owners.Count == 0 ? "none" : string.Join(", ", owners);
					}
					else
					{
						snapshotBlobs = new HashSet<Blob>();
						snapshotBytes = EngineCodec.SerializeSnapshot(snapshot, proxy, snapshotBlobs);
						snapshotBlobInfo = $"{snapshotBlobs.Count} (all components)";
					}
					int snapshotRefs = proxy.Written;
					var allBlobs = new HashSet<Blob>(snapshotBlobs);
					for (int i = 0; i < msgs.Count; i++)
					{
						var partBlobs = new HashSet<Blob>();
						msgs[i].Bundle = EngineCodec.SerializeBundle(parts[i].Bundle, proxy, partBlobs);
						msgs[i].RequiredBlobs = EngineCodec.Ids(partBlobs);
						allBlobs.UnionWith(partBlobs);
					}
					double serializeMs = serializeWatch.Elapsed.TotalMilliseconds;
					var captureBlobs = new CaptureBlobs
					{
						Snapshot = EngineCodec.Ids(snapshotBlobs)
					};
					int failed = 0;
					string? firstError = null;
					if (allBlobs.Count > 0)
					{
						if (offloading != null)
						{
							captureBlobs.Data = EngineCodec.LoadBlobs(offloading, allBlobs, BlobLoadTimeout, out failed, out firstError);
						}
						else
						{
							foreach (var blob in allBlobs)
							{
								captureBlobs.Data[EngineCodec.Id(blob)] = null;
							}
							failed = allBlobs.Count;
							firstError = "no data offloading service on the host";
						}
					}
					string blobSummary = $"{captureBlobs.Data.Count} offloaded blobs {MpLogExtensions.Bytes(captureBlobs.Bytes)} (snapshot: {snapshotBlobInfo}; parts: {string.Join("/", msgs.Select(m => m.RequiredBlobs.Count))}), loaded in {serializeWatch.Elapsed.TotalMilliseconds - serializeMs:0} ms";
					_log.Info($"host world sync: world for {peer}: {blobSummary}");
					if (failed > 0)
					{
						_log.Error($"host world sync: {failed} offloaded blob(s) could not be loaded on this PC (first: {firstError}); the entities using them will be missing on {peer}");
					}
					host.CompleteCapture(peer, snapshotBytes, msgs, $"{pre}; serialized in {serializeMs:0} ms ({snapshotRefs} entity refs in snapshot, {proxy.Written - snapshotRefs} in bundles); {blobSummary}; missions {(missions ? "on" : "off")}", missions, captureBlobs);
				}
				catch (Exception ex)
				{
					_log.Error($"host world sync: serializing the world for {peer} failed: {ex}");
					host.FailCapture(peer, ex.GetType().Name + ": " + ex.Message);
				}
			});
		}
		catch (Exception ex)
		{
			_log.Error($"host world sync: capturing the world for {peer} failed: {ex}");
			host.FailCapture(peer, ex.GetType().Name + ": " + ex.Message);
		}
	}

	private void Assign(HostSession host, Joiner j, Entity? character, IdentityId identityId, Entity? hostCharacter, string via)
	{
		if (character == null)
		{
			host.CharacterFailed(j.Peer, "spawning through " + via + " returned nothing");
			return;
		}
		j.Character = character;
		j.NetId = _ids.GetOrAssign(character);
		CharactersSpawned++;
		TestSpread(j, character, via);
		string where = "?";
		try
		{
			var spawned = character.Data.GetWorldTransform();
			where = $"at {spawned.Position.X:0.0}, {spawned.Position.Y:0.0}, {spawned.Position.Z:0.0}";
			if (hostCharacter != null && hostCharacter.Data.TryGet<WorldTransform>(out var hostTransform))
			{
				where += $", {Vector3D.Distance(in hostTransform.Position, in spawned.Position):0.0} m from the host's character";
			}
		}
		catch (Exception)
		{
		}
		_log.Info($"host world sync: SPAWNED character for ClientId {j.ClientId} '{j.Name}': NetworkId {j.NetId}, owner IdentityId {identityId}{((long)identityId == PlayerIdentities.HotfixIdentity ? " (the host's identity)" : "")}, via {via}, {where}");
		host.CharacterAssigned(j.Peer, j.NetId, "spawned via " + via + ", " + where);
	}

	public void RequestCharacter(PeerId client, ushort clientId, string name, int choice, bool respawn)
	{
		var joiner = _joiners.AddOrUpdate(clientId, _ => new Joiner
		{
			Peer = client,
			ClientId = clientId,
			Name = name,
			Choice = choice,
			Respawn = respawn
		}, (_, old) =>
		{
			old.Peer = client;
			old.Choice = choice;
			old.Respawn = respawn;
			return old;
		});
		_spawnRequests.Enqueue(joiner);
	}

	public void ReleaseCharacter(PeerId client, ushort clientId)
	{
		if (_joiners.TryRemove(clientId, out var joiner))
		{
			_allowed.TryRemove(clientId, out _);
			Interlocked.Increment(ref _pendingReleases);
			_releaseRequests.Enqueue(joiner);
		}
	}

	internal string DescribeActor(Entity character)
	{
		foreach (var joiner in _joiners.Values)
		{
			if (joiner.Character == character)
			{
				return $"ClientId {joiner.ClientId} '{joiner.Name}'";
			}
		}
		if (!_ids.TryGet(character, out var id))
		{
			return "the host's player";
		}
		return $"the host's player (character NetworkId {id})";
	}

	internal string DescribeNetId(Entity e) => _ids.TryGet(e, out var id) ? $"NetworkId {id}" : "(no NetworkId)";

	internal bool IsJoinerCharacter(Entity? serverEntity)
	{
		if (serverEntity == null)
		{
			return false;
		}
		foreach (var joiner in _joiners.Values)
		{
			if (joiner.Character == serverEntity)
			{
				return true;
			}
		}
		return false;
	}

	private string? PassengerGate(ushort clientId, object box)
	{
		// Story closures are named "<Method>d__N" and nested in the component that declares the method.
		var type = box.GetType();
		var declaringType = type.DeclaringType;
		int open = type.Name.IndexOf('<');
		int close = type.Name.IndexOf('>');
		if (declaringType == null || open != 0 || close < 2 || !ShipActionStories.Contains(declaringType.Name + "." + type.Name.Substring(1, close - 1)))
		{
			return null;
		}
		// The seat comes from the host's own scene: the input-based tracking (joiner.Seat) only knows a player after its
		// first flight input, so a passenger who had not pressed anything could switch the pilot's dampeners.
		var seat = (CharacterOf(clientId)?.AsInterface<ISeatable>()?.CurrentSeat as SeatComponent)?.Entity;
		var fields = ObjectCodec.Fields(type);
		// Static stories (the cockpit toolbar's RequestActivateTile) carry the block as their Entity argument.
		var root = (fields.FirstOrDefault(f => f.Name == "<>4__this")?.GetValue(box) as Component)?.Entity
			?? fields.FirstOrDefault(f => f.FieldType == typeof(Entity))?.GetValue(box) as Entity;
		if (seat == null || root == null || RootOf(seat) != RootOf(root))
		{
			return null;
		}
		root = RootOf(root);
		// Vanilla only reaches these stories from a cockpit's input handler; passenger seats have none.
		bool passenger = !IsControlSeat(seat);
		if (!passenger && OnePilot)
		{
			_joiners.TryGetValue(clientId, out var joiner);
			passenger = joiner != null && joiner.Seat == seat
				? !joiner.Driving
				: HostPilots(root, MpClock.NowMs()) || _joiners.Values.Any(o => o != joiner && o.SeatRoot == root && o.Driving);
		}
		if (!passenger)
		{
			return null;
		}
		Interlocked.Increment(ref PassengerActionsRefused);
		return "passenger seat: only the ship's pilot switches its power, dampeners, lights, brakes and boost or uses its toolbar";
	}

	private static bool IsControlSeat(Entity? seat) => seat != null && seat.TryGet<CockpitComponent>() != null && !PassengerSeats.IsPassengerSeat(seat);

	private static Entity RootOf(Entity e)
	{
		for (int depth = 0; depth < 16 && e.TryGet<HierarchyComponent>()?.Parent is { } parent; depth++) e = parent;
		return e;
	}

	private bool MayDriveGrid(Joiner j, Entity root, long now)
	{
		if (j.SeatRoot != root)
		{
			j.SeatRoot = root;
			j.SeatedAtMs = now;
			j.Driving = true;
		}
		if (!IsControlSeat(j.Seat))
		{
			// A passenger seat block has no controls; it must not claim the ship from whoever sits in its cockpit.
			j.Driving = false;
			return false;
		}
		if (!OnePilot)
		{
			return true;
		}
		var seated = new List<(ushort Client, long SeatedAtMs)> { (j.ClientId, j.SeatedAtMs) };
		if (HostPilots(root, now) && _hostSeatedSince.TryGetValue(root, out var hostSince))
		{
			seated.Add((SyncProtocol.HostClientId, hostSince));
		}
		foreach (var other in _joiners.Values)
		{
			if (other != j && other.Character != null && other.Seat != null && other.SeatRoot == root && IsControlSeat(other.Seat))
			{
				seated.Add((other.ClientId, other.SeatedAtMs));
			}
		}
		ushort pilotId = ShipPilots.Pilot(seated);
		string? pilot = pilotId == j.ClientId ? null
			: pilotId == SyncProtocol.HostClientId ? "the host's player sat down first and flies it"
			: $"ClientId {pilotId} '{(_joiners.TryGetValue(pilotId, out var flier) ? flier.Name : "?")}' sat down first and flies it";
		bool driving = pilot == null;
		if (driving != j.Driving)
		{
			j.Driving = driving;
			_log.Info(driving
				? $"host world sync: ClientId {j.ClientId} '{j.Name}' now flies its ship (NetworkId {(_ids.TryGet(root, out var id) ? id : 0)})"
				: $"host world sync: ClientId {j.ClientId} '{j.Name}' sits in another seat of ship NetworkId {(_ids.TryGet(root, out var otherId) ? otherId : 0)}: {pilot}; "
					+ "its flight input is ignored until then (two pilots made the ship swing between their inputs)");
		}
		return driving;
	}

	private bool HostPilots(Entity root, long now)
	{
		if (_hostPilotScan.TryGetValue(root, out var scan) && now - scan.Ms < 500)
		{
			return scan.HostPilots;
		}
		bool hostPilots = false;
		var stack = new Stack<Entity>();
		stack.Push(root);
		int visited = 0;
		while (stack.Count > 0 && visited < 20000 && !hostPilots)
		{
			var entity = stack.Pop();
			visited++;
			var seat = entity.TryGet<SeatComponent>();
			if (seat != null)
			{
				// Only the host's own player: a character of a player who left stays seated (parked, or loaded from the save)
				// and counted as the host flying, so an online player in another cockpit could neither fly nor switch dampeners.
				var pilot = seat.Pilot;
				if (pilot != null && pilot == _hostCharacterServer && entity.TryGet<CockpitComponent>() != null)
				{
					hostPilots = true;
				}
			}
			var children = entity.TryGet<HierarchyComponent>()?.Children;
			if (children == null)
			{
				continue;
			}
			foreach (var child in children)
			{
				stack.Push(child);
			}
		}
		if (_hostPilotScan.Count > 64)
		{
			_hostPilotScan.Clear();
		}
		_hostPilotScan[root] = (now, hostPilots);
		if (hostPilots)
		{
			_hostSeatedSince.TryAdd(root, now);
		}
		else
		{
			_hostSeatedSince.Remove(root);
		}
		if (_hostSeatedSince.Count > 64)
		{
			_hostSeatedSince.Clear();
		}
		return hostPilots;
	}

	private static Entity? FindPilotedSeat(Entity root, Entity pilot, out int scanned, out string seats)
	{
		scanned = 0;
		var others = new List<string>();
		var stack = new Stack<Entity>();
		stack.Push(root);
		Entity? found = null;
		while (stack.Count > 0 && scanned < 20000)
		{
			var entity = stack.Pop();
			scanned++;
			var seat = entity.TryGet<SeatComponent>();
			if (seat != null)
			{
				if (seat.Pilot == pilot)
				{
					found ??= entity;
				}
				else if (others.Count < 4)
				{
					others.Add(seat.Pilot == null ? "empty" : "other pilot");
				}
			}
			var children = entity.TryGet<HierarchyComponent>()?.Children;
			if (children == null)
			{
				continue;
			}
			foreach (var child in children)
			{
				stack.Push(child);
			}
		}
		seats = others.Count != 0 ? string.Join(", ", others) : found != null ? "ours" : "none";
		return found;
	}

	internal void SyncMenuPause()
	{
		if (_menuPauseFailed)
		{
			return;
		}
		bool keepRunning = !_disabled;
		if (keepRunning == _menuPauseOff)
		{
			return;
		}
		try
		{
			var ticks = _worldSession?.Entity?.TryGet<Keen.Game2.WorldTickComponent>()?._tickManagement;
			if (ticks == null) return;
			ticks.PauseWhenInMenu = !keepRunning;
			_menuPauseOff = keepRunning;
			_log.Info(keepRunning ? "host world sync: hosting keeps the world running while menus are open" : "host world sync: the ESC menu pauses the world again");
		}
		catch (Exception ex)
		{
			_menuPauseFailed = true;
			_log.Warn($"host world sync: cannot switch off pause-in-menu ({ex.GetType().Name}: {ex.Message}); the host's ESC menu will freeze joiners");
		}
	}

	private void UpdateJoiners(InProcessReplicationSessionComponent comp)
	{
		long now = MpClock.NowMs();
		bool refresh = now - _lastAllowedMs >= 250;
		if (refresh)
		{
			_lastAllowedMs = now;
		}
		if (refresh)
		{
			SyncMenuPause();
		}
		foreach (var joiner in _joiners.Values)
		{
			var character = joiner.Character;
			if (character == null)
			{
				continue;
			}
			bool lost = EngineCodec.IsDead(character);
			string how = "removed from the world";
			if (!lost)
			{
				try
				{
					var life = character.TryGet<CharacterLifeComponent>();
					if (life != null && !life.IsAlive)
					{
						lost = true;
						how = "died";
					}
				}
				catch (Exception)
				{
				}
			}
			if (lost)
			{
				joiner.Character = null;
				joiner.NetId = 0u;
				// A dead pilot kept the ship: its seat stayed counted, so nobody else in the ship could fly it.
				if (joiner.Driving && joiner.Seat?.TryGet<CubeBlockComponent>()?.Grid?.Entity is { } deadGrid && _ids.TryGet(deadGrid, out var deadGridId))
				{
					joiner.StopGridId = deadGridId;
				}
				joiner.Seat = null;
				joiner.SeatRoot = null;
				_allowed.TryRemove(joiner.ClientId, out _);
				CharactersLost++;
				_log.Info($"host world sync: ClientId {joiner.ClientId} '{joiner.Name}': character {how}");
				_host?.CharacterLost(joiner.Peer, how);
			}
			else
			{
				if (!refresh && _allowed.ContainsKey(joiner.ClientId))
				{
					continue;
				}
				var allowed = new HashSet<uint>();
				Collect(character, allowed, 0);
				try
				{
					// Seated as of sitting down, not as of the first flight input: the pilot is whoever sat first.
					if (joiner.Seat == null && character.AsInterface<ISeatable>()?.CurrentSeat is SeatComponent sat && sat.Pilot == character)
					{
						joiner.Seat = sat.Entity;
						_log.Info($"host world sync: ClientId {joiner.ClientId} '{joiner.Name}' sat down (seat {(_ids.TryGet(sat.Entity, out var satId) ? satId : 0)}, {(IsControlSeat(sat.Entity) ? "control seat" : "passenger seat")})");
					}
					uint candidateId;
					while (joiner.Candidates.TryDequeue(out candidateId))
					{
						if (joiner.Seat != null || !_ids.TryGet(candidateId, out var candidate))
						{
							continue;
						}
						var root = candidate;
						for (int i = 0; i < 16; i++)
						{
							var parent = root.TryGet<HierarchyComponent>()?.Parent;
							if (parent == null)
							{
								break;
							}
							root = parent;
						}
						long scanNow = MpClock.NowMs();
						if (!joiner.ScannedRoots.TryGetValue(root, out var scannedAt) || scanNow - scannedAt >= 2000)
						{
							joiner.ScannedRoots[root] = scanNow;
							var seatFound = FindPilotedSeat(root, character, out int scanned, out string seats);
							if (seatFound != null)
							{
								joiner.Seat = seatFound;
								_log.Info($"host world sync: ClientId {joiner.ClientId} '{joiner.Name}' is seated (seat {(_ids.TryGet(seatFound, out var id) ? id : 0)}, grid scan {scanned} entities); its input may drive the seat and its grid");
							}
							else if (!joiner.SeatMismatchLogged)
							{
								joiner.SeatMismatchLogged = true;
								_log.Warn($"host world sync: ClientId {joiner.ClientId} '{joiner.Name}' sends input for NetworkId {candidateId} ({candidate.GetComposition()?.Guid}) but no seat in that grid ({scanned} entities scanned; seats: {seats}) is piloted by its character; refused");
							}
						}
					}
					var seat = joiner.Seat;
					if (seat != null)
					{
						if (EngineCodec.IsDead(seat) || seat.TryGet<SeatComponent>()?.Pilot != character)
						{
							_log.Info($"host world sync: ClientId {joiner.ClientId} '{joiner.Name}' left its seat");
							if (joiner.Driving && joiner.SeatRoot != null)
							{
								var leftIds = new List<uint>();
								if (_ids.TryGet(joiner.SeatRoot, out var rootId))
								{
									leftIds.Add(rootId);
								}
								var grid = seat.TryGet<CubeBlockComponent>()?.Grid?.Entity;
								if (grid != null && _ids.TryGet(grid, out var gridId))
								{
									leftIds.Add(gridId);
									joiner.StopGridId = gridId;
								}
								joiner.LeftAtMs = now;
								joiner.LeftGridIds = leftIds.ToArray();
							}
							joiner.Seat = null;
							joiner.SeatRoot = null;
						}
						else
						{
							Collect(seat, allowed, 0);
							var seatRoot = seat;
							for (int j = 0; j < 16; j++)
							{
								var parent = seatRoot.TryGet<HierarchyComponent>()?.Parent;
								if (parent == null)
								{
									break;
								}
								seatRoot = parent;
							}
							if (MayDriveGrid(joiner, seatRoot, now))
							{
								if (_ids.TryGet(seatRoot, out var rootId))
								{
									allowed.Add(rootId);
								}
								var grid = seat.TryGet<CubeBlockComponent>()?.Grid?.Entity;
								if (grid != null && _ids.TryGet(grid, out var gridId))
								{
									allowed.Add(gridId);
								}
							}
						}
					}
				}
				catch (Exception ex)
				{
					_log.WarnOnce("host-seat", $"host world sync: seat check failed ({ex.GetType().Name}: {ex.Message})");
				}
				_allowed[joiner.ClientId] = allowed;
			}
		}

		void Collect(Entity e, HashSet<uint> set, int depth)
		{
			if (_ids.TryGet(e, out var id))
			{
				set.Add(id);
			}
			if (depth <= 8)
			{
				var children = e.TryGet<HierarchyComponent>()?.Children;
				if (children != null)
				{
					foreach (var child in children)
					{
						Collect(child, set, depth + 1);
					}
				}
			}
		}
	}

	public static void CommitPrefix(object __instance)
	{
		var current = Current;
		if (current == null || current._disabled || __instance is not InProcessReplicationSessionComponent comp)
		{
			return;
		}
		if (comp.IsServer)
		{
			try
			{
				if (comp == Volatile.Read(ref current._serverComp))
				{
					current.StampServerBuffer(comp);
				}
				return;
			}
			catch (Exception ex)
			{
				current._log.WarnOnce("host-stamp", $"host world sync: stamping server ticks failed ({ex.GetType().Name}: {ex.Message}); state uses the replay time");
				return;
			}
		}
		if (current._upstreamFailed)
		{
			return;
		}
		try
		{
			current.OnHostClientCommit(comp);
		}
		catch (Exception ex)
		{
			current._upstreamFailed = true;
			current._log.Error($"host world sync: joining players' input/characters failed and are switched off for this session (world sync continues): {ex}");
		}
	}

	private void CollectHostStrings(HostSession host)
	{
		if (_namesFailed)
		{
			return;
		}
		try
		{
			if (HashToStringField?.GetValue(null) is Dictionary<StringHash, string> names)
			{
				object? hashLock = HashLockField?.GetValue(null);
				if (hashLock != null)
				{
					List<string>? added = null;
					lock (hashLock)
					{
						if (names.Count == _lastHashCount)
						{
							return;
						}
						_lastHashCount = names.Count;
						foreach (var entry in names)
						{
							if (_sentHashes.Add(entry.Key.Hash))
							{
								(added ??= new List<string>()).Add(entry.Value);
							}
						}
					}
					if (added != null)
					{
						host.AddHostStrings(added);
					}
					return;
				}
			}
			throw new MissingFieldException("StringHash._hashToString/_lock");
		}
		catch (Exception ex)
		{
			_namesFailed = true;
			_log.Error($"host world sync: cannot read the game's StringHash names ({ex.GetType().Name}: {ex.Message}); joiners may crash on host animation signals");
		}
	}

	private void OnHostClientCommit(InProcessReplicationSessionComponent comp)
	{
		var server = Volatile.Read(ref _serverComp) as InProcessReplicationSessionComponent;
		if (server == null || comp._context.Other?.SessionComponent != server)
		{
			return;
		}
		_hostClientComp = comp;
		UpdateHostNametags(comp);
		UpdateJoinerCharacters(comp);
		DrainHostWork(comp, server, false);
	}

	internal void HeadlessServerWork(InProcessReplicationSessionComponent server)
	{
		if (!HeadlessReplication.Active || _disabled || _upstreamFailed || server.IsUnloading || _serverComp != server) return;
		try { DrainHostWork(server, server, true); }
		catch (Exception ex)
		{
			_upstreamFailed = true;
			_log.Error("dedicated: server input/character processing failed: " + ex);
			throw;
		}
	}

	private void DrainHostWork(InProcessReplicationSessionComponent comp, InProcessReplicationSessionComponent server, bool headless)
	{
		void OnServer(Action action)
		{
			if (headless) action();
			else comp.PostInvoke(_ => action());
		}
		long lap = ServerProfiler.Now;
		OnServer(() => UpdateSocial(server.Session));
		OnServer(() => UpdateAdmin(server.Session));
		OnServer(() => UpdateGps(server.Session));
		OnServer(() => UpdateHacks(server.Session));
		OnServer(() => UpdateWater(server.Session));
		OnServer(() => UpdateTrades(server.Session));
		OnServer(UpdatePunches);
		OnServer(UpdateVoicePositions);
		OnServer(UpdateSeats);
		OnServer(UpdateTurrets);
		Registration next;
		while (_registrations.TryDequeue(out next))
		{
			var r = next;
			OnServer(() => ServerRegister(server, r));
		}
		lap = ServerProfiler.Lap(ServerSection.Social, lap);
		List<WireCommand>? signals = null;
		string? heavy = null;
		lock (_signalSync)
		{
			FlushCoalesced();
			if (_pendingSignals.Count > 0)
			{
				signals = _pendingSignals;
				_pendingSignals = new List<WireCommand>();
			}
			long bytes = 0;
			foreach (var tally in _frameSignalBytes.Values) bytes += tally.Bytes;
			long nowMs = MpClock.NowMs();
			// One frame's signals go out as one ordered message: a big one delays everything after it on a slow link.
			if (bytes >= 65536 && nowMs - _heavyLoggedMs > 5000)
			{
				_heavyLoggedMs = nowMs;
				heavy = $"host world sync: one frame's signals came to {MpLogExtensions.Bytes(bytes)}: " + string.Join(", ", _frameSignalBytes.OrderByDescending(kv => kv.Value.Bytes).Take(6).Select(kv => $"{kv.Key} x{kv.Value.Count} {MpLogExtensions.Bytes(kv.Value.Bytes)}"));
			}
			_frameSignalBytes.Clear();
		}
		if (heavy != null) _log.Info(heavy);
		if (signals != null && _host != null)
		{
			CollectHostStrings(_host);
			var strings = _sids.TakePending();
			if (strings != null)
			{
				_host.AddHostStrings(strings);
			}
			_host.PublishSignals(_hostTick, signals, SignalRoots(signals));
		}
		if (_host != null)
		{
			FlushAddressedSignals(_host);
			FlushDespawns(_host);
		}
		lap = ServerProfiler.Lap(ServerSection.Signals, lap);
		var obsHost = _host;
		if (obsHost != null)
		{
			try
			{
				_observables.Flush((PeerId peer, byte[] msg) => obsHost.PublishLifecycleTo(peer, msg), comp._exchangeSync);
			}
			catch (Exception ex)
			{
				_log.WarnOnce("host-observables-flush", $"host world sync: observables flush failed ({ex.GetType().Name}: {ex.Message}); joiners' screens may stop updating");
			}
		}
		lap = ServerProfiler.Lap(ServerSection.Observables, lap);
		var stories = _stories;
		if (stories != null && stories.Pending)
		{
			var serverSession = server.Session;
			stories.SourceFor = SourceFor;
			stories.Gate = StoryGate;
			stories.RootsFor = _observables.AddRoots;
			stories.Drain(comp, () => _host, () => ServerSpace(server), serverSession, headless);
		}
		lap = ServerProfiler.Lap(ServerSection.Stories, lap);
		Joiner released;
		while (_releaseRequests.TryDequeue(out released))
		{
			var rel = released;
			OnServer(() =>
			{
				try { ServerRelease(server, rel); }
				finally { Interlocked.Decrement(ref _pendingReleases); }
			});
		}
		Joiner spawning;
		while (_spawnRequests.TryDequeue(out spawning))
		{
			var req = spawning;
			OnServer(() => ServerSpawn(server, req));
		}
		foreach (var pending in _joiners.Values)
		{
			if (!pending.ShipPending && pending.ShipSettle == null || pending.ShipQueued) continue;
			pending.ShipQueued = true;
			OnServer(() =>
			{
				pending.ShipQueued = false;
				ServerShip(server, pending);
			});
		}
		lap = ServerProfiler.Lap(ServerSection.Spawns, lap);
		var host = _host;
		if (host == null || !host.AnySynced)
		{
			return;
		}
		var inputs = host.TakeInputs();
		if (inputs.Count == 0)
		{
			return;
		}
		object exchangeSync = comp._exchangeSync;
		if (exchangeSync == null)
		{
			return;
		}
		lock (exchangeSync)
		{
			foreach (var batch in inputs)
			{
				Inject(comp, batch, host);
			}
		}
		ServerProfiler.Lap(ServerSection.Inputs, lap);
	}

	private void UpdateJoinerCharacters(InProcessReplicationSessionComponent comp)
	{
		if (!ControlGuard.Enabled && !ControlGuard.HostFlightGate)
		{
			return;
		}
		int withCharacter = 0;
		foreach (var joiner in _joiners.Values)
		{
			withCharacter += joiner.Character != null ? 1 : 0;
		}
		// Re-resolve every 5th frame, or at once when a character appears or goes.
		if (_joinerCharTick++ % 5 != 0 && withCharacter == _joinerCharCount)
		{
			return;
		}
		_joinerCharCount = withCharacter;
		try
		{
			var characters = new List<Entity>(withCharacter);
			var driven = new List<Entity>();
			foreach (var joiner in _joiners.Values)
			{
				var character = Local(joiner.Character);
				if (character != null)
				{
					characters.Add(character);
				}
				if (!OnePilot)
				{
					continue;
				}
				var seat = joiner.Seat;
				if (seat != null && joiner.Driving && joiner.SeatRoot != null)
				{
					var root = Local(joiner.SeatRoot);
					if (root != null)
					{
						driven.Add(root);
					}
					var grid = Local(seat.TryGet<CubeBlockComponent>()?.Grid?.Entity);
					if (grid != null && grid != driven.LastOrDefault())
					{
						driven.Add(grid);
					}
				}
			}
			foreach (Entity parked in _parked.Keys)
			{
				if (EngineCodec.IsDead(parked)) _parked.TryRemove(parked, out _);
				else if (Local(parked) is Entity local) characters.Add(local);
			}
			ControlGuard.SetJoinerCharacters(characters.ToArray());
			ControlGuard.SetJoinerDriven(driven.ToArray());
		}
		catch (Exception ex)
		{
			_log.WarnOnce("host-control-guard", $"host: resolving the joiners' characters / ships for the control guard failed ({ex.GetType().Name}: {ex.Message}); retrying");
		}

		Entity? Local(Entity? server)
		{
			if (server == null || EngineCodec.IsDead(server))
			{
				return null;
			}
			bool skip = false;
			return comp.ReplayContext.TranslateObject(server, ReplicationFailureStrategy.NullOnAnyFail, ref skip) as Entity;
		}
	}

	private static bool IsClientSettableProperty(CommandLayout l)
	{
		var signalType = l.SignalType;
		if (signalType is not null)
		{
			return ClientSettable.GetOrAdd(signalType, (Type tt) => tt.GetCustomAttribute<SignalSignature>()?.SyncProperty ?? false);
		}
		return false;
	}

	internal static void WriteMarkedData(ReplicationCommandBuffer cb, int stub, Entity sessionEntity, Entity target, int typeId, byte[]? bytes)
	{
		// Layout: two 4-byte object refs (session entity, target), type id, then optional length + payload.
		int length = bytes == null ? 12 : 16 + bytes.Length;
		Span<byte> buffer = length > 512 ? new byte[length] : stackalloc byte[length];
		BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(8), typeId);
		if (bytes != null)
		{
			BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(12), bytes.Length);
			bytes.CopyTo(buffer.Slice(16));
		}
		CommandBuffers.Append(cb, stub, buffer, off => cb.StoreObject(off == 0 ? sessionEntity : target), DataRefOffsets, 1580056798);
	}

	private bool ClientInput(int typeId, ushort clientId)
	{
		if (EngineCodec.IsClientInput(typeId))
		{
			return true;
		}
		_log.WarnOnce($"input-refused/{typeId}", $"input: refused data {EngineCodec.DescribeType(typeId).Name} from ClientId {clientId}: not a client input type");
		return false;
	}

	private void Inject(InProcessReplicationSessionComponent comp, ClientInputBatch b, HostSession host)
	{
		var allowed = _allowed.TryGetValue(b.ClientId, out var allowedIds) ? allowedIds : null;
		var replay = comp.ReplayContext;
		bool headless = HeadlessReplication.Active && comp.IsServer;
		var buffer = headless ? InProcessReplicationSessionComponent.AllocateCB() : ((ReplicationCommandBufferSessionComponent)(object)comp).Buffer;
		// Constructing a writer/reader initializes the static AssemblyOffset read on the next line.
		new ReplicationCommandBufferWriterReader(buffer);
		int assemblyOffset = ReplicationCommandBufferWriterReader.AssemblyOffset;
		int dataApplied = 0;
		int commandsApplied = 0;
		int rejected = 0;
		foreach (var (netId, typeId, bytes) in b.Data)
		{
			if (!ClientInput(typeId, b.ClientId))
			{
				rejected++;
				continue;
			}
			var target = allowed != null && allowed.Contains(netId) ? Local(netId) : null;
			if (target == null || WorldSocial.Access(target, IdentityOf(b.ClientId)) == false)
			{
				rejected++;
				// Input for an entity not yet allowed may be a seat the joiner just sat in: queue it for the seat scan.
				if (_joiners.TryGetValue(b.ClientId, out var seeker) && seeker.Seat == null && seeker.Candidates.Count < 64)
				{
					seeker.Candidates.Enqueue(netId);
				}
			}
			else
			{
				WriteMarkedData(buffer, assemblyOffset + 1, comp.Entity, target, typeId, FightLocked(b.ClientId) && EngineCodec.IsMovementInput(typeId) ? new byte[bytes.Length] : bytes);
				dataApplied++;
			}
		}
		_joiners.TryGetValue(b.ClientId, out var joiner);
		uint stopGrid = joiner != null ? Interlocked.Exchange(ref joiner.StopGridId, 0u) : 0u;
		if (stopGrid != 0 && Local(stopGrid) is Entity stopped)
		{
			// The joiner's own zero flight input on leaving is dropped as "not ours" / not allowed, so the server kept the last thrust/turn.
			foreach (Type flightType in new[] { typeof(Keen.Game2.Simulation.WorldObjects.Movement.ControlData), typeof(Keen.Game2.Simulation.WorldObjects.Movement.AngularControlData) })
			{
				Keen.VRage.DCS.Internal.RuntimeDataInfo.Info flightInfo = Keen.VRage.DCS.Internal.RuntimeDataInfo.Of(flightType);
				WriteMarkedData(buffer, assemblyOffset + 1, comp.Entity, stopped, flightInfo.TypeId, new byte[flightInfo.Size]);
				dataApplied++;
			}
		}
		foreach (var (netId, typeId) in b.Removes)
		{
			if (!ClientInput(typeId, b.ClientId))
			{
				rejected++;
				continue;
			}
			uint[]? leftIds = joiner != null ? joiner.LeftGridIds : null;
			bool justLeft = leftIds != null && Array.IndexOf(leftIds, netId) >= 0 && MpClock.NowMs() - joiner.LeftAtMs < 3000;
			var target = (allowed?.Contains(netId) ?? false) | justLeft ? Local(netId) : null;
			if (target == null || WorldSocial.Access(target, IdentityOf(b.ClientId)) == false)
			{
				rejected++;
				continue;
			}
			WriteMarkedData(buffer, assemblyOffset + 2, comp.Entity, target, typeId, null);
			dataApplied++;
			InputRemovals++;
			if (InputRemovals <= 3 || _removalLogged.Add(typeId))
			{
				_log.Info($"host world sync: ClientId {b.ClientId}: removed {EngineCodec.DescribeType(typeId).Name} on NetworkId {netId} like its game did (first ones / first of each type)");
			}
		}
		var sp = new ObjectSpace
		{
			Name = "host-client",
			IdOf = (Entity e) => !_ids.TryGet(e, out var id) ? null : id,
			Explain = ExplainNetId,
			Resolve = (uint id, bool _) => Local(id),
			Session = comp.Session,
			TranslatedSession = comp.Session
		};
		object serverComp = _serverComp;
		bool guarded = serverComp != null && ReplicationProbe.IsGuarded(serverComp);
		var gate = _gate;
		long actor = gate != null && _identityOf.TryGetValue(b.ClientId, out var identity) ? identity : 0;
		var permissions = actor != 0L ? comp.Session.SessionComponents.TryGet<PermissionsSessionComponent>() : null;
		ReplicationCommandBuffer? echoBuffer = null;
		int echoed = 0;
		ObjectSpace? echoSpace = null;
		foreach (var (signal, cmd) in SuitGate(b))
		{
			var layout = CommandLayouts.ByName(signal);
			string? fail = null;
			if (!guarded)
			{
				fail = "replay guard not armed (see the probe:CommitFrame line)";
			}
			else if (layout == null || !layout.Forwardable)
			{
				fail = "not forwardable here";
			}
			else if (layout.DownstreamOnly)
			{
				fail = "host to joiner only";
			}
			else if (AdminGuard.IsAdminSignal(signal) && !IsAdminClient(b.ClientId))
			{
				fail = "admin only";
				Interlocked.Increment(ref AdminGuard.Refused);
			}
			else if (IsWaterSignal(signal) && !MayEditWater(b.ClientId, actor, comp.Session))
			{
				fail = "water edits need admin or creative";
			}
			else if (SocialGuard.IsOwnershipSignal(signal))
			{
				fail = "ownership changes require the authenticated grid ownership controls";
			}
			else if (cmd.Refs.Any(r => r.Kind == WireKind.Entity && WorldSocial.Access(Local(r.Id), actor) == false))
			{
				fail = "grid access denied by its owner";
			}
			else if (FightLocked(b.ClientId) && signal.Contains("Tool", StringComparison.Ordinal))
			{
				fail = "in a fight scene";
			}
			else if (ControlGuard.Enabled && ControlGuard.IsControllerOnly(layout) && cmd.Refs.Count > 0 && cmd.Refs[0].Kind == WireKind.Entity && (allowed == null || !allowed.Contains(cmd.Refs[0].Id)) && Local(cmd.Refs[0].Id)?.TryGet<CharacterControllableComponent>() != null)
			{
				fail = "not allowed: another player's character (controller-only signal)";
				Interlocked.Increment(ref ControlGuard.HostInjectDropped);
			}
			else if (ControlGuard.IsControllerOnly(layout) && cmd.Refs.Count > 0 && cmd.Refs[0].Kind == WireKind.Entity && _ids.TryGet(cmd.Refs[0].Id, out Entity turret) && turret.TryGet<AutoTargetingComponent>() != null && !Turrets.Controls(turret, b.ClientId))
			{
				fail = "not allowed: a turret this player does not control";
			}
			else
			{
				string? denied;
				if (actor != 0L && cmd.Refs.Count > 0 && cmd.Refs[0].Kind == WireKind.Entity && (denied = gate.Check(gate.SignalValidator(signal), Local(cmd.Refs[0].Id), actor, permissions)) != null)
				{
					fail = "not allowed: " + denied;
				}
				else if (!SignalCodec.Write(buffer, layout, cmd, sp, _scratch, ref fail) && fail == null)
				{
					fail = "?";
				}
			}
			if (fail != null)
			{
				rejected++;
				NoteC2SDrop(signal, fail);
				continue;
			}
			if (_c2sApplied.AddOrUpdate(ShortName(signal), 1L, (string _, long c) => c + 1) == 1)
			{
				_log.Info($"host world sync: ClientId {b.ClientId}: joiner signal {ShortName(signal)} handed to the server (first one)");
			}
			commandsApplied++;
			if (!IsClientSettableProperty(layout))
			{
				continue;
			}
			// A client-set property also has to reach the host's own client and every other joiner.
			if (!headless)
			{
				string? echoFail = null;
				echoBuffer ??= InProcessReplicationSessionComponent.AllocateCB();
				echoSpace ??= new ObjectSpace
				{
					Name = "host-client-inbound",
					IdOf = (Entity e) => !_ids.TryGet(e, out var id) ? null : id,
					Explain = ExplainNetId,
					Resolve = (uint id, bool _) => id == 0 || !_ids.TryGet(id, out Entity entity) ? null : entity,
					Session = (serverComp as InProcessReplicationSessionComponent)?.Session,
					TranslatedSession = (serverComp as InProcessReplicationSessionComponent)?.Session
				};
				if (SignalCodec.Write(echoBuffer, layout, cmd, echoSpace, _scratch, ref echoFail))
				{
					echoed++;
				}
			}
			var key = host.SignalKeys.GetOrAdd(layout.Name, layout.Def(0).PayloadBytes, layout.Signature);
			lock (_signalSync)
			{
				_pendingSignals.Add(new WireCommand
				{
					Key = key.Key,
					Payload = cmd.Payload,
					Refs = cmd.Refs
				});
			}
			if (_echoedProps.AddOrUpdate(ShortName(signal), 1L, (string _, long c) => c + 1) == 1)
			{
				_log.Info($"host world sync: ClientId {b.ClientId}: {ShortName(signal)} is a client-set property: shown on the host and sent to every joiner (first one)");
			}
		}
		if (headless)
		{
			if (dataApplied + commandsApplied > 0) comp.ReplayBuffer(buffer);
			else InProcessReplicationSessionComponent.ReturnCB(buffer);
		}
		if (echoBuffer != null)
		{
			if (echoed > 0)
			{
				comp.ReplayBuffer(echoBuffer);
			}
			else
			{
				InProcessReplicationSessionComponent.ReturnCB(echoBuffer);
			}
		}
		InputDataApplied += dataApplied;
		InputCommandsApplied += commandsApplied;
		InputRejected += rejected;
		host.InputApplied(b.Peer, dataApplied, commandsApplied, rejected);

		Entity? Local(uint netId)
		{
			if (netId == 0 || !_ids.TryGet(netId, out Entity entity))
			{
				return null;
			}
			if (headless) return EngineCodec.IsDead(entity) ? null : entity;
			bool skip = false;
			return replay.TranslateObject(entity, ReplicationFailureStrategy.NullOnAnyFail, ref skip) as Entity;
		}
	}

	private void ServerSpawn(InProcessReplicationSessionComponent server, Joiner j)
	{
		var host = _host;
		if (host == null)
		{
			return;
		}
		try
		{
			if (!_joiners.TryGetValue(j.ClientId, out var current) || current != j)
			{
				return;
			}
			RootPerPlayerData(server, j);
			if (j.Character != null && !EngineCodec.IsDead(j.Character))
			{
				host.CharacterAssigned(j.Peer, j.NetId, "existing character");
				return;
			}
			var session = server.Session;
			var respawn = session.TryGet<IRespawn>();
			if (respawn == null)
			{
				host.CharacterFailed(j.Peer, "the host world has no respawn service (IRespawn)");
				return;
			}
			var identityId = IdentityFor(j.ClientId);
			var others = new HashSet<Entity>(_joiners.Values.Select(x => x.Character).Where(x => x != null), ReferenceEqualityComparer.Instance);
			var alive = new List<Entity>();
			Entity? hostCharacter = null;
			if (session.TryFillAliveCharactersHotfix(alive, preserveOrder: true))
			{
				hostCharacter = alive.FirstOrDefault(x => !others.Contains(x) && (long?)x.TryGet<OwnershipComponent>()?.Owner == -1559595548) ?? alive.FirstOrDefault(x => !others.Contains(x));
			}
			Entity? character = null;
			var saved = (long)identityId != PlayerIdentities.HotfixIdentity ? SavedCharacter(session, identityId, alive, others) : null;
			string via;
			if (saved != null)
			{
				_parked.TryRemove(saved, out _);
				character = saved;
				RestoreSeat(j, saved);
				via = "its saved character (the host's save kept it; same identity)";
			}
			else if (j.ShipPending || !ChosenSpawn(server.Session, host, j, respawn, identityId, hostCharacter, out character, out via))
			{
				return;
			}
			Assign(host, j, character, identityId, hostCharacter, via);
		}
		catch (Exception ex)
		{
			_log.Error($"host world sync: spawning a character for ClientId {j.ClientId} failed: {ex}");
			host.CharacterFailed(j.Peer, ex.GetType().Name + ": " + ex.Message);
		}
	}

	private readonly System.Collections.Concurrent.ConcurrentDictionary<Entity, byte> _parked = new(ReferenceEqualityComparer.Instance);

	/// <summary>Clears held movement/flight input so a left-behind character or ship does not keep moving.</summary>
	private static void StopInput(Entity entity)
	{
		if (entity.Data.TryGet<Keen.Game2.Simulation.WorldObjects.Movement.ControlData>(out _)) entity.Data.Set(default(Keen.Game2.Simulation.WorldObjects.Movement.ControlData));
		if (entity.Data.TryGet<Keen.Game2.Simulation.WorldObjects.Movement.AngularControlData>(out _)) entity.Data.Set(default(Keen.Game2.Simulation.WorldObjects.Movement.AngularControlData));
		if (entity.Data.TryGet<Keen.Game2.Simulation.WorldObjects.Characters.AdvancedCharacterControlData>(out _)) entity.Data.Set(default(Keen.Game2.Simulation.WorldObjects.Characters.AdvancedCharacterControlData));
	}

	private static Entity? SavedCharacter(Session session, IdentityId identity, List<Entity> alive, HashSet<Entity> others)
	{
		Entity? lastControlled = null;
		try
		{
			lastControlled = session.TryGet<IPerPlayerData>()?.GetPerPlayerData<ControlPerPlayerData>(identity).LastControlledEntity;
		}
		catch (Exception)
		{
		}
		// Prefer the character the identity last controlled, else any free one it owns.
		Entity? owned = null;
		foreach (var character in alive)
		{
			if (others.Contains(character) || EngineCodec.IsDead(character) || EngineCodec.IsClosing(session, character))
			{
				continue;
			}
			var ownership = character.TryGet<OwnershipComponent>();
			if (ownership != null && (long)ownership.Owner == (long)identity)
			{
				if (character == lastControlled)
				{
					return character;
				}
				owned ??= character;
			}
		}
		return owned;
	}

	// A leaving player's character stays in the world (with its position, inventory, skin and seat) and is saved with
	// it; their next join takes it over again through SavedCharacter instead of respawning at a medbay.
	private void ServerRelease(InProcessReplicationSessionComponent server, Joiner j)
	{
		try
		{
			Entity character = j.Character;
			j.Character = null;
			if (character != null && !EngineCodec.IsDead(character) && !EngineCodec.IsClosing(server.Session, character))
			{
				StopInput(character);
				Entity seatGrid = j.Seat?.TryGet<CubeBlockComponent>()?.Grid?.Entity;
				if (seatGrid != null) StopInput(seatGrid);
				if (j.SeatRoot != null && j.SeatRoot != seatGrid) StopInput(j.SeatRoot);
				_parked[character] = 0;
				CharactersReleased++;
				_log.Info($"host world sync: ClientId {j.ClientId} '{j.Name}' left; their character (NetworkId {j.NetId}) stays where it is for their next join");
			}
		}
		catch (Exception ex)
		{
			_log.Warn($"host world sync: releasing the character of ClientId {j.ClientId} failed: {ex.GetType().Name}: {ex.Message}");
		}
	}

	public static void StagedBundlePrefix(ServerSceneContext.ServerToClientEntityReplicationData data) => BundlePostfix(data);

	public static void BundlePostfix(object __result)
	{
		var current = Current;
		if (current == null || current._disabled)
		{
			return;
		}
		var host = current._host;
		if (host == null || !host.AnySynced)
		{
			return;
		}
		if (__result is not ServerSceneContext.ServerToClientEntityReplicationData data)
		{
			return;
		}
		try
		{
			current.ForwardSpawn(data);
		}
		catch (Exception e)
		{
			current.Fail("BuildAndReset postfix", e);
		}
	}

	private void ForwardSpawn(ServerSceneContext.ServerToClientEntityReplicationData data)
	{
		if (data.EntityObjectBuilders.Count == 0 && data.SkippedEntities.Count == 0)
		{
			return;
		}
		var stopwatch = Stopwatch.StartNew();
		foreach (var entity in data.EntityObjectBuilders.Keys)
		{
			_ids.GetOrAssign(entity);
		}
		var proxies = new NetIdProxyContext(e => (int)_ids.GetOrAssign(e), null);
		var blobs = new HashSet<Blob>();
		var msg = new EntityBundleMessage
		{
			Kind = 1,
			HostTick = _hostTick,
			Bundle = data.ServerBundle.Entities.Count > 0 ? EngineCodec.SerializeBundle(data.ServerBundle, proxies, blobs) : Array.Empty<byte>(),
			Entities = EngineCodec.Locate(data.ServerBundle, data.EntityObjectBuilders, e => _ids.GetOrAssign(e), out var unlocated)
		};
		msg.RequiredBlobs = EngineCodec.Ids(blobs);
		foreach (var entityData in data.EntityData)
		{
			var builder = entityData.Value;
			var entityBlobs = EngineCodec.Blobs(ref builder, Keys);
			if (entityBlobs.Count > 0)
			{
				msg.Data.Add((_ids.GetOrAssign(entityData.Key), entityBlobs));
			}
		}
		foreach (var parent in data.EntityParents)
		{
			msg.Parents.Add((_ids.GetOrAssign(parent.Key), _ids.GetOrAssign(parent.Value)));
		}
		foreach (var staged in data.StagedEntities)
		{
			msg.Staged.Add(_ids.GetOrAssign(staged));
		}
		foreach (var skipped in data.SkippedEntities)
		{
			msg.Skipped.Add(_ids.GetOrAssign(skipped));
		}
		byte[] wire = msg.Encode();
		var host = _host;
		host?.PublishLifecycle(wire, StreamSpawnFilter(data.EntityObjectBuilders));
		SpawnBundles++;
		SpawnBytes += wire.Length;
		if (blobs.Count > 0 && host != null)
		{
			SendSpawnBlobs(host, blobs);
		}
		if (data.EntityObjectBuilders.Count >= 20 || SpawnBundles <= 3 || blobs.Count > 0)
		{
			_log.Info($"host world sync: spawn bundle {data.EntityObjectBuilders.Count} entities ({data.StagedEntities.Count} staged, {data.SkippedEntities.Count} skipped, {unlocated} unlocated, {blobs.Count} offloaded blobs), {MpLogExtensions.Bytes(wire.Length)} on the wire ({MpLogExtensions.Bytes(msg.RawBodyBytes)} raw: engine bundle {MpLogExtensions.Bytes(msg.Bundle.Length)}, entity data {MpLogExtensions.Bytes(msg.DataBytes)}) in {stopwatch.Elapsed.TotalMilliseconds:0.0} ms");
		}
	}

	private void SendSpawnBlobs(HostSession host, HashSet<Blob> blobs)
	{
		var offloading = _offloading;
		var list = blobs.ToList();
		Task.Run(() =>
		{
			try
			{
				Dictionary<Guid, byte[]> loaded;
				int failed;
				string firstError;
				if (offloading != null)
				{
					loaded = EngineCodec.LoadBlobs(offloading, list, BlobLoadTimeout, out failed, out firstError);
				}
				else
				{
					loaded = list.ToDictionary(EngineCodec.Id, (Blob _) => (byte[])null);
					failed = list.Count;
					firstError = "no data offloading service on the host";
				}
				host.PublishBlobs(loaded);
				long bytes = loaded.Values.Sum((byte[] d) => (long)(d?.Length ?? 0));
				Interlocked.Add(ref SpawnBlobs, loaded.Count);
				Interlocked.Add(ref SpawnBlobBytes, bytes);
				if (failed > 0)
				{
					_log.Error($"host world sync: {failed} offloaded blob(s) of a spawn bundle could not be loaded (first: {firstError})");
				}
			}
			catch (Exception ex)
			{
				_log.Error($"host world sync: sending a spawn bundle's offloaded blobs failed: {ex}");
			}
		});
	}

	private void StampServerBuffer(InProcessReplicationSessionComponent server)
	{
		var buffer = ((ReplicationCommandBufferSessionComponent)(object)server).Buffer;
		if (buffer != null)
		{
			double stamp = _tickClock.Next(MpClock.NowMsExact());
			_bufferStamp.AddOrUpdate(buffer, new StrongBox<uint>((uint)(long)Math.Round(stamp)));
			StampedBuffers++;
		}
	}

	public void OnPostData(Entity? source, int typeId, ReadOnlySpan<byte> data, ReplicationCommandBuffer cb)
	{
		var host = _host;
		if (host == null || !host.AnySynced || _disabled)
		{
			return;
		}
		if (source == null || !_ids.TryGet(source, out var id))
		{
			StateUnknownEntity++;
			return;
		}
		var key = Keys.GetOrAdd(typeId, EngineCodec.DescribeType);
		// A tag's only news is that it is there. On the lossy state lane it could be lost, or land after a later removal
		// on the lifecycle lane (dampeners shown on while the host had them off), so it goes the reliable way too.
		if (IsTag(typeId))
		{
			if (_tags.Add((id, typeId))) host.PublishDataTag(id, key.Key);
			return;
		}
		uint hostMs;
		if (_bufferStamp.TryGetValue(cb, out var stamp))
		{
			hostMs = stamp.Value;
		}
		else
		{
			hostMs = (uint)MpClock.NowMs();
			UnstampedPostData++;
		}
		host.PublishState(new StateEntry(id, key.Key, (uint)Interlocked.Increment(ref _version), data.ToArray(), hostMs, RootIdOf(source)));
	}

	public void OnRemoveData(Entity? source, int typeId)
	{
		var host = _host;
		if (source == null || !_ids.TryGet(source, out var id))
		{
			return;
		}
		_tags.Remove((id, typeId));
		if (host != null && host.AnySynced && !_disabled)
		{
			var key = Keys.GetOrAdd(typeId, EngineCodec.DescribeType);
			host.PublishDataRemove(new (uint, ushort)[] { (id, key.Key) });
		}
	}

	internal static bool IsTag(int typeId) => TagTypes.GetOrAdd(typeId, static t =>
		Keen.VRage.DCS.Internal.RuntimeDataInfo.Of(t).RuntimeType is { IsValueType: true } type
		&& type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Length == 0);

	private bool CappedContactSignal(CommandLayout l, ReplicationCommandBuffer cb, ReadOnlySpan<byte> payload)
	{
		if ((!l.Name.EndsWith("+SendContactImpulseRawDataSignal", StringComparison.Ordinal) && !l.Name.EndsWith("+OnContactSignal", StringComparison.Ordinal)) || payload.Length < 4)
		{
			return false;
		}
		int targetRef = BinaryPrimitives.ReadInt32LittleEndian(payload);
		if (targetRef >= 0)
		{
			object target = cb.ReadObject(targetRef);
			if (target != null)
			{
				long now = MpClock.NowMs();
				(int, object) key = (l.Stub, target);
				if (_contactLast.TryGetValue(key, out var last) && now - last < ContactCapMs)
				{
					ContactCapped++;
					return true;
				}
				if (_contactLast.Count > 4096)
				{
					_contactLast.Clear();
				}
				_contactLast[key] = now;
				return false;
			}
		}
		return false;
	}

	public void OnSignal(ReplicationCommandBuffer cb, int stub, ReadOnlySpan<byte> payload)
	{
		var layout = CommandLayouts.ByStub(stub);
		if (layout == null)
		{
			return;
		}
		if (layout.Name.StartsWith("Keen.VRage.Multiplayer.Replications.ObservableObjectReplicationSessionComponent+", StringComparison.Ordinal))
		{
			string name = layout.Name;
			bool release = name == "Keen.VRage.Multiplayer.Replications.ObservableObjectReplicationSessionComponent+ReleaseReplicatedObjectSignal" || name == "Keen.VRage.Multiplayer.Replications.ObservableObjectReplicationSessionComponent+ReleaseReplicatedCollectionSignal";
			if (!release || payload.Length < 12 || _disabled)
			{
				return;
			}
			try
			{
				int keyRef = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(8));
				_observables.OnRelease(keyRef >= 0 ? cb.ReadObject(keyRef) : null, layout.Name == "Keen.VRage.Multiplayer.Replications.ObservableObjectReplicationSessionComponent+ReleaseReplicatedCollectionSignal", HeadlessReplication.Active ? (_serverComp as InProcessReplicationSessionComponent)?.ReplayContext : _hostClientComp?.ReplayContext);
				return;
			}
			catch (Exception ex)
			{
				_log.WarnOnce("host-observable-release", $"host world sync: reading an observable release failed ({ex.GetType().Name}: {ex.Message})");
				return;
			}
		}
		var host = _host;
		if (host == null || !host.AnySynced || _disabled)
		{
			return;
		}
		if (!layout.Forwardable)
		{
			NoteS2CDrop(layout.Name, layout.Why ?? "not forwardable");
			return;
		}
		if (IsHostPlayerEvent(layout.Name))
		{
			NoteS2CDrop(layout.Name, "the host player's own event");
			return;
		}
		if (IsAddressedSignal(layout, cb, payload, out var addressee) && addressee == null)
		{
			NoteS2CDrop(layout.Name, "for no player (the host's own)");
			return;
		}
		if (CappedContactSignal(layout, cb, payload))
		{
			NoteS2CDrop(layout.Name, $"rate-capped (one per {ContactCapMs} ms per entity)");
			return;
		}
		var key = host.SignalKeys.GetOrAdd(layout.Name, layout.Def(0).PayloadBytes, layout.Signature);
		var space = _tapSpace ??= new ObjectSpace
		{
			Name = "server",
			IdOf = (Entity e) => !_ids.TryGet(e, out var id) ? null : id,
			Explain = ExplainNetId,
			Resolve = (uint _, bool _) => (Entity?)null
		};
		space.ClearLocalIds();
		string? fail = null;
		var command = SignalCodec.Encode(layout, key.Key, payload, i => cb.ReadObject(i), space, _tapScratch, out _, ref fail);
		if (command == null)
		{
			NoteS2CDrop(layout.Name, fail ?? "?");
			return;
		}
		if (layout.StringIdOffsets.Length != 0)
		{
			_sids.NoteHost(command.Payload, layout.StringIdOffsets);
		}
		lock (_signalSync)
		{
			if (addressee != null) _addressedSignals.Add((addressee.Peer, command));
			else QueueSignal(layout, command);
			var name = ShortName(layout.Name);
			_frameSignalBytes.TryGetValue(name, out var tally);
			_frameSignalBytes[name] = (tally.Count + 1, tally.Bytes + command.Payload.Length + 4 * command.Refs.Count);
		}
		SignalsForwarded++;
		if (_s2cSent.AddOrUpdate(ShortName(layout.Name), 1L, (string _, long c) => c + 1) == 1)
		{
			_log.Info("host world sync: forwarding S2C signal " + ShortName(layout.Name) + " to joiners (first one)");
		}
	}

	internal static bool IsHostPlayerEvent(string fullName)
	{
		string name = ShortName(fullName);
		if (name.Contains("LocalPlayer", StringComparison.Ordinal))
		{
			return true;
		}
		foreach (string prefix in HostPlayerEventPrefixes)
		{
			if (name.StartsWith(prefix, StringComparison.Ordinal))
			{
				return true;
			}
		}
		return false;
	}

	private void NoteS2CDrop(string name, string why)
	{
		SignalsDropped++;
		_s2cDrops.AddOrUpdate(ShortName(name) + ": " + why, 1L, (string _, long c) => c + 1);
	}

	private void NoteC2SDrop(string name, string why) => _c2sDrops.AddOrUpdate(ShortName(name) + ": " + why, 1L, (string _, long c) => c + 1);

	private static string ShortName(string full)
	{
		int dot = full.LastIndexOf('.');
		if (dot < 0)
		{
			return full;
		}
		return full[(dot + 1)..];
	}

	private static string Top(ConcurrentDictionary<string, long> d, int n)
	{
		if (!d.IsEmpty)
		{
			return string.Join(", ", from kv in d.OrderByDescending(kv => kv.Value).Take(n)
				select $"{kv.Key} x{kv.Value}");
		}
		return "none";
	}

	private ObjectSpace ServerSpace(InProcessReplicationSessionComponent server)
	{
		var replay = server.ReplayContext;
		return new ObjectSpace
		{
			Name = "server",
			IdOf = (Entity e) => !_ids.TryGet(e, out var id) ? null : id,
			Explain = ExplainNetId,
			Resolve = (uint id, bool _) => !_ids.TryGet(id, out Entity entity) ? null : entity,
			Session = server.Session,
			ObservableId = o => _observables.OidOnServer(o, replay),
			ResolveObservable = (oid, inCollection) => _observables.ResolveOnServer(oid, inCollection, replay)
		};
	}

	/// <summary>Sends a server story addressed to a joiner (discovery/encounter/sector notifications) to run on their PC.</summary>
	internal bool ForwardServerStory(ushort clientId, IAsyncStateMachine stateMachine)
	{
		var host = _host;
		if (host == null || !_joiners.TryGetValue(clientId, out var j) || Volatile.Read(ref _serverComp) is not InProcessReplicationSessionComponent server)
		{
			return false;
		}
		var space = ServerSpace(server);
		StoryType? story = StoryState.Find(stateMachine.GetType());
		string? fail = story is { HostMayStart: true } ? null : "not a story joiners accept from the host";
		List<(string, WireValue)>? fields = fail != null ? null : StoryState.EncodeArguments(story!, stateMachine, space, out fail);
		if (fields == null || space.LocalIds.Count > 0)
		{
			_log.WarnOnce("server-story-" + stateMachine.GetType().FullName, $"host world sync: server story {StoryState.Name(ObjectCodec.TypeName(stateMachine.GetType()))} for ClientId {clientId} cannot travel ({fail ?? "refers to an entity the joiner does not have"}); skipped");
			return false;
		}
		if (space.Observed.Count > 0)
		{
			_observables.AddRoots(j.Peer, space.Observed.ToList());
		}
		host.SendStoryMove(j.Peer, new StoryMove
		{
			StoryId = ClientStories.ServerOrigin | (uint)Interlocked.Increment(ref _serverStoryIds),
			StateMachineType = ObjectCodec.TypeName(stateMachine.GetType()),
			Fields = fields
		});
		return true;
	}

	/// <summary>Streams a joiner's own research, colonization, contracts, credits, GPS, discoveries, tool modes and paint palette live instead of only at join.</summary>
	private void RootPerPlayerData(InProcessReplicationSessionComponent server, Joiner j)
	{
		if (!_identityOf.TryGetValue(j.ClientId, out long identity))
		{
			return;
		}
		InProcessPerPlayerDataSessionComponent? ppd = server.Session.SessionComponents.TryGet<InProcessPerPlayerDataSessionComponent>();
		if (ppd == null || !((CollectionDictionary<IdentityId, List<object>, object>)(object)ppd._perPlayerData).TryGet(new IdentityId(identity), out List<object> items))
		{
			return;
		}
		var roots = new List<uint>();
		foreach (object item in items)
		{
			if (!HostIdentities.LiveTypes.Any(t => t.IsInstanceOfType(item)))
			{
				continue;
			}
			try
			{
				if (_observables.OidOnServer(item, server.ReplayContext) is uint oid)
				{
					roots.Add(oid);
				}
			}
			catch (Exception ex)
			{
				_log.WarnOnce("host-ppd-root-" + item.GetType().Name, $"host world sync: {item.GetType().Name} of ClientId {j.ClientId} cannot stream ({ex.GetType().Name}: {ex.Message}); it stays as copied at join");
			}
		}
		if (roots.Count > 0)
		{
			_observables.AddRoots(j.Peer, roots);
		}
	}

	public void OnClientStory(PeerId client, ushort clientId, StoryMove move) => (_stories ??= new HostStories(_log)).Enqueue(client, clientId, move);

	public void ForgetStories(PeerId client)
	{
		ForgetStream(client);
		_stories?.Forget(client);
		_observables.Forget(client);
	}

	public void OnObservableEdits(PeerId client, ushort clientId, IReadOnlyList<MemberEdit> edits) => _observables.QueueEdits(client, clientId, edits);

	public void OnInvoke(Delegate callback)
	{
		if (_host == null || _disabled)
		{
			return;
		}
		string name = callback.Method.Name;
		bool removed = name.Contains("OnBeforeEntityRemovedFromScene", StringComparison.Ordinal);
		bool destroyed = !removed && name.Contains("OnBeforeEntityDestroyed", StringComparison.Ordinal);
		if (!removed && !destroyed)
		{
			return;
		}
		var entity = CapturedEntity(callback.Target);
		if (entity == null || !_ids.TryGet(entity, out var id))
		{
			return;
		}
		// Mode 0 despawns (the entity may come back into range); mode 1 forgets it for good.
		lock (_despawnSync)
		{
			_pendingDespawns.Add((id, removed ? (byte)0 : (byte)1, removed ? null : entity, 0u));
		}
	}

	private void FlushDespawns(HostSession host)
	{
		List<(uint Id, byte Mode, Entity? Forgotten, uint Root)> pending;
		lock (_despawnSync)
		{
			if (_pendingDespawns.Count == 0)
			{
				return;
			}
			pending = _pendingDespawns;
			_pendingDespawns = new();
		}
		// One message per run of the same mode: an explosion's hundred removals were a hundred ordered messages, and the
		// joiner takes at most 64 a frame.
		var run = new List<uint>();
		var runRoots = new List<uint>();
		byte runMode = 0;
		foreach (var (id, mode, forgotten, root) in pending)
		{
			if (run.Count > 0 && mode != runMode)
			{
				host.PublishDespawn(run.ToArray(), runMode, runRoots.ToArray());
				run.Clear();
				runRoots.Clear();
			}
			runMode = mode;
			run.Add(id);
			// A joiner skips ids it streamed out (roots); their children never reached it, and it ignores unknown ids.
			runRoots.Add(id);
			ForgetCoalesced(id);
			if (mode == 1)
			{
				ForgetStreamedRoot(id);
			}
			if (mode == 0)
			{
				Despawns++;
				continue;
			}
			Forgets++;
			if (forgotten != null)
			{
				_ids.Remove(forgotten, out _);
				RememberForgotten(id, forgotten);
			}
		}
		if (run.Count > 0)
		{
			host.PublishDespawn(run.ToArray(), runMode, runRoots.ToArray());
		}
	}

	private void RememberForgotten(uint id, Entity entity)
	{
		string what;
		try
		{
			what = (entity.GetComposition()?.Guid.ToString() ?? "?") ?? "";
		}
		catch (Exception)
		{
			what = "?";
		}
		lock (_forgotten)
		{
			if (_forgotten.Count >= 8192)
			{
				_forgotten.Clear();
			}
			_forgotten[id] = (MpClock.NowMs(), what);
		}
	}

	internal string ExplainNetId(uint id)
	{
		if (id == 0)
		{
			return " (null)";
		}
		if (id >= _ids.NextId)
		{
			return " (never assigned by this host)";
		}
		lock (_forgotten)
		{
			if (_forgotten.TryGetValue(id, out var forgotten))
			{
				return $" (destroyed on the server {(double)(MpClock.NowMs() - forgotten.Ms) / 1000.0:0.0} s ago: {forgotten.What})";
			}
		}
		return " (assigned earlier, no longer mapped on the host)";
	}

	private Entity? CapturedEntity(object? target)
	{
		if (target == null)
		{
			return null;
		}
		return _closureEntityField.GetOrAdd(target.GetType(), (Type t) =>
		{
			foreach (var field in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
			{
				if (field.FieldType == typeof(Entity))
				{
					return field;
				}
			}
			return null;
		})?.GetValue(target) as Entity;
	}

	public string SignalStats() =>
		$"S2C signals top: {Top(_s2cSent, 8)}; S2C dropped: {Top(_s2cDrops, 8)}; C2S applied: {Top(_c2sApplied, 8)}; C2S rejected: {Top(_c2sDrops, 6)}; "
		+ ReplayGuard.Describe()
		+ $"; 0.7.6: {AudioGate.Describe()}; {ToolShadowGuard.Describe()}; {ToolTargetDiagnostics.Describe()}; joiner data removals applied {InputRemovals}; {BuildGuards.Describe()}; {ControlGuard.Describe()}; 0.8.2: state stamped by server tick {StampedBuffers} ticks ({UnstampedPostData} unstamped, tick {_tickClock.PeriodMs:0.0} ms, stamp resyncs {_tickClock.Resyncs}), passenger ship actions refused {PassengerActionsRefused}; 0.8.3: contact signals rate-capped {ContactCapped}, StringIds announced {_sids.Announced}; 0.15.4: per-entity signals {_coalescer.Describe()}";

	public string Stats() =>
		$"NetIds {_ids.Count}, type keys {Keys.Count}, spawn bundles {SpawnBundles} ({MpLogExtensions.Bytes(SpawnBytes)}; blobs {SpawnBlobs} {MpLogExtensions.Bytes(SpawnBlobBytes)}), despawns {Despawns}, forgets {Forgets}, joiners {_joiners.Count} (characters spawned {CharactersSpawned}, released {CharactersReleased}, lost {CharactersLost}), joiner input applied {InputDataApplied} data / {InputCommandsApplied} commands, rejected {InputRejected}, S2C signals forwarded {SignalsForwarded} dropped {SignalsDropped}, {_stories?.Describe() ?? "stories: none"}, state for unknown entities {StateUnknownEntity}; {_observables.Describe()}; "
		+ (_identities != null
			? _identities.Describe() + ", players " + string.Join(", ", _identityOf.Select(kv => $"ClientId {kv.Key} = {kv.Value}"))
				+ (TeamPermissions.Enabled ? $", team-grid overrides {TeamPermissions.Overrides}" : "")
				+ (_gate != null ? $", strict-grid checks {_gate.Checked} refused {_gate.Refused}" : "")
			: "identities off");

	private void UpdateNametagRoster(InProcessReplicationSessionComponent comp)
	{
		if (_rosterFailed || !NametagOverlay.Enabled)
		{
			return;
		}
		long now = MpClock.NowMs();
		if (now - _lastRosterMs < RosterIntervalMs && !_rosterSilentChanged)
		{
			return;
		}
		_lastRosterMs = now;
		_rosterSilentChanged = false;
		try
		{
			var online = new List<(ushort, uint, long, string)>();
			var onlineCharacters = new HashSet<Entity>(ReferenceEqualityComparer.Instance);
			foreach (var joiner in _joiners.Values)
			{
				var character = joiner.Character;
				bool live = character != null && !EngineCodec.IsDead(character);
				if (live) onlineCharacters.Add(character);
				online.Add((joiner.ClientId, live ? joiner.NetId : 0, IdentityOf(joiner.ClientId), Tagged(IdentityOf(joiner.ClientId), joiner.Name)));
			}
			// Players still loading in have no joiner yet; their parked body must not read as offline to them.
			foreach (var player in _host?.Players() ?? Array.Empty<HostSession.PlayerRecord>())
			{
				if (!_joiners.ContainsKey(player.ClientId)) online.Add((player.ClientId, 0, IdentityOf(player.ClientId), ""));
			}
			var hostCharacter = _hostCharacterServer;
			if (hostCharacter != null && !EngineCodec.IsDead(hostCharacter) && !onlineCharacters.Contains(hostCharacter) && _ids.TryGet(hostCharacter, out var hostId))
			{
				online.Add((SyncProtocol.HostClientId, hostId, PlayerIdentities.HotfixIdentity, Tagged(PlayerIdentities.HotfixIdentity, HostNametags.HostName)));
			}
			var bodies = new List<(uint, long)>();
			var alive = new List<Entity>();
			if (comp.Session.TryFillAliveCharactersHotfix(alive, preserveOrder: false))
			{
				foreach (var character in alive)
				{
					if (onlineCharacters.Contains(character) || EngineCodec.IsDead(character) || !_ids.TryGet(character, out var netId)) continue;
					bodies.Add((netId, (long?)character.TryGet<OwnershipComponent>()?.Owner ?? 0));
				}
			}
			var names = new Dictionary<long, string>();
			HashSet<long> silent;
			var social = Social;
			if (social == null) silent = new HashSet<long>();
			else lock (social.Sync)
			{
				silent = new HashSet<long>(social.State.Silent);
				var owners = new HashSet<long>(bodies.Select(b => b.Item2));
				foreach (var player in social.State.Players)
				{
					if (owners.Contains(player.Identity)) names[player.Identity] = Tagged(player.Identity, player.Name);
				}
			}
			HostNametags.Roster.Set(PlayerTag.Build(NametagOverlay.Enabled, online, bodies, names, silent));
		}
		catch (Exception ex)
		{
			_rosterFailed = true;
			_log.Error($"nametags(host): building the player list failed, players get no nametags this session (game unaffected): {ex}");
		}
	}

	private void ResolveHostCharacter(InProcessReplicationSessionComponent comp)
	{
		try
		{
			var local = (comp.Session.SessionComponents.TryGet<ClientPlayersSessionComponent>()?.LocalPlayerController)?.GetTopControllable<CharacterControllableComponent>()?.Entity;
			if (local != null && comp.ReplayContext.HasPairingLocal(local) && comp.ReplayContext.TranslateLocalToRemote(local) is Entity server && server != _hostCharacterServer)
			{
				_hostCharacterServer = server;
				if (!_hostCharacterLogged)
				{
					_hostCharacterLogged = true;
					_log.Info("nametags(host): the host's character is the one its own client controls (" + (_ids.TryGet(server, out var id) ? $"NetworkId {id}" : "not synced yet") + ")");
				}
			}
		}
		catch (Exception ex)
		{
			_log.WarnOnce("host-character-resolve", $"nametags(host): finding the host's own character failed ({ex.GetType().Name}: {ex.Message}); its tag may be missing");
		}
	}

	private void UpdateHostNametags(InProcessReplicationSessionComponent comp)
	{
		if (!NametagOverlay.Enabled)
		{
			// HostPilots needs the host's character with nametags off too.
			if (_hostTagTick++ % HostTagResolveTicks == 0) ResolveHostCharacter(comp);
			return;
		}
		var overlay = _hostTags ??= new NametagOverlay(_log, "host");
		var (version, tags) = HostNametags.Roster.Snapshot();
		if (_hostTagTick++ % HostTagResolveTicks == 0 || version != _hostTagVersion)
		{
			ResolveHostCharacter(comp);
			_hostTagVersion = version;
			_hostTagList.Clear();
			try
			{
				foreach (var tag in tags)
				{
					if (tag.ClientId == SyncProtocol.HostClientId) continue;
					Entity? target = null;
					if (_ids.TryGet(tag.NetId, out Entity character) && !EngineCodec.IsDead(character))
					{
						bool skip = false;
						target = comp.ReplayContext.TranslateObject(character, ReplicationFailureStrategy.NullOnAnyFail, ref skip) as Entity;
					}
					_hostTagList.Add((tag.ClientId, tag.Name, target));
				}
			}
			catch (Exception ex)
			{
				_log.WarnOnce("host-nametag-resolve", $"nametags(host): resolving players' characters failed ({ex.GetType().Name}: {ex.Message}); retrying");
				_hostTagList.Clear();
			}
		}
		overlay.Update(comp.Session, comp.Entity, _hostTagList);
	}
}
