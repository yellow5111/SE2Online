using System;
using System.Collections.Generic;
using System.Linq;

namespace Y51_SE2Online.Sync;

/// <summary>One permission key. <see cref="Default"/> applies when no group file sets the key (a key added in a later
/// version behaves sensibly in old files). Value keys hold a number instead of allow/deny. <see cref="Where"/> says in
/// plain words where the server (or the player's game) checks it, <see cref="Related"/> names keys that go with it; both
/// feed the generated host reference (PERMISSIONS-REFERENCE.md).</summary>
public sealed record PermissionInfo(string Key, bool Default, string Category, string Description, double? DefaultValue = null, string Where = "", string[] Related = null)
{
	public bool IsValue => DefaultValue.HasValue;
}

/// <summary>Every permission SE2 Online checks, where it is checked, and its default. The runner editor and the admin
/// panel list them from here.</summary>
public static class PermissionCatalog
{
	public const string Join = "Join", Chat = "Chat and commands", Panel = "Admin panel", Keen = "Game admin tools (F12)", Bypass = "Request checks",
		Build = "Building", Use = "Use and interaction", Cleanup = "Cleanup";

	/// <summary>Command words a role may run from chat, RCON or the console (command.&lt;verb&gt;).</summary>
	public static readonly string[] CommandVerbs =
	[
		"help", "status", "players", "json", "save", "say", "kick", "ban", "unban", "bans", "group", "admins", "whitelist",
		"pvp", "raid", "pcu", "sun", "usage", "mods", "motd", "restart", "stop", "perms", "cleanup"
	];

	public static readonly PermissionInfo[] All = Build_();

	private static readonly Dictionary<string, PermissionInfo> ByKey = All.ToDictionary(p => p.Key, StringComparer.Ordinal);

	public static PermissionInfo Find(string key) => key != null && ByKey.TryGetValue(key, out var info) ? info : null;

	public static IEnumerable<string> Keys => ByKey.Keys;

	private static PermissionInfo[] Build_()
	{
		var list = new List<PermissionInfo>();
		void K(string key, bool def, string category, string description, string where, params string[] related)
			=> list.Add(new PermissionInfo(key, def, category, description, null, where, related));
		const string PanelCheck = "On the server, for the admin panel action";
		K("join.bypass_password", false, Join, "Join without the server password.",
			"On the server, when the player connects to a server that has a join password.", "join.bypass_whitelist", "join.bypass_full");
		K("join.bypass_whitelist", false, Join, "Join while the whitelist is on without being on it.",
			"On the server, when the player connects while the whitelist is on.", "admin.whitelist", "join.bypass_password");
		K("join.bypass_full", false, Join, "Join a full server (a reserved slot).",
			"On the server, when the player connects and every player slot is taken.", "join.bypass_password");
		K("join.ban_immune", false, Join, "Bans do not keep this player out.",
			"On the server, when a banned player connects (the ban stays on the list).", "admin.players.ban", "admin.players.unban");
		K("chat.send", true, Chat, "Send chat messages.",
			"On the server, for every chat message the player sends.", "chat.no_throttle", "chat.commands");
		K("chat.no_throttle", false, Chat, "No chat rate limit.",
			"On the server, the chat rate limit on each message.", "chat.send");
		K("chat.commands", true, Chat, "Run /commands in chat (each command also needs its command key).",
			"On the server, when a chat line starts with a slash, before the command's own key.", "chat.send", "command.help");
		K("voice.speak", true, Chat, "Speak on proximity voice.",
			"On the server, which stops relaying the player's voice to others.", "chat.send");
		K("rcon.connect", false, Chat, "Sign in to the remote console (RCON) with Steam.",
			"On the server, at an RCON Steam sign-in and again before every RCON command. The RCON password is an owner and skips it.", "command.status", "command.json");
		K("mods.check", false, Chat, "Make a dedicated server check the Workshop for mod updates now (mods check); a newer version restarts the server after the usual warnings.",
			"On the server, when 'mods check' comes from chat or a Steam RCON sign-in (the console always may).", "command.mods");
		foreach (string verb in CommandVerbs)
			K("command." + verb, verb is "help" or "status" or "players", Chat,
				$"Run the '{verb}' command ({(CommandRouter.ChatVerbs.Contains(verb) ? $"chat /{verb}, RCON, console" : "RCON and the server console, not chat")}).",
				"On the server, when the command comes from chat or a Steam RCON sign-in. The server console and the RCON password are owners.", "chat.commands", "rcon.connect");
		K("admin.panel", false, Panel, "Open the admin panel (each page and button needs its own key).",
			"In the player's game (Ctrl+F10 opens the panel only with it) and on the server, which also lists the player as an admin (RCON role Admin).", "admin.players.kick", "admin.world.pvp");
		K("admin.players.kick", false, Panel, "Kick players of a lower rank.",
			PanelCheck + " and the kick command; the target must rank below the actor.", "command.kick", "admin.players.ban");
		K("admin.players.ban", false, Panel, "Ban players of a lower rank.",
			PanelCheck + " and the ban command; the target must rank below the actor.", "command.ban", "admin.players.unban", "join.ban_immune");
		K("admin.players.unban", false, Panel, "Lift bans.",
			PanelCheck + " and the unban command (any rank).", "command.unban", "admin.players.view_bans");
		K("admin.players.set_group", false, Panel, "Put players of a lower rank into a group up to one's own rank (admin, deadmin, group set).",
			PanelCheck + " (Perms page), the admin, deadmin and group commands and perms check; the rank rule applies.", "command.group", PermissionSet.SetGroupScope + "*");
		K("admin.players.view_ids", false, Panel, "See players' SteamIDs.",
			"On the server, which sends SteamIDs in the panel data only to players with this or a key that acts on players (kick, ban, set_group, kill, teleport, give to others, whitelist).", "admin.players.kick");
		K("admin.players.view_bans", false, Panel, "See the ban list.",
			"On the server, which sends the ban list in the panel data only with this key.", "admin.players.unban", "command.bans");
		K("admin.players.kill", false, Panel, "Kill the character of a player of a lower rank.",
			PanelCheck + "; the target must rank below the actor (killing oneself is always allowed).", "admin.players.kick");
		K("admin.whitelist", false, Panel, "See and change the whitelist.",
			PanelCheck + " and the whitelist command.", "command.whitelist", "join.bypass_whitelist");
		K("admin.world.pvp", false, Panel, "Turn PvP on and off.", PanelCheck + " and the pvp command.", "command.pvp", "pvp.damage_players");
		K("admin.world.raid", false, Panel, "Turn offline raid protection on and off.", PanelCheck + " and the raid command.", "command.raid");
		K("admin.world.pcu", false, Panel, "Set the per-player PCU limit.", PanelCheck + " and the pcu command.", "command.pcu", "limits.pcu");
		K("admin.world.sun", false, Panel, "Control the sun.", PanelCheck + " and the sun command.", "command.sun", "game.admin.sun");
		K("admin.world.motd", false, Panel, "Set the message of the day.", PanelCheck + " and the motd command.", "command.motd");
		K("admin.world.usage", false, Panel, "Change energy, hydrogen and oxygen usage.", PanelCheck + " and the usage command.", "command.usage");
		K("admin.creative.tools", false, Panel, "Creative tools for oneself: build and paste without resources (no god mode).",
			PanelCheck + "; the server also turns the toggle off again once the player loses the key (demotion, perms reload).", "admin.creative.godmode", "game.admin.creative");
		K("admin.creative.godmode", false, Panel, "God mode for oneself: no damage, health, energy and oxygen never run out.",
			PanelCheck + "; the server also turns the toggle off again once the player loses the key.", "admin.creative.tools", "game.admin.godmode");
		K("admin.access_bypass", false, Panel, "The access bypass toggle for oneself: use anything regardless of ownership.",
			PanelCheck + "; the server also turns the toggle off again once the player loses the key.", "use.terminal", "use.inventory");
		K("admin.spawn.prefab", false, Panel, "Spawn prefabs.", PanelCheck + " (Spawn page).", "game.admin.spawn");
		K("admin.spawn.item", false, Panel, "Give items to oneself.", PanelCheck + " (Spawn page).", "admin.spawn.item_others");
		K("admin.spawn.item_others", false, Panel, "Give items to other players.", PanelCheck + " (Spawn page), any rank.", "admin.spawn.item");
		K("admin.safezone.create", false, Panel, "Create safe zones.", PanelCheck + " (Safe zones page).", "admin.safezone.edit", "game.admin.safezones");
		K("admin.safezone.edit", false, Panel, "Resize safe zones.", PanelCheck + " (Safe zones page).", "admin.safezone.create", "admin.safezone.delete");
		K("admin.safezone.delete", false, Panel, "Delete safe zones.", PanelCheck + " (Safe zones page).", "admin.safezone.edit");
		K("admin.teleport.to", false, Panel, "Teleport to a player.", PanelCheck + ", any rank.", "admin.teleport.bring", "game.admin.teleport");
		K("admin.teleport.bring", false, Panel, "Bring a player to oneself.", PanelCheck + ", any rank.", "admin.teleport.to");
		K("admin.entities.list", false, Panel, "List grids (Entities page).", PanelCheck + " (Entities page).", "admin.entities.teleport", "admin.entities.delete");
		K("admin.entities.teleport", false, Panel, "Teleport to a grid.", PanelCheck + " (Entities page).", "admin.entities.list");
		K("admin.entities.delete", false, Panel, "Delete a grid and everything attached to it.", PanelCheck + " (Entities page).", "admin.entities.list", "game.admin.delete");
		K("admin.entities.stop", false, Panel, "Stop a grid moving.", PanelCheck + " (Entities page).", "admin.entities.list");
		K("admin.entities.transfer", false, Panel, "Give a grid to another player.", PanelCheck + " (Entities page).", "admin.entities.list", "ownership.transfer");
		K("admin.entities.power", false, Panel, "Turn a grid's blocks off.", PanelCheck + " (Entities page).", "admin.entities.list");
		K("admin.cleanup", false, Panel, "Preview the cleanup on the admin panel.",
			PanelCheck + " (Cleanup page preview) and cleanup preview.", "cleanup.run", "cleanup.configure", "command.cleanup");
		K("cleanup.run", false, Cleanup, "Start or cancel a cleanup pass now.",
			"On the server, for the Cleanup page's run and cancel buttons and cleanup run or cleanup cancel.", "admin.cleanup", "command.cleanup");
		K("cleanup.configure", false, Cleanup, "Change the cleanup settings on the admin panel.",
			"On the server, when cleanup settings are saved from the Cleanup page.", "admin.cleanup");
		K("cleanup.exempt", false, Cleanup, "This player's grids and items are never cleaned up.",
			"On the server, during every cleanup pass: the player's grids are skipped.", "cleanup.run");
		foreach (var (key, what) in new[]
		{
			("teleport", "teleport entities and players"), ("delete", "delete entities, stop their movement"), ("kill", "kill characters"),
			("inventory", "add, read and use inventory items"), ("credits", "change credits"), ("spawn", "spawn entities and characters"),
			("creative", "creative mode switches"), ("godmode", "invulnerability switches"), ("safezones", "safe zone tools"), ("voxelhand", "voxel hand"),
			("sun", "sun tools"), ("pause", "pause the game"), ("destruction", "destruction on or off"), ("npc_ownership", "make grids NPC-owned"),
			("fasttravel", "debug fast travel"), ("encounters", "encounter and contract debugging"), ("colonization", "colonization and sectors"),
			("discoveries", "discoveries and datapads"), ("naming", "rename entities"), ("procedural", "procedural generation"), ("replay", "replay recording"),
			("save_variant", "save variants and publishing"), ("debug", "every other debug tool, and game tools this version does not know"),
			("conditional", "admin-only options inside normal requests (respawn, emotes, toolbars, guns)")
		})
			K("game.admin." + key, false, Keen, "The game's admin tools: " + what + ".",
				key == "conditional" ? "On the server, inside ordinary player requests that carry an admin-only option."
					: "On the server, when the player's game sends one of the game's own admin requests (the F12 debug menu and admin screens).",
				"debugmenu.open", key == "debug" ? "game.admin.conditional" : "game.admin.debug");
		K("bypass.paste_once", false, Bypass, "Skip the one-paste check on blueprints.",
			"On the server, the check that one copied blueprint is pasted only once.", "blueprint.paste", "bypass.requests");
		K("bypass.requests", false, Bypass, "Skip the request checks (spawning, copying grids without access, deposit all, autosave rate).",
			"On the server, the checks on players' spawn, copy, deposit all and autosave requests.", "bypass.paste_once", "use.deposit_all");
		K("bypass.ownership_initiator", false, Bypass, "Start ownership transfers in another player's name.",
			"On the server, on ownership transfer requests that name another player as the giver.", "ownership.transfer");
		K("bypass.foreign_character", false, Bypass, "Requests that name another player's character.",
			"On the server, on requests that act on a character that is not the player's own.", "bypass.requests");
		K("build.place", true, Build, "Place blocks on grids and projections.",
			"On the server, on each block placement request.", "build.place.new_grid", "build.remove");
		K("build.place.new_grid", true, Build, "Place a block as a new grid.",
			"On the server, on a placement that starts a new grid.", "build.place");
		K("build.remove", true, Build, "Remove blocks.",
			"On the server, on block removal requests (single blocks, volumes and projected blocks).", "build.place", "build.grind");
		K("build.weld", true, Build, "Weld with a hand welder.",
			"On the server: a welder used by the player stops welding.", "build.grind");
		K("build.grind", true, Build, "Grind with a hand grinder.",
			"On the server: a grinder used by the player stops grinding.", "build.weld", "build.remove");
		K("build.paint", true, Build, "Paint blocks.", "On the server: the paint tool does nothing for the player.", "build.place");
		K("blueprint.copy", true, Build, "Copy grids to blueprints.",
			"On the server, when the player copies a grid (clipboard or blueprint).", "blueprint.paste");
		K("blueprint.paste", true, Build, "Paste blueprints.",
			"On the server, on each paste request.", "blueprint.max_blocks", "bypass.paste_once");
		K("voxel.drill", true, Build, "Drill voxels.", "On the server: drills the player owns stop digging.", "build.grind");
		K("ownership.transfer", true, Build, "Give own grids to other players.",
			"On the server, on grid ownership transfers (Grid ownership screen and terminal).", "admin.entities.transfer", "bypass.ownership_initiator");
		list.Add(new PermissionInfo("limits.pcu", false, Build, "PCU limit for this group's players (counted per faction pool, as the game counts PCU); -1 uses the server's limit.", -1,
			"On the server, the game's PCU check for each block the player places. It only lowers the server's own limit, never raises it.", ["admin.world.pcu", "command.pcu"]));
		list.Add(new PermissionInfo("blueprint.max_blocks", false, Build, "Most blocks one pasted blueprint may have; -1 is no limit.", -1,
			"On the server, on each paste request: a bigger blueprint is refused.", ["blueprint.paste"]));
		K("use.terminal", true, Use, "Open terminals and control panels (still only where ownership allows).",
			"On the server, when the player opens or changes a terminal; ownership and sharing still apply.", "use.inventory", "admin.access_bypass");
		K("use.inventory", true, Use, "Open inventories and move items (still only where ownership allows).",
			"On the server, on inventory transfers; ownership and sharing still apply.", "use.terminal", "use.deposit_all");
		K("use.seat", true, Use, "Sit in seats and cockpits.", "On the server, when the player sits down.", "use.terminal");
		K("use.medbay", true, Use, "Bind to medical bays.", "On the server, when the player binds to a medbay or respawns at one.", "respawn.home.set");
		K("use.toolbar", true, Use, "Press button panels and block toolbars.", "On the server, on button panel and block toolbar requests.", "use.terminal");
		K("use.deposit_all", true, Use, "Deposit all items into a container.", "On the server, on deposit all requests.", "use.inventory", "bypass.requests");
		K("trade.players", true, Use, "Trade with other players.", "On the server, on trade requests.", "use.inventory");
		K("faction.create", true, Use, "Create factions.", "On the server, on faction requests from the Factions screen.", "faction.join", "faction.manage");
		K("faction.join", true, Use, "Join factions and answer invitations.", "On the server, on faction requests from the Factions screen.", "faction.create");
		K("faction.manage", true, Use, "Manage a faction (invite, roles, diplomacy) as its ranks allow.", "On the server, on faction requests from the Factions screen.", "faction.join");
		K("gps.share", true, Use, "Share GPS markers.", "On the server, when the player shares a GPS marker.", "chat.send");
		K("ping.use", true, Use, "Ping a spot with the ping key (a marker the faction or nearby players see for a while).", "On the server, on every ping, before the rate limit and range checks.", "gps.share");
		K("emote.use", true, Use, "Play emotes and punch.", "On the server, on emote and punch requests.", "game.admin.conditional");
		K("hack.use", true, Use, "Hack blocks (the server's hacking rules apply first).",
			"On the server, when a hack starts (Network.Hacking and the hacking rules come first).", "pvp.damage_players");
		K("pvp.damage_players", true, Use, "Damage other players while PvP is on.",
			"On the server, on damage the player deals to other players while PvP is on.", "admin.world.pvp");
		K("fasttravel.use", true, Use, "Fast travel between fast travel fields.",
			"On the server: the fast travel request is answered as an invalid destination.", "game.admin.fasttravel");
		K("respawn.home.set", true, Use, "Make a medbay you may respawn at your home base.",
			"On the server, when the player picks a medbay as home base.", "respawn.home.use", "use.medbay");
		K("respawn.home.use", true, Use, "Respawn at your home base.", "On the server, when the player respawns at the home base.", "respawn.home.set");
		K("debugmenu.open", true, Use, "Open the game's F12 debug menu (every debug action is still checked as game.admin.*).",
			"In the player's game: F12 does not open the debug menu. The server checks every debug action as game.admin.* anyway.", "game.admin.debug");
		return list.ToArray();
	}
}
