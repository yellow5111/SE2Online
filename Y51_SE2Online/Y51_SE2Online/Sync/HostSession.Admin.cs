using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Y51_SE2Online.Net;

namespace Y51_SE2Online.Sync;

public sealed partial class HostSession
{
	private readonly ChatThrottle _chatThrottle = new();
	private readonly List<(PeerId Peer, long DueMs)> _removals = new();
	private const int PasswordTries = 5;
	private const long PasswordWindowMs = 60_000;
	private readonly Dictionary<ulong, (int Count, long SinceMs)> _passwordFails = new();
	private readonly Dictionary<PeerId, string> _adminMessages = new();
	private long _nextAdminPushMs;
	private readonly DocDiffCache _adminDiffs = new();
	private AccessPlayer[] _shownPings = Array.Empty<AccessPlayer>();
	private long _pingsDueMs;
	private const long PingRefreshMs = 5000;

	public AccessList Access => _o.Access;

	public bool Pvp => _o.Access.Pvp ?? _o.Pvp;

	public bool OfflineRaidProtection => _o.Access.OfflineRaid ?? _o.OfflineRaidProtection;

	/// <summary>Null keeps the world's own per-player PCU, 0 is unlimited.</summary>
	public int PcuInForce { get => _o.PcuInForce; set => _o.PcuInForce = value; }

	public bool? SunRotation => _o.Access.SunRotation ?? _o.SunRotation;

	public double? SunPeriodMinutes => _o.Access.SunPeriodMinutes ?? _o.SunPeriodMinutes;

	public (bool Rotation, double PeriodMinutes) SunInForce { get => _o.SunInForce; set => _o.SunInForce = value; }

	private double _sunNudgeDeg;

	/// <summary>Degrees the sun should move, collected for the world thread.</summary>
	public double TakeSunNudge() { lock (_sync) { double deg = _sunNudgeDeg; _sunNudgeDeg = 0; return deg; } }

	private double? _sunTargetDeg;
	private (bool Absolute, double Deg)? _sunGlide;

	/// <summary>Angle the sun should be set to, collected for the world thread.</summary>
	public double? TakeSunTarget() { lock (_sync) { var deg = _sunTargetDeg; _sunTargetDeg = null; return deg; } }

	/// <summary>A smooth sun move for the world thread: to an angle (Absolute) or by a number of degrees.</summary>
	public (bool Absolute, double Deg)? TakeSunGlide() { lock (_sync) { var glide = _sunGlide; _sunGlide = null; return glide; } }

	/// <summary>Current sun angle, written by the world thread.</summary>
	public double SunDegrees { get; set; }

	public int? PcuLimit => _o.Access.PcuLimit is int chosen ? (chosen < 0 ? null : chosen) : _o.PcuLimit;

	public OnlineRole RoleOf(ulong peerId) => peerId != 0 && _o.Access.IsAdmin(PeerAlias.SteamOf(peerId)) ? OnlineRole.Admin : OnlineRole.Player;

	public bool IsAdminClient(ushort clientId)
	{
		if (clientId == SyncProtocol.HostClientId) return true;
		lock (_sync)
		{
			var p = _players.Values.FirstOrDefault(x => x.ClientId == clientId);
			return p != null && RoleOf(p.Peer?.Value ?? SteamIdOfKey(p.Key)) == OnlineRole.Admin;
		}
	}

	public string DisplayName(PlayerRecord p) => Decorate(p.Name, p.IdentityId);

	private string Decorate(string name, long identity)
	{
		string tag = identity == 0 ? null : _o.FactionTagOf?.Invoke(identity);
		return FactionTag.Prefix(tag, name);
	}

	/// <summary>Returns a reject reason for a join that passed version checks, or null to admit it.</summary>
	private bool CheckAccess(PeerId peer, JoinRequest req)
	{
		ulong account = PeerAlias.SteamOf(peer.Value);
		long now = _clock();
		(byte Code, string Text)? verdict;
		lock (_passwordFails)
		{
			if (_passwordFails.TryGetValue(account, out var fails) && now - fails.SinceMs >= PasswordWindowMs)
			{
				_passwordFails.Remove(account);
				fails = default;
			}
			if (fails.Count >= PasswordTries)
			{
				verdict = (JoinRejectCode.Generic, $"Too many wrong passwords. Try again in {(PasswordWindowMs - (now - fails.SinceMs)) / 1000 + 1} s.");
			}
			else
			{
				verdict = _o.Access.CheckJoin(account, req.Password, _o.Password, _o.SessionName, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
				if (verdict?.Code == JoinRejectCode.Password && !string.IsNullOrEmpty(req.Password))
				{
					if (_passwordFails.Count >= 4096)
					{
						foreach (ulong stale in _passwordFails.Where(f => now - f.Value.SinceMs >= PasswordWindowMs).Select(f => f.Key).ToList()) _passwordFails.Remove(stale);
					}
					_passwordFails[account] = (fails.Count + 1, fails.Count == 0 ? now : fails.SinceMs);
				}
			}
		}
		if (verdict == null) return true;
		_log.Error($"sync(host): refusing {peer} '{req.PlayerName}': {(verdict.Value.Code == JoinRejectCode.Removed ? "banned" : verdict.Value.Text)}; disconnecting");
		_t.Send(peer, Channel.Control, TextMessage.Encode(SyncMsg.JoinReject, verdict.Value.Code, verdict.Value.Text));
		// Queued after the reject on the same reliable lane, and the link lingers, so the reject arrives first.
		_t.Disconnect(peer, DisconnectCodes.NotAllowed, verdict.Value.Code == JoinRejectCode.Removed ? "banned" : verdict.Value.Text);
		return false;
	}

	private void HandleChat(RemoteClient c, byte[] data)
	{
		if (c.Status != ClientStatus.Synced || data.Length > 2048) return;
		string text = SocialState.Clean(SocialMessage.Decode<ChatSend>(data).Text, ChatLog.MaxText);
		if (text.Length == 0) return;
		if (!_chatThrottle.Allow(c.Peer.Value, _clock()))
		{
			SendChatTo(c.Peer, new ChatLine("Server", 0, "You are sending messages too quickly.", ChatKind.System, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
			return;
		}
		var kind = RoleOf(c.Peer.Value) == OnlineRole.Admin ? ChatKind.Admin : ChatKind.Player;
		BroadcastChat(new ChatLine(DisplayName(c.Player), c.Peer.Value, text, kind, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
	}

	/// <summary>Chat from the hosting player (identity known through HostIdentity) or the dedicated console.</summary>
	public void LocalChat(string text, bool console)
	{
		text = SocialState.Clean(text, ChatLog.MaxText);
		if (text.Length == 0) return;
		string from = console ? "Server" : Decorate(_o.HostName, _o.HostIdentity);
		BroadcastChat(new ChatLine(from, console ? 0 : _o.HostSteamId, text, console ? ChatKind.System : ChatKind.Admin, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
	}

	private void BroadcastChat(ChatLine line)
	{
		_log.Info($"chat: {line.From}: {line.Text}");
		ChatLog.Add(line, _clock());
		byte[] data = SocialMessage.Encode(SyncMsg.ChatLine, line);
		lock (_sync)
			foreach (RemoteClient c in _clients.Values)
				if (c.Status == ClientStatus.Synced) _t.Send(c.Peer, Channel.Control, data);
	}

	private void Announce(string text) => BroadcastChat(new ChatLine("Server", 0, text, ChatKind.System, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

	private void SendChatTo(PeerId peer, ChatLine line) => _t.Send(peer, Channel.Control, SocialMessage.Encode(SyncMsg.ChatLine, line));

	private void HandleAdmin(RemoteClient c, byte[] data)
	{
		// Room for a 1000-character message of the day after JSON escaping.
		if (c.Status != ClientStatus.Synced || data.Length > 8192) return;
		var request = SocialMessage.Decode<AdminRequest>(data);
		string message;
		if (request.Action == AdminAction.Refresh) message = "";
		else if (RoleOf(c.Peer.Value) != OnlineRole.Admin) message = "You are not an admin on this server.";
		else message = Act(c.Peer.Value, c.Player.Name, OnlineRole.Admin, request, c.Peer, c.Player.ClientId);
		lock (_sync) _adminMessages[c.Peer] = message;
		_nextAdminPushMs = 0;
	}

	/// <summary>Admin action by the hosting player or the dedicated console (actorId 0).</summary>
	/// <summary>Set on the game thread while a remote console command runs: the ban list and chat name the RCON admin.</summary>
	[ThreadStatic] public static string LocalActor;

	public string LocalAdmin(AdminRequest request) => Act(0, LocalActor ?? (_o.Dedicated ? "Server" : _o.HostName), OnlineRole.Host, request, null, SyncProtocol.HostClientId);

	private string Act(ulong actorId, string actorName, OnlineRole actorRole, AdminRequest r, PeerId? actorPeer, ushort actorClientId)
	{
		long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		if (!Enum.IsDefined(r.Action)) return "Unknown action.";
		if (r.Action >= AdminAction.Creative && r.Action is not (AdminAction.Pvp or AdminAction.OfflineRaidProtection or AdminAction.PcuLimit or AdminAction.SunRotation or AdminAction.SunPeriod or AdminAction.SunNudge or AdminAction.SunSet or AdminAction.Motd or AdminAction.AccessBypass))
		{
			_world.OnAdminRequest(actorPeer, actorClientId, r);
			return "";
		}
		string name = NameOf(r.Target);
		ulong account = PeerAlias.SteamOf(r.Target);
		bool protectedTarget = r.Target != 0 && (account == _o.HostSteamId || (actorRole != OnlineRole.Host && RoleOf(r.Target) == OnlineRole.Admin && r.Target != actorId));
		string result;
		switch (r.Action)
		{
		case AdminAction.Kick:
		case AdminAction.Ban:
			if (protectedTarget) return "Only the host can remove another admin.";
			if (r.Target == actorId) return "You cannot remove yourself.";
			if (r.Action == AdminAction.Ban) result = _o.Access.Ban(account, name, r.Reason, r.Minutes, actorName, now);
			else if (!Remove(r.Target, new RemovalNotice(false, SocialState.Clean(r.Reason, 200), null, actorName, _o.SessionName))) return "That player is not connected.";
			else result = $"Kicked {name}.";
			if (r.Action == AdminAction.Ban) RemoveAccount(account, new RemovalNotice(true, SocialState.Clean(r.Reason, 200), r.Minutes == 0 ? null : now + r.Minutes * 60000L, actorName, _o.SessionName));
			string why = SocialState.Clean(r.Reason, 200);
			Announce((r.Action == AdminAction.Ban ? $"{name} was banned {(r.Minutes == 0 ? "permanently" : "for " + AccessList.Remaining(r.Minutes * 60000L))}" : $"{name} was kicked")
				+ $" by {actorName}." + (string.IsNullOrWhiteSpace(why) ? " No reason given." : " Reason: " + why));
			break;
		case AdminAction.Unban: result = _o.Access.Unban(account); break;
		case AdminAction.Promote:
		case AdminAction.Demote:
			if (actorRole != OnlineRole.Host && r.Action == AdminAction.Demote)
			{
				if (account == PeerAlias.SteamOf(actorId)) return "You cannot demote yourself.";
				if (account == _o.HostSteamId) return "You cannot demote the host.";
				if (_o.Access.IsAdmin(account)) return "Only the host can demote another admin.";
			}
			result = _o.Access.SetAdmin(account, name, r.Action == AdminAction.Promote);
			if (r.Action == AdminAction.Demote)
				foreach (var p in Players().Where(p => PeerAlias.SteamOf(p.Peer?.Value ?? SteamIdOfKey(p.Key)) == account)) AccessBypass.Set(p.IdentityId, false);
			break;
		case AdminAction.WhitelistAdd:
			if (KnownName(r.Target) == null)
			{
				if (!AccessList.IsPlayerSteamId(account)) return "That is not a Steam player's SteamID64.";
				name = AccessList.Clean(r.Name, 64);
			}
			result = _o.Access.SetWhitelisted(account, name, true); break;
		case AdminAction.WhitelistRemove: result = _o.Access.SetWhitelisted(account, name, false); break;
		case AdminAction.WhitelistOnly: result = _o.Access.SetWhitelistOnly(r.Value); break;
		case AdminAction.Pvp:
			result = _o.Access.SetPvp(r.Value);
			_o.PvpChanged?.Invoke(r.Value);
			BroadcastChat(new ChatLine("Server", 0, result, ChatKind.System, now));
			break;
		case AdminAction.PcuLimit:
			if (r.Size > 100_000_000) return "That PCU limit is too large.";
			result = _o.Access.SetPcuLimit((int)Math.Round(r.Size));
			BroadcastChat(new ChatLine("Server", 0, result, ChatKind.System, now));
			break;
		case AdminAction.SunRotation:
			result = _o.Access.SetSunRotation(r.Value);
			BroadcastChat(new ChatLine("Server", 0, result, ChatKind.System, now));
			break;
		case AdminAction.SunPeriod:
			if (!(r.Size >= 1 && r.Size <= 10080)) return "Day length must be between 1 minute and 7 days (10080 minutes).";
			result = _o.Access.SetSunPeriod(r.Size);
			BroadcastChat(new ChatLine("Server", 0, result, ChatKind.System, now));
			break;
		case AdminAction.SunNudge:
			if (!(Math.Abs(r.Size) <= 360)) return "Move the sun by at most 360 degrees.";
			if (r.Value) { lock (_sync) _sunGlide = (false, r.Size); return "Sun moving " + r.Size.ToString("+0.#;-0.#", System.Globalization.CultureInfo.InvariantCulture) + " degrees."; }
			lock (_sync) _sunNudgeDeg += r.Size;
			result = "Sun moved " + r.Size.ToString("+0.#;-0.#", System.Globalization.CultureInfo.InvariantCulture) + " degrees.";
			break;
		case AdminAction.SunSet:
			if (!(r.Size >= 0 && r.Size <= 360)) return "Set the sun between 0 and 360 degrees.";
			if (r.Value) { lock (_sync) _sunGlide = (true, r.Size); return "Sun moving to " + r.Size.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " degrees."; }
			lock (_sync) _sunTargetDeg = r.Size;
			SunDegrees = r.Size;
			result = "Sun set to " + r.Size.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " degrees.";
			break;
		case AdminAction.Motd:
			result = _o.Access.SetMotd(r.Name);
			break;
		case AdminAction.AccessBypass:
		{
			// Only for the admin asking (the host included); the dedicated console has no player of its own.
			long identity = actorClientId == SyncProtocol.HostClientId ? _o.HostIdentity : Players().FirstOrDefault(p => p.ClientId == actorClientId)?.IdentityId ?? 0;
			if (identity == 0) return "There is no player of yours in the world to bypass access for.";
			AccessBypass.Set(identity, r.Value);
			result = $"Access bypass {(r.Value ? "on" : "off")} for {actorName}.";
			_log.Info($"access bypass {(r.Value ? "on" : "off")} for {actorName} (identity {identity})");
			break;
		}
		case AdminAction.OfflineRaidProtection:
			result = _o.Access.SetOfflineRaid(r.Value);
			_o.OfflineRaidChanged?.Invoke(r.Value);
			BroadcastChat(new ChatLine("Server", 0, result, ChatKind.System, now));
			break;
		default: return "";
		}
		_log.Info($"admin: {actorName} {r.Action} {(r.Target != 0 ? name + " (" + r.Target + ")" : "")}: {result}");
		_nextAdminPushMs = 0;
		return result;
	}

	private string NameOf(ulong steamId) => KnownName(steamId) ?? steamId.ToString();

	private string KnownName(ulong steamId)
	{
		lock (_sync)
		{
			var p = _players.Values.FirstOrDefault(x => x.Peer?.Value == steamId) ?? _players.Values.FirstOrDefault(x => SteamIdOfKey(x.Key) == steamId);
			if (p != null) return p.Name;
		}
		var (bans, whitelist, admins) = _o.Access.Copy(0);
		return bans.FirstOrDefault(b => b.SteamId == steamId)?.Name ?? whitelist.Concat(admins).FirstOrDefault(a => a.SteamId == steamId)?.Name;
	}

	/// <summary>Removes every game instance of a Steam account.</summary>
	private void RemoveAccount(ulong account, RemovalNotice notice)
	{
		List<ulong> peers;
		lock (_sync) peers = _clients.Keys.Select(p => p.Value).Where(v => PeerAlias.SteamOf(v) == account).ToList();
		foreach (ulong peer in peers) Remove(peer, notice);
	}

	/// <summary>Tells a connected player why they are removed, then disconnects them shortly after.</summary>
	public bool Remove(ulong steamId, RemovalNotice notice)
	{
		PeerId? peer;
		lock (_sync) peer = _clients.Keys.Cast<PeerId?>().FirstOrDefault(p => p.Value.Value == steamId);
		if (peer == null) return false;
		lock (_sync) if (_clients.TryGetValue(peer.Value, out var client)) client.Removed = true;
		_t.Send(peer.Value, Channel.Control, TextMessage.Encode(SyncMsg.JoinReject, JoinRejectCode.Removed, JsonSerializer.Serialize(notice)));
		lock (_removals) _removals.Add((peer.Value, _clock() + 750));
		return true;
	}

	private void TickAdmin(long now)
	{
		lock (_removals)
			for (int i = _removals.Count - 1; i >= 0; i--)
				if (now >= _removals[i].DueMs)
				{
					_t.Disconnect(_removals[i].Peer, 1000, "Removed by an admin");
					_removals.RemoveAt(i);
				}
		if (now < _nextAdminPushMs) return;
		_nextAdminPushMs = now + 1000;
		List<RemoteClient> clients;
		lock (_sync) clients = _clients.Values.Where(c => c.Status == ClientStatus.Synced).ToList();
		if (clients.Count == 0) return;
		var players = SteadyPings(AccessPlayers(), now);
		// One document per role, shared by every client of that role; only an action's reply differs per client.
		var byRole = new JsonDoc[3];
		lock (_adminDiffs)
		{
			_adminDiffs.Clear();
			foreach (var c in clients)
			{
				string message;
				lock (_sync) _adminMessages.Remove(c.Peer, out message);
				var role = RoleOf(c.Peer.Value);
				var doc = byRole[(int)role] ??= JsonDoc.Of(StateFor(role, players, ""));
				if (!string.IsNullOrEmpty(message)) doc = doc.With(nameof(AdminState.Message), JsonValue.Create(message));
				if (c.Player is { IdentityId: not 0 } player && AccessBypass.IsOn(player.IdentityId)) doc = doc.With(nameof(AdminState.AccessBypass), JsonValue.Create(true));
				SendDoc(c, c.AdminDoc, SyncMsg.AdminState, doc, _adminDiffs);
			}
			_adminDiffs.Clear();
		}
	}

	// ponytail: every client gets every ping, O(players^2) bytes; refreshed only on a real move, and every 5 s per 64 players
	// so the total stays linear in players. Send pings only to clients with the player list open if that gets too stale.
	private AccessPlayer[] SteadyPings(AccessPlayer[] players, long now)
	{
		bool refresh = now >= _pingsDueMs;
		if (refresh) _pingsDueMs = now + PingRefreshMs * Math.Max(1, players.Length / 64);
		var shown = _shownPings;
		for (int i = 0; i < players.Length; i++)
		{
			var p = players[i];
			if (i >= shown.Length || shown[i].SteamId != p.SteamId || shown[i].Name != p.Name || shown[i].PingMs < 0 || p.PingMs < 0 || !p.Connected) continue;
			int was = shown[i].PingMs;
			if (!refresh || Math.Abs(p.PingMs - was) <= Math.Max(20, was / 5)) players[i] = p with { PingMs = was };
		}
		_shownPings = players;
		return players;
	}

	/// <summary>The roster with roles; ping is -1 when unknown.</summary>
	public AccessPlayer[] AccessPlayers()
	{
		var list = new List<AccessPlayer>();
		if (_o.HostCountsAsPlayer) list.Add(new AccessPlayer(_o.HostSteamId, Decorate(_o.HostName, _o.HostIdentity), OnlineRole.Host, true, 0));
		List<(PeerId? Peer, string Key, string Name, long Identity)> players;
		lock (_sync) players = _players.Values.Select(p => (p.Peer, p.Key, p.Name, p.IdentityId)).ToList();
		foreach (var p in players)
		{
			int ping = -1;
			if (p.Peer is PeerId peer && _t.TryGetStats(peer, out PeerStats st) && st.LastRttMs >= 0) ping = (int)st.LastRttMs;
			ulong id = p.Peer?.Value ?? SteamIdOfKey(p.Key);
			list.Add(new AccessPlayer(id, Decorate(p.Name, p.Identity), RoleOf(id), p.Peer.HasValue, ping));
		}
		return list.ToArray();
	}

	private static ulong SteamIdOfKey(string key)
	{
		if (!key.StartsWith("steam:", StringComparison.Ordinal)) return 0;
		var digits = key.AsSpan(6);
		int hash = digits.IndexOf('#');
		return ulong.TryParse(hash < 0 ? digits : digits[..hash], out ulong id) ? id : 0;
	}

	public AdminState StateFor(OnlineRole role, AccessPlayer[] players, string message)
	{
		var (bans, whitelist, _) = _o.Access.Copy(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
		bool admin = role != OnlineRole.Player;
		return new AdminState
		{
			Role = role, ServerName = _o.SessionName, Dedicated = !_o.HostCountsAsPlayer, Message = message, MaxPlayers = _o.MaxPlayers,
			AccessBypass = role == OnlineRole.Host && AccessBypass.IsOn(_o.HostIdentity),
			WhitelistOnly = _o.Access.WhitelistOnly, Pvp = Pvp, OfflineRaidProtection = OfflineRaidProtection, PcuLimit = _o.PcuInForce, SunRotation = _o.SunInForce.Rotation, SunPeriodMinutes = _o.SunInForce.PeriodMinutes, SunDegrees = SunDegrees, Motd = admin ? MotdTemplate : "", VoiceRadius = _o.VoiceRadiusMeters, HasPassword = !string.IsNullOrEmpty(_o.Password), Revision = _o.Access.Revision,
			Players = admin ? players : players.Select(p => p with { SteamId = 0 }).ToArray(),
			Bans = admin ? bans : Array.Empty<BanEntry>(), Whitelist = admin ? whitelist : Array.Empty<AccessEntry>(),
			SafeZones = admin ? _o.SafeZones?.Invoke() ?? Array.Empty<SafeZoneInfo>() : Array.Empty<SafeZoneInfo>()
		};
	}

	/// <summary>Replies to a world admin action requested by a joiner.</summary>
	public void SendAdminMessage(PeerId? peer, string message)
	{
		if (peer == null) { LocalAdminMessage = message; return; }
		lock (_sync) _adminMessages[peer.Value] = message;
		_nextAdminPushMs = 0;
	}

	public string LocalAdminMessage { get; set; } = "";

	/// <summary>The message of the day in force: the runtime choice, else the server configuration.</summary>
	public string MotdTemplate => _o.Access.Motd ?? _o.Motd ?? "";

	// Sent once at join; the joiner shows it (dialog and chat) after it has spawned.
	private void SendMotd(PeerId peer, string name)
	{
		string text = Core.Motd.Expand(MotdTemplate, name, _o.SessionName, PlayerTable().Count(p => p.Connected), _o.MaxPlayers);
		if (text.Length == 0) return;
		string title = string.IsNullOrWhiteSpace(_o.MotdTitle) ? _o.SessionName : _o.MotdTitle;
		_t.Send(peer, Channel.Control, SocialMessage.Encode(SyncMsg.Motd, new MotdMessage(title ?? "", text)));
	}
}
