using System;
using System.Globalization;
using System.Linq;

namespace Y51_SE2Online.Sync;

/// <summary>
/// The moderation commands every channel shares (dedicated console and command file, RCON, /commands in chat, co-op host
/// chat). Each change goes through <see cref="HostSession.LocalAdmin"/>, which checks the actor's permissions and rank
/// (<see cref="HostSession.LocalActorId"/>; 0 is the console, an owner). Returns null for a verb that is not one of these.
/// </summary>
public static class HostCommands
{
	public static readonly string[] Verbs = ["say", "kick", "ban", "unban", "bans", "admin", "deadmin", "admins", "group", "whitelist", "pvp", "raid", "pcu", "sun", "usage", "perms", "cleanup"];

	/// <summary>"players info &lt;player&gt; [first grid]" (or "player info"): player info, which needs admin.players.info on top of command.players.</summary>
	public static bool IsPlayerInfo(string command)
	{
		string[] words = (command ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
		return words.Length > 1 && words[0].ToLowerInvariant() is "players" or "player" && words[1].Equals("info", StringComparison.OrdinalIgnoreCase);
	}

	public static string Run(HostSession host, string command)
	{
		bool was = HostSession.InCommand;
		HostSession.InCommand = true;
		try { return RunCore(host, command); }
		finally { HostSession.InCommand = was; }
	}

	private static string RunCore(HostSession host, string command)
	{
		string[] words = (command ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
		if (words.Length == 0) return null;
		string verb = words[0].ToLowerInvariant();
		bool info = IsPlayerInfo(command);
		if (!Verbs.Contains(verb) && !info) return null;
		if (host == null) throw new InvalidOperationException("Multiplayer is not running yet.");
		string Rest(int from) => string.Join(' ', words.Skip(from));
		ulong Id(int index) => words.Length > index && host.FindPlayer(words[index]) is var id && id != 0 ? id : throw new ArgumentException(words.Length > index ? $"No known player '{words[index]}' (give a SteamID64 or a name; see players)." : "Give a SteamID64 or a player name (see players).");
		long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		var invariant = CultureInfo.InvariantCulture;
		if (info)
			return words.Length < 3 ? "Use players info <player> [first grid], e.g. players info Bob 25 for the second page of grids."
				: host.LocalAdmin(new AdminRequest(AdminAction.PlayerInfo, Id(2), Minutes: words.Length > 3 && int.TryParse(words[3], NumberStyles.None, invariant, out int first) ? first : 0, Value: true));
		switch (verb)
		{
			case "say": host.LocalChat(Rest(1), true); return "";
			case "pvp":
				return words.Length > 1 && words[1] is "on" or "off" ? host.LocalAdmin(new AdminRequest(AdminAction.Pvp, Value: words[1] == "on")) : "PvP is " + (host.Pvp ? "on" : "off") + ". Use pvp on|off.";
			case "pcu":
				return words.Length > 1 && (words[1] is "off" or "default" || int.TryParse(words[1], out _))
					? host.LocalAdmin(new AdminRequest(AdminAction.PcuLimit, Size: words[1] == "off" ? 0 : words[1] == "default" ? -1 : int.Parse(words[1], invariant)))
					: "PCU limit: " + (host.PcuLimit is int limit ? (limit == 0 ? "unlimited" : limit.ToString("N0", invariant)) : "the world's own setting") + ". Use pcu <number>|off|default.";
			case "sun":
				return words.Length > 1 && words[1] is "on" or "off" ? host.LocalAdmin(new AdminRequest(AdminAction.SunRotation, Value: words[1] == "on"))
					: words.Length > 2 && words[1] == "day" && double.TryParse(words[2], NumberStyles.Float, invariant, out double day) ? host.LocalAdmin(new AdminRequest(AdminAction.SunPeriod, Size: day))
					: words.Length > 2 && words[1] == "move" && double.TryParse(words[2], NumberStyles.Float, invariant, out double deg) ? host.LocalAdmin(new AdminRequest(AdminAction.SunNudge, Value: true, Size: deg))
					: words.Length > 2 && words[1] == "set" && double.TryParse(words[2], NumberStyles.Float, invariant, out double angle) ? host.LocalAdmin(new AdminRequest(AdminAction.SunSet, Value: true, Size: angle))
					: "Sun rotation is " + (host.SunInForce.Rotation ? "on" : "off") + ", one full day every " + AccessList.DayLength(host.SunInForce.PeriodMinutes) + $", sun at {host.SunDegrees:0} degrees. Use sun on|off, sun day <minutes>, sun move <degrees>, sun set <0-360>.";
			case "usage":
				if (words.Length > 2 && double.TryParse(words[2], NumberStyles.Float, invariant, out double factor)) return host.LocalAdmin(new AdminRequest(AdminAction.Usage, Name: words[1], Size: factor));
				return "Resource usage: " + ResourceMultipliers.Current.Describe() + ". Use usage energy|hydrogen|oxygen <0-10>.";
			case "raid":
				return words.Length > 1 && words[1] is "on" or "off" ? host.LocalAdmin(new AdminRequest(AdminAction.OfflineRaidProtection, Value: words[1] == "on")) : "Offline raid protection is " + (host.OfflineRaidProtection ? "on" : "off") + ". Use raid on|off.";
			case "kick": return host.LocalAdmin(new AdminRequest(AdminAction.Kick, Id(1), Rest(2)));
			case "ban":
				int minutes = 0, reasonFrom = 2;
				if (words.Length > 2 && (int.TryParse(words[2], out minutes) || words[2].Equals("perm", StringComparison.OrdinalIgnoreCase))) reasonFrom = 3;
				return host.LocalAdmin(new AdminRequest(AdminAction.Ban, Id(1), Rest(reasonFrom), Math.Max(0, minutes)));
			case "unban": return host.LocalAdmin(new AdminRequest(AdminAction.Unban, Id(1)));
			case "admin": return host.LocalAdmin(new AdminRequest(AdminAction.Promote, Id(1)));
			case "deadmin": return host.LocalAdmin(new AdminRequest(AdminAction.Demote, Id(1)));
			case "bans":
				var bans = host.Access.Copy(now).Bans;
				return bans.Length == 0 ? "No active bans." : string.Join("\n", bans.Select(b => $"{b.SteamId}  {b.Name}  {(b.ExpiresUnixMs is long end ? AccessList.Remaining(end - now) + " left" : "permanent")}  {b.Reason}"));
			case "admins":
				var admins = host.Perms.AdminMembers();
				return admins.Count == 0 ? "No admins. Use admin <SteamID64> or group set <player> admin." : string.Join("\n", admins.Select(a => $"{a.SteamId}  {a.Name}  {host.Perms.GroupOf(a.SteamId)}"));
			case "group": return Group(host, words, Id);
			case "perms":
				string sub = words.Length > 1 ? words[1].ToLowerInvariant() : "";
				if (sub == "reload") return host.LocalAdmin(new AdminRequest(AdminAction.PermsReload));
				if (sub == "check" && words.Length > 2) return host.LocalAdmin(new AdminRequest(AdminAction.PermsCheck, Id(2), Name: words.Length > 3 ? words[3] : ""));
				return "Use perms reload | perms check <player> [key].";
			case "cleanup":
				string what = words.Length > 1 ? words[1].ToLowerInvariant() : "status";
				return what switch
				{
					"preview" => host.LocalAdmin(new AdminRequest(AdminAction.CleanupPreview, Name: words.Length > 2 ? words[2] : "")),
					"run" => host.LocalAdmin(new AdminRequest(AdminAction.CleanupRun, Name: words.Skip(2).FirstOrDefault(w => !w.Equals("now", StringComparison.OrdinalIgnoreCase)) ?? "", Value: words.Skip(2).Any(w => w.Equals("now", StringComparison.OrdinalIgnoreCase)))),
					"cancel" => host.LocalAdmin(new AdminRequest(AdminAction.CleanupRun, Minutes: -1)),
					"status" => host.CleanupStatus(),
					_ => "Use cleanup status | preview [category] | run [category] [now] | cancel."
				};
			default:
				string w = words.Length > 1 ? words[1].ToLowerInvariant() : "list";
				return w switch
				{
					"on" => host.LocalAdmin(new AdminRequest(AdminAction.WhitelistOnly, Value: true)),
					"off" => host.LocalAdmin(new AdminRequest(AdminAction.WhitelistOnly, Value: false)),
					"add" => host.LocalAdmin(new AdminRequest(AdminAction.WhitelistAdd, Id(2))),
					"remove" => host.LocalAdmin(new AdminRequest(AdminAction.WhitelistRemove, Id(2))),
					_ => (host.Access.WhitelistOnly ? "Whitelist enforced. " : "Whitelist not enforced. ") + string.Join(", ", host.Access.Copy(now).Whitelist.Select(e => e.SteamId + " " + e.Name))
				};
		}
	}

	// group list | group show <player> | group set|add <player> <group> | group remove <player> [group]
	private static string Group(HostSession host, string[] words, Func<int, ulong> id)
	{
		string sub = words.Length > 1 ? words[1].ToLowerInvariant() : "list";
		var perms = host.Perms;
		switch (sub)
		{
			case "list":
				return string.Join("\n", perms.Groups.OrderByDescending(g => g.Rank).ThenBy(g => g.Name).Select(g =>
				{
					var members = perms.Assignments().Where(a => a.Group == g.Name).ToArray();
					string who = g.Name == PermissionSet.DefaultGroup ? "everyone else" : members.Length == 0 ? "nobody" : string.Join(", ", members.Take(20).Select(m => string.IsNullOrWhiteSpace(m.Name) ? m.SteamId.ToString(CultureInfo.InvariantCulture) : m.Name)) + (members.Length > 20 ? $" and {members.Length - 20} more" : "");
					return $"{g.Name}  rank {g.Rank}{(g.Inherits is { } p && g.Name != PermissionSet.DefaultGroup ? ", inherits " + p : "")}: {who}";
				}));
			case "show":
				ulong target = id(2);
				return $"{host.NameOrId(target)}: group {perms.GroupOf(target)}, rank {perms.RankOf(target)}{(perms.IsOwner(target) ? " (owner)" : "")}";
			case "set" or "add":
				if (words.Length < 4) return "Use group set <player> <group>.";
				return host.LocalAdmin(new AdminRequest(AdminAction.SetGroup, id(2), Name: words[3]));
			case "remove":
				ulong who2 = id(2);
				if (words.Length > 3 && !perms.GroupOf(who2).Equals(words[3], StringComparison.OrdinalIgnoreCase)) return $"{host.NameOrId(who2)} is not in group {words[3].ToLowerInvariant()} (they are in {perms.GroupOf(who2)}).";
				return host.LocalAdmin(new AdminRequest(AdminAction.SetGroup, who2, Name: PermissionSet.DefaultGroup));
			default: return "Use group list | group show <player> | group set <player> <group> | group remove <player> [group].";
		}
	}
}
