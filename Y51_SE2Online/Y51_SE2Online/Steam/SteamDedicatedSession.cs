using System;
using System.Threading;
using Y51_SE2Online.Core;
using Steamworks;

namespace Y51_SE2Online.Steam;

internal sealed class SteamDedicatedSession : IDisposable
{
    private bool _initialized;
    public ulong SteamId { get; private set; }
    public void Initialize(MpOptions options, string gameVersion, Func<bool> stopping)
    {
        ushort gamePort = (ushort)(options.SteamListenIp?.Port ?? MpOptions.DefaultIpPort);
        ushort queryPort = (ushort)(gamePort == 65535 ? 65534 : gamePort + 1);
        if (!GameServer.Init(0, gamePort, queryPort, EServerMode.eServerModeAuthenticationAndSecure, gameVersion)) throw new InvalidOperationException("Steam game-server initialization failed. Check Steam and the game/query ports.");
        _initialized = true;
        SteamGameServer.SetProduct("SpaceEngineers2");
        SteamGameServer.SetModDir("SpaceEngineers2");
        SteamGameServer.SetGameDescription("Space Engineers 2 Online");
        SteamGameServer.SetDedicatedServer(true);
        SteamGameServer.SetServerName(options.SessionName);
        // A2S_INFO carries the limit in one byte; above 255 Steam's browser would show it wrapped.
        SteamGameServer.SetMaxPlayerCount(Math.Min(options.MaxPlayers, byte.MaxValue));
        SteamGameServer.SetAdvertiseServerActive(true);
        SteamGameServer.LogOnAnonymous();
        SteamGameServerNetworkingUtils.InitRelayNetworkAccess();
        long started = MpClock.NowMs();
        while (!stopping())
        {
            GameServer.RunCallbacks();
            SteamId = SteamGameServer.GetSteamID().m_SteamID;
            if (SteamGameServer.BLoggedOn() && SteamId != 0) { _live = true; return; }
            if (MpClock.NowMs() - started > 60000) throw new InvalidOperationException("Steam could not sign in the dedicated server within 60 seconds.");
            Thread.Sleep(100);
        }
        throw new OperationCanceledException("Dedicated-server startup was stopped.");
    }
    private static int _publishedAccess = -1;
    private static volatile bool _live;

    /// <summary>The game server's public IPv4 address as Steam sees it; null before sign-in or while Steam does not know it.</summary>
    public static System.Net.IPAddress PublicIp()
    {
        if (!_live) return null;
        try
        {
            // IPv4 only: the game port a dedicated server binds by default (0.0.0.0) is IPv4.
            var ip = SteamGameServer.GetPublicIP();
            return ip.IsSet() && ip.GetIPType() == ESteamIPType.k_ESteamIPTypeIPv4 ? ip.ToIPAddress() : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// Password and whitelist in the Steam server list: the lock flag plus rules "password" and "whitelist" ("1"/"0").
    /// Servers before 0.17.5 publish no such rules, which readers take as unknown. Only changes reach Steam.
    /// </summary>
    public static void PublishAccess(bool password, bool whitelist)
    {
        int access = (password ? 1 : 0) | (whitelist ? 2 : 0);
        if (access == _publishedAccess) return;
        SteamGameServer.SetPasswordProtected(password);
        SteamGameServer.SetKeyValue("password", password ? "1" : "0");
        SteamGameServer.SetKeyValue("whitelist", whitelist ? "1" : "0");
        _publishedAccess = access;
    }

    public void Dispose()
    {
        _publishedAccess = -1;
        _live = false;
        if (!_initialized) return;
        SteamGameServer.LogOff();
        GameServer.Shutdown();
        _initialized = false;
    }
}
