using System;
using System.Threading;
using Keen.VRage.Core.Services.GameSessions;
using Keen.VRage.Steam.GameSessions;
using Steamworks;
using Y51_SE2Online.Core;
using Y51_SE2Online.KeenNetcode;
using Y51_SE2Online.Net;
using Y51_SE2Online.Sync;
using Y51_SE2Online.SyncEngine;

namespace Y51_SE2Online.NetHost;

// SE2 4.0: SE2 Online's sessions ride Keen's own game connection (KeenLink); Keen's netcode carries the world.
internal sealed partial class NetSession
{
	private bool _keenJoinIssued;

	private long _nextKeenTargetMs;

	private long _codeSearchSinceMs;

	private void RunKeen()
	{
		if (_o.Host) { ResourceMultipliers.Current = _o.UsageMultipliers; InventoryMultipliers.Current = _o.InventorySizes; }
		// A dedicated server signs in as a Steam game server; the Steam client API is not running there.
		if (!_o.Dedicated && !WaitForSteam())
		{
			_log.Info("net: stopped before Steam was ready (" + _stopReason + ")");
			return;
		}
		ulong localId;
		string name;
		if (_o.Dedicated)
		{
			// Keen's dedicated session signs the Steam game server in; it has an id once its listener is up.
			while (!_stop && KeenNet.ServerComms == null) Thread.Sleep(100);
			if (_stop) return;
			localId = SteamGameServer.GetSteamID().m_SteamID;
			name = _o.SessionName;
		}
		else
		{
			localId = SteamUser.GetSteamID().m_SteamID;
			name = SteamFriends.GetPersonaName();
		}
		if (_o.PlayerName != null) name = _o.PlayerName;
		_localIdentity = localId;
		_log.Info($"net: Steam '{name}' SteamID64={localId}; SE2 Online rides the game's own connection");
		var link = new KeenLink(_o.Host, _log);
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
			if (!_o.Dedicated) SteamJoinStart(localId, name);
			if (_o.Host && _o.Advertise) _directory = new DirectoryPublisher(new DirectoryClient(_o.Dedicated ? _o.ServersDirectory : _o.SessionsDirectory), message => _log.WarnOnce("directory-publish", message));
			_keenJoinIssued = _o.KeenAttached;
			_client = client;
			_host = host;
			_transport = transport;
			LoopKeen(transport, host);
		}
		finally
		{
			try
			{
				_directory?.StopAsync().GetAwaiter().GetResult();
				_advertiser?.Clear(_stopReason);
				_friends?.Dispose();
				transport?.DisconnectAll(1004, _stopReason);
				transport?.Poll();
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

	private void LoopKeen(MpTransport transport, HostSession? host)
	{
		transport.PeerDisconnected += (_, info) => _lastLoss = DescribeLoss(info);
		long lastStatusMs = MpClock.NowMs();
		bool wasConnected = false;
		bool lossHandled = false;
		while (!_stop)
		{
			long now = MpClock.NowMs();
			ClientSession? client = _client;
			bool hasPeers = transport.Peers.Count > 0;
			wasConnected |= hasPeers;
			if (_o.Join && !_o.UseSteamIp && !_keenJoinIssued) SteamJoinTick(now, false, host);
			if (_o.Join && !_keenJoinIssued && now >= _nextKeenTargetMs && KeenTarget() is { } target)
			{
				_keenJoinIssued = true;
				ClientWorld.Current?.Join(target);
			}
			if (_o.Join && wasConnected && !hasPeers && client != null && !lossHandled)
			{
				lossHandled = true;
				if (!client.LeftOnPurpose && client.CurrentPhase != ClientSession.Phase.Rejected)
				{
					_log.Warn("net: lost the host (" + _lastLoss + ")");
					ClientWorld.ConnectionLost(client, _lastLoss);
				}
			}
			transport.Poll();
			UpdateDirectory(now);
			host?.Tick();
			client?.Tick();
			_names?.Tick();
			if (now - lastStatusMs >= LogFormat.StatusIntervalMs)
			{
				lastStatusMs = now;
				_log.Info($"net status: {(host != null ? $"hosting, {transport.Peers.Count} connected" : $"joined: {ClientWorld.Progress.Stage}")}");
				if (host != null) _log.Info("net status: " + _profile.Status(now, transport, host, PeerNames));
			}
			if (host != null) _profile.Tick(now, transport, host, PeerNames);
			Thread.Sleep(1);
		}
	}

	private static LocalSessionID ThisPcHost() => new(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, MpMenu.Settings?.ListenPort ?? MpOptions.DefaultIpPort));

	private static bool IsThisPc(System.Net.IPAddress address)
	{
		if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
		try
		{
			foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
				foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
					if (unicast.Address.Equals(address)) return true;
		}
		catch (System.Net.NetworkInformation.NetworkInformationException) { }
		return false;
	}

	/// <summary>The Keen session to join: a dedicated server's address, or the hosting friend's Steam lobby.</summary>
	private GameSessionID? KeenTarget()
	{
		_nextKeenTargetMs = MpClock.NowMs() + 1000;
		if (_o.KeenSession is { } chosen) return chosen;
		if (_o.JoinCode is { } code)
		{
			if (KeenNet.FindLobby(code) is CSteamID lobby)
				return SteamMatchmaking.GetLobbyOwner(lobby).m_SteamID == _localIdentity ? ThisPcHost() : new SteamLobbyID(lobby);
			_codeSearchSinceMs = _codeSearchSinceMs == 0 ? MpClock.NowMs() : _codeSearchSinceMs;
			if (MpClock.NowMs() - _codeSearchSinceMs > 20000)
			{
				_keenJoinIssued = true;
				ClientWorld.Current?.FailJoin($"No session uses join code {code.ToDisplayString()}. Check the code, and ask the host to turn off Friends only (Steam cannot find friends-only sessions by code).");
			}
			return null;
		}
		// A server on this PC: loopback TCP, not Steam. Two game instances of one Steam account share its P2P traffic, so a
		// second instance joining over Steam drops the first one's connection.
		if (_o.JoinIp is { } ip)
		{
			// This PC's own LAN address too: over Steam an extra instance is the account itself, so it took the host's
			// identity and the host's character (both games drove one body and neither saw the other).
			if (System.Net.IPAddress.IsLoopback(ip.Address) || IsThisPc(ip.Address) && System.IO.File.Exists(LocalPeers.KeyPath(ip.Port)))
				return new LocalSessionID(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, ip.Port));
			// Dedicated servers take IP joins on the game's own socket; a co-op host listens through SE2 Online's.
			// The game finds the server's Steam id through a server query to the address it joins, then connects by that id.
			var query = ServerPinger.QueryEndpoint(ip);
			_log.Info($"net: {ip} is {(query != null ? $"a dedicated server (queries on port {query.Port})" : "a co-op host (no server query answer)")}");
			return query != null ? new GameSessionIPEndpoint(query) : new SteamIpSessionID(ip);
		}
		if (_joinTarget == 0) return null;
		// Steam will not connect this account to itself: another instance of it hosts on this PC.
		if (_joinTarget == _localIdentity) return ThisPcHost();
		if (SteamFriends.GetFriendGamePlayed(new CSteamID(_joinTarget), out FriendGameInfo_t game) && game.m_steamIDLobby.IsValid())
		{
			return new SteamLobbyID(game.m_steamIDLobby);
		}
		if (KeenNet.FindLobbyOf(_joinTarget) is CSteamID found) return new SteamLobbyID(found);
		_log.WarnOnce("keen-no-lobby-" + _joinTarget, $"net: looking up host {_joinTarget}'s game lobby (it is not a Steam friend's game, or not joinable yet)");
		return null;
	}
}
