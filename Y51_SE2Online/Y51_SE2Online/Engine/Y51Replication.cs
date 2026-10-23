using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Keen.Game2.Simulation.WorldObjects.Characters;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.Pilotable;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.Core.Services.Data;
using Keen.VRage.Core.Services.Networking;
using Keen.VRage.DCS.Components;
using Keen.VRage.DCS.Internal;
using Keen.VRage.Multiplayer;
using Keen.VRage.Multiplayer.Annotations;
using Keen.VRage.Multiplayer.Replications;
using Y51_SE2Online.Core;
using Y51_SE2Online.KeenNetcode;
using Y51_SE2Online.Net;
using Y51_SE2Online.NetHost;
using Y51_SE2Online.Sync;
using Y51_SE2Online.SyncEngine;

namespace Y51_SE2Online.Engine;

/// <summary>
/// SE2 Online's netcode in the game's replication: every remote player is a peer of the server's replication with
/// SE2 Online's transport (Y51ServerTransport), a joined player's game has SE2 Online's transport to the server
/// (Y51ClientTransport). Inbound frames apply at the start of a frame (the game's inbound replication), outbound ones go
/// after the frame's replication; KeenOff gives these two the frame slots the game's own network transport had.
/// </summary>
internal static class Y51Replication
{
	private static readonly ConcurrentDictionary<PeerId, Y51ServerTransport> Servers = new();
	private static readonly ConcurrentDictionary<ClientId, Y51ServerTransport> ServersByClient = new();

	internal static ICollection<Y51ServerTransport> ServerTransports => Servers.Values;
	private static readonly object ClientGate = new();
	private static volatile Y51ClientTransport? _client;

	/// <summary>The joined world's transport to the server, if any.</summary>
	internal static Y51ClientTransport? Client => _client;
	private static ClientSession? _clientSession;
	private static readonly List<byte[]> ClientEarly = new();
	private static long _logMs;

	public static void Install(Harmony harmony)
	{
		KeenOff.InstallTransport(harmony);
		// A tick's signals are recorded and serialized once for all players (Y51PeerTransport.FilterShared).
		harmony.Patch(AccessTools.Method(typeof(ReplicationSessionComponent), "CommitFrame"), prefix: Hook(nameof(CommitPrefix)));
		SharedBlobs.Install(harmony);
		HostSession.EngineUpstream = OnUpstream;
		ClientSession.EngineDownstream = OnDownstream;
		GameLog.Instance.Info("engine: SE2 Online's netcode carries the world (own ids, signals, stories, object batches)");
	}

	private static HarmonyMethod Hook(string name) => new(typeof(Y51Replication), name);

	// Only the server's commit starts a new tick of shared signals (a co-op host's in-process client commits too).
	private static void CommitPrefix(ReplicationSessionComponent __instance)
	{
		if (__instance is ReplicationServerSessionComponent) System.Threading.Interlocked.Increment(ref Y51PeerTransport.CommitGeneration);
	}

	// --- server ---

	/// <summary>
	/// Server, scene thread: a player SE2 Online admitted enters the world's replication as a peer with SE2 Online's
	/// transport (the game sends it the world's replicated entities at once), then the session components hear of it (the
	/// game binds or creates the player's identity). Returns the player's ClientId.
	/// </summary>
	internal static ClientId AddPlayer(Session session, GameServiceUser user, PeerId peer, Func<byte[], bool, bool> send)
	{
		var host = OwnHost.For(session);
		var replication = (ReplicationServerSessionComponent)host.Replication;
		try { SyncProperties.Track(host.Core); }
		catch (Exception ex) { GameLog.Instance.Warn("sync properties: change tracking not installed: " + ex.Message); }
		ClientId clientId = host.NextClientId();
		var transport = new Y51ServerTransport(host, user, clientId, peer, send);
		// The old connection's peer is removed by its own leave; until then it only stops getting frames.
		if (Servers.TryGetValue(peer, out var stale)) GameLog.Instance.Info($"engine: {peer} connected again as ClientId {clientId.Id}; ClientId {stale.PeerClientId.Id} is replaced");
		Servers[peer] = transport;
		ServersByClient[clientId] = transport;
		replication.RegisterPeer(clientId, transport);
		KeenOff.ClientConnected(session, clientId, user);
		GameLog.Instance.Info($"engine: {peer} is ClientId {clientId.Id}; the world goes to it on SE2 Online's netcode");
		return clientId;
	}

	/// <summary>Server, scene thread: the player leaves the world's replication (left, kicked, lost, host stopped).</summary>
	internal static void RemovePlayer(Session session, ClientId clientId, string why)
	{
		if (!ServersByClient.TryRemove(clientId, out var transport)) return;
		Servers.TryRemove(new KeyValuePair<PeerId, Y51ServerTransport>(transport.Peer, transport));
		try { ((ReplicationServerSessionComponent)transport.Owner.Replication).UnregisterPeer(clientId); }
		catch (Exception ex) { GameLog.Instance.Warn($"engine: unregistering ClientId {clientId.Id} failed: {ex.Message}"); }
		transport.Dispose();
		KeenOff.ClientDisconnected(session, clientId);
		GameLog.Instance.Info($"engine: {transport.Peer} ({why}); {transport.Describe()}, batches {transport.BatchesOut} sent {transport.BatchesAcked} acked");
	}

	/// <summary>Every player of a server session (the world unloads: none of them is in it any more).</summary>
	internal static void RemoveAll(Session session, string why)
	{
		foreach (var transport in ServersByClient.Values.ToArray())
			if (ReferenceEquals(transport.Owner.Session, session)) RemovePlayer(session, transport.PeerClientId, why);
	}

	/// <summary>Whether the transport is its peer's current one (a replaced one keeps its bookkeeping but sends nothing).</summary>
	internal static bool IsCurrent(Y51ServerTransport transport) => Servers.TryGetValue(transport.Peer, out var current) && ReferenceEquals(current, transport);

	internal static Y51ServerTransport? ServerOf(ClientId client) => ServersByClient.TryGetValue(client, out var transport) ? transport : null;

	private static bool OnUpstream(PeerId peer, byte[] data) => !Servers.TryGetValue(peer, out var transport) || transport.Offer(data);

	/// <summary>
	/// What a player may write on the server: SE2 4.0 only checks [SyncFromClients] on the sending side. A client keeps an
	/// entry when its type is [SyncFromClients] and it lands on that player's own character or the ship it sits in.
	/// </summary>
	internal static bool ClientMayWrite(ClientId client, int typeId, Entity target, WriteCache cache, long frame)
	{
		Type type = RuntimeDataInfo.Of(typeId).RuntimeType;
		if (!type.IsDefined(typeof(SyncFromClients), inherit: false)) return Refuse(client, type, target, "not a client input type");
		// A character the server moves from its player's own inputs (OwnMotion) takes the game's copy of them from nobody.
		if (KeenNetcode.OwnMotion.ReplacesClientData(type, target)) return false;
		long now = MpClock.NowMs();
		if (cache.Frame != frame)
		{
			cache.Frame = frame;
			cache.Identity = (long)ServerAccess.IdentityOf(client);
			cache.Previous.Clear();
			cache.Previous.UnionWith(cache.Roots);
			OwnBodies(client, cache.Characters, cache.Roots);
			// The grace counts from when the seat was last seen held, so input held back for later gets none.
			if (now - cache.RefreshedMs <= LeftSeatGraceMs)
				foreach (var left in cache.Previous)
					if (!cache.Roots.Contains(left)) (cache.LeftRoot, cache.LeftMs) = (left, cache.RefreshedMs);
			cache.RefreshedMs = now;
			if (cache.LeftRoot != null && now - cache.LeftMs > LeftSeatGraceMs) cache.LeftRoot = null;
			cache.Pilots.Clear();
		}
		long identity = cache.Identity;
		if (identity == 0) return Refuse(client, type, target, "no identity yet");
		if (target.TryGet<CharacterComponent>() != null)
			return TeamPermissions.OwnerOf(target) == identity || Refuse(client, type, target, "another player's character");
		var root = target.GetTopLevelParent();
		// The keys a pilot let go of as they stood up arrive after the seat is empty.
		bool leaving = target.Scene != null && ReferenceEquals(root, cache.LeftRoot) && now - cache.LeftMs <= LeftSeatGraceMs;
		if (!leaving && (target.Scene == null || !cache.Roots.Contains(root))) return Refuse(client, type, target, "a ship the player is not seated in");
		// The host's one-pilot setting: flight input for a ship counts only from the player who flies it.
		if (KeenNetcode.OnePilot.IsFlightInput(type) && KeenNetcode.OnePilot.Enabled && root.TryGet<Keen.Game2.Simulation.WorldObjects.CubeGrids.CubeGridComponent>() != null)
		{
			if (!cache.Pilots.TryGetValue(root, out long pilot)) cache.Pilots[root] = pilot = KeenNetcode.OnePilot.PilotOf(root);
			if (pilot != 0 && pilot != identity)
			{
				KeenNetcode.OnePilot.NoteDropped(identity, pilot);
				return false;
			}
		}
		return true;
	}

	/// <summary>What ClientMayWrite looks up once per frame of a player's input: its identity, bodies, seats and pilots.</summary>
	private const long LeftSeatGraceMs = 3000;

	internal sealed class WriteCache
	{
		public long Frame = -1, Identity, LeftMs, RefreshedMs = long.MinValue / 2;
		public Entity? LeftRoot;
		public readonly HashSet<Entity> Characters = new(ReferenceEqualityComparer.Instance), Roots = new(ReferenceEqualityComparer.Instance), Previous = new(ReferenceEqualityComparer.Instance);
		public readonly Dictionary<Entity, long> Pilots = new(ReferenceEqualityComparer.Instance);
	}

	/// <summary>A player's living characters and the top-level ships they sit in: what that player's game simulates itself.</summary>
	internal static void OwnBodies(ClientId client, HashSet<Entity> characters, HashSet<Entity> roots)
	{
		characters.Clear();
		roots.Clear();
		long identity = (long)ServerAccess.IdentityOf(client);
		if (identity == 0) return;
		foreach (Entity character in CharactersOf(identity))
		{
			characters.Add(character);
			if (character.FirstOrDefault<ISeatable>()?.CurrentSeat?.Entity.GetTopLevelParent() is { } root) roots.Add(root);
		}
	}

	private static IEnumerable<Entity> CharactersOf(long identity)
	{
		if (MpPlugin.App?.ServerSession is not { } session) yield break;
		foreach (Entity character in session.GetEntitiesOfType<CharacterLifeComponent>())
			if (character.TryGet<CharacterLifeComponent>() is { IsAlive: true } && TeamPermissions.OwnerOf(character) == identity) yield return character;
	}

	private static readonly Y51_SE2Online.Sync.UnitFailures Refused = new();

	// A pilot's input after a split goes to the old grid for a few seconds, a look offset names another character: expected,
	// and it was hundreds of lines. The first of each kind is logged, then a count per kind each minute.
	private static bool Refuse(ClientId client, Type type, Entity target, string why)
	{
		try
		{
			var (line, summary) = Refused.Note(target.Scene ?? (object)Refused, "dropped", type.Name + ": " + why, $"ClientId {client.Id} on {target}", MpClock.NowMs());
			if (line != null) GameLog.Instance.Info("engine: " + line + " (further ones counted, a summary each minute)");
			if (summary != null) GameLog.Instance.Info("engine: player input dropped in the last minute: " + summary);
		}
		catch (Exception) { }
		return false;
	}

	/// <summary>Server thread, each dispatch: the minute's summary of dropped input also when nothing new was dropped.</summary>
	internal static void FlushRefused()
	{
		try
		{
			if (Refused.Flush(MpClock.NowMs()) is { } summary) GameLog.Instance.Info("engine: player input dropped in the last minute: " + summary);
		}
		catch (Exception) { }
	}

	// --- client ---

	/// <summary>A joined player's game: its session's transport to the server (KeenOff calls this where the game would
	/// have made its own network transport).</summary>
	internal static void AddServer(Session session, ReplicationClientSessionComponent replication, ReplicationCoreComponent core, IReplicationContext context)
	{
		var net = NetSession.Current?.Client ?? throw new InvalidOperationException("SE2 Online is not connected to the server");
		OwnUnits.ForgetUnknown();
		lock (ClientGate)
		{
			_client?.Dispose();
			var transport = new Y51ClientTransport(OwnHost.For(session, replication, core, context), net.SendEngine);
			if (!ReferenceEquals(net, _clientSession)) ClientEarly.Clear();
			foreach (byte[] early in ClientEarly) transport.Offer(early);
			ClientEarly.Clear();
			_clientSession = net;
			_client = transport;
			// The world's frame jobs start only once it has loaded: the watchdog counts from here.
			_watchMs = MpClock.NowMs();
			_watchLogged = false;
			replication.RegisterServerPeer(transport);
		}
		GameLog.Instance.Info("engine: the server's world arrives on SE2 Online's netcode");
	}

	/// <summary>Main menu: nothing of the last server's session stays referenced here.</summary>
	internal static void ResetClient()
	{
		lock (ClientGate)
		{
			_client?.Dispose();
			_client = null;
			_clientSession = null;
			ClientEarly.Clear();
		}
	}

	internal static void RemoveServer(ReplicationClientSessionComponent replication)
	{
		Y51ClientTransport? transport;
		lock (ClientGate)
		{
			transport = _client;
			if (transport == null) return;
			_client = null;
		}
		try { replication.UnregisterServerPeer(); }
		catch (Exception ex) { GameLog.Instance.Warn("engine: unregistering the server transport failed: " + ex.Message); }
		transport.Dispose();
		GameLog.Instance.Info("engine: client transport closed; " + transport.Describe() + $", objects created {transport.Creations} destroyed {transport.Destructions}, unreadable batches {transport.BatchesUnreadable}");
	}

	private static void OnDownstream(ClientSession session, byte[] data)
	{
		lock (ClientGate)
		{
			if (_client != null && ReferenceEquals(session, _clientSession)) { _client.Offer(data); return; }
			if (!ReferenceEquals(session, _clientSession))
			{
				ClientEarly.Clear();
				_clientSession = session;
				_client?.Dispose();
				_client = null;
			}
			// The server starts sending as soon as the game's handshake is done; the client session is still being built.
			if (ClientEarly.Count < 100_000) ClientEarly.Add(data);
		}
	}

	// --- frame slots (KeenOff) ---

	private static long _inboundMs, _outboundMs, _watchMs;
	private static bool _watchLogged;

	/// <summary>Main thread: the network transport's two frame jobs must run while SE2 Online has transports (a game update that
	/// inlined or renamed them would leave the world without replication, silently).</summary>
	internal static void Watchdog()
	{
		long now = MpClock.NowMs();
		bool active = !ServersByClient.IsEmpty || _client != null;
		if (!active) { _watchMs = now; return; }
		long seen = Math.Min(System.Threading.Volatile.Read(ref _inboundMs), System.Threading.Volatile.Read(ref _outboundMs));
		if (now - Math.Max(seen, _watchMs) < 60_000 || _watchLogged) return;
		_watchLogged = true;
		GameLog.Instance.Error("engine: SE2 Online's replication has not run for 60 s although players are connected (the game's frame jobs it runs in did not run): the world stalled for that long, or the game's frame jobs changed under this build");
	}

	/// <summary>Scene thread, at the start of a frame: what the players (or the server) sent is applied.</summary>
	internal static void Inbound(Session session, bool server)
	{
		System.Threading.Volatile.Write(ref _inboundMs, MpClock.NowMs());
		try
		{
			if (server)
			{
				var host = OwnHost.Existing(session);
				if (host == null) return;
				host.PrepareInbound();
				// What all players' frames of this job may make the game's reader allocate (OwnArchive).
				OwnArchive.BeginBudget(64L << 20);
				try
				{
					foreach (var transport in ServersByClient.Values)
						if (ReferenceEquals(transport.Owner, host)) transport.ProcessInbound();
				}
				finally { OwnArchive.EndBudget(); }
			}
			else if (_client is { } client && ReferenceEquals(client.Owner.Session, session))
			{
				KeenNetcode.AnimPlayout.BeginInbound();
				try { client.ProcessInbound(); }
				finally { KeenNetcode.AnimPlayout.EndInbound(); }
			}
		}
		catch (Exception ex) { GameLog.Instance.WarnOnce("engine-inbound", "engine: applying inbound replication failed: " + ex); }
	}

	/// <summary>Scene thread, after the frame's replication: every player's frame (or the player's) goes out.</summary>
	internal static void Outbound(Session session, bool server)
	{
		System.Threading.Volatile.Write(ref _outboundMs, MpClock.NowMs());
		try
		{
			if (server)
			{
				var host = OwnHost.Existing(session);
				if (host == null) return;
				try
				{
					foreach (var transport in ServersByClient.Values)
						if (ReferenceEquals(transport.Owner, host)) transport.Dispatch();
				}
				finally
				{
					Y51PeerTransport.EndSharedFrame();
					host.RetireRemoved();
				}
			}
			else if (_client is { } client && ReferenceEquals(client.Owner.Session, session)) client.Dispatch();
			long now = MpClock.NowMs();
			if (now - _logMs >= 30_000)
			{
				_logMs = now;
				foreach (var transport in ServersByClient.Values) GameLog.Instance.Info($"engine: {transport.Describe()}, batches {transport.BatchesOut} sent {transport.BatchesAcked} acked (serialized {Y51ServerTransport.BatchesSerialized}, shared {Y51ServerTransport.BatchesShared} server-wide), destroys {transport.DestroysOut}, send failures {transport.SendFailures}, {transport.DescribeLane()}; motion sent per entity over 30 s: {transport.TopMotion()}");
				if (_client is { } c) GameLog.Instance.Info($"engine: {c.Describe()}, objects created {c.Creations} destroyed {c.Destructions}, acks {c.Acks}, unreadable batches {c.BatchesUnreadable}");
				if (!ServersByClient.IsEmpty && OwnDesync.Server.Enabled) GameLog.Instance.Info("engine: " + DesyncStats.DescribeHost());
				if (_client is not null && DesyncStats.ChecksIn > 0) GameLog.Instance.Info("engine: " + DesyncStats.DescribeClient());
			}
		}
		catch (Exception ex) { GameLog.Instance.WarnOnce("engine-dispatch", "engine: sending replication failed: " + ex); }
	}

	public static string Describe() => Servers.IsEmpty && _client == null ? "engine: idle" : string.Join("; ", Servers.Values.Select(t => t.Describe()).Concat(_client is { } c ? new[] { c.Describe() } : Array.Empty<string>()));
}
