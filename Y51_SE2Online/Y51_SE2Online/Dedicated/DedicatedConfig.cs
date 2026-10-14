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
    public string WorldName { get; set; } = "Y51 Dedicated World";
    public bool? EnableAutosave { get; set; } = true;
    public bool? PlayMission { get; set; }
    public int SaveIntervalSeconds { get; set; } = 300;
    public bool SaveOnExit { get; set; } = true;
    /// <summary>Seconds of chat warnings before a stop while players are online; 0 stops at once.</summary>
    public int ShutdownCountdownSeconds { get; set; } = 60;
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public RestartSettings AutoRestart { get; set; } = new();
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
        FriendsOnly = false, Advertise = false, SessionName = "Y51 Dedicated Server", AllowDirectIp = true
    };
    public Dictionary<string, Dictionary<string, JsonElement>> SessionSettings { get; set; } = new();
    public Dictionary<string, Dictionary<string, JsonElement>> DefinitionSettings { get; set; } = new();
    public string SettingsDumpFile { get; set; } = "native-settings.json";
    public string DefinitionDumpFile { get; set; } = "native-definitions.json";
    public string ResumeFile { get; set; } = "dedicated-state.json";
    public string CommandFile { get; set; } = "dedicated-command.txt";
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

    public static DedicatedConfig Read(string path)
    {
        path = Path.GetFullPath(path);
        var config = JsonSerializer.Deserialize<DedicatedConfig>(File.ReadAllText(path), Json)
            ?? throw new ArgumentException("Dedicated configuration is empty.");
        config.ConfigPath = path;
        if (!string.IsNullOrWhiteSpace(config.SaveId) && !string.IsNullOrWhiteSpace(config.StartContent))
            throw new ArgumentException("Choose SaveId or StartContent, not both.");
        if (string.IsNullOrWhiteSpace(config.WorldName) || config.WorldName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("WorldName must be a valid native save name.");
        if (config.SaveIntervalSeconds < 0 || (config.SaveIntervalSeconds > 0 && config.SaveIntervalSeconds < 30))
            throw new ArgumentException("SaveIntervalSeconds must be 0 or at least 30.");
        if (config.ShutdownCountdownSeconds is < 0 or > 600)
            throw new ArgumentException("ShutdownCountdownSeconds must be between 0 and 600.");
        config.AutoRestart.Validate();
        if ((config.Motd ?? "").Length > Core.Motd.MaxText) throw new ArgumentException($"Motd can be at most {Core.Motd.MaxText} characters.");
        if ((config.MotdTitle ?? "").Length > Core.Motd.MaxTitle) throw new ArgumentException($"MotdTitle can be at most {Core.Motd.MaxTitle} characters.");
        if (config.Network == null) throw new ArgumentException("Network must be configured.");
        if (config.Network.FriendsOnly) throw new ArgumentException("Dedicated Steam servers have no Steam friends. Set Network.FriendsOnly to false.");
        if (!config.Network.AllowDirectIp) throw new ArgumentException("Dedicated servers require Network.AllowDirectIp=true for Steam Game Server IP networking.");
        if (config.Network.Advertise && string.IsNullOrWhiteSpace(config.Network.PublicAddress)) throw new ArgumentException("Set Network.PublicAddress to the public IP or hostname and port before advertising a dedicated server.");
        string error = config.Network.Validate();
        if (error != null) throw new ArgumentException(error);
        if (config.SessionSettings == null) throw new ArgumentException("SessionSettings cannot be null.");
        if (config.DefinitionSettings == null) throw new ArgumentException("DefinitionSettings cannot be null.");
        if (string.IsNullOrWhiteSpace(config.SettingsDumpFile)) throw new ArgumentException("SettingsDumpFile cannot be empty.");
        if (string.IsNullOrWhiteSpace(config.DefinitionDumpFile)) throw new ArgumentException("DefinitionDumpFile cannot be empty.");
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
