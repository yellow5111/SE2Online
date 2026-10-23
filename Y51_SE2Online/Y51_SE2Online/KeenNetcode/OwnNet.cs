using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Keen.Game2.Simulation.GameSystems.Identities;
using Keen.Game2.Simulation.GameSystems.Ownership;
using Keen.Game2.Simulation.GameSystems.Respawn;
using Keen.Game2.Simulation.GameSystems.Control;
using Keen.Game2.Simulation.RuntimeSystems.Saves;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.Core.Services.Data;
using Keen.VRage.Core.Services.GameSessions;
using Keen.VRage.Core.Services.Networking;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Utils;
using Y51_SE2Online.Core;
using Y51_SE2Online.Net;
using Y51_SE2Online.Sync;
using KTask = Keen.VRage.Library.Threading.Task;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>The server an SE2 Online join connected to (its link is SE2 Online's own, not the game's).</summary>
internal sealed record OwnSessionID(ulong Host, string Label) : GameSessionID
{
	public override string ToString() => Label;
}

/// <summary>
/// SE2 Online's own netcode on SE2 4.0: its own link (SteamLink / TcpLink), its own join and admission, the world's
/// session snapshot and the player's identity, with the game's network handshake, Steam lobby join and reconnect never
/// started. The game's networking components stay in the world (other components depend on the services they offer);
/// their network work is replaced here: the server's admits a player when SE2 Online asks it to, the client's pulls
/// the snapshot over SE2 Online's link.
/// </summary>
internal static class OwnNet
{
	private static IMpLog _log = GameLog.Instance;

	/// <summary>The server, as a joined player's game names it.</summary>
	internal static readonly GameServiceUser ServerUser = new(new Guid("2c7b9a51-0e4d-4f6a-a3b8-51e2d0c10001"));

	public static void Install(Harmony harmony)
	{
		_log.Info("netcode: SE2 Online's own (the game's network handshake, transport, lobby join and reconnect never run)");
		// The terminal's Players tab exists only while the game's own multiplayer is on; SE2 Online's sessions have players too.
		try
		{
			harmony.Patch(AccessTools.Method(typeof(Keen.Game2.Client.UI.TerminalScreen.TerminalScreenViewModel), "BuildScreenTree"),
				prefix: Hook(nameof(PlayersTabPrefix)), finalizer: Hook(nameof(PlayersTabFinalizer)));
		}
		catch (Exception ex) { _log.Warn("netcode: the terminal's Players tab stays hidden (" + ex.Message + ")"); }
	}

	/// <summary>Main thread (MpPlugin.Tick): the game's own multiplayer stays off (-enableMultiplayer turns it on while the engine builds).</summary>
	public static void KeepGameMultiplayerOff()
	{
		if (Keen.Game2.Simulation.GameFeatureConfig.MultiplayerEnabled) Keen.Game2.Simulation.GameFeatureConfig.MultiplayerEnabled = false;
	}

	private static void PlayersTabPrefix(out bool __state)
	{
		__state = Keen.Game2.Simulation.GameFeatureConfig.MultiplayerEnabled;
		if (MpPlugin.Online) Keen.Game2.Simulation.GameFeatureConfig.MultiplayerEnabled = true;
	}

	private static Exception? PlayersTabFinalizer(Exception? __exception, bool __state)
	{
		Keen.Game2.Simulation.GameFeatureConfig.MultiplayerEnabled = __state;
		return __exception;
	}

	private static HarmonyMethod Hook(string name) => new(typeof(OwnNet), name);

	// --- users ---

	private static readonly ConcurrentDictionary<GameServiceUser, ulong> PeerOfUser = new();

	private static readonly ConcurrentDictionary<GameServiceUser, bool> Instances = new();

	/// <summary>The game user a player is registered under on this server (see <see cref="OwnUsers"/>).</summary>
	public static GameServiceUser UserFor(PeerId peer, string playerKey, ulong hostSteamId)
	{
		bool steam = OwnUsers.UsesSteamUser(peer.Value, hostSteamId);
		var user = steam ? SteamUsers.UserOf(peer.Value) : new GameServiceUser(GuidHelperTools.GuidFromServiceData("SE2Online-Player", OwnUsers.InstanceKey(playerKey)));
		PeerOfUser[user] = peer.Value;
		if (!steam) Instances[user] = true;
		return user;
	}

	/// <summary>The SE2 Online peer id a registered user plays as (its Steam id, or an instance's alias).</summary>
	public static ulong? PeerOf(GameServiceUser user) => PeerOfUser.TryGetValue(user, out ulong peer) ? peer : null;

	/// <summary>Another game instance of an account (or a local test player): not that Steam user, never their 2.4 identity.</summary>
	public static bool IsInstance(GameServiceUser user) => Instances.ContainsKey(user);

	// --- server ---

	private sealed record Work(bool Join, GameServiceUser User, Action<byte[]>? SendSnapshot, Action<string>? Fail, long AtMs);

	private static readonly ConcurrentQueue<Work> ServerQueue = new();

	// Admitted players whose snapshot waits until the game has bound their identity (the snapshot carries it).
	private static readonly List<Work> Waiting = new();

	private const long IdentityWaitMs = 10000;

	// The world's networking component SE2 Online's admissions run in (it is the game's server networking job's owner).
	private static object? _server;

	/// <summary>A world is loaded and its networking component runs: players can be admitted.</summary>
	public static bool ServerReady => _server != null;

	/// <summary>Network thread: an accepted player's game asked for the world. It is admitted to the game's replication
	/// (identity, SE2 Online's replication transport), then gets the session snapshot.</summary>
	public static void Join(GameServiceUser user, Action<byte[]> sendSnapshot, Action<string> fail) => ServerQueue.Enqueue(new Work(true, user, sendSnapshot, fail, MpClock.NowMs()));

	/// <summary>Any thread: the player's game leaves the game's replication (left, kicked, lost).</summary>
	public static void Leave(GameServiceUser user) => ServerQueue.Enqueue(new Work(false, user, null, null, 0));

	/// <summary>Hosting stopped: nobody joins any more, and every player's game leaves the world's replication (the world
	/// may stay loaded; its replication would keep serving players SE2 Online no longer has).</summary>
	public static void ResetHost()
	{
		while (ServerQueue.TryDequeue(out _)) { }
		lock (Waiting) Waiting.Clear();
		ServerQueue.Enqueue(new Work(false, GameServiceUser.Invalid, null, null, 0));
	}

	// Players in the world's replication, by the game user they play as.
	// Concurrent: the world unload clears it from the unloading thread while the networking job may still poll.
	private static readonly System.Collections.Concurrent.ConcurrentDictionary<GameServiceUser, Keen.VRage.Multiplayer.ClientId> Admitted = new();

	/// <summary>The server networking component's own job (it reads nothing from the network any more): SE2 Online's
	/// admissions and leaves enter the world's replication here, then admitted players get their world snapshot.</summary>
	public static void ServerPoll(object server, Session? session, InitialEntityOptions spawnOnStart)
	{
		_server = server;
		// A world still building its session components: joins and leaves wait for it (they would find no replication).
		if (session != null && session.SessionComponents == null) return;
		while (ServerQueue.TryDequeue(out Work work))
		{
			try { Admit(session, work); }
			catch (Exception ex)
			{
				_log.Error($"own join: {(work.Join ? "admitting" : "removing")} {Describe(work.User)} failed: {ex}");
				lock (Waiting) Waiting.RemoveAll(w => w.User == work.User);
				// Not left half registered: its next join would remove a peer the game never fully added.
				if (work.Join && session != null) Forget(session, work.User, "admission failed");
				work.Fail?.Invoke("The server could not take your game in (see its log). Try joining again.");
			}
		}
		try { if (session != null) SendSnapshots(session, spawnOnStart); }
		catch (Exception ex) { _log.Error("own join: sending world snapshots failed: " + ex); }
	}

	private static string Describe(GameServiceUser user) => PeerOf(user)?.ToString() ?? user.ToString();

	private static void Forget(Session session, GameServiceUser user, string why)
	{
		if (!Admitted.TryRemove(user, out var client)) return;
		try { Engine.Y51Replication.RemovePlayer(session, client, why); }
		catch (Exception ex) { _log.Warn($"own join: removing {Describe(user)} from the world's replication failed: {ex.Message}"); }
	}

	private static void Admit(Session? session, Work work)
	{
		if (session == null)
		{
			// No world (it unloads): nobody is in a replication any more.
			Admitted.Clear();
			if (work.Join) work.Fail?.Invoke("The server has no world loaded right now. Try joining again in a moment.");
			return;
		}
		if (work.User == GameServiceUser.Invalid)
		{
			foreach (GameServiceUser user in new System.Collections.Generic.List<GameServiceUser>(Admitted.Keys)) Forget(session, user, "hosting stopped");
			return;
		}
		lock (Waiting) Waiting.RemoveAll(w => w.User == work.User);
		// A player still in the world (its old connection's leave not seen yet) is removed first: the new connection gets
		// a peer and a world of its own.
		Forget(session, work.User, work.Join ? "connected again" : "left");
		if (!work.Join) return;
		if (PeerOf(work.User) is not ulong peer || NetHost.NetSession.Current?.Host is not { } host) throw new InvalidOperationException("the player has no SE2 Online connection");
		var peerId = new PeerId(peer);
		_log.Info($"own join: {Describe(work.User)} enters the world's replication");
		Admitted[work.User] = Engine.Y51Replication.AddPlayer(session, work.User, peerId, (message, state) => host.SendEngine(peerId, message, state));
		lock (Waiting) Waiting.Add(work);
	}

	private static void SendSnapshots(Session session, InitialEntityOptions spawnOnStart)
	{
		Work[] due;
		lock (Waiting)
		{
			if (Waiting.Count == 0) return;
			due = Waiting.ToArray();
		}
		var identities = session.SessionComponents.TryGet<IdentitiesSessionComponent>();
		long now = MpClock.NowMs();
		foreach (Work work in due)
		{
			IdentityId? identity = identities?.TryGetIdentityOf(work.User);
			if (identity == null && now - work.AtMs < IdentityWaitMs) continue;
			lock (Waiting) Waiting.Remove(work);
			if (!Admitted.ContainsKey(work.User)) continue;
			if (identity == null)
			{
				// Without an identity the player's game would claim none and could not play: no half join.
				_log.Error($"own join: the game bound no identity to {Describe(work.User)} within {IdentityWaitMs / 1000} s; refusing the join");
				Forget(session, work.User, "no identity");
				work.Fail?.Invoke("The server could not create your player. Try joining again.");
				continue;
			}
			try
			{
				// The session's state a player's game builds its world session from (the entities come as object batches).
				// Timed: a join's capture can stall a busy server; the log says which half costs it.
				long started = System.Diagnostics.Stopwatch.GetTimestamp();
				var snapshot = SessionComponentsSnapshot.Capture(session, (Entity _, bool _) => false, null);
				InitialPossessionStory.CopyInitialCameraTransform(snapshot, session, identity, spawnOnStart);
				double captureMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
				byte[] bytes = Engine.OwnSnapshot.Serialize(snapshot);
				double totalMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
				_log.Info($"own join: world snapshot for {Describe(work.User)}: {MpLogExtensions.Bytes(bytes.Length)}, identity {(long)identity.Value} (capture {captureMs:0} ms, serialize {totalMs - captureMs:0} ms)");
				work.SendSnapshot!(bytes);
			}
			catch (Exception ex)
			{
				_log.Error($"own join: capturing the world for {Describe(work.User)} failed: {ex}");
				Forget(session, work.User, "snapshot failed");
				work.Fail?.Invoke("The server could not send its world (see its log). Try joining again.");
			}
		}
	}

	/// <summary>The server's world unloads (scene thread): nobody it admitted is in a replication any more.</summary>
	public static void WorldUnloaded()
	{
		Admitted.Clear();
		Engine.OwnUnits.ForgetUnknown();
		lock (Waiting) Waiting.Clear();
	}

	/// <summary>The world's networking component is destroyed (the world unloads); true when it was the one in use.</summary>
	public static bool ServerGone(object server)
	{
		if (!ReferenceEquals(_server, server)) return false;
		_server = null;
		while (ServerQueue.TryDequeue(out _)) { }
		lock (Waiting) Waiting.Clear();
		Admitted.Clear();
		return true;
	}

	// --- client ---

	private const long SnapshotWaitMs = 180000;

	/// <summary>
	/// The client world's session state, pulled over SE2 Online's link instead of the game's handshake. The game then claims
	/// the identity bound to the user the server registered this player under (another instance of an account is not that
	/// account's user); <paramref name="claim"/> is the game's own claim.
	/// </summary>
	public static async KTask Pull(object lifetime, SessionBuilder clientSession, GameServiceUser gameUser, Action<SessionBuilder, GameServiceUser> claim)
	{
		await KTask.SetLifetime(lifetime);
		var session = NetHost.NetSession.Current?.Client ?? throw new InvalidOperationException("SE2 Online is not connected to the server.");
		session.RequestSnapshot();
		long until = MpClock.NowMs() + SnapshotWaitMs;
		byte[]? bytes;
		while ((bytes = session.TryTakeSnapshot()) == null)
		{
			if (session.CurrentPhase != ClientSession.Phase.Accepted) throw new InvalidOperationException("The connection to the server closed before its world arrived.");
			if (MpClock.NowMs() > until) throw new TimeoutException($"The server did not send its world within {SnapshotWaitMs / 1000} s.");
			await KTask.Delay(TimeSpan.FromMilliseconds(50));
		}
		var snapshot = Engine.OwnSnapshot.Deserialize(bytes);
		snapshot.Apply(ref clientSession.SessionComponents);
		var user = session.SnapshotUser != Guid.Empty ? new GameServiceUser(session.SnapshotUser) : gameUser;
		claim(clientSession, user);
		_log.Info($"own join: world snapshot applied ({MpLogExtensions.Bytes(bytes.Length)}), playing as game user {user}");
	}
}
