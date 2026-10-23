using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Keen.Game2.Simulation.WorldObjects.Characters;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.Pilotable;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.Core.Services.Data;
using Keen.VRage.Core.Services.Networking;
using Keen.VRage.DCS.Components;
using Keen.VRage.DCS.Internal;
using Keen.VRage.Multiplayer;
using Keen.VRage.Multiplayer.Annotations;
using Keen.VRage.Multiplayer.Transport.OutOfProcess;
using Keen.VRage.Multiplayer.Transport.OutOfProcess.Client;
using Keen.VRage.Multiplayer.Transport.OutOfProcess.Server;
using Y51_SE2Online.Core;
using Y51_SE2Online.KeenNetcode;
using Y51_SE2Online.Net;
using Y51_SE2Online.NetHost;
using Y51_SE2Online.Sync;
using Y51_SE2Online.SyncEngine;

namespace Y51_SE2Online.Engine;

/// <summary>
/// Puts the engine transport (Y51ServerTransport / Y51ClientTransport) where Keen's network transport would go. The game's
/// out-of-process transport session components stay (they own the network id mapper, the serialization contexts and the
/// inbound/outbound jobs); for each remote player they now make our transport instead of theirs, and those jobs drive ours.
/// The host decides per session (HostSettings.OwnReplication, -y51online:keen-replication turns it off); a player follows
/// what its JoinAccept says. The listen host's own in-process client never used Keen's network transport and is untouched.
/// </summary>
internal static class Y51Replication
{
	private static readonly ConcurrentDictionary<PeerId, Y51ServerTransport> Servers = new();
	private static readonly ConcurrentDictionary<ClientId, Y51ServerTransport> ServersByClient = new();

	internal static ICollection<Y51ServerTransport> ServerTransports => Servers.Values;
	private static readonly object ClientGate = new();
	private static Y51ClientTransport? _client;
	private static ClientSession? _clientSession;
	private static readonly List<byte[]> ClientEarly = new();
	private static long _logMs;

	public static void Install(Harmony harmony)
	{
		var server = typeof(OutOfProcessServerPeerTransportSessionComponent);
		var client = typeof(OutOfProcessClientPeerTransportSessionComponent);
		var common = typeof(OutOfProcessPeerTransportSessionComponent);
		harmony.Patch(AccessTools.Method(server, "AddPeer"), prefix: Hook(nameof(ServerAddPeerPrefix)));
		harmony.Patch(AccessTools.Method(server, "RemovePeer"), prefix: Hook(nameof(ServerRemovePeerPrefix)));
		harmony.Patch(AccessTools.Method(client, "AddPeer"), prefix: Hook(nameof(ClientAddPeerPrefix)));
		harmony.Patch(AccessTools.Method(client, "RemovePeer"), prefix: Hook(nameof(ClientRemovePeerPrefix)));
		// First: AnimPlayout releases held animation changes in its own postfix, after this pass stamped them.
		harmony.Patch(AccessTools.Method(common, "ProcessFromRemote"), postfix: new HarmonyMethod(typeof(Y51Replication), nameof(ProcessFromRemotePostfix)) { priority = Priority.First });
		harmony.Patch(AccessTools.Method(common, "DispatchDataToRemote"), prefix: Hook(nameof(DispatchPrefix)));
		HostSession.EngineUpstream = OnUpstream;
		ClientSession.EngineDownstream = OnDownstream;
		GameLog.Instance.Info("engine: SE2 Online's replication transport is installed (used when the host turns it on)");
	}

	private static HarmonyMethod Hook(string name) => new(typeof(Y51Replication), name);

	private static PeerId PeerOf(GameServiceUser user) => new(KeenNet.SteamIdOf(user) ?? (ulong)user.Id.GetHashCode());

	// --- server ---

	private static bool ServerAddPeerPrefix(OutOfProcessServerPeerTransportSessionComponent __instance, GameServiceUser remoteUser, ClientId clientId)
	{
		var net = NetSession.Current;
		var host = net?.Host;
		if (host == null || !net!.Options.OwnReplication || MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "keen-replication")) return true;
		var peer = PeerOf(remoteUser);
		if (host.StatusOf(peer) != HostSession.ClientStatus.Synced)
		{
			// Its SE2 Online session went away after it was admitted: it would get the game's own netcode on a host that
			// chose SE2 Online's. It is closed; the game removes the peer it adds here.
			GameLog.Instance.Warn($"engine: {remoteUser} ({peer}) reached the game's handshake without an SE2 Online session; closing it");
			if (!MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "vanilla-clients")) KeenNet.Kick(remoteUser, "no SE2 Online session");
			return true;
		}
		try { SyncProperties.Track(__instance._replicationCore); }
		catch (Exception ex) { GameLog.Instance.Warn("sync properties: change tracking not installed: " + ex.Message); }
		var transport = new Y51ServerTransport(__instance, remoteUser, clientId, peer, (message, state) => host.SendEngine(peer, message, state));
		// The game removes the old connection's peer itself (RemovePeer); until then it only stops getting frames.
		if (Servers.TryGetValue(peer, out var stale)) GameLog.Instance.Info($"engine: {peer} connected again as ClientId {clientId.Id}; ClientId {stale.PeerClientId.Id} is replaced");
		Servers[peer] = transport;
		ServersByClient[clientId] = transport;
		__instance._replication.RegisterPeer(clientId, transport);
		__instance.GetSession().SessionComponents.ForEach((IConnectedClientsListener x) => x.OnClientConnected(clientId, remoteUser));
		GameLog.Instance.Info($"engine: {peer} is ClientId {clientId.Id}; the world goes to it on SE2 Online's transport");
		return false;
	}

	private static bool ServerRemovePeerPrefix(OutOfProcessServerPeerTransportSessionComponent __instance, ClientId clientId)
	{
		if (!ServersByClient.TryRemove(clientId, out var transport)) return true;
		Servers.TryRemove(new KeyValuePair<PeerId, Y51ServerTransport>(transport.Peer, transport));
		Drop(__instance, transport, "left");
		return false;
	}

	private static void Drop(OutOfProcessServerPeerTransportSessionComponent component, Y51ServerTransport transport, string why)
	{
		try
		{
			component._replication.UnregisterPeer(transport.PeerClientId);
		}
		catch (Exception ex) { GameLog.Instance.Warn($"engine: unregistering ClientId {transport.PeerClientId.Id} failed: {ex.Message}"); }
		transport.Dispose();
		component.GetSession().SessionComponents.ForEach((IConnectedClientsListener x) => x.OnClientDisconnected(transport.PeerClientId));
		GameLog.Instance.Info($"engine: {transport.Peer} ({why}); {transport.Describe()}, batches {transport.BatchesOut} sent {transport.BatchesAcked} acked");
	}

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
		if (cache.Frame != frame)
		{
			cache.Frame = frame;
			cache.Identity = (long)ServerAccess.IdentityOf(client);
			OwnBodies(client, cache.Characters, cache.Roots);
			cache.Pilots.Clear();
		}
		long identity = cache.Identity;
		if (identity == 0) return Refuse(client, type, target, "no identity yet");
		if (target.TryGet<CharacterComponent>() != null)
			return TeamPermissions.OwnerOf(target) == identity || Refuse(client, type, target, "another player's character");
		var root = target.GetTopLevelParent();
		if (target.Scene == null || !cache.Roots.Contains(root)) return Refuse(client, type, target, "a ship the player is not seated in");
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
	internal sealed class WriteCache
	{
		public long Frame = -1, Identity;
		public readonly HashSet<Entity> Characters = new(ReferenceEqualityComparer.Instance), Roots = new(ReferenceEqualityComparer.Instance);
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

	private static bool Refuse(ClientId client, Type type, Entity target, string why)
	{
		if (ServerAccess.LogDue(client, "engine-data"))
			GameLog.Instance.Info($"engine: dropped {type.Name} from ClientId {client.Id} on {target}: {why} (further ones within 5 s not logged)");
		return false;
	}

	// --- client ---

	private static bool ClientAddPeerPrefix(OutOfProcessClientPeerTransportSessionComponent __instance)
	{
		var session = NetSession.Current?.Client;
		if (session?.Accept is not { Settings.OwnReplication: true }) return true;
		lock (ClientGate)
		{
			_client?.Dispose();
			var transport = new Y51ClientTransport(__instance, session.SendEngine);
			if (!ReferenceEquals(session, _clientSession)) ClientEarly.Clear();
			foreach (byte[] early in ClientEarly) transport.Offer(early);
			ClientEarly.Clear();
			_clientSession = session;
			_client = transport;
			__instance._replicationClient.RegisterServerPeer(transport);
		}
		GameLog.Instance.Info("engine: the host's world arrives on SE2 Online's transport");
		return false;
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

	private static bool ClientRemovePeerPrefix(OutOfProcessClientPeerTransportSessionComponent __instance)
	{
		Y51ClientTransport? transport;
		lock (ClientGate)
		{
			transport = _client;
			if (transport == null) return true;
			_client = null;
		}
		try { __instance._replicationClient.UnregisterServerPeer(); }
		catch (Exception ex) { GameLog.Instance.Warn("engine: unregistering the server transport failed: " + ex.Message); }
		transport.Dispose();
		GameLog.Instance.Info("engine: client transport closed; " + transport.Describe() + $", objects created {transport.Creations} destroyed {transport.Destructions}");
		return false;
	}

	private static void OnDownstream(ClientSession session, byte[] data)
	{
		// The join bar follows the world's bytes, which now come on SE2 Online's channels instead of Keen's.
		JoinBytes.Add(data.Length);
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

	// --- jobs ---

	private static void ProcessFromRemotePostfix(OutOfProcessPeerTransportSessionComponent __instance)
	{
		try
		{
			if (__instance is OutOfProcessServerPeerTransportSessionComponent)
			{
				foreach (var transport in Servers.Values)
					if (ReferenceEquals(transport.Owner, __instance)) transport.ProcessInbound();
			}
			else if (_client is { } client && ReferenceEquals(client.Owner, __instance)) client.ProcessInbound();
		}
		catch (Exception ex) { GameLog.Instance.WarnOnce("engine-inbound", "engine: applying inbound replication failed: " + ex); }
	}

	private static void DispatchPrefix(OutOfProcessPeerTransportSessionComponent __instance)
	{
		try
		{
			if (__instance is OutOfProcessServerPeerTransportSessionComponent)
			{
				foreach (var transport in Servers.Values)
					if (ReferenceEquals(transport.Owner, __instance)) transport.Dispatch();
			}
			else if (_client is { } client && ReferenceEquals(client.Owner, __instance)) client.Dispatch();
			long now = MpClock.NowMs();
			if (now - _logMs >= 30_000)
			{
				_logMs = now;
				foreach (var transport in Servers.Values) GameLog.Instance.Info($"engine: {transport.Describe()}, batches {transport.BatchesOut} sent {transport.BatchesAcked} acked, destroys {transport.DestroysOut}, send failures {transport.SendFailures}, {transport.DescribeLane()}; motion sent per entity over 30 s: {transport.TopMotion()}");
				if (_client is { } c) GameLog.Instance.Info($"engine: {c.Describe()}, objects created {c.Creations} destroyed {c.Destructions}, acks {c.Acks}");
			}
		}
		catch (Exception ex) { GameLog.Instance.WarnOnce("engine-dispatch", "engine: sending replication failed: " + ex); }
	}

	public static string Describe() => Servers.IsEmpty && _client == null ? "engine: idle" : string.Join("; ", Servers.Values.Select(t => t.Describe()).Concat(_client is { } c ? new[] { c.Describe() } : Array.Empty<string>()));
}
