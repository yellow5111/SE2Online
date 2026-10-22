using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Y51_SE2Online.Core;
using Y51_SE2Online.Net;

namespace Y51_SE2Online.Sync;

/// <summary>
/// Custom rules on join: a world with custom definition values sends them to each player first, and the player only gets
/// the world (the game's own handshake) once it answers that they are applied. A player whose game cannot apply them is
/// refused with the reason instead of joining out of step with the server.
/// </summary>
public sealed partial class HostSession
{
	// Per connection: the rules hash offered and not yet answered.
	private readonly ConditionalWeakTable<RemoteClient, string> _rulesPending = new();

	[System.ThreadStatic] private static bool _rulesAnswered;

	/// <summary>True while the player still has to apply the host's rules (the offer is sent once per connection).</summary>
	private bool OfferRules(RemoteClient c)
	{
		var offer = HostRules.Offer;
		if (_rulesAnswered || offer == null) return false;
		lock (_sync)
		{
			if (_rulesPending.TryGetValue(c, out string pending) && pending == offer.Hash) return true;
			_rulesPending.AddOrUpdate(c, offer.Hash);
		}
		_log.Info($"sync(host): {c.Peer} '{c.Name}' gets this world's custom rules {offer.Hash} ({offer.Count} definition value(s), {MpLogExtensions.Bytes(offer.Message.Length)}) before the world");
		_t.Send(c.Peer, Channel.Bulk, offer.Message);
		return true;
	}

	/// <summary>The host changed its rules while players are in: everyone in the world applies the new values now.</summary>
	public void BroadcastRules(RulesOffer offer)
	{
		var peers = new List<PeerId>();
		lock (_sync)
			foreach (var client in _clients.Values)
				if (client.Status == ClientStatus.Synced) peers.Add(client.Peer);
		foreach (var peer in peers) _t.Send(peer, Channel.Bulk, offer.Message);
		if (peers.Count > 0) _log.Info($"sync(host): sent the changed custom rules {offer.Hash} ({offer.Count} definition value(s)) to {peers.Count} player(s) in the world");
	}

	private void OnRulesAck(PeerId peer, byte[] data)
	{
		var (hash, error) = RulesWire.DecodeAck(data);
		RemoteClient c;
		bool joining;
		lock (_sync)
		{
			if (!_clients.TryGetValue(peer, out c)) return;
			joining = _rulesPending.TryGetValue(c, out _);
			if (joining) _rulesPending.Remove(c);
		}
		if (!joining)
		{
			// The answer to rules the host changed while this player was in the world.
			if (error.Length == 0) { _log.Info($"sync(host): {peer} '{c.Name}' applied the changed custom rules {hash}"); return; }
			_log.Warn($"sync(host): {peer} '{c.Name}' could not apply the changed custom rules ({error}); disconnecting so the player rejoins in step");
			_t.Disconnect(peer, 1000, "The host changed the custom rules and your game could not apply them: " + error);
			return;
		}
		if (error.Length > 0)
		{
			Reject(peer, "Your game could not apply this server's custom rules, so joining would put you out of step with the server: " + error);
			return;
		}
		if (HostRules.Offer is { } offer && offer.Hash != hash)
		{
			_log.Info($"sync(host): {peer} applied custom rules {hash}, but the world now runs {offer.Hash}; sending those");
			RequestCapture(c);
			return;
		}
		_log.Info($"sync(host): {peer} '{c.Name}' applied the custom rules {hash}");
		_rulesAnswered = true;
		try { RequestCapture(c); }
		finally { _rulesAnswered = false; }
	}
}
