using System.Collections.Concurrent;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.DCS.Components;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

internal sealed partial class HostWorld
{
    private readonly ConcurrentDictionary<ushort, Vec3D> _voicePositions = new();

    public bool TryVoicePosition(ushort clientId, out Vec3D position) => _voicePositions.TryGetValue(clientId, out position);

    /// <summary>Each server frame: where every player's character is, for the network thread's voice relay.</summary>
    private void UpdateVoicePositions()
    {
        foreach (var joiner in _joiners.Values) Note(joiner.ClientId, joiner.Character);
        Note(SyncProtocol.HostClientId, Dedicated.DedicatedRuntime.Requested ? null : _hostCharacterServer);
        foreach (ushort id in _voicePositions.Keys)
            if (id != SyncProtocol.HostClientId && !_joiners.ContainsKey(id)) _voicePositions.TryRemove(id, out _);

        void Note(ushort id, Entity? character)
        {
            if (character == null || EngineCodec.IsDead(character)) { _voicePositions.TryRemove(id, out _); return; }
            var p = character.Data.GetWorldTransform().Position;
            _voicePositions[id] = new Vec3D(p.X, p.Y, p.Z);
        }
    }
}
