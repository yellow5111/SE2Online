using System.Collections.Generic;
using System.Globalization;

namespace Y51_SE2Online.Core;

/// <summary>
/// What a settings save means for a session that is already hosting. Restarting hosting drops every player, so only a
/// change to the listen socket needs it; world rules and listing details are pushed into the running session, and
/// options that are only read when hosting starts are kept for the next session.
/// </summary>
public static class HostSettingsChange
{
    public sealed record Result(IReadOnlyList<string> Live, IReadOnlyList<string> NextSession, bool Restart)
    {
        public bool Any => Live.Count > 0 || NextSession.Count > 0 || Restart;
    }

    public static Result Compare(OnlineSettings before, OnlineSettings after)
    {
        var live = new List<string>();
        var next = new List<string>();
        if (before.SessionName.Trim() != after.SessionName.Trim()) live.Add("session name \"" + after.SessionName.Trim() + "\"");
        if (before.Description.Trim() != after.Description.Trim()) live.Add("description updated");
        if (before.MaxPlayers != after.MaxPlayers) live.Add("player limit " + after.MaxPlayers);
        if ((before.Password ?? "") != (after.Password ?? "")) live.Add(string.IsNullOrEmpty(after.Password) ? "password removed" : "password changed");
        if (before.FriendsOnly != after.FriendsOnly) live.Add(after.FriendsOnly ? "Steam friends only" : "open to everyone");
        if (before.Advertise != after.Advertise) live.Add(after.Advertise ? "listed in the public directory" : "removed from the public directory");
        if ((before.PublicAddress ?? "") != (after.PublicAddress ?? "")) live.Add("public address updated");
        if (before.Pvp != after.Pvp) live.Add("PvP " + OnOff(after.Pvp));
        if (before.OfflineRaidProtection != after.OfflineRaidProtection) live.Add("offline raid protection " + OnOff(after.OfflineRaidProtection));
        if (before.PcuLimit != after.PcuLimit) live.Add(after.PcuLimit is int pcu ? (pcu == 0 ? "no PCU limit" : "PCU limit " + pcu.ToString("N0", CultureInfo.InvariantCulture)) : "the world's own PCU limit");
        if (before.SunRotation != after.SunRotation && after.SunRotation is bool sun) live.Add(sun ? "the sun moves" : "the sun is stopped");
        if (before.SunPeriodMinutes != after.SunPeriodMinutes && after.SunPeriodMinutes is double day) live.Add("a full day lasts " + Sync.AccessList.DayLength(day));
        if (before.StreamingRadiusMeters != after.StreamingRadiusMeters) live.Add(after.StreamingRadiusMeters > 0 ? "streaming radius " + Metres(after.StreamingRadiusMeters) : "streaming off");
        if (before.VoiceRadiusMeters != after.VoiceRadiusMeters) live.Add(after.VoiceRadiusMeters > 0 ? "voice range " + Metres(after.VoiceRadiusMeters) : "voice chat off");
        if (before.WaterSync != after.WaterSync) live.Add("water sync " + OnOff(after.WaterSync) + " for players who join from now on");
        if (before.UsageMultipliers != after.UsageMultipliers) live.Add("resource usage " + after.UsageMultipliers.Describe());
        if (before.Hacking != after.Hacking || before.AllowHackingPlayerGrids != after.AllowHackingPlayerGrids || before.HackTimeMultiplier != after.HackTimeMultiplier
            || before.HackLockdownSeconds != after.HackLockdownSeconds || before.HackLockdownCooldownSeconds != after.HackLockdownCooldownSeconds)
            live.Add(!after.Hacking ? "hacking off" : "hacking on" + (after.AllowHackingPlayerGrids ? " (player grids with PvP)" : " (NPC and unowned grids only)") + ", hack time x" + after.HackTimeMultiplier.ToString("0.##", CultureInfo.InvariantCulture));
        if (before.TeamGrids != after.TeamGrids) next.Add("grid sharing");
        if (before.SharedResearch != after.SharedResearch) next.Add("shared research");
        if (before.Nametags != after.Nametags || before.NametagRange != after.NametagRange) next.Add("name tags");
        if (before.SpawnNearHost != after.SpawnNearHost) next.Add("spawn near host");
        if (before.StateBudgetKiB != after.StateBudgetKiB) next.Add("bandwidth per player");
        if ((before.PlayerName ?? "").Trim() != (after.PlayerName ?? "").Trim()) next.Add("player name");
        bool restart = before.AllowDirectIp != after.AllowDirectIp || before.ListenPort != after.ListenPort || (before.BindAddress ?? "") != (after.BindAddress ?? "");
        return new Result(live, next, restart);
    }

    private static string OnOff(bool on) => on ? "on" : "off";

    private static string Metres(double metres) => metres >= 1000 ? (metres / 1000).ToString("0.#", CultureInfo.InvariantCulture) + " km" : metres.ToString("0", CultureInfo.InvariantCulture) + " m";
}
