using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Keen.Game2.Simulation.GameSystems;
using Keen.Game2.Simulation.WorldObjects.Characters.Emotes;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Definitions;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Library.Utils;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

internal sealed partial class HostWorld
{
    private readonly ConcurrentQueue<(ushort From, ushort To)> _punches = new();
    private readonly ConcurrentDictionary<ushort, long> _lastPunch = new();
    private readonly ConcurrentDictionary<ushort, long> _lastFight = new();
    private readonly ConcurrentDictionary<ushort, long> _lastEmote = new();
    private readonly List<(long DueMs, ushort Attacker, ushort Victim)> _fightHits = new();
    private int _emoteLogs;
    private readonly ConcurrentDictionary<ushort, (long Since, long Until)> _fightLocks = new();

    /// <summary>Whether a player is held still by a fight scene or punch swing (movement, body turning and tools).</summary>
    internal bool FightLocked(ushort clientId) => _fightLocks.ContainsKey(clientId);

    private void LockFighter(ushort clientId)
    {
        long now = MpClock.NowMs();
        bool already = _fightLocks.ContainsKey(clientId);
        _fightLocks[clientId] = (now, now + PunchRules.LockSafetyMs);
        if (already) return;
        _host?.SendFightLock(clientId, true);
        if (CharacterOf(clientId) is Entity body) StopInput(body);
        _log.Info($"punch: ClientId {clientId} input held for the scene");
    }

    // Ends on scene end (no emote playing on the server any more), death, disconnect (no character) or the safety timeout.
    private void UpdateFightLocks()
    {
        long now = MpClock.NowMs();
        foreach (var (clientId, (since, until)) in _fightLocks)
        {
            Entity body = CharacterOf(clientId);
            var emotes = body?.TryGet<EmoteControllerComponent>();
            string? over = body == null ? "no character" : EngineCodec.IsDead(body) ? "died" : now >= until ? "safety timeout"
                : now - since > PunchRules.LockGraceMs && emotes != null && !emotes.IsEmotePlaying ? "scene over" : null;
            if (over == null) { StopInput(body); continue; }
            _fightLocks.TryRemove(clientId, out _);
            _host?.SendFightLock(clientId, false);
            _log.Info($"punch: ClientId {clientId} input released ({over})");
        }
    }

    public void OnPunch(ushort fromClientId, ushort targetClientId)
    {
        if (_punches.Count < 64) _punches.Enqueue((fromClientId, targetClientId));
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
            inReach = PunchRules.InReach(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
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
            if (!(_lastFight.TryGetValue(from, out long fought) && now - fought < PunchRules.FightCooldownMs) && StartFight(attacker, victim))
            {
                _lastFight[from] = now;
                LockFighter(from);
                LockFighter(to);
                foreach (long at in PunchRules.FightHitsMs) _fightHits.Add((now + at, from, to));
                _log.Info($"punch: ClientId {from} -> ClientId {to}: two-character fight started");
                return;
            }
        }
        // Fallback: the single-character swing (out of reach, target busy or seated, fight on cooldown).
        if (Singleton<DefinitionManager>.Instance.TryGetDefinition(Emotes.Punch, out EmoteDefinition swing))
        {
            attacker.TryGet<EmoteControllerComponent>()?.RequestPlay(swing);
            LockFighter(from);
        }
        if (!inReach) return;
        bool hurt = PvpRules.Punch(victim, TeamPermissions.OwnerOf(attacker), PunchRules.Damage);
        if (hurt && Singleton<DefinitionManager>.Instance.TryGetDefinition(Emotes.GotHit, out EmoteDefinition hit))
        {
            victim.TryGet<EmoteControllerComponent>()?.RequestPlay(hit);
            LockFighter(to);
        }
        _log.Info($"punch: ClientId {from} -> ClientId {to}: swing, {(hurt ? PunchRules.Damage + " damage" : "no damage")}");
    }

    /// <summary>
    /// SE2's own two-character emote: the primary runs CS_PunchEmoteDefinition, which plays its reaction on the entity
    /// named after the reaction's actor ("Punch") and parents that entity to the primary at its current offset. The
    /// host places the partner, names it for the start only and runs the scene; the animation and parenting signals
    /// replicate to every client like any other emote.
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
        var at = primary.Data.GetWorldTransform();
        Vector3 forward = at.Orientation.GetForward(), up = at.Orientation.GetUp();
        secondary.Data.Set(new WorldTransform(at.Position + (Vector3D)forward * PunchRules.FightOffset, Quaternion.CreateFromForwardUp(-forward, up)));
        names.AssignName(secondary, PunchRules.FightRole);
        try { lead.RequestPlay(fight); }
        finally { names.ClearName(PunchRules.FightRole); }
        return lead.IsEmotePlaying;
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
