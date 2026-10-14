using System;

namespace Y51_SE2Online.Sync;

public sealed class HostSyncOptions
{
	public int MaxPlayers = 8;
	public bool HostCountsAsPlayer = true;
	public string PluginVersion = "";

	public string GameVersion = "";

	public int StateBudgetBytesPerSec = 1048576;

	public RateCapPolicy Caps = new RateCapPolicy();

	public int StatusIntervalMs = Y51_SE2Online.Core.LogFormat.StatusIntervalMs;

	public int StateEmitIntervalMs = 8;

	public Func<string, (int TypeId, int Size)?>? ResolveType;

	// Which announced type names a client may write as input Data; null refuses all.
	public Func<string, bool>? IsClientInput;

	public Func<SignalKeyDef, string?>? CheckSignal;

	public HostSettings Settings = new HostSettings(Identities: false, GridPermissionMode.Team, ResearchMode.Shared);

	public string HostName = "Host";

	public bool LookTotals = true;

	public AccessList Access = new AccessList();

	public string Password = "";

	public string SessionName = "";

	/// <summary>Configured message of the day; the access list's runtime choice overrides it.</summary>
	public string Motd = "";

	public string MotdTitle = "";

	public bool Dedicated;

	public ulong HostSteamId;

	public long HostIdentity;

	public Func<long, string?>? FactionTagOf;

	public Func<SafeZoneInfo[]>? SafeZones;

	public bool Pvp = true;

	public bool OfflineRaidProtection = true;

	/// <summary>Per-player PCU limit: null keeps the world's value, 0 is unlimited.</summary>
	public int? PcuLimit;

	/// <summary>Joiners only receive what is within this many metres of them; 0 sends the whole world.</summary>
	public double StreamingRadiusMeters;

	/// <summary>Players hear each other within this many metres; 0 turns voice off.</summary>
	public double VoiceRadiusMeters;

	public int PcuInForce;

	/// <summary>Sun settings from the host or server config: null keeps the world's own.</summary>
	public bool? SunRotation;

	public double? SunPeriodMinutes;

	public (bool Rotation, double PeriodMinutes) SunInForce;

	public Action<bool>? OfflineRaidChanged;

	public Action<bool>? PvpChanged;

}
