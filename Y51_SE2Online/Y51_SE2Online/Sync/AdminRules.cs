using System;
using System.Collections.Generic;

namespace Y51_SE2Online.Sync;

/// <summary>Which permission key each admin action, game admin tool and player request needs. Pure, so the decisions are
/// unit tested against the 0.17 rules.</summary>
public static class AdminRules
{
	/// <summary>The key an admin panel action needs, or null for one anybody may send (Refresh).</summary>
	public static string KeyFor(AdminRequest r) => r.Action switch
	{
		AdminAction.Refresh => null,
		AdminAction.Kick => "admin.players.kick",
		AdminAction.Ban => "admin.players.ban",
		AdminAction.Unban => "admin.players.unban",
		AdminAction.Promote or AdminAction.Demote or AdminAction.SetGroup => "admin.players.set_group",
		AdminAction.WhitelistAdd or AdminAction.WhitelistRemove or AdminAction.WhitelistOnly => "admin.whitelist",
		AdminAction.Creative => "admin.creative.tools",
		AdminAction.Invulnerable => "admin.creative.godmode",
		AdminAction.AccessBypass => "admin.access_bypass",
		AdminAction.SpawnPrefab => "admin.spawn.prefab",
		AdminAction.SpawnItem => r.Target == 0 ? "admin.spawn.item" : "admin.spawn.item_others",
		AdminAction.CreateSafeZone => "admin.safezone.create",
		AdminAction.DeleteSafeZone => r.Size > 0 ? "admin.safezone.edit" : "admin.safezone.delete",
		AdminAction.TeleportTo => "admin.teleport.to",
		AdminAction.Bring => "admin.teleport.bring",
		AdminAction.Kill => "admin.players.kill",
		AdminAction.Pvp => "admin.world.pvp",
		AdminAction.OfflineRaidProtection => "admin.world.raid",
		AdminAction.PcuLimit => "admin.world.pcu",
		AdminAction.SunRotation or AdminAction.SunPeriod or AdminAction.SunNudge or AdminAction.SunSet => "admin.world.sun",
		AdminAction.Motd => "admin.world.motd",
		AdminAction.Usage => "admin.world.usage",
		AdminAction.EntityQuery => "admin.entities.list",
		AdminAction.EntityAction => EntityKey((EntityVerb)r.Minutes),
		AdminAction.CleanupPreview => "admin.cleanup",
		AdminAction.CleanupRun => "cleanup.run",
		AdminAction.CleanupSettings => "cleanup.configure",
		AdminAction.PermsReload => "command.perms",
		AdminAction.PermsCheck => "admin.players.set_group",
		AdminAction.SetHome => r.Value ? "respawn.home.set" : null,
		AdminAction.PlayerInfo => "admin.players.info",
		_ => "admin.panel"
	};

	public static string EntityKey(EntityVerb verb) => verb switch
	{
		EntityVerb.TeleportTo => "admin.entities.teleport",
		EntityVerb.Stop => "admin.entities.stop",
		EntityVerb.Delete => "admin.entities.delete",
		EntityVerb.Transfer => "admin.entities.transfer",
		EntityVerb.PowerOff => "admin.entities.power",
		EntityVerb.Mark => "admin.players.info",
		_ => "admin.entities.list"
	};

	/// <summary>
	/// Why a non-owner <paramref name="actor"/> may not do <paramref name="r"/> (its Target a SteamID), or null: the action's
	/// key, then for actions on another player the rank rule (the actor must be ranked above the target, nobody touches an
	/// owner, nobody removes or demotes themselves).
	/// </summary>
	public static string Refusal(PermissionSet perms, ulong actor, AdminRequest r, Func<ulong, bool> isOwner, Func<ulong, string> name = null)
	{
		string key = KeyFor(r);
		if (key != null && !perms.Has(actor, key)) return $"Your group ({perms.GroupOf(actor)}) may not do that ({key}).";
		if (!RankRuled(r.Action) || r.Target == 0) return null;
		ulong target = r.Target;
		if (target == actor) return r.Action is AdminAction.Demote ? "You cannot demote yourself." : r.Action is AdminAction.Kill or AdminAction.PlayerInfo ? null : "You cannot remove yourself.";
		if (isOwner(target)) return "Nobody can do that to an owner of this server.";
		if (perms.RankOf(target) >= perms.RankOf(actor)) return $"Only someone ranked above {name?.Invoke(target) ?? target.ToString()} ({perms.GroupOf(target)}, rank {perms.RankOf(target)}) can do that.";
		return null;
	}

	/// <summary>Actions on another player that need the actor ranked above the target (the 0.17 "only the host can remove
	/// another admin" rule, by rank). Unban, teleport and give keep 0.17's rule: any rank. Player info shows a lower rank's
	/// details only (and one's own).</summary>
	public static bool RankRuled(AdminAction action) => action is AdminAction.Kick or AdminAction.Ban or AdminAction.Demote or AdminAction.Kill or AdminAction.PlayerInfo;

	/// <summary>
	/// The game admin tool key for a story or signal the game guards with ValidateAdminAccess, from its declaring type's
	/// simple name and the story, property or signal name. Anything not listed is game.admin.debug, the safe default for
	/// tools a game update adds.
	/// </summary>
	public static string GameAdminKey(string declaring, string member)
	{
		declaring ??= ""; member ??= "";
		switch (declaring)
		{
			case "EntityAdmin":
				return member switch
				{
					"TeleportEntity" or "TeleportToRandomPlanetLocation" or "TeleportPlayer" => "game.admin.teleport",
					"DeleteEntity" => "game.admin.delete",
					"KillCharacter" => "game.admin.kill",
					"SpawnItemIntoInventory" or "GetInventoryContents" or "AddConsumableToInventory" or "UseConsumableFromInventory" => "game.admin.inventory",
					"ModifyPlayerCredits" => "game.admin.credits",
					"RequestSpawnEntity" or "SpawnEntity" or "SpawnPrefab" => "game.admin.spawn",
					_ => "game.admin.debug"
				};
			case "PlayerAdmin": return "game.admin.spawn";
			case "InProcessGameServerSessionComponent":
				return member switch
				{
					"RequestDeleteEntity" or "RequestStopEntityMovement" => "game.admin.delete",
					"TrySaveVariant" or "RequestPrepareCurrentWorldForPublish" => "game.admin.save_variant",
					"RequestSpawnEntity" => "game.admin.spawn",
					_ => "game.admin.debug"
				};
			case "GameModeSessionComponent":
				return member switch
				{
					"SetCreative" or "CreativeGlobalProperty" or "CreativeChangedSignal" => "game.admin.creative",
					"SetInvulnerable" or "InvulnerableChangedSignal" or "InvulnerableProperty" => "game.admin.godmode",
					"IgnoreSafeZonesProperty" => "game.admin.safezones",
					_ => "game.admin.debug"
				};
			case "SafeZonesAdmin" or "SafeZoneComponent": return "game.admin.safezones";
			case "VoxelHandComponentBase" or "VoxelHelper": return "game.admin.voxelhand";
			case "SunSessionComponent": return "game.admin.sun";
			case "IPause" or "PauseSessionComponent": return "game.admin.pause";
			case "ManifoldCallbackDealerSessionComponent": return "game.admin.destruction";
			case "OwnershipSessionComponent": return "game.admin.npc_ownership";
			case "FastTravelServerSessionComponent": return "game.admin.fasttravel";
			case "EncounterSessionComponent" or "CargoShipEncounterSessionComponent" or "ContractSessionComponent" or "EncounterStatsSessionComponent": return "game.admin.encounters";
			case "ColonizationProgressServerSessionComponent" or "SectorComponent" or "SectorColonizationProgress" or "SectorsSessionComponent"
				or "ColonizationSectorProceduralGenerationDataSessionComponent": return "game.admin.colonization";
			case "DiscoveriesServerSessionComponent" or "DatapadServerSessionComponent": return "game.admin.discoveries";
			case "EntityNameSessionComponent": return "game.admin.naming";
			case "ProceduralGeneratorServerSessionComponent" or "ProceduralVolumeComponent": return "game.admin.procedural";
			case "ReplayRecordingSerializerSessionComponent": return "game.admin.replay";
			case "RespawnSessionComponent" or "EmoteControllerComponent" or "ProgressionSessionComponent" or "BlockToolbarServerComponent"
				or "PerIdentityData" or "GunToolComponent": return "game.admin.conditional";
			default: return "game.admin.debug";
		}
	}

	/// <summary>The (declaring type, member) of a replicated signal or property name as SE2 Online's engine writes it:
	/// "Namespace.Owner+Member" (nested type) or "Namespace.Owner.Member".</summary>
	public static (string Declaring, string Member) SplitSignal(string name)
	{
		if (string.IsNullOrEmpty(name)) return ("", "");
		int comma = name.IndexOf(',');
		if (comma >= 0) name = name[..comma];
		int plus = name.LastIndexOf('+');
		if (plus > 0)
		{
			string owner = name[..plus];
			return (owner[(owner.LastIndexOf('.') + 1)..].Split('+')[^1], name[(plus + 1)..]);
		}
		int dot = name.LastIndexOf('.');
		if (dot <= 0) return ("", name);
		string type = name[..dot];
		return (type[(type.LastIndexOf('.') + 1)..], name[(dot + 1)..]);
	}

	/// <summary>The key a player's building or blueprint request needs (all allowed by default), or null.</summary>
	public static string RequestKey(string declaring, string story) => (declaring, story) switch
	{
		(_, "PlaceOnGrid" or "PlaceOnProjection") => "build.place",
		(_, "PlaceStandalone") => "build.place.new_grid",
		(_, "DeleteCubeBlock" or "DeleteBlocksInVolume" or "DeleteProjectedBlock") => "build.remove",
		("BlockPlacerNetworkStories" or "BlockRemoverNetworkStories", "TryDelete") => "build.remove",
		(_, "TryCopy" or "PartialCopyGrid") => "blueprint.copy",
		("BlueprintHelpers", "SerializeGrid") => "blueprint.copy",
		("OwnershipSessionComponent", "RequestCreateOwnershipTransferStory") => "ownership.transfer",
		_ => null
	};

	/// <summary>The verb's key; help is always allowed (as in 0.17's RCON).</summary>
	public static string CommandKey(string verb) => PermissionFiles.CommandKey(verb);
}

/// <summary>What an AdminAction.EntityAction does to the grid named by its Target (the grid's NetId); the verb rides in Minutes.
/// Mark puts a GPS marker on the grid for the admin who asked.</summary>
public enum EntityVerb { None, TeleportTo, Stop, Delete, Transfer, PowerOff, Mark }
