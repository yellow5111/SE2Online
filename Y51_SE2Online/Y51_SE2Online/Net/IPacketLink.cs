using System;
using System.Collections.Generic;

namespace Y51_SE2Online.Net;

public interface IPacketLink : IDisposable
{
	string Name { get; }

	int MaxMessageSize { get; }

	bool AuthenticatesPeers { get; }

	void Pump(List<LinkEvent> events, List<LinkMessage> messages);

	LinkSendResult Send(uint conn, Channel channel, bool reliable, ReadOnlySpan<byte> frame);

	int GetPendingReliableBytes(uint conn);

	bool HasLanePriorities(uint conn)
	{
		return false;
	}

	/// <summary>
	/// Whether the connection underneath still holds this peer (its own keepalives, not our pings). Only a link that
	/// shares its reliable stream with other traffic answers true: our pings wait behind that traffic there.
	/// </summary>
	bool IsSessionAlive(uint conn)
	{
		return false;
	}

	void Close(uint conn, int code, string reason, bool linger);
}
