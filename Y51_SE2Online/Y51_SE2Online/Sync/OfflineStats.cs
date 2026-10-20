using System.Collections.Generic;
using System.Linq;

namespace Y51_SE2Online.Sync;

/// <summary>
/// Survival stats of offline players' parked characters stand still on the host: from the moment a player leaves until
/// they control their character again (a player who is still loading counts as offline), life support and suit
/// thrusters consume nothing and deal no suffocation damage. No game types here.
/// </summary>
public static class OfflineStats
{
    public static volatile bool Enabled = true;
    // Null until the host's first roster pass: a world that just loaded has nobody in control yet.
    private static volatile HashSet<long> _players, _live;
    public static long Paused;

    /// <summary>Every player identity of the world, and those who control their character right now.</summary>
    public static void SetPlayers(IEnumerable<long> players, IEnumerable<long> live)
    {
        _live = live.ToHashSet();
        _players = players.ToHashSet();
    }

    public static void Reset() => _players = null;

    public static bool Frozen(long owner) => _players is { } players ? Frozen(Enabled, owner, players, _live) : Enabled && PlayerIdentities.IsHuman(owner);

    public static bool Frozen(bool enabled, long owner, IReadOnlySet<long> players, IReadOnlySet<long> live)
        => enabled && PlayerIdentities.IsHuman(owner) && players.Contains(owner) && !live.Contains(owner);
}
