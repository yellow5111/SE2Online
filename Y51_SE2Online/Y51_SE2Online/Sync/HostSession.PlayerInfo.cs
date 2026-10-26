using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Y51_SE2Online.Net;

namespace Y51_SE2Online.Sync;

public sealed partial class HostSession
{
	/// <summary>Player info's account part; <paramref name="ClientId"/> is 0 while the player is offline.</summary>
	public sealed record PlayerAccount(ulong SteamId, string Name, long Identity, ushort ClientId, bool Online, DetailSection[] Sections);

	/// <summary>What the session knows about an account (<paramref name="id"/> a SteamID or one of its game instances):
	/// online or seen before, its connection, group, rights and bans. Any thread; built only when an admin asks.</summary>
	public PlayerAccount AccountInfo(ulong id)
	{
		ulong steam = PeerAlias.SteamOf(id);
		long nowUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), now = _clock();
		PlayerRecord record;
		RemoteClient client = null;
		int instances;
		lock (_sync)
		{
			var mine = _players.Values.Where(p => PeerAlias.SteamOf(p.Peer?.Value ?? SteamIdOfKey(p.Key)) == steam).ToList();
			instances = mine.Count(p => p.Peer.HasValue);
			record = mine.FirstOrDefault(p => p.Peer?.Value == id) ?? mine.FirstOrDefault(p => p.Peer.HasValue) ?? mine.FirstOrDefault();
			if (record?.Peer is PeerId peer) _clients.TryGetValue(peer, out client);
		}
		bool host = !_o.Dedicated && steam != 0 && steam == _o.HostSteamId;
		var seen = _o.Access.SeenCopy().FirstOrDefault(s => s.SteamId == steam);
		bool online = host || record?.Peer != null;
		long identity = host ? _o.HostIdentity : record is { IdentityId: not 0 } r ? r.IdentityId : seen?.Identity ?? 0;
		string name = host ? _o.HostName : record?.Name ?? seen?.Name ?? Perms.NameOf(steam) ?? steam.ToString(CultureInfo.InvariantCulture);
		ushort clientId = host ? SyncProtocol.HostClientId : online ? record.ClientId : (ushort)0;

		var player = new List<DetailLine> { new("Name", name) };
		string shown = Decorate(name, identity);
		if (shown != name) player.Add(new("Shown as", shown));
		player.Add(new("SteamID", steam.ToString(CultureInfo.InvariantCulture), true));
		player.Add(new("Steam profile", "https://steamcommunity.com/profiles/" + steam.ToString(CultureInfo.InvariantCulture), true));
		player.Add(identity != 0 ? new("Identity", identity.ToString(CultureInfo.InvariantCulture), true) : new("Identity", "none yet (no character in this world)"));
		player.Add(new("Status", host ? "online (hosting this session)" : online ? "online" + (instances > 1 ? $", {instances} game instances" : "") : "offline"));
		if (!online) player.Add(new("Last seen", seen == null ? "never on this server" : PlayerDetails.When(seen.LastSeenUnixMs, nowUtc)));
		if (client != null)
		{
			player.Add(new("Connected", PlayerDetails.When(nowUtc - (now - client.JoinedMs), nowUtc)));
			if (client.SyncedMs > 0) player.Add(new("In the world for", PlayerDetails.Span(now - client.SyncedMs)));
		}
		if (record is { Joins: > 0 }) player.Add(new("Joins since the server started", record.Joins.ToString(CultureInfo.InvariantCulture)));
		// The transport's stats belong to the net thread; this read may race it, so a failure only costs these lines.
		PeerStats st = null;
		try { if (client != null && _t.TryGetStats(client.Peer, out var stats)) st = stats.Clone(); } catch (Exception) { st = null; }
		if (st != null)
		{
			string rtt = st.LastRttMs < 0 ? "not measured yet" : $"{st.LastRttMs:0} ms (average {st.AvgRttMs:0}, best {(st.MinRttMs == double.MaxValue ? 0 : st.MinRttMs):0}, worst {st.MaxRttMs:0})";
			player.Add(new("Ping", rtt));
			if (st.PingsSent > 0) player.Add(new("Pings unanswered", PlayerDetails.Percent(Math.Max(0, st.PingsSent - st.PongsReceived), st.PingsSent) + $" of {st.PingsSent:N0}"));
			player.Add(new("Link backlog", $"{st.LinkPendingBytes + st.BulkQueuedBytes:N0} bytes waiting, {st.SeqGaps:N0} gaps, {st.ProtocolErrors:N0} protocol errors"));
			player.Add(new("Game", $"SE2 Online {(!string.IsNullOrEmpty(st.RemotePluginVersion) ? st.RemotePluginVersion : _o.PluginVersion)}, Space Engineers 2 {(!string.IsNullOrEmpty(st.RemoteGameVersion) ? st.RemoteGameVersion : _o.GameVersion)} on Steam (PC)"));
		}
		else if (host) player.Add(new("Game", $"SE2 Online {_o.PluginVersion}, Space Engineers 2 {_o.GameVersion} on Steam (PC)"));

		bool owner = IsOwner(steam) || host;
		string group = owner ? DisplayGroup(steam) : Perms.GroupOf(steam);
		var rights = new List<DetailLine>
		{
			new("Group", group + (owner ? " (owner: everything)" : "")),
			new("Rank", (owner ? PermissionSet.OwnerRank : Perms.RankOf(steam)).ToString(CultureInfo.InvariantCulture)),
		};
		if (Perms.Styles().TryGetValue(group, out var style) && (style.NamePrefix + style.NameSuffix + style.ChatPrefix).Trim().Length > 0)
			rights.Add(new("Name tag", $"prefix '{style.NamePrefix.Trim()}', suffix '{style.NameSuffix.Trim()}', chat '{style.ChatPrefix.Trim()}'"));
		if (Perms.HasOverrides(steam)) rights.Add(new("Own entries", "yes (Player.json sets keys for this player)"));
		if (!owner)
		{
			var granted = Perms.Grants(steam).ToHashSet(StringComparer.Ordinal);
			var extra = PermissionCatalog.All.Where(p => !p.IsValue && !p.Default && granted.Contains(p.Key)).Select(p => p.Key).ToList();
			var removed = PermissionCatalog.All.Where(p => !p.IsValue && p.Default && !granted.Contains(p.Key)).Select(p => p.Key).ToList();
			rights.Add(new("Admin panel", granted.Contains("admin.panel") ? "yes" : "no"));
			rights.Add(new("Beyond a player", extra.Count == 0 ? "nothing" : $"{extra.Count}: " + string.Join(", ", extra)));
			if (removed.Count > 0) rights.Add(new("Taken away", $"{removed.Count}: " + string.Join(", ", removed)));
			rights.Add(new("PCU limit (group)", Perms.Value(steam, "limits.pcu") is double pcu && pcu >= 0 ? pcu.ToString("N0", CultureInfo.InvariantCulture) : "the server's"));
			rights.Add(new("Biggest paste", Perms.Value(steam, "blueprint.max_blocks") is double blocks && blocks >= 0 ? blocks.ToString("N0", CultureInfo.InvariantCulture) + " blocks" : "no limit"));
		}

		var ban = _o.Access.ActiveBan(steam, nowUtc);
		var moderation = new List<DetailLine>
		{
			new("Ban", ban == null ? "not banned" : (ban.ExpiresUnixMs is long end ? AccessList.Remaining(end - nowUtc) + " left" : "permanent")
				+ $", by {(string.IsNullOrWhiteSpace(ban.By) ? "?" : ban.By)}, {PlayerDetails.When(ban.CreatedUnixMs, nowUtc)}, reason: {(string.IsNullOrWhiteSpace(ban.Reason) ? "none given" : ban.Reason)}"),
			new("Whitelist", (_o.Access.IsWhitelisted(steam) ? "listed" : "not listed") + (_o.Access.WhitelistOnly ? " (the server admits whitelisted players only)" : " (the whitelist is off)")),
			new("Kicks and earlier bans", "not kept per player; the server's audit log (audit.jsonl) lists every one"),
		};
		return new PlayerAccount(steam, name, identity, clientId, online,
			[new DetailSection("PLAYER", player.ToArray()), new DetailSection("GROUP AND RIGHTS", rights.ToArray()), new DetailSection("MODERATION", moderation.ToArray())]);
	}
}
