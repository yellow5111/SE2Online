using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Y51_SE2Online.Sync;

/// <summary>
/// A block's own whitelist and blacklist, kept in its saved and replicated access group as "Y51.Acl:a=1,2;d=3".
/// Owner first, then blacklist, then whitelist, then the grid's sharing. "t=1" marks an antenna whose HUD marker
/// shows its owner's faction tag: SE2 keeps no other per-block value that survives saves and blueprints.
/// </summary>
public sealed record BlockAcl(long[] Allow, long[] Deny, bool ShowTag = false)
{
    public const string Prefix = "Y51.Acl:";
    public const int MaxPerList = 32;
    public static readonly BlockAcl Empty = new(Array.Empty<long>(), Array.Empty<long>());

    public bool IsEmpty => Allow.Length == 0 && Deny.Length == 0 && !ShowTag;

    public static bool IsAcl(string value) => value != null && value.StartsWith(Prefix, StringComparison.Ordinal);

    public static BlockAcl Parse(string value)
    {
        if (!IsAcl(value)) return Empty;
        long[] allow = Array.Empty<long>(), deny = Array.Empty<long>();
        bool tag = false;
        foreach (string part in value[Prefix.Length..].Split(';'))
        {
            if (part.Length < 2 || part[1] != '=') continue;
            if (part[0] == 't') { tag = part == "t=1"; continue; }
            long[] ids = part[2..].Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long id) ? id : 0)
                .Where(PlayerIdentities.IsHuman).Distinct().Take(MaxPerList).ToArray();
            if (part[0] == 'a') allow = ids;
            else if (part[0] == 'd') deny = ids;
        }
        return new BlockAcl(allow, deny, tag);
    }

    public override string ToString() => Prefix + "a=" + Join(Allow) + ";d=" + Join(Deny) + (ShowTag ? ";t=1" : "");

    private static string Join(IEnumerable<long> ids) => string.Join(",", ids.Select(i => i.ToString(CultureInfo.InvariantCulture)));

    /// <summary>Adds or removes a player on one list; a player is never on both.</summary>
    public BlockAcl With(long player, bool deny, bool add)
    {
        var allow = Allow.Where(p => p != player).ToList();
        var denied = Deny.Where(p => p != player).ToList();
        if (add) (deny ? denied : allow).Add(player);
        return new BlockAcl(allow.Take(MaxPerList).ToArray(), denied.Take(MaxPerList).ToArray(), ShowTag);
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
