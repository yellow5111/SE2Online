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

	/// <summary>Host for an owner, Admin for a player whose group has the admin panel, else Player (display and old clients).</summary>
	public OnlineRole RoleOf(ulong peerId)
	{
		ulong steam = PeerAlias.SteamOf(peerId);
		return steam == 0 ? OnlineRole.Player : IsOwner(steam) ? OnlineRole.Host : Allowed(steam, "admin.panel") ? OnlineRole.Admin : OnlineRole.Player;
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
				verdict = _o.Access.CheckJoin(account, req.Password, _o.Password, _o.SessionName, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), key => Allowed(account, key));
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
		ulong steam = PeerAlias.SteamOf(c.Peer.Value);
		long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		if (!Allowed(steam, "chat.no_throttle") && !_chatThrottle.Allow(c.Peer.Value, _clock()))
		{
			SendChatTo(c.Peer, new ChatLine("Server", 0, "You are sending messages too quickly.", ChatKind.System, nowMs));
			return;
		}
		if (text.StartsWith('/') && text.Length > 1)
		{
			ChatCommand(c.Peer, steam, c.Player.Name, c.Player.ClientId, text[1..]);
			return;
		}
		if (!Allowed(steam, "chat.send"))
		{
			SendChatTo(c.Peer, new ChatLine("Server", 0, "Your group may not send chat messages.", ChatKind.System, nowMs));
			return;
		}
		var kind = RoleOf(c.Peer.Value) != OnlineRole.Player ? ChatKind.Admin : ChatKind.Player;
		BroadcastChat(new ChatLine(DisplayName(c.Player), c.Peer.Value, text, kind, nowMs, DisplayGroup(steam)));
	}

	/// <summary>A chat command waiting for the game thread (dedicated: DedicatedRuntime, co-op: HostWorld).</summary>
	public sealed record PendingCommand(PeerId? Peer, ulong SteamId, string Name, ushort ClientId, string Line);

	public readonly System.Collections.Concurrent.ConcurrentQueue<PendingCommand> ChatCommands = new();

	private void ChatCommand(PeerId? peer, ulong steam, string name, ushort clientId, string line)
	{
		bool owner = peer == null || IsOwner(steam);
		string refused = CommandRouter.Refusal(Perms, steam, owner, line, CommandChannel.Chat);
		AuditLog.Write("chat", $"{name} ({steam})", "/" + line, refused ?? "queued");
		if (refused != null) { Reply(peer, refused); return; }
		if (Core.RconAccess.Verb(line) == "help") { Reply(peer, CommandRouter.ChatHelp(Perms, steam, owner)); return; }
		if (ChatCommands.Count >= 64) { Reply(peer, "The server is busy; try again in a moment."); return; }
		ChatCommands.Enqueue(new PendingCommand(peer, steam, name, clientId, line));
	}

	/// <summary>Runs one queued chat command on the game thread, as its player. <paramref name="run"/> executes the line
	/// (dedicated: the console's commands; co-op: <see cref="HostCommands"/>) and returns its output.</summary>
	public void RunChatCommand(PendingCommand command, Func<string, string> run)
	{
		bool owner = command.Peer == null || IsOwner(command.SteamId);
		LocalActor = command.Name;
		LocalActorId = owner ? 0 : command.SteamId;
		LocalActorClient = command.ClientId;
		string reply;
		try { reply = run(command.Line); }
		catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException) { reply = ex.Message; }
		finally { LocalActor = null; LocalActorId = 0; LocalActorClient = 0; }
		bool answerFollows;
		lock (_sync) answerFollows = command.Peer is PeerId p && _chatReplies.ContainsKey(p);
		if (string.IsNullOrWhiteSpace(reply)) { if (answerFollows) return; reply = "Done."; }
		_log.Info($"chat command: {command.Name} ({command.SteamId}): /{command.Line}: {reply.Split('\n')[0]}");
		Reply(command.Peer, reply);
	}

	/// <summary>A command's reply in the sender's chat only (several lines are several chat lines, at most 20).</summary>
	private void Reply(PeerId? peer, string text)
	{
		long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		foreach (string line in (text ?? "").Split('\n').Where(l => l.Trim().Length > 0).Take(20))
		{
			var chat = new ChatLine("Server", 0, SocialState.Clean(line, ChatLog.MaxText), ChatKind.System, now);
			if (peer is PeerId p) SendChatTo(p, chat); else ChatLog.Add(chat, _clock());
		}
	}

	/// <summary>Chat from the hosting player (identity known through HostIdentity) or the dedicated console.</summary>
	public void LocalChat(string text, bool console)
	{
		text = SocialState.Clean(text, ChatLog.MaxText);
		if (text.Length == 0) return;
		if (!console && text.StartsWith('/') && text.Length > 1) { ChatCommand(null, _o.HostSteamId, _o.HostName, SyncProtocol.HostClientId, text[1..]); return; }
		string from = console ? "Server" : Decorate(_o.HostName, _o.HostIdentity);
		BroadcastChat(new ChatLine(from, console ? 0 : _o.HostSteamId, text, console ? ChatKind.System : ChatKind.Admin, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), console ? null : DisplayGroup(_o.HostSteamId)));
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
		else message = Act(PeerAlias.SteamOf(c.Peer.Value), c.Player.Name, false, request, c.Peer, c.Player.ClientId);
		lock (_sync) _adminMessages[c.Peer] = message;
		_nextAdminPushMs = 0;
	}

	/// <summary>Set on the game thread while a remote console command runs: the ban list and chat name the RCON admin.</summary>
	[ThreadStatic] public static string LocalActor;

	/// <summary>Admin action by the hosting player, the dedicated console, or (with <see cref="LocalActorId"/> set) a player
	/// running a command from RCON or chat.</summary>
	public string LocalAdmin(AdminRequest request)
	{
		ulong actor = LocalActorId;
		if (actor != 0 && !IsOwner(actor))
		{
			ushort client = LocalActorClient != 0 ? LocalActorClient : ClientOfSteam(actor);
			return Act(actor, LocalActor ?? NameOrId(actor), false, request, PeerOfClient(client), client);
		}
		if (actor != 0 && LocalActorClient is var own && own != 0 && own != SyncProtocol.HostClientId)
			return Act(actor, LocalActor ?? NameOrId(actor), true, request, PeerOfClient(own), own);
		return Act(actor != 0 ? actor : _o.Dedicated ? 0 : _o.HostSteamId, LocalActor ?? (_o.Dedicated ? "Server" : _o.HostName), true, request, null, SyncProtocol.HostClientId);
	}

	private ushort ClientOfSteam(ulong steam)
	{
		lock (_sync)
			foreach (var p in _players.Values)
				if (p.Peer is PeerId peer && PeerAlias.SteamOf(peer.Value) == steam) return p.ClientId;
		return 0;
	}

	private PeerId? PeerOfClient(ushort clientId)
	{
		if (clientId == 0) return null;
		lock (_sync) return _players.Values.FirstOrDefault(p => p.ClientId == clientId)?.Peer;
	}

	/// <summary>Why a non-owner may not do this action, or null. Owners skip it; every action of anybody else is checked
	/// here, whatever the channel (panel, chat command, RCON).</summary>
	public string Refusal(ulong actor, AdminRequest r)
		=> AdminRules.Refusal(Perms, actor, r with { Target = PeerAlias.SteamOf(r.Target) }, target => IsOwner(target) || (target != 0 && target == _o.HostSteamId), NameOrId);

	private string Act(ulong actorId, string actorName, bool owner, AdminRequest r, PeerId? actorPeer, ushort actorClientId)
	{
		long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		if (!Enum.IsDefined(r.Action)) return "Unknown action.";
		if (r.Action == AdminAction.Refresh) return "";
		if (!owner && Refusal(actorId, r) is { } refused)
		{
			AuditLog.Write("admin", $"{actorName} ({actorId})", r.Action.ToString(), "refused: " + refused);
			return refused;
		}
		if (r.Action is AdminAction.Creative or AdminAction.Invulnerable or AdminAction.SpawnPrefab or AdminAction.SpawnItem or AdminAction.CreateSafeZone or AdminAction.DeleteSafeZone
			or AdminAction.TeleportTo or AdminAction.Bring or AdminAction.Kill or AdminAction.EntityQuery or AdminAction.EntityAction
			or AdminAction.CleanupPreview or AdminAction.CleanupRun or AdminAction.CleanupSettings or AdminAction.SetHome)
		{
			if (r.Action is AdminAction.EntityAction or AdminAction.CleanupRun or AdminAction.CleanupSettings or AdminAction.Kill)
				AuditLog.Write("admin", $"{actorName} ({actorId})", $"{r.Action} {r.Target} {(r.Action == AdminAction.EntityAction ? ((EntityVerb)r.Minutes).ToString() : "")} {r.Name}".Trim(), "sent to the world");
			if (InCommand && _o.RunWorldAdmin?.Invoke(actorPeer, actorClientId, r with { Name = r.Name ?? "" }) is { } answer) return answer;
			if (InCommand && actorPeer is PeerId replyTo) lock (_sync) _chatReplies[replyTo] = _chatReplies.GetValueOrDefault(replyTo) + 1;
			_world.OnAdminRequest(actorPeer, actorClientId, r with { Name = r.Name ?? "" });
			return InCommand ? (actorPeer != null ? "" : "Sent to the world; its answer is in the server log (admin: ...).") : "";
		}
		string name = NameOf(r.Target);
		ulong account = PeerAlias.SteamOf(r.Target);
		string result;
		switch (r.Action)
		{
			case AdminAction.Kick:
			case AdminAction.Ban:
				if (r.Target != 0 && account == _o.HostSteamId) return "Only the host can remove another admin.";
				if (account == actorId) return "You cannot remove yourself.";
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
		case AdminAction.SetGroup:
		{
			string group = r.Action == AdminAction.Promote ? PermissionSet.AdminGroup : r.Action == AdminAction.Demote ? PermissionSet.DefaultGroup : r.Name;
			if (r.Action == AdminAction.Demote && !owner && account == actorId) return "You cannot demote yourself.";
			if (r.Action == AdminAction.Demote && Perms.GroupOf(account) == PermissionSet.DefaultGroup) return "That player is not in a group (not an admin).";
			if (r.Action == AdminAction.Promote && Perms.Group(PermissionSet.AdminGroup) == null) return "This server has no Admin group (Permissions/Groups/Admin.json).";
			var (changed, text) = Perms.SetGroup(actorId, owner, account, name == account.ToString() ? Perms.NameOf(account) ?? "" : name, group);
			result = text;
			AuditLog.Write("admin", $"{actorName} ({actorId})", $"group {account} {group}", text);
			if (!changed) return result;
			// Toggles that need a permission end with it (CreativeTools reconciles creative and god mode within 2 s).
			if (!Allowed(account, "admin.access_bypass"))
				foreach (var p in Players().Where(p => PeerAlias.SteamOf(p.Peer?.Value ?? SteamIdOfKey(p.Key)) == account)) AccessBypass.Set(p.IdentityId, false);
			break;
		}
		case AdminAction.PermsReload:
			result = ReloadPermissions();
			AuditLog.Write("admin", $"{actorName} ({actorId})", "perms reload", result);
			break;
		case AdminAction.PermsCheck:
			if (account == 0) return "Choose a player.";
			result = PermsCheck(account, r.Name);
			break;
		case AdminAction.Usage:
			if (!ResourceMultipliers.Current.TryWith(r.Name ?? "", r.Size.ToString(System.Globalization.CultureInfo.InvariantCulture), out var usage, out string usageError)) return usageError;
			ResourceMultipliers.Current = usage;
			result = "Resource usage now " + usage.Describe() + " (until restart; set Network.EnergyUsageMultiplier etc. in the settings to keep it).";
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
		TickSeen(now);
		if (now < _nextAdminPushMs) return;
		_nextAdminPushMs = now + 1000;
		List<RemoteClient> clients;
		lock (_sync) clients = _clients.Values.Where(c => c.Status == ClientStatus.Synced).ToList();
		if (clients.Count == 0) return;
		var players = SteadyPings(AccessPlayers(), now);
		// One document per group (players with their own entries and owners get their own), shared by everyone in it; a
		// reply, a query result and the player's own toggles differ per client.
		var byGroup = new Dictionary<string, JsonDoc>(StringComparer.Ordinal);
		lock (_adminDiffs)
		{
			_adminDiffs.Clear();
			foreach (var c in clients)
			{
				string message;
				AdminQuery query;
				lock (_sync) { _adminMessages.Remove(c.Peer, out message); _adminQueries.TryGetValue(c.Peer, out query); }
				ulong steam = PeerAlias.SteamOf(c.Peer.Value);
				bool owner = IsOwner(steam);
				string key = owner ? "#owner" : Perms.HasOverrides(steam) ? "#" + steam : Perms.GroupOf(steam);
				if (!byGroup.TryGetValue(key, out var doc)) byGroup[key] = doc = JsonDoc.Of(StateFor(steam, owner, players, ""));
				if (!string.IsNullOrEmpty(message)) doc = doc.With(nameof(AdminState.Message), JsonValue.Create(message));
				if (c.Player is { IdentityId: not 0 } player)
				{
					if (AccessBypass.IsOn(player.IdentityId)) doc = doc.With(nameof(AdminState.AccessBypass), JsonValue.Create(true));
					if (_o.ModeOf?.Invoke(player.IdentityId) is var (creative, god))
					{
						if (creative) doc = doc.With(nameof(AdminState.Creative), JsonValue.Create(true));
						if (god) doc = doc.With(nameof(AdminState.Invulnerable), JsonValue.Create(true));
					}
				}
				if (query != null) doc = doc.With(nameof(AdminState.Query), JsonSerializer.SerializeToNode(query));
				SendDoc(c, c.AdminDoc, SyncMsg.AdminState, doc, _adminDiffs);
			}
			_adminDiffs.Clear();
		}
	}

	private readonly Dictionary<PeerId, AdminQuery> _adminQueries = new();
	private long _querySerial;

	/// <summary>A panel query's answer (entities, cleanup preview) for one player; it stays in their state until the next one.</summary>
	public void SendAdminQuery(PeerId? peer, AdminQuery query)
	{
		query.Serial = System.Threading.Interlocked.Increment(ref _querySerial);
		if (query.Rows.Length > 100) query.Rows = query.Rows[..100];
		if (peer == null) { LocalAdminQuery = query; return; }
		lock (_sync) _adminQueries[peer.Value] = query;
		_nextAdminPushMs = 0;
	}

	public AdminQuery LocalAdminQuery { get; set; }

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

	/// <summary>The roster with roles and groups; ping is -1 when unknown.</summary>
	public AccessPlayer[] AccessPlayers()
	{
		var list = new List<AccessPlayer>();
		if (_o.HostCountsAsPlayer) list.Add(new AccessPlayer(_o.HostSteamId, Decorate(_o.HostName, _o.HostIdentity), OnlineRole.Host, true, 0, DisplayGroup(_o.HostSteamId), PermissionSet.OwnerRank));
		List<(PeerId? Peer, string Key, string Name, long Identity)> players;
		lock (_sync) players = _players.Values.Select(p => (p.Peer, p.Key, p.Name, p.IdentityId)).ToList();
		foreach (var p in players)
		{
			int ping = -1;
			if (p.Peer is PeerId peer && _t.TryGetStats(peer, out PeerStats st) && st.LastRttMs >= 0) ping = (int)st.LastRttMs;
			ulong id = p.Peer?.Value ?? SteamIdOfKey(p.Key);
			ulong steam = PeerAlias.SteamOf(id);
			list.Add(new AccessPlayer(id, Decorate(p.Name, p.Identity), RoleOf(id), p.Peer.HasValue, ping, DisplayGroup(steam), Perms.RankOf(steam)));
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

	/// <summary>The hosting player's own state (co-op host: an owner).</summary>
	public AdminState StateFor(OnlineRole role, AccessPlayer[] players, string message)
	{
		var state = StateFor(_o.Dedicated ? 0 : _o.HostSteamId, role == OnlineRole.Host, players, message);
		state.AccessBypass = role == OnlineRole.Host && AccessBypass.IsOn(_o.HostIdentity);
		if (role == OnlineRole.Host && _o.ModeOf?.Invoke(_o.HostIdentity) is var (creative, god)) { state.Creative = creative; state.Invulnerable = god; }
		state.Query = LocalAdminQuery;
		return state;
	}

	/// <summary>What one player may see: SteamIDs, bans, whitelist, safe zones and the message template only with their keys.</summary>
	public AdminState StateFor(ulong steam, bool owner, AccessPlayer[] players, string message)
	{
		var (bans, whitelist, _) = _o.Access.Copy(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
		bool Can(string key) => owner || Allowed(steam, key);
		// Acting on a player names them by SteamID, so every key that targets players shows the ids.
		bool ids = Can("admin.players.view_ids") || Can("admin.players.kick") || Can("admin.players.ban") || Can("admin.players.set_group") || Can("admin.players.kill")
			|| Can("admin.teleport.to") || Can("admin.teleport.bring") || Can("admin.spawn.item_others") || Can("admin.whitelist");
		return new AdminState
		{
			Role = owner ? OnlineRole.Host : steam != 0 && Allowed(steam, "admin.panel") ? OnlineRole.Admin : OnlineRole.Player,
			ServerName = _o.SessionName, Dedicated = !_o.HostCountsAsPlayer, Message = message, MaxPlayers = _o.MaxPlayers,
			WhitelistOnly = _o.Access.WhitelistOnly, Pvp = Pvp, OfflineRaidProtection = OfflineRaidProtection, PcuLimit = _o.PcuInForce, SunRotation = _o.SunInForce.Rotation, SunPeriodMinutes = _o.SunInForce.PeriodMinutes, SunDegrees = SunDegrees,
			Motd = Can("admin.world.motd") ? MotdTemplate : "", VoiceRadius = _o.VoiceRadiusMeters, MarkerDefaults = _o.MarkerDefaults, PingLifetimeS = _o.Pings.LifetimeS, PingMaxRangeMeters = _o.Pings.MaxRangeMeters, HasPassword = !string.IsNullOrEmpty(_o.Password), Revision = _o.Access.Revision + Perms.Revision,
			Players = ids ? players : players.Select(p => p with { SteamId = 0 }).ToArray(),
			Bans = Can("admin.players.view_bans") ? bans : Array.Empty<BanEntry>(), Whitelist = Can("admin.whitelist") ? whitelist : Array.Empty<AccessEntry>(),
			SafeZones = Can("admin.safezone.create") || Can("admin.safezone.edit") || Can("admin.safezone.delete") ? _o.SafeZones?.Invoke() ?? Array.Empty<SafeZoneInfo>() : Array.Empty<SafeZoneInfo>(),
			Group = owner ? DisplayGroup(steam) : Perms.GroupOf(steam), Rank = owner ? PermissionSet.OwnerRank : Perms.RankOf(steam),
			Grants = owner ? new[] { "*" } : Perms.Grants(steam), Styles = Perms.Styles(),
			Groups = GroupsFor(steam, owner), Known = KnownFor(steam, owner),
			Cleanup = Can("admin.cleanup") ? CleanupStatus() : "",
			CleanupConfig = Can("cleanup.configure") ? _o.CleanupConfig?.Invoke() ?? "" : ""
		};
	}

	/// <summary>Replies to a world admin action requested by a joiner.</summary>
	public void SendAdminMessage(PeerId? peer, string message)
	{
		if (peer == null) { LocalAdminMessage = message; return; }
		bool chat;
		lock (_sync)
		{
			_adminMessages[peer.Value] = message;
			chat = _chatReplies.TryGetValue(peer.Value, out int waiting) && waiting > 0;
			if (chat) { if (waiting == 1) _chatReplies.Remove(peer.Value); else _chatReplies[peer.Value] = waiting - 1; }
		}
		// The answer to a /command that went to the world comes back in that player's chat too.
		if (chat && !string.IsNullOrEmpty(message)) Reply(peer, message);
		_nextAdminPushMs = 0;
	}

	private readonly Dictionary<PeerId, int> _chatReplies = new();

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
