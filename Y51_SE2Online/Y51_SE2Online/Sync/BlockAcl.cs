using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Y51_SE2Online.Sync;

/// <summary>
/// A block's own whitelist and blacklist, kept in its saved and replicated access group as "Y51.Acl:a=1,2;d=3;o=4".
/// Owner first, then blacklist, then whitelist, then the grid's sharing. "o" is the grid owner who wrote the lists:
/// they lapse once someone else owns the grid (claim, transfer, release, hack, a pasted blueprint or projection).
/// "t=1" marks an antenna whose HUD marker shows its owner's faction tag: SE2 keeps no other per-block value that
/// survives saves and blueprints.
/// </summary>
public sealed record BlockAcl(long[] Allow, long[] Deny, bool ShowTag = false, long Owner = 0)
{
    public const string Prefix = "Y51.Acl:";
    public const int MaxPerList = 32;
    public static readonly BlockAcl Empty = new(Array.Empty<long>(), Array.Empty<long>());

    public bool IsEmpty => Allow.Length == 0 && Deny.Length == 0 && !ShowTag;

    public static bool IsAcl(string value) => value != null && value.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>
    /// A block access group that a grid's new owner must not inherit: block lists, and SE2's own "everyone may use",
    /// which bypasses grid sharing. Owner, crew and friend groups follow the new owner's sharing and stay.
    /// </summary>
    public static bool ResetsOnNewOwner(string accessGroup, string everyoneAccess) => IsAcl(accessGroup) || (accessGroup != null && accessGroup == everyoneAccess);

    public static BlockAcl Parse(string value)
    {
        if (!IsAcl(value)) return Empty;
        long[] allow = Array.Empty<long>(), deny = Array.Empty<long>();
        bool tag = false;
        long owner = 0;
        foreach (string part in value[Prefix.Length..].Split(';'))
        {
            if (part.Length < 2 || part[1] != '=') continue;
            if (part[0] == 't') { tag = part == "t=1"; continue; }
            if (part[0] == 'o') { owner = long.TryParse(part[2..], NumberStyles.Integer, CultureInfo.InvariantCulture, out long o) && PlayerIdentities.IsHuman(o) ? o : 0; continue; }
            long[] ids = part[2..].Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long id) ? id : 0)
                .Where(PlayerIdentities.IsHuman).Distinct().Take(MaxPerList).ToArray();
            if (part[0] == 'a') allow = ids;
            else if (part[0] == 'd') deny = ids;
        }
        return new BlockAcl(allow, deny, tag, owner);
    }

    public override string ToString() => Prefix + "a=" + Join(Allow) + ";d=" + Join(Deny) + (ShowTag ? ";t=1" : "") + (Owner != 0 ? ";o=" + Owner.ToString(CultureInfo.InvariantCulture) : "");

    /// <summary>The lists in force while <paramref name="owner"/> owns the grid: none unless that owner wrote them.</summary>
    public BlockAcl For(long owner) => (Owner == owner && PlayerIdentities.IsHuman(owner)) || Allow.Length + Deny.Length == 0 ? this : this with { Allow = Array.Empty<long>(), Deny = Array.Empty<long>() };

    private static string Join(IEnumerable<long> ids) => string.Join(",", ids.Select(i => i.ToString(CultureInfo.InvariantCulture)));

    /// <summary>Adds or removes a player on one list; a player is never on both.</summary>
    public BlockAcl With(long player, bool deny, bool add)
    {
        var allow = Allow.Where(p => p != player).ToList();
        var denied = Deny.Where(p => p != player).ToList();
        if (add) (deny ? denied : allow).Add(player);
        return this with { Allow = allow.Take(MaxPerList).ToArray(), Deny = denied.Take(MaxPerList).ToArray() };
    }

    public static bool CanUse(long owner, long actor, GridSharing sharing, IEnumerable<OnlineFaction> factions, BlockAcl acl)
    {
        if (!PlayerIdentities.IsHuman(actor)) return false;
        if (owner == actor) return true;
        if (acl.Deny.Contains(actor)) return false;
        if (acl.Allow.Contains(actor) && !PlayerIdentities.IsNpc(owner)) return true;
        return SocialState.CanAccess(owner, actor, sharing, factions);
    }
}
