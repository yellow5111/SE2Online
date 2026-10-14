using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.Core;

public sealed class OnlineSettings
{
    public string SessionName { get; set; } = "My SE2 session";
    public string Description { get; set; } = "";
    /// <summary>Publishes the session (host SteamID64, names, player counts) to the public directory; off unless the host turns it on.</summary>
    public bool Advertise { get; set; }
    /// <summary>Set once the host has saved the Host screen, so Advertise reflects a real choice.</summary>
    public bool AdvertiseChosen { get; set; }
    /// <summary>Asks se2-online.yellow51.dev for the newest release once per game start.</summary>
    public bool CheckForUpdates { get; set; } = true;
    public bool AllowDirectIp { get; set; } = true;
    public string BindAddress { get; set; } = "0.0.0.0";
    public int ListenPort { get; set; } = 7742;
    public string PublicAddress { get; set; } = "";
    public string ServersDirectory { get; set; } = "https://se2-online-servers.yellow51.dev";
    public string SessionsDirectory { get; set; } = "https://se2-online-sessions.yellow51.dev";
    public int MaxPlayers { get; set; } = 8;
    public bool FriendsOnly { get; set; } = true;
    public string PlayerName { get; set; } = "";
    public bool TeamGrids { get; set; } = true;
    public bool SharedResearch { get; set; } = true;
    public bool Nametags { get; set; } = true;
    public double NametagRange { get; set; } = 200;
    public bool SpawnNearHost { get; set; }
    public bool AutoReconnect { get; set; } = true;
    public int InterpolationMs { get; set; } = 60;
    public int StateBudgetKiB { get; set; } = 1024;
    public string Password { get; set; } = "";
    public string SkippedUpdate { get; set; } = "";
    /// <summary>Players can damage each other and turrets/sensors treat players outside your faction as enemies.</summary>
    public bool Pvp { get; set; } = true;
    public bool OfflineRaidProtection { get; set; } = true;
    /// <summary>Per-player PCU limit; leave out to keep the world's value, 0 for unlimited.</summary>
    public int? PcuLimit { get; set; }
    /// <summary>Whether the sun moves across the sky; leave out to keep the world's setting.</summary>
    public bool? SunRotation { get; set; }
    /// <summary>Minutes for one full day (one sun rotation); leave out to keep the world's setting.</summary>
    public double? SunPeriodMinutes { get; set; }
    /// <summary>Joiners only receive grids, characters and objects within this many metres of them; 0 sends the whole world.</summary>
    public double StreamingRadiusMeters { get; set; }
    /// <summary>Experimental: mirrors debug-menu water to joiners. Costs bandwidth and performance; not meant for full-time use yet.</summary>
    public bool WaterSync { get; set; }
    /// <summary>Hosting: the respawn ships players may choose (Pathfinder, ...); empty offers every ship.</summary>
    public List<string> RespawnShips { get; set; } = new();
    /// <summary>Hosting: minutes before a player can take another respawn ship; 0 for no wait.</summary>
    public int RespawnShipCooldownMinutes { get; set; } = 10;
    /// <summary>Hosting: players hear each other's voice within this many metres; 0 turns proximity voice off.</summary>
    public double VoiceRadiusMeters { get; set; } = 50;
    /// <summary>Hosting: how fast suits and blocks use energy, hydrogen and oxygen; 1 is the game's rate, 0 uses none, up to 10.</summary>
    public double EnergyUsageMultiplier { get; set; } = 1;
    public double HydrogenUsageMultiplier { get; set; } = 1;
    public double OxygenUsageMultiplier { get; set; } = 1;
    [System.Text.Json.Serialization.JsonIgnore] public ResourceMultipliers UsageMultipliers => new(EnergyUsageMultiplier, HydrogenUsageMultiplier, OxygenUsageMultiplier);
    /// <summary>This player's own voice: whether to talk and listen at all.</summary>
    public bool VoiceEnabled { get; set; } = true;
    /// <summary>Transmit whenever Steam hears speech instead of only while the push-to-talk key is held.</summary>
    public bool VoiceOpenMic { get; set; }
    public string VoiceKey { get; set; } = "I";
    /// <summary>Percent, 0-200.</summary>
    public int VoiceVolume { get; set; } = 100;
    /// <summary>Player names whose voice this player does not hear.</summary>
    public List<string> MutedVoices { get; set; } = new();
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public bool? PlayerTurrets { get => null; set { if (value.HasValue) Pvp = value.Value; } }
    public string ChatKey { get; set; } = "Enter";
    public string PlayersKey { get; set; } = "F3";
    public string AdminKey { get; set; } = "Alt+F10";
    public string BroadcastKey { get; set; } = "O";
    /// <summary>Hold for the emote radial menu. N is free in SE2 2.4's default bindings.</summary>
    public string EmoteKey { get; set; } = "N";
    /// <summary>Whether other players see this player's name tag (the suit antenna broadcast).</summary>
    public bool SuitBroadcast { get; set; } = true;

    public string Validate()
    {
        if (string.IsNullOrWhiteSpace(SessionName) || SessionName.Length > 100 || Description == null || Description.Length > 1000) return "Enter a session name up to 100 characters and a description up to 1000 characters.";
        foreach (char value in SessionName) if (char.IsControl(value)) return "Session name must be on one line without control characters.";
        foreach (char value in Description) if (char.IsControl(value) && value is not ('\r' or '\n' or '\t')) return "Description contains an invalid control character.";
        if (ListenPort < 1 || ListenPort > 65535) return "Listen port must be between 1 and 65535.";
        try
        {
            ServerEndpoint.Parse(BindEndpoint, listen: true);
            if (!string.IsNullOrEmpty(PublicAddress)) ServerEndpoint.Parse(PublicAddress);
            DirectoryClient.ValidateServiceUrl(ServersDirectory);
            DirectoryClient.ValidateServiceUrl(SessionsDirectory);
        }
        catch (FormatException ex) { return ex.Message; }
        if (MaxPlayers < 2 || MaxPlayers > 32) return "Player limit must be between 2 and 32, including the host.";
        if (NametagRange < 1 || NametagRange > 100000 || !double.IsFinite(NametagRange)) return "Name tag range must be between 1 and 100000 metres.";
        if (SunPeriodMinutes is double day && !(day >= 1 && day <= 10080)) return "Day length must be between 1 minute and 7 days (10080 minutes).";
        if (!Sync.StreamingRules.Valid(StreamingRadiusMeters)) return $"Streaming radius must be 0 (off) or between {Sync.StreamingRules.MinRadius:0} and {Sync.StreamingRules.MaxRadius:0} metres.";
        if (ResourceMultipliers.Validate(EnergyUsageMultiplier, HydrogenUsageMultiplier, OxygenUsageMultiplier) is string usage) return usage;
        if (!Sync.VoiceRelay.ValidRadius(VoiceRadiusMeters)) return $"Voice radius must be 0 (off) or between {Sync.VoiceRelay.MinRadius:0} and {Sync.VoiceRelay.MaxRadius:0} metres.";
        if (VoiceVolume < 0 || VoiceVolume > 200) return "Voice volume must be between 0 and 200 percent.";
        if (Sync.RespawnShips.Validate(RespawnShips, RespawnShipCooldownMinutes) is { } ships) return ships;
        if (MutedVoices == null || MutedVoices.Count > 200) return "Too many muted players.";
        if (InterpolationMs < 0 || InterpolationMs > 250) return "Motion smoothing must be between 0 and 250 milliseconds.";
        if (StateBudgetKiB < 32 || StateBudgetKiB > 4096) return "Update bandwidth must be between 32 and 4096 KiB/s.";
        if (PlayerName == null || PlayerName.Trim().Length > 32 || PlayerName.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0) return "Player name must be at most 32 characters on one line.";
        if (Password == null || Password.Length > 64 || Password.Any(char.IsControl)) return "Password must be at most 64 characters on one line.";
        foreach (var (label, key) in new[] { ("Chat", ChatKey), ("Players", PlayersKey), ("Admin panel", AdminKey), ("Suit broadcast", BroadcastKey), ("Push to talk", VoiceKey), ("Emotes", EmoteKey) })
            if (!KeyBinding.TryParse(key, out _)) return label + " key binding '" + key + "' is not a valid key.";
        return null;
    }

    public MpOptions HostOptions(bool local) => Options(local ? "host-ip" : "host");
    public string BindEndpoint => (BindAddress?.Contains(':') == true ? "[" + BindAddress + "]" : BindAddress) + ":" + ListenPort;
    public MpOptions JoinAddressOptions(string address) => Options("join-address:" + ServerEndpoint.Parse(address));

    public MpOptions JoinOptions(ulong steamId, bool local, int virtualPort = MpOptions.DefaultVirtualPort)
    {
        var options = Options(local ? "join-ip" : "join:" + MpOptions.ParseSteamId(steamId.ToString(CultureInfo.InvariantCulture)), virtualPort);
        return options;
    }

    private List<string> HostArgs(string mode, int virtualPort)
    {
        string error = Validate();
        if (error != null) throw new ArgumentException(error);
        var args = new List<string>
        {
            MpOptions.Prefix + mode,
            MpOptions.Prefix + "grids:" + (TeamGrids ? "team" : "strict"),
            MpOptions.Prefix + "research:" + (SharedResearch ? "shared" : "own"),
            MpOptions.Prefix + "nametags:" + (Nametags ? NametagRange.ToString(CultureInfo.InvariantCulture) : "off"),
            MpOptions.Prefix + "spawn:" + (SpawnNearHost ? "host" : "respawn"),
            MpOptions.Prefix + "rejoin:" + (AutoReconnect ? "on" : "off"),
            MpOptions.Prefix + "interp:" + InterpolationMs,
            MpOptions.Prefix + "statebudget:" + StateBudgetKiB,
            MpOptions.Prefix + "vport:" + virtualPort
        };
        if (!string.IsNullOrWhiteSpace(PlayerName)) args.Add(MpOptions.Prefix + "name:" + PlayerName.Trim());
        if (mode == "host" && AllowDirectIp) args.Add(MpOptions.Prefix + "listen-address:" + BindEndpoint);
        return args;
    }

    private MpOptions Options(string mode, int virtualPort = MpOptions.DefaultVirtualPort)
    {
        var options = MpOptions.Parse(HostArgs(mode, virtualPort));
        if (options.Errors.Count > 0) throw new ArgumentException(string.Join("\n", options.Errors));
        options.MaxPlayers = MaxPlayers;
        options.FriendsOnly = FriendsOnly;
        options.Advertise = Advertise;
        options.SessionName = SessionName.Trim();
        options.Description = Description.Trim();
        options.PublicAddress = PublicAddress ?? "";
        options.Password = Password ?? "";
        options.Pvp = Pvp;
        options.OfflineRaidProtection = OfflineRaidProtection;
        options.PcuLimit = PcuLimit;
        options.SunRotation = SunRotation;
        options.SunPeriodMinutes = SunPeriodMinutes;
        options.StreamingRadiusMeters = StreamingRadiusMeters;
        options.WaterSync = WaterSync;
        options.VoiceRadiusMeters = VoiceRadiusMeters;
        options.UsageMultipliers = UsageMultipliers;
        options.ServersDirectory = ServersDirectory;
        options.SessionsDirectory = SessionsDirectory;
        return options;
    }
}
