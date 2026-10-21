using System;
using System.Collections.Generic;
using System.Net;
using Keen.VRage.Core.Services.Data;
using Keen.VRage.Core.Services.GameSessions;
using Keen.VRage.Core.Services.Networking;
using Y51_SE2Online.Core;
using Y51_SE2Online.Net;
using Y51_SE2Online.Steam;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>A co-op host reached at its IP address over Steam's authenticated IP sockets (direct connect).</summary>
internal sealed record SteamIpSessionID(IPEndPoint Endpoint) : GameSessionID;

/// <summary>
/// Direct IP connections for co-op hosts: SE2 4.0 hosts a world only through a Steam lobby, so the host also listens on
/// its IP with Steam's IP sockets (as SE2 Online 2.4 did) and feeds those players into the game's session. Steam still
/// authenticates each player, so they are the same game users, identities and SE2 Online players as over the lobby.
/// Each message travels as [game channel][reliability][payload].
/// </summary>
internal sealed class DirectIpPeers : IDisposable
{
	private readonly SteamLink _link;

	private readonly object _sync = new();

	private readonly Dictionary<uint, GameServiceUser> _users = new();

	private readonly Dictionary<GameServiceUser, uint> _conns = new();

	private readonly List<LinkEvent> _events = new();

	private readonly List<LinkMessage> _messages = new();

	private readonly GameServiceUser? _server;

	private readonly FragmentLink<GameServiceUser> _fragments;

	/// <summary>The host's first message on each connection: its mod list (KeenMods text), read before the game joins.</summary>
	public const byte ModsChannel = 255;

	/// <summary>Client: the host's mod list once it arrived.</summary>
	public string? HostMods { get; private set; }

	/// <summary>Host: listens on <paramref name="listen"/>. Client: connects to <paramref name="connect"/>, whose game user is <paramref name="server"/>.</summary>
	public DirectIpPeers(IMpLog log, IPEndPoint? listen, IPEndPoint? connect = null, GameServiceUser? server = null)
	{
		_link = new SteamLink(log, MpOptions.DefaultVirtualPort, 1024 * 1024, 8 * 1024 * 1024, _ => true, listen);
		_link.Start(listen != null);
		_server = server;
		_fragments = new FragmentLink<GameServiceUser>(SendFrame, log, Close, connect == null ? KeenNet.MayFragment : null);
		if (connect != null)
		{
			uint conn = _link.Connect(connect);
			_users[conn] = server!.Value;
			_conns[server.Value] = conn;
		}
	}

	public bool Has(GameServiceUser user)
	{
		lock (_sync) return _conns.ContainsKey(user);
	}

	/// <summary>Keen's network job thread.</summary>
	public void Pump(Action<GameServiceUser, bool> changed, Action<GameServiceUser, byte, MessageReliability, byte[]> received)
	{
		lock (_sync)
		{
			_events.Clear();
			_messages.Clear();
			_link.Pump(_events, _messages);
			foreach (var e in _events)
			{
				if (e.Kind == LinkEventKind.Connected)
				{
					// A client knows its server before the connection is up; a host learns each player's Steam id here.
					if (_server == null)
					{
						var user = KeenNet.UserOf(e.RemoteId);
						_users[e.Conn] = user;
						_conns[user] = e.Conn;
						_fragments.Send(user, System.Text.Encoding.UTF8.GetBytes(KeenMods.Current), ModsChannel, MessageReliability.Reliable);
						changed(user, true);
					}
				}
				else if (_users.Remove(e.Conn, out var user))
				{
					_conns.Remove(user);
					_fragments.Forget(user);
					changed(user, false);
				}
			}
			foreach (var m in _messages)
			{
				if (m.Data.Length < 2 || !_users.TryGetValue(m.Conn, out var user)) continue;
				bool mods = m.Data[0] == ModsChannel && _server != null;
				if (!mods && !GameChannels.Read(m.Data[0])) continue;
				if (_fragments.Receive(user, m.Data[0], m.Data.AsSpan(2)) is not { } whole) continue;
				if (mods) HostMods = System.Text.Encoding.UTF8.GetString(whole);
				else received(user, m.Data[0], (MessageReliability)m.Data[1], whole);
			}
			_fragments.Flush();
		}
	}

	public bool Send(GameServiceUser user, ReadOnlySpan<byte> data, byte channel, MessageReliability reliability)
	{
		lock (_sync)
		{
			return _conns.ContainsKey(user) && _fragments.Send(user, data, channel, reliability);
		}
	}

	// Steam's IP sockets take 512 KiB per message too; FragmentLink keeps each frame under that.
	private bool SendFrame(GameServiceUser user, byte channel, MessageReliability reliability, byte[] fragment)
	{
		lock (_sync)
		{
			if (!_conns.TryGetValue(user, out uint conn)) return false;
			var frame = new byte[fragment.Length + 2];
			frame[0] = channel;
			frame[1] = (byte)reliability;
			fragment.CopyTo(frame.AsSpan(2));
			bool reliable = reliability == MessageReliability.Reliable;
			return _link.Send(conn, reliable ? Channel.Control : Channel.State, reliable, frame) == LinkSendResult.Ok;
		}
	}

	public void Close(GameServiceUser user)
	{
		lock (_sync)
		{
			if (_conns.Remove(user, out uint conn))
			{
				_users.Remove(conn);
				_fragments.Forget(user);
				_link.Close(conn, DisconnectCodes.Bye, "closed", linger: true);
			}
		}
	}

	public void Dispose()
	{
		lock (_sync) _link.Dispose();
	}
}

/// <summary>Client side of a direct IP join to a co-op host: Keen's game session over <see cref="DirectIpPeers"/>.</summary>
internal sealed class DirectIpClientSession : LocalMessageSource, IGameSession, IPeerToPeerSession, IDisposable
{
	private readonly DirectIpPeers _peers;

	private bool _lost;

	public IPeerToPeerSession Communication => this;

	public GameServiceUser Server => LocalPeers.Server;

	public bool IsDedicated => false;

	public SessionJoinCode? JoinCode => null;

	public DirectIpClientSession(IPEndPoint endpoint, IMpLog log) => _peers = DirectIpSessions.Take(endpoint) ?? new DirectIpPeers(log, null, endpoint, LocalPeers.Server);

	public event Action<GameServiceUser>? ConnectionRequested;

	public event Action<GameServiceUser, ConnectionFailureReason>? PeerConnectionFailed;

	/// <summary>Keen's network job thread.</summary>
	public void Pump()
	{
		_peers.Pump((user, connected) =>
		{
			if (!connected && !_lost)
			{
				_lost = true;
				PeerConnectionFailed?.Invoke(user, ConnectionFailureReason.ClosedByPeer);
			}
		}, Enqueue);
	}

	public bool AcceptConnection(GameServiceUser user) => user == LocalPeers.Server;

	public void CloseConnection(GameServiceUser user) => _peers.Close(user);

	public bool SendMessage(GameServiceUser user, ReadOnlySpan<byte> data, byte channel = 0, MessageReliability reliability = MessageReliability.Reliable)
		=> _peers.Send(user, data, channel, reliability);

	public bool TryReadNextMessage(byte channel, out IPeerToPeerSession.Message message)
	{
		if (!TryDequeue(this, channel, out message)) return false;
		if (channel < Y51_SE2Online.Net.KeenLink.FirstChannel) JoinBytes.Add(message.GetContents().Length);
		return true;
	}

	void IPeerToPeerSession.DisposeMessage(nint messageHandle) => Free(messageHandle);

	protected override IPeerToPeerSession.Message Create(GameServiceUser from, nint buffer, int length, MessageReliability reliability, IPeerToPeerSession session)
		=> IPeerToPeerSession.CreateMessage(from, buffer, length, buffer, reliability, session);

	public void Dispose() => _peers.Dispose();
}

/// <summary>The connection a direct IP join opens early to read the host's mod list, kept for the join itself.</summary>
internal static class DirectIpSessions
{
	private static readonly Dictionary<IPEndPoint, DirectIpPeers> Pending = new();

	public static DirectIpPeers? Take(IPEndPoint endpoint)
	{
		lock (Pending) return Pending.Remove(endpoint, out var peers) ? peers : null;
	}

	/// <summary>The co-op host's mod list, or null when it does not answer within 10 s.</summary>
	public static async System.Threading.Tasks.Task<List<Sync.WorldMod>?> ModsAsync(IPEndPoint endpoint, System.Threading.CancellationToken stop)
	{
		DirectIpPeers peers;
		lock (Pending)
		{
			if (Pending.Remove(endpoint, out var old)) old.Dispose();
			peers = new DirectIpPeers(GameLog.Instance, null, endpoint, LocalPeers.Server);
			Pending[endpoint] = peers;
		}
		for (int i = 0; i < 200 && !stop.IsCancellationRequested; i++)
		{
			peers.Pump((_, _) => { }, (_, _, _, _) => { });
			if (peers.HostMods is { } text) return KeenMods.Decode(text);
			await System.Threading.Tasks.Task.Delay(50, stop);
		}
		return null;
	}
}
