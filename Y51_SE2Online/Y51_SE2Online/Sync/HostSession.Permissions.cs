using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Y51_SE2Online.Net;

namespace Y51_SE2Online.Sync;

public sealed partial class HostSession
{
	/// <summary>The permission set in force; replaced whole by perms reload.</summary>
	public PermissionSet Perms => _o.Permissions;

	/// <summary>Set on the game thread while a command from RCON or chat runs for a signed-in player (0: the console or the
	/// hosting player, an owner): admin actions are then checked against that player's permissions and rank.</summary>
	[ThreadStatic] public static ulong LocalActorId;

	/// <summary>The ClientId of the player a chat command runs for (their character is the one teleported, given items...).</summary>
	[ThreadStatic] public static ushort LocalActorClient;

	/// <summary>Set while a console, RCON or chat command runs on the game thread: world actions answer at once, to the command.</summary>
	[ThreadStatic] public static bool InCommand;

	private volatile Dictionary<long, ulong> _identitySteam = new();
	private long _identityVersion = -1, _identityBuiltMs;

	private void InitPermissions()
	{
		_o.Permissions ??= PermissionSet.FromAccess(_o.Access);
		_o.Permissions.SetOwners((_o.OwnerSteamIds ?? Array.Empty<ulong>()).Append(_o.Dedicated ? 0 : _o.HostSteamId));
		_o.Permissions.AdminsChanged = admins => _o.Access.MirrorAdmins(admins);
	}

	/// <summary>Owners always pass: the co-op hosting player and server.json OwnerSteamIds (the console and RCON password by call site).</summary>
	public bool IsOwner(ulong steamId) => Perms.IsOwner(steamId);

	public bool Allowed(ulong steamId, string key) => Perms.Has(steamId, key);

	/// <summary>The player's SteamID behind a ClientId (the hosting player's for HostClientId), 0 if unknown.</summary>
	public ulong SteamOfClient(ushort clientId)
	{
		if (clientId == SyncProtocol.HostClientId) return _o.Dedicated ? 0 : _o.HostSteamId;
		lock (_sync)
			foreach (var p in _players.Values)
				if (p.ClientId == clientId) return PeerAlias.SteamOf(p.Peer?.Value ?? SteamIdOfKey(p.Key));
		return 0;
	}

	/// <summary>Whether the player behind a ClientId may do <paramref name="key"/>; the co-op host's own game always may.</summary>
	public bool AllowedClient(ushort clientId, string key)
	{
		if (clientId == SyncProtocol.HostClientId) return !_o.Dedicated || _o.HostCountsAsPlayer;
		ulong steam = SteamOfClient(clientId);
		return steam != 0 && Allowed(steam, key);
	}

	/// <summary>The SteamID of a player identity in this world (the co-op host's too), 0 for identities that are not players
	/// of this session (NPCs, players not seen since the server started).</summary>
	public ulong SteamOfIdentity(long identity)
	{
		if (identity == 0) return 0;
		if (!_o.Dedicated && identity == _o.HostIdentity) return _o.HostSteamId;
		long version = System.Threading.Interlocked.Read(ref PlayerTableVersion), now = _clock();
		var map = _identitySteam;
		if (version != _identityVersion || now - _identityBuiltMs > 2000)
		{
			var next = new Dictionary<long, ulong>();
			lock (_sync)
				foreach (var p in _players.Values)
					if (p.IdentityId != 0) next[p.IdentityId] = PeerAlias.SteamOf(p.Peer?.Value ?? SteamIdOfKey(p.Key));
			_identitySteam = map = next;
			_identityVersion = version; _identityBuiltMs = now;
		}
		return map.TryGetValue(identity, out ulong steam) ? steam : 0;
	}

	/// <summary>The group whose style a player shows: an owner shows group "owner" if there is one, else "admin".</summary>
	public string DisplayGroup(ulong steamId)
	{
		if (!IsOwner(steamId)) return Perms.GroupOf(steamId);
		return Perms.Group("owner")?.Name ?? Perms.Group(PermissionSet.AdminGroup)?.Name ?? Perms.GroupOf(steamId);
	}

	/// <summary>A SteamID64, or the name of a player online or seen before (exact, then a unique prefix); 0 if none.</summary>
	public ulong FindPlayer(string text)
	{
		text = (text ?? "").Trim();
		if (text.Length == 0) return 0;
		if (ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out ulong id) && id != 0) return id;
		var names = new List<(ulong Id, string Name)>();
		lock (_sync) names.AddRange(_players.Values.Select(p => (PeerAlias.SteamOf(p.Peer?.Value ?? SteamIdOfKey(p.Key)), p.Name)));
		if (!_o.Dedicated) names.Add((_o.HostSteamId, _o.HostName));
		names.AddRange(_o.Access.SeenCopy().Select(s => (s.SteamId, s.Name)));
		names.AddRange(Perms.Assignments().Select(a => (a.SteamId, a.Name)));
		var hits = names.Where(n => n.Id != 0 && string.Equals(n.Name, text, StringComparison.OrdinalIgnoreCase)).Select(n => n.Id).Distinct().ToList();
		if (hits.Count == 1) return hits[0];
		if (hits.Count > 1) return 0;
		hits = names.Where(n => n.Id != 0 && !string.IsNullOrEmpty(n.Name) && n.Name.StartsWith(text, StringComparison.OrdinalIgnoreCase)).Select(n => n.Id).Distinct().ToList();
		return hits.Count == 1 ? hits[0] : 0;
	}

	public string NameOrId(ulong steamId) => KnownName(steamId) ?? Perms.NameOf(steamId) ?? _o.Access.SeenCopy().FirstOrDefault(s => s.SteamId == steamId)?.Name ?? steamId.ToString(CultureInfo.InvariantCulture);

	/// <summary>The cleanup's state for the console and the panel.</summary>
	public string CleanupStatus() => _o.CleanupStatus?.Invoke() ?? "The cleanup runs with the world (not loaded).";

	/// <summary>Re-reads the permission files; a set that fails to load keeps the old one in force.</summary>
	public string ReloadPermissions()
	{
		if (_o.PermissionsFolder == null) return "This host has no permission files (they could not be created); nothing to reload.";
		try
		{
			var set = PermissionSet.Load(_o.PermissionsFolder, _o.PlayersFile, out var warnings);
			set.SetOwners((_o.OwnerSteamIds ?? Array.Empty<ulong>()).Append(_o.Dedicated ? 0 : _o.HostSteamId));
			set.AdminsChanged = admins => _o.Access.MirrorAdmins(admins);
			set.ContinueFrom(_o.Permissions);
			foreach (string w in warnings) _log.Warn("permissions: " + w);
			_o.Permissions = set;
			_o.Access.MirrorAdmins(set.AdminMembers());
			_nextAdminPushMs = 0;
			return $"Permissions reloaded: {set.Groups.Count} groups, {set.Assignments().Length} assigned players" + (warnings.Count > 0 ? $", {warnings.Count} warnings (see the server log)." : ".");
		}
		catch (Exception e) when (e is System.IO.InvalidDataException or System.IO.IOException or UnauthorizedAccessException)
		{
			_log.Error("permissions: reload failed, the previous permissions stay in force: " + e.Message);
			return "Reload failed, the previous permissions stay in force: " + e.Message;
		}
	}

	/// <summary>Effective permissions of a player, for perms check: one key explained, or every granted key.</summary>
	private string PermsCheck(ulong target, string key)
	{
		string who = NameOrId(target);
		if (!string.IsNullOrWhiteSpace(key)) return $"{who} ({DisplayGroup(target)}): {key.Trim().ToLowerInvariant()} = {Perms.Explain(target, key.Trim().ToLowerInvariant())}";
		var grants = Perms.Grants(target);
		return $"{who}: group {Perms.GroupOf(target)}, rank {Perms.RankOf(target)}{(IsOwner(target) ? ", owner (everything)" : "")}. Allowed: " + (grants.Contains("*") ? "everything" : string.Join(", ", grants));
	}

	/// <summary>The players this actor sees in the group assignment search (only with admin.players.set_group).</summary>
	private KnownPlayer[] KnownFor(ulong actor, bool owner)
	{
		if (!owner && !Allowed(actor, "admin.players.set_group")) return Array.Empty<KnownPlayer>();
		var seen = _o.Access.SeenCopy().ToDictionary(s => s.SteamId);
		var all = new Dictionary<ulong, KnownPlayer>();
		foreach (var s in seen.Values) all[s.SteamId] = new KnownPlayer(s.SteamId, s.Name, Perms.GroupOf(s.SteamId), s.LastSeenUnixMs);
		foreach (var a in Perms.Assignments())
			if (!all.ContainsKey(a.SteamId)) all[a.SteamId] = new KnownPlayer(a.SteamId, a.Name, a.Group, 0);
		return all.Values.OrderByDescending(k => k.LastSeenUnixMs).ThenBy(k => k.Name).Take(500).ToArray();
	}

	private GroupInfo[] GroupsFor(ulong actor, bool owner)
	{
		if (!owner && !Allowed(actor, "admin.players.set_group")) return Array.Empty<GroupInfo>();
		int mine = owner ? PermissionSet.OwnerRank : Perms.RankOf(actor);
		return Perms.Groups.Where(g => owner || (g.Rank < mine && Allowed(actor, PermissionSet.ScopeKey(g.Name)))).OrderBy(g => g.Rank).ThenBy(g => g.Name).Select(g => new GroupInfo(g.Name, g.Rank)).ToArray();
	}

	private long _seenTickMs;

	// Last-seen times for the cleanup's inactivity clock and the known players list: every minute while online.
	private void TickSeen(long now)
	{
		if (now - _seenTickMs < 60_000) return;
		_seenTickMs = now;
		MarkOnlineSeen(false);
	}

	private void MarkOnlineSeen(bool force)
	{
		var online = new List<(ulong, string, long)>();
		if (!_o.Dedicated && _o.HostSteamId != 0) online.Add((_o.HostSteamId, _o.HostName, _o.HostIdentity));
		lock (_sync)
			foreach (var p in _players.Values)
				if (p.Peer.HasValue) online.Add((PeerAlias.SteamOf(p.Peer.Value.Value), p.Name, p.IdentityId));
		_o.Access.MarkSeen(online, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), force);
	}

	/// <summary>A joining player's name with role tags and look-alike characters taken out (<see cref="NameGuard"/>).</summary>
	private string GuardName(string name, PeerId peer)
	{
		var affixes = Perms.Styles().Values.SelectMany(s => new[] { s.NamePrefix, s.NameSuffix, s.ChatPrefix, s.ChatSuffix }).Where(a => !string.IsNullOrWhiteSpace(a));
		string clean = NameGuard.Clean(name, PeerAlias.SteamOf(peer.Value), affixes);
		if (clean != (name ?? "").Trim()) _log.Info($"sync(host): player name '{SocialState.Clean(name, 64)}' shown as '{clean}' (role tags and hidden characters are not part of names)");
		return clean;
	}

	/// <summary>A player left or got an identity: their last-seen time is written at once.</summary>
	public void NoteSeen(ulong steamId, string name, long identity)
		=> _o.Access.MarkSeen(new[] { (steamId, name, identity) }, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), true);
}
