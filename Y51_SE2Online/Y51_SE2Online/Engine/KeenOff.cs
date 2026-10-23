using System;
using System.Linq;
using HarmonyLib;
using Keen.Game2;
using Keen.Game2.Game.Networking;
using Keen.Game2.Game.RuntimeSystems.GameSessions;
using Keen.Game2.OutOfProcess;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.Core.Services.Data;
using Keen.VRage.Core.Services.GameSessions;
using Keen.VRage.Core.Services.Networking;
using Keen.VRage.DCS.Builders;
using Keen.VRage.Library.Utils;
using Keen.VRage.Multiplayer.Replications;
using Keen.VRage.Multiplayer.Transport;
using Keen.VRage.Multiplayer.Transport.OutOfProcess;
using Keen.VRage.Multiplayer.Transport.OutOfProcess.Client;
using Keen.VRage.Multiplayer.Transport.OutOfProcess.Server;
using Y51_SE2Online.Core;
using Y51_SE2Online.KeenNetcode;
using KTask = Keen.VRage.Library.Threading.Task;

namespace Y51_SE2Online.Engine;

/// <summary>
/// The only place SE2 Online names the game's own network code, and only to keep it from doing anything. The game's world
/// builders still put its networking components into every world (other components depend on the services they offer):
/// the network transport's two per-frame jobs are where SE2 Online reads and writes its own link instead, and the transport
/// never gets a peer (SE2 Online's transports are the session's only peers); the server networking component's job is
/// where SE2 Online admits players; the client one's snapshot pull is SE2 Online's; the game's session, lobby, dedicated
/// Steam session and reconnect never start. Each patch here replaces the game's method outright (a prefix that returns
/// false), except the two that only add SE2 Online's own clean-up before the game's (world unload, networking destroyed).
/// The release gate (Tests/NetGate, run by Build.ps1 and preflight) fails the build if any other code names one of these types.
/// </summary>
internal static class KeenOff
{
	public static void InstallTransport(Harmony harmony)
	{
		var common = typeof(OutOfProcessPeerTransportSessionComponent);
		var server = typeof(OutOfProcessServerPeerTransportSessionComponent);
		var client = typeof(OutOfProcessClientPeerTransportSessionComponent);
		// First: AnimPlayout's own hooks and the probes run around SE2 Online's inbound, not around the game's.
		harmony.Patch(AccessTools.Method(common, "ProcessFromRemote"), prefix: new HarmonyMethod(typeof(KeenOff), nameof(InboundPrefix)) { priority = Priority.First });
		harmony.Patch(AccessTools.Method(common, "DispatchDataToRemote"), prefix: new HarmonyMethod(typeof(KeenOff), nameof(OutboundPrefix)) { priority = Priority.First });
		harmony.Patch(AccessTools.Method(server, "ConnectionsObserverOnConnected"), prefix: Skip);
		harmony.Patch(AccessTools.Method(server, "ConnectionsObserverOnDisconnected"), prefix: Skip);
		harmony.Patch(AccessTools.Method(server, "OnBeforeSessionUnloaded"), prefix: new HarmonyMethod(typeof(KeenOff), nameof(ServerUnloadPrefix)));
		harmony.Patch(AccessTools.Method(client, "AddPeer"), prefix: new HarmonyMethod(typeof(KeenOff), nameof(ClientAddPrefix)));
		harmony.Patch(AccessTools.Method(client, "RemovePeer"), prefix: new HarmonyMethod(typeof(KeenOff), nameof(ClientRemovePrefix)));
	}

	private static readonly HarmonyMethod Skip = new(typeof(KeenOff), nameof(Never));

	private static bool Never() => false;

	private static bool InboundPrefix(OutOfProcessPeerTransportSessionComponent __instance)
	{
		Y51Replication.Inbound(__instance.GetSession(), __instance.IsServer);
		return false;
	}

	private static bool OutboundPrefix(OutOfProcessPeerTransportSessionComponent __instance)
	{
		Y51Replication.Outbound(__instance.GetSession(), __instance.IsServer);
		return false;
	}

	// The world unloads: every player leaves its replication first, as the game's own transport did with its peers.
	private static void ServerUnloadPrefix(OutOfProcessServerPeerTransportSessionComponent __instance)
	{
		Y51Replication.RemoveAll(__instance.GetSession(), "the world unloaded");
		OwnNet.WorldUnloaded();
	}

	private static bool ClientAddPrefix(OutOfProcessClientPeerTransportSessionComponent __instance)
	{
		// Inside the game's build of the session components: the session does not have them yet, the component does.
		Y51Replication.AddServer(__instance.GetSession(), __instance._replicationClient, __instance._replicationCore, __instance._replicationContext);
		return false;
	}

	private static bool ClientRemovePrefix(OutOfProcessClientPeerTransportSessionComponent __instance)
	{
		Y51Replication.RemoveServer(__instance._replicationClient);
		return false;
	}

	// --- the game's networking components: handshake, snapshot pull, sessions, lobby, reconnect ---

	public static void InstallNetworking(Harmony harmony)
	{
		var server = typeof(GameServerNetworkingComponent);
		var client = typeof(GameClientNetworkingComponent);
		harmony.Patch(AccessTools.Method(server, "Init"), prefix: Skip);
		harmony.Patch(AccessTools.Method(server, "Poll"), prefix: new HarmonyMethod(typeof(KeenOff), nameof(ServerPollPrefix)));
		harmony.Patch(AccessTools.Method(server, "Destroy"), prefix: new HarmonyMethod(typeof(KeenOff), nameof(ServerDestroyPrefix)));
		harmony.Patch(AccessTools.Method(client, "Poll"), prefix: Skip);
		harmony.Patch(AccessTools.Method(client, nameof(GameClientNetworkingComponent.PullDataFromServerAsync)), prefix: new HarmonyMethod(typeof(KeenOff), nameof(PullPrefix)));
		_claim = AccessTools.GetDeclaredMethods(client).FirstOrDefault(m => m.IsStatic && m.Name.Contains("ClaimLocalIdentity"))
			?.CreateDelegate<Action<SessionBuilder, GameServiceUser>>() ?? throw new MissingMethodException(client.FullName, "ClaimLocalIdentity");
		harmony.Patch(AccessTools.Method(typeof(ReconnectComponent), nameof(ReconnectComponent.ClientConnected)), prefix: Skip);
		var join = typeof(GameSessionClientComponent);
		harmony.Patch(AccessTools.Method(join, "Init"), prefix: new HarmonyMethod(typeof(KeenOff), nameof(JoinInitPrefix)));
		harmony.Patch(AccessTools.Method(join, "Destroy"), prefix: new HarmonyMethod(typeof(KeenOff), nameof(JoinDestroyPrefix)));
		harmony.Patch(AccessTools.Method(typeof(GameSessionHostComponent), "Init"), prefix: new HarmonyMethod(typeof(KeenOff), nameof(LobbyInitPrefix)));
		// SE2 Online's own dedicated session signs the Steam game server in (Steam/SteamDedicatedSession).
		harmony.Patch(AccessTools.Method(typeof(GameSessionDedicatedServerComponent), "Init"), prefix: Skip);
	}

	private static Action<SessionBuilder, GameServiceUser>? _claim;

	private static bool ServerPollPrefix(GameServerNetworkingComponent __instance)
	{
		OwnNet.ServerPoll(__instance, __instance._worldSessionComponent?.OwnedSession, __instance._config.TypeOfEntityToSpawnOnStart);
		return false;
	}

	private static void ServerDestroyPrefix(GameServerNetworkingComponent __instance)
	{
		Streaming.Reset();
		if (OwnNet.ServerGone(__instance)) Y51_SE2Online.SyncEngine.HostWorld.Current?.WorldGone();
	}

	private static bool PullPrefix(GameClientNetworkingComponent __instance, SessionBuilder clientSession, ref KTask __result)
	{
		__result = OwnNet.Pull(__instance, clientSession, __instance._gameService?.User ?? GameServiceUser.Invalid, _claim!);
		return false;
	}

	// The game's client world asks its session component for the server's session: SE2 Online's link is already connected to
	// it, so the game's session connects nothing. A join SE2 Online did not start never builds a world.
	private static bool JoinInitPrefix(GameSessionClientComponent __instance, GameSessionClientObjectBuilder ob)
	{
		__instance._gameSessionTask = ob.SessionToJoin is OwnSessionID
			? KTask.FromResult<IGameSession>(new JoinedSession())
			: KTask.FromException<IGameSession>(new InvalidOperationException($"{ob.SessionToJoin} is not a server SE2 Online joined; join through SE2 Online's menu"));
		return false;
	}

	private static bool JoinDestroyPrefix(GameSessionClientComponent __instance) => false;

	// Nobody joins through the game's lobby: it stays private.
	private static void LobbyInitPrefix(GameSessionHostComponentObjectBuilder ob) => ob.SessionType = JoinableSessionType.Private;

	/// <summary>A dedicated server's world: the game's server networking components it is built with (HeadlessEngine).</summary>
	public static void AddServerNetworking(ref EntityBuilder server)
	{
		server.WithComponent<GameSessionDedicatedServerComponent>(new GameSessionDedicatedServerComponentObjectBuilder
		{
			ListenEndpoint = new System.Net.IPEndPoint(System.Net.IPAddress.Any, 27015),
			ServerName = "Dedicated server"
		}, StringId.Get("IGameSessionProvider"), StringId.Get("IServerGameSessionProvider"));
		server.WithComponent<GameServerNetworkingComponent>(new GameServerNetworkingComponentObjectBuilder());
		server.WithComponent<OutOfProcessServerTransportCoreComponent>();
		server.WithComponent<LoggingIncidentReportingCoreComponent>();
	}

	/// <summary>A dedicated server's core scene: the server networking component's job (SE2 Online's admissions run in it).</summary>
	public static void AddServerNetworkingJobs(SceneBuilder builder) => builder.AddJobsFromType(typeof(GameServerNetworkingComponent));

	/// <summary>The session a joined player's game builds its world for: no connection of its own.</summary>
	private sealed class JoinedSession : IGameSession
	{
		public IPeerToPeerSession Communication => NullComms.Instance;

		public GameServiceUser Server => OwnNet.ServerUser;

		public SessionJoinCode? JoinCode => null;
	}

	/// <summary>The game's networking components get this connection: it never connects, sends or receives anything.</summary>
	private sealed class NullComms : IPeerToPeerSession
	{
		public static readonly NullComms Instance = new();

		public event Action<GameServiceUser>? ConnectionRequested { add { } remove { } }

		public event Action<GameServiceUser, ConnectionFailureReason>? PeerConnectionFailed { add { } remove { } }

		public void CloseConnection(GameServiceUser user) { }

		public bool AcceptConnection(GameServiceUser user) => false;

		public bool SendMessage(GameServiceUser user, ReadOnlySpan<byte> data, byte channel = 0, MessageReliability reliability = MessageReliability.Reliable) => false;

		public bool TryReadNextMessage(byte channel, out IPeerToPeerSession.Message message)
		{
			message = default;
			return false;
		}

		void IPeerToPeerSession.DisposeMessage(nint messageHandle) { }

		public void Dispose() { }
	}

	/// <summary>The game's own notice to its session components that a player connected (they bind its identity, start its
	/// per-player data); the interface lives with the game's network transport.</summary>
	public static void ClientConnected(Session session, Keen.VRage.Multiplayer.ClientId client, Keen.VRage.Core.Services.Data.GameServiceUser user)
		=> session.SessionComponents.ForEach((IConnectedClientsListener x) => x.OnClientConnected(client, user));

	public static void ClientDisconnected(Session session, Keen.VRage.Multiplayer.ClientId client)
		=> session.SessionComponents.ForEach((IConnectedClientsListener x) => x.OnClientDisconnected(client));

	/// <summary>The session's replication core, through the transport component that holds it (when the session's service lookup has none).</summary>
	public static ReplicationCoreComponent? CoreOf(Keen.VRage.DCS.Components.Entity sessionComponents)
		=> (sessionComponents.TryGet<OutOfProcessServerPeerTransportSessionComponent>() as OutOfProcessPeerTransportSessionComponent
			?? sessionComponents.TryGet<OutOfProcessClientPeerTransportSessionComponent>())?._replicationCore;
}
