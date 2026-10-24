using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Y51_SE2Online.Sync;

/// <summary>
/// First start of 0.18 (or a missing Permissions folder): writes Player.json and Groups/Admin.json so that together they
/// give exactly the 0.17 rights (admins: every panel action, every game admin tool, every request bypass, password and
/// whitelist skipped, bans not; players: none of that), copies access.json's admins into players.json, and moves the
/// server.json RconRoles lists into the groups. Files that exist are never touched.
/// </summary>
public static class PermissionFiles
{
	/// <summary>The 0.17 RconRoles defaults; an unchanged Admin list becomes the default admin command set.</summary>
	public static readonly string[] DefaultRconAdmin = ["status", "players", "json", "save", "say", "kick", "ban", "unban", "bans", "admins", "whitelist", "pvp", "raid", "pcu", "sun", "usage", "mods", "motd"];

	/// <summary>An RCON command word as a permission key: admin and deadmin are the group command.</summary>
	public static string CommandKey(string verb) => "command." + (verb is "admin" or "deadmin" ? "group" : verb);

	public static JsonObject DefaultGroupJson(IReadOnlyCollection<string> rconPlayer)
	{
		var p = new JsonObject
		{
			["chat.send"] = true, ["chat.commands"] = true, ["voice.speak"] = true,
			["build.*"] = true, ["blueprint.*"] = true, ["voxel.drill"] = true, ["ownership.transfer"] = true,
			["use.*"] = true, ["trade.*"] = true, ["faction.*"] = true, ["gps.share"] = true, ["ping.use"] = true, ["emote.use"] = true, ["hack.use"] = true,
			["pvp.damage_players"] = true, ["fasttravel.use"] = true, ["respawn.home.*"] = true,
			["admin.*"] = false, ["game.admin.*"] = false, ["bypass.*"] = false, ["cleanup.*"] = false, ["join.*"] = false,
			["chat.no_throttle"] = false, ["debugmenu.open"] = true,
			["command.*"] = false, ["command.help"] = true, ["command.status"] = true, ["command.players"] = true,
			["rcon.connect"] = rconPlayer.Count > 0,
			["limits.pcu"] = -1, ["blueprint.max_blocks"] = -1
		};
		foreach (string verb in rconPlayer) p[verb == "*" ? "command.*" : CommandKey(verb)] = true;
		return new JsonObject
		{
			["Version"] = 1, ["Rank"] = 0,
			["Style"] = new JsonObject { ["NameColor"] = "#78C8FF", ["TextColor"] = "#FFFFFF" },
			["Permissions"] = p
		};
	}

	public static JsonObject AdminGroupJson(IReadOnlyCollection<string> rconAdmin)
	{
		var p = new JsonObject
		{
			["admin.*"] = true, ["game.admin.*"] = true, ["bypass.*"] = true,
			["join.bypass_password"] = true, ["join.bypass_whitelist"] = true,
			["debugmenu.open"] = true, ["cleanup.run"] = true, ["mods.check"] = true,
			["rcon.connect"] = rconAdmin.Count > 0,
			["command.*"] = false
		};
		foreach (string verb in rconAdmin) p[verb == "*" ? "command.*" : CommandKey(verb)] = true;
		// New in 0.18 (no 0.17 rule to keep): group changes by command follow the rank rule, cleanup by its own keys.
		if (!p.ContainsKey("command.group")) p["command.group"] = true;
		if (!p.ContainsKey("command.cleanup")) p["command.cleanup"] = true;
		if (!p.ContainsKey("command.perms")) p["command.perms"] = true;
		return new JsonObject
		{
			["Version"] = 1, ["Inherits"] = PermissionSet.DefaultGroup, ["Rank"] = PermissionSet.AdminRank,
			["Style"] = new JsonObject { ["NameColor"] = "#FFAA3C", ["TextColor"] = "#FFFFFF" },
			["Permissions"] = p
		};
	}

	private static IReadOnlyCollection<string> Role(IReadOnlyDictionary<string, List<string>> roles, string name, string[] fallback)
	{
		if (roles == null) return fallback;
		foreach (var (role, list) in roles)
			if (string.Equals(role, name, StringComparison.OrdinalIgnoreCase))
				return (list ?? new()).Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim() == "*" ? "*" : Core.RconAccess.Verb(c)).Distinct().ToList();
		return fallback;
	}

	/// <summary>
	/// Creates what is missing. <paramref name="groupsFolder"/> holds Player.json and Groups/; <paramref name="playersFile"/>
	/// is seeded from <paramref name="access"/>'s admins. Returns one line per file written.
	/// </summary>
	public static List<string> Prepare(string groupsFolder, string playersFile, AccessList access, IReadOnlyDictionary<string, List<string>> rconRoles)
	{
		var notes = new List<string>();
		RenamePreReleaseFiles(groupsFolder, notes);
		string defaults = Path.Combine(groupsFolder, PermissionSet.DefaultGroup + ".json");
		if (!File.Exists(defaults))
		{
			Directory.CreateDirectory(Path.Combine(groupsFolder, "Groups"));
			var player = Role(rconRoles, Core.RconCaller.Player, Array.Empty<string>());
			var admin = Role(rconRoles, Core.RconCaller.Admin, DefaultRconAdmin);
			Write(defaults, DefaultGroupJson(player));
			notes.Add("wrote " + defaults);
			string adminFile = Path.Combine(groupsFolder, "Groups", PermissionSet.AdminGroup + ".json");
			if (!File.Exists(adminFile)) { Write(adminFile, AdminGroupJson(admin)); notes.Add("wrote " + adminFile); }
			if (rconRoles != null) notes.Add("RconRoles moved to Permissions/ (kept in server.json for a 0.17 rollback, ignored from now on)");
		}
		if (playersFile != null && !File.Exists(playersFile))
		{
			var file = new PlayerAssignments();
			foreach (var a in access?.Copy(0).Admins ?? Array.Empty<AccessEntry>())
				file.Players[a.SteamId.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new PlayerAssignment { Group = PermissionSet.AdminGroup, Name = a.Name };
			file.MirroredAdmins = file.Players.Keys.ToList();
			Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(playersFile))!);
			Write(playersFile, JsonSerializer.SerializeToNode(file, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull })!.AsObject());
			notes.Add($"wrote {playersFile} ({file.Players.Count} admin{(file.Players.Count == 1 ? "" : "s")} from the access list)");
		}
		return notes;
	}

	/// <summary>
	/// The 0.18.0 pre-releases wrote default.json and Groups/admin.json: they become Player.json (default.json is kept as
	/// default.json.bak) and Groups/Admin.json, so the names read well in game. players.json and Inherits still saying
	/// "default" or "admin" resolve to the new names; nothing else changes.
	/// </summary>
	public static void RenamePreReleaseFiles(string groupsFolder, List<string> notes)
	{
		try
		{
			if (groupsFolder == null || !Directory.Exists(groupsFolder)) return;
			string old = Path.Combine(groupsFolder, "default.json"), player = Path.Combine(groupsFolder, PermissionSet.DefaultGroup + ".json");
			if (File.Exists(old) && !File.Exists(player))
			{
				File.Copy(old, old + ".bak", true);
				File.Move(old, player);
				notes.Add($"renamed {old} to {PermissionSet.DefaultGroup}.json (backup: default.json.bak)");
			}
			string groups = Path.Combine(groupsFolder, "Groups");
			if (!Directory.Exists(groups)) return;
			// Windows file names ignore case, so admin.json is found by its exact spelling and renamed through a temporary name.
			if (Directory.GetFiles(groups, "*.json").FirstOrDefault(f => Path.GetFileName(f) == "admin.json") is { } lower
				&& !Directory.GetFiles(groups, "*.json").Any(f => Path.GetFileName(f) == PermissionSet.AdminGroup + ".json"))
			{
				string temp = lower + ".renaming";
				File.Move(lower, temp, true);
				File.Move(temp, Path.Combine(groups, PermissionSet.AdminGroup + ".json"));
				notes.Add($"renamed Groups/admin.json to Groups/{PermissionSet.AdminGroup}.json");
			}
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException) { notes.Add("could not rename the pre-release permission files: " + e.Message); }
	}

	private static void Write(string path, JsonObject json)
	{
		string tmp = path + ".tmp";
		File.WriteAllText(tmp, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
		File.Move(tmp, path, true);
	}
}
