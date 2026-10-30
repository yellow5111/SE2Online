using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Keen.Game2.Simulation.GameSystems.Colonization.Rewards;
using Keen.Game2.Simulation.GameSystems.Contracts;
using Keen.Game2.Simulation.GameSystems.Encounters;
using Keen.Game2.Simulation.GameSystems.Ownership;
using Keen.Game2.Simulation.GameSystems.Player;
using Keen.Game2.Simulation.GameSystems.Progression;
using Keen.VRage.Core.Game.Definitions;
using Keen.VRage.DCS.Definitions;
using Keen.VRage.Library.Extensions;
using Keen.VRage.Library.Localization;
using Keen.VRage.Library.Memory;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// The host's research, contract and progression settings on top of SE2 4.0, which keeps every block unlock on its one
/// default faction and every contract on one shared record, so by default everyone shares both.
/// Faction or individual research: new unlocks go to the player's own record, and a player may build what the default
/// faction holds (everything unlocked before), what they unlocked and, in faction mode, what a current member of their
/// SE2 Online faction unlocked. Leaving a faction keeps only the player's own unlocks.
/// Faction or individual contracts: accepting, failing, finishing and rewards reach the player (and their faction members),
/// and each player's own record keeps their contracts across a restart.
/// Progression off: the game's "is progression enabled" answers no, so every block counts as unlocked; nothing is erased.
/// </summary>
internal static class ProgressionSharing
{
    private static ResearchMode Research => HostWorld.HostResearch ?? ClientWorld.Current?.BoundSession?.Accept?.Settings.Research ?? ResearchMode.Shared;

    private static bool ProgressionOff => HostWorld.HostProgressionOff ?? ClientWorld.Current?.BoundSession?.Accept?.Settings.ProgressionOff ?? false;

    private static bool SplitContracts => HostWorld.HostContracts != ResearchMode.Shared;

    public static void Install(Harmony harmony)
    {
        var progression = typeof(ProgressionSessionComponent);
        var storage = typeof(HostContractStorageSessionComponent);
        var patches = new List<(MethodInfo, string, string)>
        {
            (AccessTools.Method(progression, nameof(ProgressionSessionComponent.IsProgressionEnabled)), nameof(ProgressionEnabledPrefix), null),
            (AccessTools.Method(progression, nameof(ProgressionSessionComponent.IsBlockUnlocked)), nameof(IsBlockUnlockedPrefix), null),
            (AccessTools.Method(progression, "UnlockBlocks", new[] { typeof(BlockRewardGroupDefinition), typeof(IdentityId), typeof(bool) }), nameof(UnlockBlocksPrefix), null),
            (AccessTools.Method(storage, "GetSharingIdentities"), nameof(GetSharingIdentitiesPrefix), null),
            (AccessTools.Method(storage, "GetOrCreateModel"), nameof(GetOrCreateModelPrefix), null),
            (AccessTools.Method(storage, "AddAccepted"), nameof(AddAcceptedPrefix), null),
            (AccessTools.Method(storage, "RemoveAccepted"), nameof(RemoveAcceptedPrefix), null),
            (AccessTools.Method(storage, "AddFinished"), nameof(AddFinishedPrefix), null),
            (AccessTools.Method(storage, "RemoveFinished"), nameof(RemoveFinishedPrefix), null),
            (AccessTools.Method(storage, "AddFailed"), nameof(AddFailedPrefix), null),
            (AccessTools.Method(storage, "RemoveFailed"), nameof(RemoveFailedPrefix), null),
            (AccessTools.Method(storage, "AddCompleted"), nameof(AddCompletedPrefix), null),
            (AccessTools.Method(storage, "Clear", new[] { typeof(ContractId) }), null, nameof(ClearContractPostfix)),
            (AccessTools.Method(storage, "Clear", new[] { typeof(IdentityId) }), null, nameof(ClearPlayerPostfix)),
        };
        // All or nothing: half the contract storage on players' own records and half on the shared one would lose contracts.
        int gap = patches.FindIndex(p => p.Item1 == null);
        if (gap >= 0) throw new MissingMethodException($"{nameof(ProgressionSharing)}: no target for {patches[gap].Item2 ?? patches[gap].Item3}");
        var done = new List<(MethodInfo Target, MethodInfo Patch)>();
        try
        {
            foreach (var (target, prefix, postfix) in patches)
            {
                var patch = AccessTools.Method(typeof(ProgressionSharing), prefix ?? postfix);
                harmony.Patch(target, prefix == null ? null : new HarmonyMethod(patch), postfix == null ? null : new HarmonyMethod(patch));
                done.Add((target, patch));
            }
        }
        catch
        {
            foreach (var (target, patch) in done) harmony.Unpatch(target, patch);
            throw;
        }
    }

    private static long[] FactionOf(IdentityId identity) => TeamPermissions.OnlineFactionOf((long)identity)?.Members ?? Array.Empty<long>();

    // Research groups: the player alone, or the player and their faction's members.
    private static List<long> Group(ResearchMode mode, IdentityId identity)
    {
        long[] faction = mode == ResearchMode.Faction ? FactionOf(identity) : Array.Empty<long>();
        return Sharing.With(mode, (long)identity, faction, faction);
    }

    private static bool ProgressionEnabledPrefix(ref bool __result)
    {
        if (!ProgressionOff) return true;
        __result = false;
        return false;
    }

    private static bool IsBlockUnlockedPrefix(ProgressionSessionComponent __instance, EntityCompositeDefinition block, IdentityId identity, ref bool __result)
    {
        if (ProgressionOff)
        {
            __result = true;
            return false;
        }
        var mode = Research;
        if (mode == ResearchMode.Shared) return true;
        __result = !__instance.IsProgressionEnabled(identity) || __instance._initialUnlocks.AllBlocks.Contains(block) || Holds(__instance.TryGetFactionData(identity), block)
            || Group(mode, identity).Any(member => Holds(OwnRecord(__instance, member), block));
        return false;
    }

    private static bool Holds(FactionProgressionData data, EntityCompositeDefinition block) => data != null && (data.UnlockedBlocks.Contains(block) || data.UnreachableUnlockedBlocks.Contains(block));

    // A read never creates a record: a joined game may only read what the server made.
    private static FactionProgressionData OwnRecord(ProgressionSessionComponent progression, long identity)
    {
        switch (progression._playerData)
        {
            case InProcessPerPlayerDataSessionComponent server:
                return server._perPlayerData.TryGet(new IdentityId(identity), out List<object> records) ? records.OfType<FactionProgressionData>().FirstOrDefault() : null;
            case InProcessPerPlayerDataClientSessionComponent client when client.IsReplicated && client._data != null:
                foreach (var data in client._data.Data)
                    if ((long)data.IdentityId == identity)
                        return data.DataByType.TryGetValue(typeof(FactionProgressionData), out PerIdentityData record) ? record as FactionProgressionData : null;
                return null;
            default:
                return null;
        }
    }

    private static bool UnlockBlocksPrefix(ProgressionSessionComponent __instance, BlockRewardGroupDefinition group, IdentityId identity, bool notify, ref List<EntityCompositeDefinition> __result)
    {
        var mode = Research;
        if (mode == ResearchMode.Shared) return true;
        var own = __instance._playerData.GetPerPlayerData<FactionProgressionData>(identity);
        var everyone = __instance.TryGetFactionData(identity);
        var added = new List<EntityCompositeDefinition>();
        foreach (PrefabDefinition block in group.Blocks)
        {
            if (own.UnlockedBlocks.Contains(block.Composition) || __instance._initialUnlocks.AllBlocks.Contains(block.Composition)) continue;
            own.UnlockedBlocks.Add(block.Composition);
            if (!Holds(everyone, block.Composition)) added.Add(block.Composition);
        }
        __result = added.Count == 0 ? null : added;
        if (__result == null || !notify) return false;
        foreach (long member in Group(mode, identity))
        {
            var data = __instance._playerData.GetPerPlayerData<PlayerProgressionData>(new IdentityId(member));
            foreach (var block in added) data.RecentlyUnlockedBlocks.Add(block);
            if (!data.RecentlyUnlockedGroups.Contains(group)) data.RecentlyUnlockedGroups.Add(group);
        }
        return false;
    }

    private static bool GetSharingIdentitiesPrefix(HostContractStorageSessionComponent __instance, IdentityId identity, ContractStateChange change, ref Buffer<IdentityId, Inline8<IdentityId>> __result)
    {
        var mode = HostWorld.HostContracts;
        if (mode == ResearchMode.Shared || change == ContractStateChange.Focused) return true;
        __result = Keen.VRage.Library.Memory.Buffer.Inline8<IdentityId>("GetSharingIdentities");
        foreach (long member in Sharing.With(mode, (long)identity, __instance._identities.GetPlayerIdentityIds().Select(p => (long)p), FactionOf(identity)))
            __result.Add(new IdentityId(member));
        return false;
    }

    private static bool GetOrCreateModelPrefix(HostContractStorageSessionComponent __instance, IdentityId playerId, ref PlayerContractsStateModel __result)
    {
        if (!SplitContracts) return true;
        if (__instance._playerModels.TryGetValue(playerId, out __result)) return false;
        var own = __instance.GetPlayerData(playerId);
        __result = new PlayerContractsStateModel();
        __result.AcceptedContracts.AddRange(own.AcceptedContracts);
        __result.FinishedContracts.AddRange(own.FinishedContracts);
        __result.FailedContracts.AddRange(own.FailedContracts.Select(f => new PlayerContractsStateModel.FailedContract { Contract = f.Contract, Reason = f.Reason }));
        __result.CompletedContracts.AddRange(own.CompletedContracts);
        __result.FocusedContracts.AddRange(own.FocusedContracts);
        __instance._playerModels.Add(playerId, __result);
        return false;
    }

    // Each player's own record instead of the shared one; the model is made first so it does not start with the change already in it.
    private static bool Each(HostContractStorageSessionComponent storage, ReadOnlySpan<IdentityId> players, Action<ContractPerPlayerData, PlayerContractsStateModel> change)
    {
        if (!SplitContracts) return true;
        foreach (IdentityId player in players)
        {
            var model = storage.GetOrCreateModel(player);
            change(storage.GetPlayerData(player), model);
        }
        return false;
    }

    private static bool AddAcceptedPrefix(HostContractStorageSessionComponent __instance, ReadOnlySpan<IdentityId> players, ContractId contract)
        => Each(__instance, players, (own, model) => { own.AcceptedContracts.Add(contract); model.AcceptedContracts.Add(contract); });

    private static bool RemoveAcceptedPrefix(HostContractStorageSessionComponent __instance, ReadOnlySpan<IdentityId> players, ContractId contract)
        => Each(__instance, players, (own, model) => { own.AcceptedContracts.Remove(contract); model.AcceptedContracts.Remove(contract); });

    private static bool AddFinishedPrefix(HostContractStorageSessionComponent __instance, ReadOnlySpan<IdentityId> players, ContractId contract)
        => Each(__instance, players, (own, model) => { own.FinishedContracts.Add(contract); model.FinishedContracts.Add(contract); });

    private static bool RemoveFinishedPrefix(HostContractStorageSessionComponent __instance, ReadOnlySpan<IdentityId> players, ContractId contract)
        => Each(__instance, players, (own, model) => { own.FinishedContracts.Remove(contract); model.FinishedContracts.Remove(contract); });

    private static bool AddFailedPrefix(HostContractStorageSessionComponent __instance, ReadOnlySpan<IdentityId> players, ContractId contract, LocKey reason)
        => Each(__instance, players, (own, model) =>
        {
            own.FailedContracts.Add(new ContractPerPlayerData.FailedContract { Contract = contract, Reason = reason });
            model.FailedContracts.Add(new PlayerContractsStateModel.FailedContract { Contract = contract, Reason = reason });
        });

    private static bool RemoveFailedPrefix(HostContractStorageSessionComponent __instance, ReadOnlySpan<IdentityId> players, ContractId contract)
        => Each(__instance, players, (own, model) =>
        {
            own.FailedContracts.RemoveFirst(contract, (ContractId c, ContractPerPlayerData.FailedContract f) => f.Contract == c);
            model.FailedContracts.RemoveFirst(contract, (ContractId c, PlayerContractsStateModel.FailedContract f) => f.Contract == c);
        });

    private static bool AddCompletedPrefix(HostContractStorageSessionComponent __instance, ReadOnlySpan<IdentityId> players, ContractId contract)
        => Each(__instance, players, (own, model) => { own.CompletedContracts.Add(contract); model.CompletedContracts.Add(contract); });

    private static void ClearContractPostfix(HostContractStorageSessionComponent __instance, ContractId contract)
    {
        if (!SplitContracts) return;
        foreach (IdentityId player in __instance._identities.GetPlayerIdentityIds())
        {
            var own = __instance.GetPlayerData(player);
            own.AcceptedContracts.Remove(contract);
            own.FinishedContracts.Remove(contract);
            own.CompletedContracts.Remove(contract);
            own.FailedContracts.RemoveFirst(contract, (ContractId c, ContractPerPlayerData.FailedContract f) => f.Contract == c);
        }
    }

    private static void ClearPlayerPostfix(HostContractStorageSessionComponent __instance, IdentityId playerId)
    {
        if (!SplitContracts) return;
        var own = __instance.GetPlayerData(playerId);
        own.AcceptedContracts.Clear();
        own.FinishedContracts.Clear();
        own.FailedContracts.Clear();
        own.CompletedContracts.Clear();
    }
}
