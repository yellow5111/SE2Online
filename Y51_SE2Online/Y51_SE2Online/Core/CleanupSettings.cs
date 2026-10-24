using System;
using System.Text.Json.Serialization;

namespace Y51_SE2Online.Core;

/// <summary>
/// SE2 Online's own cleanup (server.json "Cleanup"; a co-op host's Y51_SE2Online.cleanup.json). Player grids go only when
/// their owner (and, with FactionShared, every member of the owner's faction) has not been online for the set time;
/// nothing else ever removes them. NPC grids go after the same kind of idle time with no player near; an NPC grid a player
/// took over is a player grid. Defaults: only old floating items and tiny unowned, unpowered debris.
/// </summary>
public sealed class CleanupSettings
{
	public bool Enabled { get; set; } = true;
	/// <summary>Log and announce what would go, remove nothing.</summary>
	public bool DryRun { get; set; }
	/// <summary>Minutes between passes (10-1440).</summary>
	public int EveryMinutes { get; set; } = 60;
	/// <summary>Chat warning this many seconds before a pass removes anything, while players are online (0-600).</summary>
	public int WarnSeconds { get; set; } = 60;
	/// <summary>Dedicated servers: save the world before a pass that removes grids; a failed save cancels the pass.</summary>
	public bool SaveBefore { get; set; }
	/// <summary>At most this many removals per second (1-50).</summary>
	public int MaxRemovalsPerSecond { get; set; } = 5;
	/// <summary>Nothing within this distance of a player is touched (50-100000 m).</summary>
	public double ProtectWithinMeters { get; set; } = 300;
	/// <summary>A grid whose name contains this (any case) is never cleaned up; empty turns the tag off.</summary>
	public string KeepTag { get; set; } = "[KEEP]";
	/// <summary>The game's own PCU trash removal: "npc-only" (it may trim NPC grids only, never a grid group a player owns
	/// any part of; unowned debris is the Debris rule's) or "off". It never removes player grids either way.</summary>
	public string VanillaGridPcu { get; set; } = "npc-only";
	// npc-only: the game's cleaner trims NPC grids only (its "player" bucket, player and unowned grids, is off).

	[JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
	public FloatingItemRule FloatingItems { get; set; } = new();
	[JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
	public DebrisRule Debris { get; set; } = new();
	[JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
	public NpcGridRule NpcGrids { get; set; } = new();
	[JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
	public PlayerGridRule PlayerGrids { get; set; } = new();

	public sealed class FloatingItemRule
	{
		public bool Enabled { get; set; } = true;
		public double MinAgeMinutes { get; set; } = 15;
		public double MinDistanceMeters { get; set; } = 100;
	}

	/// <summary>Grids nobody owns (owner 0: bits that broke off, abandoned scraps), never a player's or an NPC's.</summary>
	public sealed class DebrisRule
	{
		public bool Enabled { get; set; } = true;
		public int MaxBlocks { get; set; } = 5;
		public bool RequireUnpowered { get; set; } = true;
		public double MinAgeMinutes { get; set; } = 30;
		public double MinDistanceMeters { get; set; } = 300;
	}

	/// <summary>NPC-owned grid groups no player has been near for the idle time (never a group a player owns any part of).</summary>
	public sealed class NpcGridRule
	{
		public bool Enabled { get; set; }
		public double IdleDays { get; set; } = 7;
		public double IdleHours { get; set; }
		public double MinDistanceMeters { get; set; } = 1000;
		[JsonIgnore] public double IdleMinutes => IdleDays * 1440 + IdleHours * 60;
	}

	/// <summary>Player-owned grid groups whose owners have all been offline for the inactive time.</summary>
	public sealed class PlayerGridRule
	{
		public bool Enabled { get; set; }
		public double InactiveDays { get; set; } = 30;
		public double InactiveHours { get; set; }
		/// <summary>An owner in a faction counts as active while any member of the faction is.</summary>
		public bool FactionShared { get; set; } = true;
		public double MinDistanceMeters { get; set; } = 1000;
		[JsonIgnore] public double InactiveMinutes => InactiveDays * 1440 + InactiveHours * 60;
	}

	/// <summary>Null when valid, else what to fix (one message, the first problem).</summary>
	public string Validate()
	{
		FloatingItems ??= new(); Debris ??= new(); NpcGrids ??= new(); PlayerGrids ??= new();
		KeepTag = (KeepTag ?? "").Trim();
		VanillaGridPcu = (VanillaGridPcu ?? "npc-only").Trim().ToLowerInvariant();
		if (EveryMinutes is < 10 or > 1440) return "Cleanup.EveryMinutes must be between 10 and 1440.";
		if (WarnSeconds is < 0 or > 600) return "Cleanup.WarnSeconds must be between 0 and 600.";
		if (MaxRemovalsPerSecond is < 1 or > 50) return "Cleanup.MaxRemovalsPerSecond must be between 1 and 50.";
		if (!(ProtectWithinMeters >= 50 && ProtectWithinMeters <= 100_000)) return "Cleanup.ProtectWithinMeters must be between 50 and 100000.";
		if (KeepTag.Length > 32) return "Cleanup.KeepTag is at most 32 characters.";
		if (VanillaGridPcu is not ("npc-only" or "off")) return "Cleanup.VanillaGridPcu must be \"npc-only\" or \"off\" (the game never removes player grids).";
		if (!InRange(FloatingItems.MinAgeMinutes, 1, 10080) || !InRange(FloatingItems.MinDistanceMeters, 0, 100_000)) return "Cleanup.FloatingItems: MinAgeMinutes 1-10080, MinDistanceMeters 0-100000.";
		if (Debris.MaxBlocks is < 1 or > 50 || !InRange(Debris.MinAgeMinutes, 1, 10080) || !InRange(Debris.MinDistanceMeters, 0, 100_000)) return "Cleanup.Debris: MaxBlocks 1-50, MinAgeMinutes 1-10080, MinDistanceMeters 0-100000.";
		if (!InRange(NpcGrids.IdleDays, 0, 365) || !InRange(NpcGrids.IdleHours, 0, 8760) || (NpcGrids.Enabled && NpcGrids.IdleMinutes < 60) || !InRange(NpcGrids.MinDistanceMeters, 0, 100_000)) return "Cleanup.NpcGrids: IdleDays 0-365 and IdleHours 0-8760, together at least 1 hour; MinDistanceMeters 0-100000.";
		if (!InRange(PlayerGrids.InactiveDays, 0, 3650) || !InRange(PlayerGrids.InactiveHours, 0, 87600) || (PlayerGrids.Enabled && PlayerGrids.InactiveMinutes < 24 * 60) || !InRange(PlayerGrids.MinDistanceMeters, 0, 100_000)) return "Cleanup.PlayerGrids: InactiveDays 0-3650 and InactiveHours 0-87600, together at least 24 hours; MinDistanceMeters 0-100000.";
		return null;
	}

	private static bool InRange(double value, double min, double max) => value >= min && value <= max;

	public CleanupSettings Copy() => System.Text.Json.JsonSerializer.Deserialize<CleanupSettings>(System.Text.Json.JsonSerializer.Serialize(this));
}
