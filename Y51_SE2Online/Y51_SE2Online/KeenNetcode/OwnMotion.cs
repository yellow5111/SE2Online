using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using HarmonyLib;
using Keen.Game2.Client.GameSystems.PlayerControl;
using Keen.Game2.Client.WorldObjects.Character;
using Keen.Game2.Simulation.WorldObjects.Characters;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.Pilotable;
using Keen.Game2.Simulation.WorldObjects.Movement;
using Keen.Game2.Simulation.WorldObjects.Shared.Movement;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.Accessors;
using Keen.VRage.DCS.Components;
using Keen.VRage.Game.Multiplayer;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Multiplayer.Sessions;
using Keen.VRage.Physics.Character;
using Keen.VRage.Physics.Data;
using Y51_SE2Online.Core;
using Y51_SE2Online.NetHost;
using Y51_SE2Online.Sync;
using Y51_SE2Online.SyncEngine;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>
/// Predicted, server-authoritative movement of a player's own character (on foot and by jetpack). SE2 4.0 simulates
/// every character on the server and only plays the result back on the player's game, so every ping and every server
/// stall was input lag. When the server allows it (<see cref="HostSettings.ClientMovement"/>), a joined player's game
/// builds its own character with the server's physics definition and simulates it at once from its inputs, ignoring the
/// server's motion samples for it. Each tick it sends that tick's input with a sequence number (<see cref="OwnInputs"/>).
/// The server never takes a position from a player: it applies those inputs instead of Keen's later ControlData, one per
/// server tick in order (<see cref="OwnInputBuffer"/>), so Keen's own simulation enforces walls, speed, fuel and gravity,
/// and sends back where the character is after each input. The player's game compares that with what it predicted for
/// the same input and eases the difference out (<see cref="OwnCorrection"/>); a teleport, a seat or a new body resyncs
/// it (new epoch). Seated and dead characters stay server-driven; the co-op host's own character is left alone. If the
/// two sides lose step (no ack reaches the player's game, or none of its inputs is the one the server is due), both go
/// back to Keen's own server-driven movement for that player and log it instead of drifting apart.
/// </summary>
internal static class OwnMotion
{
	private static readonly Guid ServerPhysics = new("80400e4a-57d6-4478-9b46-8a6fe652f5c9");

	private static readonly System.Reflection.FieldInfo PhysicsDefinition = AccessTools.Field(typeof(Keen.VRage.Physics.Components.CharacterPhysicsComponent), "CharacterPhysicsDefinition");

	private const double TickSeconds = 1 / 60.0;

	// -y51online:tickpace:off: this game does not offer to skip ticks; the server merges inputs as before.
	private static readonly bool TickPaceOff = System.Linq.Enumerable.Contains(MpPlugin.LaunchArgs, MpOptions.Prefix + "tickpace:off");
	private static readonly SkipPlanner Pace = new();
	private static Frozen? _skipFrozen;

	// What a skipped tick would look like now (Sync.SkipPlanner decides): standing (no input, slower than 1.5 m/s),
	// walking (on the ground, under 7 m/s), or blocked (boosting, charging the boost, a jump due, on a moving body).
	private static (bool Standing, bool Walking, bool Blocked) SkipConditions(Entity own)
	{
		if (_jumpPending || !own.Data.TryGet<RigidBodyData>(out var body)) return (false, false, true);
		bool blocked = own.Data.TryGet<CharacterMovementComponent.JetpackModeData>(out var jet) && jet.IsBoosting
			|| own.Data.TryGet<AdvancedCharacterControlData>(out var flags) && flags.IsBoosting;
		if (Parent(own) is { } under && !EngineCodec.IsDead(under) && under.Data.Has<DynamicTag>())
		{
			under.Data.TryGet<RigidBodyData>(out var deck);
			blocked |= !SkipPlanner.FrameAllows(true, deck.LinearVelocity.Length(), deck.AngularVelocity.Length());
		}
		own.Data.TryGet<ControlData>(out var control);
		float speed = RelativeSpeed(own, body);
		bool standing = control.Movement == Vector3.Zero && control.Rotation == Vector3.Zero && speed < 1.5f;
		var state = own.TryGet<CharacterMovementComponent>()?.CurrentMovementState;
		// A jetpack under 3 m/s too (-y51online:jetskip:off: on the ground only): flying, a 55-58 ticks/s server asked 66
		// skips a minute and got 6, and drained the rest of its queue by merging two inputs into a tick.
		bool walking = state == CharacterMovementState.Ground && speed < 7f || !JetSkipOff && state == CharacterMovementState.Jetpack && speed < 3f;
		return (standing, walking, blocked);
	}

	private static readonly bool JetSkipOff = System.Linq.Enumerable.Contains(MpPlugin.LaunchArgs, MpOptions.Prefix + "jetskip:off");

	// A stall of this game (0.2-1.3 s a few times a minute: an autosave, a GC) is followed by the missed ticks back to
	// back. Each of them predicted the character and sent an input: about 36 inputs in 100 ms after a 0.6 s stall, over the
	// server's 100 a second (the rest guessed), and a queue the server drained by merging. Holding the character on
	// those ticks left it the whole stall behind the server, which kept moving it on the last input: at speed that came
	// back as one 10-20 m snap after every stall. They are predicted again; -y51online:catchupfreeze:on holds them.
	private static readonly bool CatchUpFreezeOff = !System.Linq.Enumerable.Contains(MpPlugin.LaunchArgs, MpOptions.Prefix + "catchupfreeze:on");
	private static readonly System.Diagnostics.Stopwatch TickWall = System.Diagnostics.Stopwatch.StartNew();
	private static readonly CatchUpDetector CatchUp = new();
	internal static long CatchUpHeld;
	private static long _inputsThisSecond, _inputSecondMs, _catchUpAtSecond, _serverTicksSecond, _serverCatchUps, _serverSecondMs;

	private static bool CatchingUp() => CatchUp.Tick(TickWall.Elapsed.TotalMilliseconds) && !CatchUpFreezeOff;

	public static void Install(Harmony harmony)
	{
		var motion = AccessTools.TypeByName("Keen.VRage.Game.Multiplayer.MotionStateApplicationSystem");
		harmony.Patch(AccessTools.Method(motion, "EvaluateMotionState"), prefix: Hook(nameof(EvaluatePrefix)));
		harmony.Patch(AccessTools.Method(motion, "AttachMotionHistory"), prefix: Hook(nameof(AttachPrefix)));
		harmony.Patch(AccessTools.Method(typeof(Keen.VRage.Physics.Components.RigidBodyComponent), nameof(Keen.VRage.Physics.Components.RigidBodyComponent.OnAddedToScene)), prefix: Hook(nameof(AddedToScenePrefix)));
		harmony.Patch(AccessTools.Method(typeof(CharacterMovementControlComponent), nameof(CharacterMovementControlComponent.RequestJump)), postfix: Hook(nameof(JumpPostfix)));
		harmony.Patch(AccessTools.Method(typeof(CharacterMovementControlComponent), nameof(CharacterMovementControlComponent.RequestSprint)), postfix: Hook(nameof(SprintPostfix)));
		harmony.Patch(AccessTools.Method(typeof(CharacterMovementControlComponent), nameof(CharacterMovementControlComponent.RequestBoost)), postfix: Hook(nameof(BoostPostfix)));
		foreach (string request in new[] { nameof(CharacterMovementServerComponent.RequestJump), nameof(CharacterMovementServerComponent.RequestSprint), nameof(CharacterMovementServerComponent.RequestBoost) })
			harmony.Patch(AccessTools.Method(typeof(CharacterMovementServerComponent), request), prefix: Hook(nameof(KeenRequestPrefix)));
		var sync = typeof(Keen.Game2.Game.SessionComponents.ServerSessionComponent.ServerToClientDataSynchronizations);
		harmony.Patch(AccessTools.Method(sync, "SyncMotionState"), prefix: Hook(nameof(SyncMotionPrefix)));
		harmony.Patch(AccessTools.Method(typeof(Keen.VRage.Physics.Components.CharacterPhysicsComponent), "UpdateOrientationConstraint"), prefix: Hook(nameof(OrientationConstraintPrefix)));
		harmony.Patch(AccessTools.Method(typeof(Keen.VRage.Physics.Havok.HavokSessionComponent), "CollectDataForCharacters"), prefix: Hook(nameof(CollectCharacterPrefix)));
		if (!System.Linq.Enumerable.Contains(MpPlugin.LaunchArgs, MpOptions.Prefix + "ownyaw:off"))
			harmony.Patch(AccessTools.Method(typeof(CharacterMovementComponent), "ApplyControlData"), postfix: Hook(nameof(GroundYawPostfix)));
		OwnStatePrediction.Install(harmony);
		if (!VoxelCutOff && AccessTools.Method(typeof(Keen.VRage.Voxels.Components.VoxelPhysicsComponent), "OnVoxelPhysicsChunkChanged") is { } chunkChanged)
			harmony.Patch(chunkChanged, postfix: Hook(nameof(VoxelChunkPostfix)));
	}

	// -y51online:voxelcut:off: acks of inputs predicted before a nearby voxel cut reached this game's colliders are
	// corrected like any other (Sync.OwnCorrection.Blind).
	private static readonly bool VoxelCutOff = System.Linq.Enumerable.Contains(MpPlugin.LaunchArgs, MpOptions.Prefix + "voxelcut:off");
	private const double VoxelCutReach = 2;
	private static readonly ConcurrentQueue<BoundingBoxD> VoxelCuts = new();

	// After the collider update is applied (DCS thread): the chunk's world bounds.
	private static void VoxelChunkPostfix(BoundingBoxD worldBounds, Keen.VRage.Voxels.Components.VoxelPhysicsChunkChange change)
	{
		if (_driving && change == Keen.VRage.Voxels.Components.VoxelPhysicsChunkChange.Updated) VoxelCuts.Enqueue(worldBounds);
	}


	private static HarmonyMethod Hook(string name) => new(typeof(OwnMotion), name);

	// --- player's game ---

	/// <summary>A joined player's game whose server takes this game's inputs for its own character.</summary>
	private static bool ClientEnabled => MpPlugin.App?.ServerSession == null && NetSession.Current?.Client?.Accept?.Settings.ClientMovement == true;

	// Read by the motion-state and incoming-data prefixes on the client scene's job and network paths.
	private static volatile bool _driving;
	private static DEntity _own;
	private static volatile Entity? _ownEntity;

	private static ClientSession? _boundTo;
	private static Entity? _correctedFor;
	private static ushort _epoch;
	private static uint _seq;
	private static volatile bool _jumpPending;
	private static readonly List<OwnInput> Recent = new();
	// -y51online:softcorrect:off: every error over the deadband eased at once, as in 0.18.1 (Sync.OwnCorrection.Soft).
	private static readonly OwnCorrection Correction = new(soft: !System.Linq.Enumerable.Contains(MpPlugin.LaunchArgs, MpOptions.Prefix + "softcorrect:off"));
	private static readonly OwnStateCheck StateCheck = new();

	// The server answers with its movement state after each input: it takes this game's toggles from its inputs.
	private static bool _serverStates;
	private static SessionIds? _clientIds;
	private static Session? _idsSession;
	private static long _nextScanMs, _statsLogMs, _ackedMs, _vanillaUntilMs, _ackCount, _errorLogMs;

	// An ack this far off is logged (at most once a second): what the character did when prediction went wrong.
	private const double ErrorLogMeters = 0.3;

	// No ack for this long while predicting: the server does not take this game's inputs (a path that drops them, a
	// server out of step), so the character goes back to the server's own movement for a while instead of drifting apart.
	private const long NoAckMs = 1500, NoFirstAckMs = 5000, VanillaMs = 10000;
	private static bool _answered;
	private static bool _logged;

	/// <summary>Frames this game has driven its character (the rig counts its response in frames).</summary>
	internal static long Frames;

#if !PULSAR
	/// <summary>Test rig: the movement this game sends instead of its own (absurd inputs the server must clamp).</summary>
	internal static System.Numerics.Vector3? ProbeSentMovement;
#endif

	internal static Entity? Driving => _driving ? _ownEntity : null;

	/// <summary>This game's own character as last found (predicted or not).</summary>
	internal static Entity? OwnEntity => _ownEntity;

	/// <summary>Main menu: let go of the last world's character and ids (the next session binds itself again).</summary>
	internal static void ResetClient()
	{
		_driving = false;
		_ownEntity = null;
		_correctedFor = null;
		_clientIds = null;
		_idsSession = null;
		_boundTo = null;
		_skipFrozen = null;
		_deckWas = null;
		MotionSmoothing.Deck = null;
		lock (HeldFrames) HeldFrames.Clear();
	}

	/// <summary>The own character while this game predicts its toggles (<see cref="OwnStatePrediction"/>).</summary>
	internal static Entity? TogglePredicting => _driving && _serverStates && !OwnStatePrediction.Off ? _ownEntity : null;

	internal static OwnCorrection ClientCorrection => Correction;

	/// <summary>The newest input sequence number this game sent.</summary>
	internal static uint Seq => _seq;

	internal static long LocalIdentity(Session? session)
	{
		if (NetSession.Current?.Client?.IdentityId is long assigned and not 0) return assigned;
		return session?.SessionComponents.TryGet<ClientPlayersSessionComponent>() is { } players ? (long)players.LocalPlayerIdentity : 0;
	}

	// The game builds every character from the client physics definition (Simulated false: a kinematic body the server's
	// samples move). The player's own one gets the server's definition, so this game simulates it. Its owner is not
	// known yet at Init, so the switch happens just before the body is made, undoing what Init set up as kinematic.
	private static void AddedToScenePrefix(Keen.VRage.Physics.Components.RigidBodyComponent __instance)
	{
		if (__instance is not CharacterPhysicsEntityComponent character || !Own(character) || ServerDefinition() is not { } definition) return;
		character.Definition = definition;
		PhysicsDefinition.SetValue(character, definition);
		character._bodyArgs.MotionType = BodyArgs.Motion.Dynamic;
		character.Data.TryRemove<TransformDriven>();
		character._stateMachine?.Dispose();
		character._stateMachine = definition.CreateStateMachine(character._physics);
		// Movement and thrust read the motion type in their own Init, which ran while this character was still kinematic:
		// it got no gravity and no jetpack thrust.
		if (character.Entity.TryGet<CharacterMovementComponent>() is { } movement)
			movement.SetGravityType(movement.CurrentMovementState == CharacterMovementState.Jetpack ? movement._definition.JetpackGravityType : movement._definition.GroundedGravityType);
		character.Entity.TryGet<ThrustComponent>()?.OnMotionTypeChanged(BodyArgs.Motion.Dynamic);
		GameLog.Instance.Info("own motion: this player's character is predicted by this game");
	}

	private static bool Own(CharacterPhysicsEntityComponent character)
	{
		if (!ClientEnabled || character.Definition?.Simulated != false) return false;
		long owner = TeamPermissions.OwnerOf(character.Entity);
		return owner != 0 && owner == LocalIdentity(MpPlugin.App?.ClientSession);
	}

	private static CharacterPhysicsEntityComponentDefinition? ServerDefinition()
	{
		if (Keen.VRage.Library.Utils.Singleton<Keen.VRage.Library.Definitions.DefinitionManager>.Instance.TryGetDefinition(ServerPhysics, out CharacterPhysicsEntityComponentDefinition definition)) return definition;
		GameLog.Instance.WarnOnce("own-motion-def", "own motion: the server's character physics definition is not loaded; the server moves this player's character");
		return null;
	}

	private static bool EvaluatePrefix(DEntity entity) => !(_driving && entity == _own);

	private static bool AttachPrefix(DEntity entity, EntityData<MotionHistory> histories)
	{
		if (!_driving || entity != _own) return true;
		histories.WriteTo(entity, default);
		return false;
	}

	// Jump, sprint and boost are server requests: the flags are set here at once, and ride in this tick's input.
	private static void JumpPostfix(CharacterMovementControlComponent __instance)
	{
		if (Driven(__instance.Entity) is not { } own || own.Data.Has<CharacterCrouchTag>()) return;
		own.Data.GetWritePtr<AdvancedCharacterControlData>().IsJumping = true;
		_jumpPending = true;
	}

	private static void SprintPostfix(CharacterMovementControlComponent __instance, bool sprint)
	{
		if (Driven(__instance.Entity) is { } own) own.Data.GetWritePtr<AdvancedCharacterControlData>().IsSprinting = sprint;
	}

	private static void BoostPostfix(CharacterMovementControlComponent __instance, bool boost)
	{
		if (Driven(__instance.Entity) is { } own) own.Data.GetWritePtr<AdvancedCharacterControlData>().IsBoosting = boost;
	}

	private static Entity? Driven(Entity? entity) => _driving && entity != null && ReferenceEquals(entity, _ownEntity) && entity.Data.Has<AdvancedCharacterControlData>() ? entity : null;

	// The server's copies of what this game works out itself for its predicted character come back a round trip late: the
	// flags would undo a sprint or boost let go of since, the physics state (on ground, falling, ground speed) set the
	// character's movement and animation back by the round trip at every jump, landing, start and stop, and the states this
	// game predicts (crouch, dampeners, jetpack tuning) flipped back for a moment after each toggle. The server's state
	// still wins: it comes with each ack (OwnStateCheck).
	private static readonly HashSet<Type> PredictedStates = new()
	{
		typeof(CharacterCrouchTag), typeof(DampeningData), typeof(MaxThrustData), typeof(CharacterMovementComponent.JetpackModeData), typeof(SoftSpeedLimitData),
	};

	private static readonly bool KeepServerStates = System.Linq.Enumerable.Contains(MpPlugin.LaunchArgs, MpOptions.Prefix + "ownfilter:off");

	/// <summary>
	/// Player's game: a value the server sends for the character this game drives itself that this game keeps its own copy
	/// of (the server's comes a round trip late): its control flags, its physics state, and the states it predicts (crouch,
	/// dampeners, jetpack tuning) while it predicts toggles. Removals of the control flags are the server's.
	/// </summary>
	internal static bool KeepsOwn(int typeId, Entity target, bool removal)
	{
		if (!_driving || _ownEntity is not { } own || !ReferenceEquals(own, target)) return false;
		Type type = Keen.VRage.DCS.Internal.RuntimeDataInfo.Of(typeId).RuntimeType;
		if (type == typeof(AdvancedCharacterControlData)) return !removal;
		if (KeepServerStates) return false;
		return type == typeof(Keen.VRage.Physics.Character.CharacterPhysicsState) || TogglePredicting != null && PredictedStates.Contains(type);
	}

	private static bool Seated(Entity character)
		=> character.TryGet<CharacterMovementComponent>()?.CurrentMovementState == CharacterMovementState.Sitting || character.AsInterface<ISeatable>()?.CurrentSeat != null;

	private static bool Alive(Entity character) => !EngineCodec.IsDead(character) && character.TryGet<CharacterLifeComponent>()?.IsAlive == true;

	/// <summary>Client session thread, after each update (one simulation tick).</summary>
	public static void ClientTick(Session session)
	{
		bool catchUp = CatchingUp();
		// Taken (or not) by the physics step that just ran.
		_softShiftPending = false;
		var client = NetSession.Current?.Client;
		if (!ReferenceEquals(client, _boundTo))
		{
			_boundTo = client;
			_epoch = 0;
			_logged = false;
			_ackedMs = MpClock.NowMs();
			_answered = false;
			_serverStates = false;
			Pace.Reset();
			_skipFrozen = null;
			Recent.Clear();
			Correction.Reset();
			StateCheck.Reset();
			OwnStatePrediction.Reset();
			while (OwnMotionInbox.Poses.TryDequeue(out _)) { }
		}
		if (!ClientEnabled || client == null)
		{
			_driving = false;
			return;
		}
		if (!_logged)
		{
			_logged = true;
			GameLog.Instance.Info("own motion: the server takes this game's inputs for its own character");
		}
		var own = OwnCharacter(session);
		bool drive = own != null && own.TryGet<CharacterPhysicsEntityComponent>()?.CharacterPhysicsDefinition.Simulated == true && !Seated(own);
		if (!ReferenceEquals(session, _idsSession)) { _idsSession = session; _clientIds = new SessionIds(session, server: false); }
		var ids = _clientIds!;
		if (!ReferenceEquals(own, _correctedFor))
		{
			_correctedFor = own;
			Correction.Reset();
			StateCheck.Reset();
		}
		OwnPose? ack = null;
		while (OwnMotionInbox.Poses.TryDequeue(out var pose))
		{
			if (pose.State is byte flagged && (flagged & OwnState.Skip) != 0)
			{
				pose = pose with { State = (byte)(flagged & ~OwnState.Skip) };
				if (!TickPaceOff) Pace.Ask(MpClock.NowMs());
			}
			if (pose.State.HasValue && !OwnStatePrediction.Off) _serverStates = true;
			if (OwnPose.NewerEpoch(pose.Epoch, _epoch))
			{
				_epoch = pose.Epoch;
				ack = null;
				Pace.Reset();
				_skipFrozen = null;
				_vanillaUntilMs = 0;
				_ackedMs = MpClock.NowMs();
				_answered = false;
				Recent.Clear();
				Correction.Reset();
				StateCheck.Reset();
				OwnStatePrediction.Reset();
				if (drive)
				{
					Rebase(own!, pose, ids, $"the server moved this character (epoch {_epoch})");
					if (pose.State is byte resynced && _serverStates && !Seated(own!) && OwnStatePrediction.Differs(own!, resynced)) OwnStatePrediction.Take(own!, resynced, "resync");
				}
				continue;
			}
			if (pose.Epoch == _epoch && pose.Seq != 0 && (ack is not { } newest || Net.Seq.Newer(pose.Seq, newest.Seq))) ack = pose;
		}
		long now = MpClock.NowMs();
		if (drive && now < _vanillaUntilMs) drive = false;
		if (!drive)
		{
			_ackedMs = now;
			if (own != null && !Seated(own) && now >= _vanillaUntilMs) GameLog.Instance.WarnOnce("own-motion-sim", "own motion: this player's character kept the client physics definition; the server moves it");
			_driving = false;
			_deckWas = null;
			MotionSmoothing.Deck = null;
			return;
		}
		_ownEntity = own;
		_own = own!.DEntity;
		_driving = true;
		Frames++;
		var standsAt = own.Data.GetWorldTransform().Position;
		while (VoxelCuts.TryDequeue(out var cut))
			if (cut.Distance(standsAt) < VoxelCutReach) Correction.Blind(_seq);
		if (ack is { } a)
		{
			own.Data.TryGet<ControlData>(out var ownControl);
			own.Data.TryGet<RigidBodyData>(out var ownBody);
			bool atRest = ownControl.Movement == Vector3.Zero && !_jumpPending && RelativeSpeed(own, ownBody) < 0.3f;
			// Aiming a placement turns the mouse, which would leave only the 0.5 deg band: placing uses the steady 2 deg one.
			Correction.FlyingSpeed = Flying(own) ? RelativeSpeed(own, ownBody) : 0;
			switch (Correction.Measure(a.Seq, a.Parent, a.Position, a.Velocity, a.Rotation, standing: atRest, steady: ownControl.Rotation == Vector3.Zero || SyncEngine.BuildGhosts.LocalPlacing, onDeck: !DeckVelocityOff && OnMovingDeck(own)))
			{
			case OwnCorrection.Outcome.Rebase:
				Correction.Reset();
				Rebase(own, a, ids, "this game and the server disagree on what the character stands on");
				break;
			case OwnCorrection.Outcome.Snap:
				GameLog.Instance.Info($"own motion: the server's character is over {OwnCorrection.SnapMeters} m off this game's prediction for input {a.Seq}; applied at once ({Correction.Snaps} so far)");
				break;
			}
			if (MotionTrace.Recording)
				MotionTrace.Note("ack", "own", FormattableString.Invariant($"seq={a.Seq} age={_seq - a.Seq} error_cm={Correction.LastError.Length * 100:0.0} ex={Correction.LastError.X * 100:0.0} ey={Correction.LastError.Y * 100:0.0} ez={Correction.LastError.Z * 100:0.0} velocity_error={Correction.LastVelocityError.Length():0.00} speed={ownBody.LinearVelocity.Length():0.0} flying={(Flying(own) ? 1 : 0)}"));
		}
		if (ack is { } measured && Correction.LastError.Length > ErrorLogMeters && now - _errorLogMs > 1000)
		{
			_errorLogMs = now;
			var e = Correction.LastError;
			var ve = Correction.LastVelocityError;
			GameLog.Instance.Info($"own motion: input {measured.Seq} ({_seq - measured.Seq} ticks ago) {e.Length * 100:0} cm off ({e.X:0.00}, {e.Y:0.00}, {e.Z:0.00}), velocity {ve.Length():0.00} m/s off; state {own.TryGet<CharacterMovementComponent>()?.CurrentMovementState}{(own.Data.TryGet<Keen.VRage.Physics.Character.CharacterPhysicsState>(out var ps) ? " " + ps.State : "")}");
		}
		if (ack is { State: byte serverState } stated && TogglePredicting != null && StateCheck.Measure(stated.Seq, serverState) is byte take)
		{
			OwnStatePrediction.Take(own, take, $"input {stated.Seq}");
			StateCheck.TakenAt(_seq);
		}
		if (Correction.Acks != _ackCount)
		{
			_ackCount = Correction.Acks;
			_ackedMs = now;
			_answered = true;
		}
		// Still on the loading screen: this game sends no inputs yet, so silence is not an unanswered server. Timing it
		// gave up on prediction for the first 10 s of play after every join that loaded for over 5 s.
		else if (ClientWorld.Progress.Stage != Sync.JoinStage.Playing || JoinStatusUi.ScreenHeld)
			_ackedMs = now;
		// The first answer also waits for the join to finish loading on both sides.
		else if (now - _ackedMs > (_answered ? NoAckMs : NoFirstAckMs))
		{
			_vanillaUntilMs = now + VanillaMs;
			_answered = false;
			_driving = false;
			Recent.Clear();
			Correction.Reset();
			StateCheck.Reset();
			OwnStatePrediction.Reset();
			GameLog.Instance.Warn($"own motion: no answer from the server to this game's inputs for {now - _ackedMs} ms; the server moves this character for {VanillaMs / 1000} s, then prediction retries");
			return;
		}

		if (catchUp)
		{
			// Held where it is on what it stands on until the catch-up is over (Freeze/Thaw as for a skipped tick).
			if (_skipFrozen is { } kept) Thaw(own, kept);
			else _skipFrozen = Freeze(own);
			var under = Parent(own) is { } deckUnder && !EngineCodec.IsDead(deckUnder) ? PointVelocity(deckUnder, own.Data.GetWorldTransform().Position) : Vector3.Zero;
			if (own.Data.TryGet<RigidBodyData>(out var holding)) own.Data.Set(new RigidBodyData(under, holding.AngularVelocity));
			CatchUpHeld++;
			if (MotionTrace.Recording) MotionTrace.Own(own, "catchup", own.Data.GetWorldTransform(), under, "held, no input");
			return;
		}
		if (_skipFrozen is { } held)
		{
			_skipFrozen = null;
			Thaw(own, held);
		}
		else if (Pace.Wanted > 0 && SkipConditions(own) is var (standing, walking, blocked) && Pace.TryTake(standing, walking, blocked, _seq + 1, now))
		{
			// The server ticks slower than this game: one of its ticks is left out here (the character holds still, no
			// input is made) instead of the server merging two inputs into one tick, which costs a tick of travel.
			// Held by its velocity only: the player's ControlData (keys held) is left alone, and the frozen pose and
			// velocity are put back on the next tick.
			_skipFrozen = Freeze(own);
			if (MotionTrace.Recording) MotionTrace.Own(own, "freeze", own.Data.GetWorldTransform(), Vector3.Zero, "tick skipped for the server");
			// Still relative to what it stands on (at rest when a skip is allowed, SkipConditions), not to the world.
			var still = Parent(own) is { } under && !EngineCodec.IsDead(under) && under.Data.TryGet<RigidBodyData>(out var deck) ? deck.LinearVelocity : Vector3.Zero;
			if (own.Data.TryGet<RigidBodyData>(out var moving)) own.Data.Set(new RigidBodyData(still, moving.AngularVelocity));
			return;
		}
		var input = InputOf(own);
		var (parent, frame) = FrameOf(own, ids);
		var at = own.Data.GetWorldTransform();
		own.Data.TryGet<RigidBodyData>(out var body);
		var relative = frame is { } f ? WorldTransform.GetRelativeTransform(at, f) : at;
		var local = relative.Position;
		var velocity = frame is { } g ? WorldTransform.TransformDirectionInv(body.LinearVelocity, g) : body.LinearVelocity;
		Correction.Record(++_seq, parent, new Vec3D(local.X, local.Y, local.Z), new System.Numerics.Vector3(velocity.X, velocity.Y, velocity.Z),
			new System.Numerics.Quaternion(relative.Orientation.X, relative.Orientation.Y, relative.Orientation.Z, relative.Orientation.W));
		Correction.Placing = SyncEngine.BuildGhosts.LocalPlacing;
		Correction.FlyingSpeed = Flying(own) ? RelativeSpeed(own, body) : 0;
		var (dp, dv) = Correction.Step(TickSeconds, velocity.Length());
		var turn = FacingOff ? System.Numerics.Quaternion.Identity : Correction.StepTurn();
		if (dp != default || dv != default || !turn.IsIdentity) Shift(own, frame, dp, dv, turn);
		CarryOnDeck(own);

#if !PULSAR
		if (ProbeSentMovement is { } absurd) input = input with { Movement = absurd };
#endif
		_ownAt = own.Data.GetWorldTransform().Position;
		Recent.Insert(0, input);
		if (Recent.Count > OwnInputs.Redundancy) Recent.RemoveAt(Recent.Count - 1);
		client.SendOwnInputs(OwnInputs.Encode(_epoch, _seq, Recent, toggles: !OwnStatePrediction.Off, paced: !TickPaceOff && !OwnStatePrediction.Off));
		if (MotionTrace.Recording)
		{
			_inputsThisSecond++;
			if (now - _inputSecondMs >= 1000)
			{
				MotionTrace.Note("inputs", "own", FormattableString.Invariant($"sent={_inputsThisSecond} catchup_held={CatchUpHeld - _catchUpAtSecond} shift_cm={dp.Length * 100:0.0}"));
				_inputSecondMs = now;
				_inputsThisSecond = 0;
				_catchUpAtSecond = CatchUpHeld;
			}
		}
		if (!OwnStatePrediction.Off)
		{
			if (TogglePredicting != null) StateCheck.Record(_seq, OwnStatePrediction.Pack(own));
			OwnStatePrediction.Tick(own, predict: TogglePredicting != null);
		}

		if (now - _statsLogMs >= 30000)
		{
			_statsLogMs = now;
			GameLog.Instance.Info($"own motion: {DescribeCorrection()}; {OwnStatePrediction.Stats}{(_serverStates ? "" : " (the server does not take toggles from inputs)")}; {Pace.Taken} ticks skipped for the server ({Pace.Asked} asked); carried with the deck {DeckCarried} ticks (largest speed change {DeckCarryMax:0.0} m/s), {Correction.DeckVelocityKept} deck velocity corrections left out");
		}
	}

	internal static string DescribeCorrection()
		=> $"{Correction.Acks} acks, error mean {Correction.MeanError * 100:0.0} cm max {Correction.ErrorMax * 100:0.0} cm, largest eased step {Correction.StepMax * 100:0.0} cm, facing off mean {(Correction.Acks == 0 ? 0 : Correction.TurnErrorSum / Correction.Acks):0.00} max {Correction.TurnErrorMax:0.00} deg, {Correction.Snaps} applied at once, {Correction.Rebases} rebased, {Correction.Blinded} not measured after nearby voxel cuts,{_shiftsTurned}/{_shifts} eased shifts kept the tick's turn ({_softShifts} soft), {CatchUpHeld} catch-up ticks held, {_snaps} grounded turns snapped, {_turns} facing corrections";

	private static OwnInput InputOf(Entity own)
	{
		own.Data.TryGet<ControlData>(out var control);
		own.Data.TryGet<AdvancedCharacterControlData>(out var flags);
		bool jump = _jumpPending;
		_jumpPending = false;
		return new OwnInput(new System.Numerics.Vector3(control.Movement.X, control.Movement.Y, control.Movement.Z),
			new System.Numerics.Vector3(control.Rotation.X, control.Rotation.Y, control.Rotation.Z), jump, flags.IsSprinting, flags.IsBoosting,
			OwnStatePrediction.Off ? OwnToggles.None : OwnStatePrediction.TakeForInput());
	}

	private static readonly bool FacingOff = System.Linq.Enumerable.Contains(MpPlugin.LaunchArgs, MpOptions.Prefix + "ownfacing:off");

	private static void Shift(Entity own, WorldTransform? frame, Vec3D dp, System.Numerics.Vector3 dv, System.Numerics.Quaternion turn)
	{
		var shift = new Vector3D(dp.X, dp.Y, dp.Z);
		bool turning = !turn.IsIdentity;
		if (shift != Vector3D.Zero || turning)
		{
			var at = own.Data.GetWorldTransform();
			var q = new Quaternion(turn.X, turn.Y, turn.Z, turn.W);
			var relative = frame is { } f0 ? WorldTransform.GetRelativeTransform(at, f0) : at;
			var orientation = turning ? Quaternion.Normalize(q * relative.Orientation) : relative.Orientation;
			var to = frame is { } f ? f * new WorldTransform(relative.Position + shift, orientation) : new WorldTransform(relative.Position + shift, orientation);
			// The tick's own turn is taken from the SoftTransform when the shift lands (CollectCharacterPrefix): it gets
			// the same correction.
			if (own.Data.TryGet<SoftTransform>(out var soft))
			{
				if (turning)
				{
					var softRelative = frame is { } f1 ? WorldTransform.GetRelativeTransform(soft.Value, f1) : soft.Value;
					softRelative.Orientation = Quaternion.Normalize(q * softRelative.Orientation);
					soft.Value = frame is { } f2 ? f2 * softRelative : softRelative;
					_turns++;
				}
				// The third person camera follows the SoftTransform: moved with the body, or every correction left the
				// camera behind by the correction until the next physics step (a snap by 3 m and more: the camera ended
				// up beside or in front of the character, F19).
				if (!SoftSyncOff) soft.Value.Position = to.Position;
				if (turning || !SoftSyncOff) own.Data.Set(soft);
				// Off by default since 0.18.3 (-y51online:softshift:on tries it): a shift written to the SoftTransform alone is
				// not where Havok's character controller ends up. 0.18.2 eased 1295 such shifts a minute while jetpacking on
				// Freejack's server and the error stayed at 30-46 cm the whole time (every ack the same offset again, the
				// view easing all along): the controller kept its own position and the shift was lost each step, while the
				// correction counted it as applied. Every shift writes the WorldTransform, as before 0.18.2.
				if (SoftShiftOn && !SoftSyncOff && shift.Length() < SoftShiftMeters)
				{
					_softShiftPending = true;
					_shifts++;
					_softShifts++;
					LastShiftMeters = (float)(to.Position - at.Position).Length();
					if (MotionTrace.Recording) MotionTrace.Own(own, "softshift", to, dv == default ? Vector3.Zero : new Vector3(dv.X, dv.Y, dv.Z), $"by {LastShiftMeters * 100:0.0} cm");
					goto Velocity;
				}
			}
			_shiftedTo = to.Position;
			if (MotionTrace.Recording) MotionTrace.Own(own, "shift", to, dv == default ? Vector3.Zero : new Vector3(dv.X, dv.Y, dv.Z), $"by {LastShiftMeters * 100:0.0} cm");
			_shiftPending = true;
			_shifts++;
			LastShiftMeters = (float)(to.Position - at.Position).Length();
			own.Data.SetWorldTransform(to);
		}
		Velocity:
		if (dv != default && own.Data.TryGet<RigidBodyData>(out var body))
		{
			var v = new Vector3(dv.X, dv.Y, dv.Z);
			own.Data.Set(new RigidBodyData(body.LinearVelocity + (frame is { } g ? WorldTransform.TransformDirection(v, g) : v), body.AngularVelocity));
		}
	}

	// Havok and the flying orientation constraint take a written WorldTransform over the SoftTransform the next tick's
	// movement jobs turned, so every eased correction would drop that tick's turn and the facing would drift from the
	// server's. The shifted position keeps the turned facing.
	private static readonly bool SoftSyncOff = System.Linq.Enumerable.Contains(MpPlugin.LaunchArgs, MpOptions.Prefix + "softsync:off");
	private static readonly bool SoftShiftOn = System.Linq.Enumerable.Contains(MpPlugin.LaunchArgs, MpOptions.Prefix + "softshift:on");
	private const double SoftShiftMeters = 0.05;
	private static long _softShifts;
	private static volatile bool _softShiftPending;
	private static volatile bool _shiftPending;
	private static Vector3D _shiftedTo;
	private static long _shifts, _shiftsTurned, _snaps, _turns;

	/// <summary>Camera diagnostic (<see cref="CameraCreep"/>): eased shifts so far, and how far the last one moved the body.</summary>
	internal static long Shifts => _shifts;
	internal static volatile float LastShiftMeters;

	private static void OrientationConstraintPrefix(ref WorldTransform wt, in SoftTransform st, in TransformChangeTypeData tcd)
	{
		if (_shiftPending && (tcd.ChangeTypes & TransformChangeTypeData.ModifiedTransform.HardTransform) != 0 && Vector3D.DistanceSquared(wt.Position, _shiftedTo) <= 1e-6)
			wt.Orientation = st.Value.Orientation;
	}

	// The server alone runs CharacterMovementServerComponent.SnapOrientationForGroundedYaw: on a tick a grounded character
	// turns, its WorldTransform turns with the SoftTransform, so Havok takes the turned pose as a hard transform at once.
	// Here only the SoftTransform turned and the body followed a tick later: the facing lagged the server's by one tick's
	// turn while turning, and the position drifted with it. The player's own body gets the same snap.
	private static volatile float _groundYaw;
	private static Vector3D _ownAt;

	private static void GroundYawPostfix(DEntity dataEntity, in ControlData controlData)
	{
		if (_driving && dataEntity == _own && controlData.Rotation.Y != 0f) _groundYaw = controlData.Rotation.Y;
	}

	private static void CollectCharacterPrefix(ref WorldTransform wt, in SoftTransform st, ref TransformChangeTypeData transformChange)
	{
		if (MotionTrace.Recording && _driving && Vector3D.DistanceSquared(wt.Position, _ownAt) <= 1) MotionTrace.Collect(wt, (int)transformChange.ChangeTypes);
		float yaw = _groundYaw;
		if (yaw != 0f && Vector3D.DistanceSquared(wt.Position, _ownAt) <= 1e-6)
		{
			_groundYaw = 0f;
			if (!_shiftPending)
			{
				// A soft shift this tick: the snap makes this a hard transform, which would drop the shifted SoftTransform.
				if (_softShiftPending) { wt.Position = st.Value.Position; _softShiftPending = false; }
				wt.Orientation *= Quaternion.CreateFromAxisAngle(Vector3.Up, -yaw);
				transformChange.ChangeTypes |= TransformChangeTypeData.ModifiedTransform.HardTransform;
				_snaps++;
				return;
			}
		}
		if (!_shiftPending || (transformChange.ChangeTypes & TransformChangeTypeData.ModifiedTransform.HardTransform) == 0 || Vector3D.DistanceSquared(wt.Position, _shiftedTo) > 1e-6) return;
		_shiftPending = false;
		_shiftsTurned++;
		wt.Orientation = st.Value.Orientation;
	}

	private static void Rebase(Entity own, in OwnPose pose, SessionIds ids, string why)
	{
		var at = ToWorld(pose, ids, out var parent);
		own.Data.SetWorldTransform(at);
		lock (HeldFrames) HeldFrames.Forget(own.DEntity);
		if (!SoftSyncOff && own.Data.TryGet<SoftTransform>(out var soft))
		{
			soft.Value = at;
			own.Data.Set(soft);
		}
		var frame = parent?.Data.GetWorldTransform();
		var v = new Vector3(pose.Velocity.X, pose.Velocity.Y, pose.Velocity.Z);
		var w = new Vector3(pose.Angular.X, pose.Angular.Y, pose.Angular.Z);
		own.Data.Set(new RigidBodyData(frame is { } f ? WorldTransform.TransformDirection(v, f) : v, frame is { } g ? WorldTransform.TransformDirection(w, g) : w));
		GameLog.Instance.Info($"own motion: {why}; moved to {at.Position.X:0.00}, {at.Position.Y:0.00}, {at.Position.Z:0.00}");
		if (MotionTrace.Recording) MotionTrace.Own(own, "rebase", at, Vector3.Zero, why);
	}

	// --- the deck under the character ---

	// A grid someone else flies is played back here: a kinematic body Havok moves to each tick's pose by velocity, a
	// playout delay behind the server's. The character standing on it is this game's own, carried by Havok's character
	// controller at that grid's velocity, which it can follow at most 200 m/s^2 (CharacterPhysics MaxAcceleration): 3.3 m/s
	// a tick. Each change of the played-back grid's speed beyond that (a blend into a late sample, the clock's catch-up, a
	// sample carried past the newest) left the character behind on the deck for a few ticks, and the corrections that
	// followed pushed it back: thrown around at speed. Here the character's velocity takes the change the deck will make
	// over the next step before Havok runs it (the deck's next pose from the playback, MotionSmoothing.DeckNext), so its
	// speed relative to the deck carries on unchanged and Havok only has its own walking to do. -y51online:deckcarry:off.
	private static readonly bool DeckCarryOff = System.Linq.Enumerable.Contains(MpPlugin.LaunchArgs, MpOptions.Prefix + "deckcarry:off");
	private const double DeckTeleportMeters = 7;
	// Havok moves a character at most 310 m/s (MaximumCharacterSpeedLinear): the deck's speed given to the character
	// stays under that. The character's velocity holds the deck speed last given (_carried), so a tick the deck runs
	// faster than that (a catch-up at top speed) costs the character only that tick's excess, not a kick back after it.
	private const float CarryLimit = 305;
	private static Entity? _deckWas;
	private static long _deckWasFrame;
	private static Vector3 _carried;
	private static bool _carriedValid;
	private static WorldTransform _deckWasAt;
	internal static long DeckCarried;
	internal static double DeckCarryMax;

	// Standing or walking on a grid that moves (not one at rest, not a planet or station).
	private static Entity? DeckUnder(Entity own)
	{
		var under = Parent(own, motionParent: false);
		if (under == null || EngineCodec.IsDead(under) || !under.Data.Has<DynamicTag>()) return null;
		return under;
	}

	// The jetpack on, or in the air (no ground under the feet).
	private static bool Flying(Entity own)
		=> own.TryGet<CharacterMovementComponent>()?.CurrentMovementState == CharacterMovementState.Jetpack
			|| own.Data.TryGet<Keen.VRage.Physics.Character.CharacterPhysicsState>(out var state) && !state.Supported;

	private static bool OnMovingDeck(Entity own)
		=> DeckUnder(own) is { } deck && deck.Data.TryGet<RigidBodyData>(out var b) && (b.LinearVelocity.LengthSquared() >= 0.25f || b.AngularVelocity.LengthSquared() >= 1e-3f);

	// -y51online:deckvelocity:off: the server's velocity for an input on a moving deck is corrected as anywhere else.
	// On a deck the server's velocity is the deck's at the moment it ran that input, this game's the played-back deck's a
	// playout delay earlier: a ship speeding up at 20 m/s^2 put them 5 m/s apart, and each ack's velocity correction kicked
	// the character along the deck (Havok's controller sets a standing character's velocity from the deck every tick
	// anyway). There only the position, in the deck's frame, is corrected.
	private static readonly bool DeckVelocityOff = System.Linq.Enumerable.Contains(MpPlugin.LaunchArgs, MpOptions.Prefix + "deckvelocity:off");

	private static void CarryOnDeck(Entity own)
	{
		var deck = DeckCarryOff ? null : DeckUnder(own);
		MotionSmoothing.Deck = deck;
		var was = _deckWas;
		var wasAt = _deckWasAt;
		// Only from the tick right before: a tick left out (a skip, a seat, the server moving this character) would count
		// several ticks of the deck's travel as one step and kick the character backwards at its speed times the gap.
		bool consecutive = _deckWasFrame + 1 == Frames;
		_deckWas = deck;
		_deckWasFrame = Frames;
		if (deck == null) return;
		var now = deck.Data.GetWorldTransform();
		_deckWasAt = now;
		if (!consecutive || !ReferenceEquals(deck, was) || !MotionSmoothing.DeckNext(deck, out var next, out _, out _) || !own.Data.TryGet<RigidBodyData>(out var body)) { _carriedValid = false; return; }
		// The character's point on the deck, carried by the deck's last step and by its next one.
		var at = own.Data.GetWorldTransform().Position;
		var onDeck = WorldTransform.TransformInv(at, now);
		var before = WorldTransform.Transform(in onDeck, in wasAt);
		var after = WorldTransform.Transform(in onDeck, in next);
		// A deck put somewhere (a teleport, a jump): not a speed to carry.
		if ((at - before).LengthSquared() > DeckTeleportMeters * DeckTeleportMeters || (after - at).LengthSquared() > DeckTeleportMeters * DeckTeleportMeters) { _carriedValid = false; return; }
		var given = _carriedValid ? _carried : Limited((Vector3)((at - before) / TickSeconds));
		// Havok collides the character with the deck where it drives it: the step, not the samples' velocity.
		var coming = Limited((Vector3)((after - at) / TickSeconds));
		_carried = coming;
		_carriedValid = true;
		var change = coming - given;
		float size = change.Length();
		if (size < 1e-3f) return;
		own.Data.Set(new RigidBodyData(body.LinearVelocity + change, body.AngularVelocity));
		DeckCarried++;
		DeckCarryMax = Math.Max(DeckCarryMax, size);
		if (MotionTrace.Recording) MotionTrace.Own(own, "carry", own.Data.GetWorldTransform(), change, $"deck speed {coming.Length():0.0} m/s");
	}

	private static Vector3 Limited(Vector3 v)
	{
		float speed = v.Length();
		return speed > CarryLimit ? v * (CarryLimit / speed) : v;
	}

	private static Entity? OwnCharacter(Session session)
	{
		if (_ownEntity is { } known && Alive(known)) return known;
		_ownEntity = null;
		long now = MpClock.NowMs();
		if (now < _nextScanMs) return null;
		_nextScanMs = now + 250;
		long me = LocalIdentity(session);
		if (me == 0) return null;
		foreach (Entity character in session.GetEntitiesOfType<CharacterLifeComponent>())
			if (Alive(character) && TeamPermissions.OwnerOf(character) == me) return _ownEntity = character;
		return null;
	}

	// What the character stands on, else what Keen moves it with.
	private static Entity? Parent(Entity character, bool motionParent = true)
	{
		Entity? parent = null;
		if (character.Data.TryGet<CharacterSupportData>(out var support) && support.CharacterContacts.Count > 0)
			foreach (var contact in support.CharacterContacts)
				if (contact.IsSupportContact && Entity.TryGetFromDataEntity(new DEntityContext(character.Scene, contact.Entity)) is { } touched) { parent = touched; break; }
		if (parent == null && motionParent && character.Data.TryGet<MotionParent>(out var keenParent))
			parent = Entity.TryGetFromDataEntity(new DEntityContext(character.Scene, keenParent.Parent));
		for (int depth = 0; parent?.TryGet<HierarchyComponent>()?.Parent is { } up && depth < 16; depth++) parent = up;
		return parent;
	}

	// Both sides compare in a moving grid's frame (walking on a flying ship), else in world space: a resting grid or the
	// ground would only make the frame flip on every step and jump.
	// The frame both sides compare in, decided the same way from each side's own contacts. A dynamic body (a ship) the
	// character stands on is its frame even at rest: a ship that starts moving under the character switched frames on
	// the server a round trip before this game saw its copy move, and the 30 acks of that round trip disagreeing forced a
	// rebase. The ship stays the frame for a second after the feet leave it (steps, jumps) instead of falling back to
	// Keen's MotionParent, whose copy here comes late; MotionParent only counts with nothing under the feet and none
	// held, and only while it moves. Static bodies (planets, flora, stations) are world space.
	// -y51online:gridframe:off: the support or MotionParent while it moves, as before.
	private static readonly bool GridFrameOff = System.Linq.Enumerable.Contains(MpPlugin.LaunchArgs, MpOptions.Prefix + "gridframe:off");
	private static readonly FrameHold<DEntity, Entity> HeldFrames = new();

	private static (uint Id, WorldTransform? Frame) FrameOf(Entity character, SessionIds ids)
	{
		if (GridFrameOff)
		{
			if (Parent(character) is not { } old || EngineCodec.IsDead(old) || !old.Data.TryGet<RigidBodyData>(out var b)
				|| b.LinearVelocity.LengthSquared() < 0.0025f && b.AngularVelocity.LengthSquared() < 1e-4f || !ids.TryGet(old, out uint oldId))
				return (0, null);
			return (oldId, old.Data.GetWorldTransform());
		}
		var parent = Parent(character, motionParent: false);
		if (parent != null && (EngineCodec.IsDead(parent) || !parent.Data.Has<DynamicTag>())) parent = null;
		lock (HeldFrames) parent = HeldFrames.Choose(character.DEntity, parent, e => !EngineCodec.IsDead(e));
		// Nothing under the feet and none held (out of a seat, flying near a ship): Keen's MotionParent while it moves,
		// as before.
		if (parent == null && Parent(character) is { } keen && !EngineCodec.IsDead(keen) && keen.Data.TryGet<RigidBodyData>(out var kb)
			&& (kb.LinearVelocity.LengthSquared() >= 0.0025f || kb.AngularVelocity.LengthSquared() >= 1e-4f))
			parent = keen;
		if (parent == null || !ids.TryGet(parent, out uint id)) return (0, null);
		return (id, parent.Data.GetWorldTransform());
	}

	private static WorldTransform ToWorld(in OwnPose pose, SessionIds ids, out Entity? parent)
	{
		var at = new WorldTransform(new Vector3D(pose.Position.X, pose.Position.Y, pose.Position.Z), new Quaternion(pose.Rotation.X, pose.Rotation.Y, pose.Rotation.Z, pose.Rotation.W));
		parent = null;
		if (pose.Parent == 0 || !ids.TryGet(pose.Parent, out parent) || EngineCodec.IsDead(parent)) return at;
		return parent.Data.GetWorldTransform() * at;
	}

	// --- server ---

	private sealed class Player
	{
		public readonly object Gate = new();
		// Under Gate (network thread offers, server thread takes).
		public double Tokens = OwnMotionRules.Burst;
		public long TokensAt, LastInputMs, Dropped;
		public ushort Epoch;
		public bool OldEpoch, Toggles, Paced;
		// -y51online:drain:off (server): one merge every 8 ticks whatever the depth, as in 0.18.1 (Sync.OwnInputBuffer.Adaptive).
		public readonly OwnInputBuffer Inputs = new(adaptive: !DrainOff);

		// Server thread only.
		public Entity? Character;
		public bool Held, Driving, Sprint, Boost, BoostHeld, Holding, ResyncHeld;
		public int HeldTicks;
		public Frozen? Frozen;
		public byte Expected;
		public uint AppliedSeq;
		public long Resyncs, LogMs, ResyncMs, RealMs, RealSeen, HeldSinceMs;
		public int DepthPeak, SkipsToAsk;
		public readonly SkipAsker Asker = new();
	}

	private static readonly bool DrainOff = System.Linq.Enumerable.Contains(MpPlugin.LaunchArgs, MpOptions.Prefix + "drain:off");

	private static readonly ConcurrentDictionary<ushort, Player> Players = new();

	// Characters this server currently moves from their players' inputs; replaced whole each tick, read by the prefixes.
	// A DEntity is per scene: a co-op host's client scene only has kinematic characters, which the hooked jobs skip.
	private static volatile HashSet<DEntity> _drivenServer = new();

	// Driven characters whose players' games put their toggles in their inputs (Keen's own toggle requests are ignored).
	private static volatile HashSet<DEntity> _togglesServer = new();

	private static readonly ConcurrentQueue<Entity> TeleportedQueue = new();

	// No input for this long: the connection is in trouble, the character goes back to Keen's own ControlData. Longer
	// than a server stall, after which the inputs that queued up meanwhile arrive together.
	private const long StaleMs = 2000;

	// Inputs arrive but none is ever the one due: the queue is out of step with the player's game.
	private const long NoRealInputMs = 1000;

	// -y51online:togglegate:off: Keen's toggle requests are ignored only on ticks the inputs moved the character (0.18.1).
	private static readonly bool ToggleGateOff = System.Linq.Enumerable.Contains(MpPlugin.LaunchArgs, MpOptions.Prefix + "togglegate:off");

	// Set while this code itself makes the server's jump, sprint and boost requests.
	private static bool _ourRequest;

	/// <summary>Network thread: a player's newest inputs.</summary>
	public static void Offer(ushort clientId, byte[] data)
	{
		if (OwnInputs.Decode(data) is not { } inputs) return;
		var player = Players.GetOrAdd(clientId, _ => new Player { TokensAt = MpClock.NowMs() });
		long now = MpClock.NowMs();
		lock (player.Gate)
		{
			if (!OwnMotionRules.Take(ref player.Tokens, ref player.TokensAt, now)) { player.Dropped++; return; }
			if (inputs.Epoch != player.Epoch)
			{
				player.OldEpoch = true;
				return;
			}
			player.LastInputMs = now;
			player.Toggles |= inputs.Toggles;
			player.Paced |= inputs.Paced && !TickPaceOff;
			for (int i = inputs.NewestFirst.Length - 1; i >= 0; i--) player.Inputs.Offer(inputs.Newest - (uint)i, inputs.NewestFirst[i]);
		}
	}

	/// <summary>Server thread, after each update: answers the input the last tick applied and applies the next one.</summary>
	public static void ServerTick(Session session, IEnumerable<(ushort ClientId, Entity? Character)> players, HostSession host, SessionIds ids)
	{
		long now = MpClock.NowMs();
		long stamp = System.Diagnostics.Stopwatch.GetTimestamp();
		// Ticks run back to back: the server catches up after a hitch. The inputs sent meanwhile wait unread (direct-IP
		// players are read with the game's network poll, which runs once a frame, not once a tick) or with SE2 Online's
		// network thread, so the catch-up ticks guessed and the inputs arriving behind them became extra ticks (metres off
		// at speed).
		bool catchingUp = stamp - _lastServerTick < System.Diagnostics.Stopwatch.Frequency * CatchUpMs / 1000;
		_lastServerTick = stamp;
		if (MotionTrace.Recording)
		{
			_serverTicksSecond++;
			if (catchingUp) _serverCatchUps++;
			if (now - _serverSecondMs >= 1000)
			{
				MotionTrace.Note("server", "server", FormattableString.Invariant($"ticks={_serverTicksSecond} catchup={_serverCatchUps}"));
				_serverSecondMs = now;
				_serverTicksSecond = _serverCatchUps = 0;
			}
		}
		var teleported = new HashSet<Entity>();
		while (TeleportedQueue.TryDequeue(out var entity)) teleported.Add(entity);
		var driven = new HashSet<DEntity>();
		var toggles = new HashSet<DEntity>();
		var online = new HashSet<ushort>();
		foreach (var (clientId, character) in players)
		{
			online.Add(clientId);
			var player = Players.GetOrAdd(clientId, _ => new Player { TokensAt = now });
			if (character == null || !Alive(character))
			{
				player.Character = null;
				player.Driving = false;
				continue;
			}
			if (!ReferenceEquals(player.Character, character))
			{
				player.Character = character;
				player.Held = false;
				player.Driving = false;
				Resync(host, clientId, player, character, ids, now, "a new body");
				continue;
			}
			if (Seated(character))
			{
				if (player.Driving) StopDriving(player, character);
				player.Held = true;
				continue;
			}
			if (player.Held)
			{
				player.Held = false;
				Resync(host, clientId, player, character, ids, now, "left a seat");
				continue;
			}
			if (teleported.Contains(character))
			{
				StopDriving(player, character);
				Resync(host, clientId, player, character, ids, now, "the server moved this character");
				continue;
			}
			// Keen's own toggle requests from this player's game are left to its inputs on every tick it sends them, not
			// only on ticks its inputs moved the character; not on a resync tick (above), whose toggle input is dropped with its
			// epoch, so Keen's request is the one that counts there (Sync.OwnState.IgnoresKeenToggle).
			if (!ToggleGateOff)
			{
				bool togglesInInputs;
				long inputAt;
				lock (player.Gate) { togglesInInputs = player.Toggles; inputAt = player.LastInputMs; }
				if (OwnState.IgnoresKeenToggle(togglesInInputs, now - inputAt, Seated(character), serverOwnCall: false)) toggles.Add(character.DEntity);
			}
			if (player.Frozen is { } frozen) Thaw(character, frozen);
			bool oldEpoch;
			lock (player.Gate)
			{
				oldEpoch = player.OldEpoch;
				player.OldEpoch = false;
			}
			// The resync may have reached the player's game before it was ready for it.
			if (oldEpoch && now - player.ResyncMs >= 1000)
			{
				player.ResyncMs = now;
				host.SendOwnMotion(clientId, PoseOf(character, 0, player.Epoch, ids, player.Toggles), reliable: true);
			}
			if (player.AppliedSeq != 0)
			{
				var pose = PoseOf(character, player.AppliedSeq, player.Epoch, ids, player.Toggles);
				if (pose.State is byte after && OwnState.ForcedBy(player.Expected, after, player.BoostHeld)) pose = pose with { State = (byte)(after | OwnState.Forced) };
				// Skips ride one per ack; a lost ack loses its skip, and the queue asks again later.
				if (player.SkipsToAsk > 0 && pose.State is byte asked)
				{
					pose = pose with { State = (byte)(asked | OwnState.Skip) };
					player.SkipsToAsk--;
				}
				host.SendOwnMotion(clientId, pose, reliable: false);
				player.AppliedSeq = 0;
			}
			if (player.ResyncHeld)
			{
				bool waiting;
				lock (player.Gate) waiting = !player.Inputs.HasNext;
				if (waiting && now - player.HeldSinceMs < ResyncHoldMs)
				{
					Hold(character);
					driven.Add(character.DEntity);
					if (player.Toggles) toggles.Add(character.DEntity);
					continue;
				}
				player.ResyncHeld = false;
				player.Frozen = null;
				GameLog.Instance.Info($"own motion: ClientId {clientId}: held {now - player.HeldSinceMs} ms at the resync pose{(waiting ? ", no input came" : " for its first input")}");
			}
			if (catchingUp) WaitForInput(player, now);
			if (player.Driving && ShouldHold(player, catchingUp, now))
			{
				player.Frozen ??= Freeze(character);
				Hold(character);
				driven.Add(character.DEntity);
				if (player.Toggles) toggles.Add(character.DEntity);
				continue;
			}
			if (player.Frozen != null)
			{
				_holdRuns++;
				player.Frozen = null;
			}
			(OwnInput Input, uint Ack)? next = null;
			bool resting = character.Data.TryGet<RigidBodyData>(out var body) && body.LinearVelocity.LengthSquared() < 0.09f;
			lock (player.Gate)
			{
				if (now - player.LastInputMs < StaleMs) next = player.Inputs.Take(resting, SlowMergeOff ? 0 : RelativeSpeed(character, body), player.Paced && player.Toggles);
				else player.Inputs.Reset();
			}
			if (next is not { } take)
			{
				if (player.Driving) StopDriving(player, character);
				continue;
			}
			long real, lastInput;
			lock (player.Gate) { real = player.Inputs.Real; lastInput = player.LastInputMs; }
			if (real != player.RealSeen || !player.Driving)
			{
				player.RealSeen = real;
				player.RealMs = now;
			}
			else if (now - player.RealMs > NoRealInputMs && now - lastInput < 250)
			{
				GameLog.Instance.Warn($"own motion: ClientId {clientId}: none of its inputs applied for {now - player.RealMs} ms while it sends them; its character goes back to Keen's movement until the queue starts over");
				StopDriving(player, character);
				lock (player.Gate) player.Inputs.Reset();
				continue;
			}
			Apply(player, character, take.Input);
			player.AppliedSeq = take.Ack;
			lock (player.Gate)
			{
				player.DepthPeak = Math.Max(player.DepthPeak, player.Inputs.Depth);
				if (player.Paced && player.Toggles) player.SkipsToAsk += player.Asker.Observe(player.Inputs.Surplus, player.Inputs.Depth, now);
			}
			player.Driving = true;
			driven.Add(character.DEntity);
			if (player.Toggles) toggles.Add(character.DEntity);
			if (now - player.LogMs >= 30000)
			{
				player.LogMs = now;
				var b = player.Inputs;
				lock (player.Gate)
				{
					GameLog.Instance.Info($"own motion: ClientId {clientId}: {b.Applied} ticks, {b.Real} of the player's inputs applied, {b.Depth} waiting, {b.Guessed} guessed ({b.Late} late, {b.Rewound} turned into extra ticks), {b.Skipped} idle skipped, {b.Merged} merged ({b.PutOff} ticks put off while fast), deepest queue {player.DepthPeak} since the last report, {player.Asker.Asked} tick skips asked{(player.Paced ? "" : " (its game does not skip)")}, {player.Dropped} over the rate, {player.Resyncs} resyncs, catch-up ticks waited for input {_inputWaits} times ({_inputWaitsServed} got it), {_holdRuns} catch-ups held still for {_heldTicks} ticks; {OwnStatePrediction.Blocked} of Keen's own toggle requests left to the inputs (all players)");
					player.DepthPeak = 0;
				}
			}
		}
		foreach (var gone in Players.Keys)
			if (!online.Contains(gone)) Players.TryRemove(gone, out _);
		// Held frames of players who left (or stopped being driven) would keep their ships' entities.
		lock (HeldFrames) HeldFrames.KeepOnly(driven);
		_drivenServer = driven;
		_togglesServer = toggles;
	}

	// What Keen's own server does with a client's ControlData and its jump/sprint/boost requests, for this tick's input.
	private static void Apply(Player player, Entity character, in OwnInput input)
	{
		var movement = new Vector3(input.Movement.X, input.Movement.Y, input.Movement.Z);
		var rotation = new Vector3(input.Rotation.X, input.Rotation.Y, input.Rotation.Z);
		character.Data.Set(new ControlData { Movement = movement, Rotation = rotation });
		character.Data.Set(new AngularControlData { TargetAngularVelocity = rotation });
		if (player.Toggles)
		{
			player.Expected = OwnState.Expect(OwnStatePrediction.Pack(character), input.Toggles);
			player.BoostHeld = input.Boost;
		}
		if (input.Toggles != OwnToggles.None && player.Toggles) OwnStatePrediction.ApplyServer(character, input.Toggles);
		if (character.TryGet<CharacterMovementServerComponent>() is not { } requests) return;
		_ourRequest = true;
		try
		{
			if (input.Jump) requests.RequestJump();
			if (input.Sprint != player.Sprint) requests.RequestSprint(input.Sprint);
			if (input.Boost != player.Boost) requests.RequestBoost(input.Boost);
		}
		finally { _ourRequest = false; }
		player.Sprint = input.Sprint;
		player.Boost = input.Boost;
	}

	// A catch-up after a server hitch runs the ticks the player's game already ran, faster than its inputs can arrive
	// (they are still on the way). Guessing those ticks and applying the real inputs after them as extra ticks put the
	// character a stall's worth of movement ahead (metres at boost speed). Instead the character holds still in time:
	// each tick without its input it is put back where it stood, until the inputs are here again with the buffer's
	// cushion, and runs them from there exactly as the player's game did. -y51online:catchuphold:off guesses instead.
	private static readonly bool HoldOff = System.Linq.Enumerable.Contains(MpPlugin.LaunchArgs, MpOptions.Prefix + "catchuphold:off");
	private const int MaxHeldTicks = 90;
	private static long _heldTicks, _holdRuns;

	private sealed record Frozen(Entity? Parent, WorldTransform At, Vector3 Velocity, Vector3 Angular);

	private static bool ShouldHold(Player player, bool catchingUp, long now)
	{
		if (HoldOff) return false;
		lock (player.Gate)
		{
			bool ready = player.Inputs.HasNext && (!player.Holding || player.Inputs.Depth >= OwnInputBuffer.Target);
			if (ready || player.HeldTicks >= MaxHeldTicks || now - player.LastInputMs > 250)
			{
				player.Holding = false;
				player.HeldTicks = 0;
				return false;
			}
			if (!player.Holding && !catchingUp) return false;
			player.Holding = true;
			player.HeldTicks++;
			_heldTicks++;
			return true;
		}
	}

	private static Frozen Freeze(Entity character)
	{
		var at = character.Data.GetWorldTransform();
		character.Data.TryGet<RigidBodyData>(out var body);
		var parent = Parent(character);
		if (parent == null || EngineCodec.IsDead(parent)) return new Frozen(null, at, body.LinearVelocity, body.AngularVelocity);
		var frame = parent.Data.GetWorldTransform();
		// Relative to the deck's own motion at the character's point: thawed on a deck that changed speed meanwhile (a 0.6 s
		// catch-up on a ship speeding up), it gets the deck's new speed, not the old one.
		var relative = body.LinearVelocity - PointVelocity(parent, at.Position);
		return new Frozen(parent, WorldTransform.GetRelativeTransform(at, frame), WorldTransform.TransformDirectionInv(relative, frame), WorldTransform.TransformDirectionInv(body.AngularVelocity, frame));
	}

	// A body's velocity at a point (its linear velocity is its centre of mass's).
	private static Vector3 PointVelocity(Entity body, Vector3D point)
	{
		if (!body.Data.TryGet<RigidBodyData>(out var b)) return Vector3.Zero;
		var com = body.Data.TryGet<RigidBodyMassProperties>(out var mass) ? WorldTransform.Transform((Vector3D)mass.CenterOfMass, body.Data.GetWorldTransform()) : body.Data.GetWorldTransform().Position;
		return b.LinearVelocity + Vector3.Cross(b.AngularVelocity, (Vector3)(point - com));
	}

	private static void Thaw(Entity character, Frozen frozen)
	{
		WorldTransform at;
		if (frozen.Parent is { } parent && !EngineCodec.IsDead(parent))
		{
			var frame = parent.Data.GetWorldTransform();
			at = frame * frozen.At;
			character.Data.SetWorldTransform(at);
			character.Data.Set(new RigidBodyData(WorldTransform.TransformDirection(frozen.Velocity, frame) + PointVelocity(parent, at.Position), WorldTransform.TransformDirection(frozen.Angular, frame)));
		}
		else
		{
			at = frozen.At;
			character.Data.SetWorldTransform(at);
			character.Data.Set(new RigidBodyData(frozen.Velocity, frozen.Angular));
		}
		// The third person camera follows the SoftTransform: moved with the body, as Rebase does (softsync).
		if (!SoftSyncOff && character.Data.TryGet<SoftTransform>(out var soft))
		{
			soft.Value = at;
			character.Data.Set(soft);
		}
		if (MotionTrace.Recording) MotionTrace.Own(character, "thaw", at, Vector3.Zero);
	}

	// One tick standing still in time: no input, no momentum, and the boost charge (game time) waits too.
	private static void Hold(Entity character)
	{
		character.Data.Set(new ControlData());
		character.Data.Set(new AngularControlData());
		character.Data.Set(new RigidBodyData(Vector3.Zero, Vector3.Zero));
		if (character.TryGet<ServerJetpackBoostComponent>() is { BoostState: CharacterBoostState.Charging } boost)
		{
			var tick = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 60);
			var charge = boost.BoostChargeData;
			boost.BoostChargeData = charge with { StartTime = charge.StartTime + tick, EndTime = charge.EndTime + tick };
		}
	}

	private const long CatchUpMs = 8, InputWaitMs = 4;

	private static readonly bool SlowMergeOff = System.Linq.Enumerable.Contains(MpPlugin.LaunchArgs, MpOptions.Prefix + "slowmerge:off");

	private static float RelativeSpeed(Entity character, in RigidBodyData body)
		=> Parent(character) is { } support && !EngineCodec.IsDead(support) && support.Data.TryGet<RigidBodyData>(out var under)
			? (body.LinearVelocity - under.LinearVelocity).Length() : body.LinearVelocity.Length();

	// After a resync the player's game runs its inputs from the pose it was sent, the moment it gets it; the server
	// waits for the first of them with the character held still at that pose, so both run the same inputs from the same
	// state. Moving it meanwhile put the server a round trip ahead: a character put 1.5 m above the ground had landed
	// there before its player's first input, 147 cm off the prediction. Only server-made resyncs hold (a new body, a seat
	// left, a teleport), once each, and never longer than ResyncHoldMs (a round trip with margin): a game that sends no
	// input is let go. -y51online:resynchold:off moves it at once.
	private static readonly bool ResyncHoldOff = System.Linq.Enumerable.Contains(MpPlugin.LaunchArgs, MpOptions.Prefix + "resynchold:off");
	private const long ResyncHoldMs = 750;
	private static long _lastServerTick, _inputWaits, _inputWaitsServed;

	// A catch-up tick whose input is not here yet waits a few milliseconds for the network thread before guessing.
	private static void WaitForInput(Player player, long now)
	{
		lock (player.Gate)
			if (player.Inputs.HasNext || now - player.LastInputMs > StaleMs) return;
		_inputWaits++;
		long until = System.Diagnostics.Stopwatch.GetTimestamp() + System.Diagnostics.Stopwatch.Frequency * InputWaitMs / 1000;
		while (System.Diagnostics.Stopwatch.GetTimestamp() < until)
		{
			System.Threading.Thread.Sleep(0);
			lock (player.Gate)
				if (player.Inputs.HasNext) { _inputWaitsServed++; return; }
		}
	}

	private static void StopDriving(Player player, Entity character)
	{
		player.Driving = false;
		player.AppliedSeq = 0;
		player.Frozen = null;
		player.Holding = false;
		Apply(player, character, default);
	}

	private static OwnPose PoseOf(Entity character, uint seq, ushort epoch, SessionIds ids, bool state)
	{
		var at = character.Data.GetWorldTransform();
		character.Data.TryGet<RigidBodyData>(out var body);
		Vector3 v = body.LinearVelocity, w = body.AngularVelocity;
		var (parentId, frame) = FrameOf(character, ids);
		if (frame is { } f)
		{
			at = WorldTransform.GetRelativeTransform(at, f);
			v = WorldTransform.TransformDirectionInv(v, f);
			w = WorldTransform.TransformDirectionInv(w, f);
		}
		return new OwnPose(seq, epoch, parentId, new Vec3D(at.Position.X, at.Position.Y, at.Position.Z),
			new System.Numerics.Quaternion(at.Orientation.X, at.Orientation.Y, at.Orientation.Z, at.Orientation.W),
			new System.Numerics.Vector3(v.X, v.Y, v.Z), new System.Numerics.Vector3(w.X, w.Y, w.Z), state ? OwnStatePrediction.Pack(character) : null);
	}

	/// <summary>The server's pose wins: the player's game jumps there and inputs sent before it are ignored (new epoch).</summary>
	private static void Resync(HostSession host, ushort clientId, Player player, Entity character, SessionIds ids, long now, string why)
	{
		ushort epoch;
		lock (player.Gate)
		{
			epoch = ++player.Epoch;
			player.Inputs.Reset();
			player.Frozen = null;
			player.Holding = false;
		}
		player.SkipsToAsk = 0;
		lock (HeldFrames) HeldFrames.Forget(character.DEntity);
		player.AppliedSeq = 0;
		player.Resyncs++;
		player.ResyncMs = now;
		host.SendOwnMotion(clientId, PoseOf(character, 0, epoch, ids, player.Toggles), reliable: true);
		player.ResyncHeld = !ResyncHoldOff;
		if (player.ResyncHeld)
		{
			player.Frozen = Freeze(character);
			player.HeldSinceMs = now;
			Hold(character);
		}
		GameLog.Instance.Info($"own motion: ClientId {clientId}: {why}; resync (epoch {epoch})");
	}

	/// <summary>The rig and logs: whether the server moves a player's character from their inputs, and how its inputs fared.</summary>
	internal static (long Applied, long Guessed, long Merged, int Depth, bool Driven) StatsOf(ushort clientId)
	{
		if (!Players.TryGetValue(clientId, out var p)) return (0, 0, 0, 0, false);
		lock (p.Gate) return (p.Inputs.Applied, p.Inputs.Guessed, p.Inputs.Merged, p.Inputs.Depth, p.Character != null && _drivenServer.Contains(p.Character.DEntity));
	}

	/// <summary>The server moves <paramref name="character"/> from inputs that carry its player's toggles.</summary>
	internal static bool TogglesFromInputs(Entity character) => _togglesServer.Contains(character.DEntity);

	/// <summary>Keen's ControlData from this player's game would overwrite the input this server applies for the tick.</summary>
	internal static bool ReplacesClientData(Type data, Entity? target)
		=> target != null && (data == typeof(ControlData) || data == typeof(AngularControlData)) && _drivenServer.Contains(target.DEntity);

	// Keen's requests from the player's game arrive at their own time; the copy inside the inputs is applied in order.
	private static bool KeenRequestPrefix(CharacterMovementServerComponent __instance) => _ourRequest || !_drivenServer.Contains(__instance.Entity.DEntity);

	private static void SyncMotionPrefix(Entity entity, OptionalWritable<Teleported> teleported)
	{
		if (teleported.HasValue && teleported.Value.Value && _drivenServer.Contains(entity.DEntity)) TeleportedQueue.Enqueue(entity);
	}
}
