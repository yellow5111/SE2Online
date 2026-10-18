using System.Collections.Generic;
using Y51_SE2Online.Net;

namespace Y51_SE2Online.Sync;

public sealed partial class HostSession
{
	private readonly GhostRelay _ghosts = new();

	private void HandleBuildGhost(RemoteClient c, byte[] data)
	{
		if (c.Status != ClientStatus.Synced || c.Player == null || !BuildGhostMessage.TryDecodeUp(data, out BuildGhost ghost)) return;
		RelayGhost(c.Player.ClientId, ghost);
	}

	/// <summary>The hosting player's own build preview.</summary>
	public void LocalGhost(BuildGhost ghost)
	{
		if (_o.HostCountsAsPlayer) RelayGhost(SyncProtocol.HostClientId, ghost);
	}

	// Positions come from the voice snapshot (every player's character, refreshed each server frame).
	private void RelayGhost(ushort builder, BuildGhost ghost)
	{
		if (!BuildGhostMessage.HostEnabled() || !_ghosts.Allow(builder, _clock()) || !_world.TryVoicePosition(builder, out Vec3D at)) return;
		if (ghost.Active && !GhostRelay.Plausible(at, ghost)) return;
		ghost = GhostRelay.Vetted(ghost, BuildGhostMessage.IsBlockType);
		var viewers = new List<(PeerId Peer, ushort ClientId)>();
		lock (_sync)
		{
			foreach (RemoteClient c in _clients.Values)
				if (c.BuildGhosts && c.Status == ClientStatus.Synced && c.Player != null && c.Player.ClientId != builder) viewers.Add((c.Peer, c.Player.ClientId));
		}
		byte[] down = null;
		foreach (var (peer, clientId) in viewers)
		{
			// A clear goes to everyone so a viewer who just walked out of range drops it now, not at expiry.
			if (ghost.Active && (!_world.TryVoicePosition(clientId, out Vec3D viewer) || !GhostRelay.InRange(at, viewer))) continue;
			down ??= BuildGhostMessage.EncodeDown(builder, ghost);
			_t.Send(peer, Channel.Control, down);
		}
		if (builder != SyncProtocol.HostClientId && _o.HostCountsAsPlayer
			&& (!ghost.Active || _world.TryVoicePosition(SyncProtocol.HostClientId, out Vec3D host) && GhostRelay.InRange(at, host)))
			BuildGhostMessage.Received?.Invoke(builder, ghost);
	}
}
