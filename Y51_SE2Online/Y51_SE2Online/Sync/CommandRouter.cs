using System;
using System.Collections.Generic;
using System.Linq;

namespace Y51_SE2Online.Sync;

public enum CommandChannel { Console, Rcon, Chat }

/// <summary>
/// One gate for every way a command arrives: chat (/kick ...), RCON with a Steam sign-in, and the console (always an
/// owner). The verb needs command.&lt;verb&gt;, chat needs chat.commands and RCON rcon.connect; what a command then does
/// to a player goes through the admin action checks (permission key and rank) like the admin panel.
/// </summary>
public static class CommandRouter
{
	/// <summary>Verbs a chat line may run; the rest (save, restart, stop, json, mods) are dedicated console and RCON only.</summary>
	public static readonly string[] ChatVerbs = ["help", "status", "players", "say", "kick", "ban", "unban", "bans", "admin", "deadmin", "admins", "group", "whitelist", "pvp", "raid", "pcu", "sun", "usage", "motd", "perms", "cleanup", "mods"];

	/// <summary>The canonical command word (aliases resolved), the one command.&lt;verb&gt; is named after.</summary>
	public static string Verb(string line)
	{
		string verb = Core.RconAccess.Verb(line);
		return verb is "admin" or "deadmin" ? "group" : verb;
	}

	/// <summary>Why <paramref name="actor"/> may not run <paramref name="line"/> on <paramref name="channel"/>, or null.</summary>
	public static string Refusal(PermissionSet perms, ulong actor, bool owner, string line, CommandChannel channel)
	{
		if (Core.RconAccess.Malformed(line ?? "") is { } bad) return bad;
		string word = Core.RconAccess.Verb(line);
		if (channel == CommandChannel.Chat && word.Length > 0 && !ChatVerbs.Contains(word)) return $"/{word} is not a command here. Try /help.";
		if (owner || channel == CommandChannel.Console) return null;
		if (channel == CommandChannel.Chat && !perms.Has(actor, "chat.commands")) return "Your group may not use chat commands.";
		if (channel == CommandChannel.Rcon && !perms.Has(actor, "rcon.connect")) return "Your group has no remote console access.";
		if (word is "help" or "") return null;
		string verb = Verb(line);
		if (!PermissionCatalog.CommandVerbs.Contains(verb)) return $"'{word}' is not a command.";
		if (!perms.Has(actor, "command." + verb)) return $"'{word}' is not allowed for your group ({perms.GroupOf(actor)}).";
		// "mods" lists the Workshop mods; "mods check" makes the server ask the Workshop now (and maybe restart).
		if (verb == "mods" && (line ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).FirstOrDefault()?.ToLowerInvariant() == "check" && !perms.Has(actor, "mods.check"))
			return $"'mods check' is not allowed for your group ({perms.GroupOf(actor)}).";
		return null;
	}

	/// <summary>The RCON command words this actor may run (aliases included), as the RCON help and json commands list them.</summary>
	public static List<string> AllowedVerbs(PermissionSet perms, ulong actor, bool owner)
	{
		if (owner) return ["*"];
		if (!perms.Has(actor, "rcon.connect")) return [];
		var list = new List<string>();
		foreach (string verb in Core.RconAccess.Commands)
			if (verb == "help" || perms.Has(actor, "command." + (verb is "admin" or "deadmin" ? "group" : verb))) list.Add(verb);
		return list;
	}

	/// <summary>The help text for chat: the commands this player may run.</summary>
	public static string ChatHelp(PermissionSet perms, ulong actor, bool owner)
	{
		var verbs = ChatVerbs.Where(v => v == "help" || owner || perms.Has(actor, "command." + (v is "admin" or "deadmin" ? "group" : v))).ToList();
		return "Commands you may use: " + string.Join(", ", verbs.Select(v => "/" + v)) + ". Players: a SteamID64 or a name.";
	}
}
