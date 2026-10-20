using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Keen.Game2.Game.Networking;
using Keen.Game2.Game.RuntimeSystems.GameSessions;
using Keen.Game2.OutOfProcess;
using Keen.VRage.Core.Services.GameSessions;
using Keen.VRage.Core.Services.Data;
using Keen.VRage.Core.Services.Networking;
using Keen.VRage.Steam.Core;
using Keen.VRage.Steam.Networking;
using Keen.VRage.Steam.Utils;
using Steamworks;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>
/// Hooks into SE2 4.0's own client/server networking: hands its Steam session to <see cref="Net.KeenLink"/>, and on the
/// server parks every player's hello until SE2 Online's join check (password, bans, whitelist, version, full) admits
/// that Steam id. Keen's handshake components run on the game's network job thread; NetSession runs on its own, so
/// decisions cross over through concurrent queues and are applied in the Poll postfixes.
/// </summary>
internal static class KeenNet
{
	private static IMpLog _log = GameLog.Instance;

	private static GameServerNetworkingComponent? _server;

	private static GameClientNetworkingComponent? _client;

	private static SteamIdServiceEngineComponent? _ids;

	private static readonly ConcurrentDictionary<ulong, bool> Admitted = new();

	private static readonly ConcurrentDictionary<ulong, string> Refused = new();

	// Players whose hello waits for SE2 Online's join check, by the time it arrived.
	private static readonly Dictionary<GameServiceUser, long> Held = new();

	private static readonly ConcurrentQueue<(GameServiceUser User, string Why)> Kicks = new();

	private static readonly ConcurrentQueue<(GameServiceUser User, string Why)> HostGone = new();

	private static readonly ConcurrentQueue<(GameServiceUser User, string Why)> ClientGone = new();

	private static bool _inReplay;

	/// <summary>Set while an SE2 Online host session runs; without one the server refuses every player.</summary>
	public static volatile bool HostingActive;

	public static IPeerToPeerSession? ServerComms { get; private set; }

	public static IPeerToPeerSession? ClientComms { get; private set; }

	public static GameServiceUser? ClientServer { get; private set; }

	/// <summary>Refused player's reason, from Keen's own reject (for example a game version mismatch).</summary>
	public static ConnectionRejectReason? ClientRejectReason => _client?.RejectReason;

	public static void Install(Harmony harmony, IMpLog log)
	{
		_log = log;
		var server = typeof(GameServerNetworkingComponent);
		var client = typeof(GameClientNetworkingComponent);
		harmony.Patch(AccessTools.Method(server, "Poll"), prefix: new HarmonyMethod(typeof(KeenNet), nameof(ServerPollPrefix)), postfix: new HarmonyMethod(typeof(KeenNet), nameof(ServerPollPostfix)));
		harmony.Patch(AccessTools.Method(server, "HandleClientHello"), prefix: new HarmonyMethod(typeof(KeenNet), nameof(HelloPrefix)));
		harmony.Patch(AccessTools.Method(server, "Destroy"), prefix: new HarmonyMethod(typeof(KeenNet), nameof(ServerDestroyPrefix)));
		harmony.Patch(AccessTools.Method(client, "Poll"), postfix: new HarmonyMethod(typeof(KeenNet), nameof(ClientPollPostfix)));
		harmony.Patch(AccessTools.Method(client, "Destroy"), prefix: new HarmonyMethod(typeof(KeenNet), nameof(ClientDestroyPrefix)));
		harmony.Patch(AccessTools.Method(typeof(SteamIdServiceEngineComponent), "Initialize"), postfix: new HarmonyMethod(typeof(KeenNet), nameof(IdsPostfix)));
		var lobby = typeof(GameSessionHostComponent);
		harmony.Patch(AccessTools.Method(lobby, "Init"), prefix: new HarmonyMethod(typeof(KeenNet), nameof(LobbyInitPrefix)), postfix: new HarmonyMethod(typeof(KeenNet), nameof(LobbyInitPostfix)));
		harmony.Patch(AccessTools.Method(lobby, "Destroy"), prefix: new HarmonyMethod(typeof(KeenNet), nameof(LobbyDestroyPrefix)));
		harmony.Patch(AccessTools.Method(typeof(GameSessionDedicatedServerComponent), "Init"), prefix: new HarmonyMethod(typeof(KeenNet), nameof(DedicatedInitPrefix)));
		var join = typeof(GameSessionClientComponent);
		harmony.Patch(AccessTools.Method(join, "Init"), prefix: new HarmonyMethod(typeof(KeenNet), nameof(JoinInitPrefix)));
		harmony.Patch(AccessTools.Method(join, "Destroy"), prefix: new HarmonyMethod(typeof(KeenNet), nameof(JoinDestroyPrefix)));
		if (AccessTools.GetDeclaredMethods(typeof(GameClientNetworkingComponent)).FirstOrDefault(m => m.Name.Contains("ClaimLocalIdentity")) is { } claim)
			harmony.Patch(claim, prefix: new HarmonyMethod(typeof(KeenNet), nameof(ClaimLocalIdentityPrefix)));
		else _log.Warn("local players: the game's local identity claim was not found; extra instances of an account may act as its first player");
	}

	/// <summary>Address and name of this dedicated server (server.json); Keen's default is any address, port 27015.</summary>
	public static (System.Net.IPEndPoint Endpoint, string Name)? DedicatedListen;

	private static bool JoinInitPrefix(GameSessionClientComponent __instance, GameSessionClientObjectBuilder ob)
	{
		IGameSession session;
		if (ob.SessionToJoin is LocalSessionID local)
		{
			_log.Info($"local players: joining the game at {local.Endpoint} over loopback TCP");
			session = new LocalClientSession(local.Endpoint);
		}
		else if (ob.SessionToJoin is SteamIpSessionID ip)
		{
			_log.Info($"direct IP: joining the co-op host at {ip.Endpoint}");
			session = new DirectIpClientSession(ip.Endpoint, _log);
		}
		else
		{
			// Keen's own lobby and IP joins: framed so messages over Steam's 512 KiB limit arrive (FragmentLink).
			__instance._gameSessionTask = FramedJoin(__instance, ob.SessionToJoin);
			return false;
		}
		__instance._gameSessionTask = Keen.VRage.Library.Threading.Task.FromResult(session);
		return false;
	}

	// After the world snapshot the game claims the identity whose player is its own game user; an extra instance of an
	// account over loopback is registered on the server under its own user, so it claims that one (it took player A's body).
	private static void ClaimLocalIdentityPrefix(ref GameServiceUser user)
	{
		if (LocalClientSession.Current is { } local && local.AssignedUser != GameServiceUser.Invalid) user = local.AssignedUser;
	}

	private static bool JoinDestroyPrefix(GameSessionClientComponent __instance)
	{
		IGameSession session;
		// A failed join's task rethrows its failure here; there is no session to leave then.
		try { if (!__instance._gameSessionTask.TryGetResult(out session)) return true; }
		catch (Exception) { return false; }
		if (session is FramedGameSession framed) __instance._gameSessionsService.LeaveSession(framed.Inner);
		else if (session is LocalClientSession or DirectIpClientSession) ((IDisposable)session).Dispose();
		else return true;
		return false;
	}

	private static async Keen.VRage.Library.Threading.Task<IGameSession> FramedJoin(GameSessionClientComponent component, GameSessionID id)
	{
		// Keen's async tasks need an owner; the component's own InitAsync gets it from its instance.
		await Keen.VRage.Library.Threading.Task.SetLifetime(component);
		IGameSession inner = await component._gameSessionsService.JoinSession(id, component);
		FramedSession.RaiseSendBuffer(_log);
		return new FramedGameSession(inner, _log);
	}

	private static void DedicatedInitPrefix(GameSessionDedicatedServerComponentObjectBuilder ob)
	{
		if (DedicatedListen is not { } listen) return;
		ob.ListenEndpoint = listen.Endpoint;
		ob.ServerName = listen.Name;
		_log.Info($"keen dedicated: Steam game server on {listen.Endpoint} as '{listen.Name}'");
	}

	private static GameSessionHostComponent? _lobby;

	private static (JoinableSessionType Type, int Max)? _lobbyApplied;

	/// <summary>Who may join this PC's world through Keen's lobby: nobody unless SE2 Online hosts it.</summary>
	public static (JoinableSessionType Type, int Max) LobbyWanted = (JoinableSessionType.Private, 1);

	private static void LobbyInitPrefix(GameSessionHostComponentObjectBuilder ob) => ob.SessionType = LobbyWanted.Type;

	private static void LobbyInitPostfix(GameSessionHostComponent __instance)
	{
		_lobby = __instance;
		_lobbyApplied = null;
	}

	private static void LobbyDestroyPrefix(GameSessionHostComponent __instance)
	{
		if (ReferenceEquals(_lobby, __instance)) _lobby = null;
	}

	/// <summary>Main thread: keeps the Steam lobby's type and size on what hosting asks for.</summary>
	/// <summary>Lobby data naming the hosting player: the server browser lists co-op sessions by host SteamID.</summary>
	public const string HostKey = "y51host";

	private static CallResult<LobbyMatchList_t>? _lobbySearch;

	private static string? _searched;

	/// <summary>The lobby of a co-op session someone hosts, found through Steam's lobby search (works for public lobbies,
	/// which an advertised session has); null while the search runs or when nothing was found.</summary>
	public static CSteamID? FindLobbyOf(ulong hostSteamId) => FindLobby(HostKey, hostSteamId.ToString());

	/// <summary>The co-op session using the game's join code; Steam only searches public and hidden lobbies, not friends-only ones.</summary>
	public static CSteamID? FindLobby(SessionJoinCode code) => FindLobby("code", code.ToString());

	/// <summary>Polled: starts a Steam lobby search for the first lobby with this data, null until it answers.</summary>
	private static CSteamID? FindLobby(string key, string value)
	{
		string wanted = key + "=" + value;
		if (_searched == wanted && _foundLobby is CSteamID found) return found;
		if (_searched == wanted && _lobbySearch != null) return null;
		_searched = wanted;
		_foundLobby = null;
		SteamMatchmaking.AddRequestLobbyListStringFilter(key, value, ELobbyComparison.k_ELobbyComparisonEqual);
		SteamMatchmaking.AddRequestLobbyListDistanceFilter(ELobbyDistanceFilter.k_ELobbyDistanceFilterWorldwide);
		_lobbySearch ??= CallResult<LobbyMatchList_t>.Create((result, failed) =>
		{
			if (!failed && result.m_nLobbiesMatching > 0) _foundLobby = SteamMatchmaking.GetLobbyByIndex(0);
			_lobbySearch = null;
		});
		_lobbySearch.Set(SteamMatchmaking.RequestLobbyList());
		return null;
	}

	private static volatile object? _foundLobbyBox;

	private static CSteamID? _foundLobby
	{
		get => _foundLobbyBox as CSteamID?;
		set => _foundLobbyBox = value;
	}

	public static void ApplyLobby()
	{
		if (_lobby == null || _lobbyApplied == LobbyWanted) return;
		if (!((IHostedGameSessionProvider)_lobby).HostedGameSession.TryGetResult(out IHostedGameSession session)) return;
		var wanted = LobbyWanted;
		session.SetJoinableType(wanted.Type);
		session.SetJoinableMaxPlayers(Math.Max(1, wanted.Max));
		if (LobbyId is CSteamID lobby) SteamMatchmaking.SetLobbyData(lobby, HostKey, SteamUser.GetSteamID().m_SteamID.ToString());
		_lobbyApplied = wanted;
		_log.Info($"keen lobby: {wanted.Type}, up to {wanted.Max} player(s)");
	}

	/// <summary>The Steam lobby of the world this PC hosts, once Keen created it.</summary>
	public static Steamworks.CSteamID? LobbyId => _lobby != null && ((IHostedGameSessionProvider)_lobby).HostedGameSession.TryGetResult(out IHostedGameSession s)
		&& s is Keen.VRage.Steam.GameSessions.SteamGameSessionsEngineComponent.LobbyBasedSession lobby ? lobby.LobbyId : null;

	public static SessionJoinCode? JoinCode => _lobby != null && ((IHostedGameSessionProvider)_lobby).HostedGameSession.TryGetResult(out IHostedGameSession s) ? s.JoinCode : null;

	private static void IdsPostfix(SteamIdServiceEngineComponent __instance) => _ids = __instance;

	/// <summary>Whether this user is connected to the server over Steam (not the local loopback link).</summary>
	public static bool IsSteamPeer(GameServiceUser user) => _server is { } server && (server._validatedPeers.Contains(user) || server._pendingPeers.ContainsKey(user))
		&& !(server._comms is LocalServerSession local && local.HasPeer(user));

	public static ulong? SteamIdOf(GameServiceUser user)
	{
		if (LocalPeers.SteamIdOf(user) is ulong local) return local;
		CSteamID? id = _ids?.TryGetSteamId(user);
		return id.HasValue ? id.Value.m_SteamID : null;
	}

	public static GameServiceUser UserOf(ulong steamId) => LocalPeers.UserOf(steamId) ?? _ids?.GetServiceUser(new CSteamID(steamId)) ?? IdGenerator.GetGameServiceUserFromUserSteamId(new CSteamID(steamId));

	// ponytail: no pending-bytes query yet (Steam returns LimitExceeded once its send buffer is full, MpTransport retries);
	// add SteamNetworkingMessages.GetSessionConnectionInfo if bulk sends starve the replication channel.
	public static int PendingReliableBytes(bool host, GameServiceUser user) => 0;

	/// <summary>SE2 Online's join check passed: the player's parked hello goes on to Keen's handshake.</summary>
	public static void Admit(ulong steamId)
	{
		Refused.TryRemove(steamId, out _);
		Admitted[steamId] = true;
	}

	/// <summary>SE2 Online refused the player: Keen rejects its hello (after our reason has been sent).</summary>
	public static void Refuse(ulong steamId, string why)
	{
		Admitted.TryRemove(steamId, out _);
		Refused[steamId] = why;
	}

	public static void Kick(GameServiceUser user, string why)
	{
		if (SteamIdOf(user) is ulong id) Admitted.TryRemove(id, out _);
		Kicks.Enqueue((user, why));
	}

	public static bool TryTakeDisconnect(bool host, out GameServiceUser user, out string why)
	{
		bool ok = (host ? HostGone : ClientGone).TryDequeue(out var item);
		(user, why) = item;
		return ok;
	}

	public static void ResetHost()
	{
		Admitted.Clear();
		Refused.Clear();
		lock (Held) Held.Clear();
	}

	private static bool HelloPrefix(GameServerNetworkingComponent __instance, GameServiceUser peer)
	{
		if (_inReplay || __instance._validatedPeers.Contains(peer)) return true;
#if !PULSAR
		if (VanillaClientsForTests) return true;
#endif
		ulong? steamId = SteamIdOf(peer);
		if (steamId is ulong id && Admitted.ContainsKey(id)) return true;
		if (!HostingActive || steamId == null)
		{
			_log.Warn($"keen gate: refusing {peer} ({(steamId == null ? "no Steam id" : "this world is not hosted with SE2 Online")})");
			Reject(__instance, peer);
			return false;
		}
		lock (Held)
		{
			if (!Held.ContainsKey(peer)) _log.Info($"keen gate: holding {steamId} until SE2 Online admits it");
			Held[peer] = Environment.TickCount64;
		}
		return false;
	}

	private static void ServerPollPostfix(GameServerNetworkingComponent __instance)
	{
		if (!ReferenceEquals(_server, __instance))
		{
			_server = __instance;
			// The publicizer exposes the event's backing field under the same name.
			typeof(GameServerNetworkingComponent).GetEvent("OnDisconnected")!.AddEventHandler(__instance, new Action<GameServiceUser>(user => HostGone.Enqueue((user, "left the game"))));
		}
		(__instance._comms as LocalServerSession)?.Pump();
		_serverFramed?.Flush();
		ServerComms = __instance._comms;
		if (ServerComms == null) return;
		while (Kicks.TryDequeue(out var kick))
		{
			_log.Info($"keen gate: closing {kick.User}: {kick.Why}");
			lock (Held) Held.Remove(kick.User);
			Reject(__instance, kick.User);
			HostGone.Enqueue(kick);
		}
		List<GameServiceUser>? release = null;
		lock (Held)
		{
			long now = Environment.TickCount64;
			foreach (var (user, since) in Held)
			{
				ulong? id = SteamIdOf(user);
				if (id is ulong admitted && Admitted.ContainsKey(admitted) || id is ulong refused && Refused.ContainsKey(refused) || now - since > 120000)
				{
					(release ??= new()).Add(user);
				}
				else if (__instance._pendingPeers.ContainsKey(user))
				{
					// Keen drops a peer that has not said hello in 30 s; SE2 Online's check may wait for the world to load.
					__instance._pendingPeers[user] = now + 30000;
				}
			}
			if (release != null) foreach (GameServiceUser user in release) Held.Remove(user);
		}
		if (release == null) return;
		foreach (GameServiceUser user in release)
		{
			ulong? id = SteamIdOf(user);
			if (id is ulong ok && Admitted.ContainsKey(ok))
			{
				_log.Info($"keen gate: {ok} admitted, continuing the game's handshake");
				Span<byte> hello = stackalloc byte[9];
				GameNetworkingUtils.WriteHello(hello, 4, __instance._gameVersion);
				_inReplay = true;
				try { __instance.HandleClientHello(user, hello); }
				finally { _inReplay = false; }
			}
			else
			{
				string why = id is ulong r && Refused.TryGetValue(r, out string reason) ? reason : "no answer from SE2 Online in 2 minutes";
				_log.Info($"keen gate: refusing {user}: {why}");
				Reject(__instance, user);
			}
		}
	}

	// Keen's own RejectPeer sends reason VersionMismatch; SE2 Online's reason already reached the player on its channel.
	private static void Reject(GameServerNetworkingComponent server, GameServiceUser peer)
	{
		server._comms?.SendMessage(peer, stackalloc byte[2] { 5, (byte)ConnectionRejectReason.Unknown }, 0);
		server.ForgetPeer(peer);
		server._pendingCloseDeadlines[peer] = Environment.TickCount64 + 1000;
	}

	private static FramedSession? _serverFramed;

#if !PULSAR
	// Test rig: -y51online:vanilla-clients lets the game's own client (no plugin) in, to tell Keen's bugs from ours.
	private static bool VanillaClientsForTests => System.Linq.Enumerable.Contains(MpPlugin.LaunchArgs, MpOptions.Prefix + "vanilla-clients");
#endif

	// Before Keen's first poll reads anything, so every message to and from Steam players is framed.
	private static void ServerPollPrefix(GameServerNetworkingComponent __instance)
	{
		if (__instance._comms is { } comms && comms is not LocalServerSession) WrapLocal(__instance, comms);
	}

	// Other game instances on this Steam account join through loopback TCP (Steam will not connect an account to itself).
	// A co-op host also takes direct IP players; a dedicated server's own Steam socket already listens on its IP.
	private static void WrapLocal(GameServerNetworkingComponent server, IPeerToPeerSession comms)
	{
		int port = DedicatedListen?.Endpoint.Port ?? Y51_SE2Online.SyncEngine.MpMenu.Settings?.ListenPort ?? MpOptions.DefaultIpPort;
		DirectIpPeers? direct = null;
		if (DedicatedListen == null && MpPlugin.Controller?.Current?.SteamListenIp is { } listen)
		{
			try
			{
				direct = new DirectIpPeers(_log, listen);
				_log.Info($"direct IP: players can join this co-op session at {listen} (UDP, Steam-authenticated)");
			}
			catch (Exception ex)
			{
				_log.Warn($"direct IP: cannot listen on {listen} ({ex.GetType().Name}: {ex.Message}); players can still join through Steam");
			}
		}
		FramedSession.RaiseSendBuffer(_log);
		_serverFramed = new FramedSession(comms, _log);
		IPeerToPeerSession steam = _serverFramed;
#if !PULSAR
		if (VanillaClientsForTests) { _serverFramed = null; steam = comms; _log.Warn("test: vanilla clients admitted unframed, no join check"); }
#endif
		var local = new LocalServerSession(steam, port, direct, _log);
		comms.ConnectionRequested -= server.OnConnectionRequested;
		comms.PeerConnectionFailed -= server.OnPeerConnectionFailed;
		local.ConnectionRequested += server.OnConnectionRequested;
		local.PeerConnectionFailed += server.OnPeerConnectionFailed;
		server._comms = local;
	}

	private static void ServerDestroyPrefix(GameServerNetworkingComponent __instance)
	{
		Streaming.Reset();
		if (__instance._comms is LocalServerSession local)
		{
			__instance._comms = local.Inner is FramedSession framed ? framed.Inner : local.Inner;
			local.Dispose();
		}
		_serverFramed = null;
		if (!ReferenceEquals(_server, __instance)) return;
		_server = null;
		ServerComms = null;
		lock (Held) Held.Clear();
	}

	private static long _clientSince;

	// Keen can poll a client networking component once more after destroying it; picking its session up again had
	// SE2 Online reconnect to a dead host every 15 s after a failed join.
	private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<GameClientNetworkingComponent, object> DeadClients = new();

	private static void ClientPollPostfix(GameClientNetworkingComponent __instance)
	{
		if (DeadClients.TryGetValue(__instance, out _)) return;
		if (!ReferenceEquals(_client, __instance)) _clientSince = Environment.TickCount64;
		_client = __instance;
		// Keen gives up on the server's hello after 30 s; SE2 Online's join check may wait for the host's world (2 min cap).
		var stage = Y51_SE2Online.SyncEngine.ClientWorld.Progress.Stage;
		if (!__instance._readyForSnapshotRequest.Task.IsCompleted && Environment.TickCount64 - _clientSince < 120000
			&& stage is not (Sync.JoinStage.Rejected or Sync.JoinStage.Failed or Sync.JoinStage.Idle))
		{
			__instance._handshakeStartTick = Environment.TickCount64;
		}
		(__instance._comms as LocalClientSession)?.Pump();
		(__instance._comms as DirectIpClientSession)?.Pump();
		(__instance._comms as FramedSession)?.Flush();
		ClientComms = __instance._comms;
		ClientServer = __instance._server;
	}

	private static void ClientDestroyPrefix(GameClientNetworkingComponent __instance)
	{
		DeadClients.AddOrUpdate(__instance, true);
		if (!ReferenceEquals(_client, __instance)) return;
		if (ClientServer is { } server) ClientGone.Enqueue((server, "left the game"));
		_client = null;
		ClientComms = null;
		ClientServer = null;
	}
}
