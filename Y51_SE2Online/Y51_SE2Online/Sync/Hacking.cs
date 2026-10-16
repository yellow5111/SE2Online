using System;
using System.Collections.Generic;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.Sync;

public enum HackRole { Hacker, Defender }

/// <summary>
/// Running: full speed. Slowed: an owner or faction member is near the grid without engaging. Lockdown: the owner's
/// remote lockdown freezes it. Halted: the first seconds after a defender counter-hacks. Contested: slowed.
/// </summary>
public enum HackPhase { Running, Slowed, Lockdown, Halted, Contested }

/// <summary>One hack as a player's client shows it. Glitch and Boot count clashes; a client reacts when they grow.</summary>
public sealed record HackView(uint Grid, string GridName, string Hacker, HackRole Role, HackPhase Phase, double Progress, double Rate,
    int LockdownLeft, int LockdownReadyIn, int Glitch, int Boot, bool Powered);

/// <summary>Who may hack what, how long it takes, and how defences slow it. Host-authoritative; no game types here.</summary>
public static class HackRules
{
    public const double RangeMeters = 3, CancelSlackMeters = 1, DefenderRadiusMeters = 25;
    public const double BaseSeconds = 30, SecondsPerBlock = 0.25, CapSeconds = 300, PoweredFactor = 1.5, ContestSpeed = 0.25, DefenderNearSpeed = 0.5;
    public const long CooldownMs = 120_000, ContestHaltMs = 5_000, ClashMs = 18_000, GlitchMs = 3_000;
    /// <summary>A player waits this long after an attempt that started nothing before the host looks for terminals again.</summary>
    public const long RetryMs = 2_000;
    public const double MinMultiplier = 0.1, MaxMultiplier = 10;

    /// <summary>Seconds at full speed for a ship of this many blocks; a powered grid resists 1.5x longer.</summary>
    public static double Seconds(int blocks, bool powered, double multiplier)
        => Math.Min(CapSeconds, BaseSeconds + Math.Max(0, blocks) * SecondsPerBlock) * (powered ? PoweredFactor : 1) * Math.Clamp(multiplier, MinMultiplier, MaxMultiplier);

    public readonly record struct Check(bool Enabled, bool AllowPlayerGrids, bool Pvp, bool OfflineRaidProtection, bool OwnerSideOnline,
        bool InSafeZone, bool HasWorkingTerminal, long CooldownLeftMs, bool AlreadyHacked);

    /// <summary>Why <paramref name="hacker"/> may not hack a grid owned by <paramref name="owner"/>; null when allowed.</summary>
    public static string Refusal(long hacker, long owner, PlayerRelation relation, Check c)
    {
        if (!c.Enabled) return "Hacking is turned off on this server.";
        if (!PlayerIdentities.IsHuman(hacker)) return "Your player identity is not ready.";
        if (owner == hacker) return "You already own this grid.";
        if (c.AlreadyHacked) return "Someone is already hacking this grid.";
        if (c.CooldownLeftMs > 0) return $"This grid's security is still resetting; try again in {(c.CooldownLeftMs + 999) / 1000} s.";
        if (!c.HasWorkingTerminal) return "This grid has no working terminal or control block to hack into.";
        if (c.InSafeZone) return "That grid is inside a safe zone.";
        if (!PlayerIdentities.IsHuman(owner)) return null;
        if (!c.AllowPlayerGrids) return "Hacking other players' grids is turned off on this server.";
        if (!c.Pvp) return "PvP is off, so other players' grids can't be hacked.";
        switch (relation)
        {
            case PlayerRelation.Faction: return "That grid belongs to your own faction.";
            case PlayerRelation.Allied: return "That grid belongs to an allied faction.";
            case PlayerRelation.Peace: return "Your factions are at peace.";
        }
        if (c.OfflineRaidProtection && !c.OwnerSideOnline) return "Its owner and their faction are offline (offline raid protection).";
        return null;
    }

    /// <summary>The owner and their faction defend a player's grid; NPC and unowned grids have no defenders.</summary>
    public static bool Defends(long player, long owner, PlayerRelation relation)
        => PlayerIdentities.IsHuman(owner) && PlayerIdentities.IsHuman(player) && (player == owner || relation == PlayerRelation.Faction);

    public static double Speed(HackPhase phase) => phase switch { HackPhase.Running => 1, HackPhase.Slowed => DefenderNearSpeed, HackPhase.Contested => ContestSpeed, _ => 0 };

    public static string Validate(OnlineSettings s)
    {
        if (!(s.HackTimeMultiplier >= MinMultiplier && s.HackTimeMultiplier <= MaxMultiplier)) return $"Hack time multiplier must be between {MinMultiplier} and {MaxMultiplier}.";
        if (s.HackLockdownSeconds < 0 || s.HackLockdownSeconds > 600) return "Hack lockdown must be between 0 (off) and 600 seconds.";
        if (s.HackLockdownCooldownSeconds < 0 || s.HackLockdownCooldownSeconds > 3600) return "Hack lockdown cooldown must be between 0 and 3600 seconds.";
        return null;
    }
}

/// <summary>
/// One hack's progress. A remote lockdown freezes it; a defender near the grid who is not counter-hacking pauses it;
/// a counter-hack halts it for a few seconds, then lets it creep on at a quarter speed with a clash every 18 s.
/// </summary>
public sealed class HackJob
{
    public double Progress { get; private set; }
    public bool Contested { get; private set; }
    public long LockdownUntilMs { get; private set; } = long.MinValue;
    public long HaltUntilMs { get; private set; } = long.MinValue;
    public long NextClashMs { get; private set; } = long.MaxValue;
    public int Clashes { get; private set; }

    public HackPhase PhaseAt(long now, bool defenderNear)
        => now < LockdownUntilMs ? HackPhase.Lockdown
            : Contested ? (now < HaltUntilMs ? HackPhase.Halted : HackPhase.Contested)
            : defenderNear ? HackPhase.Slowed
            : HackPhase.Running;

    /// <summary>Advances by <paramref name="seconds"/> of wall time; true once the hack is complete.</summary>
    public bool Advance(long now, double seconds, bool defenderNear, double fullSeconds)
    {
        if (seconds > 0) Progress = Math.Min(1, Progress + HackRules.Speed(PhaseAt(now, defenderNear)) * seconds / Math.Max(1, fullSeconds));
        return Progress >= 1;
    }

    /// <summary>A defender counter-hacks: true when that starts the contest (the first clash).</summary>
    public bool Contest(long now)
    {
        if (Contested) return false;
        Contested = true;
        HaltUntilMs = now + HackRules.ContestHaltMs;
        NextClashMs = now + HackRules.ClashMs;
        Clashes++;
        return true;
    }

    /// <summary>True when a repeat clash (glitch and terminal boot) is due while contested.</summary>
    public bool ClashDue(long now)
    {
        if (!Contested || now < NextClashMs) return false;
        NextClashMs = now + HackRules.ClashMs;
        Clashes++;
        return true;
    }

    public void Lockdown(long now, int seconds) => LockdownUntilMs = Math.Max(LockdownUntilMs, now + seconds * 1000L);
}

/// <summary>Per-grid timers (hack attempts, lockdowns). ponytail: in memory only, a restart forgets them.</summary>
public sealed class HackCooldowns
{
    private readonly Dictionary<uint, long> _until = new();
    public long LeftMs(uint grid, long now) => _until.TryGetValue(grid, out long until) ? Math.Max(0, until - now) : 0;
    public void Start(uint grid, long now, long ms)
    {
        if (_until.Count > 4096) _until.Clear();
        _until[grid] = now + ms;
    }
}
