using System;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia;

namespace SE2OnlineInstaller;

internal static class Program
{
    /// <summary>Set by --screenshot PATH: the window saves a picture of itself once loaded and closes (for build checks).</summary>
    public static string? ScreenshotPath;

    [STAThread]
    public static int Main(string[] args)
    {
        int check = Array.IndexOf(args, "--check");
        if (check >= 0 && check + 1 < args.Length) return Check(args[check + 1]);
        int install = Array.IndexOf(args, "--install-only");
        if (install >= 0 && install + 1 < args.Length) return InstallOnly(args[install + 1]);
        int shot = Array.IndexOf(args, "--screenshot");
        if (shot >= 0 && shot + 1 < args.Length) ScreenshotPath = Path.GetFullPath(args[shot + 1]);
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();

    /// <summary>
    /// --check OUT: reports what the installer finds and proves, without writing anything to Steam, that rewriting each
    /// account's localconfig.vdf would change nothing but SE2's launch options.
    /// </summary>
    private static int Check(string output)
    {
        var report = new StringBuilder();
        int failures = 0;
        string? steam = Steam.Root();
        report.AppendLine("steam: " + (steam ?? "not found"));
        if (steam != null)
        {
            string? game = Steam.GameFolder(steam);
            report.AppendLine("game: " + (game ?? "not found") + (game != null ? " version " + Steam.GameVersion(game) : ""));
            foreach (var account in Steam.Accounts(steam))
            {
                try
                {
                    string original = File.ReadAllText(account.LocalConfig);
                    var parsed = KvNode.Parse(original);
                    string before = Steam.ReadLaunchOptions(account);
                    var changed = KvNode.Parse(original);
                    changed.Block("Software").Block("Valve").Block("Steam").Block("apps").Block(Steam.AppId).Set("LaunchOptions", LaunchOptions.With(before, @"C:\SE2Online\Y51_SE2Online.dll"));
                    var restored = KvNode.Parse(changed.Serialize());
                    restored.Block("Software").Block("Valve").Block("Steam").Block("apps").Block(Steam.AppId).Set("LaunchOptions", before);
                    // Setting the old value back must give the original tree (plus the empty entry when SE2 had none).
                    var expected = KvNode.Parse(original);
                    expected.Block("Software").Block("Valve").Block("Steam").Block("apps").Block(Steam.AppId).Set("LaunchOptions", before);
                    bool same = Same(expected, restored);
                    if (!same) failures++;
                    report.AppendLine($"account {account.Name} ({account.SteamId}){(account.MostRecent ? " most recent" : "")}: launch options '{before}', {CountNodes(parsed)} nodes, rewrite {(same ? "changes only the launch options" : "CHANGES OTHER DATA")}");
                }
                catch (Exception e) { failures++; report.AppendLine($"account {account.Name}: {e.GetType().Name}: {e.Message}"); }
            }
        }
        report.AppendLine("pulsar: " + PluginInstall.PulsarInstalled() + ", local copy: " + PluginInstall.PulsarLocalCopy());
        File.WriteAllText(output, report.ToString());
        return failures == 0 ? 0 : 1;
    }

    /// <summary>--install-only FOLDER: downloads, verifies and installs the newest plugin there; Steam is not touched.</summary>
    private static int InstallOnly(string folder)
    {
        string log = Path.Combine(Path.GetFullPath(folder), "..", "install-only.txt");
        try
        {
            var package = PluginInstall.LatestAsync(default).GetAwaiter().GetResult().Client ?? throw new InvalidOperationException("no client package listed");
            string version = PluginInstall.InstallAsync(package, folder, new Progress<(string, double)>(), default).GetAwaiter().GetResult();
            File.WriteAllText(log, "installed " + version + " to " + Path.GetFullPath(folder));
            return 0;
        }
        catch (Exception e) { File.WriteAllText(log, e.ToString()); return 1; }
    }

    private static bool Same(KvNode a, KvNode b) =>
        a.Key == b.Key && a.Value == b.Value && a.Children.Count == b.Children.Count && a.Children.Zip(b.Children).All(p => Same(p.First, p.Second));

    private static int CountNodes(KvNode n) => 1 + n.Children.Sum(CountNodes);
}
