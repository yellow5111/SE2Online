using System.Collections.Generic;
using System.Linq;

namespace Y51_SE2Online.Sync;

/// <summary>Which look-at player options apply, from the social snapshot this client has. The host checks again.</summary>
public static class PlayerOptionRules
{
    public static OnlineFaction FactionOf(IEnumerable<OnlineFaction> factions, long player) =>
        player == 0 ? null : factions?.FirstOrDefault(f => f.Members.Contains(player));

    /// <summary>You are a leader, admin or moderator, and the target is in no faction and not invited yet.</summary>
    public static bool CanInvite(SocialSnapshot social, long target)
    {
        var own = FactionOf(social.Factions, social.Actor);
        return own != null && target != social.Actor && PlayerIdentities.IsHuman(target)
            && own.RoleOf(social.Actor) >= SocialState.Needed(SocialAction.Invite)
            && FactionOf(social.Factions, target) == null && !own.Invitations.Contains(target);
    }

    /// <summary>You are in no faction, the target can accept applications for theirs, and you have not applied yet.</summary>
    public static bool CanApply(SocialSnapshot social, long target)
    {
        var theirs = FactionOf(social.Factions, target);
        return theirs != null && target != social.Actor && FactionOf(social.Factions, social.Actor) == null
            && theirs.RoleOf(target) >= SocialState.Needed(SocialAction.ApproveRequest) && !theirs.Requests.Contains(social.Actor);
    }

    public static string Describe(SocialSnapshot social, long target, string name)
    {
        var faction = FactionOf(social.Factions, target);
        string relation = target == social.Actor ? "You" : faction == null ? "Not in a faction" : "[" + faction.Tag + "] " + faction.Name + " - " + faction.RoleOf(target);
        return name + "\n" + relation;
    }
}
