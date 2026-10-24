using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Y51_SE2Online.Sync;

public enum OnlineRole : byte { Player, Admin, Host }
public enum ChatKind : byte { Player, System, Admin }
public enum AdminAction
{
    Refresh, Kick, Ban, Unban, Promote, Demote, WhitelistAdd, WhitelistRemove, WhitelistOnly,
    Creative, SpawnPrefab, SpawnItem, CreateSafeZone, DeleteSafeZone, TeleportTo, Bring, Pvp, OfflineRaidProtection, PcuLimit,
    SunRotation, SunPeriod, SunNudge, Motd, SunSet,
    /// <summary>An admin's own access bypass (Value on/off): use, open, sit in and build on anything regardless of ownership.</summary>
    AccessBypass,
    /// <summary>God mode for oneself (Value on/off), apart from creative tools.</summary>
    Invulnerable,
    /// <summary>Put Target into group Name.</summary>
    SetGroup,
    /// <summary>List grids (Name: filter text, Value: everywhere instead of near me); the rows come back in AdminState.Query.</summary>
    EntityQuery,
    /// <summary>Act on the grid with NetId Target; Minutes is the <see cref="EntityVerb"/>, Name a SteamID for Transfer.</summary>
    EntityAction,
    CleanupPreview, CleanupRun, CleanupSettings, PermsReload,
    /// <summary>Effective permissions of Target (Name: a key, or empty for every granted key).</summary>
    PermsCheck,
    Kill,
    /// <summary>Resource usage: Name energy|hydrogen|oxygen, Size the multiplier.</summary>
    Usage,
    /// <summary>The player's own home base: Minutes is a card index of their last spawn menu (Value true), or Value false clears it.</summary>
    SetHome
}

/// <summary>
/// Identities whose admin turned the access bypass on for themselves. Nobody (host, admins included) may use what they
/// do not own or have faction or everyone access to unless their identity is listed here. Held for the session only.
/// On a client it holds just this game's own player, mirrored from the host's admin state.
/// </summary>
public static class AccessBypass
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, byte> On = new();

    public static bool IsOn(long identity) => identity != 0 && On.ContainsKey(identity);

    public static void Set(long identity, bool on)
    {
        if (identity == 0) return;
        if (on) On[identity] = 0;
        else On.TryRemove(identity, out _);
    }

    public static void Clear() => On.Clear();

    public static long[] All() => On.Keys.ToArray();
}

public sealed record ChatSend(string Text);
/// <summary>Message of the day for one joiner, placeholders already filled in.</summary>
public sealed record MotdMessage(string Title, string Text);
public sealed record ChatLine(string From, ulong FromId, string Text, ChatKind Kind, long UnixMs, string Group = null);
public sealed record AdminRequest(AdminAction Action, ulong Target = 0, string Reason = "", int Minutes = 0,
    string Name = "", bool Value = false, double Size = 0);
public sealed record AccessEntry(ulong SteamId, string Name);
public sealed record AccessPlayer(ulong SteamId, string Name, OnlineRole Role, bool Connected, int PingMs, string Group = null, int Rank = 0);
/// <summary>A group players can be put into, for the admin panel's group choices.</summary>
public sealed record GroupInfo(string Name, int Rank);
/// <summary>A player the server knows (online now, or seen before), for finding offline players to put into a group.</summary>
public sealed record KnownPlayer(ulong SteamId, string Name, string Group, long LastSeenUnixMs);
/// <summary>A per-player reply to a panel query (entities, cleanup preview, permission check), sent only to who asked.</summary>
public sealed class AdminQuery
{
    public string Kind { get; set; } = "";
    public string Text { get; set; } = "";
    public QueryRow[] Rows { get; set; } = Array.Empty<QueryRow>();
    public long Serial { get; set; }
}
public sealed record QueryRow(uint NetId, string Name, string Owner, int Blocks, double Distance, string Note);
public sealed record SafeZoneInfo(uint NetId, string Name, double Radius, double Distance);

public sealed class BanEntry
{
    public ulong SteamId { get; set; }
    public string Name { get; set; } = "";
    public string Reason { get; set; } = "";
    public string By { get; set; } = "";
    public long CreatedUnixMs { get; set; }
    public long? ExpiresUnixMs { get; set; }
    public bool ActiveAt(long now) => ExpiresUnixMs == null || ExpiresUnixMs > now;
}

public sealed class SeenEntry
{
    public ulong SteamId { get; set; }
    public string Name { get; set; } = "";
    /// <summary>The player's game identity in this world (the grid owner id), 0 until known.</summary>
    public long Identity { get; set; }
    public long LastSeenUnixMs { get; set; }
}

/// <summary>Sent as JoinReject code <see cref="JoinRejectCode.Removed"/> to kicked and banned players.</summary>
/// <summary>A player's home base as last seen, so the spawn screen can still name it after the game forgets a destroyed one.</summary>
public sealed class HomeEntry
{
    public ulong SteamId { get; set; }
    /// <summary>The world it is in (a dedicated server's access.json outlives a world change).</summary>
    public string World { get; set; } = "";
    public string Name { get; set; } = "";
    public string Grid { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
}

public sealed record RemovalNotice(bool Banned, string Reason, long? ExpiresUnixMs, string By, string ServerName)
{
    public string Describe(long now)
    {
        string what = Banned ? "You are banned from " : "You were kicked from ";
        string text = what + (string.IsNullOrWhiteSpace(ServerName) ? "this server" : ServerName) + ".";
        text += "\n\nReason: " + (string.IsNullOrWhiteSpace(Reason) ? "No reason given" : Reason);
        if (Banned) text += "\nDuration: " + (ExpiresUnixMs is long end ? AccessList.Remaining(end - now) + " remaining" : "Permanent");
        if (!string.IsNullOrWhiteSpace(By)) text += "\nBy: " + By;
        return text;
    }
}

public sealed class AdminState
{
    public OnlineRole Role { get; set; }
    /// <summary>The server's marker filter defaults (design 10.2); a player's own saved choice overrides them.</summary>
    public MarkerDefaults MarkerDefaults { get; set; } = new();
    public int PingLifetimeS { get; set; } = 60;
    /// <summary>How far from their character the server lets a player ping, in metres.</summary>
    public double PingMaxRangeMeters { get; set; } = 2000;
    public string ServerName { get; set; } = "";
    public bool Dedicated { get; set; }
    public bool WhitelistOnly { get; set; }
    public bool HasPassword { get; set; }
    public bool Pvp { get; set; }
    public bool OfflineRaidProtection { get; set; }
    /// <summary>Per-player PCU limit in force; 0 means unlimited.</summary>
    public int PcuLimit { get; set; }
    public bool SunRotation { get; set; }
    /// <summary>Minutes for one full day.</summary>
    public double SunPeriodMinutes { get; set; }
    /// <summary>Sun angle in degrees (0-360) when this state was built; it keeps turning while rotation is on.</summary>
    public double SunDegrees { get; set; }
    public string Motd { get; set; } = "";
    /// <summary>Whether the player this state was sent to has its access bypass on.</summary>
    public bool AccessBypass { get; set; }
    /// <summary>Proximity voice range on this server; 0 when voice is off.</summary>
    public double VoiceRadius { get; set; } = 50;
    public int MaxPlayers { get; set; }
    public string Message { get; set; } = "";
    public long Revision { get; set; }
    public AccessPlayer[] Players { get; set; } = Array.Empty<AccessPlayer>();
    public BanEntry[] Bans { get; set; } = Array.Empty<BanEntry>();
    public AccessEntry[] Whitelist { get; set; } = Array.Empty<AccessEntry>();
    public SafeZoneInfo[] SafeZones { get; set; } = Array.Empty<SafeZoneInfo>();
    /// <summary>This player's group and rank (1000 for an owner).</summary>
    public string Group { get; set; } = PermissionSet.DefaultGroup;
    public int Rank { get; set; }
    /// <summary>The catalogue keys this player has ("*" for an owner): the panel shows only what these allow; the server checks every action anyway.</summary>
    public string[] Grants { get; set; } = Array.Empty<string>();
    /// <summary>Every group's style (public), for names in chat, over heads and in player lists.</summary>
    public Dictionary<string, GroupStyle> Styles { get; set; } = new();
    /// <summary>Groups this player may hand out (empty without admin.players.set_group).</summary>
    public GroupInfo[] Groups { get; set; } = Array.Empty<GroupInfo>();
    /// <summary>Players seen before, online or not (only with admin.players.set_group), at most 500, newest first.</summary>
    public KnownPlayer[] Known { get; set; } = Array.Empty<KnownPlayer>();
    /// <summary>Whether this player's own creative tools and god mode are on.</summary>
    public bool Creative { get; set; }
    public bool Invulnerable { get; set; }
    /// <summary>The cleanup's next pass and last result, for the panel (only with admin.cleanup).</summary>
    public string Cleanup { get; set; } = "";
    /// <summary>The cleanup settings in force as JSON (only with cleanup.configure), so the panel edits them rather than defaults.</summary>
    public string CleanupConfig { get; set; } = "";
    public AdminQuery Query { get; set; }

    public bool Can(string key) => Role == OnlineRole.Host || Array.IndexOf(Grants, "*") >= 0 || Array.IndexOf(Grants, key) >= 0;
}

/// <summary>Persistent admins, bans and whitelist for one host or dedicated server. Thread-safe.</summary>
public sealed class AccessList
{
    public int Version { get; set; } = 1;
    public bool WhitelistOnly { get; set; }
    /// <summary>Runtime PvP choice made by the host or server console; overrides the configured setting.</summary>
    public bool? Pvp { get; set; }
    public bool? OfflineRaid { get; set; }
    /// <summary>Per-player PCU limit chosen at runtime: 0 unlimited, -1 the world's own value, null the server config.</summary>
    public int? PcuLimit { get; set; }
    /// <summary>The world's own per-player PCU, remembered before any override (the world save keeps the override).</summary>
    public int? WorldPcu { get; set; }
    /// <summary>Runtime sun choices; override the server config. The world save keeps whatever is in force.</summary>
    public bool? SunRotation { get; set; }
    public double? SunPeriodMinutes { get; set; }
    /// <summary>Runtime message of the day; overrides the server config. Empty clears it, null uses the config.</summary>
    public string Motd { get; set; }
    public List<AccessEntry> Admins { get; set; } = new();
    public List<BanEntry> Bans { get; set; } = new();
    public List<AccessEntry> Whitelist { get; set; } = new();
    /// <summary>When each player was last online (unix ms), by SteamID: the cleanup's "owner inactive" clock, and the known
    /// players for group assignment. 0.17 ignores it.</summary>
    public List<SeenEntry> Seen { get; set; } = new();
    /// <summary>Home bases by SteamID (the game keeps the medbay itself in the world save). 0.17 ignores it.</summary>
    public List<HomeEntry> Homes { get; set; } = new();
    private readonly object _sync = new();
    private string _path;
    [System.Text.Json.Serialization.JsonIgnore] public long Revision { get; private set; }

    public static AccessList Load(string path)
    {
        AccessList list = null;
        try { if (File.Exists(path)) list = JsonSerializer.Deserialize<AccessList>(File.ReadAllText(path)); }
        catch (Exception e) when (e is JsonException or IOException) { throw new InvalidDataException("Could not read " + path + ": " + e.Message, e); }
        list ??= new AccessList();
        list.Admins ??= new(); list.Bans ??= new(); list.Whitelist ??= new(); list.Seen ??= new(); list.Homes ??= new();
        list._path = path;
        return list;
    }

    /// <summary>Writes a new list at <paramref name="path"/> with this list's bans, whitelist and whitelist-only switch; never its admins.</summary>
    public void SeedCopy(string path)
    {
        AccessList copy;
        lock (_sync) copy = new AccessList { Bans = Bans.ToList(), Whitelist = Whitelist.ToList(), WhitelistOnly = WhitelistOnly, _path = path };
        copy.Save();
    }

    /// <summary>
    /// Creates a listen host's per-world file on first use. A world hosted before its first save was keyed by its
    /// name: that file moves to the container key. Otherwise the old shared file seeds it (<see cref="SeedCopy"/>).
    /// </summary>
    public static void PrepareWorldFile(string path, string nameKeyed, string legacy)
    {
        if (File.Exists(path)) return;
        if (nameKeyed != null && nameKeyed != path && File.Exists(nameKeyed)) File.Move(nameKeyed, path);
        else if (File.Exists(legacy)) Load(legacy).SeedCopy(path);
    }

    private void Save()
    {
        Revision++;
        if (_path == null) return;
        string tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, _path, true);
    }

    public bool IsAdmin(ulong id) { lock (_sync) return Admins.Any(a => a.SteamId == id); }

    /// <summary>Replaces the admin list with the players of rank 50 or more (0.18 permissions), so a 0.17 rollback keeps its admins.</summary>
    public void MirrorAdmins(IEnumerable<(ulong SteamId, string Name)> admins)
    {
        lock (_sync)
        {
            var next = admins.Select(a => new AccessEntry(a.SteamId, Clean(string.IsNullOrWhiteSpace(a.Name) ? Admins.FirstOrDefault(x => x.SteamId == a.SteamId)?.Name ?? "" : a.Name, 64))).ToList();
            if (next.Select(a => a.SteamId).SequenceEqual(Admins.Select(a => a.SteamId))) return;
            Admins = next;
            Save();
        }
    }

    /// <summary>Notes players as online now (join, leave, and every minute while online). Writes the file at most once a minute unless <paramref name="force"/>.</summary>
    public void MarkSeen(IEnumerable<(ulong SteamId, string Name, long Identity)> players, long now, bool force = false)
    {
        lock (_sync)
        {
            bool changed = false;
            foreach (var (id, name, identity) in players)
            {
                if (id == 0) continue;
                var entry = Seen.FirstOrDefault(s => s.SteamId == id);
                if (entry == null) { entry = new SeenEntry { SteamId = id }; Seen.Add(entry); changed = true; }
                string clean = Clean(name, 64);
                if (clean.Length > 0 && entry.Name != clean) { entry.Name = clean; changed = true; }
                if (identity != 0 && entry.Identity != identity) { entry.Identity = identity; changed = true; }
                if (now > entry.LastSeenUnixMs) { entry.LastSeenUnixMs = now; changed = true; }
            }
            if (Seen.Count > 20000) { Seen = Seen.OrderByDescending(s => s.LastSeenUnixMs).Take(20000).ToList(); changed = true; }
            if (changed && (force || now - _seenSavedMs >= 60_000)) { _seenSavedMs = now; Save(); }
        }
    }

    private long _seenSavedMs;

    public HomeEntry HomeOf(ulong steam, string world)
    {
        lock (_sync) return Homes.FirstOrDefault(h => h.SteamId == steam && h.World == (world ?? "")) is { } h ? new HomeEntry { SteamId = h.SteamId, World = h.World, Name = h.Name, Grid = h.Grid, X = h.X, Y = h.Y, Z = h.Z } : null;
    }

    /// <summary>Records (or with null, forgets) a player's home base; writes the file only when it changed.</summary>
    public void SetHome(ulong steam, HomeEntry home)
    {
        if (steam == 0) return;
        lock (_sync)
        {
            var old = Homes.FirstOrDefault(h => h.SteamId == steam);
            if (home == null) { if (old != null) { Homes.Remove(old); Save(); } return; }
            string name = Clean(home.Name, 64), grid = Clean(home.Grid, 64);
            string world = home.World ?? "";
            // A home on a moving ship is rewritten only when it moved far: each write is the whole file.
            if (old != null && old.World == world && old.Name == name && old.Grid == grid && Math.Abs(old.X - home.X) + Math.Abs(old.Y - home.Y) + Math.Abs(old.Z - home.Z) < 1000) return;
            if (old == null) { old = new HomeEntry { SteamId = steam }; Homes.Add(old); }
            old.World = world; old.Name = name; old.Grid = grid; old.X = home.X; old.Y = home.Y; old.Z = home.Z;
            Save();
        }
    }

    public SeenEntry[] SeenCopy() { lock (_sync) return Seen.Select(s => new SeenEntry { SteamId = s.SteamId, Name = s.Name, Identity = s.Identity, LastSeenUnixMs = s.LastSeenUnixMs }).ToArray(); }
    public bool IsWhitelisted(ulong id) { lock (_sync) return Whitelist.Any(a => a.SteamId == id); }

    public BanEntry ActiveBan(ulong id, long now)
    {
        lock (_sync) return Bans.FirstOrDefault(b => b.SteamId == id && b.ActiveAt(now));
    }

    /// <summary>Returns null when the player may join, otherwise the reject code and text.</summary>
    public (byte Code, string Text)? CheckJoin(ulong id, string password, string required, string serverName, long now)
        => CheckJoin(id, password, required, serverName, now, key => key is "join.bypass_password" or "join.bypass_whitelist" && IsAdmin(id));

    /// <summary>The join check with the player's permissions: join.ban_immune, join.bypass_whitelist, join.bypass_password.</summary>
    public (byte Code, string Text)? CheckJoin(ulong id, string password, string required, string serverName, long now, Func<string, bool> has)
    {
        var ban = ActiveBan(id, now);
        if (ban != null && !has("join.ban_immune"))
            return (JoinRejectCode.Removed, JsonSerializer.Serialize(new RemovalNotice(true, ban.Reason, ban.ExpiresUnixMs, ban.By, serverName)));
        if (WhitelistOnly && !IsWhitelisted(id) && !has("join.bypass_whitelist"))
            return (JoinRejectCode.Generic, $"This server only admits whitelisted players. Ask an admin to whitelist your SteamID {id}.");
        if (has("join.bypass_password")) return null;
        if (!string.IsNullOrEmpty(required) && !FixedEquals(password ?? "", required))
            return (JoinRejectCode.Password, string.IsNullOrEmpty(password) ? "This server requires a password." : "The password was incorrect.");
        return null;
    }

    private static bool FixedEquals(string a, string b)
        => System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(a), System.Text.Encoding.UTF8.GetBytes(b));

    public string Ban(ulong id, string name, string reason, int minutes, string by, long now)
    {
        if (id == 0) return "Choose a player.";
        if (minutes < 0 || minutes > 5256000) return "Ban duration must be up to 10 years.";
        lock (_sync)
        {
            Bans.RemoveAll(b => b.SteamId == id || !b.ActiveAt(now));
            Bans.Add(new BanEntry { SteamId = id, Name = Clean(name, 64), Reason = Clean(reason, 200), By = Clean(by, 64), CreatedUnixMs = now, ExpiresUnixMs = minutes == 0 ? null : now + minutes * 60000L });
            Save();
        }
        return $"Banned {Display(name, id)} {(minutes == 0 ? "permanently" : "for " + Remaining(minutes * 60000L))}.";
    }

    public string Unban(ulong id)
    {
        lock (_sync) { if (Bans.RemoveAll(b => b.SteamId == id) == 0) return "That player is not banned."; Save(); }
        return "Ban removed.";
    }

    public string SetAdmin(ulong id, string name, bool admin)
    {
        if (id == 0) return "Choose a player.";
        lock (_sync)
        {
            bool has = Admins.Any(a => a.SteamId == id);
            if (has == admin) return admin ? "That player is already an admin." : "That player is not an admin.";
            if (admin) Admins.Add(new AccessEntry(id, Clean(name, 64))); else Admins.RemoveAll(a => a.SteamId == id);
            Save();
        }
        return admin ? $"{Display(name, id)} is now an admin." : $"{Display(name, id)} is no longer an admin.";
    }

    public string SetWhitelisted(ulong id, string name, bool listed)
    {
        if (id == 0) return "Choose a player.";
        lock (_sync)
        {
            bool has = Whitelist.Any(a => a.SteamId == id);
            if (has == listed) return listed ? $"{Display(name, id)} is already on the whitelist." : "Not on the whitelist.";
            if (listed) Whitelist.Add(new AccessEntry(id, Clean(name, 64))); else Whitelist.RemoveAll(a => a.SteamId == id);
            Save();
        }
        return listed ? $"{Display(name, id)} added to the whitelist." : $"{Display(name, id)} removed from the whitelist.";
    }

    /// <summary>A Steam player account: public universe, individual type, desktop instance, nonzero account.</summary>
    public static bool IsPlayerSteamId(ulong id) => id >> 32 == 0x0110_0001 && (uint)id != 0;

    /// <summary>Parses a SteamID64 or a steamcommunity.com/profiles/ link into a player account id.</summary>
    public static bool TryParsePlayerId(string text, out ulong id)
    {
        const string profiles = "steamcommunity.com/profiles/";
        string s = (text ?? "").Trim();
        int at = s.IndexOf(profiles, StringComparison.OrdinalIgnoreCase);
        if (at >= 0) s = s.Substring(at + profiles.Length).Split('/', '?', '#')[0];
        return ulong.TryParse(s, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out id) && IsPlayerSteamId(id);
    }

    public string SetWhitelistOnly(bool on)
    {
        lock (_sync) { if (WhitelistOnly == on) return on ? "Whitelist is already enforced." : "Whitelist is already off."; WhitelistOnly = on; Save(); }
        return on ? "Only whitelisted players and admins may join now." : "Whitelist is no longer enforced.";
    }

    public string SetPvp(bool on)
    {
        lock (_sync) { Pvp = on; Save(); }
        return on ? "PvP is on: players can damage each other and turrets attack players outside your faction." : "PvP is off: players cannot damage each other.";
    }

    public string SetOfflineRaid(bool on)
    {
        lock (_sync) { OfflineRaid = on; Save(); }
        return on ? "Offline raid protection is on: players cannot damage grids whose owner and faction are all offline." : "Offline raid protection is off.";
    }

    public void RememberWorldPcu(int value)
    {
        lock (_sync) { if (WorldPcu != null) return; WorldPcu = value; Save(); }
    }

    public string SetPcuLimit(int limit)
    {
        lock (_sync) { PcuLimit = limit; Save(); }
        return limit == 0 ? "PCU limit removed: players can build without a PCU cap." : limit < 0 ? "PCU limit back to the world's own setting." : $"PCU limit is now {limit:N0} per player.";
    }

    public string SetMotd(string text)
    {
        text = (text ?? "").Replace("\r\n", "\n").Trim();
        if (text.Length > Core.Motd.MaxText) return $"The message of the day can be at most {Core.Motd.MaxText} characters.";
        lock (_sync) { Motd = text; Save(); }
        return text.Length == 0 ? "Message of the day cleared." : "Message of the day set.";
    }

    public string SetSunRotation(bool on)
    {
        lock (_sync) { SunRotation = on; Save(); }
        return on ? "The sun moves across the sky again." : "The sun is stopped where it is.";
    }

    public string SetSunPeriod(double minutes)
    {
        lock (_sync) { SunPeriodMinutes = minutes; Save(); }
        return "A full day now lasts " + DayLength(minutes) + ".";
    }

    public static string DayLength(double minutes) => (minutes >= 120 ? minutes / 60 : minutes).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + (minutes >= 120 ? " hours" : " minutes");

    public (BanEntry[] Bans, AccessEntry[] Whitelist, AccessEntry[] Admins) Copy(long now)
    {
        lock (_sync) return (Bans.Where(b => b.ActiveAt(now)).ToArray(), Whitelist.ToArray(), Admins.ToArray());
    }

    public static string Remaining(long ms)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(ms, 60000));
        if (t.TotalDays >= 1) return $"{(int)t.TotalDays} day{((int)t.TotalDays == 1 ? "" : "s")}" + (t.Hours > 0 ? $" {t.Hours} h" : "");
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours} h" + (t.Minutes > 0 ? $" {t.Minutes} min" : "");
        return $"{(int)Math.Ceiling(t.TotalMinutes)} min";
    }

    private static string Display(string name, ulong id) => string.IsNullOrWhiteSpace(name) ? id.ToString() : name;
    public static string Clean(string value, int limit) => SocialState.Clean(value, limit);
}

/// <summary>Recent chat lines shown in the HUD and the chat screen.</summary>
public static class ChatLog
{
    public const int MaxText = 240;
    private static readonly List<(ChatLine Line, long ReceivedMs)> Lines = new();
    public static long Version { get; private set; }

    public static void Add(ChatLine line, long nowMs)
    {
        lock (Lines)
        {
            Lines.Add((line, nowMs));
            if (Lines.Count > 100) Lines.RemoveAt(0);
            Version++;
        }
    }

    public static (ChatLine Line, long ReceivedMs)[] Recent(int count)
    {
        lock (Lines) return Lines.Skip(Math.Max(0, Lines.Count - count)).ToArray();
    }

    public static void Clear() { lock (Lines) { Lines.Clear(); Version++; } }
}

/// <summary>Per-sender chat rate limit: a burst of 5 lines, refilling one per second.</summary>
public sealed class ChatThrottle
{
    private readonly Dictionary<ulong, (double Tokens, long At)> _buckets = new();
    public bool Allow(ulong sender, long now)
    {
        lock (_buckets)
        {
            var (tokens, at) = _buckets.TryGetValue(sender, out var b) ? b : (5.0, now);
            tokens = Math.Min(5, tokens + (now - at) / 1000.0);
            bool ok = tokens >= 1;
            _buckets[sender] = (ok ? tokens - 1 : tokens, now);
            return ok;
        }
    }
}

public static class AdminPanel
{
    /// <summary>Why this player can't open the admin panel, or null when they can.</summary>
    public static string Refusal(bool inSession, OnlineRole role, string serverName)
        => !inSession ? "The admin panel is available while you host a multiplayer session or are an admin on a server."
        : role == OnlineRole.Player ? "You are not an admin on " + serverName + ", so you can't use the admin panel." : null;

    /// <summary>The 0.18 refusal: the panel needs admin.panel (owners always have it).</summary>
    public static string Refusal(bool inSession, AdminState state, string serverName)
        => !inSession ? "The admin panel is available while you host a multiplayer session or are an admin on a server."
        : state.Can("admin.panel") ? null : "Your role on " + serverName + " (" + state.Group + ") does not include the admin panel.";
}
