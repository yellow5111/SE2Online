using System;

namespace Y51_SE2Online.Core;

/// <summary>Plugin version comparison and the wording players see when builds differ.</summary>
public static class VersionMismatch
{
    public const string DownloadPage = "https://se2-online.yellow51.dev/#downloads";

    /// <summary>-1 when <paramref name="a"/> is older, 1 when newer, 0 when equal or unparseable.</summary>
    public static int Compare(string? a, string? b)
    {
        if (!TryParse(a, out var x) || !TryParse(b, out var y)) return 0;
        return x.CompareTo(y);
    }

    public static bool IsNewer(string? candidate, string? current) => Compare(candidate, current) > 0;

    // "0.13.1" and "0.13.1.0" are the same release.
    private static bool TryParse(string? text, out Version version)
    {
        version = null;
        if (!Version.TryParse((text ?? "").Split('-', '+')[0], out var v)) return false;
        version = new Version(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));
        return true;
    }

    /// <summary>What the server tells a client whose plugin build differs.</summary>
    public static string Refusal(string host, string client)
        => $"This server runs Space Engineers 2 Online {host}. You have {(string.IsNullOrEmpty(client) ? "a different version" : client)}. "
            + (IsNewer(host, client) ? $"Update to {host} to join." : IsNewer(client, host) ? $"The server is on an older version; ask its owner to update, or install {host} to join." : $"Install {host} to join.");

    /// <summary>The client's own explanation, from the versions it saw.</summary>
    public static string Explain(string host, string mine)
        => IsNewer(host, mine) ? $"This server runs Space Engineers 2 Online {host}, which is newer than yours ({mine}). Update to join."
            : IsNewer(mine, host) ? $"This server runs Space Engineers 2 Online {host}, which is older than yours ({mine}). Ask the server owner to update, or install {host} to join."
            : $"This server runs Space Engineers 2 Online {host}. You have {mine}. Both need the same version.";
}
