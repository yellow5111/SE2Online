using System;
using System.Collections.Generic;
using System.Linq;

namespace Y51_SE2Online.Sync;

public enum FactionRole { Member, Moderator, Admin, Leader }

/// <summary>Between two factions. Neutral is the default (no treaty); Peace and Allied are agreed, War is declared.</summary>
public enum Diplomacy { Neutral, Peace, Allied, War }

public enum PlayerRelation { None, Own, Faction, Allied, Peace, Neutral, War, Npc }

public sealed class FactionPact
{
    public string A { get; set; } = "";
    public string B { get; set; } = "";
    public Diplomacy State { get; set; }
}

public sealed class FactionProposal
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    /// <summary>Peace or Allied.</summary>
    public Diplomacy State { get; set; }
}

/// <summary>
/// Who may hurt whom, and how markers read it. The server's PvP switch wins: with PvP off no player harms another
/// whatever the treaties. With PvP on, players fight only when their factions have no treaty (Neutral, including
/// players without a faction) or are at war; own faction, allies and factions at peace never do.
/// </summary>
public sealed class RelationTable
{
    public static readonly RelationTable Empty = new(new Dictionary<long, string>(), new Dictionary<(string, string), Diplomacy>());

    private readonly Dictionary<long, string> _factionOf;
    private readonly Dictionary<(string, string), Diplomacy> _pacts;

    private RelationTable(Dictionary<long, string> factionOf, Dictionary<(string, string), Diplomacy> pacts)
    {
        _factionOf = factionOf;
        _pacts = pacts;
    }

    public static RelationTable Build(IEnumerable<OnlineFaction> factions, IEnumerable<FactionPact> pacts)
    {
        var factionOf = new Dictionary<long, string>();
        foreach (var faction in factions ?? Enumerable.Empty<OnlineFaction>())
            foreach (long member in faction.Members) factionOf.TryAdd(member, faction.Id);
        var map = new Dictionary<(string, string), Diplomacy>();
        foreach (var pact in pacts ?? Enumerable.Empty<FactionPact>()) map[Key(pact.A, pact.B)] = pact.State;
        return new RelationTable(factionOf, map);
    }

    public static (string, string) Key(string a, string b) => string.CompareOrdinal(a, b) <= 0 ? (a, b) : (b, a);

    public string? FactionOf(long identity) => _factionOf.TryGetValue(identity, out string id) ? id : null;

    public Diplomacy Between(string a, string b) => a == null || b == null || a == b ? Diplomacy.Neutral : _pacts.TryGetValue(Key(a, b), out var state) ? state : Diplomacy.Neutral;

    /// <summary>How <paramref name="other"/> stands to <paramref name="self"/>.</summary>
    public PlayerRelation Of(long self, long other)
    {
        if (other == 0 || self == 0) return PlayerRelation.None;
        if (PlayerIdentities.IsNpc(other) || PlayerIdentities.IsNpc(self)) return PlayerRelation.Npc;
        if (self == other) return PlayerRelation.Own;
        string a = FactionOf(self), b = FactionOf(other);
        if (a != null && a == b) return PlayerRelation.Faction;
        return Between(a, b) switch
        {
            Diplomacy.Allied => PlayerRelation.Allied,
            Diplomacy.Peace => PlayerRelation.Peace,
            Diplomacy.War => PlayerRelation.War,
            _ => PlayerRelation.Neutral
        };
    }

    /// <summary>Whether two players may damage each other and their turrets target each other.</summary>
    public static bool MayFight(PlayerRelation relation, bool pvp) => pvp && relation is PlayerRelation.Neutral or PlayerRelation.War;
}

/// <summary>Treaty changes between factions; the caller has already checked the actor may speak for <c>own</c>.</summary>
public static class FactionDiplomacy
{
    public static string Apply(SocialState state, OnlineFaction own, OnlineFaction other, SocialAction action)
    {
        if (own == other) return "Choose another faction.";
        var key = RelationTable.Key(own.Id, other.Id);
        var current = state.Pacts.FirstOrDefault(p => RelationTable.Key(p.A, p.B) == key)?.State ?? Diplomacy.Neutral;
        var incoming = state.Proposals.FirstOrDefault(p => p.From == other.Id && p.To == own.Id);
        switch (action)
        {
            case SocialAction.ProposeAlliance:
            case SocialAction.ProposePeace:
            {
                var wanted = action == SocialAction.ProposeAlliance ? Diplomacy.Allied : Diplomacy.Peace;
                if (wanted == Diplomacy.Allied && current == Diplomacy.Allied) return "You are already allied.";
                if (wanted == Diplomacy.Peace && current != Diplomacy.War) return "Peace is proposed to end a war; you are not at war with them.";
                // Both sides asking for the same thing is an agreement.
                if (incoming?.State == wanted) { Set(state, own, other, wanted); return wanted == Diplomacy.Allied ? "Alliance formed." : "Peace agreed."; }
                state.Proposals.RemoveAll(p => p.From == own.Id && p.To == other.Id);
                if (state.Proposals.Count >= 4096) return "Too many pending proposals in this world.";
                state.Proposals.Add(new FactionProposal { From = own.Id, To = other.Id, State = wanted });
                return wanted == Diplomacy.Allied ? "Alliance proposed; their leader or an admin must accept." : "Peace proposed; their leader or an admin must accept.";
            }
            case SocialAction.DeclareWar:
                if (current == Diplomacy.War) return "You are already at war.";
                Set(state, own, other, Diplomacy.War); return "War declared.";
            case SocialAction.CancelTreaty:
                if (current == Diplomacy.War) return "A war ends with an accepted peace proposal.";
                if (current == Diplomacy.Neutral) return "You have no treaty with them.";
                Set(state, own, other, Diplomacy.Neutral); return current == Diplomacy.Allied ? "Alliance ended." : "Peace treaty ended.";
            case SocialAction.AcceptProposal:
                if (incoming == null) return "They have no pending proposal for you.";
                Set(state, own, other, incoming.State); return incoming.State == Diplomacy.Allied ? "Alliance formed." : "Peace agreed.";
            case SocialAction.RejectProposal:
                if (incoming == null) return "They have no pending proposal for you.";
                state.Proposals.Remove(incoming); return "Proposal rejected.";
            default: return "Unknown diplomacy action.";
        }
    }

    private static void Set(SocialState state, OnlineFaction a, OnlineFaction b, Diplomacy value)
    {
        var key = RelationTable.Key(a.Id, b.Id);
        state.Pacts.RemoveAll(p => RelationTable.Key(p.A, p.B) == key);
        if (value != Diplomacy.Neutral) state.Pacts.Add(new FactionPact { A = key.Item1, B = key.Item2, State = value });
        state.Proposals.RemoveAll(p => (p.From == a.Id && p.To == b.Id) || (p.From == b.Id && p.To == a.Id));
    }

    public static void Forget(SocialState state, string faction)
    {
        state.Pacts.RemoveAll(p => p.A == faction || p.B == faction);
        state.Proposals.RemoveAll(p => p.From == faction || p.To == faction);
    }
}
