using System;
using System.Globalization;
using System.Linq;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.Core;

public sealed class DirectoryListing
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "session";
    public string Name { get; set; } = "";
    public string WorldName { get; set; } = "";
    public string Description { get; set; } = "";
    public string SteamId { get; set; } = "0";
    public string Address { get; set; } = "";
    public int VirtualPort { get; set; } = MpOptions.DefaultVirtualPort;
    public int Players { get; set; }
    public int MaxPlayers { get; set; }
    public string PluginVersion { get; set; } = "";
    public string GameVersion { get; set; } = "";
    public int SyncVersion { get; set; }
    public bool FriendsOnly { get; set; }
    /// <summary>2-letter country code: configured by the host, or located by the directory from its IP; "" when unknown.</summary>
    public string Country { get; set; } = "";
    /// <summary>The host's Steam ping location (SteamNetworkingUtils string form), so players can estimate their ping without contacting it; "" when unknown.</summary>
    public string PingLocation { get; set; } = "";
    public ListingMod[] Mods { get; set; } = Array.Empty<ListingMod>();
    public string[] Tags { get; set; } = Array.Empty<string>();
    public DateTimeOffset UpdatedAt { get; set; }
    public string FavoriteKey => Kind + ":" + (Kind == "dedicated" && !string.IsNullOrEmpty(Address) ? Address : SteamId != "0" ? SteamId : Address);
    public bool MatchesBuild(string pluginVersion, string gameVersion) => PluginVersion == pluginVersion && GameVersion == gameVersion && SyncVersion == SyncProtocol.Version;

    public void Validate()
    {
        if (Kind is not ("session" or "dedicated") || Id == null || Id.Length > 128 || string.IsNullOrWhiteSpace(Name) || Name.Length > 100 || WorldName == null || WorldName.Length > 120 || Description == null || Description.Length > 1000) throw new FormatException("The listing contains invalid server information.");
        if (!ulong.TryParse(SteamId, NumberStyles.None, CultureInfo.InvariantCulture, out ulong id) || (id == 0 && (Kind != "dedicated" || string.IsNullOrEmpty(Address)))) throw new FormatException("The listing has no valid Steam identity.");
        if (!string.IsNullOrEmpty(Address))
        {
            try { ServerEndpoint.Parse(Address); }
            catch (FormatException) when (id != 0 && ValidHostAddress(Address)) { }
        }
        if (VirtualPort < 0 || VirtualPort > 65535 || Players < 0 || MaxPlayers < 1 || MaxPlayers > 1024 || Players > MaxPlayers || Tags == null || Tags.Length > 8 || PluginVersion == null || PluginVersion.Length > 100 || GameVersion == null || GameVersion.Length > 100 || SyncVersion < 1 || (Country ??= "").Length > 8) throw new FormatException("The listing contains invalid player or version information.");
        if ((PingLocation ??= "").Length > MaxPingLocation) throw new FormatException("The listing contains an invalid ping location.");
        Mods ??= Array.Empty<ListingMod>();
        if (Mods.Length > WorldMods.MaxMods || Mods.Any(m => m == null || !ulong.TryParse(m.Id, NumberStyles.None, CultureInfo.InvariantCulture, out _) || (m.Name ?? "").Length > 120 || m.Size < 0))
            throw new FormatException("The listing contains an invalid mod list.");
    }

    public const int MaxPingLocation = 1024;

    private static bool ValidHostAddress(string address)
    {
        int split = address.LastIndexOf(':');
        return address.Length <= 253 && split > 0 && int.TryParse(address.Substring(split + 1), out int port) && port is >= 1 and <= 65535
            && Uri.CheckHostName(address.Substring(0, split)) == UriHostNameType.Dns;
    }
}

/// <summary>A mod the listed world runs, as its host reports it: Workshop item id ("0" for a local mod), title and size on disk (0 when unknown).</summary>
public sealed record ListingMod(string Id, string Name, long Size)
{
    public string Describe() => (Id == "0" ? "Local mod (not on the Workshop)" : "Workshop ID " + Id) + (Size > 0 ? ", " + ModDownloads.Size((ulong)Size) : "");

    public static string Total(ListingMod[] mods) => mods == null || mods.Length == 0 ? "None"
        : mods.Length + (mods.Length == 1 ? " mod" : " mods") + (mods.Any(m => m.Size > 0) ? ", " + ModDownloads.Size((ulong)mods.Sum(m => m.Size)) : "");
}
