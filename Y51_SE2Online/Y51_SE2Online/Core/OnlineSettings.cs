using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.Core;

public sealed class OnlineSettings : System.Text.Json.Serialization.IJsonOnDeserializing, System.Text.Json.Serialization.IJsonOnDeserialized
{
    public string SessionName { get; set; } = "My SE2 session";
    public string Description { get; set; } = "";
    /// <summary>Publishes the session (host SteamID64, names, player counts) to the public directory. On for new settings; a file without the key keeps the old default, off.</summary>
    public bool Advertise { get; set; } = true;
    /// <summary>Set once the host has saved the Host screen (and for new settings), so Advertise reflects a real choice.</summary>
    public bool AdvertiseChosen { get; set; } = true;

    // Runs before a file's values are read: a key the file leaves out keeps the default it was written under (listing off),
    // only new settings start public. FriendsOnly is left alone: a dedicated server.json that omits it must stay false.
    void System.Text.Json.Serialization.IJsonOnDeserializing.OnDeserializing() { Advertise = false; AdvertiseChosen = false; ResearchSharing = null; _sharedResearch = null; }
    // A file from before 0.18.3 has only SharedResearch: false there meant each player on their own.
    void System.Text.Json.Serialization.IJsonOnDeserialized.OnDeserialized() => ResearchSharing ??= _sharedResearch == false ? Sync.Sharing.Individual : Sync.Sharing.Everyone;
    /// <summary>Asks se2-online.yellow51.dev for the newest release once per game start.</summary>
    public bool CheckForUpdates { get; set; } = true;
    /// <summary>Plain -plugins: installs download, verify and install a newer release themselves, then offer a restart.</summary>
    public bool AutoUpdate { get; set; } = true;
    public bool AllowDirectIp { get; set; } = true;
    public string BindAddress { get; set; } = "0.0.0.0";
    public int ListenPort { get; set; } = 7742;
    /// <summary>Advanced override: the address the server is listed at. Empty lists the one the directory sees (see ListedAddress).</summary>
    public string PublicAddress { get; set; } = "";
    public string ServersDirectory { get; set; } = "https://se2-online-servers.yellow51.dev";
    public string SessionsDirectory { get; set; } = "https://se2-online-sessions.yellow51.dev";
    public const int MinPlayers = 2, PlayerLimit = 384, StablePlayers = 64;
    public const string UnstablePlayersWarning = "More than 64 players may be unstable (lag, desync, long saves).";
    public static bool Unstable(int maxPlayers) => maxPlayers > StablePlayers;
    public int MaxPlayers { get; set; } = 8;
    /// <summary>New hosts start public (listed, anyone can join); every saved settings file states its own choice.</summary>
    public bool FriendsOnly { get; set; }
    public string PlayerName { get; set; } = "";
    /// <summary>A world SE2 Online never hosted shares its grids with everyone when it is first hosted. Off for new hosts: up
    /// to 0.18.0-pre4 it shared every grid without SE2 Online's sharing at every load.</summary>
    public bool TeamGrids { get; set; }
    /// <summary>Hosting: who shares research (block unlocks and discoveries): everyone, faction (members of the same SE2 Online faction) or individual.</summary>
    public string ResearchSharing { get; set; } = Sync.Sharing.Everyone;
    private bool? _sharedResearch;
    /// <summary>Read only, from settings saved before 0.18.3; ResearchSharing replaces it.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public bool? SharedResearch { get => null; set => _sharedResearch = value; }
    /// <summary>Hosting: who shares contracts (accepting, progress and rewards): everyone (SE2's own), faction or individual.</summary>
    public string ContractSharing { get; set; } = Sync.Sharing.Everyone;
    /// <summary>Hosting: blocks unlock through research; off makes every block available to everyone (unlocks are kept).</summary>
    public bool Progression { get; set; } = true;
    [System.Text.Json.Serialization.JsonIgnore] public Sync.ResearchMode Research => Sync.Sharing.TryParse(ResearchSharing, out var mode) ? mode : Sync.ResearchMode.Shared;
    [System.Text.Json.Serialization.JsonIgnore] public Sync.ResearchMode Contracts => Sync.Sharing.TryParse(ContractSharing, out var mode) ? mode : Sync.ResearchMode.Shared;
    public bool Nametags { get; set; } = true;
    public double NametagRange { get; set; } = 200;
    public bool SpawnNearHost { get; set; }
    public bool AutoReconnect { get; set; } = true;
    public int InterpolationMs { get; set; } = 60;
    public int StateBudgetKiB { get; set; } = 1024;
    public string Password { get; set; } = "";
    /// <summary>Hosting: message of the day shown to each player after they spawn; {player}, {server}, {online} and {max} are filled in. The admin panel's message replaces it for a world.</summary>
    public string Motd { get; set; } = "";
    /// <summary>Hosting: title of the message of the day; empty uses the session name.</summary>
    public string MotdTitle { get; set; } = "";
    /// <summary>Hosting: the country shown with this server in the directory, as a 2-letter code (US, DE, ...), or "auto" to use the location of its IP.</summary>
    public string Country { get; set; } = Countries.Auto;

    /// <summary>The public address as listed: one typed without a port gets this host's listen port (the directory requires one).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string PublishedAddress
    {
        get
        {
            string text = PublicAddress?.Trim() ?? "";
            if (text.Length == 0) return "";
            if (System.Net.IPAddress.TryParse(text, out var ip)) return ListedAddress.Endpoint(ip, ListenPort);
            return text.Contains(':') ? text : text + ":" + ListenPort.ToString(CultureInfo.InvariantCulture);
        }
    }
    public string SkippedUpdate { get; set; } = "";
    /// <summary>Joining: the most Workshop mod data, in GiB, this game downloads for one server; a server needing more is refused.</summary>
    public int MaxModDownloadGiB { get; set; } = ModDownloads.DefaultMaxGiB;
    /// <summary>Players can damage each other and turrets/sensors treat players outside your faction as enemies.</summary>
    public bool Pvp { get; set; } = true;
    public bool OfflineRaidProtection { get; set; } = true;
    /// <summary>Hosting: offline players' parked characters use no suit energy, oxygen or hydrogen and cannot suffocate until they are back.</summary>
    public bool PauseOfflineStats { get; set; } = true;
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
    /// <summary>Hosting: each player's own game predicts their character on foot and by jetpack, so it responds at once at any ping and while the server lags; the server still simulates it from the player's inputs and has the last word.</summary>
    public bool ClientMovement { get; set; } = true;
    /// <summary>Hosting: SE2 Online's own replication engine carries the world to players (ordering, lanes, motion timing); off falls back to the game's own netcode. Players need the same setting from the host, which they get on joining.</summary>
    public bool OwnReplication { get; set; } = true;
    /// <summary>Hosting: only the player who sat down first in one of a ship's control seats flies it; others in its seats are passengers.</summary>
    public bool OnePilot { get; set; } = true;
    /// <summary>Hosting: every 5 s the server checks each player's world against its own and logs what stays different.</summary>
    public bool DesyncChecks { get; set; } = true;
    /// <summary>Hosting: players see the placement outline of anyone building near them.</summary>
    public bool BuildGhosts { get; set; } = true;
    /// <summary>Hosting: the respawn ships built from blueprints (the Banana Boat and the Atmospheric Lander by default).</summary>
    public List<Sync.RespawnBlueprint> RespawnBlueprints { get; set; } = Sync.RespawnShips.DefaultBlueprints();
    /// <summary>Hosting: also offer the vanilla encounter ships (Pathfinder, Nova 35, ...) as respawn ships.</summary>
    public bool VanillaRespawnShips { get; set; }
    /// <summary>Hosting: with <see cref="VanillaRespawnShips"/>, the vanilla ships players may choose (Pathfinder, ...); empty offers every ship.</summary>
    public List<string> RespawnShips { get; set; } = new();
    /// <summary>Hosting: minutes before a player can take another respawn ship; 0 for no wait.</summary>
    public int RespawnShipCooldownMinutes { get; set; } = 10;
    /// <summary>Hosting: players hear each other's voice within this many metres; 0 turns proximity voice off.</summary>
    public double VoiceRadiusMeters { get; set; } = 50;
    /// <summary>Hosting: how fast suits and blocks use energy, hydrogen and oxygen; 1 is the game's rate, 0 uses none, up to 10.</summary>
    public double EnergyUsageMultiplier { get; set; } = 1;
    public double HydrogenUsageMultiplier { get; set; } = 1;
    public double OxygenUsageMultiplier { get; set; } = 1;
    /// <summary>Hosting: how much the player inventory and cargo containers (crates, lockers, barrels too) hold; 1 is the game's size.</summary>
    public double PlayerInventoryMultiplier { get; set; } = 1;
    public double CargoInventoryMultiplier { get; set; } = 1;
    [System.Text.Json.Serialization.JsonIgnore] public InventoryMultipliers InventorySizes => new(PlayerInventoryMultiplier, CargoInventoryMultiplier);
    [System.Text.Json.Serialization.JsonIgnore] public ResourceMultipliers UsageMultipliers => new(EnergyUsageMultiplier, HydrogenUsageMultiplier, OxygenUsageMultiplier);
    /// <summary>This player's own voice: whether to talk and listen at all.</summary>
    public bool VoiceEnabled { get; set; } = true;
    /// <summary>Transmit whenever Steam hears speech instead of only while the push-to-talk key is held.</summary>
    public bool VoiceOpenMic { get; set; }
    public string VoiceKey { get; set; } = "I";
    /// <summary>Percent, 0-200.</summary>
    public int VoiceVolume { get; set; } = 100;
    /// <summary>Player names whose voice this player does not hear.</summary>
    public const int MaxMutedVoices = 1000;
    public List<string> MutedVoices { get; set; } = new();
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public bool? PlayerTurrets { get => null; set { if (value.HasValue) Pvp = value.Value; } }
    public string ChatKey { get; set; } = "Enter";
    public string PlayersKey { get; set; } = "F3";
    /// <summary>Not Alt+F10 (SE1's key): NVIDIA's overlay takes it for "save instant replay". Saved settings keep their key.</summary>
    public string AdminKey { get; set; } = "Ctrl+F10";
    public string BroadcastKey { get; set; } = "O";
    /// <summary>Hold for the emote radial menu. N is free in SE2 2.4's default bindings.</summary>
    public string EmoteKey { get; set; } = "N";
    /// <summary>Whether other players see this player's name tag (the suit antenna broadcast).</summary>
    public bool SuitBroadcast { get; set; } = true;
    /// <summary>Hosting: players can take over grids by hacking a terminal or control block (Alt+H by default). Off unless the host opts in.</summary>
    public bool Hacking { get; set; }
    /// <summary>Hosting: scales every hack's duration (0.1-10).</summary>
    public double HackTimeMultiplier { get; set; } = 1;
    /// <summary>Hosting: other players' grids can be hacked too (still only with PvP on and never inside safe zones). Off unless the host opts in.</summary>
    public bool AllowHackingPlayerGrids { get; set; }
    /// <summary>Set once the host has saved the Host screen, so Hacking and AllowHackingPlayerGrids reflect a real choice.</summary>
    public bool HackingChosen { get; set; }
    /// <summary>Hosting: how long an owner's remote lockdown freezes a hack; 0 turns lockdown off.</summary>
    public int HackLockdownSeconds { get; set; } = 60;
    /// <summary>Hosting: wait before the same grid can be locked down again.</summary>
    public int HackLockdownCooldownSeconds { get; set; } = 300;
    public string HackKey { get; set; } = "Alt+H";
    /// <summary>Tap: ping what you look at. Hold: the radial menu (Go, Danger, Mine, Build, Regroup, Look). Ctrl+key on a ping keeps it as a GPS marker.</summary>
    public string PingKey { get; set; } = "Alt+G";
    /// <summary>Tap: cycle the marker filter (All, Relevant, Pings only, Off). Hold: the per-category toggles.</summary>
    public string MarkerFilterKey { get; set; } = "Alt+M";
    /// <summary>The player's marker filter, "" for the server's default.</summary>
    public string MarkerMode { get; set; } = "";
    /// <summary>The player's marker categories turned off (pings, gps, players, signals, quest, ore); null for the server's default.</summary>
    public List<string> MarkersOff { get; set; }
    /// <summary>How many of our markers show in full in the Relevant filter (3 to 10).</summary>
    public int MarkerFullCount { get; set; } = Sync.MarkerRelevance.DefaultFullCount;
    /// <summary>Markers within this many degrees of the crosshair always show in full (2 to 30).</summary>
    public double MarkerAimDegrees { get; set; } = 8;
    /// <summary>Hosting: who sees a player's pings: faction (default), allies (faction and allied factions), all, or range.</summary>
    public string PingAudience { get; set; } = "faction";
    /// <summary>Hosting, with PingAudience range: players within this many metres of the pinger see it (their faction always does).</summary>
    public double PingRangeMeters { get; set; } = 5000;
    /// <summary>Hosting: seconds a ping stays.</summary>
    public int PingLifetimeSeconds { get; set; } = 60;
    /// <summary>Hosting: pings in a burst, then one every PingRefillSeconds.</summary>
    public int PingBurst { get; set; } = 3;
    public double PingRefillSeconds { get; set; } = 5;
    /// <summary>Hosting: how far from their character a player may ping, in metres.</summary>
    public double PingMaxRangeMeters { get; set; } = 2000;
    /// <summary>Hosting: words replaced by *** in ping labels (none by default).</summary>
    public List<string> PingWordFilter { get; set; } = new();
    /// <summary>Hosting: the marker filter players start with (a player's own choice overrides it).</summary>
    public Sync.MarkerDefaults MarkerDefaults { get; set; } = new();

    /// <summary>The ping settings as the host uses them.</summary>
    public Sync.PingSettings PingSettingsOf()
        => new(Sync.PingRules.TryAudience(PingAudience, out var audience) ? audience : Sync.PingAudience.Faction, PingRangeMeters, PingLifetimeSeconds, PingBurst, PingRefillSeconds, PingMaxRangeMeters,
            (PingWordFilter ?? new()).Where(w => !string.IsNullOrWhiteSpace(w)).Select(w => w.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());

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
        if (MaxPlayers < MinPlayers || MaxPlayers > PlayerLimit) return $"Player limit must be between {MinPlayers} and {PlayerLimit}, including the host.";
        if (NametagRange < 1 || NametagRange > 100000 || !double.IsFinite(NametagRange)) return "Name tag range must be between 1 and 100000 metres.";
        if (SunPeriodMinutes is double day && !(day >= 1 && day <= 10080)) return "Day length must be between 1 minute and 7 days (10080 minutes).";
        if (!Sync.StreamingRules.Valid(StreamingRadiusMeters)) return $"Streaming radius must be 0 (off) or between {Sync.StreamingRules.MinRadius:0} and {Sync.StreamingRules.MaxRadius:0} metres.";
        if (ResourceMultipliers.Validate(EnergyUsageMultiplier, HydrogenUsageMultiplier, OxygenUsageMultiplier) is string usage) return usage;
        if (InventoryMultipliers.Validate(PlayerInventoryMultiplier, CargoInventoryMultiplier) is string sizes) return sizes;
        if (!Sync.VoiceRelay.ValidRadius(VoiceRadiusMeters)) return $"Voice radius must be 0 (off) or between {Sync.VoiceRelay.MinRadius:0} and {Sync.VoiceRelay.MaxRadius:0} metres.";
        if (VoiceVolume < 0 || VoiceVolume > 200) return "Voice volume must be between 0 and 200 percent.";
        if (Sync.RespawnShips.Validate(RespawnShips, RespawnShipCooldownMinutes) is { } ships) return ships;
        if (Sync.RespawnShips.ValidateBlueprints(RespawnBlueprints) is { } blueprints) return blueprints;
        if (!Sync.PingRules.TryAudience(PingAudience, out _)) return "PingAudience must be faction, allies, all or range.";
        if (!(PingRangeMeters >= 50 && PingRangeMeters <= 1_000_000)) return "Ping range must be between 50 and 1000000 metres.";
        if (PingLifetimeSeconds < Sync.PingRules.MinLifetimeS || PingLifetimeSeconds > Sync.PingRules.MaxLifetimeS) return $"Ping lifetime must be between {Sync.PingRules.MinLifetimeS} and {Sync.PingRules.MaxLifetimeS} seconds.";
        if (PingBurst < 1 || PingBurst > 20 || !(PingRefillSeconds >= 0.5 && PingRefillSeconds <= 600)) return "Ping burst must be 1 to 20 and the refill 0.5 to 600 seconds.";
        if (!(PingMaxRangeMeters >= 10 && PingMaxRangeMeters <= 1_000_000)) return "Ping maximum range must be between 10 and 1000000 metres.";
        if (PingWordFilter == null || PingWordFilter.Count > 500) return "PingWordFilter must be a list of at most 500 words.";
        if (!(MarkerAimDegrees >= 2 && MarkerAimDegrees <= 30)) return "MarkerAimDegrees must be between 2 and 30.";
        if (MarkerFullCount < Sync.MarkerRelevance.MinFullCount || MarkerFullCount > Sync.MarkerRelevance.MaxFullCount) return $"MarkerFullCount must be between {Sync.MarkerRelevance.MinFullCount} and {Sync.MarkerRelevance.MaxFullCount}.";
        if (!Sync.Sharing.TryParse(ResearchSharing, out _)) return "ResearchSharing must be everyone, faction or individual.";
        if (!Sync.Sharing.TryParse(ContractSharing, out _)) return "ContractSharing must be everyone, faction or individual.";
        if (MarkerDefaults == null || !Sync.PingRules.TryMode(MarkerDefaults.Mode, out _)) return "MarkerDefaults.Mode must be all, relevant, pings or off.";
        if ((MarkerDefaults.Off ?? new()).Any(c => !Sync.PingRules.TryCategory(c, out _))) return "MarkerDefaults.Off may list pings, gps, players, signals, quest and ore.";
        if (MaxModDownloadGiB < 1 || MaxModDownloadGiB > ModDownloads.MaxGiBLimit) return $"Workshop download limit must be between 1 and {ModDownloads.MaxGiBLimit} GiB.";
        if (MutedVoices == null|| MutedVoices.Count > MaxMutedVoices) return "Too many muted players.";
        if (HackRules.Validate(this) is string hacking) return hacking;
        if (InterpolationMs < 0 || InterpolationMs > 250) return "Motion smoothing must be between 0 and 250 milliseconds.";
        if (StateBudgetKiB < 32 || StateBudgetKiB > 4096) return "Update bandwidth must be between 32 and 4096 KiB/s.";
        if (PlayerName == null || PlayerName.Trim().Length > 32 || PlayerName.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0) return "Player name must be at most 32 characters on one line.";
        if (Countries.Normalize(Country) == null) return "Country must be auto or a 2-letter country code such as US, GB or DE.";
        if (Password == null || Password.Length > 64 || Password.Any(char.IsControl)) return "Password must be at most 64 characters on one line.";
        if (Motd == null || Motd.Length > Core.Motd.MaxText) return $"The message of the day can be at most {Core.Motd.MaxText} characters.";
        if (MotdTitle == null || MotdTitle.Length > Core.Motd.MaxTitle || MotdTitle.Any(char.IsControl)) return $"The message of the day title must be at most {Core.Motd.MaxTitle} characters on one line.";
        if (PcuLimit < 0) return "PCU limit must be 0 (unlimited) or more; leave it empty to keep the world's limit.";
        foreach (var (label, key) in new[] { ("Chat", ChatKey), ("Players", PlayersKey), ("Admin panel", AdminKey), ("Suit broadcast", BroadcastKey), ("Push to talk", VoiceKey), ("Emotes", EmoteKey), ("Hack", HackKey), ("Ping", PingKey), ("Marker filter", MarkerFilterKey) })
            if (!KeyBinding.TryParse(key, out _)) return label + " key binding '" + key + "' is not a valid key.";
        return null;
    }

    public MpOptions HostOptions(bool local) => Options(local ? "host-ip" : "host");
    public string BindEndpoint => (BindAddress?.Contains(':') == true ? "[" + BindAddress + "]" : BindAddress) + ":" + ListenPort;
    public MpOptions JoinAddressOptions(string address) => Options("join-address:" + ServerEndpoint.Parse(address));

    public MpOptions JoinCodeOptions(string code)
    {
        if (!Keen.VRage.Core.Services.GameSessions.SessionJoinCode.TryParse(code?.Trim(), out var parsed)) throw new ArgumentException("A join code is 8 letters and digits, like ABCD-1234.");
        var options = Options("join:wait");
        options.JoinCode = parsed;
        return options;
    }

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
            MpOptions.Prefix + "research:" + (Research switch { Sync.ResearchMode.Own => "own", Sync.ResearchMode.Faction => "faction", _ => "shared" }),
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
        options.Country = Countries.Normalize(Country) ?? "";
        options.PublicAddress = PublishedAddress;
        options.Password = Password ?? "";
        options.Motd = Motd ?? "";
        options.MotdTitle = MotdTitle ?? "";
        options.Pvp = Pvp;
        options.Contracts = Contracts;
        options.Progression = Progression;
        options.OfflineRaidProtection = OfflineRaidProtection;
        options.PcuLimit = PcuLimit;
        options.SunRotation = SunRotation;
        options.SunPeriodMinutes = SunPeriodMinutes;
        options.StreamingRadiusMeters = StreamingRadiusMeters;
        options.WaterSync = WaterSync;
        options.ClientMovement = ClientMovement;
        options.OwnReplication = OwnReplication;
        options.OnePilot = OnePilot;
        options.DesyncChecks = DesyncChecks;
        options.VoiceRadiusMeters = VoiceRadiusMeters;
        options.Pings = PingSettingsOf();
        options.MarkerDefaults = MarkerDefaults ?? new();
        options.UsageMultipliers = UsageMultipliers;
        options.InventorySizes = InventorySizes;
        options.ServersDirectory = ServersDirectory;
        options.SessionsDirectory = SessionsDirectory;
        return options;
    }
}
