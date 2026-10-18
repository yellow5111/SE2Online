using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Keen.Game2.Client.UI.Library;
using Keen.Game2.Client.UI.Library.Dialogs.TwoOptionsDialog;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// Client auto-update for the plain -plugins: install: downloads the signed client ZIP, verifies it, swaps the plugin
/// files beside the loaded DLL (the loaded ones are renamed to *.old), then offers to restart the game at the main menu.
/// Pulsar installs (hub or Local) are updated by Pulsar and never touched here.
/// </summary>
internal static class AutoUpdateClient
{
    private const string UrlArgument = "-y51updateurl:";
    private static volatile string _installed;
    private static bool _offered;

    /// <summary>The version installed on disk this session, waiting for a game restart.</summary>
    public static string Installed => _installed;

#if PULSAR
    public static bool Eligible => false;
#else
    public static bool Eligible => MpMenu.Settings.AutoUpdate && MpPlugin.PulsarDataDir == null && !PluginPaths.InMemory && !Dedicated.DedicatedRuntime.Requested;
#endif

    private static string DllDir => Path.GetDirectoryName(typeof(AutoUpdateClient).Assembly.Location);

    /// <summary>version.php, or a test server given with -y51updateurl: (HTTPS, or HTTP to this machine only).</summary>
    public static string Url
    {
        get
        {
            string custom = MpPlugin.LaunchArgs.FirstOrDefault(a => a.StartsWith(UrlArgument, StringComparison.OrdinalIgnoreCase))?.Substring(UrlArgument.Length);
            if (custom != null && AutoUpdate.AllowedUrl(custom)) return custom;
            if (custom != null) GameLog.Instance.Warn("update: ignoring " + UrlArgument + custom + " (HTTPS only, or HTTP to localhost)");
            return AutoUpdate.VersionUrl;
        }
    }

    /// <summary>Removes the files the last update renamed; they are no longer loaded.</summary>
    public static void CleanUp()
    {
        if (PluginPaths.InMemory || MpPlugin.PulsarDataDir != null) return;
        try
        {
            int removed = AutoUpdate.CleanOld(DllDir);
            if (removed > 0) GameLog.Instance.Info($"update: removed {removed} file(s) left by the previous update");
            string staging = Path.Combine(DllDir, ".y51-update");
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
        catch (Exception ex) { GameLog.Instance.Info("update: cleanup: " + ex.Message); }
    }

    // Only the plugin itself and its Content; docs, the Pulsar copy and the server scripts stay as the player left them.
    private static bool ClientFile(string path)
        => path is "Y51_SE2Online.dll" or "0Harmony.dll" or "Y51_SE2Online.pdb" or "Y51_SE2Online.deps.json"
           || (path.StartsWith("Content/", StringComparison.Ordinal) && !path.EndsWith(".old", StringComparison.OrdinalIgnoreCase));

    /// <summary>Downloads, verifies and installs <paramref name="package"/>; false means the player should update by hand.</summary>
    public static async Task<bool> InstallAsync(UpdatePackage package)
    {
        string staging = Path.Combine(DllDir, ".y51-update");
        try
        {
            // A read-only install folder fails here, before anything is downloaded.
            Directory.CreateDirectory(staging);
            string zip = Path.Combine(staging, "payload.zip");
            GameLog.Instance.Info($"update: downloading {package.Version} from {package.Url} ({package.Size} bytes)");
            string error = await AutoUpdate.DownloadAsync(package, zip, "Y51_SE2Online/" + MpPlugin.Version);
            if (error != null)
            {
                GameLog.Instance.Error($"update: REJECTED {package.Version} payload: {error}. Nothing was installed.");
                return false;
            }
            string files = Path.Combine(staging, "files");
            var extracted = AutoUpdate.Extract(zip, files, ClientFile);
            if (!extracted.Contains("Y51_SE2Online.dll"))
            {
                GameLog.Instance.Error($"update: REJECTED {package.Version} payload: no Y51_SE2Online.dll in it. Nothing was installed.");
                return false;
            }
            error = AutoUpdate.CheckVersion(package.Version, AutoUpdate.FileVersion(Path.Combine(files, "Y51_SE2Online.dll")), MpPlugin.Version);
            if (error != null)
            {
                GameLog.Instance.Error($"update: REJECTED {package.Version} payload: {error}. Nothing was installed.");
                return false;
            }
            AutoUpdate.Swap(files, DllDir, extracted);
            _installed = package.Version;
            GameLog.Instance.Info($"update: installed {package.Version} into {DllDir} (signature and SHA-256 verified, {extracted.Count} files); it loads on the next game start");
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            GameLog.Instance.Warn($"update: cannot write the plugin folder {DllDir} ({ex.Message}); showing the download page instead");
            return false;
        }
        catch (Exception ex)
        {
            GameLog.Instance.Error("update: install failed: " + ex);
            return false;
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch (Exception) { }
        }
    }

    /// <summary>Restart now / Later, once, at the main menu (never inside a session).</summary>
    public static void Tick(SharedUIComponent shared, bool atMainMenu)
    {
        string installed = _installed;
        if (installed == null || _offered || shared == null || !atMainMenu) return;
        _offered = true;
        GameLog.Instance.Info($"update: offering a restart into {installed} (Restart now / Later)");
        shared.ShowDialog(new TwoOptionsDialogViewModel(new TwoOptionsDialogDefinition
        {
            Title = OnlineUi.Raw("Update installed"),
            Content = OnlineUi.Raw($"Space Engineers 2 Online {installed} is installed (you are running {MpPlugin.Version}). Restart the game now to use it?\n\nLater: it loads the next time you start the game."),
            ConfirmOption = OnlineUi.Raw("Restart now"),
            CancelOption = OnlineUi.Raw("Later")
        }) { ConfirmAction = RestartNow });
    }

    /// <summary>Starts the game again with the same command line once this process has exited, then exits.</summary>
    public static void RestartNow()
    {
        try
        {
            string exe = Environment.ProcessPath;
            string arguments = string.Join(" ", Environment.GetCommandLineArgs().Skip(1).Select(QuoteArgument));
            // The relaunch must survive this process: Steam runs the game in a job object that kills its children on
            // exit, so the waiter is created by WMI, outside the job. Started directly, SE2 hands itself to Steam if it
            // needs to (SteamAPI.RestartAppIfNecessary), which applies the player's launch options again.
            string waiter = $"Wait-Process -Id {Environment.ProcessId} -ErrorAction SilentlyContinue; Start-Sleep -Seconds 3; "
                + $"Start-Process -FilePath {Ps(exe)} -WorkingDirectory {Ps(Path.GetDirectoryName(exe))}" + (arguments.Length > 0 ? " -ArgumentList " + Ps(arguments) : "");
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(waiter));
            string create = "$r = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = 'powershell.exe -NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand " + encoded + "' }; exit $r.ReturnValue";
            var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
            foreach (string a in new[] { "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-Command", create }) start.ArgumentList.Add(a);
            using var process = Process.Start(start);
            if (process == null || !process.WaitForExit(20000) || process.ExitCode != 0) throw new InvalidOperationException("the relaunch helper did not start (exit " + (process?.HasExited == true ? process.ExitCode : -1) + ")");
            GameLog.Instance.Info($"update: restarting into {_installed}: {exe} {arguments}");
            MpPlugin.App.ExitGame();
        }
        catch (Exception ex)
        {
            GameLog.Instance.Warn("update: automatic restart failed, the player restarts by hand: " + ex.Message);
            MpMenu.Report($"Space Engineers 2 Online {_installed} is installed. Restart the game to use it.");
        }
    }

    private static string Ps(string text) => "'" + text.Replace("'", "''") + "'";

    // Windows command-line quoting (CommandLineToArgvW rules).
    internal static string QuoteArgument(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return arg;
        var sb = new StringBuilder("\"");
        int slashes = 0;
        foreach (char c in arg)
        {
            if (c == '\\') { slashes++; continue; }
            sb.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
            slashes = 0;
            sb.Append(c);
        }
        return sb.Append('\\', slashes * 2).Append('"').ToString();
    }
}
