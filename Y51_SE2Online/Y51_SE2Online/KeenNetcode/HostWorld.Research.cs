using System;
using System.Linq;
using Keen.Game2.Simulation.GameSystems.Colonization;
using Keen.Game2.Simulation.GameSystems.Contracts;
using Keen.Game2.Simulation.GameSystems.Economy;
using Keen.Game2.Simulation.GameSystems.Identities;
using Keen.Game2.Simulation.GameSystems.Ownership;
using Keen.Game2.Simulation.GameSystems.Player;
using Keen.Game2.Simulation.GameSystems.Progression;
using Keen.VRage.Core.Game.Systems;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

// Shared research (the default): every player reads and writes the world owner's progression, colonization and credits,
// as on 2.4. Contracts are not in the list: SE2 4.0 already shares accepted and finished contracts between all players
// (HostContractStorageSessionComponent), and its per-player record only holds which contracts that player focuses. Each player's record list and the per-player data the game replicates hold the owner's own
// objects, so clients find the shared research under their own identity without any change on their side.
internal sealed partial class HostWorld
{
	private static readonly Type[] SharedResearchTypes = { typeof(PlayerProgressionData), typeof(ColonizationProgressData), typeof(PlayerCreditsData) };

	private ResearchMode _research = ResearchMode.Shared;

	internal static bool ResearchShared => Current?._research == ResearchMode.Shared;

	internal static ResearchMode? HostResearch => Current?._research;

	private bool _progressionOff;

	internal static bool? HostProgressionOff => Current?._progressionOff;

	internal ResearchMode Contracts { get; set; }

	internal static ResearchMode HostContracts => Current?.Contracts ?? ResearchMode.Shared;

	private readonly System.Collections.Generic.Dictionary<long, string> _researchFaction = new();

	private readonly System.Collections.Generic.HashSet<long> _discoveriesCaughtUp = new();

	private void ShareResearch(Session session, IdentitiesSessionComponent identities)
	{
		if (_research == ResearchMode.Own) return;
		var data = session.SessionComponents.TryGet<InProcessPerPlayerDataSessionComponent>();
		if (data == null) return;
		if (_research == ResearchMode.Faction)
		{
			CatchUpFactions(session, data);
			return;
		}
		IdentityId owner = identities.LocalIdentity;
		foreach (long identity in _identityOf.Values)
		{
			if (identity == (long)owner || identity == 0) continue;
			int shared = 0;
			foreach (Type type in SharedResearchTypes) shared += Share(data, owner, new IdentityId(identity), type);
			if (shared > 0) _log.Info($"research: identity {identity} now shares {shared} research record(s) with the world owner {(long)owner}");
			// Discoveries stay per identity; while research is shared a new player gets everything anyone discovered.
			if (_discoveriesCaughtUp.Add(identity) && SharedDiscoveries.CatchUp(session, data, new IdentityId(identity)) is > 0 and int raised)
				_log.Info($"research: identity {identity} caught up on {raised} discoveries");
		}
	}

	// Faction research: joining a faction brings everything its members discovered; leaving keeps what the player saw.
	private void CatchUpFactions(Session session, InProcessPerPlayerDataSessionComponent data)
	{
		foreach (long identity in _identityOf.Values)
		{
			if (identity == 0) continue;
			var faction = TeamPermissions.OnlineFactionOf(identity);
			string id = faction?.Id ?? "";
			if (_researchFaction.TryGetValue(identity, out string was) && was == id) continue;
			_researchFaction[identity] = id;
			if (faction is { } f && SharedDiscoveries.CatchUp(session, data, new IdentityId(identity), f.Members) is > 0 and int raised)
				_log.Info($"research: identity {identity} caught up on {raised} discoveries of its faction");
		}
	}

	private static int Share(InProcessPerPlayerDataSessionComponent data, IdentityId owner, IdentityId identity, Type type)
	{
		object shared = data.GetPerIdentityData(type, owner);
		var records = data.GetOrCreateCollectionForIdentity(identity);
		var visible = data._clientVisibleData.Data.First(d => (long)d.IdentityId == (long)identity);
		int index = records.FindIndex(type.IsInstanceOfType);
		if (index >= 0 && ReferenceEquals(records[index], shared)) return 0;
		if (index >= 0)
		{
			visible.Data.Remove((PerIdentityData)records[index]);
			records[index] = shared;
		}
		else records.Add(shared);
		if (!visible.Data.Contains((PerIdentityData)shared)) visible.Data.Add((PerIdentityData)shared);
		return 1;
	}
}
