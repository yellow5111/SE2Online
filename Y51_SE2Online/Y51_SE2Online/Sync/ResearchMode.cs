using System;
using System.Collections.Generic;
using System.Linq;

namespace Y51_SE2Online.Sync;

/// <summary>Who shares something a player earns: Shared = everyone, Faction = their SE2 Online faction, Own = the player alone.</summary>
public enum ResearchMode : byte
{
	Shared,
	Own,
	Faction
}

public static class Sharing
{
	public const string Everyone = "everyone", FactionMembers = "faction", Individual = "individual";

	public static bool TryParse(string? text, out ResearchMode mode)
	{
		switch (text?.Trim().ToLowerInvariant())
		{
			case Everyone: mode = ResearchMode.Shared; return true;
			case FactionMembers: mode = ResearchMode.Faction; return true;
			case Individual: mode = ResearchMode.Own; return true;
			default: mode = ResearchMode.Shared; return false;
		}
	}

	public static string Name(ResearchMode mode) => mode switch { ResearchMode.Own => Individual, ResearchMode.Faction => FactionMembers, _ => Everyone };

	/// <summary>The players who share with <paramref name="identity"/>: itself first, then every other player (Shared) or the other
	/// members of its faction (Faction, none without a faction), or nobody else (Own).</summary>
	public static List<long> With(ResearchMode mode, long identity, IEnumerable<long> players, IReadOnlyCollection<long> faction)
	{
		var result = new List<long> { identity };
		if (mode == ResearchMode.Own) return result;
		result.AddRange(players.Where(p => p != identity && (mode == ResearchMode.Shared || faction.Contains(p))).Distinct());
		return result;
	}
}
