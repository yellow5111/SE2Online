using System;
using System.Collections.Generic;
using System.Linq;
using Keen.Game2.Simulation.GameSystems.Player;
using Keen.Game2.Simulation.RuntimeSystems.WorldSettings;
using Y51_SE2Online.Core;
using Y51_SE2Online.Dedicated;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

internal sealed partial class HostWorld
{
    /// <summary>World and server settings for the Server info screen. Plain reads; a value read mid-change is only shown, never used.</summary>
    public ServerInfoRow[] ServerInfo()
    {
        var rows = new List<ServerInfoRow>();
        void Add(string section, string name, string value) => rows.Add(new ServerInfoRow(section, name, value));
        try
        {
            Add("World", "Name", WorldName);
            Add("World", "Mods", WorldMods.Summary(_mods));
            if (BrokenMods.Skipped is { Count: > 0 } skipped) Add("World", "Skipped mods", string.Join(", ", skipped) + " (broken on this game version)");
            var components = _worldSession?.OwnedSession?.SessionComponents;
            if (components?.TryGet<GameModeSessionComponent>() is { } mode)
            {
                Add("World", "Game mode", mode.CreativeGlobal ? "Creative" : "Survival");
                Add("World", "Super dampeners", Sync.ServerInfo.OnOff(mode.SuperDampeners));
                if (mode.IgnoreSafeZones) Add("World", "Safe zones", "Ignored");
            }
            if (components?.TryGet<WorldSettingsSessionComponent>() is { } world)
            {
                Add("World", "Dead bodies removed after", Sync.ServerInfo.Duration(world.DeadCharacterRemovalTimeout));
                Add("World", "Backpacks removed after", Sync.ServerInfo.Duration(world.BackpackRemovalTimeout));
            }
        }
        catch (Exception ex) { _log.WarnOnce("server-info-world", "server info: reading world settings failed: " + ex.Message); }
        var config = DedicatedRuntime.Config;
        if (config != null)
        {
            Add("Server", "Shutdown warning", config.ShutdownCountdownSeconds > 0 ? config.ShutdownCountdownSeconds + " s countdown in chat" : "None");
            var restart = config.AutoRestart;
            string schedule = restart.Times.Length > 0 ? "Daily at " + string.Join(", ", restart.Times)
                : restart.EveryHours > 0 ? $"Every {restart.EveryHours:0.#} h" : "Off";
            Add("Server", "Automatic restart", schedule);
            if (DedicatedRuntime.NextRestart is DateTime next) Add("Server", "Next restart", next.ToString("yyyy-MM-dd HH:mm") + " server time (in " + Sync.ServerInfo.Duration(next - DateTime.Now) + ")");
            Add("Server", "Restart after a crash", restart.RestartOnCrash ? "Yes" : "No");
        }
        return rows.ToArray();
    }
}
