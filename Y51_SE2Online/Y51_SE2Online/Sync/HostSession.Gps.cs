using System;
using System.Linq;
using Y51_SE2Online.Net;

namespace Y51_SE2Online.Sync;

public sealed partial class HostSession
{
	private void HandleGpsShare(RemoteClient c, byte[] data)
	{
		if (c.Status != ClientStatus.Synced || data.Length > 1024) return;
		if (!Allowed(PeerAlias.SteamOf(c.Peer.Value), "gps.share")) { Reply(c.Peer, "Your group may not share GPS markers."); return; }
		Share(c.Player.ClientId, DisplayName(c.Player), c.Peer.Value, SocialMessage.Decode<GpsShare>(data));
	}

	/// <summary>A marker shared by the hosting player.</summary>
	public void LocalGpsShare(GpsShare share) => Share(SyncProtocol.HostClientId, Decorate(_o.HostName, _o.HostIdentity), _o.HostSteamId, share);

	private void Share(ushort from, string fromName, ulong sender, GpsShare share)
	{
		// To yourself: "Keep" on a ping saves it as your own GPS marker.
		if (!_chatThrottle.Allow(sender, _clock()))
		{
			GpsNotice(from, "You are sharing markers too quickly.");
			return;
		}
		_world.OnGpsShare(from, fromName, share with { Name = SocialState.Clean(share.Name ?? "", 80) });
	}

	/// <summary>Tells a fighter's client to hold or release its own input; the hosting player is handled locally.</summary>
	public void SendFightLock(ushort clientId, bool locked)
	{
		if (clientId == SyncProtocol.HostClientId) { FightLockState.Set(locked); return; }
		PeerId? peer;
		lock (_sync) peer = _players.Values.FirstOrDefault(p => p.ClientId == clientId)?.Peer;
		if (peer.HasValue) _t.Send(peer.Value, Channel.Control, SocialMessage.Encode(SyncMsg.FightLock, new FightLockMessage(locked)));
	}

	/// <summary>Where the server has a player's own character: an ack every tick (unreliable) or a resync (reliable).</summary>
	public void SendOwnMotion(ushort clientId, OwnPose pose, bool reliable)
	{
		PeerId? peer;
		lock (_sync) peer = _players.Values.FirstOrDefault(p => p.ClientId == clientId)?.Peer;
		if (peer.HasValue) _t.Send(peer.Value, reliable ? Channel.Control : Channel.State, pose.Encode());
	}

	/// <summary>A system chat line for one player; the hosting player's goes straight into the local chat log.</summary>
	public void GpsNotice(ushort clientId, string text)
	{
		var line = new ChatLine("Server", 0, text, ChatKind.System, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
		if (clientId == SyncProtocol.HostClientId)
		{
			ChatLog.Add(line, _clock());
			return;
		}
		PeerId? peer;
		lock (_sync) peer = _players.Values.FirstOrDefault(p => p.ClientId == clientId)?.Peer;
		if (peer.HasValue) SendChatTo(peer.Value, line);
	}
}
