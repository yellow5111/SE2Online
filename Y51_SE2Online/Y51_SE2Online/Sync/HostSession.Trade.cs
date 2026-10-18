using System;
using System.Linq;
using Y51_SE2Online.Net;

namespace Y51_SE2Online.Sync;

public sealed partial class HostSession
{
	private void HandleTrade(RemoteClient c, byte[] data)
	{
		if (c.Status != ClientStatus.Synced || c.Player == null || data.Length > 512) return;
		_world.OnTrade(c.Player.ClientId, data);
	}

	/// <summary>A trade request or action from the hosting player.</summary>
	public void LocalTrade(byte[] data)
	{
		if (_o.HostCountsAsPlayer && data != null && data.Length <= 512) _world.OnTrade(SyncProtocol.HostClientId, data);
	}

	/// <summary>The player's game shows a trade partner's inventory and sends item requests; the hosting player's always does.</summary>
	public bool TakesTradeView(ushort clientId)
	{
		if (clientId == SyncProtocol.HostClientId) return true;
		lock (_sync)
		{
			var peer = _players.Values.FirstOrDefault(p => p.ClientId == clientId)?.Peer;
			return peer.HasValue && _clients.TryGetValue(peer.Value, out var client) && client.TradeView;
		}
	}

	public void SendTrade<T>(ushort clientId, SyncMsg kind, T message)
	{
		byte[] data = SocialMessage.Encode(kind, message);
		if (clientId == SyncProtocol.HostClientId)
		{
			TradeInbox.Raise(kind, data);
			return;
		}
		PeerId? peer;
		lock (_sync) peer = _players.Values.FirstOrDefault(p => p.ClientId == clientId)?.Peer;
		if (peer.HasValue) _t.Send(peer.Value, Channel.Control, data);
	}
}
