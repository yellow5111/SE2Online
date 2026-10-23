using System;
using System.Linq;
using HarmonyLib;
using Keen.Game2.Client.WorldObjects.Character;
using Keen.Game2.Simulation.WorldObjects.Characters;
using Keen.Game2.Simulation.WorldObjects.Movement;
using Keen.Game2.Simulation.WorldObjects.Shared.Movement;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.Core.Game.GameSystems.Gravity;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Definitions;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Library.Utils;
using Keen.VRage.Physics.Character;
using Keen.VRage.Physics.Data;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>
/// The discrete half of a predicted own character (<see cref="OwnMotion"/>): jetpack on/off, crouch, dampeners and the
/// jetpack boost. SE2 4.0 decides all of them on the server (Keen's toggle requests, the server-only
/// JetpackMovementComponent and ServerJetpackBoostComponent) and replicates the result, so a predicted body took off,
/// crouched, braked or boosted a round trip late and the server's movement ran ahead of it. Now the player's game applies
/// a toggle at once with the server's own rules and sends it inside the next tick's input; the server applies it on that
/// same input (Keen's own request from that player is ignored while their inputs carry toggles) and reports the state
/// after each input with the ack (<see cref="OwnState"/>). When the two keep disagreeing (no fuel, no room to stand up)
/// the player's game takes the server's state (<see cref="OwnStateCheck"/>). In jetpack mode the player's game also
/// keeps the thrust, torque, speed limit and movement tuning the server-only jetpack components would set, and runs
/// the boost charge and impulse itself. Only with servers that answer with states (0.16.4 on);
/// -y51online:owntoggles:off leaves it all to the server.
/// </summary>
internal static class OwnStatePrediction
{
	internal static readonly bool Off = MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "owntoggles:off");

	private static readonly Guid JetpackId = new("76487d13-1aba-4689-9ba1-fc57c49fbb40"), BoostId = new("4325391d-2dcb-4067-8981-4010365011bf");

	private static readonly System.Reflection.MethodInfo DampenerToggle = AccessTools.Method(typeof(CharacterMovementComponent), "RequestDampenerToggleInternal");

	public static void Install(Harmony harmony)
	{
		var control = typeof(CharacterMovementControlComponent);
		harmony.Patch(AccessTools.Method(control, nameof(CharacterMovementControlComponent.RequestStateToggle)), postfix: Hook(nameof(JetpackPostfix)));
		harmony.Patch(AccessTools.Method(control, nameof(CharacterMovementControlComponent.RequestCrouchToggle)), postfix: Hook(nameof(CrouchPostfix)));
		harmony.Patch(AccessTools.Method(control, nameof(CharacterMovementControlComponent.RequestDampenerToggle)), postfix: Hook(nameof(DampenerPostfix)));
		harmony.Patch(AccessTools.Method(typeof(CharacterMovementComponent), nameof(CharacterMovementComponent.RequestStateToggleInternal)), prefix: Hook(nameof(KeenTogglePrefix)));
		harmony.Patch(DampenerToggle, prefix: Hook(nameof(KeenTogglePrefix)));
		harmony.Patch(AccessTools.Method(typeof(CharacterMovementServerComponent), nameof(CharacterMovementServerComponent.RequestCrouchToggleInternal)), prefix: Hook(nameof(KeenTogglePrefix)));
		// The server's own calls of the jetpack toggle (leaving gravity, death) are not a player's request.
		foreach (var own in new[] { AccessTools.Method(typeof(CharacterMovementServerComponent), "OnExitGravityAndSurface"), AccessTools.Method(typeof(CharacterDeathServerComponent), "OnCharacterDeathSignal") })
			harmony.Patch(own, prefix: Hook(nameof(InternalEnter)), finalizer: Hook(nameof(InternalLeave)));
	}

	private static HarmonyMethod Hook(string name) => new(typeof(OwnStatePrediction), name);

	// --- player's game ---

	private static readonly object Requests = new();
	private static OwnToggles _requested, _carried;
	private static Entity? _for;
	private static bool _hadGravity;
	private static CharacterBoostState _boost;
	private static bool _boostRefused;
	private static int _chargeTicks;
	private static bool _impulse;
	private static long _predicted, _taken;

	private static void JetpackPostfix(CharacterMovementControlComponent __instance) => Request(__instance.Entity, OwnToggles.Jetpack);
	private static void CrouchPostfix(CharacterMovementControlComponent __instance) => Request(__instance.Entity, OwnToggles.Crouch);
	private static void DampenerPostfix(CharacterMovementControlComponent __instance) => Request(__instance.Entity, OwnToggles.Dampeners);

	// Keen's request still goes to the server too: a server that does not take toggles from inputs uses it. One that
	// does ignores it once it has seen this game's toggle flag, so the toggle rides in the inputs even before this game
	// knows the server answers with states (until then it is not applied here, the server's own replication shows it).
	private static void Request(Entity entity, OwnToggles toggle)
	{
		if (Off || OwnMotion.Driving is not { } own || !ReferenceEquals(entity, own)) return;
		lock (Requests) _requested ^= toggle;
	}

	/// <summary>The toggles applied after the previous tick, for this tick's input (they ran in this tick on this game).</summary>
	internal static OwnToggles TakeForInput()
	{
		var t = _carried;
		_carried = OwnToggles.None;
		return t;
	}

	internal static void Reset()
	{
		lock (Requests) _requested = OwnToggles.None;
		_carried = OwnToggles.None;
		_for = null;
		_boost = CharacterBoostState.None;
		_impulse = false;
		_boostRefused = false;
		Ledger.Reset();
	}

	/// <summary>
	/// Client session thread after an update, once that tick's input is sent: applies the toggles requested during it
	/// (they ride in the next input, so both sides switch before the same tick) and the server-only rules for the next tick.
	/// </summary>
	internal static void Tick(Entity own, bool predict)
	{
		if (!ReferenceEquals(own, _for))
		{
			Reset();
			_for = own;
			_hadGravity = own.Data.Has<GravityEffectData>();
		}
		OwnToggles requested;
		lock (Requests)
		{
			requested = _requested;
			_requested = OwnToggles.None;
		}
		_carried ^= requested;
		if (!predict) return;
		if ((requested & OwnToggles.Jetpack) != 0) ToggleJetpack(own);
		if ((requested & OwnToggles.Crouch) != 0) ToggleCrouch(own);
		if ((requested & OwnToggles.Dampeners) != 0) own.Data.ToggleDampeners(clearRelativeDampeners: false);
		if (requested != OwnToggles.None) _predicted++;
		Fuel(own);
		ServerRules(own);
		Jetpack(own);
	}

	private static CharacterMovementComponent? Movement(Entity e) => e.TryGet<CharacterMovementComponent>();

	// JetpackFuelComponent is server-only: the fuel this game sees is the replicated stat, a round trip old, so a boost
	// (or flight) that ran dry went on here until the server's forced state came back (3 m and more at boost speed). The
	// burn of each predicted tick is kept by input; the fuel left is the replicated value minus what the inputs the
	// server had not answered yet when it arrived burned. At zero the boost ends (and the jetpack, out of flight fuel)
	// as the server's fuel signals do. The server's state still wins in the ack.
	private static readonly Guid FuelId = new("c634cbaa-2ab6-424f-a129-99c1428ac2c7");
	private static JetpackFuelComponentDefinition? _fuel;
	// Only a stat the server is seen burning is predicted (Sync.FuelLedger).
	private static readonly FuelLedger Ledger = new();
	private static long _fuelOuts;

	private static readonly bool FuelOff = MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "ownfuel:off");

	private static void Fuel(Entity own)
	{
		if (FuelOff) return;
		if (_fuel == null && !Singleton<DefinitionManager>.Instance.TryGetDefinition(FuelId, out _fuel)) return;
		if (own.TryGet<Keen.Game2.Simulation.WorldObjects.Characters.Stats.StatCollectionComponent>() is not { } stats) return;
		var flightStat = _fuel!.PrecisionFlightFuel.Stat;
		var boostStat = _fuel.BoostFlightFuel.Stat;
		if (stats.TryGetStat(flightStat) is not { } flight || stats.TryGetStat(boostStat) is not { } boost) return;
		uint seq = OwnMotion.Seq;
		// ConsumeJetpackFuel for the tick just run.
		float f = 0, b = 0;
		if (Movement(own)?.CurrentMovementState == CharacterMovementState.Jetpack)
		{
			own.Data.TryGet<Keen.Game2.Simulation.WorldObjects.CubeBlocks.Movement.VoluntaryThrustData>(out var voluntary);
			var amount = !own.Data.Has<ActiveThrustData>() || Math.Clamp(voluntary.VoluntaryThrust.Length(), 0f, 1f).IsZero() ? _fuel.StationaryFuel
				: _boost != CharacterBoostState.None ? _fuel.BoostFlightFuel : _fuel.PrecisionFlightFuel;
			if (amount.Stat == boostStat) b = amount.GetFrameValue(); else f = amount.GetFrameValue();
		}
		Ledger.Record(seq, f, b);
		var (flightLeft, boostLeft) = Ledger.Left(seq, flight.Value, boost.Value, OwnMotion.ClientCorrection.NewestAck, _fuel.PrecisionFlightFuel.GetFrameValue(), _fuel.BoostFlightFuel.GetFrameValue());
		if (Ledger.BoostBurns && _boost != CharacterBoostState.None && boostLeft <= boost.MinValue)
		{
			SetBoost(own, CharacterBoostState.None);
			_boostRefused = true;
			if (_fuelOuts++ < 20) GameLog.Instance.Info($"own motion: predicted boost fuel out ({boost.Value:0.00} replicated, input {seq}, {Ledger.Unanswered(seq)} unanswered)");
		}
		if (Ledger.FlightBurns && _boost == CharacterBoostState.None && flightLeft <= flight.MinValue && Movement(own) is { CurrentMovementState: CharacterMovementState.Jetpack } movement)
		{
			SetState(own, movement, CharacterMovementState.Ground);
			if (_fuelOuts++ < 20) GameLog.Instance.Info($"own motion: predicted flight fuel out ({flight.Value:0.00} replicated, input {seq})");
		}
	}

	// CharacterMovementComponent.RequestStateToggleInternal (fuel is the server's to know: a refusal comes back in the ack).
	private static void ToggleJetpack(Entity own)
	{
		if (Movement(own) is not { } movement) return;
		var state = movement.CurrentMovementState;
		if (state == CharacterMovementState.Sitting) return;
		if (state == CharacterMovementState.Jetpack) SetState(own, movement, CharacterMovementState.Ground);
		else if (!own.Data.Has<CharacterCrouchTag>() || !StandingOccupied(own)) SetState(own, movement, CharacterMovementState.Jetpack);
	}

	// The movement state is a server-owned sync property: its setter throws on a player's game. This does what applying
	// the server's value does there: the field, then the property's other handlers (OnMovementStateChanged and the rest).
	private static void SetState(Entity own, CharacterMovementComponent movement, CharacterMovementState value)
	{
		if (movement._currentMovementState == value) return;
		movement._currentMovementState = value;
		ref var signals = ref own.CompositionData.Signals;
		var (begin, end) = signals.SignalIndex[movement.__signalTableOffset];
		if (end - begin > 1)
			CharacterMovementComponent.__InternalCurrentMovementStateProperty.Trigger(own, signals.SignalHandlers.AsSpan().Slice(begin + 1, end - begin - 1), in value);
	}

	// CharacterMovementServerComponent.RequestCrouchToggleInternal.
	private static void ToggleCrouch(Entity own)
	{
		if (!own.Data.TryGet<CharacterPhysicsState>(out var physics) || !physics.Supported) return;
		if (!own.IsCrouched()) own.SetCrouched(crouched: true);
		else if (!StandingOccupied(own)) own.SetCrouched(crouched: false);
	}

	private static bool StandingOccupied(Entity own) => own.TryGet<Keen.VRage.Physics.Components.CharacterPhysicsComponent>()?.IsStandingColliderOccupied() == true;

	// The server-only CharacterMovementServerComponent jobs that change the state: a crouch ends off the ground; leaving
	// gravity with nothing to stand on switches the jetpack on with dampeners.
	private static void ServerRules(Entity own)
	{
		var data = own.Data;
		if (data.Has<CharacterCrouchTag>() && data.TryGet<CharacterPhysicsState>(out var physics) && physics.State != CharacterState.OnGround) own.SetCrouched(crouched: false);
		bool gravity = data.Has<GravityEffectData>();
		if (_hadGravity && !gravity && !data.Has<CharacterMovementComponent.EntityAlignData>() && Movement(own) is { CurrentMovementState: CharacterMovementState.Ground } movement)
		{
			SetState(own, movement, CharacterMovementState.Jetpack);
			if (data.GetDampenerState() == DampenerState.Off) data.ToggleDampeners(clearRelativeDampeners: false);
		}
		_hadGravity = gravity;
	}

	private static JetpackMovementComponentDefinition? _jetpack;
	private static ServerJetpackBoostComponentDefinition? _boostDef;
	private static float _boostSpeed;

	/// <summary>Definitions were pushed (a world, maybe with other mods or rules): the cached ones may be stale.</summary>
	internal static void ForgetDefinitions() => (_jetpack, _boostDef) = (null, null);

	private static bool Definitions()
	{
		if (_jetpack != null && _boostDef != null) return true;
		var manager = Singleton<DefinitionManager>.Instance;
		manager.TryGetDefinition(JetpackId, out _jetpack);
		manager.TryGetDefinition(BoostId, out _boostDef);
		_boostSpeed = manager.TryGetConfiguration<Keen.Game2.Simulation.GameSystems.Physicss.PhysicsSessionConfiguration>(out var physics) ? physics.MaximumCharacterSpeedLinear : 0;
		if (_jetpack == null || _boostDef == null) GameLog.Instance.WarnOnce("own-jetpack-def", "own motion: the server's jetpack definitions are not loaded here; jetpack tuning comes from the server");
		return _jetpack != null && _boostDef != null;
	}

	// JetpackMovementComponent (UpdatePrecisionBoostMode, UpdateJetpackThrust, UpdateJetpackTorque) and
	// ServerJetpackBoostComponent (UpdateBoostActivation, BoostTick, ApplyBoostImpulse), for the next tick.
	private static void Jetpack(Entity own)
	{
		var data = own.Data;
		if (Movement(own)?.CurrentMovementState != CharacterMovementState.Jetpack || !Definitions())
		{
			_boost = CharacterBoostState.None;
			_impulse = false;
			return;
		}
		var def = _jetpack!;
		var boostDef = _boostDef!;
		data.TryGet<AdvancedCharacterControlData>(out var control);
		data.TryGet<RigidBodyData>(out var body);
		if (!control.IsBoosting) { SetBoost(own, CharacterBoostState.None); _boostRefused = false; }
		else if (_boostRefused) { }
		else if (_boost == CharacterBoostState.None) { SetBoost(own, CharacterBoostState.Charging); _chargeTicks = 0; }
		else if (_boost == CharacterBoostState.Charging && ++_chargeTicks * TimeSpan.TicksPerSecond / 60 >= boostDef.BoostHoldDuration.Ticks) SetBoost(own, CharacterBoostState.Boosting);
		else if (_boost == CharacterBoostState.Boosting && _impulse) Impulse(own, boostDef, body);

		bool boosting = _boost == CharacterBoostState.Boosting;
		var multiplier = boosting ? new Vector3(boostDef.LateralBoostThrustMultiplier, boostDef.LateralBoostThrustMultiplier, boostDef.ForwardBoostThrustMultiplier) : Vector3.One;
		var thrust = new MaxThrustData { Regular = new Thrust6Directions(def.MaxThrust * multiplier), Braking = new Thrust6Directions(def.MaxThrust * def.BoostThrustMultiplier) };
		if (!data.TryGet<MaxThrustData>(out var oldThrust) || !oldThrust.Equals(thrust)) data.Set(thrust);
		float speed = boosting && _boostSpeed > 0 ? _boostSpeed : def.PrecisionModeSpeedLimit;
		if (!data.TryGet<SoftSpeedLimitData>(out var limit) || limit.Speed != speed) data.Set(new SoftSpeedLimitData { Speed = speed });
		var movementDef = boosting ? def.BoostMovementDefinition : own.TryGet<MovementComponent>()?.Definition;
		if (movementDef != null && data.TryGet<MovementData>(out var movement) && !movement.Definition.Equals(movementDef.ReferenceHandle))
			data.Set(movement with { Definition = movementDef.ReferenceHandle });
		float torque = def.MaxTorque * (boosting && body.LinearVelocity.LengthSquared() > def.PrecisionModeSpeedLimit * def.PrecisionModeSpeedLimit ? boostDef.BoostTorqueMultiplier : 1f);
		data.TryGet<CharacterMovementComponent.JetpackModeData>(out var mode);
		if (mode.IsBoosting != boosting || mode.MaxTorque != torque)
		{
			mode.IsBoosting = boosting;
			mode.MaxTorque = torque;
			data.Set(mode);
		}
		// JetpackMovementComponent.UpdateInAirFriction: with no thrust asked for, the body takes the "resting in air"
		// physics material (it grips what it touches). Without it the player's jetpack character slid down slopes the
		// server's sat still on (dampeners off on a 40% grade: 3 m off in 2 s). A tick later than the server's.
		if (!AirFrictionOff && data.TryGet<Keen.VRage.Physics.Character.CharacterPhysicsData>(out var physics))
		{
			data.TryGet<Keen.Game2.Simulation.WorldObjects.CubeBlocks.Movement.VoluntaryThrustData>(out var voluntary);
			bool resting = Vector3.IsZero(voluntary.VoluntaryThrust);
			if (physics.RestingInAir != resting)
			{
				physics.RestingInAir = resting;
				data.Set(physics);
			}
		}
	}

	private static readonly bool AirFrictionOff = MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "ownairfriction:off");

	private static void SetBoost(Entity own, CharacterBoostState state)
	{
		if (_boost == state) return;
		_boost = state;
		if (state == CharacterBoostState.None)
		{
			_impulse = false;
			if (own.Data.TryGet<OverriddenThrustData>(out var o) && o.DirectionalThrust != Vector3.Zero) own.Data.Set(o with { DirectionalThrust = Vector3.Zero });
		}
		else if (state == CharacterBoostState.Boosting) _impulse = true;
	}

	private static void Impulse(Entity own, ServerJetpackBoostComponentDefinition def, in RigidBodyData body)
	{
		var forward = own.Data.GetWorldTransform().Orientation.GetForward();
		float along = Math.Max(0f, Vector3.Dot(body.LinearVelocity, forward));
		if (along > def.BoostImpulseTargetVelocity || (def.BoostImpulseTargetVelocity - along).IsZero())
		{
			_impulse = false;
			return;
		}
		float step = (float)(0.01666666753590107 / def.BoostImpulseDuration.TotalSeconds);
		float t = Math.Clamp((float)(1.0 - Math.Pow(1f - along / def.BoostImpulseTargetVelocity, 1f / (float)def.BoostImpulseEaseOutPower)) + step, 0f, 1f);
		float target = MathHelper.Lerp(0f, def.BoostImpulseTargetVelocity, 1f - (float)Math.Pow(1f - t, def.BoostImpulseEaseOutPower));
		own.Data.TryGet<RigidBodyMassProperties>(out var mass);
		own.Data.TryGet<OverriddenThrustData>(out var o);
		own.Data.Set(o with { DirectionalThrust = Vector3.Forward * (target - along) * mass.Mass / (1f / 60f) });
	}

	/// <summary>The state byte for <paramref name="e"/> (either side).</summary>
	internal static byte Pack(Entity e)
		=> OwnState.Pack((int)(Movement(e)?.CurrentMovementState ?? CharacterMovementState.None), e.Data.Has<CharacterCrouchTag>(),
			e.Data.GetDampenerState() != DampenerState.Off, e.Data.TryGet<CharacterMovementComponent.JetpackModeData>(out var j) && j.IsBoosting);

	internal static bool Differs(Entity own, byte server) => OwnState.Differs(Pack(own), server);

	/// <summary>The server disagreed for several acks in a row (or moved the character): its state is taken.</summary>
	internal static void Take(Entity own, byte server, string why)
	{
		byte mine = Pack(own);
		bool forced = (server & OwnState.Forced) != 0;
		// The live state already caught up (the disagreement was with what this game predicted for that older input).
		if (forced ? !OwnState.DiffersAtAll(mine, server) : !OwnState.Differs(mine, server)) return;
		var state = (CharacterMovementState)OwnState.Movement(server);
		if (Movement(own) is { } movement && movement.CurrentMovementState != state && state != CharacterMovementState.Sitting && movement.CurrentMovementState != CharacterMovementState.Sitting)
			SetState(own, movement, state);
		bool crouched = (server & OwnState.Crouched) != 0;
		if (own.IsCrouched() != crouched) own.SetCrouched(crouched);
		bool dampeners = (server & OwnState.Dampeners) != 0;
		if ((own.Data.GetDampenerState() != DampenerState.Off) != dampeners) own.Data.ToggleDampeners(clearRelativeDampeners: false);
		if ((server & OwnState.Boosting) == 0 && _boost != CharacterBoostState.None) SetBoost(own, CharacterBoostState.None);
		// A boost this game ended early (its fuel guess ran out first) that the server still runs.
		if ((server & OwnState.Boosting) != 0 && _boost != CharacterBoostState.Boosting)
		{
			_boostRefused = false;
			SetBoost(own, CharacterBoostState.Boosting);
			_impulse = false;
		}
		// A boost the server ended (no fuel) stays off until the key is pressed again, as on the server.
		if (forced && (server & OwnState.Boosting) == 0 && (mine & OwnState.Boosting) != 0) _boostRefused = true;
		lock (Requests) _requested = OwnToggles.None;
		_carried = OwnToggles.None;
		Jetpack(own);
		_taken++;
		GameLog.Instance.Info($"own motion: took the server's movement state ({why}{(forced ? ", the server changed it" : "")}): {Describe(mine)} -> {Describe(server)}");
	}

	internal static string Describe(byte s) => $"{(CharacterMovementState)OwnState.Movement(s)}{((s & OwnState.Crouched) != 0 ? " crouched" : "")}{((s & OwnState.Dampeners) != 0 ? " dampeners" : "")}{((s & OwnState.Boosting) != 0 ? " boosting" : "")}";

	internal static string Stats => $"{_predicted} toggles predicted, server state taken {_taken} times, {_blocked} of Keen's toggle requests left to the inputs, {_fuelOuts} fuel outs predicted";

	// --- server ---

	[ThreadStatic] private static int _ours, _internal;
	private static long _blocked;

	/// <summary>Server thread: the toggles of the input applied this tick, with Keen's own server rules.</summary>
	internal static void ApplyServer(Entity character, OwnToggles toggles)
	{
		_ours++;
		try
		{
			if ((toggles & OwnToggles.Jetpack) != 0) character.TryGet<CharacterMovementComponent>()?.RequestStateToggleInternal();
			if ((toggles & OwnToggles.Crouch) != 0) character.TryGet<CharacterMovementServerComponent>()?.RequestCrouchToggleInternal();
			if ((toggles & OwnToggles.Dampeners) != 0 && character.TryGet<CharacterMovementComponent>() is { } movement) DampenerToggle.Invoke(movement, null);
		}
		finally { _ours--; }
	}

	private static bool KeenTogglePrefix(Component __instance)
	{
		if (_ours > 0 || _internal > 0 || !OwnMotion.TogglesFromInputs(__instance.Entity)) return true;
		System.Threading.Interlocked.Increment(ref _blocked);
		return false;
	}

	private static void InternalEnter() => _internal++;

	private static Exception? InternalLeave(Exception? __exception)
	{
		_internal--;
		return __exception;
	}
}
