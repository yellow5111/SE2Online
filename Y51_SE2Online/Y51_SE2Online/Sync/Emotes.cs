using System;

namespace Y51_SE2Online.Sync;

/// <summary>Host to a fighter: hold (or release) all movement, body turning and tool input.</summary>
public sealed record FightLockMessage(bool Locked);

/// <summary>What the host last asked of this player's input; SyncEngine.FightLock applies it on the game thread.</summary>
public static class FightLockState
{
    public static volatile bool Locked;
    public static long ChangedMs;

    public static void Set(bool locked)
    {
        ChangedMs = Y51_SE2Online.Core.MpClock.NowMs();
        Locked = locked;
    }
}

/// <summary>A punch at another player, by the target's client id (0 swings at the air).</summary>
public sealed record PunchRequest(ushort Target);

/// <summary>The emote menu's entries: SE2 2.4's own single-character emote definitions (Content/Characters/Shared/Animations/Male/Emotes).</summary>
public static class Emotes
{
    public sealed record Entry(string Label, Guid Definition);

    public static readonly Guid Punch = new("a5ee20b1-605e-4bdf-95d2-0fa0f1eac02e");
    public static readonly Guid GotHit = new("a4283c7b-7a7e-4e2e-b868-5d12b79010cb");

    public static readonly Entry[] Menu =
    {
        new("Wave", new Guid("95fe4588-5ac5-4502-a84e-454ac37c8741")),
        new("Point", new Guid("d61b91a4-aee0-43e6-8944-1fec6657ec4b")),
        new("Thumbs up", new Guid("069be753-acc3-437a-9305-9151b920601a")),
        new("Dance", new Guid("c05862bf-9f8b-4867-8bbb-ca1aed894af3")),
        new("Facepalm", new Guid("b223268c-78e2-443b-9e35-9e154954f96e")),
        new("Punch", Punch),
    };
}

/// <summary>Host-side punch limits; whether a punch hurts is the PvP relation rule (PvpRules).</summary>
public static class PunchRules
{
    public const float Damage = 10f;
    public const long CooldownMs = 800;
    /// <summary>Character origins; a little more than the client's 3.5 m aim reach to the chest covers latency.</summary>
    public const double Reach = 3.5;

    /// <summary>SE2's two-character punch scene (CS_PunchEmoteDefinition); its reaction finds the partner by this entity name.</summary>
    public static readonly Guid Fight = new("f5ae41ed-7f26-4326-bde8-6a2170b67752");
    public const string FightRole = "Punch";
    // ponytail: role assignment read from the definitions (the "Punch" actor plays Punch.def), not seen in game; flip if the victim throws the punches.
    public const bool AttackerIsSecondary = true;
    /// <summary>Metres in front of the primary where the partner is placed before the scene starts.</summary>
    public const float FightOffset = 1.0f;
    public const long FightCooldownMs = 6000;
    /// <summary>The scene has no hit events the host sees; damage lands at these times after it starts.</summary>
    public static readonly long[] FightHitsMs = { 1000, 2000 };
    public const float FightHitDamage = 8f;
    /// <summary>A fight or swing lock never outlives this, whatever happens to the scene.</summary>
    public const long LockSafetyMs = 15000;
    /// <summary>Time the scene gets to start before "no emote playing" ends the lock.</summary>
    public const long LockGraceMs = 750;

    public static bool Ready(long lastMs, long nowMs) => nowMs - lastMs >= CooldownMs;

    /// <summary>Why a player may not fight another, for the refusal notice.</summary>
    public static string Refusal(PlayerRelation relation, bool pvp) => !pvp ? "PvP is off on this server" : relation switch
    {
        PlayerRelation.Faction => "you are in the same faction",
        PlayerRelation.Allied => "your factions are allied",
        PlayerRelation.Peace => "your factions are at peace",
        _ => "the server rules don't allow it",
    };

    public static bool InReach(double dx, double dy, double dz)
    {
        double squared = dx * dx + dy * dy + dz * dz;
        return double.IsFinite(squared) && squared <= Reach * Reach;
    }
}
