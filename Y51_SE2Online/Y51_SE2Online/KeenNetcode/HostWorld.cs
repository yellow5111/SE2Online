using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Keen.Game2.Game.SessionComponents;
using Keen.Game2.Simulation.GameSystems.Identities;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.Components;
using Keen.VRage.Core.Game.Components;
using Y51_SE2Online.Core;
using Y51_SE2Online.KeenNetcode;
using Y51_SE2Online.Net;
using Y51_SE2Online.Sync;

using Keen.VRage.Core.Game.Data;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// The host side on SE2 4.0: Keen's netcode replicates the world, so "capturing the world" for a player is only
/// admitting it to Keen's handshake. Feature partials (social, admin, GPS, trade, ...) hang off this class like they
/// did off the 2.4 engine; they run on the server session's own update (<see cref="SessionUpdatePostfix"/>).
/// </summary>
internal sealed partial class HostWorld : IHostWorld, IPingWorld
{
	private readonly IMpLog _log;

	private volatile HostSession? _host;

	private volatile WorldSessionComponent? _worldSession;

	private readonly ConcurrentDictionary<ushort, (ulong SteamId, string Name, Keen.VRage.Core.Services.Data.GameServiceUser User)> _unidentified = new();

	// SE2 Online's own netcode: the game user each connected player is registered under.
	private readonly ConcurrentDictionary<PeerId, Keen.VRage.Core.Services.Data.GameServiceUser> _userOf = new();

	private volatile bool _missions;

	private readonly ConcurrentDictionary<ushort, long> _identityOf = new();

	/// <summary>A connected player, as the feature partials know it; Keen spawns and owns its character.</summary>
	private sealed class Joiner
	{
		public PeerId Peer;

		public ushort ClientId;

		public string Name;

		public Entity? Character;

		public uint NetId;

		/// <summary>The listen host's own player: its spawn menu and answers stay in this process.</summary>
		public bool Local;

		public bool Respawn;

		public List<SpawnTarget>? Menu;

		public bool ShipPending;

		public volatile Entity[]? ShipRoots;

		/// <summary>The pending ship comes from a blueprint: its roots are the ship's own grids, not an encounter.</summary>
		public bool ShipBlueprint;

		/// <summary>Counts ship spawns; a spawn that finishes for an older request is deleted, not handed over.</summary>
		public long ShipRequest;

		public long ShipDeadline;

		public List<Entity>? ShipSettle;

		public long ShipSettleUntil;

		public List<(Entity Grid, Keen.VRage.Core.WorldTransform At)>? ShipPins;

		public int ShipPinHeld, ShipPinFixes;

		public bool ShipPinsDynamic;

		/// <summary>A respawn ship's cockpit the new character sits in once its player has possessed it.</summary>
		public Entity? SeatTarget, SeatBody;

		public long SeatDeadline;

		public volatile bool Possessed;
	}

	private readonly ConcurrentDictionary<ushort, Joiner> _joiners = new();

	/// <summary>Server: the characters of the players joined now (test rigs).</summary>
	internal List<Entity> JoinedCharacters => _joiners.Values.Select(j => j.Character).OfType<Entity>().ToList();

	private SessionIds? _ids;

	private Entity? _hostCharacterServer;

	private long _nextCharacterScanMs;

	private bool _seatHoldFailed;



	public static HostWorld? Current { get; private set; }

	public static bool CleanupPending => false;

	public bool SpawnNearHost { get; set; }

	public bool OnePilot { get; set; }

	/// <summary>The server checks every player's world against its own (OwnDesync); -y51online:desync:off turns it off.</summary>
	public bool DesyncChecks { get; set; } = true;

	public TypeKeyTable Keys { get; } = new();

	// ponytail: filled from the loaded world's mod list in phase 3 (mod consent/download); empty means "no mods".
	private volatile System.Collections.Generic.List<WorldMod> _mods = new();

	// The server session the mod list, missions flag and ids were read for: a co-op host may load another world while hosting.
	private volatile Session? _readFor;

	private static Keen.VRage.Core.Worlds.ModList? LoadedMods()
	{
		var projects = MpPlugin.App?.GameData?._projects?.LoadedProjects;
		if (projects == null) return null;
		var records = projects.Where(p => p.Data.Type == Keen.VRage.Core.Project.ProjectType.Mod)
			.Select(p => new Keen.VRage.Core.Worlds.ModList.ModRecord { Project = p.Data.ProjectID, Version = p.Data.Version, DisplayName = p.Data.Name }).ToArray();
		return records.Length == 0 ? null : new Keen.VRage.Core.Worlds.ModList { Records = records };
	}

	public System.Collections.Generic.IReadOnlyList<WorldMod> Mods => _mods;


	private HostWorld(IMpLog log) => _log = log;

	public static HostWorld Install(IMpLog log) => Current ??= new HostWorld(log);

	public static void Reset()
	{
		Current = null;
		OwnNet.ResetHost();
	}

	public void Bind(HostSession host)
	{
		_host = host;
	}

	public void SetWorldSession(WorldSessionComponent world) => _worldSession = world;

	private Session? ServerSession => _worldSession?.OwnedSession;

	// Own netcode: the accept carries the mod list and missions flag, so they must have been read for this world first.
	public bool WorldReady => ServerSession is { } session && OwnNet.ServerReady && ReferenceEquals(_readFor, session);

	public bool HostMissions => _missions;

	public bool WorldLoaded => ServerSession != null;

	public string WorldName
	{
		get
		{
			try { return _worldSession?.DisplayName ?? "?"; }
			catch (Exception) { return "?"; }
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

	internal WorldSocial Social => _worldSession == null ? null : WorldSocial.For(_worldSession);

	internal Session? HostClientSession => MpPlugin.App?.ClientSession;

#if !PULSAR
	private long _bringAtMs, _bringCheckMs;
	private Entity? _brought;
	private int _bringChecks;

	// Test rig (-y51online:selftest-bring): the host brings the first client to itself through the admin menu's own
	// path, then logs where the server has that character so a client that freezes or snaps back shows up.
	private void SelfTestBring(Session session)
	{
		if (!MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "selftest-bring") || _host is not { } host) return;
		long now = MpClock.NowMs();
		if (_brought == null)
		{
			var client = host.Players().FirstOrDefault(p => p.ClientId != SyncProtocol.HostClientId && p.Peer != null && CharacterOf(p.ClientId) != null);
			if (client == null) { _bringAtMs = 0; return; }
			if (_bringAtMs == 0) { _bringAtMs = now + 45000; return; }
			if (now < _bringAtMs) return;
			var before = CharacterOf(client.ClientId).Data.GetWorldTransform().Position;
			_log.Info($"selftest: bring ClientId {client.ClientId} from {before.X:0}, {before.Y:0}, {before.Z:0}: " + Admin(session, null, SyncProtocol.HostClientId, new AdminRequest(AdminAction.Bring, client.Peer!.Value.Value)));
			_brought = CharacterOf(client.ClientId);
			_bringCheckMs = now + 1000;
		}
		else if (_bringChecks < 8 && now >= _bringCheckMs)
		{
			_bringChecks++;
			_bringCheckMs = now + 5000;
			var at = _brought.Data.GetWorldTransform().Position;
			var hostAt = CharacterOf(SyncProtocol.HostClientId)?.Data.GetWorldTransform().Position;
			_log.Info($"selftest: brought character on the server at {at.X:0}, {at.Y:0}, {at.Z:0}" + (hostAt is { } h ? $", {Keen.VRage.Library.Mathematics.Vector3D.Distance(at, h):0.0} m from the host" : ""));
		}
	}
#endif

	private long _lastPcuMs;

	// Each player's PCU use and limit for their players-list row (2.4 read these in its own engine; 4.0 never sent them).
	private void PublishPcu(Session session)
	{
		long now = MpClock.NowMs();
		if (_host is not { } host || now - _lastPcuMs < 1000 || session.SessionComponents.TryGet<Keen.Game2.Simulation.GameSystems.PCU.PCUSessionComponent>() is not { } pcu) return;
		_lastPcuMs = now;
		foreach (var (clientId, identity) in _identityOf)
			host.PublishPlayerStatus(clientId, pcu.GetPCU(new Keen.Game2.Simulation.GameSystems.Ownership.IdentityId(identity)), pcu.MaxPerPlayerPCU);
	}

	internal long IdentityOf(ushort clientId) => _identityOf.TryGetValue(clientId, out long identity) ? identity : 0;

	/// <summary>A player of this identity is in the world with a living character (server thread).</summary>
	internal bool IsPlaying(long identity)
	{
		if (identity == 0) return false;
		foreach (var joiner in _joiners.Values)
			if (joiner.Character != null && IdentityOf(joiner.ClientId) == identity) return true;
		return false;
	}



	public void ConfigureIdentities(HostSettings settings)
	{
		_research = settings.Research;
		_progressionOff = settings.ProgressionOff;
		IdentitiesOn = settings.Identities;
	}

	// The world goes to the player once its game asks for the snapshot (SnapshotRequested), after any mod downloads.
	public void RequestCapture(PeerId client) => _host?.AcceptKeen(client);

	// The game user of a player, as SE2 Online registers it (OwnUsers).
	private Keen.VRage.Core.Services.Data.GameServiceUser UserOf(PeerId peer, string key) => OwnNet.UserFor(peer, key, _host?.HostSteamId ?? 0);

	public void SnapshotRequested(PeerId peer, ushort clientId)
	{
		var host = _host;
		if (host == null || host.Players().FirstOrDefault(p => p.ClientId == clientId && p.Peer == peer) is not { } record) return;
		var user = UserOf(peer, record.Key);
		_userOf[peer] = user;
		_log.Info($"own join: ClientId {clientId} '{record.Name}' ({peer}) asks for the world; game user {user}");
		OwnNet.Join(user, bytes => host.SendSnapshot(peer, bytes, user.Id), why => host.DropPeer(peer, why));
	}

	/// <summary>The hosted world unloaded (a co-op host loads another one): every player's game had that world, so each
	/// connection closes as a loss and the player's game joins again by itself, into whatever world is hosted then.</summary>
	public void WorldGone()
	{
		var host = _host;
		_userOf.Clear();
		// Everyone connected, also players accepted but still downloading mods: their accept named the old world.
		if (host != null)
			foreach (var record in host.Players())
				if (record.Peer is { } peer) host.DisconnectPeer(peer, "the host's world closed");
	}

	public void PeerLeft(PeerId peer)
	{
		if (_userOf.TryRemove(peer, out var user)) OwnNet.Leave(user);
	}

	public void CancelCapture(PeerId client) { }

	public void Rejected(PeerId peer, string why) { }

	public void OnOwnMotion(ushort clientId, byte[] inputs) => KeenNetcode.OwnMotion.Offer(clientId, inputs);

	public void RegisterPlayer(PeerId client, ushort clientId, string identityKey, string name) => _unidentified[clientId] = (client.Value, name, UserOf(client, identityKey));

	public string Stats() => $"SE2 Online netcode, {_joiners.Values.Count(j => !j.Local)} player(s) online";

	public string DesyncDescribe() => DesyncChecks ? DesyncStats.DescribeHost() : "desync checks: off";

	public string SignalStats() => "";

	public long SignalsForwarded => 0;

	public long SignalsDropped => 0;

	public long InputCommandsApplied => 0;

	public long InputRejected => 0;

	private static readonly ConcurrentQueue<Action<Session>> ServerWork = new();

	/// <summary>Runs <paramref name="work"/> on the loaded world's server thread, hosting or not.</summary>
	public static void PostToServer(Action<Session> work) => ServerWork.Enqueue(work);

	/// <summary>Runs on the server session's own thread, once per server update.</summary>
	public static void SessionUpdatePostfix(Session __instance)
	{
		long started = ServerProfiler.Now;
		try { SessionUpdate(__instance); }
		finally { ServerProfiler.Lap(ServerSection.Online, started); }
	}

	private static void SessionUpdate(Session __instance)
	{
		if (!ServerWork.IsEmpty && ReferenceEquals(__instance, MpPlugin.App?.ServerSession))
		{
			while (ServerWork.TryDequeue(out var work)) work(__instance);
		}
		if (ReferenceEquals(__instance, MpPlugin.App?.ServerSession)) KeenNetcode.SuitSync.Flush();
		if (ReferenceEquals(__instance, MpPlugin.App?.ClientSession))
		{
			ClientWorld.Current?.ClientSessionTick(__instance);
			KeenNetcode.MotionTrace.BeforeClientWork(__instance);
			KeenNetcode.MotionSmoothing.ClientTick(__instance);
			KeenNetcode.FloraPhysicsRecovery.ClientTick(__instance);
			try { KeenNetcode.OwnMotion.ClientTick(__instance); }
			catch (Exception ex) { GameLog.Instance.WarnOnce("own-motion-client", "own motion: client tick failed: " + ex); }
			try { KeenNetcode.LoadReadiness.ClientTick(__instance); }
			catch (Exception ex) { GameLog.Instance.WarnOnce("load-readiness-tick", "loading screen: client tick failed: " + ex); }
			KeenNetcode.OwnAnimation.ClientTick();
			KeenNetcode.AnimPlayout.ClientTick(__instance);
			KeenNetcode.MotionTrace.AfterClientWork();
#if !PULSAR
			KeenNetcode.OwnMotionProbe.ClientTick(__instance);
			KeenNetcode.AnimSyncProbe.ClientTick(__instance);
#endif
		}
#if !PULSAR
		KeenNetcode.SelfTest.CountGrids(__instance);
		KeenNetcode.NpcUseProbe.Tick(__instance);
		KeenNetcode.TurretProbe.Tick(__instance);
		KeenNetcode.ContractsProbe.Tick(__instance);
		KeenNetcode.ProjectionProbe.Tick(__instance);
		if (ReferenceEquals(__instance, MpPlugin.App?.ClientSession) && MpPlugin.App?.ServerSession == null) KeenNetcode.AccessProbe.ClientTick(__instance);
		if (ReferenceEquals(__instance, MpPlugin.App?.ServerSession)) Engine.SyncProbe.Tick(__instance, server: true);
		else if (ReferenceEquals(__instance, MpPlugin.App?.ClientSession) && MpPlugin.App?.ServerSession == null) Engine.SyncProbe.Tick(__instance, server: false);
		if (ReferenceEquals(__instance, MpPlugin.App?.ServerSession)) Engine.StateProbe.Tick(__instance, server: true);
		else if (ReferenceEquals(__instance, MpPlugin.App?.ClientSession) && MpPlugin.App?.ServerSession == null) Engine.StateProbe.Tick(__instance, server: false);
#if !PULSAR
		if (ReferenceEquals(__instance, MpPlugin.App?.ClientSession) && MpPlugin.App?.ServerSession == null) KeenNetcode.GroupProbe.ClientTick(__instance);
		KeenNetcode.DockProbe.Tick(__instance);
		KeenNetcode.BoulderProbe.Tick(__instance);
#endif
		if (ReferenceEquals(__instance, MpPlugin.App?.ClientSession) && MpPlugin.App?.ServerSession == null && ClientWorld.Progress.Stage == JoinStage.Playing)
		{
			Engine.ActionProbe.Tick(__instance);
			Engine.SystemsProbe.Tick(__instance);
		}
#endif
		if (ReferenceEquals(__instance, MpPlugin.App?.ServerSession)) Engine.CorpseMotion.Tick(__instance);
		var current = Current;
		if (current == null || !ReferenceEquals(__instance, current.ServerSession)) return;
		try { current.ServerTick(__instance); }
		catch (Exception ex) { current._log.WarnOnce("keen-host-tick", "keen host: server tick failed: " + ex); }
	}

	private void ServerTick(Session session)
	{
		var host = _host;
		if (host == null) return;
		var identities = session.SessionComponents.TryGet<IdentitiesSessionComponent>();
		if (identities != null)
		{
			foreach (var (clientId, who) in _unidentified)
			{
				// Keen creates the player's identity when its client asks for the world snapshot.
				if (identities.TryGetIdentityOf(who.User != default ? who.User : SteamUsers.UserOf(who.SteamId)) is not { } identity) continue;
				_unidentified.TryRemove(clientId, out _);
				_identityOf[clientId] = (long)identity;
				_log.Info($"keen host: ClientId {clientId} '{who.Name}' is IdentityId {(long)identity}");
				host.PlayerIdentified(clientId, (long)identity, (long)identity);
				if ((long)identity == PlayerIdentities.HotfixIdentity && Y51_SE2Online.Dedicated.DedicatedRuntime.Requested && Social is { } social)
				{
					try { lock (social.Sync) social.ReclaimHostSlot(session, who.SteamId); }
					catch (Exception ex) { _log.Warn($"factions: giving the world creator's grids back failed ({ex.GetType().Name}: {ex.Message}); tried again at their next join"); }
				}
			}
		}
		if (!ReferenceEquals(_readFor, session))
		{
			_ids = new SessionIds(session, server: true);
			// A co-op host's in-process server session does not get the game's ModList service; the projects the game
			// loaded for the world are the same list (it builds ModList from them).
			_mods = WorkshopMods.From(session.ExternalServices?.TryGet<Keen.VRage.Core.Worlds.ModList>() ?? LoadedMods());
			if (_mods.Count > 0) _log.Info("keen host: the world runs mods: " + WorldMods.Summary(_mods));
			_missions = session.SessionComponents.TryGet<Keen.Game2.Simulation.GameSystems.Missions.MissionSessionComponent>() != null;
			_readFor = session;
		}
		long now = MpClock.NowMs();
		if (now >= _nextCharacterScanMs)
		{
			_nextCharacterScanMs = now + 500;
			UpdateJoiners(session, host, identities);
			if (identities != null) ShareResearch(session, identities);
		}
		UpdateSpawns(session, host, identities);
		try { KeenNetcode.OwnMotion.ServerTick(session, _joiners.Values.Where(j => !j.Local).Select(j => (j.ClientId, j.Character)), host, _ids!); }
		catch (Exception ex) { _log.WarnOnce("own-motion-server", "own motion: server tick failed: " + ex); }
		_hostIdentity = Y51_SE2Online.Dedicated.DedicatedRuntime.Requested ? 0 : identities != null ? (long)identities.LocalIdentity : 0;
		if (host.HostIdentity != _hostIdentity) host.HostIdentity = _hostIdentity;
		UpdateSocial(session);
#if !PULSAR
		if (Social is { } selfTestSocial) KeenNetcode.SelfTest.Server(session, selfTestSocial, _log);
		SelfTestBring(session);
		KeenNetcode.MotionProbe.ServerTick(session, _hostIdentity);
		KeenNetcode.CruiseProbe.ServerTick(session, KeenNetcode.CruiseProbe.HostToo ? new[] { ((ushort)0, _hostCharacterServer) } : _joiners.Values.Select(j => (j.ClientId, j.Character)));
		KeenNetcode.BuildProbe.Server(session, _log);
		KeenNetcode.OwnMotionProbe.ServerTick(session, _joiners.Values.Where(j => !j.Local).Select(j => (j.ClientId, j.Character)));
		KeenNetcode.AnimSyncProbe.ServerTick(session);
#endif
		UpdateAdmin(session);
		PublishPcu(session);
		if (NetHost.NetSession.Current?.Options is { } streamingOptions) KeenNetcode.Streaming.Tick(session, streamingOptions.StreamingRadiusMeters, _identityOf.Count, _log);
		UpdateGps(session);
		UpdatePings();
		UpdateHacks(session);
		UpdateTrades(session);
		UpdateWater(session);
		UpdatePunches();
		UpdateVoicePositions();
		UpdateNametagRoster(session);
		// Seated characters stay free physics characters the seat only pins: in gravity their speed climbs unless held.
		if (!_seatHoldFailed)
		{
			try { PassengerSeats.HoldSeated(session, server: true); }
			catch (Exception ex) { _seatHoldFailed = true; _log.Warn("passenger seats: holding seated characters still failed (server): " + ex); }
		}
	}

	// Players' characters, from the identity that owns each living character (Keen spawns and replicates them).
	private void UpdateJoiners(Session session, HostSession host, IdentitiesSessionComponent? identities)
	{
		var characters = new System.Collections.Generic.Dictionary<long, Entity>();
		foreach (Entity character in session.GetEntitiesOfType<Keen.Game2.Simulation.WorldObjects.Characters.CharacterLifeComponent>())
		{
			if (character.Get<Keen.Game2.Simulation.WorldObjects.Characters.CharacterLifeComponent>().IsAlive) characters.TryAdd(TeamPermissions.OwnerOf(character), character);
		}
		var online = new System.Collections.Generic.HashSet<ushort>();
		foreach (var record in host.Players())
		{
			if (record.Peer is not PeerId peer) continue;
			online.Add(record.ClientId);
			var joiner = _joiners.GetOrAdd(record.ClientId, id => new Joiner { ClientId = id });
			joiner.Peer = peer;
			joiner.Name = record.Name;
			joiner.Character = characters.TryGetValue(IdentityOf(record.ClientId), out Entity c) ? c : null;
			joiner.NetId = joiner.Character != null ? _ids!.GetOrAssign(joiner.Character) : 0;
			// Server status and host stats read the record: Playing once the player has a living character.
			record.CharacterNetId = joiner.NetId;
		}
		foreach (ushort gone in _joiners.Keys.Where(id => !online.Contains(id))) _joiners.TryRemove(gone, out _);
		_hostCharacterServer = identities != null && characters.TryGetValue((long)identities.LocalIdentity, out Entity own) ? own : null;
	}


	private static void StopInput(Entity entity)
	{
		if (entity.Data.TryGet<Keen.Game2.Simulation.WorldObjects.Movement.ControlData>(out _)) entity.Data.Set(default(Keen.Game2.Simulation.WorldObjects.Movement.ControlData));
		if (entity.Data.TryGet<Keen.Game2.Simulation.WorldObjects.Movement.AngularControlData>(out _)) entity.Data.Set(default(Keen.Game2.Simulation.WorldObjects.Movement.AngularControlData));
		if (entity.Data.TryGet<Keen.Game2.Simulation.WorldObjects.Characters.AdvancedCharacterControlData>(out _)) entity.Data.Set(default(Keen.Game2.Simulation.WorldObjects.Characters.AdvancedCharacterControlData));
	}

    private static readonly Type? CargoSpeed = typeof(Keen.Game2.Simulation.GameSystems.Encounters.CargoShips.CargoShipEncounterComponent).GetNestedType("TargetSpeedData", System.Reflection.BindingFlags.NonPublic);

    private static readonly Lazy<System.Reflection.MethodInfo?> RemoveCargoSpeed = new(() =>
    {
        try { return CargoSpeed == null ? null : typeof(Keen.VRage.DCS.Accessors.DEntityContext).GetMethods().FirstOrDefault(m => m.Name == "TryRemove" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0)?.MakeGenericMethod(CargoSpeed); }
        catch (Exception) { return null; }
    });

    /// <summary>
    /// Undoes the cargo route (dampeners off, thrusters held at a target speed) so the ship holds still for its new owner.
    /// The route starts asynchronously after the spawn, so this runs for a few seconds after the takeover.
    /// Dampeners are switched on at the takeover (<paramref name="force"/>) and after that only when the route ran again:
    /// it turns them off in the same step as it sets the target speed. Forcing them every tick undid the pilot's Z.
    /// </summary>
    internal static void Settle(IEnumerable<Entity> grids, bool force = false)
    {
        foreach (var grid in grids)
        {
            if (EngineCodec.IsDead(grid) || grid.TryGet<CubeGridComponent>() is not { } cube) continue;
            bool routed = false;
            cube.VisitAllBlocksWithComponent((Keen.Game2.Simulation.WorldObjects.CubeBlocks.Movement.ThrusterComponent t) =>
            {
                object data = t.Data;
                try { routed |= RemoveCargoSpeed.Value?.Invoke(data, null) is true; } catch (Exception) { }
            });
            if ((force || routed) && Keen.Game2.Simulation.WorldObjects.Movement.EntityMovementExtensions.GetDampenerState(grid.Data) == Keen.Game2.Simulation.WorldObjects.Movement.DampenerState.Off)
                Keen.Game2.Simulation.WorldObjects.Movement.EntityMovementExtensions.ToggleDampeners(grid.Data);
        }
    }
}
