using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Avalonia.Controls;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.SyncEngine;

/// <summary>The Host page's CLEANUP section: the co-op host's Y51_SE2Online.cleanup.json (a dedicated server uses server.json).</summary>
public sealed partial class OnlineScreen
{
    private readonly OnlineToggle _cleanupOn = new("Cleanup", "Floating items and tiny unowned debris by default. Player grids only by the owner-offline rule below.");
    private readonly OnlineToggle _cleanupDry = new("Dry run", "Announce and log what would go, remove nothing");
    private readonly OnlineToggle _cleanupItems = new("Floating items", "Dropped items and ore older than 15 min, no player within 100 m");
    private readonly OnlineToggle _cleanupDebris = new("Unowned debris", "Grids nobody owns with up to 5 blocks, unpowered, no player within 300 m");
    private readonly OnlineToggle _cleanupNpc = new("Idle NPC grids", "NPC grids no player was near for the idle days. A grid a player took over is a player grid.");
    private readonly OnlineToggle _cleanupPlayers = new("Abandoned player grids", "Only when every owner was offline for the days below. Off by default.");
    private readonly OnlineToggle _cleanupFaction = new("Faction keeps it", "An owner counts as active while any member of their faction is");
    private readonly TextBox _cleanupNpcDays = OnlineUi.Input("days", 6), _cleanupPlayerDays = OnlineUi.Input("days", 6);
    private readonly TextBlock _cleanupNote = OnlineUi.Text("", "SubtleNotice", 18);

    private Control BuildCleanupSection()
    {
        LoadCleanupFields();
        var apply = OnlineUi.SmallButton("Apply cleanup", ApplyCleanup, "ApplyCleanup");
        return OnlineUi.Section("CLEANUP", _cleanupOn, _cleanupDry, _cleanupItems, _cleanupDebris, _cleanupNpc,
            OnlineUi.Field("NPC idle days", _cleanupNpcDays, "At least 1 hour (0.05 days)", null, 200),
            _cleanupPlayers, OnlineUi.Field("Owner offline days", _cleanupPlayerDays, "At least 1 day", null, 200), _cleanupFaction,
            OnlineUi.Text("Nothing within 300 m of a player, nothing anyone sits in and no grid named [KEEP] is touched. Every removal is in the log and in Y51_SE2Online.audit.jsonl. Advanced settings: Y51_SE2Online.cleanup.json next to the plugin settings.", "SubtleNotice", 18),
            apply, _cleanupNote);
    }

    private void LoadCleanupFields()
    {
        var s = HostWorld.LoadCoopCleanup();
        _cleanupOn.IsChecked = s.Enabled; _cleanupDry.IsChecked = s.DryRun;
        _cleanupItems.IsChecked = s.FloatingItems.Enabled; _cleanupDebris.IsChecked = s.Debris.Enabled;
        _cleanupNpc.IsChecked = s.NpcGrids.Enabled; _cleanupPlayers.IsChecked = s.PlayerGrids.Enabled; _cleanupFaction.IsChecked = s.PlayerGrids.FactionShared;
        _cleanupNpcDays.Text = (s.NpcGrids.IdleMinutes / 1440).ToString("0.##", CultureInfo.InvariantCulture);
        _cleanupPlayerDays.Text = (s.PlayerGrids.InactiveMinutes / 1440).ToString("0.##", CultureInfo.InvariantCulture);
    }

    private void ApplyCleanup()
    {
        var s = HostWorld.LoadCoopCleanup();
        s.Enabled = _cleanupOn.IsChecked; s.DryRun = _cleanupDry.IsChecked;
        s.FloatingItems.Enabled = _cleanupItems.IsChecked; s.Debris.Enabled = _cleanupDebris.IsChecked;
        s.NpcGrids.Enabled = _cleanupNpc.IsChecked; s.PlayerGrids.Enabled = _cleanupPlayers.IsChecked; s.PlayerGrids.FactionShared = _cleanupFaction.IsChecked;
        if (!double.TryParse(_cleanupNpcDays.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double npc) || !double.TryParse(_cleanupPlayerDays.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double players))
        {
            _cleanupNote.Text = "Enter the days as numbers, for example 7 or 0.5.";
            return;
        }
        s.NpcGrids.IdleDays = npc; s.NpcGrids.IdleHours = 0; s.PlayerGrids.InactiveDays = players; s.PlayerGrids.InactiveHours = 0;
        if (s.Validate() is { } problem) { _cleanupNote.Text = problem; return; }
        try
        {
            string path = PluginPaths.Data("Y51_SE2Online.cleanup.json");
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(path + ".tmp", path, true);
            HostWorld.Current?.ReloadCoopCleanup();
            _cleanupNote.Text = "Cleanup settings saved; they apply from the next pass.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _cleanupNote.Text = "Could not save: " + ex.Message; }
    }
}
