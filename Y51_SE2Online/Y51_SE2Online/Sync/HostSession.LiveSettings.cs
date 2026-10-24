using System.Collections.Generic;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.Sync;

public sealed partial class HostSession
{
	/// <summary>
	/// The host saved new settings while hosting: world and join rules change in place, so nobody is dropped. A world
	/// rule only changes when the save changed it, through the same per-world choice the admin panel sets, so an
	/// unrelated save never undoes an admin's choice.
	/// </summary>
	public void ApplySettings(OnlineSettings before, OnlineSettings after, IReadOnlyList<string> changes)
	{
		_o.MaxPlayers = after.MaxPlayers;
		_o.Pings = after.PingSettingsOf();
		_o.MarkerDefaults = after.MarkerDefaults ?? new();
		_o.Password = after.Password ?? "";
		_o.SessionName = after.SessionName.Trim();
		_o.Motd = after.Motd ?? "";
		_o.MotdTitle = after.MotdTitle ?? "";
		_o.StreamingRadiusMeters = after.StreamingRadiusMeters;
		_o.VoiceRadiusMeters = after.VoiceRadiusMeters;
		_o.Settings = _o.Settings with { WaterSync = after.WaterSync };
		if (before.Pvp != after.Pvp) { _o.Access.SetPvp(after.Pvp); _o.PvpChanged?.Invoke(after.Pvp); }
		if (before.OfflineRaidProtection != after.OfflineRaidProtection) { _o.Access.SetOfflineRaid(after.OfflineRaidProtection); _o.OfflineRaidChanged?.Invoke(after.OfflineRaidProtection); }
		if (before.PcuLimit != after.PcuLimit) _o.Access.SetPcuLimit(after.PcuLimit ?? -1);
		if (before.SunRotation != after.SunRotation && after.SunRotation is bool sun) _o.Access.SetSunRotation(sun);
		if (before.SunPeriodMinutes != after.SunPeriodMinutes && after.SunPeriodMinutes is double day) _o.Access.SetSunPeriod(day);
		_nextAdminPushMs = 0;
		if (changes.Count > 0) Announce("Server settings changed: " + string.Join(", ", changes) + ".");
	}
}
