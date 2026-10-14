using System.Collections.Generic;
using System.Linq;
using Y51_SE2Online.Net;

namespace Y51_SE2Online.Sync;

public sealed partial class HostSession
{
	public bool WaterSync => _o.Settings.WaterSync;

	/// <summary>Joined players that are in the world, for the water stream.</summary>
	public List<(PeerId Peer, ushort ClientId)> WaterPeers()
	{
		lock (_sync) return _clients.Values.Where(c => c.Status == ClientStatus.Synced && c.Player != null).Select(c => (c.Peer, c.Player.ClientId)).ToList();
	}

	public void SendWater(PeerId peer, byte[] data) => _t.Send(peer, data.Length > 8192 ? Channel.Bulk : Channel.Control, data);
}
