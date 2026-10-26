using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Keen.Game2.Simulation.WorldObjects.Characters;
using Keen.Game2.Simulation.WorldObjects.Characters.AnimationsBlackboard;
using Keen.Game2.Simulation.WorldObjects.Movement;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.GameSystems.Animations;
using Keen.VRage.Core.Game.GameSystems.Gravity;
using Keen.VRage.DCS.Components;
using Keen.VRage.Game.Components.Animations;
using Keen.VRage.Library.Definitions;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Library.Utils;
using Keen.VRage.Physics.Character;
using Keen.VRage.Core.Game.Data;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;
using Y51_SE2Online.SyncEngine;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>
/// Animates a player's own predicted character (<see cref="OwnMotion"/>) from this game's own simulation. SE2 4.0 works
/// out a character's locomotion animation (ground speed and direction, on ground / jumping / falling / floating,
/// landing, walk/run/sprint intent, turning, crouch, jetpack, slope) on the server
/// (CharacterAnimationBlackboardServerComponent) and sends it back, so a predicted body jumped, landed and turned a
/// round trip before its animation did. Right before this character's animation update, the same values are set from
/// its predicted state with the server component's own rules; the server's values for those keys are kept aside, and
/// put back when prediction stops (a seat, death, a fallback to server movement). Everything else (tools, weapons,
/// damage, building, seats, look) still comes from the server. -y51online:ownanim:off leaves it all to the server.
/// </summary>
internal static class OwnAnimation
{
	private static readonly Guid DefinitionId = new("1981b639-34c7-41dc-918c-c909042a5e4e");

	private static readonly BlackboardConstants.FloatKeys F = ImmutableSingleton<BlackboardConstants.FloatKeys>.Instance;
	private static readonly BlackboardConstants.BoolKeys B = ImmutableSingleton<BlackboardConstants.BoolKeys>.Instance;
	private static readonly BlackboardConstants.StringKeys S = ImmutableSingleton<BlackboardConstants.StringKeys>.Instance;
	private static readonly BlackboardConstants.CharacterStates States = ImmutableSingleton<BlackboardConstants.CharacterStates>.Instance;
	private static readonly BlackboardConstants.GroundMovementIntents Intents = ImmutableSingleton<BlackboardConstants.GroundMovementIntents>.Instance;

	/// <summary>The keys this game sets for its own predicted character.</summary>
	internal static readonly HashSet<string> Driven = new()
	{
		F.VelX_driver, F.VelY_driver, F.GroundVelocityTotal, F.LandingVelocity, F.MoveToIdleRecordedInput_X, F.MoveToIdleRecordedInput_Y,
		F.AngularVelX_driver, F.AngularVelY_driver, F.AngularVelZ_driver, F.SurfaceSlopeForward, F.SurfaceSlopeRight,
		S.CharacterState, S.GroundMovementIntent, B.Crouching, B.Jetpack,
	};

	private static bool _on;
	private sealed record Tuning(float MaxTurnX, float MaxTurnY, float MaxTurnZ, int AverageSamples, float SpeedThreshold);

	// Content\Characters\System\PrefabCharacterClient_CharacterAnimationBlackboardServerDefinition.def, when the
	// definition itself is not found.
	private static readonly Tuning Fallback = new(0.78f, 1.57f, 1.57f, 2, 0.05f);

	private static Tuning? _tuning;

	// Runs on the client scene's jobs, one frame after another; the lock covers the hand-over between them.
	private static readonly object Sync = new();
	private static Entity? _entity;
	private static readonly Dictionary<string, BlackboardValue> ServerValues = new();
	private static CharacterState _lastState;
	private static Vector3 _lastGroundVelocity;
	private static float _averageSpeedSquared;
	private static bool _wasIdle = true;
	private static Vector2 _lastGroundMovement;

	// -y51online:landdebounce:off: every flicker of the predicted body's ground contact reaches the animation (Sync.FallDebounce).
	private static readonly bool DebounceOff = MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "landdebounce:off");
	private static readonly FallDebounce Fall = new();
	private static long _heldTicks, _heldLogged, _heldLogMs;

	public static void Install(Harmony harmony)
	{
		if (MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "ownanim:off"))
		{
			GameLog.Instance.Info("own animation: a predicted character's animation comes from the server (-y51online:ownanim:off)");
			return;
		}
		harmony.Patch(AccessTools.Method(typeof(Keen.VRage.Animation.Client.GameObjects.MultiStrategyAnimationControllerComponent), "UpdateAnimationController"),
			prefix: new HarmonyMethod(typeof(OwnAnimation), nameof(UpdatePrefix)));
		_on = true;
	}

	/// <summary>The own predicted character whose server values for <see cref="Driven"/> keys are set aside, if any.</summary>
	internal static Entity? Animating => _on ? OwnMotion.Driving : null;

	private static bool _drivenThisFrame;

	private static void UpdatePrefix(Component __instance)
	{
		if (_on && OwnMotion.Driving is { } own && ReferenceEquals(__instance.Entity, own)) Update(own);
	}

	/// <summary>Client session thread, after each update: a character whose animation job did not run this frame (its
	/// animations are off in first person) still gets its values, so they are right when the view changes.</summary>
	public static void ClientTick()
	{
		if (_on && !_drivenThisFrame && OwnMotion.Driving is { } own) Update(own);
		_drivenThisFrame = false;
	}

	private static void Update(Entity own)
	{
		// Definitions load with the world, after the plugin.
		if (_tuning == null)
		{
			var manager = Singleton<DefinitionManager>.Instance;
			var def = manager.TryGetDefinition(DefinitionId, out CharacterAnimationBlackboardServerDefinition found) ? found : manager.GetDefinitionsOfType<CharacterAnimationBlackboardServerDefinition>().FirstOrDefault();
			_tuning = def == null ? Fallback : new Tuning(def.MaxAngularVelocityX, def.MaxAngularVelocityY, def.MaxAngularVelocityZ, def.GroundVelocityAverageSamples, def.GroundVelocityThreshold);
			GameLog.Instance.Info($"own animation: driving this player's character ({(def == null ? "built-in tuning, the definition is not loaded here" : "the game's tuning")}; physics state {own.Data.Has<CharacterPhysicsState>()}, controls {own.Data.Has<ControlData>()})");
		}
		if (own.TryGet<AnimationBlackboardComponent>() is not { Blackboard: { } blackboard }) return;
		lock (Sync)
		{
			if (!ReferenceEquals(_entity, own)) { _entity = own; _lastState = (CharacterState)(-1); _averageSpeedSquared = 0; _wasIdle = true; _lastGroundVelocity = default; Fall.Reset(); }
			Drive(own, blackboard, _tuning);
			_drivenThisFrame = true;
		}
	}

	/// <summary>The keys of a server change for <paramref name="own"/> this game does not set itself; the rest is kept for later.</summary>
	internal static (T Key, TV Value)[] Keep<T, TV>((T Key, TV Value)[] changes, Func<T, string> name, Func<TV, BlackboardValue> boxed)
	{
		lock (Sync)
		{
			var kept = new List<(T, TV)>(changes.Length);
			foreach (var change in changes)
			{
				string key = name(change.Key);
				if (Driven.Contains(key)) ServerValues[key] = boxed(change.Value);
				else kept.Add(change);
			}
			return kept.ToArray();
		}
	}

	/// <summary>Inbound replication job: prediction of the character stopped, so the server's own values go back in.</summary>
	internal static void Restore()
	{
		lock (Sync)
		{
			if (_entity == null || ReferenceEquals(_entity, OwnMotion.Driving)) return;
			var blackboard = EngineCodec.IsDead(_entity) ? null : _entity.TryGet<AnimationBlackboardComponent>()?.Blackboard;
			if (blackboard != null)
				foreach (var (key, value) in ServerValues)
					switch (value.Type)
					{
					case AnimatedValueType.Float: blackboard.Set(key, value.GetValue<float>()); break;
					case AnimatedValueType.Int: blackboard.Set(key, value.GetValue<int>()); break;
					case AnimatedValueType.Bool: blackboard.Set(key, value.GetValue<bool>()); break;
					case AnimatedValueType.String: blackboard.Set(key, value.GetValue<StringId>()); break;
					}
			ServerValues.Clear();
			_entity = null;
		}
	}

	private static void DriveControls(Entity e, Blackboard bb, Tuning def)
	{
		var data = e.Data;
		data.TryGet<ControlData>(out var control);
		var turn = control.Rotation * 60f;
		bb.Set<float>(F.AngularVelX_driver, Math.Clamp(turn.X / def.MaxTurnX, -1f, 1f));
		bb.Set<float>(F.AngularVelY_driver, Math.Clamp(turn.Y / def.MaxTurnY, -1f, 1f));
		bb.Set<float>(F.AngularVelZ_driver, Math.Clamp(turn.Z / def.MaxTurnZ, -1f, 1f));
		bb.Set<bool>(B.Crouching, data.Has<CharacterCrouchTag>());
		bb.Set<bool>(B.Jetpack, e.TryGet<CharacterMovementComponent>()?.CurrentMovementState == CharacterMovementState.Jetpack);
		if (data.TryGet<CharacterSupportData>(out var support) && Vector3.IsUnit(support.SupportingSurface.SurfaceNormal))
		{
			var wt = e.Data.GetWorldTransform();
			var n = Vector3.Transform(support.SupportingSurface.SurfaceNormal, Quaternion.Conjugate(wt.Orientation));
			bb.Set<float>(F.SurfaceSlopeForward, Math.Clamp(MathF.Atan2(n.Z, n.Y) / (MathF.PI / 2f), -1f, 1f));
			bb.Set<float>(F.SurfaceSlopeRight, Math.Clamp(MathF.Atan2(-n.X, n.Y) / (MathF.PI / 2f), -1f, 1f));
		}
	}

	// CharacterAnimationBlackboardServerComponent's UpdateState, BlackboardGroundMovementIntentUpdate,
	// BlackboardAngularVelocityUpdate, BlackboardCrouchingUpdate, BlackboardJetpackUpdate and
	// BlackboardCharacterSupportUpdate, on this game's predicted state.
	private static void Drive(Entity e, Blackboard bb, Tuning def)
	{
		var data = e.Data;
		DriveControls(e, bb, def);
		if (!data.TryGet<CharacterPhysicsState>(out var physics)) return;
		var state = physics;
		// A tick or two without ground right after a voxel cut is not a fall (and its landing would shake the view).
		// Not a jetpack lift-off (that is a real leaving of the ground). While held the walk blend keeps the last ground speed.
		bool jetpack = e.TryGet<CharacterMovementComponent>()?.CurrentMovementState == CharacterMovementState.Jetpack;
		if (!DebounceOff && !jetpack && Fall.Holds(_lastState == CharacterState.OnGround, state.State == CharacterState.Falling, data.Has<GravityEffectData>()))
		{
			state.State = CharacterState.OnGround;
			state.GroundVelocity = _lastGroundVelocity;
			_heldTicks++;
		}
		long now = MpClock.NowMs();
		if (_heldTicks > _heldLogged && now - _heldLogMs >= 60000)
		{
			_heldLogMs = now;
			GameLog.Instance.Info($"own animation: {_heldTicks - _heldLogged} ticks without ground under {FallDebounce.Ticks} in a row not shown as falls (voxel cuts and similar) in the last minute");
			_heldLogged = _heldTicks;
		}
		float baseSpeed = e.TryGet<CharacterPhysicsEntityComponent>()?.Definition?.BaseGroundVelocity ?? 1f;
		float speed = state.GroundVelocity.Length();
		float ratio = Math.Clamp(speed / baseSpeed, 0f, 1f);
		Vector3 scaled = speed > 0 ? state.GroundVelocity / speed * ratio : Vector3.Zero;
		var planar = new Vector2(scaled.X, -scaled.Z);
		_averageSpeedSquared = (_averageSpeedSquared * (def.AverageSamples - 1) + planar.LengthSquared()) / def.AverageSamples;
		if (_averageSpeedSquared < def.SpeedThreshold * def.SpeedThreshold) planar = Vector2.Zero;
		float stretch = 0;
		if (!Vector2.IsZero(planar, 1E-05f))
		{
			var dir = Vector2.Normalize(planar);
			stretch = Math.Abs(dir.X) >= 0.70710677f ? 1f / Math.Abs(dir.X) : 1f / Math.Abs(dir.Y);
		}
		bb.Set<float>(F.VelY_driver, planar.Y * stretch);
		bb.Set<float>(F.VelX_driver, planar.X * stretch);
		bb.Set<float>(F.GroundVelocityTotal, planar.Length());
		if (state.State != _lastState)
		{
			switch (state.State)
			{
			case CharacterState.Jumping: bb.Set<StringId>(S.CharacterState, States.Jumping); break;
			case CharacterState.Falling:
				if (!data.Has<GravityEffectData>()) bb.Set<StringId>(S.CharacterState, States.Floating);
				else if (_lastState != CharacterState.Jumping) bb.Set<StringId>(S.CharacterState, States.Falling);
				break;
			case CharacterState.OnGround:
				bb.Set<StringId>(S.CharacterState, States.OnGround);
				if (_lastState is CharacterState.Jumping or CharacterState.Sliding or CharacterState.Falling or CharacterState.Floating)
					bb.Set<float>(F.LandingVelocity, Math.Abs(_lastGroundVelocity != Vector3.Zero ? _lastGroundVelocity.Length() : speed));
				break;
			case CharacterState.Floating: bb.Set<StringId>(S.CharacterState, States.Floating); break;
			}
		}
		_lastState = state.State;
		_lastGroundVelocity = state.GroundVelocity;

		data.TryGet<ControlData>(out var control);
		data.TryGet<AdvancedCharacterControlData>(out var advanced);
		if (state.State != CharacterState.OnGround) bb.Set<StringId>(S.GroundMovementIntent, Intents.None);
		else
		{
			var move = new Vector2(control.Movement.X, -control.Movement.Z);
			GroundGait? gait = data.TryGet<GroundGaitIntentData>(out var g) ? g.Gait : null;
			var intent = CharacterAnimationBlackboardServerComponent.ResolveGroundMovementIntent(move, advanced.IsSprinting, gait);
			bb.Set<StringId>(S.GroundMovementIntent, intent);
			bool idle = intent == Intents.Idle;
			if (idle && !_wasIdle)
			{
				bb.Set<float>(F.MoveToIdleRecordedInput_X, _lastGroundMovement.X);
				bb.Set<float>(F.MoveToIdleRecordedInput_Y, _lastGroundMovement.Y);
			}
			if (!idle) _lastGroundMovement = move;
			_wasIdle = idle;
		}
	}
}
