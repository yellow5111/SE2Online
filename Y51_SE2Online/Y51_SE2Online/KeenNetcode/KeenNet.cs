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

	// Steam id -> when SE2 Online admitted it. One admission lets one hello through, within AdmissionMs.
	private static readonly ConcurrentDictionary<ulong, long> Admitted = new();

	private const long AdmissionMs = 120000;

	// Players past Keen's hello on this server; read off the network job, so not the game's own HashSet.
	private static readonly ConcurrentDictionary<GameServiceUser, bool> Validated = new();

	private static readonly ConcurrentDictionary<ulong, string> Refused = new();

	// Players whose hello waits for SE2 Online's join check: when it arrived, and the hello itself (its game build).
	private static readonly Dictionary<GameServiceUser, (long Since, byte[] Hello)> Held = new();

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
		harmony.Patch(AccessTools.Method(typeof(GameServer), nameof(GameServer.Init)), prefix: new HarmonyMethod(typeof(KeenNet), nameof(QueryPortPrefix)));
		var join = typeof(GameSessionClientComponent);
		harmony.Patch(AccessTools.Method(join, "Init"), prefix: new HarmonyMethod(typeof(KeenNet), nameof(JoinInitPrefix)));
		harmony.Patch(AccessTools.Method(join, "Destroy"), prefix: new HarmonyMethod(typeof(KeenNet), nameof(JoinDestroyPrefix)));
		if (AccessTools.GetDeclaredMethods(typeof(GameClientNetworkingComponent)).FirstOrDefault(m => m.Name.Contains("ClaimLocalIdentity")) is { } claim)
			harmony.Patch(claim, prefix: new HarmonyMethod(typeof(KeenNet), nameof(ClaimLocalIdentityPrefix)));
		else _log.Warn("local players: the game's local identity claim was not found; extra instances of an account may act as its first player");
	}

	/// <summary>Address and name of this dedicated server (server.json); Keen's default is any address, port 27015.</summary>
	public static (System.Net.IPEndPoint Endpoint, string Name)? DedicatedListen;

	/// <summary>The session the game's own client last joined (a lobby, a dedicated server's address, ...).</summary>
	public static volatile GameSessionID? LastJoined;

	private static bool JoinInitPrefix(GameSessionClientComponent __instance, GameSessionClientObjectBuilder ob)
	{
		LastJoined = ob.SessionToJoin;
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

	// Keen starts the Steam game server with the query port shared with the game socket (0xFFFF), and on a remote server
	// nothing answered server queries there, so clients took it for a co-op host and never joined. Steam answers on its own
	// port: game port + 1, the port 0.15 servers used (existing firewall rules already open it).
	private static void QueryPortPrefix(ushort __1, ref ushort __2)
	{
		if (DedicatedListen == null || __2 != ushort.MaxValue || __1 == ushort.MaxValue) return;
		__2 = (ushort)(__1 + 1);
		_log.Info($"keen dedicated: server queries on port {__2}");
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

	/// <summary>
	/// Whether Steam still holds a connected session with this player and data waits on it, in Steam's buffer or SE2
	/// Online's large-message queue (only over Steam; false for loopback and direct IP). Connected with nothing waiting is a
	/// game that stopped answering: it gets no grace beyond the ping timeout.
	/// </summary>
	public static bool SteamSessionConnected(bool host, GameServiceUser user)
		=> SteamSession(host, user, out var q) == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected
		   && Sync.LinkGrace.Backlogged(q.m_cbPendingReliable + PendingReliableBytes(host, user), q.m_usecQueueTime.m_SteamNetworkingMicroseconds);

	/// <summary>
	/// The game's own connection to that peer is still up: its Steam session, or for a direct IP join its TCP connection.
	/// A direct IP join had no answer here, so every Pong later than 30 s behind a queued stream dropped the player (F20).
	/// </summary>
	public static bool SessionConnected(bool host, GameServiceUser user)
	{
		if (SteamSessionConnected(host, user)) return true;
		if (host) return (_server?._comms as LocalServerSession)?.DirectConnected(user) == true;
		return ((ClientComms as FramedSession)?.Inner ?? ClientComms) is DirectIpClientSession direct && direct.IsConnected(user);
	}

	private static ESteamNetworkingConnectionState SteamSession(bool host, GameServiceUser user, out SteamNetConnectionRealTimeStatus_t status)
	{
		status = default;
		if (SteamOf((host ? _serverFramed : ClientComms as FramedSession)?.Inner) is not { } steam || !steam.TryGetIdentity(user, out SteamNetworkingIdentity id))
			return ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_None;
		return steam._isServer
			? SteamGameServerNetworkingMessages.GetSessionConnectionInfo(ref id, out _, out status)
			: SteamNetworkingMessages.GetSessionConnectionInfo(ref id, out _, out status);
	}

	private static System.Reflection.FieldInfo? _statImpl;

	// SteamNetworkingEngineComponent.CreateSession hands out the Steam session wrapped in a StatCountingPeerToPeerSession,
	// so the Steam check above never matched: every player got only the 30 s ping timeout, never the grace for a long queue.
	private static SteamPeerToPeerSession? SteamOf(IPeerToPeerSession? session)
	{
		if (session is SteamPeerToPeerSession steam) return steam;
		if (session is not StatCountingPeerToPeerSession counted) return null;
		_statImpl ??= AccessTools.Field(typeof(StatCountingPeerToPeerSession), "_impl");
		return _statImpl?.GetValue(counted) as SteamPeerToPeerSession;
	}

	private static long _linksLoggedMs, _linksCheckedMs;

	private static readonly Dictionary<GameServiceUser, (bool Bad, long LoggedMs)> _badLinks = new();

	// Input lag reports need the link's numbers: Steam's ping and how much waits ahead of the next input or update. Every
	// 2 minutes for everyone, and for a link that turns slow or backed up at once, then at most once a minute while it stays so.
	private static void LogLinks(GameServerNetworkingComponent server)
	{
		long now = Environment.TickCount64;
		if (now - _linksCheckedMs < 1000) return;
		_linksCheckedMs = now;
		bool due = now - _linksLoggedMs >= 120000;
		if (due) _linksLoggedMs = now;
		foreach (GameServiceUser user in server._validatedPeers)
		{
			if (SteamSession(true, user, out var q) != ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected) continue;
			bool bad = Sync.LinkGrace.Bad(q.m_nPing, q.m_cbPendingReliable, q.m_usecQueueTime.m_SteamNetworkingMicroseconds);
			_badLinks.TryGetValue(user, out var was);
			bool turnedBad = bad && (!was.Bad || now - was.LoggedMs >= 60000);
			if (_badLinks.Count > 512) _badLinks.Clear();
			if (!due && !turnedBad) { _badLinks[user] = (bad, was.LoggedMs); continue; }
			_badLinks[user] = (bad, now);
			_log.Info($"steam link {SteamIdOf(user)?.ToString() ?? user.ToString()}: ping {q.m_nPing} ms, quality {q.m_flConnectionQualityLocal:P0} here / {q.m_flConnectionQualityRemote:P0} there, "
				+ $"out {q.m_flOutBytesPerSec / 1024:0} KiB/s of {q.m_nSendRateBytesPerSecond / 1024} KiB/s allowed, in {q.m_flInBytesPerSec / 1024:0} KiB/s, "
				+ $"waiting {q.m_cbPendingReliable / 1024} KiB reliable + {q.m_cbPendingUnreliable / 1024} KiB unreliable ({q.m_usecQueueTime.m_SteamNetworkingMicroseconds / 1000} ms queue)");
		}
	}

	// What waits in the large-message layer once Steam's send buffer is full. It was always 0, so a join stream went into
	// that one in-order queue whole and the player's pings sat behind all of it on a slow link (F20's first loss).
	// ponytail: Steam's own buffer is not counted; add SteamNetworkingMessages.GetSessionConnectionInfo if that matters.
	public static int PendingReliableBytes(bool host, GameServiceUser user) => (host ? _serverFramed : ClientComms as FramedSession)?.PendingBytes(user) ?? 0;

	/// <summary>SE2 Online's join check passed: the player's parked hello goes on to Keen's handshake.</summary>
	public static void Admit(ulong steamId)
	{
		Refused.TryRemove(steamId, out _);
		Admitted[steamId] = Environment.TickCount64;
	}

	private static bool IsAdmitted(ulong steamId) => Admitted.TryGetValue(steamId, out long since) && Environment.TickCount64 - since < AdmissionMs;

	private static bool TakeAdmission(ulong steamId) => Admitted.TryRemove(steamId, out long since) && Environment.TickCount64 - since < AdmissionMs;

	/// <summary>Drops admissions not yet used: a ban, whitelist or password change applies to players still joining.</summary>
	public static void ForgetAdmissions() => Admitted.Clear();

	/// <summary>Whether this player may send the game's messages in pieces: admitted by SE2 Online or past Keen's hello.</summary>
	public static bool MayFragment(GameServiceUser user) => Validated.ContainsKey(user) || SteamIdOf(user) is ulong id && IsAdmitted(id);

	// A connection that failed or left takes its unused admission with it.
	private static void ForgetUser(GameServiceUser user)
	{
		Validated.TryRemove(user, out _);
		if (SteamIdOf(user) is ulong id) Admitted.TryRemove(id, out _);
	}

	private static void ForgetFailedPeer(GameServiceUser user, ConnectionFailureReason reason) => ForgetUser(user);

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
		Validated.Clear();
		lock (Held) Held.Clear();
	}

	private static bool HelloPrefix(GameServerNetworkingComponent __instance, GameServiceUser peer, ReadOnlySpan<byte> content)
	{
		if (_inReplay || __instance._validatedPeers.Contains(peer)) return true;
#if !PULSAR
		if (VanillaClientsForTests) return true;
#endif
		ulong? steamId = SteamIdOf(peer);
		if (steamId is ulong id && TakeAdmission(id)) return true;
		if (!HostingActive || steamId == null)
		{
			_log.Warn($"keen gate: refusing {peer} ({(steamId == null ? "no Steam id" : "this world is not hosted with SE2 Online")})");
			Reject(__instance, peer);
			return false;
		}
		lock (Held)
		{
			if (!Held.ContainsKey(peer)) _log.Info($"keen gate: holding {steamId} until SE2 Online admits it");
			// Keen reads only the first HELLO_PAYLOAD_LENGTH bytes; the rest need not be kept.
			Held[peer] = (Environment.TickCount64, content[..Math.Min(content.Length, GameNetworkingUtils.HELLO_PAYLOAD_LENGTH)].ToArray());
		}
		return false;
	}

	private static void ServerPollPostfix(GameServerNetworkingComponent __instance)
	{
		if (!ReferenceEquals(_server, __instance))
		{
			_server = __instance;
			// The publicizer exposes the event's backing field under the same name.
			typeof(GameServerNetworkingComponent).GetEvent("OnDisconnected")!.AddEventHandler(__instance, new Action<GameServiceUser>(user =>
			{
				ForgetUser(user);
				HostGone.Enqueue((user, "left the game"));
			}));
			typeof(GameServerNetworkingComponent).GetEvent("OnConnected")!.AddEventHandler(__instance, new Action<IPeerToPeerSession, GameServiceUser>((_, user) => Validated[user] = true));
		}
		(__instance._comms as LocalServerSession)?.Pump();
		_serverFramed?.Flush();
		ServerComms = __instance._comms;
		if (ServerComms == null) return;
		LogLinks(__instance);
		while (Kicks.TryDequeue(out var kick))
		{
			_log.Info($"keen gate: closing {kick.User}: {kick.Why}");
			lock (Held) Held.Remove(kick.User);
			Reject(__instance, kick.User);
			HostGone.Enqueue(kick);
		}
		List<(GameServiceUser User, byte[] Hello)>? release = null;
		lock (Held)
		{
			long now = Environment.TickCount64;
			foreach (var (user, (since, hello)) in Held)
			{
				ulong? id = SteamIdOf(user);
				if (id is ulong admitted && IsAdmitted(admitted) || id is ulong refused && Refused.ContainsKey(refused) || now - since > 120000)
				{
					(release ??= new()).Add((user, hello));
				}
				else if (__instance._pendingPeers.ContainsKey(user))
				{
					// Keen drops a peer that has not said hello in 30 s; SE2 Online's check may wait for the world to load.
					__instance._pendingPeers[user] = now + 30000;
				}
			}
			if (release != null) foreach (var (user, _) in release) Held.Remove(user);
		}
		if (release == null) return;
		foreach (var (user, hello) in release)
		{
			ulong? id = SteamIdOf(user);
			if (id is ulong ok && TakeAdmission(ok))
			{
				_log.Info($"keen gate: {ok} admitted, continuing the game's handshake");
				// The player's own hello, so Keen still refuses a client on another game build.
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
		_serverFramed = new FramedSession(comms, _log, MayFragment);
		IPeerToPeerSession steam = _serverFramed;
#if !PULSAR
		if (VanillaClientsForTests) { _serverFramed = null; steam = comms; _log.Warn("test: vanilla clients admitted unframed, no join check"); }
#endif
		var local = new LocalServerSession(steam, port, direct, _log);
		comms.ConnectionRequested -= server.OnConnectionRequested;
		comms.PeerConnectionFailed -= server.OnPeerConnectionFailed;
		local.PeerConnectionFailed += ForgetFailedPeer;
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
			local.PeerConnectionFailed -= ForgetFailedPeer;
			local.Dispose();
		}
		_serverFramed = null;
		Validated.Clear();
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
		if (!__instance._readyForSnapshotRequest.Task.IsCompleted && Environment.TickCount64 - _clientSince < Sync.RejoinPolicy.HandshakeHoldMs
			&& Sync.RejoinPolicy.HoldsHandshake(stage))
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
