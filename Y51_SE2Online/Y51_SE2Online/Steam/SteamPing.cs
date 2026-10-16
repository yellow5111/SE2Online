using System;
using System.Collections.Generic;
using System.Text;
using Steamworks;

namespace Y51_SE2Online.Steam;

/// <summary>
/// Steam's ping locations: a host publishes its own (measured to Steam's relays), and a player estimates the round
/// trip from two locations without contacting the host. Used for player-hosted sessions, which have no address to query.
/// </summary>
internal static class SteamPing
{
    private static readonly Dictionary<string, (int Ms, long AtMs)> Cache = new();

    /// <summary>Starts measuring this game's own location; the browser's estimates need it.</summary>
    public static void Start()
    {
        try { SteamNetworkingUtils.InitRelayNetworkAccess(); } catch (Exception) { }
    }

    /// <summary>This game's ping location, "" until Steam has measured it. Dedicated servers use the game-server interface.</summary>
    public static string Local(bool server)
    {
        try
        {
            SteamNetworkPingLocation_t location;
            string text;
            if (server)
            {
                if (SteamGameServerNetworkingUtils.GetLocalPingLocation(out location) < 0) return "";
                SteamGameServerNetworkingUtils.ConvertPingLocationToString(ref location, out text, Constants.k_cchMaxSteamNetworkingPingLocationString);
            }
            else
            {
                if (SteamNetworkingUtils.GetLocalPingLocation(out location) < 0) return "";
                SteamNetworkingUtils.ConvertPingLocationToString(ref location, out text, Constants.k_cchMaxSteamNetworkingPingLocationString);
            }
            return text ?? "";
        }
        catch (Exception) { return ""; }
    }

    /// <summary>A ping location cut to whole relay entries within Steam rich presence's 255 bytes.</summary>
    public static string ForPresence(string location)
    {
        if (Encoding.UTF8.GetByteCount(location) <= 255) return location;
        int cut = location.LastIndexOf(',', 255);
        return cut > 0 ? location.Substring(0, cut) : "";
    }

    /// <summary>Estimated round trip in ms from this game to a published location, or -1 when unknown. UI thread only.</summary>
    public static int Estimate(string location, long nowMs)
    {
        if (string.IsNullOrEmpty(location)) return -1;
        if (Cache.TryGetValue(location, out var hit) && nowMs - hit.AtMs < 2000) return hit.Ms;
        int ms = -1;
        try
        {
            if (SteamNetworkingUtils.ParsePingLocationString(location, out var remote)) ms = Math.Max(-1, SteamNetworkingUtils.EstimatePingTimeFromLocalHost(ref remote));
        }
        catch (Exception) { }
        if (Cache.Count > 512) Cache.Clear();
        Cache[location] = (ms, nowMs);
        return ms;
    }
}
