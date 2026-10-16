using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Keen.Game2.Simulation.GameSystems;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.Game2.Simulation.WorldObjects.CubeGrids.Physicss;
using Keen.Game2.Simulation.WorldObjects.Characters.Emotes;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.Core.Game.GameSystems.Queries;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Definitions;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Library.Utils;
using Keen.VRage.Physics;
using Keen.VRage.Physics.Queries;
using Keen.VRage.Voxels.Components;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

internal sealed partial class HostWorld
{
    private readonly ConcurrentQueue<(ushort From, ushort To)> _punches = new();
    private readonly PendingShare _punchShare = new(64, PunchRules.MaxPendingPerClient);
    private readonly ConcurrentDictionary<ushort, long> _lastPunch = new();
    private readonly ConcurrentDictionary<ushort, long> _lastFight = new();
    private readonly ConcurrentDictionary<ushort, long> _lastEmote = new();
    private readonly List<(long DueMs, ushort Attacker, ushort Victim)> _fightHits = new();
    private int _emoteLogs;
    private readonly ConcurrentDictionary<ushort, (long Since, long Until, bool ByOther)> _fightLocks = new();
    private readonly ConcurrentDictionary<ushort, long> _lockReleased = new();
    private const double ChestHeight = 1.0;

    /// <summary>Whether a player is held still by a fight scene or punch swing (movement, body turning and tools).</summary>
    internal bool FightLocked(ushort clientId) => _fightLocks.ContainsKey(clientId);

    /// <param name="byOther">Someone else's punch or fight holds this player, not their own swing.</param>
    private void LockFighter(ushort clientId, bool byOther)
    {
        long now = MpClock.NowMs();
        if (_fightLocks.TryGetValue(clientId, out var held))
        {
            // Never extended: a hit during the player's own swing only caps that lock at the victim limit.
            if (byOther && !held.ByOther) _fightLocks[clientId] = (held.Since, Math.Min(held.Until, PunchRules.LockUntil(true, now)), true);
            return;
        }
        if (!PunchRules.MayLock(false, byOther, Released(clientId), now)) return;
        _fightLocks[clientId] = (now, PunchRules.LockUntil(byOther, now), byOther);
        _host?.SendFightLock(clientId, true);
        if (CharacterOf(clientId) is Entity body) StopInput(body);
        _log.Info($"punch: ClientId {clientId} input held for the scene");
    }

    private long? Released(ushort clientId) => _lockReleased.TryGetValue(clientId, out long at) ? at : null;

    // Ends on scene end (no emote playing on the server any more), death, disconnect (no character) or the safety timeout.
    private void UpdateFightLocks()
    {
        long now = MpClock.NowMs();
        foreach (var (clientId, (since, until, byOther)) in _fightLocks)
        {
            Entity body = CharacterOf(clientId);
            var emotes = body?.TryGet<EmoteControllerComponent>();
            string? over = body == null ? "no character" : EngineCodec.IsDead(body) ? "died" : now >= until ? "safety timeout"
                : now - since > PunchRules.LockGraceMs && emotes != null && !emotes.IsEmotePlaying ? "scene over" : null;
            if (over == null) { StopInput(body); continue; }
            _fightLocks.TryRemove(clientId, out _);
            if (byOther) _lockReleased[clientId] = now;
            _host?.SendFightLock(clientId, false);
            _log.Info($"punch: ClientId {clientId} input released ({over})");
        }
    }

    public void OnPunch(ushort fromClientId, ushort targetClientId)
    {
        if (_punchShare.Admit(fromClientId)) _punches.Enqueue((fromClientId, targetClientId));
    }

    /// <summary>
    /// SE2's RequestPlayEmote story also covers admin-only multi-character scenes, so the admin guard refuses it for
    /// every joiner. A single-character emote on the joiner's own character is allowed (null: not an emote story,
    /// "": allowed, otherwise the refusal).
    /// </summary>
    private string? EmoteGate(ushort clientId, object box)
    {
        Type type = box.GetType();
        if (type.DeclaringType != typeof(EmoteControllerComponent) || !type.Name.StartsWith("<RequestPlayEmote>", StringComparison.Ordinal)) return null;
        var character = type.GetField("character")?.GetValue(box) as Entity;
        if (type.GetField("emote")?.GetValue(box) is not EmoteDefinition emote || emote.Reactions.Length != 0) return null;
        string? refused = null;
        long now = MpClock.NowMs();
        if (character == null || character != CharacterOf(clientId)) refused = "You can only play emotes on your own character.";
        else if (_lastEmote.TryGetValue(clientId, out long last) && now - last < 500) refused = "Slow down with the emotes.";
        else _lastEmote[clientId] = now;
        if (_emoteLogs++ < 40) _log.Info($"emotes: ClientId {clientId} asks for {emote.DebugNameShort}: {refused ?? "playing it"}");
        return refused ?? "";
    }

    private void UpdatePunches()
    {
        UpdateFightLocks();
        while (_punches.TryDequeue(out var punch))
        {
            _punchShare.Release(punch.From);
            try { Punch(punch.From, punch.To); }
            catch (Exception ex) { _log.WarnOnce("host-punch", "punch: ClientId " + punch.From + " failed: " + ex.Message); }
        }
        long now = MpClock.NowMs();
        for (int i = _fightHits.Count - 1; i >= 0; i--)
        {
            var hit = _fightHits[i];
            if (now < hit.DueMs) continue;
            _fightHits.RemoveAt(i);
            try { FightHit(hit.Attacker, hit.Victim); }
            catch (Exception ex) { _log.WarnOnce("host-fight-hit", "punch: fight hit failed: " + ex.Message); }
        }
    }

    private void Punch(ushort from, ushort to)
    {
        long now = MpClock.NowMs();
        if (_lastPunch.TryGetValue(from, out long last) && !PunchRules.Ready(last, now)) return;
        Entity attacker = CharacterOf(from);
        if (attacker == null || EngineCodec.IsDead(attacker)) return;
        _lastPunch[from] = now;
        Entity victim = to == 0 || to == from ? null : CharacterOf(to);
        bool inReach = false;
        if (victim != null && !EngineCodec.IsDead(victim))
        {
            var a = attacker.Data.GetWorldTransform().Position;
            var b = victim.Data.GetWorldTransform().Position;
            inReach = PunchRules.InReach(a.X - b.X, a.Y - b.Y, a.Z - b.Z) && Clear(attacker, Chest(attacker.Data.GetWorldTransform()), Chest(victim.Data.GetWorldTransform()));
        }
        if (inReach)
        {
            long attackerId = TeamPermissions.OwnerOf(attacker), victimId = TeamPermissions.OwnerOf(victim);
            if (!PvpRules.MayHurt(attackerId, victimId))
            {
                string why = PunchRules.Refusal(TurretRelations.Table.Of(attackerId, victimId), PvpRules.Pvp);
                _host?.GpsNotice(from, $"You can't fight {NameOf(to)}: {why}.");
                _log.Info($"punch: ClientId {from} -> ClientId {to}: refused ({why})");
                return;
            }
            if (!(_lastFight.TryGetValue(from, out long fought) && now - fought < PunchRules.FightCooldownMs)
                && PunchRules.MayLock(FightLocked(to), true, Released(to), now) && StartFight(attacker, victim))
            {
                _lastFight[from] = now;
                LockFighter(from, false);
                LockFighter(to, true);
                foreach (long at in PunchRules.FightHitsMs) _fightHits.Add((now + at, from, to));
                _log.Info($"punch: ClientId {from} -> ClientId {to}: two-character fight started");
                return;
            }
        }
        // Fallback: the single-character swing (out of reach, target busy or seated, fight on cooldown).
        if (Singleton<DefinitionManager>.Instance.TryGetDefinition(Emotes.Punch, out EmoteDefinition swing))
        {
            attacker.TryGet<EmoteControllerComponent>()?.RequestPlay(swing);
            LockFighter(from, false);
        }
        if (!inReach) return;
        bool hurt = PvpRules.Punch(victim, TeamPermissions.OwnerOf(attacker), PunchRules.Damage);
        if (hurt && Singleton<DefinitionManager>.Instance.TryGetDefinition(Emotes.GotHit, out EmoteDefinition hit))
        {
            victim.TryGet<EmoteControllerComponent>()?.RequestPlay(hit);
            LockFighter(to, true);
        }
        _log.Info($"punch: ClientId {from} -> ClientId {to}: swing, {(hurt ? PunchRules.Damage + " damage" : "no damage")}");
    }

    /// <summary>
    /// SE2's own two-character emote: the primary runs CS_PunchEmoteDefinition, which plays its reaction on the entity
    /// named after the reaction's actor ("Punch") and parents that entity to the primary at its current offset. The
    /// host lines the pair up, names the partner for the start only and runs the scene; the animation and parenting
    /// signals replicate to every client like any other emote. The victim stays where it is and only turns; the attacker
    /// moves to a spot on its own side of the victim, and only when no grid or voxel lies on the way there.
    /// </summary>
    private bool StartFight(Entity attacker, Entity victim)
    {
        var names = (attacker.Scene?.UserObject as Keen.VRage.Core.Game.Systems.Session)?.SessionComponents.TryGet<EntityNameSessionComponent>();
        Entity primary = PunchRules.AttackerIsSecondary ? victim : attacker, secondary = PunchRules.AttackerIsSecondary ? attacker : victim;
        var lead = primary.TryGet<EmoteControllerComponent>();
        var partner = secondary.TryGet<EmoteControllerComponent>();
        if (names == null || lead == null || partner == null || lead.IsInSeat || partner.IsInSeat || lead.IsEmotePlaying || partner.IsEmotePlaying) return false;
        if (primary.Data.TryGet<EntityParentData>(out _) || secondary.Data.TryGet<EntityParentData>(out _) || names.TryGetNamedEntity(PunchRules.FightRole) != null) return false;
        if (!Singleton<DefinitionManager>.Instance.TryGetDefinition(PunchRules.Fight, out EmoteDefinition fight)) return false;
        WorldTransform a = attacker.Data.GetWorldTransform(), v = victim.Data.GetWorldTransform();
        Vector3 up = v.Orientation.GetUp(), toAttacker = (Vector3)(a.Position - v.Position);
        toAttacker -= up * Vector3.Dot(toAttacker, up);
        if (!(toAttacker.LengthSquared() > 1e-4f)) return false;
        toAttacker = toAttacker.GetNormalized();
        Vector3D spot = v.Position + (Vector3D)toAttacker * PunchRules.FightOffset;
        if (!Clear(attacker, Chest(a), spot + (Vector3D)up * ChestHeight)) return false;
        // The scene parents the partner at its current offset, so the pair must face each other first.
        victim.Data.Set(new WorldTransform(v.Position, Quaternion.CreateFromForwardUp(toAttacker, up)));
        attacker.Data.Set(new WorldTransform(spot, Quaternion.CreateFromForwardUp(-toAttacker, up)));
        names.AssignName(secondary, PunchRules.FightRole);
        try { lead.RequestPlay(fight); }
        finally { names.ClearName(PunchRules.FightRole); }
        return lead.IsEmotePlaying;
    }

    private static Vector3D Chest(in WorldTransform at) => at.Position + (Vector3D)at.Orientation.GetUp() * ChestHeight;

    /// <summary>No grid or voxel between the two points; characters, tools and loose items do not block.</summary>
    private static bool Clear(Entity context, Vector3D from, Vector3D to)
    {
        var physics = context.GetSession<IPhysics>();
        var scene = context.Scene;
        if (physics == null || scene == null) return false;
        using Keen.VRage.Library.Memory.Buffer<SweepQueryHit> hits = default;
        var ray = RayCastArgs.CreateFromTo(from, to);
        physics.CastRay(hits, in ray, CollisionPreset.Default);
        foreach (var hit in hits)
        {
            if (VoxelStorageComponentBase.IsEntityVoxel((Scene: scene, Entity: hit.Body))) return false;
            var body = Entity.TryGetFromDataEntity((Scene: scene, Entity: hit.Body));
            if (body != null && (body.TryGet<CubeGridComponent>() != null || body.TryGet<GridPhysicsComponent>() != null)) return false;
        }
        return true;
    }

    private void FightHit(ushort from, ushort to)
    {
        Entity attacker = CharacterOf(from), victim = CharacterOf(to);
        if (attacker == null || victim == null || EngineCodec.IsDead(attacker) || EngineCodec.IsDead(victim)) return;
        bool hurt = PvpRules.Punch(victim, TeamPermissions.OwnerOf(attacker), PunchRules.FightHitDamage);
        _log.Info($"punch: fight hit ClientId {from} -> ClientId {to}: {(hurt ? PunchRules.FightHitDamage + " damage" : "no damage")}");
    }

    private string NameOf(ushort clientId)
    {
        foreach (var player in _host?.PlayerTable() ?? new List<PlayerInfo>())
            if (player.ClientId == clientId) return player.Name;
        return "that player";
    }
}
