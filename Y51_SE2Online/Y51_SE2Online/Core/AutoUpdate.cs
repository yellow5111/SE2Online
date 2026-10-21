#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Y51_SE2Online.Core;

/// <summary>One update payload as version.php lists it: a release ZIP served as a static file.</summary>
public sealed record UpdatePackage(string Version, string Url, long Size, string Sha256, string Signature);

/// <summary>version.php: the newest client version, plus the signed client and server ZIPs when the site has them.</summary>
public sealed record UpdateManifest(string Version, UpdatePackage? Client, UpdatePackage? Server)
{
    public static UpdateManifest Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        return new UpdateManifest(Text(root, "version") ?? "", Package(root, "client"), Package(root, "server"));
    }

    private static UpdatePackage? Package(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.Object) return null;
        string? version = Text(p, "version"), url = Text(p, "url"), sha = Text(p, "sha256"), signature = Text(p, "signature");
        if (version == null || url == null || sha == null || signature == null || !p.TryGetProperty("size", out var size) || !size.TryGetInt64(out long bytes)) return null;
        return new UpdatePackage(version, url, bytes, sha, signature);
    }

    private static string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>
/// Auto-update plumbing shared by the client plugin and the dedicated server runner. Every payload is checked against
/// its SHA-256 and an ECDSA P-256 signature made with the release key (Sign-Update.ps1) before anything is unpacked,
/// and must contain a strictly newer version than the one running. Nothing in a payload is executed by the updater;
/// the replaced plugin or runner only runs on the next start.
/// </summary>
public static class AutoUpdate
{
    public const string VersionUrl = "https://se2-online.yellow51.dev/version.php";

    /// <summary>SubjectPublicKeyInfo of the release signing key. The private half lives outside every repo and package.</summary>
    public const string PublicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEd3WDb0i090jfItWxPg5ZtAtacH7TL0zeW/BM++2ugFdQeR4hKtKvGtDFsRs36jt+8+dEIEiLhU5UOVTxjSAemQ==";

    public const long MaxPayloadBytes = 512L * 1024 * 1024;

    /// <summary>HTTPS anywhere; plain HTTP only to this machine, for the local update test server.</summary>
    public static bool AllowedUrl(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback));

    private static HttpClient Http(string userAgent, TimeSpan timeout)
    {
        // No redirects: a redirect could leave HTTPS. The site serves the files directly.
        var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        return http;
    }

    public static async Task<UpdateManifest> FetchAsync(string url, string userAgent, CancellationToken cancel = default)
    {
        if (!AllowedUrl(url)) throw new InvalidOperationException("update source must be HTTPS: " + url);
        using var http = Http(userAgent, TimeSpan.FromSeconds(10));
        return UpdateManifest.Parse(await http.GetStringAsync(url, cancel).ConfigureAwait(false));
    }

    /// <summary>Downloads the payload to <paramref name="path"/> and verifies it; null when it is good, else why not.</summary>
    public static async Task<string?> DownloadAsync(UpdatePackage package, string path, string userAgent, CancellationToken cancel = default)
    {
        if (!AllowedUrl(package.Url)) return "download URL is not HTTPS: " + package.Url;
        if (package.Size <= 0 || package.Size > MaxPayloadBytes) return "declared size " + package.Size + " is out of range";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using (var http = Http(userAgent, TimeSpan.FromMinutes(10)))
        using (var response = await http.GetAsync(package.Url, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false))
        {
            if (response.StatusCode != HttpStatusCode.OK) return "download answered HTTP " + (int)response.StatusCode;
            using var body = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
            using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await body.ReadAsync(buffer, 0, buffer.Length, cancel).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > package.Size) return "download is larger than the declared " + package.Size + " bytes";
                await file.WriteAsync(buffer, 0, read, cancel).ConfigureAwait(false);
            }
        }
        return Verify(path, package);
    }

    /// <summary>Size, SHA-256 and signature of a downloaded payload; null when all match, else why not.</summary>
    public static string? Verify(string path, UpdatePackage package, string publicKey = PublicKey)
    {
        var info = new FileInfo(path);
        if (!info.Exists) return "payload missing";
        if (info.Length != package.Size) return $"size {info.Length} does not match the declared {package.Size}";
        byte[] hash;
        using (var stream = File.OpenRead(path)) hash = SHA256.HashData(stream);
        if (!string.Equals(Convert.ToHexString(hash), package.Sha256?.Trim(), StringComparison.OrdinalIgnoreCase)) return "SHA-256 mismatch";
        byte[] signature;
        try { signature = Convert.FromBase64String(package.Signature?.Trim() ?? ""); }
        catch (FormatException) { return "signature is not base64"; }
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
        return key.VerifyHash(hash, signature) ? null : "signature does not verify against the release key";
    }

    /// <summary>The payload's own version must be the one offered and strictly newer than the running one (no downgrades).</summary>
    public static string? CheckVersion(string offered, string? payloadVersion, string current)
    {
        if (string.IsNullOrEmpty(payloadVersion)) return "payload has no plugin version";
        if (VersionMismatch.Compare(payloadVersion, offered) != 0) return $"payload is {payloadVersion}, the site offered {offered}";
        return VersionMismatch.IsNewer(payloadVersion, current) ? null : $"payload {payloadVersion} is not newer than {current} (downgrade refused)";
    }

    /// <summary>Product (informational) version of a plugin DLL, read without loading it.</summary>
    public static string? FileVersion(string dll) => File.Exists(dll) ? FileVersionInfo.GetVersionInfo(dll).ProductVersion : null;

    /// <summary>Unpacks the entries <paramref name="allowed"/> accepts into <paramref name="directory"/>; returns their relative paths.</summary>
    public static List<string> Extract(string zip, string directory, Func<string, bool> allowed)
    {
        string root = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(root);
        var files = new List<string>();
        using var archive = ZipFile.OpenRead(zip);
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith("/", StringComparison.Ordinal)) continue;
            string relative = entry.FullName.Replace('\\', '/');
            if (!allowed(relative)) continue;
            string target = Path.GetFullPath(Path.Combine(root, relative));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase) || relative.Split('/').Any(p => p is "" or "." or ".."))
                throw new InvalidDataException("payload entry escapes the update folder: " + entry.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, false);
            files.Add(relative);
        }
        return files;
    }

    /// <summary>
    /// Moves the staged files over the installed ones. A loaded DLL or running exe cannot be overwritten on Windows but
    /// can be renamed, so each current file becomes "*.old" first. All or nothing: a failure puts every file back.
    /// </summary>
    public static void Swap(string staged, string target, IEnumerable<string> relative)
    {
        var done = new List<(string Dest, string? Old)>();
        try
        {
            foreach (string rel in relative)
            {
                string source = Path.Combine(staged, rel), dest = Path.Combine(target, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                string? old = null;
                if (File.Exists(dest))
                {
                    old = FreeOldName(dest);
                    File.Move(dest, old);
                }
                done.Add((dest, old));
                File.Move(source, dest);
            }
        }
        catch
        {
            for (int i = done.Count - 1; i >= 0; i--)
            {
                try
                {
                    if (done[i].Old == null) { if (File.Exists(done[i].Dest)) File.Delete(done[i].Dest); }
                    else { if (File.Exists(done[i].Dest)) File.Delete(done[i].Dest); File.Move(done[i].Old!, done[i].Dest); }
                }
                catch (Exception) { }
            }
            throw;
        }
    }

    // The previous update's .old can still be in use (a runner that is waiting on its replacement).
    private static string FreeOldName(string dest)
    {
        for (int n = 0; ; n++)
        {
            string old = dest + ".old" + (n == 0 ? "" : n.ToString());
            if (!File.Exists(old)) return old;
            try { File.Delete(old); return old; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Deletes the "*.old" files a previous update left in <paramref name="directory"/> and its Content folder.</summary>
    /// <summary>
    /// Moves temp over path. Antivirus opens every freshly written file, and a reader that doesn't share delete holds
    /// the target, each for a few ms ("Access to the path is denied"): the replace is retried before it fails.
    /// </summary>
    public static void ReplaceFile(string temp, string path)
    {
        for (int attempt = 1; ; attempt++)
        {
            try { File.Move(temp, path, true); return; }
            catch (Exception ex) when (attempt < 5 && ex is UnauthorizedAccessException or IOException) { System.Threading.Thread.Sleep(20 * attempt); }
        }
    }

    public static int CleanOld(string directory)
    {
        int removed = 0;
        var files = Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "*.old*").ToList() : new List<string>();
        string content = Path.Combine(directory, "Content");
        if (Directory.Exists(content)) files.AddRange(Directory.EnumerateFiles(content, "*.old*", SearchOption.AllDirectories));
        foreach (string file in files.Where(f => System.Text.RegularExpressions.Regex.IsMatch(f, @"\.old\d*$")))
        {
            try { File.Delete(file); removed++; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return removed;
    }
}
