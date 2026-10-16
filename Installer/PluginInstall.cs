using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Y51_SE2Online.Core;

namespace SE2OnlineInstaller;

/// <summary>
/// Puts the plugin in the chosen folder. The files come from the SE2 Online release storage (Cloudflare R2, served by
/// the site's /updates/ path) as the newest client ZIP that version.php lists; it must match that listing's size,
/// SHA-256 and release-key signature, exactly like the plugin's own auto-update, before anything is unpacked.
/// </summary>
public static class PluginInstall
{
    public const string UserAgent = "SE2OnlineInstaller/1.0";

    // Files the plugin package ships. Uninstall removes only these, so the player's settings and favorites stay
    // unless they ask for a clean removal.
    private static readonly string[] PackageFiles = { "Y51_SE2Online.dll", "0Harmony.dll", "Y51_SE2Online.pdb", "Y51_SE2Online.deps.json", "LICENSE" };

    public static string StatePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SE2OnlineInstaller", "install.json");

    public sealed record State(string Folder, string Version);

    public static State? Load()
    {
        try { return JsonSerializer.Deserialize<State>(File.ReadAllText(StatePath)); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    public static string DefaultFolder() => Load()?.Folder ?? @"C:\SE2Online";

    /// <summary>Why the folder can't take the plugin, or null. Tries a real write, so permissions are checked too.</summary>
    public static string? CheckFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder)) return "Choose a full folder path, such as C:\\SE2Online.";
        string full = Path.GetFullPath(folder);
        if (Path.GetPathRoot(full) == full) return "Choose a folder, not the root of a drive.";
        try
        {
            Directory.CreateDirectory(full);
            string probe = Path.Combine(full, ".se2online-write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "Windows won't let the installer write to that folder. Pick one in your user folder or on another drive (for example C:\\SE2Online).";
        }
    }

    public static Task<UpdateManifest> LatestAsync(CancellationToken cancel) => AutoUpdate.FetchAsync(AutoUpdate.VersionUrl, UserAgent, cancel);

    /// <summary>Downloads, verifies and installs; returns the installed version.</summary>
    public static async Task<string> InstallAsync(UpdatePackage package, string folder, IProgress<(string Text, double Fraction)> progress, CancellationToken cancel)
    {
        string folderFull = Path.GetFullPath(folder);
        string work = Path.Combine(Path.GetTempPath(), "SE2OnlineInstaller");
        Directory.CreateDirectory(work);
        string zip = Path.Combine(work, "Y51_SE2Online-" + package.Version + ".zip");
        await DownloadAsync(package, zip, progress, cancel);
        progress.Report(("Checking the download's signature...", 1));
        string? problem = AutoUpdate.Verify(zip, package);
        if (problem != null) throw new InvalidDataException("The download failed its safety check (" + problem + "). Nothing was installed.");

        progress.Report(("Installing to " + folderFull + "...", 1));
        string staging = Path.Combine(folderFull, ".se2online-staging");
        // Releases before 0.15.3 also packed Pulsar and server files and docs; the client needs none of them.
        var files = AutoUpdate.Extract(zip, staging, f => !f.StartsWith("Pulsar/", StringComparison.OrdinalIgnoreCase) && !f.StartsWith("DedicatedServer/", StringComparison.OrdinalIgnoreCase) && !f.EndsWith(".md", StringComparison.OrdinalIgnoreCase));
        string? version = AutoUpdate.FileVersion(Path.Combine(staging, "Y51_SE2Online.dll"));
        if (version == null || VersionMismatch.Compare(version, package.Version) != 0)
            throw new InvalidDataException($"The download holds plugin {version ?? "?"}, not {package.Version}. Nothing was installed.");
        AutoUpdate.Swap(staging, folderFull, files);
        Directory.Delete(staging, true);
        AutoUpdate.CleanOld(folderFull);
        File.Delete(zip);

        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        File.WriteAllText(StatePath, JsonSerializer.Serialize(new State(folderFull, version)));
        return version;
    }

    private static async Task DownloadAsync(UpdatePackage package, string path, IProgress<(string, double)> progress, CancellationToken cancel)
    {
        if (!AutoUpdate.AllowedUrl(package.Url)) throw new InvalidDataException("The download address is not HTTPS: " + package.Url);
        if (package.Size <= 0 || package.Size > AutoUpdate.MaxPayloadBytes) throw new InvalidDataException("The release listing has an impossible size.");
        // No redirects: the release storage answers directly, and a redirect could leave HTTPS.
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        using var response = await http.GetAsync(package.Url, HttpCompletionOption.ResponseHeadersRead, cancel);
        if (response.StatusCode != HttpStatusCode.OK) throw new HttpRequestException("The download server answered HTTP " + (int)response.StatusCode + ".");
        await using var body = await response.Content.ReadAsStreamAsync(cancel);
        await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await body.ReadAsync(buffer, cancel)) > 0)
        {
            total += read;
            if (total > package.Size) throw new InvalidDataException("The download is larger than the release listing says. Nothing was installed.");
            await file.WriteAsync(buffer.AsMemory(0, read), cancel);
            progress.Report(($"Downloading SE2 Online {package.Version}: {total / 1048576.0:0.0} of {package.Size / 1048576.0:0.0} MiB", (double)total / package.Size));
        }
    }

    /// <summary>Removes the plugin's files; with <paramref name="everything"/> also its settings and the folder.</summary>
    public static void Uninstall(string folder, bool everything)
    {
        string full = Path.GetFullPath(folder);
        if (!Directory.Exists(full)) return;
        if (!File.Exists(Path.Combine(full, LaunchOptions.PluginFile))) throw new InvalidOperationException("There is no SE2 Online plugin in " + full + ".");
        if (everything)
        {
            // Only the plugin's own files: a folder the player also keeps other things in is left alone.
            foreach (string f in Directory.GetFiles(full).Where(f => Path.GetFileName(f).StartsWith("Y51_SE2Online", StringComparison.OrdinalIgnoreCase))) File.Delete(f);
        }
        foreach (string name in PackageFiles) { string f = Path.Combine(full, name); if (File.Exists(f)) File.Delete(f); }
        string content = Path.Combine(full, "Content");
        if (Directory.Exists(content)) Directory.Delete(content, true);
        AutoUpdate.CleanOld(full);
        if (!Directory.EnumerateFileSystemEntries(full).Any()) Directory.Delete(full);
        if (Load()?.Folder is string saved && string.Equals(Path.GetFullPath(saved), full, StringComparison.OrdinalIgnoreCase)) File.Delete(StatePath);
    }

    /// <summary>Pulsar's own copy of the plugin, if the player also set one up by hand.</summary>
    public static bool PulsarLocalCopy() =>
        Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Pulsar", "Modern", "Local", "Y51_SE2Online"));

    public static bool PulsarInstalled() =>
        Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Pulsar"));
}
