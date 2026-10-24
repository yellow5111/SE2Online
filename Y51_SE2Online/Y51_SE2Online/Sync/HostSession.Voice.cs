using System.Collections.Generic;
using Y51_SE2Online.Net;

namespace Y51_SE2Online.Sync;

public sealed partial class HostSession
{
	private readonly VoiceRelay _voice = new();
	private uint _hostVoiceSeq;

	public double VoiceRadiusMeters => _o.VoiceRadiusMeters;

	private void HandleVoice(RemoteClient c, byte[] data)
	{
		if (c.Status != ClientStatus.Synced || c.Player == null || !VoiceMessage.TryDecodeUp(data, out uint seq, out byte[] frame)) return;
		if (!Allowed(PeerAlias.SteamOf(c.Peer.Value), "voice.speak")) return;
		Relay(c.Player.ClientId, seq, frame);
	}

	/// <summary>A voice frame from the hosting player.</summary>
	public void LocalVoice(byte[] frame)
	{
		if (!_o.HostCountsAsPlayer || frame == null) return;
		Relay(SyncProtocol.HostClientId, ++_hostVoiceSeq, frame);
	}

	// Only to players within the radius, never back to the speaker; positions come from the world's snapshot, read
	// outside _sync like every other call into the world.
	private void Relay(ushort speaker, uint seq, byte[] frame)
	{
		double radius = _o.VoiceRadiusMeters;
		if (radius <= 0 || !_voice.Allow(speaker, frame.Length, _clock()) || !_world.TryVoicePosition(speaker, out Vec3D at)) return;
		var listeners = new List<(PeerId Peer, ushort ClientId)>();
		lock (_sync)
		{
			foreach (RemoteClient c in _clients.Values)
				if (c.Status == ClientStatus.Synced && c.Player != null && c.Player.ClientId != speaker) listeners.Add((c.Peer, c.Player.ClientId));
		}
		byte[] down = null;
		foreach (var (peer, clientId) in listeners)
		{
			if (!_world.TryVoicePosition(clientId, out Vec3D listener) || !VoiceRelay.InRange(at, listener, radius)) continue;
			down ??= VoiceMessage.EncodeDown(speaker, at, seq, frame);
			_t.Send(peer, Channel.State, down);
		}
		if (speaker != SyncProtocol.HostClientId && _o.HostCountsAsPlayer
			&& _world.TryVoicePosition(SyncProtocol.HostClientId, out Vec3D host) && VoiceRelay.InRange(at, host, radius))
			VoiceMessage.Received?.Invoke(speaker, at, frame);
	}
}
