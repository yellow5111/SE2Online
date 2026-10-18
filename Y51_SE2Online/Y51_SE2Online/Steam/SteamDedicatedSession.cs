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
            if (SteamGameServer.BLoggedOn() && SteamId != 0) return;
            if (MpClock.NowMs() - started > 60000) throw new InvalidOperationException("Steam could not sign in the dedicated server within 60 seconds.");
            Thread.Sleep(100);
        }
        throw new OperationCanceledException("Dedicated-server startup was stopped.");
    }
    public void Dispose()
    {
        if (!_initialized) return;
        SteamGameServer.LogOff();
        GameServer.Shutdown();
        _initialized = false;
    }
}
