using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Keen.Game2.Simulation.GameSystems.TrashRemoval;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.Game2.Simulation.WorldObjects.FloatingObjects;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.Library.Utils;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Mathematics;
using Y51_SE2Online.Core;
using Y51_SE2Online.KeenNetcode;
using Y51_SE2Online.Net;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// SE2 Online's cleanup (host only, server thread). Each pass scans once, warns in chat, optionally saves, then removes at
/// most MaxRemovalsPerSecond, checking every candidate again just before it goes. Whole grid groups only. Every removal is
/// logged with its owner, name, position and reason (server log and audit.jsonl). Player grids go only by the owner
/// inactivity rule (<see cref="CleanupRules"/>); <see cref="PlayerGridGuard"/> keeps the game's own systems off them.
/// </summary>
internal sealed partial class HostWorld
{
    private sealed record Candidate(CleanupCategory Category, List<Entity> Entities, string Name, string Owner, Vector3D At, int Blocks, string Why);

    private sealed class CleanupState
    {
        public int Version { get; set; } = 1;
        /// <summary>When the cleanup first saw each player owner without a last-seen record (their inactivity clock starts there).</summary>
        public Dictionary<string, long> FirstTracked { get; set; } = new();
        /// <summary>NPC grid groups by name and 50 m cell: since when no player was near.</summary>
        public Dictionary<string, long> NpcIdleSince { get; set; } = new();
    }

    private CleanupState _cleanupState;
    private string _cleanupStatePath;
    private long _cleanupStateSavedMs, _nextCleanupMs, _npcScanMs, _vanillaAppliedMs, _removeBudgetMs;
    private int _removeBudget;
    private CleanupSettings _cleanupOverride, _coopCleanup;
    private readonly Dictionary<Entity, long> _cleanupFirstSeen = new(ReferenceEqualityComparer.Instance);
    private List<Candidate> _pass;
    private int _passIndex, _passRemoved, _passSkipped;
    private long _passStartMs;
    private bool _passSaving, _passDry;
    // "cleanup run" works while the scheduled cleanup is off; every other setting is read live during a pass.
    private bool _passForced;
    private string _cleanupLast = "No pass yet.";
    private (TrashRemovalConfiguration.Entry Player, TrashRemovalConfiguration.Entry Other)? _vanillaSaved;

    /// <summary>The settings in force: a session override from the admin panel, else server.json (dedicated) or the co-op host's file.</summary>
    internal CleanupSettings CleanupConfig
    {
        get
        {
            if (_cleanupOverride != null) return _cleanupOverride;
            if (Y51_SE2Online.Dedicated.DedicatedRuntime.Requested) return Y51_SE2Online.Dedicated.DedicatedRuntime.Config?.Cleanup ?? new CleanupSettings();
            return _coopCleanup ??= LoadCoopCleanup();
        }
    }

    /// <summary>The co-op host's cleanup settings, Y51_SE2Online.cleanup.json beside the plugin settings, written with the defaults when missing.</summary>
    internal static CleanupSettings LoadCoopCleanup()
    {
        string path = PluginPaths.Data("Y51_SE2Online.cleanup.json");
        try
        {
            if (File.Exists(path))
            {
                var settings = JsonSerializer.Deserialize<CleanupSettings>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) ?? new();
                if (settings.Validate() is { } problem) { GameLog.Instance.Warn($"cleanup: {path}: {problem}; the cleanup is off until it is fixed"); return new CleanupSettings { Enabled = false }; }
                return settings;
            }
            var defaults = new CleanupSettings();
            File.WriteAllText(path, JsonSerializer.Serialize(defaults, new JsonSerializerOptions { WriteIndented = true }));
            return defaults;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            GameLog.Instance.Warn($"cleanup: could not read {path} ({ex.Message}); the cleanup is off");
            return new CleanupSettings { Enabled = false };
        }
    }

    /// <summary>Re-reads the co-op host's settings file (after the Host screen saved it).</summary>
    internal void ReloadCoopCleanup() => _coopCleanup = null;

    private volatile string _cleanupStatusText = "The cleanup starts with the world.", _cleanupConfigJson = "";

    /// <summary>The cleanup's state line, built on the game thread every second (the net thread reads it for the panel).</summary>
    public string CleanupStatus() => _cleanupStatusText;

    /// <summary>The settings in force as JSON, published on the game thread.</summary>
    public string CleanupConfigJson() => _cleanupConfigJson;

    private long _statusBuiltMs;

    private string BuildCleanupStatus()
    {
        var s = CleanupConfig;
        if (!s.Enabled) return "Cleanup is off. " + _cleanupLast;
        string rules = string.Join(", ", new[]
        {
            s.FloatingItems.Enabled ? $"floating items after {s.FloatingItems.MinAgeMinutes:0} min" : null,
            s.Debris.Enabled ? $"unowned debris up to {s.Debris.MaxBlocks} blocks" : null,
            s.NpcGrids.Enabled ? $"NPC grids idle {CleanupRules.Duration(s.NpcGrids.IdleMinutes)}" : null,
            s.PlayerGrids.Enabled ? $"player grids whose owners were offline {CleanupRules.Duration(s.PlayerGrids.InactiveMinutes)}" : null
        }.Where(r => r != null));
        string next = _pass != null ? (_passSaving ? "saving before the pass" : _passIndex == 0 && MpClock.NowMs() < _passStartMs ? $"pass starts in {(_passStartMs - MpClock.NowMs()) / 1000 + 1} s" : $"pass running ({_passIndex}/{_pass.Count})")
            : _nextCleanupMs == 0 ? "first pass soon" : $"next pass in {Math.Max(0, (_nextCleanupMs - MpClock.NowMs()) / 60000)} min";
        return $"Cleanup {(s.DryRun ? "(dry run) " : "")}on: {(rules.Length == 0 ? "no categories on" : rules)}; nothing within {s.ProtectWithinMeters:0} m of a player; {next}. {_cleanupLast}";
    }

    private void UpdateCleanup(Session session)
    {
        long now = MpClock.NowMs();
        try
        {
            PlayerGridGuard.Sweep(session);
            var s = CleanupConfig;
            if (now - _statusBuiltMs >= 1000)
            {
                _statusBuiltMs = now;
                _cleanupStatusText = BuildCleanupStatus();
                _cleanupConfigJson = JsonSerializer.Serialize(s);
            }
            if (now - _vanillaAppliedMs > 60_000) { _vanillaAppliedMs = now; ApplyVanilla(session, s); }
            if (_cleanupState == null) LoadCleanupState();
            if (now - _npcScanMs > 60_000) { _npcScanMs = now; TrackNpcIdle(session, s); }
            if (_pass != null) { RunPass(session, _passForced && !s.Enabled ? WithEnabled(s) : s, now); return; }
            if (!s.Enabled) return;
            if (_nextCleanupMs == 0) { _nextCleanupMs = now + 5 * 60_000; return; }
            if (now < _nextCleanupMs) return;
            _nextCleanupMs = now + s.EveryMinutes * 60_000L;
            StartPass(session, s, null, warn: true, actor: "schedule");
        }
        catch (Exception ex) { _pass = null; _log.WarnOnce("cleanup-failed", "cleanup: a pass failed and was stopped: " + ex); }
    }

    // The game's own grid PCU trash removal: "off" turns it off; "npc-only" leaves it on, and PlayerGridGuard pins every
    // grid group a player owns any part of, so it can only trim NPC and unowned grids.
    private void ApplyVanilla(Session session, CleanupSettings s)
    {
        var manager = session.SessionComponents.TryGet<TrashRemovalManagerSessionComponent>();
        if (manager?.Settings == null) return;
        // The game sorts grids into NPC and "player" (everything else: player-owned and unowned). npc-only turns its player
        // bucket off: it trims NPC grids only, and unowned debris is left to our own Debris rule (age and distance checked).
        var off = new TrashRemovalConfiguration.Entry { TargetCleanupLimit = int.MaxValue, ExecuteCleanupLimit = int.MaxValue };
        _vanillaSaved ??= (manager.Settings.PlayerGridPCU, manager.Settings.NonPlayerGridPCU);
        var (player, npc) = _vanillaSaved.Value;
        var wantNpc = s.VanillaGridPcu == "off" ? off : npc;
        if (manager.Settings.PlayerGridPCU.ExecuteCleanupLimit != int.MaxValue || manager.Settings.NonPlayerGridPCU.ExecuteCleanupLimit != wantNpc.ExecuteCleanupLimit)
        {
            manager.Settings.PlayerGridPCU = off; manager.Settings.NonPlayerGridPCU = wantNpc;
            _log.Info(s.VanillaGridPcu == "off" ? "cleanup: the game's grid PCU trash removal is off (Cleanup.VanillaGridPcu off)"
                : $"cleanup: the game's grid PCU trash removal trims only NPC grids now (over {npc.ExecuteCleanupLimit:N0} NPC PCU); it never removes player or unowned grids");
        }
    }

    private void LoadCleanupState()
    {
        _cleanupStatePath = Y51_SE2Online.Dedicated.DedicatedRuntime.Requested && Y51_SE2Online.Dedicated.DedicatedRuntime.Config is { } config
            ? Path.Combine(config.Directory, "cleanup-state.json")
            : PluginPaths.Data("Y51_SE2Online.cleanup-state." + string.Concat((WorldKey ?? "world").Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_')) + ".json");
        try { _cleanupState = File.Exists(_cleanupStatePath) ? JsonSerializer.Deserialize<CleanupState>(File.ReadAllText(_cleanupStatePath)) : null; }
        catch (Exception ex) when (ex is IOException or JsonException) { _log.Warn("cleanup: " + _cleanupStatePath + " unreadable (" + ex.Message + "); inactivity clocks start again"); }
        _cleanupState ??= new CleanupState();
        _cleanupState.FirstTracked ??= new(); _cleanupState.NpcIdleSince ??= new();
    }

    private void SaveCleanupState(bool force = false)
    {
        long now = MpClock.NowMs();
        if (_cleanupState == null || _cleanupStatePath == null || (!force && now - _cleanupStateSavedMs < 300_000)) return;
        _cleanupStateSavedMs = now;
        try
        {
            File.WriteAllText(_cleanupStatePath + ".tmp", JsonSerializer.Serialize(_cleanupState));
            File.Move(_cleanupStatePath + ".tmp", _cleanupStatePath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _log.WarnOnce("cleanup-state", "cleanup: could not write " + _cleanupStatePath + ": " + ex.Message); }
    }

    private List<Vector3D> PlayerPositions()
    {
        var list = new List<Vector3D>();
        if (_hostCharacterServer is { } host && !EngineCodec.IsDead(host) && !Y51_SE2Online.Dedicated.DedicatedRuntime.Requested) list.Add(host.Data.GetWorldTransform().Position);
        foreach (var j in _joiners.Values) if (j.Character is { } c && !EngineCodec.IsDead(c)) list.Add(c.Data.GetWorldTransform().Position);
        return list;
    }

    private static double Nearest(List<Vector3D> players, IEnumerable<Entity> entities)
    {
        double best = double.MaxValue;
        foreach (var e in entities)
        {
            var at = e.Data.GetWorldTransform().Position;
            foreach (var p in players) best = Math.Min(best, Vector3D.Distance(at, p));
        }
        return best;
    }

    private static string NpcKey(string name, Vector3D at) => $"{name}|{Math.Round(at.X / 50)}|{Math.Round(at.Y / 50)}|{Math.Round(at.Z / 50)}";

    // ponytail: SE2 entities keep no id across a save, so an NPC group is known by its name and 50 m cell; a moved or
    // renamed NPC grid starts its idle clock again (only ever later, never sooner).
    private void TrackNpcIdle(Session session, CleanupSettings s)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var players = PlayerPositions();
        var live = new HashSet<string>();
        foreach (var group in Groups(session))
        {
            var owners = group.Select(TeamPermissions.OwnerOf).ToArray();
            if (CleanupRules.GroupCategory(owners) != CleanupCategory.NpcGrids) continue;
            var main = Main(group);
            string key = NpcKey(GridName(main), main.Data.GetWorldTransform().Position);
            live.Add(key);
            if (Nearest(players, group) < Math.Max(s.NpcGrids.MinDistanceMeters, s.ProtectWithinMeters)) _cleanupState.NpcIdleSince[key] = now;
            else _cleanupState.NpcIdleSince.TryAdd(key, now);
        }
        foreach (string gone in _cleanupState.NpcIdleSince.Keys.Where(k => !live.Contains(k)).ToList()) _cleanupState.NpcIdleSince.Remove(gone);
        SaveCleanupState();
    }

    private static IEnumerable<List<Entity>> Groups(Session session)
    {
        var seen = new HashSet<Entity>(ReferenceEqualityComparer.Instance);
        foreach (var grid in session.GetEntitiesOfType<CubeGridComponent>())
        {
            if (EngineCodec.IsDead(grid) || seen.Contains(grid)) continue;
            var group = PlayerGridGuard.Group(grid);
            foreach (var g in group) seen.Add(g);
            yield return group;
        }
    }

    private static Entity Main(List<Entity> group) => group.OrderByDescending(g => g.Get<CubeGridComponent>().CubeBlockStorage.CubeBlockCount).First();

    private static bool Powered(List<Entity> group)
    {
        foreach (var g in group)
        {
            if (!g.Get<CubeGridComponent>().GridIsTurnedOn) continue;
            bool supplied = false;
            g.Get<CubeGridComponent>().VisitAllBlocksWithComponent<PowerableBlockComponent>(p => { if (p.Supplied) supplied = true; }, includeSubgrids: false);
            if (supplied) return true;
        }
        return false;
    }

    private HashSet<Entity> GamePinned(Session session)
    {
        var pinned = session.SessionComponents.TryGet<GridPCUTrashRemovalSessionComponent>()?._pinnedGrids;
        return pinned == null ? new HashSet<Entity>() : new HashSet<Entity>(pinned.OfType<Entity>(), ReferenceEqualityComparer.Instance);
    }

    /// <summary>What a decision needs about the world right now: player positions, who is online, last-seen times, factions.</summary>
    private sealed record ScanContext(long NowMs, long Utc, List<Vector3D> Players, HashSet<Entity> Pinned, HashSet<long> Online, Dictionary<long, long> Seen, List<HashSet<long>> Factions);

    private ScanContext Context(Session session)
    {
        var online = new HashSet<long>((_host?.Players() ?? Array.Empty<HostSession.PlayerRecord>()).Where(p => p.Peer.HasValue && p.IdentityId != 0).Select(p => p.IdentityId));
        if (!Y51_SE2Online.Dedicated.DedicatedRuntime.Requested && _hostIdentity != 0) online.Add(_hostIdentity);
        var seen = (_host?.Access.SeenCopy() ?? Array.Empty<SeenEntry>()).Where(e => e.Identity != 0).GroupBy(e => e.Identity).ToDictionary(g => g.Key, g => g.Max(e => e.LastSeenUnixMs));
        var social = Social;
        List<HashSet<long>> factions;
        lock (social?.Sync ?? new object()) factions = social?.State.Factions.Select(f => new HashSet<long>(f.Members)).ToList() ?? new();
        return new ScanContext(MpClock.NowMs(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), PlayerPositions(), GamePinned(session), online, seen, factions);
    }

    /// <summary>One grid group's category and the reason it goes now (null: it stays), from the world as it is.</summary>
    private Candidate Evaluate(List<Entity> group, CleanupSettings s, ScanContext x, CleanupCategory? only, bool track = true)
    {
        var owners = group.Select(TeamPermissions.OwnerOf).Distinct().ToArray();
        var category = CleanupRules.GroupCategory(owners);
        if (only != null && category != only) return null;
        var main = Main(group);
        string name = GridName(main);
        var at = main.Data.GetWorldTransform().Position;
        int blocks = group.Sum(g => g.Get<CubeGridComponent>().CubeBlockStorage.CubeBlockCount);
        long first = CleanupRules.Clock(_cleanupFirstSeen, main, x.NowMs, track);
        double age = category == CleanupCategory.NpcGrids
            ? (_cleanupState.NpcIdleSince.TryGetValue(NpcKey(name, at), out long idle) ? (x.Utc - idle) / 60000.0 : 0)
            : (x.NowMs - first) / 60000.0;
        double inactive = 0;
        bool exempt = false;
        if (category == CleanupCategory.PlayerGrids)
        {
            foreach (long owner in owners.Where(PlayerIdentities.IsHuman))
            {
                ulong steam = _host?.SteamOfIdentity(owner) ?? 0;
                if (steam == 0) steam = _host?.Access.SeenCopy().FirstOrDefault(e => e.Identity == owner)?.SteamId ?? 0;
                if (steam != 0 && _host!.Allowed(steam, "cleanup.exempt")) exempt = true;
                CleanupRules.Clock(_cleanupState.FirstTracked, owner.ToString(System.Globalization.CultureInfo.InvariantCulture), x.Utc, track);
            }
            inactive = CleanupRules.InactiveMinutes(owners, s.PlayerGrids.FactionShared, x.Utc,
                id => x.Seen.TryGetValue(id, out long ms) ? ms : null,
                id => x.Factions.FirstOrDefault(f => f.Contains(id)) ?? Enumerable.Empty<long>(),
                id => _cleanupState.FirstTracked.TryGetValue(id.ToString(System.Globalization.CultureInfo.InvariantCulture), out long ms) ? ms : x.Utc,
                x.Online.Contains);
        }
        var facts = new CleanupFacts
        {
            Category = category, Name = name, Owners = owners, Blocks = blocks,
            Powered = category == CleanupCategory.Debris && s.Debris.RequireUnpowered && Powered(group),
            Occupied = group.Any(Occupied), Pinned = group.Any(x.Pinned.Contains),
            NearestPlayerMeters = Nearest(x.Players, group), AgeMinutes = age, OwnersInactiveMinutes = inactive, OwnerExempt = exempt
        };
        if (CleanupRules.Why(s, facts) is not { } why) return null;
        return new Candidate(category, group, name, string.Join(", ", owners.Select(o => OwnerName(o) + (PlayerIdentities.IsHuman(o) && (_host?.SteamOfIdentity(o) ?? 0) is var st && st != 0 ? $" ({st})" : "")).Distinct()), at, blocks, why);
    }

    /// <summary>What a pass would take right now: every candidate with its reason.</summary>
    private List<Candidate> Scan(Session session, CleanupSettings s, CleanupCategory? only)
    {
        var x = Context(session);
        var found = new List<Candidate>();
        var live = new HashSet<Entity>(ReferenceEqualityComparer.Instance);
        if (only is null or CleanupCategory.FloatingItems)
            foreach (var item in session.GetEntitiesOfType<FloatingObjectComponent>())
            {
                if (EngineCodec.IsDead(item) || item.TryGet<FloatingContainerComponent>() != null) continue;
                live.Add(item);
                long first = _cleanupFirstSeen.TryGetValue(item, out long t) ? t : _cleanupFirstSeen[item] = x.NowMs;
                var facts = new CleanupFacts { Category = CleanupCategory.FloatingItems, Name = item.GetComposition()?.DebugNameShort ?? "item", AgeMinutes = (x.NowMs - first) / 60000.0, NearestPlayerMeters = Nearest(x.Players, new[] { item }) };
                if (CleanupRules.Why(s, facts) is { } why) found.Add(new Candidate(CleanupCategory.FloatingItems, new List<Entity> { item }, facts.Name, "nobody", item.Data.GetWorldTransform().Position, 0, why));
            }
        foreach (var group in Groups(session))
        {
            foreach (var g in group) live.Add(g);
            if (Evaluate(group, s, x, only) is { } candidate) found.Add(candidate);
        }
        foreach (var gone in _cleanupFirstSeen.Keys.Where(e => !live.Contains(e)).ToList()) _cleanupFirstSeen.Remove(gone);
        SaveCleanupState();
        return found;
    }

    private static string Summary(List<Candidate> list)
    {
        if (list.Count == 0) return "nothing";
        var parts = list.GroupBy(c => c.Category).Select(g => $"{g.Count()} {Label(g.Key, g.Count())}").ToList();
        var players = list.Where(c => c.Category == CleanupCategory.PlayerGrids).Take(3).Select(c => $"{c.Owner}'s '{c.Name}'").ToList();
        return string.Join(", ", parts) + (players.Count > 0 ? " (" + string.Join("; ", players) + ")" : "");
    }

    private static string Label(CleanupCategory c, int n) => c switch
    {
        CleanupCategory.FloatingItems => n == 1 ? "floating item" : "floating items",
        CleanupCategory.Debris => n == 1 ? "debris grid" : "debris grids",
        CleanupCategory.NpcGrids => n == 1 ? "idle NPC grid" : "idle NPC grids",
        _ => n == 1 ? "abandoned player grid" : "abandoned player grids"
    };

    private string StartPass(Session session, CleanupSettings s, CleanupCategory? only, bool warn, string actor)
    {
        if (_pass != null) return "A cleanup pass is already running (cleanup cancel stops it).";
        if (_cleanupState == null) LoadCleanupState();
        var found = Scan(session, s, only);
        if (found.Count == 0) { _cleanupLast = $"Last pass {DateTime.Now:HH:mm}: nothing to clean up."; return "Nothing to clean up."; }
        _pass = found; _passForced = actor == "admin"; _passIndex = 0; _passRemoved = 0; _passSkipped = 0; _passDry = s.DryRun; _passSaving = false;
        bool online = PlayerPositions().Count > 0 || (_host?.Players().Any(p => p.Peer.HasValue) ?? false);
        _passStartMs = MpClock.NowMs() + (warn && online ? s.WarnSeconds * 1000L : 0);
        string summary = Summary(found);
        _log.Info($"cleanup: pass started by {actor}{(_passDry ? " (dry run)" : "")}: {summary}");
        AuditLog.Write("cleanup", actor, _passDry ? "dry run" : "pass", summary);
        if (online && warn && s.WarnSeconds > 0)
            _host?.LocalChat(_passDry ? $"Cleanup dry run in {s.WarnSeconds} s would remove {summary}. Nothing will be removed." : $"Cleanup in {s.WarnSeconds} s: {summary}. Name a grid [KEEP] or stay near it to keep it.", true);
        if (!_passDry && s.SaveBefore && Y51_SE2Online.Dedicated.DedicatedRuntime.Requested && found.Any(c => c.Category != CleanupCategory.FloatingItems))
        {
            _passSaving = true;
            Y51_SE2Online.Dedicated.DedicatedRuntime.SaveNow(ok =>
            {
                _passSaving = false;
                if (ok) return;
                _log.Warn("cleanup: the save before the pass failed; pass cancelled");
                _cleanupLast = $"Pass at {DateTime.Now:HH:mm} cancelled: the save before it failed.";
                _pass = null;
            });
        }
        return $"{(_passDry ? "Dry run" : "Cleanup")} {(warn && online && s.WarnSeconds > 0 ? "in " + s.WarnSeconds + " s" : "now")}: {summary}.";
    }

    private void RunPass(Session session, CleanupSettings s, long now)
    {
        if (_passSaving || now < _passStartMs) return;
        if (s.DryRun) _passDry = true;
        if (now - _removeBudgetMs >= 1000) { _removeBudgetMs = now; _removeBudget = s.MaxRemovalsPerSecond; }
        var x = _removeBudget > 0 ? Context(session) : null;
        while (_passIndex < _pass.Count && _removeBudget > 0)
        {
            var c = _pass[_passIndex++];
            if (c.Entities.All(EngineCodec.IsDead)) continue;
            // Checked again: things move between the scan and now.
            if (!StillDue(session, s, c, x)) { _passSkipped++; continue; }
            _removeBudget--;
            string line = $"{(_passDry ? "would remove" : "removed")} {Label(c.Category, 1)} '{c.Name}' of {c.Owner} at {c.At.X:0}, {c.At.Y:0}, {c.At.Z:0}{(c.Blocks > 0 ? $", {c.Blocks} blocks in {c.Entities.Count} grid{(c.Entities.Count == 1 ? "" : "s")}" : "")}: {c.Why}";
            _log.Info("cleanup: " + line);
            AuditLog.Write("cleanup", "cleanup", _passDry ? "would remove" : "remove", line, new { Category = c.Category.ToString(), c.Name, c.Owner, X = Math.Round(c.At.X), Y = Math.Round(c.At.Y), Z = Math.Round(c.At.Z), c.Blocks, c.Why });
            if (_passDry) { _passRemoved++; continue; }
            var spawner = session.Get<IEntitySpawner>();
            PlayerGridGuard.Allow(() => { foreach (var e in c.Entities) if (!EngineCodec.IsDead(e)) spawner.DeleteEntity(e); });
            _passRemoved++;
        }
        if (_passIndex < _pass.Count) return;
        string done = $"{(_passDry ? "Cleanup dry run" : "Cleanup")} done: {(_passDry ? "would have removed" : "removed")} {_passRemoved}{(_passSkipped > 0 ? $", kept {_passSkipped} that changed since the warning" : "")}.";
        _cleanupLast = $"Last pass {DateTime.Now:HH:mm}: {(_passDry ? "dry run, " : "")}{_passRemoved} {(_passDry ? "would go" : "removed")}, {_passSkipped} kept.";
        _log.Info("cleanup: " + done);
        if (PlayerPositions().Count > 0 && (_passRemoved > 0 || _passDry)) _host?.LocalChat(done, true);
        _pass = null;
        SaveCleanupState(true);
    }

    // Everything is decided again from the world as it is now: the group (docked or split since), its owners, their
    // last-seen times and factions, distance, seats and the keep tag. A grid that changed in any way is kept this pass.
    private bool StillDue(Session session, CleanupSettings s, Candidate c, ScanContext x)
    {
        if (!s.Enabled && !_passDry) return false;
        if (Nearest(x.Players, c.Entities) < s.ProtectWithinMeters) return false;
        if (c.Category == CleanupCategory.FloatingItems) return true;
        var alive = c.Entities.FirstOrDefault(e => !EngineCodec.IsDead(e));
        if (alive == null) return false;
        var now = PlayerGridGuard.Group(alive);
        if (now.Count != c.Entities.Count || now.Any(g => !c.Entities.Contains(g))) return false;
        return Evaluate(now, s, x, c.Category) is not null;
    }

    private string CleanupAdmin(Session session, PeerId? peer, AdminRequest r)
    {
        var s = CleanupConfig;
        if (!CleanupRules.TryCategory(r.Name, out var category) && r.Action != AdminAction.CleanupSettings) return "Categories: floating, debris, npc, player (or all).";
        switch (r.Action)
        {
        case AdminAction.CleanupPreview:
        {
            var found = Scan(session, s, category);
            _host?.SendAdminQuery(peer, new AdminQuery
            {
                Kind = "cleanup",
                Text = found.Count == 0 ? "Nothing would be cleaned up now." : $"{found.Count} would go now{(s.DryRun ? " (dry run is on)" : "")}: {Summary(found)}",
                Rows = found.Take(100).Select(c => new QueryRow(c.Entities.Count > 0 && c.Category != CleanupCategory.FloatingItems ? _ids.GetOrAssign(c.Entities[0]) : 0, c.Name, c.Owner, c.Blocks, -1, Label(c.Category, 1) + ": " + c.Why)).ToArray()
            });
            string list = string.Join("\n", found.Take(20).Select(c => $"{Label(c.Category, 1)} '{c.Name}' of {c.Owner} at {c.At.X:0}, {c.At.Y:0}, {c.At.Z:0}: {c.Why}"));
            return found.Count == 0 ? "Nothing would be cleaned up now." : $"{found.Count} would go now: {Summary(found)}" + (peer == null ? "\n" + list + (found.Count > 20 ? $"\n... and {found.Count - 20} more" : "") : "");
        }
        case AdminAction.CleanupRun:
            if (r.Minutes == -1)
            {
                if (_pass == null) return "No cleanup pass is running.";
                _pass = null; _passSaving = false;
                _host?.LocalChat("Cleanup cancelled.", true);
                return "Cleanup pass cancelled.";
            }
            return StartPass(session, s.Enabled ? s : WithEnabled(s), category, warn: !r.Value, actor: "admin");
        case AdminAction.CleanupSettings:
        {
            CleanupSettings next;
            try { next = JsonSerializer.Deserialize<CleanupSettings>(r.Name ?? "", new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
            catch (JsonException ex) { return "Those cleanup settings are not readable: " + ex.Message; }
            if (next == null) return "No settings sent.";
            if (next.Validate() is { } problem) return problem;
            _cleanupOverride = next;
            _vanillaAppliedMs = 0;
            AuditLog.Write("cleanup", "admin", "settings", JsonSerializer.Serialize(next));
            return "Cleanup settings changed for this session (until the server restarts; " + (Y51_SE2Online.Dedicated.DedicatedRuntime.Requested ? "set Cleanup in server.json" : "the Host screen's Cleanup section") + " keeps them).";
        }
        default: return "Unknown cleanup action.";
        }
    }

    private static CleanupSettings WithEnabled(CleanupSettings s) { var copy = s.Copy(); copy.Enabled = true; return copy; }
}
