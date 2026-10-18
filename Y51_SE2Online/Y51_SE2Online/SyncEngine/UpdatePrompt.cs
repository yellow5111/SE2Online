using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Keen.Game2.Client.UI.Library;
using Keen.Game2.Client.UI.Library.Dialogs.OneOptionDialog;
using Keen.Game2.Client.UI.Library.Dialogs.ThreeOptionsDialog;
using Keen.Game2.Client.UI.Library.Dialogs.TwoOptionsDialog;
using Keen.VRage.Library.Localization;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.SyncEngine;

/// <summary>Release check against the main site, and the native dialogs for new releases and version refusals.</summary>
internal static class UpdatePrompt
{
    public const string VersionUrl = "https://se2-online.yellow51.dev/version.php";
    private static bool _checked;
    private static volatile string _available;

    /// <summary>The newest release the site reported this game start; null until the check answers (or with checks off).</summary>
    public static volatile string Latest;

    /// <summary>The installed version, and what the site offers once it has answered.</summary>
    public static string VersionLine()
    {
        string line = "SE2 Online version " + MpPlugin.Version;
        string latest = Latest;
        if (!MpMenu.Settings.CheckForUpdates) return line;
        if (latest == null) return line + " - checking se2-online.yellow51.dev...";
        if (AutoUpdateClient.Installed != null) return line + " - version " + AutoUpdateClient.Installed + " is installed; restart the game to use it";
        return VersionMismatch.IsNewer(latest, MpPlugin.Version)
            ? line + " - version " + latest + " is available on se2-online.yellow51.dev"
            : line + " - up to date (latest " + latest + ")";
    }

    public static async Task<string> LatestAsync() => (await AutoUpdate.FetchAsync(VersionUrl, "Y51_SE2Online/" + MpPlugin.Version)).Version;

    /// <summary>Checks once per game start; the dialog waits for the main menu.</summary>
    public static void Tick(SharedUIComponent shared, bool atMainMenu)
    {
        if (!_checked && MpMenu.Settings.CheckForUpdates)
        {
            _checked = true;
            Task.Run(async () =>
            {
                try
                {
                    var manifest = await AutoUpdate.FetchAsync(AutoUpdateClient.Url, "Y51_SE2Online/" + MpPlugin.Version);
                    string latest = manifest.Version;
                    Latest = latest;
                    GameLog.Instance.Info($"update check: newest release {latest ?? "unknown"}, this game runs {MpPlugin.Version}");
                    if (!VersionMismatch.IsNewer(latest, MpPlugin.Version) || latest == MpMenu.Settings.SkippedUpdate) return;
                    if (AutoUpdateClient.Eligible && manifest.Client != null && await AutoUpdateClient.InstallAsync(manifest.Client)) return;
                    _available = latest;
                }
                catch (Exception ex) { GameLog.Instance.Info("update check: " + ex.GetType().Name + ": " + ex.Message); }
            });
        }
        AutoUpdateClient.Tick(shared, atMainMenu);
        string available = _available;
        if (available == null || shared == null || !atMainMenu) return;
        _available = null;
        if (PluginPaths.PulsarHub)
        {
            shared.ShowDialog(new TwoOptionsDialogViewModel(new TwoOptionsDialogDefinition
            {
                Title = OnlineUi.Raw("Update available"),
                Content = OnlineUi.Raw($"Space Engineers 2 Online {available} is available. You have {MpPlugin.Version}.\n\nServers only admit players on their exact version. " + PulsarUpdateHint),
                ConfirmOption = OnlineUi.Raw("OK"),
                CancelOption = OnlineUi.Raw("Skip this version")
            }) { CancelAction = () => MpMenu.RememberSkippedUpdate(available) });
            return;
        }
        shared.ShowDialog(new ThreeOptionsDialogViewModel(new ThreeOptionsDialogDefinition
        {
            Title = OnlineUi.Raw("Update available"),
            Content = OnlineUi.Raw($"Space Engineers 2 Online {available} is available. You have {MpPlugin.Version}.\n\nServers only admit players on their exact version. Update now?"),
            ConfirmOption = OnlineUi.Raw("Update"),
            DefaultOption = OnlineUi.Raw("Skip this version"),
            CancelOption = OnlineUi.Raw("Later")
        })
        {
            ConfirmAction = OpenDownloadPage,
            DefaultAction = () => MpMenu.RememberSkippedUpdate(available)
        });
    }

    /// <summary>A server refused the join because its plugin version differs.</summary>
    public static void VersionRefused(SharedUIComponent shared, string hostVersion, string serverText)
    {
        string text = string.IsNullOrEmpty(hostVersion) ? serverText : VersionMismatch.Explain(hostVersion, MpPlugin.Version);
        if (VersionMismatch.IsNewer(hostVersion, MpPlugin.Version) && PluginPaths.PulsarHub)
            text += "\n\n" + PulsarUpdateHint;
        else if (VersionMismatch.IsNewer(hostVersion, MpPlugin.Version))
        {
            shared.ShowDialog(new TwoOptionsDialogViewModel(new TwoOptionsDialogDefinition
            {
                Title = OnlineUi.Raw("Different version"), Content = OnlineUi.Raw(text),
                ConfirmOption = OnlineUi.Raw("Get update"), CancelOption = OnlineUi.Raw("OK")
            }) { ConfirmAction = OpenDownloadPage });
            return;
        }
        shared.ShowDialog(new OneOptionDialogViewModel(new OneOptionDialogDefinition
        {
            Title = OnlineUi.Raw("Different version"), Content = OnlineUi.Raw(text), ConfirmOption = OnlineUi.Raw("OK")
        }));
    }

    // Hub releases go live only after the hub merges the new commit, which can lag the website.
    private const string PulsarUpdateHint = "Pulsar installs plugin updates when the game starts, so restart the game to update. If the version stays the same, the update is still waiting for approval on Pulsar's plugin hub.";

    // Downloads need a Steam sign-in on the site, so the browser does the update.
    public static void OpenDownloadPage()
    {
        try { Process.Start(new ProcessStartInfo(VersionMismatch.DownloadPage) { UseShellExecute = true }); }
        catch (Exception ex) { GameLog.Instance.Warn("update: could not open the download page: " + ex.Message); }
    }
}
