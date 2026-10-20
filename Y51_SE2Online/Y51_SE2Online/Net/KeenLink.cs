using System;
using System.Collections.Generic;
using Keen.VRage.Core.Services.Data;
using Keen.VRage.Core.Services.Networking;
using Y51_SE2Online.Core;
using Y51_SE2Online.KeenNetcode;

namespace Y51_SE2Online.Net;

/// <summary>
/// SE2 Online's protocol on Keen's own game connection (SE2 4.0): the same Steam networking session the game
/// replicates over, on channels of its own. Keen uses channel 0 (handshake) and 1 (replication).
/// </summary>
public sealed class KeenLink : IPacketLink
{
	public const byte FirstChannel = 120;

	private readonly bool _host;

	private readonly IMpLog _log;

	private readonly Dictionary<GameServiceUser, uint> _connOf = new();

	private readonly Dictionary<uint, GameServiceUser> _userOf = new();

	private readonly HashSet<uint> _closed = new();

	private uint _nextConn = 1;

	private IPeerToPeerSession? _comms;

	public string Name => _host ? "keen-host" : "keen-client";

	// Steam's reliable message limit; MpTransport fragments above it.
	public int MaxMessageSize => 524288;

	public bool AuthenticatesPeers => true;

	public KeenLink(bool host, IMpLog log)
	{
		_host = host;
		_log = log;
	}

	public void Pump(List<LinkEvent> events, List<LinkMessage> messages)
	{
		IPeerToPeerSession? comms = _host ? KeenNet.ServerComms : KeenNet.ClientComms;
		if (!ReferenceEquals(comms, _comms))
		{
			// A new world (or none) replaced the game's session: every connection on the old one is gone.
			foreach (uint conn in _userOf.Keys)
			{
				if (_closed.Add(conn)) events.Add(new LinkEvent(LinkEventKind.Disconnected, conn, SteamIdOf(_userOf[conn]), DisconnectCodes.Shutdown, "the game's network session ended", byPeer: true));
			}
			_connOf.Clear();
			_userOf.Clear();
			_comms = comms;
		}
		if (comms == null) return;
		if (!_host && KeenNet.ClientServer is { } server && server != GameServiceUser.Invalid && !_connOf.ContainsKey(server))
		{
			Open(server, events);
		}
		while (KeenNet.TryTakeDisconnect(_host, out GameServiceUser gone, out string why))
		{
			if (_connOf.TryGetValue(gone, out uint conn) && _closed.Add(conn))
			{
				events.Add(new LinkEvent(LinkEventKind.Disconnected, conn, SteamIdOf(gone), DisconnectCodes.Shutdown, why, byPeer: true));
				_connOf.Remove(gone);
			}
		}
		for (byte lane = 0; lane < 3; lane++)
		{
			while (comms.TryReadNextMessage((byte)(FirstChannel + lane), out IPeerToPeerSession.Message message))
			{
				using (message)
				{
					GameServiceUser from = message.RemotePeer;
					if (!_connOf.TryGetValue(from, out uint conn))
					{
						// The host learns of a player from its Hello; a client only talks to its server. Anything else from
						// an unknown player is a leftover of a connection that just closed (frames still in flight when
						// it left): opening a connection for it looped through "frames before Hello" and timeouts.
						if (!_host || !Frame.TryRead(message.GetContents(), out FrameHeader header, out _, out _) || header.Kind != FrameKind.Hello) continue;
						conn = Open(from, events);
					}
					messages.Add(new LinkMessage(conn, message.GetContents().ToArray()));
				}
			}
		}
	}

	private uint Open(GameServiceUser user, List<LinkEvent> events)
	{
		uint conn = _nextConn++;
		_connOf[user] = conn;
		_userOf[conn] = user;
		events.Add(new LinkEvent(LinkEventKind.Connected, conn, SteamIdOf(user)));
		return conn;
	}

	private ulong SteamIdOf(GameServiceUser user) => KeenNet.SteamIdOf(user) ?? (ulong)user.Id.GetHashCode();

	public LinkSendResult Send(uint conn, Channel channel, bool reliable, ReadOnlySpan<byte> frame)
	{
		IPeerToPeerSession? comms = _comms;
		if (comms == null || _closed.Contains(conn) || !_userOf.TryGetValue(conn, out GameServiceUser user)) return LinkSendResult.Failed;
		if (frame.Length > MaxMessageSize) return LinkSendResult.Failed;
		bool ok = comms.SendMessage(user, frame, (byte)(FirstChannel + (byte)channel), reliable ? MessageReliability.Reliable : MessageReliability.Unreliable);
		return ok ? LinkSendResult.Ok : LinkSendResult.Busy;
	}

	public int GetPendingReliableBytes(uint conn)
	{
		if (!_userOf.TryGetValue(conn, out GameServiceUser user)) return int.MaxValue;
		return KeenNet.PendingReliableBytes(_host, user);
	}

	public void Close(uint conn, int code, string reason, bool linger)
	{
		if (!_userOf.TryGetValue(conn, out GameServiceUser user) || !_closed.Add(conn)) return;
		_connOf.Remove(user);
		_log.Info($"keen link: closing {user} ({code}: {reason})");
		// Kicks, bans and refused joins end the game connection too; the host's Keen layer closes it after its reject.
		if (_host) KeenNet.Kick(user, reason);
	}

	public void Dispose()
	{
		_connOf.Clear();
		_userOf.Clear();
		_comms = null;
	}
}
