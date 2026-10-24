using System;
using System.Collections.Generic;
using System.Linq;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.Sync;

public enum CleanupCategory { FloatingItems, Debris, NpcGrids, PlayerGrids }

/// <summary>What the cleanup knows about one floating item or one whole grid group when it decides.</summary>
public sealed record CleanupFacts
{
	public CleanupCategory Category { get; init; }
	public string Name { get; init; } = "";
	/// <summary>Owners of every grid in the group (0 nobody, NPC identities, players).</summary>
	public long[] Owners { get; init; } = Array.Empty<long>();
	public int Blocks { get; init; }
	public bool Powered { get; init; }
	public bool Occupied { get; init; }
	/// <summary>The game pins it (a contract or world area is using it).</summary>
	public bool Pinned { get; init; }
	public double NearestPlayerMeters { get; init; } = double.MaxValue;
	/// <summary>Minutes since the cleanup first saw it (items, debris) or since a player was last near (NPC grids).</summary>
	public double AgeMinutes { get; init; }
	/// <summary>Minutes since the most recently active of the owners (and with FactionShared their factions) was online; PlayerGrids only.</summary>
	public double OwnersInactiveMinutes { get; init; }
	/// <summary>An owner has the cleanup.exempt permission.</summary>
	public bool OwnerExempt { get; init; }
}

/// <summary>The cleanup's decisions, apart from the game so they are unit tested.</summary>
public static class CleanupRules
{
	public static bool AnyHuman(IEnumerable<long> owners) => owners.Any(PlayerIdentities.IsHuman);

	/// <summary>Which category a grid group falls in by its owners: any player-owned grid makes the whole group a player
	/// group (an NPC grid a player took over included); otherwise NPC if any grid is NPC-owned; otherwise debris.</summary>
	public static CleanupCategory GroupCategory(IReadOnlyCollection<long> owners)
		=> AnyHuman(owners) ? CleanupCategory.PlayerGrids : owners.Any(PlayerIdentities.IsNpc) ? CleanupCategory.NpcGrids : CleanupCategory.Debris;

	/// <summary>Why this goes in a pass, or null to keep it.</summary>
	public static string Why(CleanupSettings s, CleanupFacts f)
	{
		if (!s.Enabled) return null;
		if (f.Occupied || f.OwnerExempt) return null;
		if (s.KeepTag.Length > 0 && f.Name.Contains(s.KeepTag, StringComparison.OrdinalIgnoreCase)) return null;
		if (f.NearestPlayerMeters < s.ProtectWithinMeters) return null;
		bool human = AnyHuman(f.Owners);
		switch (f.Category)
		{
		case CleanupCategory.FloatingItems:
			if (!s.FloatingItems.Enabled || f.AgeMinutes < s.FloatingItems.MinAgeMinutes || f.NearestPlayerMeters < s.FloatingItems.MinDistanceMeters) return null;
			return $"floating item older than {s.FloatingItems.MinAgeMinutes:0} min, no player within {s.FloatingItems.MinDistanceMeters:0} m";
		case CleanupCategory.Debris:
			// Only grids nobody owns: a player's or an NPC's grid is never debris, however small.
			if (!s.Debris.Enabled || f.Owners.Any(o => o != 0) || f.Blocks > s.Debris.MaxBlocks || (s.Debris.RequireUnpowered && f.Powered)
				|| f.AgeMinutes < s.Debris.MinAgeMinutes || f.NearestPlayerMeters < s.Debris.MinDistanceMeters || f.Pinned) return null;
			return $"unowned debris ({f.Blocks} block{(f.Blocks == 1 ? "" : "s")}{(f.Powered ? "" : ", unpowered")}), no player within {s.Debris.MinDistanceMeters:0} m";
		case CleanupCategory.NpcGrids:
			if (!s.NpcGrids.Enabled || human || !f.Owners.Any(PlayerIdentities.IsNpc) || f.Pinned || f.AgeMinutes < s.NpcGrids.IdleMinutes || f.NearestPlayerMeters < s.NpcGrids.MinDistanceMeters) return null;
			return $"NPC grid with no player near for {Duration(f.AgeMinutes)} (rule: {Duration(s.NpcGrids.IdleMinutes)})";
		case CleanupCategory.PlayerGrids:
			if (!s.PlayerGrids.Enabled || !human || f.OwnersInactiveMinutes < s.PlayerGrids.InactiveMinutes || f.NearestPlayerMeters < s.PlayerGrids.MinDistanceMeters) return null;
			return $"owner{(f.Owners.Count(PlayerIdentities.IsHuman) == 1 ? "" : "s")}{(s.PlayerGrids.FactionShared ? " and faction" : "")} offline for {Duration(f.OwnersInactiveMinutes)} (rule: {Duration(s.PlayerGrids.InactiveMinutes)})";
		default: return null;
		}
	}

	/// <summary>
	/// Minutes since the owners were last active: the most recent last-seen among every player owner (and, with
	/// <paramref name="factionShared"/>, every member of their factions). A player the server has no record of counts from
	/// <paramref name="firstTracked"/>, the time the cleanup first saw that owner, so nothing goes right after an update.
	/// </summary>
	public static double InactiveMinutes(IEnumerable<long> owners, bool factionShared, long nowMs, Func<long, long?> lastSeenMs,
		Func<long, IEnumerable<long>> factionMates, Func<long, long> firstTracked, Func<long, bool> online)
	{
		var people = new HashSet<long>(owners.Where(PlayerIdentities.IsHuman));
		if (factionShared) foreach (long owner in people.ToList()) foreach (long mate in factionMates(owner) ?? Enumerable.Empty<long>()) if (PlayerIdentities.IsHuman(mate)) people.Add(mate);
		if (people.Count == 0) return 0;
		long latest = long.MinValue;
		foreach (long person in people)
		{
			if (online(person)) return 0;
			long seen = Math.Max(lastSeenMs(person) ?? long.MinValue, firstTracked(person));
			latest = Math.Max(latest, seen);
		}
		return Math.Max(0, (nowMs - latest) / 60000.0);
	}

	public static string Duration(double minutes)
		=> minutes >= 2880 ? $"{minutes / 1440:0.#} days" : minutes >= 120 ? $"{minutes / 60:0.#} hours" : $"{minutes:0} min";

	public static bool TryCategory(string text, out CleanupCategory? category)
	{
		category = null;
		if (string.IsNullOrWhiteSpace(text) || text.Equals("all", StringComparison.OrdinalIgnoreCase)) return true;
		foreach (CleanupCategory c in Enum.GetValues<CleanupCategory>())
			if (c.ToString().Equals(text.Trim(), StringComparison.OrdinalIgnoreCase) || c.ToString().StartsWith(text.Trim(), StringComparison.OrdinalIgnoreCase)) { category = c; return true; }
		return false;
	}
}
