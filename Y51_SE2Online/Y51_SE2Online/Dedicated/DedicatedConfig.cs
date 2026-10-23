using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Keen.VRage.Core.Worlds;
using Keen.VRage.Library.Serialization;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.Dedicated;

public sealed class DedicatedConfig
{
    public string SaveId { get; set; } = "";
    public string StartContent { get; set; } = "";
    /// <summary>Steam Workshop item (id or link) the world starts from: a Workshop world, or a mod that ships worlds (StartContent then names one).</summary>
    public string StartWorkshop { get; set; } = "";
    [JsonIgnore] public ulong StartWorkshopId { get; private set; }
    // The name (and SessionName's below) a server.json without the key has always run with: kept, so an older file keeps its
    // world. New servers get "Default SE2 Online World" / "Default SE2 Online Server" from server.example.json.
    public string WorldName { get; set; } = "Y51 Dedicated World";
    public bool? EnableAutosave { get; set; } = true;
    public bool? PlayMission { get; set; }
    public int SaveIntervalSeconds { get; set; } = 300;
    public bool SaveOnExit { get; set; } = true;
    /// <summary>Seconds of chat warnings before a stop while players are online; 0 stops at once.</summary>
    public int ShutdownCountdownSeconds { get; set; } = 60;
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public RestartSettings AutoRestart { get; set; } = new();
    /// <summary>Read by the runner's live console: signed server updates from the website, installed with a warned restart.</summary>
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public UpdateSettings AutoUpdate { get; set; } = new();
    /// <summary>Message of the day shown to each joiner; {player}, {server}, {online} and {max} are filled in.</summary>
    public string Motd { get; set; } = "";
    public string MotdTitle { get; set; } = "";
    public JsonElement? OverrideModList { get; set; }
    /// <summary>Steam Workshop items (ids or links) the world loads on top of its own mods; downloaded at startup.</summary>
    public List<JsonElement> Mods { get; set; } = new();
    [JsonIgnore] public ulong[] WorkshopIds { get; private set; } = Array.Empty<ulong>();
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public OnlineSettings Network { get; set; } = new OnlineSettings
    {
        FriendsOnly = false, Advertise = false, SessionName = "Y51 Dedicated Server", AllowDirectIp = true,
        // Not co-op's 7742: a dedicated server and a co-op host on one PC no longer fight over the port.
        ListenPort = 27016,
        StreamingRadiusMeters = Y51_SE2Online.Sync.StreamingRules.DedicatedDefaultRadius
    };
    public Dictionary<string, Dictionary<string, JsonElement>> SessionSettings { get; set; } = new();
    public Dictionary<string, Dictionary<string, JsonElement>> DefinitionSettings { get; set; } = new();
    public string SettingsDumpFile { get; set; } = "native-settings.json";
    public string DefinitionDumpFile { get; set; } = "native-definitions.json";
    public string ResumeFile { get; set; } = "dedicated-state.json";
    public string CommandFile { get; set; } = "dedicated-command.txt";
    /// <summary>Headless: how far behind the wall clock the server still runs missed ticks back to back (ms); 0 keeps Keen's 83 ms.</summary>
    public int CatchUpMs { get; set; } = 1000;
    /// <summary>Headless: the game's worker threads; 0 keeps the game's own count (up to 15).</summary>
    public int WorkerThreads { get; set; }
    /// <summary>Headless: wait for each tick on a high-resolution timer; false uses Thread.Sleep, whose wake-ups can run tens of ms late.</summary>
    public bool PreciseTimer { get; set; } = true;
    /// <summary>Headless: priority of the server loop thread (Normal, AboveNormal or Highest) within the above-normal process.</summary>
    public string LoopPriority { get; set; } = "AboveNormal";
    /// <summary>Opt-in JSONL server frame and network profile (relative to this file's folder); empty is off.</summary>
    public string ProfileFile { get; set; } = "";
    /// <summary>Remote console (Source RCON over TCP): off until the host turns it on and gives it a way in.</summary>
    public bool RconEnabled { get; set; }
    /// <summary>TCP port; not the game port or its query port (game + 1), never 7742 (co-op hosting).</summary>
    public int RconPort { get; set; } = 27020;
    /// <summary>Address the remote console listens on; empty uses Network.BindAddress.</summary>
    public string RconBindAddress { get; set; } = "";
    /// <summary>Signs in as Owner (every command); empty allows Steam sign-in only.</summary>
    public string RconPassword { get; set; } = "";
    /// <summary>Steam sign-in: the RCON tool sends a Steam auth ticket, and the account's place on the admin list gives its role.</summary>
    public bool RconSteamAuth { get; set; } = true;
    /// <summary>Commands a Steam sign-in may run over RCON by role (Admin, Player): command words, "*" for all. The password is Owner: every command.</summary>
    public Dictionary<string, List<string>> RconRoles { get; set; } = Core.RconAccess.DefaultRoles();
    [JsonIgnore] public string ConfigPath { get; private set; }
    [JsonIgnore] public string Directory => Path.GetDirectoryName(ConfigPath);
    [JsonIgnore] public string DumpPath => Path.GetFullPath(Path.Combine(Directory, SettingsDumpFile));
    [JsonIgnore] public string DefinitionDumpPath => Path.GetFullPath(Path.Combine(Directory, DefinitionDumpFile));
    [JsonIgnore] public string ResumePath => Path.GetFullPath(Path.Combine(Directory, ResumeFile));
    [JsonIgnore] public string CommandPath => Path.GetFullPath(Path.Combine(Directory, CommandFile));

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true, WriteIndented = true, IncludeFields = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    // Reading takes a file with options this build does not know (a newer version's server.json after a rollback): they
    // are named in the log and ignored, never a reason not to start.
    private static readonly JsonSerializerOptions Lenient = new(Json) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip };

    /// <summary>The option paths in a server.json this build has no setting for (one level into Network, AutoRestart...).</summary>
    internal static List<string> UnknownOptions(string text)
        => System.Text.Json.Nodes.JsonNode.Parse(text) is System.Text.Json.Nodes.JsonObject file
            ? JsonDefaults.UnknownKeys(file, (System.Text.Json.Nodes.JsonObject)JsonSerializer.SerializeToNode(new DedicatedConfig(), Json)!, FreeForm)
            : new List<string>();

    private static readonly HashSet<string> FreeForm = new(StringComparer.OrdinalIgnoreCase) { "SessionSettings", "DefinitionSettings", "RconRoles" };

    private static readonly HashSet<string> NotAdded = new(StringComparer.OrdinalIgnoreCase)
    {
        // Free-form overrides of the world's own settings: an entry the host did not write must stay unset.
        "SessionSettings", "DefinitionSettings",
        // Top-level settings on a dedicated server; under Network they are refused.
        "Network.Motd", "Network.MotdTitle",
        // Read-only, worked out from BindAddress and ListenPort.
        "Network.BindEndpoint",
        // The client's own record of the Host screen; a server.json states its hacking options outright.
        "Network.HackingChosen",
        // A host's own role lists: added whole when missing, never given a role the host left out.
        "RconRoles.Admin", "RconRoles.Player"
    };

    /// <summary>Writes options this version added into the host's server.json with their defaults; returns their paths.</summary>
    public static List<string> AddNewOptions(string path)
    {
        var defaults = (System.Text.Json.Nodes.JsonObject)JsonSerializer.SerializeToNode(new DedicatedConfig(), Json)!;
        return JsonDefaults.UpgradeFile(path, defaults, NotAdded, text => JsonSerializer.Deserialize<DedicatedConfig>(text, Lenient));
    }

    public static DedicatedConfig Read(string path)
    {
        path = Path.GetFullPath(path);
        string text = File.ReadAllText(path);
        var config = JsonSerializer.Deserialize<DedicatedConfig>(text, Lenient)
            ?? throw new ArgumentException("Dedicated configuration is empty.");
        // An unknown option is a newer version's setting (warned, ignored), unless it looks like a typo of a known one: then
        // the server does not start, rather than run with that setting (a password, a port) left at its default.
        var known = (System.Text.Json.Nodes.JsonObject)JsonSerializer.SerializeToNode(new DedicatedConfig(), Json)!;
        foreach (string option in UnknownOptions(text))
        {
            string name = option[(option.LastIndexOf('.') + 1)..];
            if (JsonDefaults.LikelyTypo(name, JsonDefaults.KnownKeysAt(known, option)) is { } meant)
                throw new ArgumentException($"{Path.GetFileName(path)}: '{option}' is not an option; did you mean '{meant}'? Fix the name (or remove it) and start again.");
            GameLog.Instance.Warn($"dedicated: unknown option {option} in {Path.GetFileName(path)} ignored (a newer version's setting)");
        }
        config.ConfigPath = path;
        if (!string.IsNullOrWhiteSpace(config.SaveId) && !string.IsNullOrWhiteSpace(config.StartContent))
            throw new ArgumentException("Choose SaveId or StartContent, not both.");
        config.StartWorkshop = config.StartWorkshop?.Trim() ?? "";
        if (config.StartWorkshop.Length > 0)
        {
            if (!Y51_SE2Online.Sync.WorldMods.TryParseWorkshopId(config.StartWorkshop, out ulong start))
                throw new ArgumentException($"StartWorkshop: '{config.StartWorkshop}' is not a Steam Workshop item id or link.");
            if (!string.IsNullOrWhiteSpace(config.SaveId)) throw new ArgumentException("Choose SaveId or StartWorkshop, not both.");
            config.StartWorkshopId = start;
            // The resume checkpoint compares this; switching between the id and its link is not a new world.
            config.StartWorkshop = start.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        if (string.IsNullOrWhiteSpace(config.WorldName) || config.WorldName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("WorldName must be a valid native save name.");
        if (config.SaveIntervalSeconds < 0 || (config.SaveIntervalSeconds > 0 && config.SaveIntervalSeconds < 30))
            throw new ArgumentException("SaveIntervalSeconds must be 0 or at least 30.");
        if (config.CatchUpMs is < 0 or > 10_000) throw new ArgumentException("CatchUpMs must be between 0 and 10000.");
        if (!TickPacer.IsLoopPriority(config.LoopPriority)) throw new ArgumentException("LoopPriority must be Normal, AboveNormal or Highest.");
        if (config.WorkerThreads is < 0 or > 256) throw new ArgumentException("WorkerThreads must be 0 (the game's own count) or between 1 and 256.");
        if (config.ShutdownCountdownSeconds is < 0 or > 600)
            throw new ArgumentException("ShutdownCountdownSeconds must be between 0 and 600.");
        config.AutoRestart.Validate();
        if (config.AutoUpdate == null || config.AutoUpdate.CheckMinutes is < 5 or > 1440) throw new ArgumentException("AutoUpdate.CheckMinutes must be between 5 and 1440.");
        if ((config.Motd ?? "").Length > Core.Motd.MaxText) throw new ArgumentException($"Motd can be at most {Core.Motd.MaxText} characters.");
        if ((config.MotdTitle ?? "").Length > Core.Motd.MaxTitle) throw new ArgumentException($"MotdTitle can be at most {Core.Motd.MaxTitle} characters.");
        if (config.Network == null) throw new ArgumentException("Network must be configured.");
        if (!string.IsNullOrEmpty(config.Network.Motd) || !string.IsNullOrEmpty(config.Network.MotdTitle)) throw new ArgumentException("Set Motd and MotdTitle at the top level of server.json, not under Network.");
        config.Network.Motd = config.Motd ?? "";
        config.Network.MotdTitle = config.MotdTitle ?? "";
        if (config.Network.FriendsOnly) throw new ArgumentException("Dedicated Steam servers have no Steam friends. Set Network.FriendsOnly to false.");
        if (!config.Network.AllowDirectIp) throw new ArgumentException("Dedicated servers require Network.AllowDirectIp=true for Steam Game Server IP networking.");
        if (config.Network.Advertise && string.IsNullOrWhiteSpace(config.Network.PublicAddress)) throw new ArgumentException("Set Network.PublicAddress to the public IP or hostname and port before advertising a dedicated server.");
        string error = config.Network.Validate();
        if (error != null) throw new ArgumentException(error);
        if (config.SessionSettings == null) throw new ArgumentException("SessionSettings cannot be null.");
        if (config.DefinitionSettings == null) throw new ArgumentException("DefinitionSettings cannot be null.");
        if (string.IsNullOrWhiteSpace(config.SettingsDumpFile)) throw new ArgumentException("SettingsDumpFile cannot be empty.");
        if (string.IsNullOrWhiteSpace(config.DefinitionDumpFile)) throw new ArgumentException("DefinitionDumpFile cannot be empty.");
        ValidateRcon(config);
        if (string.IsNullOrWhiteSpace(config.ResumeFile) || string.IsNullOrWhiteSpace(config.CommandFile))
            throw new ArgumentException("ResumeFile and CommandFile cannot be empty.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            config.ConfigPath, config.DumpPath, config.DefinitionDumpPath, config.ResumePath, config.CommandPath,
            Path.Combine(config.Directory, "dedicated-status.json"),
            Path.Combine(config.Directory, "dedicated-console.jsonl")
        };
        if (paths.Count != 7) throw new ArgumentException("Configuration, schema, resume, command, status and console log files must have distinct paths.");
        config.WorkshopIds = Y51_SE2Online.Sync.WorldMods.ParseWorkshopIds((config.Mods ?? new()).Select(m => m.ValueKind switch
        {
            JsonValueKind.Number => m.GetRawText(),
            JsonValueKind.String => m.GetString(),
            _ => throw new ArgumentException("Mods: each entry is a Workshop item id or link.")
        }));
        if (config.OverrideModList.HasValue)
            SerializationHelper.DeserializeFromJson<ModList>(config.OverrideModList.Value.GetRawText());
        NativeSessionSettings.Validate(config.SessionSettings);
        NativeDefinitionSettings.Validate(config.DefinitionSettings);
        return config;
    }

    private static void ValidateRcon(DedicatedConfig config)
    {
        config.RconBindAddress = config.RconBindAddress?.Trim() ?? "";
        config.RconPassword ??= "";
        config.RconRoles ??= Core.RconAccess.DefaultRoles();
        if (config.RconPort is < 1 or > 65535) throw new ArgumentException("RconPort must be between 1 and 65535.");
        if (config.RconBindAddress.Length > 0 && !System.Net.IPAddress.TryParse(config.RconBindAddress, out _))
            throw new ArgumentException($"RconBindAddress: '{config.RconBindAddress}' is not an IP address (empty uses Network.BindAddress).");
        if (config.RconPassword.Length > 128 || config.RconPassword.Any(char.IsControl)) throw new ArgumentException("RconPassword is one line of at most 128 characters.");
        foreach (var (role, commands) in config.RconRoles)
        {
            if (!role.Equals(Core.RconCaller.Admin, StringComparison.OrdinalIgnoreCase) && !role.Equals(Core.RconCaller.Player, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"RconRoles: '{role}' is not a role; use Admin or Player (the password signs in as Owner, with every command).");
            foreach (string command in commands ?? new())
                if (command != "*" && !Core.RconAccess.Commands.Contains(Core.RconAccess.Verb(command)))
                    throw new ArgumentException($"RconRoles.{role}: '{command}' is not an RCON command; use {string.Join(", ", Core.RconAccess.Commands)} or *.");
        }
        if (!config.RconEnabled) return;
        int game = config.Network?.ListenPort ?? 0;
        // RCON is TCP and the game UDP, but one number per service keeps firewall rules and port forwards unambiguous.
        if (config.RconPort == game || config.RconPort == game + 1)
            throw new ArgumentException($"RconPort {config.RconPort} is the game port or its Steam query port (Network.ListenPort {game} and {game + 1}); pick another, e.g. 27020.");
        if (config.RconPort == 7742) throw new ArgumentException("RconPort 7742 is SE2 Online's co-op hosting port; pick another, e.g. 27020.");
    }
}

/// <summary>Server auto-update (the runner checks, downloads and verifies; players get the AutoRestart warnings first).</summary>
public sealed class UpdateSettings
{
    public bool Enabled { get; set; } = true;
    public int CheckMinutes { get; set; } = 30;
}

/// <summary>Scheduled restarts (the runner relaunches the server) and restarts after a crash.</summary>
public sealed class RestartSettings
{
    /// <summary>Daily local server times, HH:mm.</summary>
    public string[] Times { get; set; } = Array.Empty<string>();
    /// <summary>Restart every N hours after startup; 0 off.</summary>
    public double EveryHours { get; set; }
    /// <summary>Chat warnings this many minutes before a scheduled restart, while players are online.</summary>
    public int[] WarnMinutes { get; set; } = { 15, 5, 1 };
    /// <summary>No scheduled restart sooner than this after startup.</summary>
    public int MinUptimeMinutes { get; set; } = 30;
    /// <summary>The runner relaunches a server that exits without its final save (a crash), at most this many times per hour.</summary>
    public bool RestartOnCrash { get; set; } = true;
    public int MaxCrashRestartsPerHour { get; set; } = 3;

    internal bool Scheduled => Times.Length > 0 || EveryHours > 0;

    internal void Validate()
    {
        Times ??= Array.Empty<string>();
        WarnMinutes ??= Array.Empty<int>();
        foreach (string time in Times)
            if (!RestartSchedule.TryParseTime(time, out _)) throw new ArgumentException($"AutoRestart.Times: '{time}' is not a HH:mm time.");
        if (EveryHours != 0 && !(EveryHours >= 0.5 && EveryHours <= 168)) throw new ArgumentException("AutoRestart.EveryHours must be 0 (off) or between 0.5 and 168.");
        if (WarnMinutes.Any(m => m is < 1 or > 120)) throw new ArgumentException("AutoRestart.WarnMinutes must be between 1 and 120.");
        if (MinUptimeMinutes is < 0 or > 1440) throw new ArgumentException("AutoRestart.MinUptimeMinutes must be between 0 and 1440.");
        if (MaxCrashRestartsPerHour is < 0 or > 20) throw new ArgumentException("AutoRestart.MaxCrashRestartsPerHour must be between 0 and 20.");
    }
}
