using System;
using System.Threading;
using System.Linq;
using Keen.Game2.Simulation.GameSystems.Ownership;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.DCS.Components;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

internal static class TeamPermissions
{
	public static long Overrides;

	public static IMpLog? Log;

	internal static Func<Entity?, long>? OwnerOverride;

	internal static long OwnerOf(Entity? e)
	{
		if (OwnerOverride != null)
		{
			return OwnerOverride(e);
		}
		// The nearest ownership up the hierarchy (a block's grid, a subgrid's parent grid).
		for (int depth = 0; e != null && depth < 64; depth++)
		{
			OwnershipComponent ownership = e.TryGet<OwnershipComponent>();
			if (ownership != null)
			{
				return ownership.Owner;
			}
			e = e.TryGet<HierarchyComponent>()?.Parent;
		}
		return 0L;
	}

	// Where SE2 Online has no answer (NPC and unowned grids, "everyone may use" blocks, characters) the game's own rule
	// stands. Up to 0.18.0-pre4 team grids gave every player the owner's rights there: IsOwner said yes for another
	// player's character, and an "everyone may use" block opened its control panel to everyone.
	public static void IsOwnerPostfix(Entity entity, IdentityId owner, ref bool __result) => ApplyAccess(entity, owner, ref __result);

	public static void CanPlaceBlockPostfix(Entity target, IdentityId identity, ref bool __result) => ApplyAccess(target, identity, ref __result);

	public static void CanUseBlockActionPostfix(Entity block, Entity grid, IdentityId identity, ref bool __result) => ApplyAccess(block, identity, ref __result);

	public static void CanLinkGridsPostfix(Entity firstGrid, Entity secondGrid, ref bool __result) => ApplyLink(firstGrid, secondGrid, false, ref __result);

	public static void CanMergeGridsPostfix(Entity firstGrid, Entity secondGrid, ref bool __result) => ApplyLink(firstGrid, secondGrid, true, ref __result);

	private static bool ApplyAccess(Entity target, long actor, ref bool result)
	{
		if (AccessBypass.IsOn(actor)) { result = true; return true; }
		bool? access = WorldSocial.Access(target, actor);
		if (!access.HasValue) return false;
		if (access.Value && !result) Interlocked.Increment(ref Overrides);
		result = access.Value;
		return true;
	}

	private static bool ApplyLink(Entity first, Entity second, bool merge, ref bool result)
	{
		bool? firstAccess = WorldSocial.Access(first, OwnerOf(second));
		bool? secondAccess = WorldSocial.Access(second, OwnerOf(first));
		if (!firstAccess.HasValue || !secondAccess.HasValue) return false;
		result = first == second ? !merge : firstAccess.Value && secondAccess.Value;
		return true;
	}

	public static void IsFriendlyPostfix(IdentityId source, Entity target, ref bool __result)
	{
		long owner = OwnerOf(target);
		if (!PlayerIdentities.IsHuman(owner) || !PlayerIdentities.IsHuman(source)) return;
		__result = owner == (long)source || SameOnlineFaction(owner, (long)source);
	}

	/// <summary>Both identities are members of one SE2 Online faction (this game's view: the host's state, or a client's snapshot).</summary>
	internal static bool SameOnlineFaction(long a, long b)
	{
		if (ClientWorld.Current?.BoundSession is { } client)
			return client.Social?.Factions.Any(f => f.Members.Contains(a) && f.Members.Contains(b)) ?? false;
		if (HostWorld.Current?.Social is { } social)
			lock (social.Sync) return social.State.Factions.Any(f => f.Members.Contains(a) && f.Members.Contains(b));
		return false;
	}

	/// <summary>The SE2 Online faction of an identity, as this game sees it (like <see cref="SameOnlineFaction"/>); null without one.</summary>
	internal static (string Id, long[] Members)? OnlineFactionOf(long identity)
	{
		if (ClientWorld.Current?.BoundSession is { } client)
			return client.Social?.Factions.FirstOrDefault(f => f.Members.Contains(identity)) is { } mine ? (mine.Id, mine.Members.ToArray()) : null;
		if (HostWorld.Current?.Social is { } social)
			lock (social.Sync) return social.State.FactionOf(identity) is { } own ? (own.Id, own.Members.ToArray()) : null;
		return null;
	}

	// SE2 4.0 puts every player into one default faction, and its permission rule gives a faction member the owner's
	// access: every player owned every grid, was friendly to every turret and filled one shared PCU pool. In an SE2
	// Online session the faction is the player's SE2 Online faction. Research keeps the default faction: 4.0 stores
	// unlocks on it, and SE2 Online's shared/own research setting decides who gets them.
	public static bool InSameFactionPrefix(IdentityId playerA, IdentityId playerB, ref bool __result)
	{
		if (!AdminAccess.InSession) return true;
		__result = (long)playerA == (long)playerB || SameOnlineFaction((long)playerA, (long)playerB);
		return false;
	}

	public static bool PcuPoolPrefix(IdentityId owner, ref IdentityId __result)
	{
		if (!AdminAccess.InSession) return true;
		__result = owner;
		return false;
	}
}
