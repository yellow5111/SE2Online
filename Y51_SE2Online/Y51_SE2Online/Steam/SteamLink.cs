using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using Steamworks;
using Y51_SE2Online.Core;
using Y51_SE2Online.Net;

namespace Y51_SE2Online.Steam;

internal sealed class SteamLink : IPacketLink, IDisposable
{
	private sealed class Conn
	{
		public HSteamNetConnection Handle;

		public ulong Remote;

		public bool Incoming;

		public bool Connected;

		public bool LanesOk;

		public long LastInboundMs;

		/// <summary>A host's adaptive send rate floor for this player (incoming connections only).</summary>
		public Y51_SE2Online.Net.SendRatePolicy? Rate;

		public long NextRateLookMs;
	}

	private readonly IMpLog _log;

	private readonly int _vport;

	private readonly Func<ulong, bool> _accept;

	private readonly int _sendRateMin;

	private readonly int _sendRateMax;

	private readonly SteamNetworkingConfigValue_t[] _options;

	private readonly ConnectionTable _table = new();

	private readonly ConcurrentQueue<SteamNetConnectionStatusChangedCallback_t> _status = new();

	private readonly Dictionary<uint, Conn> _conns = new();

	private readonly nint[] _recv = new nint[64];

	private readonly nint[] _sendPtr = new nint[1];

	private readonly long[] _sendResult = new long[1];

	private Callback<SteamNetConnectionStatusChangedCallback_t>? _statusCallback;

	private HSteamListenSocket _listen = HSteamListenSocket.Invalid;

	private HSteamListenSocket _ipListen = HSteamListenSocket.Invalid;

	private readonly SteamSocketApi _api;

	private readonly bool _gameServer;

	private readonly bool _adaptiveRate;

	private readonly IPEndPoint _listenAddress;

	private HSteamNetPollGroup _pollGroup = HSteamNetPollGroup.Invalid;

	private bool _disposed;

	public long CallbacksSeen;

	private readonly List<LinkEvent> _pendingEvents = new();

	public string Name => _gameServer ? "steam-dedicated" : "steam-p2p-ip";

	public int MaxMessageSize => 524288;

	public bool AuthenticatesPeers => true;

	public SteamLink(IMpLog log, int virtualPort, int sendRateMin, int sendRateMax, Func<ulong, bool> accept, IPEndPoint listenAddress = null, bool gameServer = false, bool adaptiveRate = false)
	{
		_adaptiveRate = adaptiveRate;
		_api = new SteamSocketApi(gameServer);
		_gameServer = gameServer;
		_listenAddress = listenAddress;
		_log = log;
		_vport = virtualPort;
		_accept = accept;
		_sendRateMin = Math.Min(sendRateMin, sendRateMax);
		_sendRateMax = sendRateMax;
		_options = new[]
		{
			Int32(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMin, _sendRateMin),
			Int32(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax, sendRateMax),
			Int32(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendBufferSize, 4194304),
			Int32(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_RecvBufferSize, 8388608),
			Int32(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_TimeoutConnected, 20000)
		};
	}

	private static SteamNetworkingConfigValue_t Int32(ESteamNetworkingConfigValue key, int value)
	{
		var config = new SteamNetworkingConfigValue_t
		{
			m_eValue = key,
			m_eDataType = ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32
		};
		config.m_val.m_int32 = value;
		return config;
	}

	public void Start(bool listen)
	{
		try
		{
			StartCore(listen);
		}
		catch
		{
			Dispose();
			throw;
		}
	}

	private void StartCore(bool listen)
	{
		if (!SteamMessageLayout.MatchesNative(out string detail))
		{
			throw new InvalidOperationException("SteamNetworkingMessage_t layout mismatch: " + detail);
		}
		_statusCallback = _gameServer
			? Callback<SteamNetConnectionStatusChangedCallback_t>.CreateGameServer(OnStatusChanged)
			: Callback<SteamNetConnectionStatusChangedCallback_t>.Create(OnStatusChanged);
		_api.InitAuthentication();
		_pollGroup = _api.CreatePollGroup();
		if (_pollGroup == HSteamNetPollGroup.Invalid)
		{
			throw new InvalidOperationException("CreatePollGroup failed");
		}
		if (listen)
		{
			if (_gameServer && _listenAddress == null)
			{
				throw new InvalidOperationException("Dedicated Steam servers require an IP listen address.");
			}
			if (!_gameServer)
			{
				_listen = _api.CreateListenSocketP2P(_vport, _options.Length, _options);
				if (_listen == HSteamListenSocket.Invalid)
				{
					throw new InvalidOperationException($"CreateListenSocketP2P({_vport}) failed");
				}
				_log.Info($"listening for P2P connections on virtual port {_vport}");
			}
			if (_listenAddress != null && !TryListenIp() && _gameServer)
			{
				throw new InvalidOperationException("Could not listen on " + _listenAddress + ". Check that its UDP port is available.");
			}
		}
	}

	private static SteamNetworkingIPAddr NativeAddress(IPEndPoint endpoint)
	{
		var result = new SteamNetworkingIPAddr { m_ipv6 = new byte[16] };
		if (!result.ParseString(endpoint.ToString()))
		{
			throw new FormatException("Steam could not parse the IP endpoint.");
		}
		return result;
	}

	public uint Connect(IPEndPoint endpoint)
	{
		var address = NativeAddress(endpoint);
		var connection = _api.ConnectByIPAddress(ref address, _options.Length, _options);
		if (connection == HSteamNetConnection.Invalid)
		{
			return 0;
		}
		_api.SetConnectionPollGroup(connection, _pollGroup);
		_conns[connection.m_HSteamNetConnection] = new Conn { Handle = connection };
		ApplyRates(connection, _sendRateMin);
		_log.Info("Connecting with Steam Networking Sockets to " + endpoint);
		return connection.m_HSteamNetConnection;
	}

	public uint Connect(ulong steamId)
	{
		if (_gameServer)
		{
			throw new InvalidOperationException("Dedicated servers cannot initiate P2P connections.");
		}
		SteamNetworkingIdentity identity = default;
		identity.SetSteamID64(steamId);
		var connection = _api.ConnectP2P(ref identity, _vport, _options.Length, _options);
		if (connection == HSteamNetConnection.Invalid)
		{
			_log.Error($"ConnectP2P({steamId}, vport {_vport}) returned an invalid handle");
			return 0;
		}
		_api.SetConnectionPollGroup(connection, _pollGroup);
		Register(connection, steamId, incoming: false);
		_log.Info($"connecting to {steamId} on virtual port {_vport} (conn {connection.m_HSteamNetConnection})");
		return connection.m_HSteamNetConnection;
	}

	private void Register(HSteamNetConnection h, ulong remote, bool incoming)
	{
		uint id = h.m_HSteamNetConnection;
		long now = MpClock.NowMs();
		if (incoming && _conns.Values.Any(c => c.Remote == remote && c.Connected))
		{
			remote = PeerFor(id, remote, now);
			if (remote == 0)
			{
				_api.CloseConnection(h, DisconnectCodes.NotAllowed, "too many game instances on this Steam account", false);
				return;
			}
		}
		// An IP connection's identity comes with authentication (MarkConnected); superseding on 0 would close
		// whatever else is still connecting by IP.
		if (remote != 0) Supersede(id, remote, incoming, now, _pendingEvents);
		var conn = new Conn
		{
			Handle = h,
			Remote = remote,
			Incoming = incoming,
			LastInboundMs = now,
			Rate = _adaptiveRate && incoming ? new Y51_SE2Online.Net.SendRatePolicy(_sendRateMin, _sendRateMax) : null
		};
		_conns[id] = conn;
		ApplyRates(h, conn.Rate?.Current ?? _sendRateMin);
	}

	// Another live game instance on the same Steam account is a separate player (an alias peer id). A slot whose
	// connection has gone quiet is a crashed game coming back before Steam's 20 s timeout: it takes that slot again
	// (Supersede closes the old connection), so the player keeps its identity and character. 0: every slot is live.
	private ulong PeerFor(uint id, ulong account, long now)
	{
		List<long> Holders(ulong peer) => _conns.Values.Where(c => c.Remote == peer && c.Connected).Select(c => c.LastInboundMs).ToList();
		ulong chosen = PeerAlias.Choose(account, Holders, now, out bool replaced);
		if (chosen != 0)
		{
			_log.Info(replaced
				? $"incoming conn {id}: peer {chosen} reconnected while its old connection had received nothing for {now - Holders(chosen).Max()} ms; replacing it (same player)"
				: $"incoming conn {id}: this Steam account is already connected; the new game instance joins as peer {chosen}");
		}
		return chosen;
	}

	// Every other connection on this peer id is closed; a connected one reports its disconnect (the player is parked).
	private void Supersede(uint id, ulong remote, bool incoming, long now, List<LinkEvent> events)
	{
		foreach (var superseded in _table.Add(id, remote, incoming, now))
		{
			if (_conns.Remove(superseded.Conn, out var stale))
			{
				_log.Warn($"conn {superseded.Conn} from {remote} ({(stale.Connected ? "connected" : "still connecting")}) superseded by newer conn {id}: closing the stale one");
				_api.CloseConnection(stale.Handle, DisconnectCodes.Superseded, "superseded by a newer connection", bEnableLinger: false);
				if (stale.Connected)
				{
					events.Add(new LinkEvent(LinkEventKind.Disconnected, superseded.Conn, remote, DisconnectCodes.Superseded, "superseded by a newer connection"));
				}
			}
		}
	}

	private void ApplyRates(HSteamNetConnection h, int min)
	{
		try
		{
			bool minOk = SetConnInt(h, ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMin, min);
			bool maxOk = SetConnInt(h, ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax, _sendRateMax);
			if (!minOk || !maxOk)
			{
				_log.WarnOnce("steam-rate-set", $"SetConfigValue(SendRateMin/Max) on a connection failed (min ok={minOk}, max ok={maxOk}); relying on socket options");
			}
		}
		catch (Exception ex)
		{
			_log.WarnOnce("steam-rate-set-ex", "SetConfigValue(SendRateMin/Max) threw " + ex.GetType().Name + ": " + ex.Message);
		}
	}

	private unsafe bool SetConnInt(HSteamNetConnection h, ESteamNetworkingConfigValue key, int value)
	{
		int nativeValue = value;
		return _api.SetConfigValue(key, ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Connection, (nint)h.m_HSteamNetConnection, ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32, (nint)(&nativeValue));
	}

	public bool HasConnection(uint conn) => _conns.ContainsKey(conn);

	private void OnStatusChanged(SteamNetConnectionStatusChangedCallback_t s) => _status.Enqueue(s);

	private long _ipRetryAt = -1;

	// A game that crashed or is still closing can hold the UDP port for a while; P2P keeps working and the IP
	// listener (direct IP joins, and same-account joins over loopback) comes up once the port is free.
	private bool TryListenIp()
	{
		var address = NativeAddress(_listenAddress);
		_ipListen = _api.CreateListenSocketIP(ref address, _options.Length, _options);
		if (_ipListen != HSteamListenSocket.Invalid)
		{
			_ipRetryAt = -1;
			_log.Info("Steam Networking Sockets IP listener: " + _listenAddress);
			return true;
		}
		if (!_gameServer)
		{
			if (_ipRetryAt < 0) _log.Info("could not listen on " + _listenAddress + " (UDP port in use, often by another SE2 instance that is still closing); Steam P2P joins work, direct IP and same-account joins wait until the port is free. Retrying every 5 s.");
			_ipRetryAt = Environment.TickCount64 + 5000;
		}
		return false;
	}

	public unsafe void Pump(List<LinkEvent> events, List<LinkMessage> messages)
	{
		if (_ipRetryAt >= 0 && Environment.TickCount64 >= _ipRetryAt) TryListenIp();
		if (_disposed)
		{
			return;
		}
		if (_pendingEvents.Count > 0)
		{
			events.AddRange(_pendingEvents);
			_pendingEvents.Clear();
		}
		SteamNetConnectionStatusChangedCallback_t status;
		while (_status.TryDequeue(out status))
		{
			CallbacksSeen++;
			HandleStatus(in status, events);
		}
		for (int batch = 0; batch < 32; batch++)
		{
			int count = _api.ReceiveMessagesOnPollGroup(_pollGroup, _recv, _recv.Length);
			if (count <= 0)
			{
				break;
			}
			for (int i = 0; i < count; i++)
			{
				nint message = _recv[i];
				try
				{
					nint data = Marshal.ReadIntPtr(message, SteamMessageLayout.Data);
					int size = Marshal.ReadInt32(message, SteamMessageLayout.Size);
					uint connId = (uint)Marshal.ReadInt32(message, SteamMessageLayout.Conn);
					byte[] payload = new byte[size];
					if (size > 0)
					{
						new ReadOnlySpan<byte>((void*)data, size).CopyTo(payload);
					}
					if (_conns.TryGetValue(connId, out var c))
					{
						c.LastInboundMs = MpClock.NowMs();
						if (!c.Connected) TryPromote(c, events);
					}
					messages.Add(new LinkMessage(connId, payload));
				}
				finally
				{
					SteamNetworkingMessage_t.Release(message);
				}
			}
			if (count < _recv.Length)
			{
				break;
			}
		}
		if (_adaptiveRate) LookRates();
	}

	// Once a second per player: the send rate floor follows what the player's link carries (SendRatePolicy).
	private void LookRates()
	{
		long now = MpClock.NowMs();
		foreach (var pair in _conns)
		{
			var c = pair.Value;
			if (c.Rate == null || !c.Connected || now < c.NextRateLookMs) continue;
			c.NextRateLookMs = now + Y51_SE2Online.Net.SendRatePolicy.LookEveryMs;
			var status = new SteamNetConnectionRealTimeStatus_t { reserved = new uint[16] };
			var lanes = new SteamNetConnectionRealTimeLaneStatus_t { reserved = new uint[10] };
			if (_api.GetConnectionRealTimeStatus(c.Handle, ref status, 0, ref lanes) != EResult.k_EResultOK) continue;
			int before = c.Rate.Current;
			if (c.Rate.Look(status.m_flConnectionQualityRemote, status.m_nPing))
			{
				bool ok = SetConnInt(c.Handle, ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMin, c.Rate.Current);
				_log.Info($"steam: conn {PopId(pair.Key)} send rate floor {before / 1024} -> {c.Rate.Current / 1024} KiB/s (quality {status.m_flConnectionQualityRemote:0.00}, ping {status.m_nPing} ms){(ok ? "" : "; Steam refused the setting")}");
			}
		}
	}

	private void TryPromote(Conn c, List<LinkEvent> events)
	{
		if (_api.GetConnectionInfo(c.Handle, out var info) && info.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected)
		{
			MarkConnected(c, events);
		}
	}

	private void HandleStatus(in SteamNetConnectionStatusChangedCallback_t s, List<LinkEvent> events)
	{
		uint id = s.m_hConn.m_HSteamNetConnection;
		var state = s.m_info.m_eState;
		ulong remote = s.m_info.m_identityRemote.GetSteamID64();
		_conns.TryGetValue(id, out var c);
		// Steam reports other local game instances' connections (same account) to this process too; only
		// connections arriving on our own listen sockets are ours to accept or close.
		if (c == null && (s.m_info.m_hListenSocket.m_HSteamListenSocket == 0 || (s.m_info.m_hListenSocket != _listen && s.m_info.m_hListenSocket != _ipListen)))
		{
			return;
		}
		switch (state)
		{
		case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting:
			if (c == null)
			{
				AcceptIncoming(s.m_hConn, remote);
				break;
			}
			_log.Info($"conn {id} to {remote}: connecting");
			break;
		case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_FindingRoute:
			_log.Info($"conn {id} to {remote}: finding route");
			break;
		case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected:
			if (c != null && !c.Connected)
			{
				MarkConnected(c, events);
			}
			break;
		case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer:
		case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally:
		{
			bool byPeer = state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer;
			int endReason = s.m_info.m_eEndReason;
			string endDebug = s.m_info.m_szEndDebug;
			_log.Info($"conn {id} to {remote}: {(byPeer ? "closed by peer" : "problem detected locally")} (was {Short(s.m_eOldState)}), end reason {endReason} {EndReasonName(endReason)}: '{endDebug}'");
			if (c != null)
			{
				_conns.Remove(id);
				_table.Remove(id);
				if (c.Connected)
				{
					events.Add(new LinkEvent(LinkEventKind.Disconnected, id, c.Remote, endReason, EndReasonName(endReason) + ": " + endDebug, byPeer));
				}
				else
				{
					events.Add(new LinkEvent(LinkEventKind.Disconnected, id, c.Remote, endReason, "connect failed, " + EndReasonName(endReason) + ": " + endDebug, byPeer));
				}
			}
			_api.CloseConnection(s.m_hConn, 0, null, bEnableLinger: false);
			break;
		}
		}
	}

	private void AcceptIncoming(HSteamNetConnection h, ulong remote)
	{
		if (remote == 0)
		{
			_api.CloseConnection(h, DisconnectCodes.NotAllowed, "Steam identity required", false);
			return;
		}
		uint id = h.m_HSteamNetConnection;
		if (!_accept(remote))
		{
			_log.Warn($"rejecting incoming connection from {remote} (not on -y51online:allow list)");
			_api.CloseConnection(h, DisconnectCodes.NotAllowed, "not on allow list", bEnableLinger: false);
			return;
		}
		var result = _api.AcceptConnection(h);
		if (result != EResult.k_EResultOK)
		{
			_log.Warn($"AcceptConnection from {remote} failed: {result}");
			_api.CloseConnection(h, 0, null, bEnableLinger: false);
		}
		else
		{
			_api.SetConnectionPollGroup(h, _pollGroup);
			Register(h, remote, incoming: true);
			_log.Info($"accepted incoming connection from {remote} (conn {id})");
		}
	}

	private void MarkConnected(Conn c, List<LinkEvent> events)
	{
		if (!_api.GetConnectionInfo(c.Handle, out var info) || info.m_identityRemote.GetSteamID64() == 0)
		{
			_api.CloseConnection(c.Handle, DisconnectCodes.NotAllowed, "Authenticated Steam identity required", false);
			events.Add(new LinkEvent(LinkEventKind.Disconnected, c.Handle.m_HSteamNetConnection, c.Remote, DisconnectCodes.NotAllowed, "No authenticated Steam identity"));
			_conns.Remove(c.Handle.m_HSteamNetConnection);
			return;
		}
		ulong identity = info.m_identityRemote.GetSteamID64();
		if (c.Remote == 0)
		{
			// IP connections learn the peer's Steam identity only once authenticated: the same slot rules as Register,
			// or two live connections ended up on one peer id.
			uint id = c.Handle.m_HSteamNetConnection;
			long now = MpClock.NowMs();
			ulong peer = c.Incoming && _conns.Values.Any(o => o.Remote == identity && o.Connected) ? PeerFor(id, identity, now) : identity;
			if (peer == 0)
			{
				_api.CloseConnection(c.Handle, DisconnectCodes.NotAllowed, "too many game instances on this Steam account", false);
				_conns.Remove(id);
				_table.Remove(id);
				return;
			}
			c.Remote = peer;
			Supersede(id, peer, c.Incoming, now, events);
		}
		else if (PeerAlias.SteamOf(c.Remote) != identity)
		{
			_api.CloseConnection(c.Handle, DisconnectCodes.NotAllowed, "Steam identity mismatch", false);
			return;
		}
		c.Connected = true;
		_table.MarkConnected(c.Handle.m_HSteamNetConnection);
		var lanes = _api.ConfigureConnectionLanes(c.Handle, LanePlan.Priorities.Length, LanePlan.Priorities, LanePlan.Weights);
		c.LanesOk = lanes == EResult.k_EResultOK;
		if (!c.LanesOk)
		{
			_log.Warn($"ConfigureConnectionLanes failed ({lanes}); all channels share lane 0");
		}
		_log.Info($"conn {c.Handle.m_HSteamNetConnection} to {c.Remote}: CONNECTED ({(c.Incoming ? "incoming" : "outgoing")}), lanes {(c.LanesOk ? "ok" : "off")}; {QuickStatus(c.Handle.m_HSteamNetConnection)}");
		events.Add(new LinkEvent(LinkEventKind.Connected, c.Handle.m_HSteamNetConnection, c.Remote));
	}

	public unsafe LinkSendResult Send(uint conn, Channel channel, bool reliable, ReadOnlySpan<byte> frame)
	{
		if (!_conns.TryGetValue(conn, out var c) || !c.Connected)
		{
			return LinkSendResult.Failed;
		}
		if (frame.Length > MaxMessageSize)
		{
			return LinkSendResult.Failed;
		}
		nint message = _api.AllocateMessage(frame.Length);
		if (message == IntPtr.Zero)
		{
			return LinkSendResult.Busy;
		}
		nint data = Marshal.ReadIntPtr(message, SteamMessageLayout.Data);
		frame.CopyTo(new Span<byte>((void*)data, frame.Length));
		Marshal.WriteInt32(message, SteamMessageLayout.Conn, (int)conn);
		// k_nSteamNetworkingSend_UnreliableNoNagle (1) or k_nSteamNetworkingSend_ReliableNoNagle (9)
		Marshal.WriteInt32(message, SteamMessageLayout.Flags, !reliable ? 1 : 9);
		Marshal.WriteInt16(message, SteamMessageLayout.Lane, (short)(c.LanesOk ? channel : Channel.Control));
		_sendPtr[0] = message;
		_api.SendMessages(1, _sendPtr, _sendResult);
		long result = _sendResult[0];
		if (result >= 0)
		{
			return LinkSendResult.Ok;
		}
		// A negative result is the message's -EResult.
		var error = (EResult)(-result);
		if (error == EResult.k_EResultLimitExceeded)
		{
			return LinkSendResult.Busy;
		}
		_log.WarnOnce($"steam-send-{error}", $"SendMessages to {c.Remote} failed: {error}");
		return LinkSendResult.Failed;
	}

	public bool HasLanePriorities(uint conn)
	{
		if (_conns.TryGetValue(conn, out var c))
		{
			return c.LanesOk;
		}
		return false;
	}

	/// <summary>The connection is up and visibly backed up (Sync/LinkGrace): late pongs from it are a slow link, not a dead game.</summary>
	public bool IsSessionAlive(uint conn)
	{
		if (!_conns.TryGetValue(conn, out var c) || !c.Connected) return false;
		var status = new SteamNetConnectionRealTimeStatus_t { reserved = new uint[16] };
		var lanes = new SteamNetConnectionRealTimeLaneStatus_t { reserved = new uint[10] };
		return _api.GetConnectionRealTimeStatus(c.Handle, ref status, 0, ref lanes) == EResult.k_EResultOK
			&& Y51_SE2Online.Sync.LinkGrace.Backlogged(status.m_cbPendingReliable, status.m_usecQueueTime.m_SteamNetworkingMicroseconds);
	}

	public int GetPendingReliableBytes(uint conn)
	{
		if (!_conns.TryGetValue(conn, out var c))
		{
			return int.MaxValue;
		}
		var status = new SteamNetConnectionRealTimeStatus_t { reserved = new uint[16] };
		var lanes = new SteamNetConnectionRealTimeLaneStatus_t { reserved = new uint[10] };
		if (_api.GetConnectionRealTimeStatus(c.Handle, ref status, 0, ref lanes) != EResult.k_EResultOK)
		{
			return int.MaxValue;
		}
		return status.m_cbPendingReliable;
	}

	public void Close(uint conn, int code, string reason, bool linger)
	{
		_table.Remove(conn);
		if (_conns.Remove(conn, out var c))
		{
			_api.CloseConnection(c.Handle, code, Truncate(reason, 120), linger);
		}
	}

	public int ConnectionCountFor(ulong remote) => _table.CountFor(remote);

	public IEnumerable<(uint Conn, ulong Remote, bool Connected)> Connections()
	{
		foreach (var pair in _conns)
		{
			yield return (pair.Key, pair.Value.Remote, pair.Value.Connected);
		}
	}

	public string QuickStatus(uint conn)
	{
		if (!_conns.TryGetValue(conn, out var c))
		{
			return "no such connection";
		}
		if (!_api.GetConnectionInfo(c.Handle, out var info))
		{
			return "GetConnectionInfo failed";
		}
		var status = new SteamNetConnectionRealTimeStatus_t { reserved = new uint[16] };
		var lanes = new SteamNetConnectionRealTimeLaneStatus_t { reserved = new uint[10] };
		var statusResult = _api.GetConnectionRealTimeStatus(c.Handle, ref status, 0, ref lanes);
		string route = (info.m_nFlags & 0x10) != 0
			? $"RELAYED via SDR pop={PopId(info.m_idPOPRelay.m_SteamNetworkingPOPID)} (remote pop {PopId(info.m_idPOPRemote.m_SteamNetworkingPOPID)})"
			: "DIRECT " + Addr(info.m_addrRemote);
		string flags = Flags(info.m_nFlags);
		if (statusResult == EResult.k_EResultOK)
		{
			return $"route={route} flags={flags} ping={status.m_nPing} ms quality local={status.m_flConnectionQualityLocal:0.00} remote={status.m_flConnectionQualityRemote:0.00} out={status.m_flOutBytesPerSec / 1024f:0.0} KiB/s in={status.m_flInBytesPerSec / 1024f:0.0} KiB/s sendRate={status.m_nSendRateBytesPerSecond / 1024} KiB/s pendingRel={status.m_cbPendingReliable} unackedRel={status.m_cbSentUnackedReliable} pendingUnrel={status.m_cbPendingUnreliable} queue={(double)status.m_usecQueueTime.m_SteamNetworkingMicroseconds / 1000.0:0.0} ms desc='{info.m_szConnectionDescription}'";
		}
		return $"route={route} flags={flags} (realtime status {statusResult})";
	}

	public string DetailedStatus(uint conn)
	{
		if (!_conns.TryGetValue(conn, out var c))
		{
			return "no such connection";
		}
		int result = _api.GetDetailedConnectionStatus(c.Handle, out var text, 8192);
		if (result != 0)
		{
			return $"GetDetailedConnectionStatus returned {result}";
		}
		return text;
	}

	public static string RelayStatus(bool gameServer = false)
	{
		SteamRelayNetworkStatus_t details;
		var relay = gameServer
			? SteamGameServerNetworkingUtils.GetRelayNetworkStatus(out details)
			: SteamNetworkingUtils.GetRelayNetworkStatus(out details);
		return $"relay={Short(relay)} anyRelay={Short(details.m_eAvailAnyRelay)} netConfig={Short(details.m_eAvailNetworkConfig)} pingMeasuring={details.m_bPingMeasurementInProgress != 0} '{details.m_debugMsg}'";
	}

	public static bool RelayReady() =>
		SteamNetworkingUtils.GetRelayNetworkStatus(out _) == ESteamNetworkingAvailability.k_ESteamNetworkingAvailability_Current;

	public static bool NetworkConfigReady()
	{
		SteamNetworkingUtils.GetRelayNetworkStatus(out var details);
		return details.m_eAvailNetworkConfig == ESteamNetworkingAvailability.k_ESteamNetworkingAvailability_Current;
	}

	// "k_ESteamNetworkingAvailability_Current" -> "Current"
	private static string Short<T>(T e) where T : Enum
	{
		string name = e.ToString();
		int underscore = name.LastIndexOf('_');
		if (underscore < 0)
		{
			return name;
		}
		return name.Substring(underscore + 1);
	}

	// k_nSteamNetworkConnectionInfoFlags_*
	private static string Flags(int f)
	{
		var names = new List<string>();
		if ((f & 1) != 0)
		{
			names.Add("unauthenticated");
		}
		if ((f & 2) != 0)
		{
			names.Add("unencrypted");
		}
		if ((f & 4) != 0)
		{
			names.Add("loopback");
		}
		if ((f & 8) != 0)
		{
			names.Add("fast");
		}
		if ((f & 0x10) != 0)
		{
			names.Add("relayed");
		}
		if ((f & 0x20) != 0)
		{
			names.Add("dualwifi");
		}
		if (names.Count != 0)
		{
			return string.Join("|", names);
		}
		return "none";
	}

	private static string Addr(SteamNetworkingIPAddr a)
	{
		try
		{
			if (a.IsIPv6AllZeros())
			{
				return "(no ip)";
			}
			a.ToString(out var text, bWithPort: true);
			return text;
		}
		catch (Exception)
		{
			return "(ip?)";
		}
	}

	// A POP id packs a 3- or 4-letter data-centre code; the optional 4th letter lives in the top byte.
	public static string PopId(uint id)
	{
		if (id == 0)
		{
			return "-";
		}
		Span<char> chars = stackalloc char[4];
		chars[0] = (char)((id >> 16) & 0xFF);
		chars[1] = (char)((id >> 8) & 0xFF);
		chars[2] = (char)(id & 0xFF);
		chars[3] = (char)((id >> 24) & 0xFF);
		return new string(chars[3] == '\0' ? chars.Slice(0, 3) : chars);
	}

	public static string EndReasonName(int reason)
	{
		if (Enum.IsDefined(typeof(ESteamNetConnectionEnd), reason))
		{
			return Short((ESteamNetConnectionEnd)reason);
		}
		return reason switch
		{
			1000 => "App_Bye",
			1001 => "App_ProtocolError",
			1002 => "App_HelloTimeout",
			1003 => "App_PingTimeout",
			1004 => "App_Shutdown",
			1005 => "App_NotAllowed",
			1006 => "App_Superseded",
			1007 => "App_VersionMismatch",
			>= 1000 and < 2000 => "App",
			>= 2000 and < 3000 => "AppException",
			>= 3000 and < 4000 => "Local",
			>= 4000 and < 5000 => "Remote",
			>= 5000 and < 6000 => "Misc",
			_ => "?",
		};
	}

	private static string Truncate(string s, int n)
	{
		if (s.Length > n)
		{
			return s.Substring(0, n);
		}
		return s;
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		foreach (var c in _conns.Values)
		{
			_api.CloseConnection(c.Handle, DisconnectCodes.Shutdown, "shutting down", bEnableLinger: true);
		}
		_conns.Clear();
		_table.Clear();
		if (_listen != HSteamListenSocket.Invalid)
		{
			_api.CloseListenSocket(_listen);
		}
		if (_ipListen != HSteamListenSocket.Invalid)
		{
			_api.CloseListenSocket(_ipListen);
		}
		if (_pollGroup != HSteamNetPollGroup.Invalid)
		{
			_api.DestroyPollGroup(_pollGroup);
		}
		_statusCallback?.Dispose();
	}
}
