#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using HarmonyLib;
using Keen.Game2.Client.GameSystems;
using Keen.Game2.Client.GameSystems.Interaction;
using Keen.Game2.Client.UI.Library.Controls.ControlHints;
using Keen.Game2.Client.WorldObjects.CubeBlocks.Cockpit;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.Pilotable;
using Keen.Game2.Simulation.WorldObjects.Characters;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.Core.Game.Definitions;
using Keen.VRage.Core.Model;
using Keen.VRage.DCS.Components;
using Keen.VRage.DCS.Definitions;
using Keen.VRage.DCS.ObjectBuilders;
using Keen.VRage.Game.Components.Models;
using Keen.VRage.Input;
using Keen.VRage.Library.Collections;
using Keen.VRage.Library.Collections.Readers;
using Keen.VRage.Library.Definitions;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Library.Threading;
using Keen.VRage.Library.Utils;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// SE2 ships Seat, Toilet and Bed blocks with no seat in them. Before each world loads, their composites get SE2's own
/// <see cref="SeatComponent"/> (the control seat's definition, so the native sitting pose), and their models get the
/// control seat's "Seat" and "Enter" dummies. Sitting, riding with the grid, one occupant, release on death, block
/// destruction or loss of function, saving the seated player and multiplayer replication are then SE2's own seat code;
/// the plugin's story gate checks grid access exactly as for cockpits. With no controllable on the block, nobody flies
/// the ship from it. SE2's Use action on the Enter dummy only knows cockpits, so it is taught to sit down in these, and
/// while seated in one Use stands the player up again.
/// </summary>
internal static class PassengerSeats
{
    private sealed record Block(string Name, Guid Composite, Guid ClientComposite, Guid Model, float SeatHeight, float SeatBack, float Yaw);

    // Where the seated character's origin goes: height above the model's floor (about 0.35 m above the cushion, as in the
    // control seat), offset towards the seat's back in metres (the bed's is negative: sit on its edge), and facing.
    // Tuned from rig screenshots in E:\SE2MP\artifacts\seats.
    private static readonly Block[] Blocks =
    {
        new("Seat", new("45fedc87-d06c-4499-bc06-05a7864a8343"), new("4a1a3ea8-1da8-4232-813e-755548575108"), new("96b0ffdc-3e90-4398-9bc0-443ddb7d94cb"), 0.80f, 0.05f, 180f),
        new("Toilet", new("4e787b1a-0ee4-4c52-8936-b921f817451d"), new("0f8f5754-797c-42aa-8125-d0fb49823d20"), new("685525a9-4aab-4d08-8a22-40072a696417"), 0.77f, 0.05f, 0f),
        new("Bed", new("2afbbab3-a9c1-4508-a8d0-320a9442111d"), new("9a73c55d-a663-4edc-80b2-a5f57aa62770"), new("64585083-da8a-45dc-ab67-07656ec2055a"), 0.78f, -0.25f, 0f),
    };

    private static readonly Guid ControlSeatSeat = new("ac78e060-eede-4ab4-8830-1d955fb7bd51");
    // The control seat's client-side seat: hides the jetpack and backpack (a render preset) while someone sits.
    private static readonly Guid ControlSeatClientSeat = new("45f8d0ac-2496-4304-88c2-f863ef7469ec");
    private static readonly Guid ControlSeatModel = new("93524a87-56ad-4604-84e7-c568cea5552e");
    private static readonly Guid SeatDummyType = new("8e581544-8c74-4c85-be9f-5e175f2792c1");
    private static readonly Guid EnterDummyType = new("2193fd9c-28c1-4e86-b5c3-e4c695ecefe4");
    // The slot cockpits and control seats keep their SeatComponent in, so saved seat data lands in the same place.
    private static readonly Guid SeatSlot = new("43efa952-810d-4e80-8733-038c52f5b6f7");
    // SE2's generic UseAction.def; its InteractPrimary is the player's Use key.
    private static readonly Guid UseActionId = new("f49a9b10-fe13-42f5-8bd8-dc32f98c4333");

    private static readonly HashSet<Guid> Composites = Blocks.SelectMany(b => new[] { b.Composite, b.ClientComposite }).ToHashSet();
    private static readonly AccessTools.FieldRef<PrefabDefinition, EntityObjectBuilder> PrefabEntity = AccessTools.FieldRefAccess<PrefabDefinition, EntityObjectBuilder>("_entity");
    private static readonly object Lock = new();
    private static bool _sitting;

    internal static string State { get; private set; } = "not applied";

    /// <summary>Whether this entity is one of the blocks given a passenger seat.</summary>
    internal static bool IsPassengerSeat(Entity? entity) => entity != null && entity.GetComposition() is { } c && Composites.Contains(c.Guid) && entity.TryGet<SeatComponent>() != null;

    /// <summary>
    /// Idempotent; has to run before any replication or entity of these blocks is built, so it runs on every definition
    /// set push, world init and replication setup. Replication maps server to client components by tag when it is made.
    /// </summary>
    internal static void Apply()
    {
        lock (Lock)
        {
            try
            {
                var defs = Singleton<DefinitionManager>.Instance;
                if (!defs.TryGetDefinition(ControlSeatSeat, out SeatComponentDefinition seat) || !defs.TryGetDefinition(ControlSeatModel, out ModelComponentDefinition controlModel)) { State = "control seat definitions not loaded"; return; }
                var seatDummy = controlModel.Dummies.FirstOrDefault(d => d.Type?.Guid == SeatDummyType);
                var enterDummy = controlModel.Dummies.FirstOrDefault(d => d.Type?.Guid == EnterDummyType);
                if (seatDummy == null || enterDummy == null) { Fail("control seat dummies missing"); return; }
                var done = new List<string>();
                int changed = 0;
                foreach (var block in Blocks)
                {
                    string? problem = Patch(defs, block, seat, seatDummy, enterDummy, ref changed);
                    if (problem != null) GameLog.Instance.Warn($"passenger seats: {block.Name} left as it is: {problem}");
                    else done.Add(block.Name);
                }
                foreach (var prefab in defs.GetDefinitionsOfTypeAndDescendants<PrefabDefinition>()) changed += AddSeatBuilders(PrefabEntity(prefab));
                State = done.Count == 0 ? "none applied" : string.Join(", ", done);
                if (changed > 0) GameLog.Instance.Info($"passenger seats: {State} can be sat in ({changed} definitions and prefab entities given a seat)");
            }
            catch (Exception ex) { Fail(ex.ToString()); }
        }
    }

    private static void Fail(string why)
    {
        State = "failed: " + why;
        GameLog.Instance.Warn("passenger seats: " + why);
    }

    private static string? Patch(DefinitionManager defs, Block block, SeatComponentDefinition seat, ModelDummy seatDummy, ModelDummy enterDummy, ref int changed)
    {
        if (!defs.TryGetDefinition(block.Composite, out EntityCompositeDefinition server) || !defs.TryGetDefinition(block.ClientComposite, out EntityCompositeDefinition client)) return "composites missing";
        if (!defs.TryGetDefinition(block.Model, out ModelComponentDefinition model)) return "model missing";
        var composites = new[] { server, client };
        if (!composites.All(c => c.Types.Any(t => typeof(FunctionalBlockComponent).IsAssignableFrom(t)))) return "not a functional block";
        if (model.Collider == null) return "model has no collider";

        if (model.TryGetDummy(seatDummy.Type) == null)
        {
            var box = model.Collider.GetBoundingBox(Quaternion.Identity);
            var sit = seatDummy.DeepClone();
            sit.Name = "Seat";
            // A seated character faces -Z; Yaw turns it to face the way the model's seat faces.
            var facing = Quaternion.CreateFromAxisAngle(Vector3.Up, block.Yaw * (float)Math.PI / 180f);
            var back = Vector3.Transform(Vector3.Backward, facing);
            sit.Transform = new RelativeTransformWithEulerHint(new Vector3(box.Center.X, box.Min.Y + block.SeatHeight, box.Center.Z) + back * block.SeatBack, facing);
            var enter = enterDummy.DeepClone();
            enter.Name = "Enter";
            enter.Transform = new RelativeTransformWithEulerHint(box.Center, Quaternion.Identity);
            enter.Scale = box.HalfExtents * 2f + new Vector3(0.1f);
            var dummies = model.Dummies.Add(sit).Add(enter);
            var byType = new ListDictionary<DummyTypeDefinition, ModelDummy>();
            foreach (var dummy in dummies) byType.Add(dummy.Type, dummy);
            ListDictionaryReader<DummyTypeDefinition, ModelDummy> reader = byType;
            AccessTools.PropertySetter(typeof(ModelComponentDefinition), nameof(ModelComponentDefinition.Dummies)).Invoke(model, new object[] { dummies });
            AccessTools.PropertySetter(typeof(ModelComponentDefinition), nameof(ModelComponentDefinition.DummiesByType)).Invoke(model, new object[] { reader });
            changed++;
        }

        defs.TryGetDefinition(ControlSeatClientSeat, out ModelChangingSeatComponentDefinition clientSeat);
        foreach (var composite in composites)
        {
            if (composite.Types.Any(t => typeof(SeatComponent).IsAssignableFrom(t))) continue;
            // Clients get the control seat's own client seat so the seated character looks the same as in one.
            bool hides = composite == client && clientSeat != null;
            int n = composite.ComponentCount;
            var builder = new EntityCompositeDefinition.Builder(n + 1);
            for (int i = 0; i < n; i++)
            {
                builder.Ids[i] = composite.Ids[i];
                builder.Types[i] = composite.Types[i];
                builder.Definitions[i] = composite.Definitions[i];
                builder.Tags[i] = composite.Tags[i];
            }
            // Last is a valid dependency order: the seat needs the block's other components and nothing there needs it.
            // The default tag is what TryGet<SeatComponent> and the server-to-client component matching look up.
            builder.Ids[n] = SeatSlot;
            builder.Types[n] = hides ? typeof(ModelChangingSeatComponent) : typeof(SeatComponent);
            builder.Definitions[n] = hides ? clientSeat : seat;
            builder.Tags[n] = ImmutableArray.Create(DefaultTag.Get(builder.Types[n]));
            AccessTools.Method(typeof(EntityCompositeDefinition), "Assign").Invoke(composite, new object[] { builder });
            changed++;
        }
        return null;
    }

    // Placed blocks, blueprints and the save loader's migration of older saves all start from the prefab's object
    // builders, which have to line up with the composite's components.
    private static int AddSeatBuilders(EntityObjectBuilder? entity)
    {
        if (entity?.Definition == null) return 0;
        int added = 0;
        int slot = entity.Definition.Ids.IndexOf(SeatSlot);
        if (Composites.Contains(entity.Definition.Guid) && slot >= 0 && entity.ObjectBuilders.Keys.IndexOf(SeatSlot) < 0)
        {
            entity.ObjectBuilders.Insert(Math.Min(slot, entity.ObjectBuilders.Count), SeatSlot, new SeatObjectBuilder());
            added++;
        }
        foreach (var hierarchy in entity.OBsOfType<HierarchyComponentObjectBuilder>())
            if (hierarchy.Children != null)
                foreach (var child in hierarchy.Children) added += AddSeatBuilders(child);
        return added;
    }

    /// <summary>Plugin init, every mode: re-applies whenever a definition set is pushed.</summary>
    internal static void Hook()
    {
        try
        {
            var defs = Singleton<DefinitionManager>.Instance;
            if (defs == null) { GameLog.Instance.Warn("passenger seats: no definition manager yet; seats are set up at world load only"); return; }
            defs.DefinitionSetPushed += _ => Apply();
        }
        catch (Exception ex) { GameLog.Instance.Warn("passenger seats: " + ex.Message); }
    }

    internal static void Install(Harmony harmony)
    {
        harmony.Patch(AccessTools.PropertyGetter(typeof(UsePilotableAction), nameof(UsePilotableAction.CanActivate)), prefix: new HarmonyMethod(typeof(PassengerSeats), nameof(CanActivatePrefix)));
        harmony.Patch(AccessTools.Method(typeof(UsePilotableAction), nameof(UsePilotableAction.InteractPrimaryStart)), prefix: new HarmonyMethod(typeof(PassengerSeats), nameof(InteractPrefix)));
    }

    private static SeatComponent? PassengerSeat(UseAction action) =>
        action.Entity.TryGet(StringId.Get(CockpitControllableComponent.COCKPIT_TAG)) == null && IsPassengerSeat(action.Entity) ? action.Entity.TryGet<SeatComponent>() : null;

    private static bool CanActivatePrefix(UsePilotableAction __instance, ref bool __result)
    {
        var seat = PassengerSeat(__instance);
        if (seat == null) return true;
        __result = __instance._canActivate && seat.IsFunctional && seat.Pilot == null;
        return false;
    }

    private static bool InteractPrefix(UsePilotableAction __instance)
    {
        var seat = PassengerSeat(__instance);
        if (seat == null) return true;
        __instance.InvokeDummyInteractedAction();
        if (!_sitting && __instance.Interactor.AsInterface<ISeatable>()?.CurrentSeat == null && seat.CanSeat(__instance.Interactor)) SitAsync(seat, __instance.Interactor);
        return false;
    }

    private static async void SitAsync(SeatComponent seat, Entity pilot)
    {
        _sitting = true;
        try
        {
            if (!await seat.TrySetPilotAsync(pilot)) GameLog.Instance.Info($"passenger seats: could not sit in {seat.Entity}");
        }
        catch (Exception ex) { GameLog.Instance.Warn("passenger seats: sitting failed: " + ex.Message); }
        finally { _sitting = false; }
    }

    private static InputContext? _context;
    private static IInputProcessor? _processor;
    private static IDisposable? _hints;
    private static InteractorEntityComponent? _interactor;
    private static SeatComponent? _seat;
    private static bool _failed;

    /// <summary>Client tick: while the local player sits in a passenger seat, Use stands them up.</summary>
    internal static void Tick()
    {
        if (_failed) return;
        try
        {
            var session = MpPlugin.App?.ClientSession;
            try { if (!_keepFailed) KeepSeated(session); }
            catch (Exception ex) { _keepFailed = true; GameLog.Instance.Warn("passenger seats: keeping seated characters seated failed: " + ex); }
            var interactor = session?.GetEntitiesOfType<InteractorEntityComponent>().Select(e => e.Get<InteractorEntityComponent>()).FirstOrDefault(i => i.IsControlledByLocalPlayer);
            var seat = interactor?.Entity.AsInterface<ISeatable>()?.CurrentSeat as SeatComponent;
            if (seat == null || !IsPassengerSeat(seat.Entity) || session == null) { Clear(); return; }
            // Use belongs to standing up while seated; a block in view keeps its action paused.
            if (interactor!._actionInputContext is { } blockContext && blockContext != _context) (_processor ?? interactor._inputProcessor)?.DeactivateContext(blockContext);
            if (_seat == seat && _context != null) return;
            Clear();
            if (!Singleton<DefinitionManager>.Instance.TryGetDefinition(UseActionId, out UseActionDefinition use)) throw new InvalidOperationException("SE2's generic use action definition is missing");
            _seat = seat;
            _interactor = interactor;
            _processor = interactor._inputProcessor ?? Singleton<VRageCore>.Instance.Engine.Single<IInputProcessor>();
            _context = new InputContext(use.InputContextDefinition);
            _context.SetHandler<bool>(use.InteractPrimary, (pressed, _) => { if (pressed) Stand(); }, ControlActivationFlags.Start);
            _processor?.ActivateContext(_context);
            _hints = session.SessionComponents.TryGet<ControlHintsSessionComponent>()?.Attach(_context, OnlineUi.Raw(seat.Entity.TryGet<FunctionalBlockComponent>()?.DisplayName.EvaluateText() ?? "Seat"),
                new List<ActionHint> { new BoundActionHint(use.InteractPrimary, OnlineUi.Raw("Stand up")) });
        }
        catch (Exception ex)
        {
            _failed = true;
            Clear();
            GameLog.Instance.Warn("passenger seats: standing up with Use is off for this session: " + ex);
        }
    }

    private static long _nextSeatedCheckMs;
    private static bool _keepFailed;

    /// <summary>
    /// SE2 switches a character to its Sitting movement state only on the server, and a character's saved or replicated
    /// state is the one from before it sat down. A client that receives an already seated character (joining, rejoining,
    /// streaming the grid in) therefore shows it with its jetpack running. Passengers get Sitting locally here; standing
    /// up replicates the server's state change back.
    /// </summary>
    private static void KeepSeated(Keen.VRage.Core.Game.Systems.Session? session)
    {
        long now = MpClock.NowMs();
        if (session == null || now < _nextSeatedCheckMs) return;
        _nextSeatedCheckMs = now + 250;
        foreach (var e in session.GetEntitiesOfType<SeatComponent>())
        {
            if (!IsPassengerSeat(e) || e.Get<SeatComponent>().Pilot?.TryGet<CharacterMovementComponent>() is not { } movement) continue;
            if (movement.CurrentMovementState == CharacterMovementState.Sitting) continue;
            // Clients may not set the property; write it the way a replicated change lands: the field, then every
            // handler after the first one (that one is the setter, a throwing stub on clients).
            var sitting = CharacterMovementState.Sitting;
            movement.__SetCurrentMovementState(in sitting);
            var pilot = movement.Entity;
            ref var signals = ref pilot.CompositionData.Signals;
            var (begin, end) = signals.SignalIndex[movement.__signalTableOffset];
            if (end - begin > 1) CharacterMovementComponent.__InternalCurrentMovementStateProperty.Trigger(pilot, signals.SignalHandlers.AsSpan().Slice(begin + 1, end - begin - 1), in sitting);
        }
    }

    // Leaves the passenger seat the local player sits in, if any.
    private static void Stand()
    {
        var seat = _seat;
        if (seat?.Pilot == null) return;
        seat.TryClearPilotAsync().SkipWait();
    }

    private static void Clear()
    {
        if (_context == null && _seat == null) return;
        _hints?.Dispose();
        _hints = null;
        if (_context != null) _processor?.DeactivateContext(_context);
        if (_interactor?._actionInputContext is { } blockContext) _processor?.ActivateContext(blockContext);
        _context = null;
        _processor = null;
        _interactor = null;
        _seat = null;
    }

    /// <summary>Diagnostics: every passenger seat in a session with its occupant.</summary>
    internal static string Describe(Keen.VRage.Core.Game.Systems.Session session)
    {
        var rows = new List<string>();
        foreach (var e in session.GetEntitiesOfType<SeatComponent>())
        {
            if (!IsPassengerSeat(e)) continue;
            var s = e.Get<SeatComponent>();
            rows.Add($"{e} functional {s.IsFunctional} pilot {(s.Pilot == null ? "none" : s.Pilot + " " + s.Pilot.TryGet<CharacterMovementComponent>()?.CurrentMovementState)}");
        }
        int blocks = 0, withSeat = 0;
        foreach (var e in session.Entities)
            if (e.GetComposition() is { } c && Composites.Contains(c.Guid))
            {
                blocks++;
                if (e.TryGet<SeatComponent>() != null) withSeat++;
                else if (blocks == 1)
                {
                    Singleton<DefinitionManager>.Instance.TryGetDefinition(c.Guid, out EntityCompositeDefinition registered);
                    rows.Add($"first {e}: composite {c.ComponentCount} types (seat {c.Types.Contains(typeof(SeatComponent))}), registered same {ReferenceEquals(c, registered)}, entity components {e.Components.Length}: {string.Join(",", e.Components.Select(x => x?.GetType().Name))}");
                }
            }
        return State + $" ({blocks} seat blocks, {withSeat} with a seat): " + (rows.Count == 0 ? "no passenger seats" : string.Join("; ", rows));
    }

    // Rig: which block, where the pilot sits relative to it, and the dummies the seat aligns.
    private static string SeatGeometry(SeatComponent seat, Entity pilot)
    {
        var blockAt = seat.Entity.Data.GetWorldTransform();
        var local = WorldTransform.GetRelativeTransform(pilot.Data.GetWorldTransform(), blockAt);
        var model = seat.Entity.TryGet<ModelComponent>()?.Definition;
        var pilotDummy = pilot.TryGet<ModelComponent>()?.Definition?.TryGetDummy(seat.Definition.PilotDummy);
        return FormattableString.Invariant($"[{seat.Entity.GetComposition()?.DebugNameShort}] movement {pilot.TryGet<CharacterMovementComponent>()?.CurrentMovementState} pilot in block space {local.Position}; seat dummy {model?.TryGetDummy(seat.Definition.SeatDummy)?.Transform.Transform.Position}; pilot dummy on character {(pilotDummy == null ? "none" : pilotDummy.Transform.Transform.Position.ToString())}");
    }

    /// <summary>Rig (client): "sit" takes the nearest free passenger seat through SE2's seat story, "stand" leaves it,
    /// anything else lists the seats and where the local character is relative to its seat.</summary>
    internal static string Rig(string args)
    {
        var session = MpPlugin.App?.ClientSession;
        var me = session?.GetEntitiesOfType<InteractorEntityComponent>().Select(e => e.Get<InteractorEntityComponent>()).FirstOrDefault(i => i.IsControlledByLocalPlayer)?.Entity;
        if (session == null || me == null) return "no local character";
        var at = me.Data.GetWorldTransform().Position;
        var current = me.AsInterface<ISeatable>()?.CurrentSeat as SeatComponent;
        var words = args.Trim().Split(' ', 2);
        switch (words[0])
        {
            case "sit":
                if (current != null) return "already seated";
                var seat = session.GetEntitiesOfType<SeatComponent>().Where(e => words.Length < 2 ? IsPassengerSeat(e) : (e.GetComposition()?.DebugNameShort ?? "").StartsWith(words[1], StringComparison.OrdinalIgnoreCase)).Select(e => e.Get<SeatComponent>()).Where(s => s.CanSeat(me))
                    .OrderBy(s => Vector3D.Distance(s.Entity.Data.GetWorldTransform().Position, at)).FirstOrDefault();
                if (seat == null) return "no free passenger seat";
                SitAsync(seat, me);
                return $"sitting in {seat.Entity} {Vector3D.Distance(seat.Entity.Data.GetWorldTransform().Position, at):0.0} m away";
            case "cam":
            {
                // cam [right up forward]: this client's free spectator camera at that offset (in the seated character's
                // frame) from the first other seated character, looking at its chest
                var other = session.GetEntitiesOfType<SeatComponent>().Select(e => e.Get<SeatComponent>()).Where(s => s.Pilot != null && s.Pilot != me).OrderBy(s => IsPassengerSeat(s.Entity) ? 0 : 1).FirstOrDefault();
                if (other == null) return "no seated character";
                var o = words.Length > 1 ? words[1].Split(' ').Select(x => double.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray() : new[] { 0.0, 0.6, 1.8 };
                var p = other.Pilot!.Data.GetWorldTransform();
                Vector3D right = (Vector3D)p.Orientation.GetRight(), up = (Vector3D)p.Orientation.GetUp(), forward = (Vector3D)p.Orientation.GetForward();
                var eye = p.Position + right * o[0] + up * o[1] + forward * o[2];
                var look = Vector3D.Normalize(p.Position + up * 0.6 - eye);
                var side = Vector3D.Normalize(Vector3D.Cross(look, up));
                session.SessionComponents.TryGet<Keen.Game2.Client.WorldObjects.Spectators.SpectatingModesSessionComponent>()?.SwitchToSpectator(new WorldTransform(eye, Quaternion.CreateFromForwardUp((Vector3)look, (Vector3)Vector3D.Cross(side, look)))).SkipWait();
                return $"camera on {other.Entity.GetComposition()?.DebugNameShort}";
            }
            case "stand":
                if (current == null) return "not seated";
                current.TryClearPilotAsync().SkipWait();
                return "standing up from " + current.Entity;
            default:
                string where = current == null ? "standing" : FormattableString.Invariant($"seated in {current.Entity} (passenger {IsPassengerSeat(current.Entity)}), {Vector3D.Distance(current.Entity.Data.GetWorldTransform().Position, at):0.000} m from it, parented {me.Data.Has<EntityParentData>()}, use-to-stand {(_context != null)}") + (current == null ? "" : " " + SeatGeometry(current, me));
                return FormattableString.Invariant($"me at {at.X:0.00},{at.Y:0.00},{at.Z:0.00} {where} | {Describe(session)}");
        }
    }
}
