using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Keen.Game2.Simulation.GameSystems.Explosions;
using Keen.Game2.Simulation.GameSystems.ProjectileSystems;
using Keen.Game2.Simulation.WorldObjects.Characters;
using Keen.Game2.Simulation.WorldObjects.Characters.Damage;
using Keen.Game2.Simulation.WorldObjects.Characters.Stats;
using Keen.Game2.Simulation.GameSystems.Player;
using Keen.Game2.Simulation.WorldObjects.CubeGrids.Damage;
using Keen.Game2.Simulation.WorldObjects.Tools.ToolDamage;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Mathematics;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// Server-side damage rules for multiplayer: with PvP off, players cannot hurt each other's characters or grids; with
/// PvP on, only players whose factions have no treaty or are at war can (<see cref="Sync.RelationTable.MayFight"/>);
/// characters of players who are offline cannot die at all, except to another player's weapons when PvP is on.
/// With PvP on, other players' weapons hurt characters even in a creative world.
/// </summary>
internal static class PvpRules
{
    /// <summary>True on the host or dedicated server while a multiplayer session runs.</summary>
    public static volatile bool Active;
    public static volatile bool Pvp;
    /// <summary>Grids whose owner and whole faction are offline take no damage from other players.</summary>
    public static volatile bool OfflineRaid = true;
    public static long Blocked, OfflineProtected, PlayerHits, RaidsBlocked;
    private static volatile string _lastBlocked = "", _lastHit = "";

    private static volatile HashSet<long> _players = new();
    private static volatile HashSet<long> _online = new();
    private static volatile HashSet<long> _undefended = new();
    private static readonly List<(Vector3D Center, long Attacker, long Ms)> Explosions = new();

    [ThreadStatic] private static long t_attacker;
    [ThreadStatic] private static bool t_pvpDamage;

    private static FieldInfo _spawnSource;
    private static PropertyInfo _simulatedSource;
    private static FieldInfo _statGameMode;
    private static MethodInfo _updateStat;

    public static void Install(Harmony harmony)
    {
        var projectiles = typeof(ProjectileSystemSessionComponent);
        _spawnSource = AccessTools.Field(AccessTools.Inner(projectiles, "ProjectileSpawnInfo"), "SourceEntity");
        // SE2 4.0: the spawn info holds a weak reference, the simulated projectile a property.
        _simulatedSource = AccessTools.Property(AccessTools.Inner(projectiles, "SimulatedProjectileData"), "SourceEntity");
        HarmonyMethod Own(string name) => new(typeof(PvpRules), name);
        harmony.Patch(AccessTools.Method(projectiles, "SpawnHitscanProjectile"), prefix: Own(nameof(HitscanPrefix)), finalizer: Own(nameof(ClearFinalizer)));
        harmony.Patch(AccessTools.Method(projectiles, "ProcessHit"), prefix: Own(nameof(SimulatedPrefix)), finalizer: Own(nameof(ProcessHitFinalizer)));
        harmony.Patch(AccessTools.Method(projectiles, "ProcessHit"), prefix: Own(nameof(HitBodyAlivePrefix)));
        harmony.Patch(AccessTools.Method(projectiles, "ExplodeProjectile"), prefix: Own(nameof(ExplodePrefix)));
        foreach (var receiver in new[] { typeof(CharacterHitReceiverComponent), typeof(GridHitReceiverComponent) })
        {
            harmony.Patch(AccessTools.Method(receiver, "TryReceiveProjectileHit"), prefix: Own(nameof(ProjectileHitPrefix)));
            harmony.Patch(AccessTools.Method(receiver, "ReceiveExplosionHit"), prefix: Own(nameof(ExplosionHitPrefix)), finalizer: Own(nameof(ClearFinalizer)));
        }
        foreach (var tool in new[] { typeof(CharacterToolDamageAdapterComponent), typeof(GridToolDamageAdapterComponent) })
            harmony.Patch(AccessTools.Method(tool, "TryDamage"), prefix: Own(nameof(ToolPrefix)), finalizer: Own(nameof(ClearFinalizer)));
        harmony.Patch(AccessTools.Method(typeof(CharacterLifeComponent), nameof(CharacterLifeComponent.DoDamage)), prefix: Own(nameof(DoDamagePrefix)), finalizer: Own(nameof(DoDamageFinalizer)));
        _statGameMode = AccessTools.Field(typeof(StatCollectionComponent), "_gameMode");
        _updateStat = AccessTools.Method(typeof(StatCollectionComponent), "UpdateStat");
        harmony.Patch(AccessTools.Method(typeof(StatCollectionComponent), nameof(StatCollectionComponent.TrySetValue)), prefix: Own(nameof(TrySetValuePrefix)));
    }

    /// <summary>Every player identity of this world, and those currently connected (server thread snapshots).</summary>
    public static void SetPlayers(IEnumerable<long> players, IEnumerable<long> online, IEnumerable<IReadOnlyCollection<long>> factions)
    {
        var all = players.ToHashSet();
        var present = online.ToHashSet();
        var defended = new HashSet<long>(present);
        foreach (var members in factions)
            if (members.Any(present.Contains)) defended.UnionWith(members);
        _players = all;
        _online = present;
        _undefended = all.Where(p => !defended.Contains(p)).ToHashSet();
    }

    private static bool Raid(Component receiver, long victim)
        => OfflineRaid && (receiver is GridHitReceiverComponent || receiver is GridToolDamageAdapterComponent)
            && IsPlayer(t_attacker) && t_attacker != victim && _undefended.Contains(victim);

    public static void Reset()
    {
        Active = false; Pvp = false;
        _players = new(); _online = new(); _undefended = new();
        lock (Explosions) Explosions.Clear();
    }

    private static bool IsPlayer(long identity) => identity != 0 && _players.Contains(identity);

    /// <summary>Whether damage from <paramref name="attacker"/> to something owned by <paramref name="victim"/> is allowed.</summary>
    public static bool Allowed(long attacker, long victim, bool pvp, Func<long, bool> isPlayer)
        => !isPlayer(attacker) || !isPlayer(victim) || attacker == victim || Sync.RelationTable.MayFight(TurretRelations.Table.Of(attacker, victim), pvp);

    private static void HitscanPrefix(object[] __args)
    {
        t_attacker = Active && __args[0] != null ? TeamPermissions.OwnerOf((_spawnSource.GetValue(__args[0]) as Keen.VRage.Core.Game.Utils.WeakEntityReference)?.Entity) : 0;
    }

    private static void SimulatedPrefix(object[] __args)
    {
        t_attacker = 0;
        if (!Active || __args[2] == null) return;
        if (_simulatedSource.GetValue(__args[2]) is Entity source) t_attacker = TeamPermissions.OwnerOf(source);
    }

    private static void ExplodePrefix(Vector3D explosionCenter)
    {
        if (!Active || t_attacker == 0) return;
        long now = MpClock.NowMs();
        lock (Explosions)
        {
            Explosions.RemoveAll(e => now - e.Ms > 5000);
            Explosions.Add((explosionCenter, t_attacker, now));
        }
    }

    private static bool ProjectileHitPrefix(Component __instance, ref Vector3D? __result)
    {
        if (!Active) return true;
        long victim = TeamPermissions.OwnerOf(__instance.Entity);
        if (Allowed(t_attacker, victim, Pvp, IsPlayer) && !Raid(__instance, victim))
        {
            if (IsPlayer(t_attacker) && IsPlayer(victim) && t_attacker != victim) Note(ref PlayerHits, ref _lastHit, "projectile", victim, __instance);
            return true;
        }
        if (Raid(__instance, victim)) System.Threading.Interlocked.Increment(ref RaidsBlocked);
        Note(ref Blocked, ref _lastBlocked, "projectile", victim, __instance);
        __result = null;
        return false;
    }

    private static void Note(ref long counter, ref string last, string kind, long victim, Component receiver)
    {
        if (System.Threading.Interlocked.Increment(ref counter) % 50 == 1) last = $"{kind} {t_attacker} on {victim} ({receiver.GetType().Name})";
    }

    private static bool ExplosionHitPrefix(Component __instance, ExplosionHit explosion)
    {
        if (!Active) return true;
        long now = MpClock.NowMs();
        lock (Explosions) t_attacker = Explosions.LastOrDefault(e => now - e.Ms <= 5000 && e.Center == explosion.Center).Attacker;
        long victim = TeamPermissions.OwnerOf(__instance.Entity);
        if (Allowed(t_attacker, victim, Pvp, IsPlayer) && !Raid(__instance, victim))
        {
            if (IsPlayer(t_attacker) && IsPlayer(victim) && t_attacker != victim) Note(ref PlayerHits, ref _lastHit, "explosion", victim, __instance);
            return true;
        }
        if (Raid(__instance, victim)) System.Threading.Interlocked.Increment(ref RaidsBlocked);
        Note(ref Blocked, ref _lastBlocked, "explosion", victim, __instance);
        return false;
    }

    private static bool ToolPrefix(Component __instance, ReadOnlySpan<Keen.Game2.Simulation.WorldObjects.Tools.ToolDamage.ToolDamageTarget> targets, Entity sourceTopEntity, ref bool __result)
    {
        t_attacker = Active ? TeamPermissions.OwnerOf(sourceTopEntity) : 0;
        if (!Active) return true;
        foreach (var hit in targets)
        {
            Entity target = hit.Entity;
            long victim = target == null ? 0 : TeamPermissions.OwnerOf(target);
            if (target != null && (!Allowed(t_attacker, victim, Pvp, IsPlayer) || Raid(__instance, victim)))
            {
                if (Raid(__instance, victim)) System.Threading.Interlocked.Increment(ref RaidsBlocked);
                if (System.Threading.Interlocked.Increment(ref Blocked) % 50 == 1) _lastBlocked = $"tool {t_attacker} on {victim}";
                __result = false;
                return false;
            }
        }
        return true;
    }

    /// <summary>Whether damage from one player identity to another would be allowed right now.</summary>
    public static bool MayHurt(long attacker, long victim) => !Active || Allowed(attacker, victim, Pvp, IsPlayer);

    private static readonly Guid ImpactDamage = new("c28c6d5f-44a0-4543-ba28-a7f1b2233e10");

    /// <summary>
    /// A punch, applied as damage from <paramref name="attacker"/> so every rule here decides it (PvP, faction and
    /// alliance relations, offline protection, creative). True when the victim actually lost health.
    /// </summary>
    public static bool Punch(Entity victim, long attacker, float damage)
    {
        var life = victim.TryGet<CharacterLifeComponent>();
        if (life == null || !Keen.VRage.Library.Utils.Singleton<Keen.VRage.Library.Definitions.DefinitionManager>.Instance.TryGetDefinition(ImpactDamage, out DamageSourceDefinition source)) return false;
        float before = life.Health;
        t_attacker = attacker;
        try { life.DoDamage(damage, source); }
        finally { t_attacker = 0; }
        return life.Health < before;
    }

    private static bool _staleHitLogged;

    // The usual stray hit: the body the sweep found was removed before the hit was processed (a grid despawned, a block
    // destroyed in the same tick). Dropped before ProcessHit touches it; the finalizer stays for anything else.
    private static bool HitBodyAlivePrefix(ProjectileSystemSessionComponent __instance, Keen.VRage.Physics.Queries.SweepQueryHit entryHit, ref bool isProjectileAlive, ref Vector3D? __result)
    {
        if (!MpPlugin.Online || __instance.Entity.Scene.IsEntityAlive(entryHit.Body)) return true;
        __result = null;
        isProjectileAlive = false;
        return false;
    }

    // ProcessHit dereferences the projectile's source entity and the hit body's entity. In multiplayer either can
    // be gone mid-flight (a joiner's gun swapped or despawned, a grid despawned), and the NullReferenceException
    // on the in-process server thread takes the host's whole game down. The stray projectile is dropped instead.
    private static Exception ProcessHitFinalizer(Exception __exception, ref Vector3D? __result, ref bool isProjectileAlive)
    {
        t_attacker = 0;
        if (__exception is not NullReferenceException) return __exception;
        __result = null;
        isProjectileAlive = false;
        if (!_staleHitLogged)
        {
            _staleHitLogged = true;
            GameLog.Instance.Warn("pvp: a projectile hit an entity that no longer exists (shooter or target removed mid-flight); the projectile was dropped: " + __exception.Message);
        }
        return null;
    }

    private static Exception ClearFinalizer(Exception __exception)
    {
        t_attacker = 0;
        return __exception;
    }

    // Offline players' characters stay in the world; nothing kills them unless PvP is on and another player attacks.
    private static bool DoDamagePrefix(CharacterLifeComponent __instance, float damage)
    {
        t_pvpDamage = false;
        if (!Active || damage <= 0) return true;
        long owner = TeamPermissions.OwnerOf(__instance.Entity);
        if (!IsPlayer(owner)) return true;
        bool byPlayer = IsPlayer(t_attacker) && t_attacker != owner;
        if (byPlayer && !Sync.RelationTable.MayFight(TurretRelations.Table.Of(t_attacker, owner), Pvp))
        {
            System.Threading.Interlocked.Increment(ref Blocked);
            return false;
        }
        t_pvpDamage = byPlayer;
        if (t_pvpDamage || _online.Contains(owner)) return true;
        System.Threading.Interlocked.Increment(ref OfflineProtected);
        return false;
    }

    private static Exception DoDamageFinalizer(Exception __exception)
    {
        t_pvpDamage = false;
        return __exception;
    }

    // A creative world locks character health; PvP damage from another player still lands there, unless the victim
    // is in per-player creative (an admin), which stays invulnerable. Healing (medbay, survival kit, regen, medkits) must
    // get through the same lock, or that damage could never be undone.
    private static bool TrySetValuePrefix(StatCollectionComponent __instance, StatDefinition stat, float value, ref bool __result)
    {
        if (stat.ModifiableInCreative || _statGameMode.GetValue(__instance) is not GameModeSessionComponent mode || !mode.CreativeGlobal) return true;
        if (!t_pvpDamage && !(stat.Clamp(value) > __instance.GetValue(stat))) return true;
        _updateStat.Invoke(__instance, new object[] { stat, stat.Clamp(value) });
        __result = true;
        return false;
    }

    public static string Describe() => $"pvp {(Pvp ? "on" : "off")}: player-on-player hits {PlayerHits} (last {_lastHit}), blocked {Blocked} (last {_lastBlocked}), offline characters protected {OfflineProtected}, offline raid protection {(OfflineRaid ? "on" : "off")} ({RaidsBlocked} blocked, {_undefended.Count} players undefended)";
}
