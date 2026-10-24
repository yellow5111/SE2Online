using System;
using System.Linq;
using Y51_SE2Online.Net;

namespace Y51_SE2Online.Sync;

/// <summary>Pings (design 10.1): the host checks the player's right, the rate and the label here, then the world checks the
/// range from the pinger's character and sends it to the players who may see it.</summary>
public sealed partial class HostSession
{
	private readonly PingLimiter _pingLimiter = new();

	private void HandlePing(RemoteClient c, byte[] data)
	{
		if (c.Status != ClientStatus.Synced || data.Length > 1024) return;
		var ping = SocialMessage.Decode<PingSend>(data);
		Ping(c.Player.ClientId, DisplayName(c.Player), PeerAlias.SteamOf(c.Peer.Value), ping);
	}

	/// <summary>A ping by the hosting player.</summary>
	public void LocalPing(PingSend ping) => Ping(SyncProtocol.HostClientId, Decorate(_o.HostName, _o.HostIdentity), _o.Dedicated ? 0 : _o.HostSteamId, ping);

	private void Ping(ushort from, string fromName, ulong steam, PingSend ping)
	{
		if (ping == null) return;
		if (steam != 0 && !Allowed(steam, "ping.use")) { GpsNotice(from, "Your group may not ping."); return; }
		var settings = _o.Pings;
		_pingLimiter.Burst = settings.Burst;
		_pingLimiter.RefillSeconds = settings.RefillSeconds;
		if (!_pingLimiter.Allow(steam != 0 ? steam : from, _clock())) { GpsNotice(from, "You are pinging too quickly."); return; }
		var cleaned = ping with
		{
			Label = PingRules.CleanLabel(ping.Label, settings.WordFilter),
			What = PingRules.CleanText(ping.What, PingRules.MaxWhat, settings.WordFilter)
		};
		string group = steam != 0 ? DisplayGroup(steam) : "";
		if (_world is IPingWorld grouped) grouped.OnPing(from, fromName, group, cleaned);
		else _world.OnPing(from, fromName, cleaned);
	}

	public PingSettings PingSettings => _o.Pings;

	/// <summary>Server thread: one ping to one player; the hosting player's goes straight to its own screen.</summary>
	public void SendPing(ushort clientId, PingShow show)
	{
		if (clientId == SyncProtocol.HostClientId)
		{
			if (!_o.Dedicated) PingInbox.Add(show);
			return;
		}
		PeerId? peer;
		lock (_sync) peer = _players.Values.FirstOrDefault(p => p.ClientId == clientId)?.Peer;
		if (peer.HasValue) _t.Send(peer.Value, Channel.Control, SocialMessage.Encode(SyncMsg.PingShow, show));
	}
}

/// <summary>A world that also takes the pinger's group (its style colours the marker).</summary>
public interface IPingWorld
{
	void OnPing(ushort fromClientId, string fromName, string group, PingSend ping);
}

/// <summary>Pings for this player's screen: from the host's own session or from the network. The marker provider drains it.</summary>
public static class PingInbox
{
	private static readonly System.Collections.Concurrent.ConcurrentQueue<PingShow> Queue = new();

	public static void Add(PingShow show)
	{
		if (show != null && Queue.Count < 256) Queue.Enqueue(show);
	}

	public static bool TryTake(out PingShow show) => Queue.TryDequeue(out show);

	public static void Clear() { while (Queue.TryDequeue(out _)) { } }
}

/// <summary>The host's ping settings, from the settings file (dedicated server.json Network.* or the co-op Host page).</summary>
public sealed record PingSettings(PingAudience Audience, double RangeMeters, int LifetimeS, int Burst, double RefillSeconds, double MaxRangeMeters, string[] WordFilter)
{
	public static readonly PingSettings Default = new(PingAudience.Faction, 5000, 60, 3, 5, 2000, Array.Empty<string>());
}
