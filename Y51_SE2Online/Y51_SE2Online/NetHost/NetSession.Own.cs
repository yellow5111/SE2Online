using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading;
using Steamworks;
using Y51_SE2Online.Core;
using Y51_SE2Online.KeenNetcode;
using Y51_SE2Online.Net;
using Y51_SE2Online.Steam;
using Y51_SE2Online.Sync;
using Y51_SE2Online.SyncEngine;

namespace Y51_SE2Online.NetHost;

// SE2 Online's own link (SteamLink: Steam P2P and IP; TcpLink: same-PC tests), as SE2 Online ran on SE2 2.4.
internal sealed partial class NetSession
{
	private long _lastDetailedMs;

	private bool _idleLogged;

	private bool _ownJoinRefused;

	private long _nextPlayerCountMs;

	private void RunOwn()
	{
		// Before Steam: a dedicated server whose network fails still runs its world with the configured rules.
		if (_o.Host) { ResourceMultipliers.Current = _o.UsageMultipliers; InventoryMultipliers.Current = _o.InventorySizes; }
		SteamLink? steamLink = null;
		using var dedicatedSteam = _o.Dedicated ? new SteamDedicatedSession() : null;
		TcpLink? tcpLink = null;
		ulong localId;
		string name;
		IPacketLink link;
		if (_o.NeedsSteam)
		{
			if (!_o.Dedicated && !WaitForSteam())
			{
				_log.Info("net: stopped before Steam was ready (" + _stopReason + ")");
				return;
			}
			if (_o.Dedicated)
			{
				dedicatedSteam!.Initialize(_o, _gameVersion, () => _stop);
				localId = dedicatedSteam.SteamId;
				name = _o.SessionName;
			}
			else
			{
				try
				{
					localId = SteamUser.GetSteamID().m_SteamID;
					name = SteamFriends.GetPersonaName();
				}
				catch (InvalidOperationException ex)
				{
					_log.Error("net: Steam API not available (" + ex.Message + "); networking disabled");
					return;
				}
			}
			_log.Info($"net: Steam '{name}' SteamID64={localId}; SE2 Online's own link");
			if (!_o.Dedicated) SteamNetworkingUtils.InitRelayNetworkAccess();
			_localIdentity = localId;
			steamLink = new SteamLink(_log, _o.VirtualPort, _o.SendRateMinBytes, _o.SendRateMaxBytes,
				// Another game instance on the host's own Steam account (Steam authenticates the identity) is never a stranger.
				id => (id != 0 && id == _localIdentity) || ((_o.Allow.Count == 0 || _o.Allow.Contains(id)) && (!_o.FriendsOnly || SteamFriends.GetFriendRelationship(new CSteamID(id)) == EFriendRelationship.k_EFriendRelationshipFriend)),
				// A co-op host without Direct IP still listens on this PC's loopback: another game on the same Steam account joins
				// through it (Steam will not connect an account to itself).
				_o.SteamListenIp ?? (_o.Host && !_o.Dedicated ? new IPEndPoint(IPAddress.Loopback, MpMenu.Settings?.ListenPort ?? MpOptions.DefaultIpPort) : null), _o.Dedicated,
				// A host sends the world: each player's send rate floor starts at 4 MiB/s and backs off (SendRatePolicy).
				adaptiveRate: _o.Host);
			steamLink.Start(_o.Host);
			link = steamLink;
			SteamJoinStart(localId, name);
			if (_o.Host && _o.Advertise) _directory = new DirectoryPublisher(new DirectoryClient(_o.Dedicated ? _o.ServersDirectory : _o.SessionsDirectory), message => _log.WarnOnce("directory-publish", message));
		}
		else
		{
			(localId, name) = Identity();
			if (_o.Join) name += " (tcp)";
			tcpLink = new TcpLink("tcp", _log);
			if (_o.HostIp != null)
			{
				tcpLink.Listen(_o.HostIp);
				_log.Info("HOST READY (local sandbox): join through Multiplayer using Join local test.");
			}
			link = tcpLink;
		}
		if (_o.PlayerName != null) name = _o.PlayerName;
		_localIdentity = localId;
		MpTransport? transport = null;
		try
		{
			transport = new MpTransport(link, new TransportOptions
			{
				LocalId = localId,
				Role = !_o.Host ? (byte)1 : (byte)0,
				LocalName = name,
				PluginVersion = _pluginVersion,
				GameVersion = _gameVersion
			}, _log);
			var (host, client) = CreateSessions(transport, name, localId);
			_client = client;
			_host = host;
			_transport = transport;
			LoopOwn(steamLink, tcpLink, transport, host);
		}
		finally
		{
			try
			{
				_directory?.StopAsync().GetAwaiter().GetResult();
				_advertiser?.Clear(_stopReason);
				_friends?.Dispose();
			}
			catch (Exception) { }
			try
			{
				bool hadPeers = transport != null && transport.Peers.Count > 0;
				transport?.DisconnectAll(1004, _stopReason);
				transport?.Poll();
				// Give the disconnect frames a moment to leave before the link is torn down.
				if (hadPeers) Thread.Sleep(500);
			}
			catch (Exception ex)
			{
				_log.Warn("net: error while disconnecting: " + ex.Message);
			}
			link.Dispose();
			if (_o.Host) { ResourceMultipliers.Current = ResourceMultipliers.Default; InventoryMultipliers.Current = InventoryMultipliers.Default; }
			_log.Info("net: stopped (" + _stopReason + ")");
		}
	}

	private (ulong Id, string Name) Identity()
	{
		try
		{
			if (_steamReady.IsSet) return (SteamUser.GetSteamID().m_SteamID, SteamFriends.GetPersonaName());
		}
		catch (Exception) { }
		return (0uL, Environment.UserName);
	}

	// Steam: a few attempts 15 s apart (a minute, as the game's own join waited); a same-PC TCP host may still be starting.
	private const int SteamAttempts = 4, SteamRetryMs = 15000, TcpAttempts = 400, TcpRetryMs = 3000;

	private void LoopOwn(SteamLink? steam, TcpLink? tcp, MpTransport transport, HostSession? host)
	{
		var connectedAt = new Dictionary<PeerId, long>();
		var detailedDone = new HashSet<PeerId>();
		transport.PeerConnected += p => connectedAt[p] = MpClock.NowMs();
		transport.PeerDisconnected += (p, info) =>
		{
			connectedAt.Remove(p);
			detailedDone.Remove(p);
			_lastLoss = DescribeLoss(info);
		};
		long lastStatusMs = MpClock.NowMs();
		long lastRelayCheckMs = 0L;
		string lastRelayStatus = "";
		uint conn = 0u;
		if (steam == null) _joinTarget = 0uL;
		// Steam's first attempt waits only for the relay or network config (checked below, with a 20 s fallback); TCP's
		// keeps half a second for a server started on this PC alongside.
		long nextAttemptMs = MpClock.NowMs() + (steam != null ? 0 : 500);
		int attempts = 0;
		int maxAttempts = steam != null ? SteamAttempts : TcpAttempts;
		int retryMs = steam != null ? SteamRetryMs : TcpRetryMs;
		long waitStartMs = MpClock.NowMs();
		bool wasConnected = false;
		bool lossHandled = false;
		var iteration = new Stopwatch();
		while (!_stop)
		{
			iteration.Restart();
			long now = MpClock.NowMs();
			if (steam != null && now - lastRelayCheckMs >= 2000)
			{
				lastRelayCheckMs = now;
				string relay = SteamLink.RelayStatus(_o.Dedicated);
				if (relay != lastRelayStatus)
				{
					_log.Info("net: " + relay);
					lastRelayStatus = relay;
				}
			}
			ClientSession? client = _client;
			bool hasPeers = transport.Peers.Count > 0;
			bool linkOpen = conn != 0 && (steam?.HasConnection(conn) ?? tcp!.HasConnection(conn));
			wasConnected |= hasPeers;
			if (_o.Join && wasConnected && !hasPeers && !linkOpen && client != null && !lossHandled)
			{
				lossHandled = true;
				nextAttemptMs = long.MaxValue;
				if (client.LeftOnPurpose) _log.Info("net: left the server");
				else if (client.CurrentPhase == ClientSession.Phase.Rejected) _log.Info("net: the server refused this join and closed the connection");
				else
				{
					_log.Warn("net: lost the server (" + _lastLoss + ")");
					ClientWorld.ConnectionLost(client, _lastLoss);
				}
			}
			if (steam != null && !_o.UseSteamIp)
			{
				ulong previousTarget = _joinTarget;
				SteamJoinTick(now, hasPeers || linkOpen || wasConnected, host);
				if (_joinTarget != previousTarget && !hasPeers && !linkOpen)
				{
					attempts = 0;
					nextAttemptMs = now;
				}
			}
			if (_o.Join && _o.JoinCode != null && !_ownJoinRefused)
			{
				_ownJoinRefused = true;
				nextAttemptMs = long.MaxValue;
				ClientWorld.Current?.FailJoin("Join codes do not work with this version. Join through a Steam friend, an invite, the server list or the server's address.");
			}
			_names?.Tick();
			if (_o.Join && !wasConnected && !hasPeers && !linkOpen && now >= nextAttemptMs && (client == null || client.CurrentPhase != ClientSession.Phase.Rejected) && (steam == null || _joinTarget != 0 || _o.UseSteamIp || _o.JoinSteamId != 0))
			{
				bool networkReady = steam == null || (_o.UseSteamIp ? SteamLink.NetworkConfigReady() : SteamLink.RelayReady());
				if (networkReady || now - waitStartMs > 20000)
				{
					if (attempts >= maxAttempts)
					{
						_log.Error($"net: no answer from the server after {maxAttempts} connection attempts");
						ClientWorld.Progress.SetAttempt(maxAttempts, maxAttempts);
						ClientWorld.Current?.FailJoin($"No answer from the server within {maxAttempts * retryMs / 1000} s. It may still be starting, be offline, or be blocked by a firewall.");
						nextAttemptMs = long.MaxValue;
					}
					else
					{
						attempts++;
						ClientWorld.Progress.SetAttempt(attempts, maxAttempts);
						if (ClientWorld.Progress.Stage <= JoinStage.LookingForHost)
							ClientWorld.Progress.Set(JoinStage.Connecting, now, networkReady ? null : _o.UseSteamIp ? "Steam authentication configuration loading" : "relay starting");
						conn = Dial(steam, tcp, attempts, networkReady);
						nextAttemptMs = now + retryMs;
					}
				}
			}
			transport.Poll();
			if (_o.Dedicated)
			{
				GameServer.RunCallbacks();
				// Steam's server list counts authenticated users; SE2 Online's players are not, so they are reported as bots.
				if (host != null && now >= _nextPlayerCountMs)
				{
					_nextPlayerCountMs = now + 5000;
					try { SteamGameServer.SetBotPlayerCount(host.Players().Count(p => p.Peer.HasValue)); } catch (Exception) { }
					try { Y51_SE2Online.Steam.SteamDedicatedSession.PublishAccess(host.HasPassword, host.Access.WhitelistOnly); } catch (Exception) { }
				}
			}
			UpdateDirectory(now);
			host?.Tick();
			_client?.Tick();
			foreach (var entry in connectedAt)
			{
				if (steam != null && !detailedDone.Contains(entry.Key) && now - entry.Value >= 3000)
				{
					detailedDone.Add(entry.Key);
					LogDetailed(steam, entry.Key);
				}
			}
			if (now - lastStatusMs >= LogFormat.StatusIntervalMs)
			{
				lastStatusMs = now;
				LogLinkStatus(steam, tcp, transport, now);
				_log.Info($"net status: {(host != null ? $"hosting, {transport.Peers.Count} connected" : $"joined: {ClientWorld.Progress.Stage}")}");
				if (host != null) _log.Info("net status: " + _profile.Status(now, transport, host, PeerNames));
			}
			if (host != null) _profile.Tick(now, transport, host, PeerNames);
			if (iteration.ElapsedMilliseconds > 250) _log.Warn($"net: loop iteration took {iteration.ElapsedMilliseconds} ms ({transport.LastPollDetail})");
			Thread.Sleep(1);
		}
	}

	private uint Dial(SteamLink? steam, TcpLink? tcp, int attempt, bool networkReady)
	{
		if (steam == null)
		{
			if (attempt == 1 || attempt % 10 == 0) _log.Info($"net: connect attempt {attempt} to TCP host {_o.JoinIp}");
			return tcp!.Connect(_o.JoinIp!);
		}
		if (_o.UseSteamIp)
		{
			_log.Info($"net: connect attempt {attempt} to {_o.JoinIp}{(networkReady ? "" : " (Steam not ready yet)")}");
			ClientWorld.Progress.SetHost(_o.JoinIp!.ToString());
			return steam.Connect(_o.JoinIp!);
		}
		ulong hostId = JoinTargetRule.SteamConnectId(_joinTarget, _o);
		_log.Info($"net: connect attempt {attempt} to host {hostId}{(networkReady ? "" : " (relay network not ready yet)")}");
		// Steam refuses P2P to your own Steam ID: a second game instance on this account joins the first one through its
		// IP listener on this PC.
		return hostId == _localIdentity && hostId != 0
			? steam.Connect(new IPEndPoint(IPAddress.Loopback, MpMenu.Settings?.ListenPort ?? MpOptions.DefaultIpPort))
			: steam.Connect(hostId);
	}

	private void LogLinkStatus(SteamLink? steam, TcpLink? tcp, MpTransport transport, long now)
	{
		bool any = false;
		foreach (var (conn, remote, connected, quickStatus) in steam != null ? SteamConnections(steam) : TcpConnections(tcp!))
		{
			any = true;
			string app = "";
			if (transport.TryGetStats(new PeerId(remote), out PeerStats stats))
			{
				ChannelStats[] channels = stats.Channels;
				app = $" | app rtt={stats.LastRttMs:0} ms (avg {stats.AvgRttMs:0.0}, max {stats.MaxRttMs:0}, pongs {stats.PongsReceived}/{stats.PingsSent}) ctl {channels[0].FramesSent}/{channels[0].FramesReceived} fr, bulk {channels[1].BytesSent / 1024}/{channels[1].BytesReceived / 1024} KiB tx/rx, state {channels[2].FramesSent}/{channels[2].FramesReceived} fr {channels[2].BytesSent / 1024}/{channels[2].BytesReceived / 1024} KiB, queued {MpLogExtensions.Bytes(stats.BulkQueuedBytes)}, reasm {stats.ReassemblyInProgress}, seqGaps {stats.SeqGaps} protoErr {stats.ProtocolErrors} busy {stats.SendBusy}";
			}
			_log.Info($"net status {remote} conn {conn} {(connected ? "connected" : "pending")}: {quickStatus}{app}");
		}
		if (steam != null && any && now - _lastDetailedMs >= LogFormat.DetailedStatusIntervalMs)
		{
			_lastDetailedMs = now;
			foreach (var c in steam.Connections())
				if (c.Connected) LogDetailed(steam, new PeerId(c.Remote));
		}
		if (!any && !_idleLogged) _log.Info(_o.Host ? "net status: no players connected (waiting)" : "net status: not connected");
		_idleLogged = !any;

		static IEnumerable<(uint, ulong, bool, string)> SteamConnections(SteamLink l)
		{
			foreach (var (conn, remote, connected) in l.Connections()) yield return (conn, remote, connected, l.QuickStatus(conn));
		}

		static IEnumerable<(uint, ulong, bool, string)> TcpConnections(TcpLink l)
		{
			foreach (var (conn, _, connected) in l.Connections()) yield return (conn, TcpLink.SyntheticId(conn), connected, l.QuickStatus(conn));
		}
	}

	private void LogDetailed(SteamLink link, PeerId peer)
	{
		foreach (var (conn, remote, connected) in link.Connections())
			if (remote == peer.Value && connected) _log.Info($"net detailed status {remote}:\n{link.DetailedStatus(conn).TrimEnd()}");
	}
}
