using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SE2OnlineInstaller;

/// <summary>A Steam login on this PC that has its own localconfig.vdf (where Steam keeps per-game launch options).</summary>
public sealed record SteamAccount(ulong SteamId, string Name, bool MostRecent, string LocalConfig)
{
    public override string ToString() => Name + (MostRecent ? "  (last signed in)" : "");
}

/// <summary>Finds Steam, Space Engineers 2 and the Steam accounts, and reads or writes SE2's launch options.</summary>
public static class Steam
{
    public const string AppId = "1133870";
    private const ulong AccountIdBase = 76561197960265728UL;

    public static string? Root()
    {
        string? path = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
        path ??= Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string;
        if (path != null) path = Path.GetFullPath(path.Replace('/', '\\'));
        if (path != null && File.Exists(Path.Combine(path, "steam.exe"))) return path;
        string fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        return File.Exists(Path.Combine(fallback, "steam.exe")) ? fallback : null;
    }

    /// <summary>SE2's Game2 folder (where SpaceEngineers2.exe is), or null when it is not installed.</summary>
    public static string? GameFolder(string steam)
    {
        var libraries = new List<string> { steam };
        try
        {
            var folders = KvNode.Parse(File.ReadAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf")));
            libraries.AddRange(folders.Children.Where(f => f.IsBlock).Select(f => f.Text("path")).Where(p => !string.IsNullOrEmpty(p))!);
        }
        catch (Exception e) when (e is IOException or FormatException or UnauthorizedAccessException) { }
        foreach (string library in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string apps = Path.Combine(library, "steamapps");
            string manifest = Path.Combine(apps, "appmanifest_" + AppId + ".acf");
            string installDir = "SpaceEngineers2";
            try { if (File.Exists(manifest)) installDir = KvNode.Parse(File.ReadAllText(manifest)).Text("installdir") ?? installDir; }
            catch (FormatException) { }
            string game = Path.Combine(apps, "common", installDir, "Game2");
            if (File.Exists(Path.Combine(game, "SpaceEngineers2.exe"))) return game;
        }
        return null;
    }

    public static string? GameVersion(string game)
    {
        try { return FileVersionInfo.GetVersionInfo(Path.Combine(game, "SpaceEngineers2.exe")).ProductVersion; }
        catch (FileNotFoundException) { return null; }
    }

    public static List<SteamAccount> Accounts(string steam)
    {
        var names = new Dictionary<ulong, (string Name, bool Recent)>();
        try
        {
            foreach (var user in KvNode.Parse(File.ReadAllText(Path.Combine(steam, "config", "loginusers.vdf"))).Children.Where(c => c.IsBlock))
                if (ulong.TryParse(user.Key, NumberStyles.None, CultureInfo.InvariantCulture, out ulong id))
                    names[id] = (user.Text("PersonaName") ?? user.Text("AccountName") ?? user.Key, user.Text("MostRecent") == "1");
        }
        catch (Exception e) when (e is IOException or FormatException or UnauthorizedAccessException) { }
        var accounts = new List<SteamAccount>();
        string userdata = Path.Combine(steam, "userdata");
        if (!Directory.Exists(userdata)) return accounts;
        foreach (string dir in Directory.GetDirectories(userdata))
        {
            if (!uint.TryParse(Path.GetFileName(dir), NumberStyles.None, CultureInfo.InvariantCulture, out uint accountId) || accountId == 0) continue;
            string config = Path.Combine(dir, "config", "localconfig.vdf");
            if (!File.Exists(config)) continue;
            ulong id = AccountIdBase + accountId;
            var (name, recent) = names.TryGetValue(id, out var known) ? known : ("Steam account " + accountId, false);
            accounts.Add(new SteamAccount(id, name, recent, config));
        }
        return accounts.OrderByDescending(a => a.MostRecent).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static KvNode AppNode(KvNode config) =>
        config.Block("Software").Block("Valve").Block("Steam").Block("apps").Block(AppId);

    public static string ReadLaunchOptions(SteamAccount account)
    {
        var config = KvNode.Parse(File.ReadAllText(account.LocalConfig));
        return AppNode(config).Text("LaunchOptions") ?? "";
    }

    /// <summary>
    /// Rewrites SE2's launch options in the account's localconfig.vdf, keeping a backup beside it. Steam rewrites that
    /// file from memory when it exits, so this only sticks while Steam is closed.
    /// </summary>
    public static void WriteLaunchOptions(SteamAccount account, Func<string, string> change)
    {
        string text = File.ReadAllText(account.LocalConfig);
        var config = KvNode.Parse(text);
        var app = AppNode(config);
        app.Set("LaunchOptions", change(app.Text("LaunchOptions") ?? ""));
        File.WriteAllText(account.LocalConfig + ".se2online-backup", text);
        string temp = account.LocalConfig + ".se2online-tmp";
        File.WriteAllText(temp, config.Serialize(), new UTF8Encoding(false));
        File.Move(temp, account.LocalConfig, overwrite: true);
    }

    public static bool Running() => Process.GetProcessesByName("steam").Length > 0;

    public static bool GameRunning() => Process.GetProcessesByName("SpaceEngineers2").Length > 0;

    /// <summary>Asks Steam to exit and waits for it; false if it is still running after <paramref name="timeout"/>.</summary>
    public static async Task<bool> ShutdownAsync(string steam, TimeSpan timeout, CancellationToken cancel = default)
    {
        if (!Running()) return true;
        Process.Start(new ProcessStartInfo(Path.Combine(steam, "steam.exe"), "-shutdown") { UseShellExecute = false })?.Dispose();
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(500, cancel);
            if (!Running()) { await Task.Delay(1500, cancel); return true; }
        }
        return false;
    }

    public static void Start(string steam) => Process.Start(new ProcessStartInfo(Path.Combine(steam, "steam.exe")) { UseShellExecute = true })?.Dispose();
}
