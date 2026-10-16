using System.Reflection;
using Keen.Game2.Simulation.WorldObjects.Characters;
using Keen.VRage.DCS.Components;
using Keen.VRage.Multiplayer.Replications.FailureStrategies;

namespace Y51_SE2Online.SyncEngine;

internal sealed partial class HostWorld
{
    /// <summary>
    /// A living character named by a client's story must be that client's own character. SE2 validates the story's own
    /// component at most (the seat for SetPilotStory/TryClearPilotStory, nothing at all for "ValidateControlled": UseConsumable,
    /// the consumables HUD attaches), never whose character an argument is, so a modified client could seat, unseat or
    /// feed someone else's character from anywhere. Dead bodies stay lootable. Admin stories are left to the admin gate
    /// (admins move other players on purpose).
    /// </summary>
    private string? ForeignCharacterGate(ushort clientId, object box)
    {
        var story = StoryState.Find(box.GetType());
        if (story == null) return null;
        Entity? own = null;
        foreach (FieldInfo field in story.Arguments)
        {
            if (field.Name == "<>4__this") continue;
            Entity? named = field.GetValue(box) switch
            {
                Entity e => e,
                Component c => c.Entity,
                NullOnAnyReplicationFail<Entity> n => n.Value,
                SkipOnReplicationFail<Entity> s => s.Value,
                _ => null,
            };
            if (named == null || named.TryGet<CharacterComponent>() == null || EngineCodec.IsDead(named)) continue;
            own ??= CharacterOf(clientId);
            if (named != own) return "You can only do that with your own character.";
        }
        return null;
    }
}
