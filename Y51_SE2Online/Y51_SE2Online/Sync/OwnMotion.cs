using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using Y51_SE2Online.Net;

namespace Y51_SE2Online.Sync;

/// <summary>Toggles a player pressed on a tick: Keen's jetpack (movement state), crouch and dampener requests.</summary>
[Flags]
public enum OwnToggles : byte { None = 0, Jetpack = 1, Crouch = 2, Dampeners = 4, All = 7 }

/// <summary>
/// One simulation tick of a player's own character input: Keen's ControlData (movement keys, look/turn deltas) and the
/// jump, sprint, boost and toggle requests that tick used. The only thing a player's game sends about its own movement.
/// </summary>
public readonly record struct OwnInput(Vector3 Movement, Vector3 Rotation, bool Jump, bool Sprint, bool Boost, OwnToggles Toggles = OwnToggles.None)
{
	/// <summary>Keen's own input path (EntityMovementExtensions.UpdateControlData) builds each movement axis from two keys
	/// (0..1 each) and roll the same way; pitch and yaw are mouse deltas per tick, half a turn is already a flick.</summary>
	public const float MaxTurn = MathF.PI;

	public OwnInput Sanitized() => new(new Vector3(Axis(Movement.X, 1), Axis(Movement.Y, 1), Axis(Movement.Z, 1)),
		new Vector3(Axis(Rotation.X, MaxTurn), Axis(Rotation.Y, MaxTurn), Axis(Rotation.Z, 1)), Jump, Sprint, Boost, Toggles & OwnToggles.All);

	public bool Idle => Movement == Vector3.Zero && Rotation == Vector3.Zero && !Jump && Toggles == OwnToggles.None;

	private static float Axis(float v, float max) => float.IsFinite(v) ? Math.Clamp(v, -max, max) : 0;
}

/// <summary>
/// Player to server: the newest inputs, newest first, so one lost packet loses none of them. Flag bit
/// <see cref="TogglesFlag"/> on every input says this game puts its toggles in its inputs and predicts them (servers
/// before 0.16.4 ignore it and keep taking Keen's own toggle requests).
/// </summary>
public static class OwnInputs
{
	public const int Redundancy = 6, InputSize = 25, HeaderSize = 8;
	public const byte TogglesFlag = 0x80;

	public static byte[] Encode(ushort epoch, uint newest, IReadOnlyList<OwnInput> newestFirst, bool toggles = true)
	{
		int count = Math.Min(newestFirst.Count, Redundancy);
		var w = new WireWriter(HeaderSize + count * InputSize).U8((byte)SyncMsg.OwnMotion).U16(epoch).U32(newest).U8((byte)count);
		for (int i = 0; i < count; i++)
		{
			var input = newestFirst[i];
			foreach (float f in new[] { input.Movement.X, input.Movement.Y, input.Movement.Z, input.Rotation.X, input.Rotation.Y, input.Rotation.Z })
				w.I32(BitConverter.SingleToInt32Bits(f));
			w.U8((byte)((input.Jump ? 1 : 0) | (input.Sprint ? 2 : 0) | (input.Boost ? 4 : 0) | ((int)(input.Toggles & OwnToggles.All) << 3) | (toggles ? TogglesFlag : 0)));
		}
		return w.ToArray();
	}

	/// <summary>Null when <paramref name="d"/> is not a whole input message.</summary>
	public static (ushort Epoch, uint Newest, OwnInput[] NewestFirst, bool Toggles)? Decode(ReadOnlySpan<byte> d)
	{
		if (d.Length < HeaderSize || d[0] != (byte)SyncMsg.OwnMotion) return null;
		var r = new WireReader(d.Slice(1));
		ushort epoch = r.U16();
		uint newest = r.U32();
		int count = r.U8();
		if (count > Redundancy || d.Length != HeaderSize + count * InputSize) return null;
		var inputs = new OwnInput[count];
		bool toggles = false;
		for (int i = 0; i < count; i++)
		{
			var movement = new Vector3(F(ref r), F(ref r), F(ref r));
			var rotation = new Vector3(F(ref r), F(ref r), F(ref r));
			byte flags = r.U8();
			inputs[i] = new OwnInput(movement, rotation, (flags & 1) != 0, (flags & 2) != 0, (flags & 4) != 0, (OwnToggles)((flags >> 3) & (int)OwnToggles.All));
			toggles |= (flags & TogglesFlag) != 0;
		}
		return (epoch, newest, inputs, toggles);
		static float F(ref WireReader r) => BitConverter.Int32BitsToSingle(r.I32());
	}
}

/// <summary>
/// Server to player: where the server has the player's own character after it applied input <see cref="Seq"/> (an ack,
/// unreliable, every tick), or with <see cref="Seq"/> 0 a resync (reliable, after a teleport, a seat or a new body). With a
/// parent (a moving grid, by Keen network id) the position and velocities are in that grid's frame. A newer
/// <see cref="Epoch"/> means the player's game jumps to this pose. <see cref="State"/> (only to games that predict their
/// toggles, one byte more) is the character's movement state after that input, see <see cref="OwnState"/>.
/// </summary>
public readonly record struct OwnPose(uint Seq, ushort Epoch, uint Parent, Vec3D Position, Quaternion Rotation, Vector3 Velocity, Vector3 Angular, byte? State = null)
{
	public const int Size = 75, SizeWithState = 76;

	public byte[] Encode()
	{
		var w = new WireWriter(State.HasValue ? SizeWithState : Size).U8((byte)SyncMsg.OwnMotion).U32(Seq).U16(Epoch).U32(Parent)
			.I64(BitConverter.DoubleToInt64Bits(Position.X)).I64(BitConverter.DoubleToInt64Bits(Position.Y)).I64(BitConverter.DoubleToInt64Bits(Position.Z));
		foreach (float f in new[] { Rotation.X, Rotation.Y, Rotation.Z, Rotation.W, Velocity.X, Velocity.Y, Velocity.Z, Angular.X, Angular.Y, Angular.Z })
			w.I32(BitConverter.SingleToInt32Bits(f));
		if (State is byte state) w.U8(state);
		return w.ToArray();
	}

	/// <summary>Null when <paramref name="d"/> is not a whole pose message.</summary>
	public static OwnPose? Decode(ReadOnlySpan<byte> d)
	{
		if (d.Length is not (Size or SizeWithState) || d[0] != (byte)SyncMsg.OwnMotion) return null;
		var r = new WireReader(d.Slice(1));
		uint seq = r.U32();
		ushort epoch = r.U16();
		uint parent = r.U32();
		var position = new Vec3D(D(ref r), D(ref r), D(ref r));
		var rotation = new Quaternion(F(ref r), F(ref r), F(ref r), F(ref r));
		var velocity = new Vector3(F(ref r), F(ref r), F(ref r));
		var angular = new Vector3(F(ref r), F(ref r), F(ref r));
		byte? state = d.Length == SizeWithState ? r.U8() : null;
		return new OwnPose(seq, epoch, parent, position, rotation, velocity, angular, state);
		static double D(ref WireReader r) => BitConverter.Int64BitsToDouble(r.I64());
		static float F(ref WireReader r) => BitConverter.Int32BitsToSingle(r.I32());
	}

	public static bool NewerEpoch(ushort a, ushort b) => (short)(a - b) > 0;
}

/// <summary>
/// A character's discrete movement state in one byte: Keen's movement state (0 none, 1 ground, 2 jetpack, 3 seated),
/// crouched, dampeners on, boosting. The server sends it with each ack; the player's game compares it with what it
/// predicted for the same input and takes the server's when they keep disagreeing.
/// </summary>
public static class OwnState
{
	public const byte Crouched = 4, Dampeners = 8, Boosting = 16, Forced = 64;

	private const byte Toggled = 3 | Crouched | Dampeners;
	private const int Ground = 1, Jetpack = 2;

	/// <summary>Server: the state the player's game predicts after an input, from the state before it and its toggles
	/// (each toggle taken as granted).</summary>
	public static byte Expect(byte before, OwnToggles toggles)
	{
		byte e = before;
		if ((toggles & OwnToggles.Jetpack) != 0 && Movement(e) is Ground or Jetpack) e = (byte)(e & ~3 | (Movement(e) == Ground ? Jetpack : Ground));
		if ((toggles & OwnToggles.Crouch) != 0) e ^= Crouched;
		if ((toggles & OwnToggles.Dampeners) != 0) e ^= Dampeners;
		return e;
	}

	/// <summary>Server: the state after the tick is not the one the player's input asked for (a refused toggle, fuel run
	/// out, gravity lost, death): the ack says so, and the player's game takes it at once instead of waiting out
	/// <see cref="OwnStateCheck.Limit"/> acks. A boost ended while the input still holds it (no boost fuel) counts.</summary>
	public static bool ForcedBy(byte expected, byte after, bool boostHeld)
		=> ((expected ^ after) & Toggled) != 0 || boostHeld && (expected & Boosting) != 0 && (after & Boosting) == 0;

	public static byte Pack(int movementState, bool crouched, bool dampeners, bool boosting)
		=> (byte)((movementState & 3) | (crouched ? Crouched : 0) | (dampeners ? Dampeners : 0) | (boosting ? Boosting : 0));

	public static int Movement(byte state) => state & 3;

	/// <summary>A disagreement the player's game corrects. Boosting is left out: it follows the boost key held for the
	/// charge time on both sides, and a stopped boost (no fuel) shows in the movement state and position anyway.</summary>
	public static bool Differs(byte predicted, byte server) => ((predicted ^ server) & ~(Boosting | Forced)) != 0;

	/// <summary>Any disagreement, the boost included.</summary>
	public static bool DiffersAtAll(byte predicted, byte server) => ((predicted ^ server) & ~Forced) != 0;
}

/// <summary>
/// Player side: the state this game predicted after each input, compared with the server's for the same input. Toggles
/// run on the same input on both sides, so a disagreement means the server refused one (no fuel, no room to stand) or
/// changed the state itself (gravity lost, fuel out); after <see cref="Limit"/> acks in a row the server's state is taken.
/// A toggle whose input reached the server late (a hitch on either side) is applied there a few ticks after its own
/// input, so a short disagreement is waited out rather than undone and redone.
/// </summary>
public sealed class OwnStateCheck
{
	public const int Ring = 512, Limit = 10;

	private readonly (uint Seq, byte State)[] _ring = new (uint, byte)[Ring];
	private int _misses;
	private uint _settled;

	public long Taken, Forced;

	public void Reset()
	{
		Array.Clear(_ring);
		_misses = 0;
		_settled = 0;
	}

	/// <summary>The server's state was taken while this game was at <paramref name="seq"/>: acks of the inputs before it
	/// still carry the old prediction and say nothing new.</summary>
	public void TakenAt(uint seq) => _settled = seq;

	public void Record(uint seq, byte state) => _ring[seq % Ring] = (seq, state);

	/// <summary>The server's state to take now, or null.</summary>
	public byte? Measure(uint ack, byte server)
	{
		var (seq, predicted) = _ring[ack % Ring];
		if (seq != ack || ack == 0 || ack <= _settled) return null;
		bool forced = (server & OwnState.Forced) != 0;
		if (forced && OwnState.DiffersAtAll(predicted, server))
		{
			_misses = 0;
			Forced++;
			Taken++;
			return server;
		}
		if (!OwnState.Differs(predicted, server)) { _misses = 0; return null; }
		if (++_misses < Limit) return null;
		_misses = 0;
		Taken++;
		return server;
	}
}

/// <summary>
/// Server side, per player: the inputs the player's game sent, applied one per server tick in the player's order, so the
/// server's simulation of the character runs the same input sequence as the player's prediction did. A missing input is
/// guessed (the last one held, then nothing); when the real one turns up a tick or two late, its extra turn and a missed
/// jump go into the next tick. When it turns up more than <see cref="RewindTicks"/> late and nothing real was applied since
/// (the player's game paused while loading, or ticks a little slower), the guesses become extra server ticks and the
/// inputs run from there, their guessed turn taken back: otherwise the server would stay ahead of every input forever. When the server falls behind (a stall it does not catch up, a server slower
/// than 60 ticks a second) the queue grows past <see cref="Target"/>. It drains for free by dropping idle inputs while
/// the character rests, otherwise slowly: every <see cref="DrainEvery"/>th tick merges one extra input, so the character
/// covers a little less ground than predicted spread thin enough for the player's game to ease it out unseen (a burst of
/// merges after a stall would add up to a visible jump). Over <see cref="Cap"/> every tick merges one, over
/// <see cref="HardCap"/> the queue is merged down at once. Not thread-safe; the caller locks.
/// </summary>
public sealed class OwnInputBuffer
{
	public const int Target = 4, DrainEvery = 8, Cap = 60, HardCap = 180, GuessTicks = 10, RewindTicks = 2, Window = 600;

	private readonly Dictionary<uint, OwnInput> _queued = new();
	private readonly Dictionary<uint, OwnInput> _guessed = new();
	private bool _started;
	private uint _next, _newest, _lastReal;
	private int _guessRun, _drainTicks;
	private OwnInput _last;
	private Vector3 _turnOwed;
	private bool _jumpOwed;
	private OwnToggles _togglesOwed;

	public long Applied, Real, Guessed, Late, Rewound, Skipped, Merged;

	/// <summary>The input for the next tick is here (no guess needed).</summary>
	public bool HasNext => _started && _queued.ContainsKey(_next);

	/// <summary>Ticks of input waiting, holes included.</summary>
	public int Depth => _started && !Seq.Newer(_next, _newest) ? Seq.Diff(_newest, _next) + 1 : 0;

	public void Reset()
	{
		_queued.Clear();
		_guessed.Clear();
		_started = false;
		_guessRun = 0;
		_last = default;
		_turnOwed = default;
		_jumpOwed = false;
		_togglesOwed = OwnToggles.None;
	}

	public void Offer(uint seq, OwnInput input)
	{
		input = input.Sanitized();
		if (!_started)
		{
			_started = true;
			_next = _newest = seq;
			_lastReal = seq - 1;
		}
		if (Seq.Diff(_next, seq) > RewindTicks && Seq.Newer(seq, _lastReal))
		{
			for (uint s = seq; s != _next; s++)
				if (_guessed.Remove(s, out var guess))
				{
					_turnOwed -= guess.Rotation;
					Rewound++;
				}
			_next = _newest = seq;
			_guessRun = 0;
		}
		if (Seq.Newer(_next, seq))
		{
			if (_guessed.Remove(seq, out var guess))
			{
				_turnOwed += input.Rotation - guess.Rotation;
				_jumpOwed |= input.Jump && !guess.Jump;
				_togglesOwed ^= input.Toggles;
				Late++;
			}
			return;
		}
		if (Seq.Diff(seq, _next) >= Window) return;
		_queued.TryAdd(seq, input);
		if (Seq.Newer(seq, _newest)) _newest = seq;
	}

	/// <summary>
	/// One server tick: the input to apply and the newest sequence it covers (the ack), or null before the first input.
	/// <paramref name="resting"/>: the character stands still, so dropping an idle input changes nothing.
	/// </summary>
	public (OwnInput Input, uint Ack)? Take(bool resting)
	{
		if (!_started) return null;
		var input = Pop();
		int depth = Depth;
		int extra = depth > HardCap ? depth - Target : depth > Cap ? 1 : 0;
		if (extra == 0 && depth > Target)
		{
			if (resting && input.Idle && _queued.TryGetValue(_next, out var following) && following.Idle)
			{
				Pop();
				Skipped++;
			}
			else if (++_drainTicks % DrainEvery == 0) extra = 1;
		}
		for (int i = 0; i < extra; i++)
		{
			var merged = Pop();
			input = new OwnInput(merged.Movement, input.Rotation + merged.Rotation, input.Jump || merged.Jump, merged.Sprint, merged.Boost, input.Toggles ^ merged.Toggles);
			Merged++;
		}
		if (_turnOwed != Vector3.Zero || _jumpOwed || _togglesOwed != OwnToggles.None)
		{
			input = input with { Rotation = input.Rotation + _turnOwed, Jump = input.Jump || _jumpOwed, Toggles = input.Toggles ^ _togglesOwed };
			_turnOwed = default;
			_jumpOwed = false;
			_togglesOwed = OwnToggles.None;
		}
		Applied++;
		return (input, _next - 1);
	}

	private OwnInput Pop()
	{
		if (_queued.Remove(_next, out var input))
		{
			_guessRun = 0;
			_lastReal = _next;
			Real++;
		}
		else
		{
			// Holding a key is the likely case; after a few ticks with nothing the player is let go of, not run off a cliff.
			input = _guessRun++ < GuessTicks ? _last with { Jump = false, Toggles = OwnToggles.None } : new OwnInput(default, default, false, _last.Sprint, _last.Boost);
			_guessed[_next] = input;
			_guessed.Remove(_next - Window);
			Guessed++;
		}
		if (!Seq.Newer(_newest, _next)) _newest = _next;
		_last = input;
		_next++;
		return input;
	}
}

/// <summary>
/// Player side: what the player's game predicted after each input, and how the server's answer for that input is blended
/// into the live character. The error for an ack is the server's pose minus the prediction minus every correction already
/// applied since that prediction, so an error is never applied twice. Small errors are eased out over
/// <see cref="BlendSeconds"/> at most <see cref="MaxStep"/> plus a share of the character's own speed per tick; an error
/// over <see cref="SnapMeters"/> is applied at once. Positions and velocities are in the frame of the parent the
/// prediction was made in; a different parent starts a new frame generation and ends comparing older predictions.
/// Facing gets the same treatment: the player's own turn inputs replay exactly on the server, but jetpack flight in
/// gravity (alignment, dampers, collisions) can turn the two bodies apart, and nothing turned them back; a facing off by
/// more than <see cref="FacingDeadbandDegrees"/> is eased toward the server's at <see cref="MaxTurnStepDegrees"/> a tick.
/// </summary>
public sealed class OwnCorrection
{
	public const double SnapMeters = 3, DeadbandMeters = 0.02, BlendSeconds = 0.1, MaxStep = 0.05, SpeedShare = 0.2;
	public const double FacingDeadbandDegrees = 0.5, MaxTurnStepDegrees = 0.5;
	public const int Ring = 512, MismatchLimit = 30;

	private struct Predicted
	{
		public uint Seq, Parent;
		public int Gen;
		public Vec3D Position, Applied;
		public Vector3 Velocity, AppliedVelocity;
		public Quaternion Rotation, AppliedTurn;
	}

	public enum Outcome { None, Blend, Snap, Rebase }

	private readonly Predicted[] _ring = new Predicted[Ring];
	private int _gen = 1;
	private uint _parent;
	private int _mismatches;
	private uint _newestAck;
	private Vec3D _applied, _pending;
	private Vector3 _appliedVelocity, _pendingVelocity;
	private Quaternion _appliedTurn = Quaternion.Identity, _pendingTurn = Quaternion.Identity;
	private bool _snapNow;

	public long Acks, Snaps, Rebases;
	public double ErrorSum, ErrorMax, StepMax, TurnErrorSum, TurnErrorMax;

	/// <summary>The newest measured error (diagnostics).</summary>
	public Vec3D LastError;
	public Vector3 LastVelocityError;

	public void Reset()
	{
		Array.Clear(_ring);
		_gen++;
		_mismatches = 0;
		_newestAck = 0;
		_applied = _pending = default;
		_appliedVelocity = _pendingVelocity = default;
		_appliedTurn = _pendingTurn = Quaternion.Identity;
		_snapNow = false;
	}

	/// <summary>After input <paramref name="seq"/> ran: where the character is, in <paramref name="parent"/>'s frame.</summary>
	public void Record(uint seq, uint parent, Vec3D position, Vector3 velocity, Quaternion rotation = default)
	{
		if (parent != _parent)
		{
			_parent = parent;
			_gen++;
			_applied = _pending = default;
			_appliedVelocity = _pendingVelocity = default;
			_appliedTurn = _pendingTurn = Quaternion.Identity;
			_snapNow = false;
		}
		_ring[seq % Ring] = new Predicted { Seq = seq, Parent = parent, Gen = _gen, Position = position, Applied = _applied, Velocity = velocity, AppliedVelocity = _appliedVelocity, Rotation = rotation, AppliedTurn = _appliedTurn };
	}

	/// <summary>The server's pose after input <paramref name="ack"/>. Rebase: frames disagree for too long, jump to the server's pose.</summary>
	public Outcome Measure(uint ack, uint parent, Vec3D position, Vector3 velocity, Quaternion rotation = default)
	{
		if (ack == 0 || (_newestAck != 0 && !Seq.Newer(ack, _newestAck))) return Outcome.None;
		ref var p = ref _ring[ack % Ring];
		if (p.Seq != ack || p.Gen == 0) return Outcome.None;
		_newestAck = ack;
		if (p.Gen != _gen || p.Parent != parent)
		{
			if (++_mismatches < MismatchLimit) return Outcome.None;
			_mismatches = 0;
			Rebases++;
			return Outcome.Rebase;
		}
		_mismatches = 0;
		var error = position - p.Position - (_applied - p.Applied);
		var velocityError = velocity - p.Velocity - (_appliedVelocity - p.AppliedVelocity);
		double size = error.Length;
		LastError = error;
		LastVelocityError = velocityError;
		Acks++;
		ErrorSum += size;
		ErrorMax = Math.Max(ErrorMax, size);
		if (rotation != default && p.Rotation != default)
		{
			// The turn still off: the server's facing against the prediction with the turns applied since it was made.
			var since = _appliedTurn * Quaternion.Conjugate(p.AppliedTurn);
			var off = Quaternion.Normalize(Quaternion.Normalize(rotation) * Quaternion.Conjugate(since * Quaternion.Normalize(p.Rotation)));
			if (off.W < 0) off = Quaternion.Negate(off);
			double turn = Degrees(off);
			TurnErrorSum += turn;
			TurnErrorMax = Math.Max(TurnErrorMax, turn);
			_pendingTurn = turn < FacingDeadbandDegrees ? Quaternion.Identity : off;
		}
		_pending = size < DeadbandMeters ? default : error;
		_pendingVelocity = velocityError.Length() < 0.05f ? default : velocityError;
		if (size <= SnapMeters) return _pending == default && _pendingVelocity == default ? Outcome.None : Outcome.Blend;
		_snapNow = true;
		Snaps++;
		return Outcome.Snap;
	}

	public uint NewestAck => _newestAck;

	public double MeanError => Acks == 0 ? 0 : ErrorSum / Acks;

	private static double Degrees(Quaternion q) => 2 * Math.Acos(Math.Min(1, Math.Abs(q.W))) * 180 / Math.PI;

	/// <summary>The turn to apply to the live character this tick, in world space (applied before its facing), or identity.</summary>
	public Quaternion StepTurn()
	{
		double left = Degrees(_pendingTurn);
		if (left < 1e-3) return Quaternion.Identity;
		var turn = left <= MaxTurnStepDegrees ? _pendingTurn : Quaternion.Slerp(Quaternion.Identity, _pendingTurn, (float)(MaxTurnStepDegrees / left));
		_pendingTurn = Quaternion.Normalize(_pendingTurn * Quaternion.Conjugate(turn));
		_appliedTurn = Quaternion.Normalize(turn * _appliedTurn);
		return turn;
	}

	/// <summary>The shift to apply to the live character this tick (<paramref name="dt"/> seconds, moving at <paramref name="speed"/>).</summary>
	public (Vec3D Position, Vector3 Velocity) Step(double dt, double speed)
	{
		Vec3D dp;
		Vector3 dv;
		if (_snapNow)
		{
			_snapNow = false;
			dp = _pending;
			dv = _pendingVelocity;
		}
		else
		{
			double share = 1 - Math.Exp(-dt / BlendSeconds);
			dp = _pending * share;
			double cap = MaxStep + SpeedShare * speed * dt, size = dp.Length;
			if (size > cap) dp = dp * (cap / size);
			else if (_pending.Length < 0.002) dp = _pending;
			dv = _pendingVelocity * (float)share;
			StepMax = Math.Max(StepMax, dp.Length);
		}
		_pending -= dp;
		_pendingVelocity -= dv;
		_applied += dp;
		_appliedVelocity += dv;
		return (dp, dv);
	}
}

/// <summary>Messages a player may send: a burst after a stall, then the tick rate with headroom.</summary>
public static class OwnMotionRules
{
	public const double Burst = 30, PerSecond = 100;

	/// <summary>Token bucket per player: true when one more message may be used now.</summary>
	public static bool Take(ref double tokens, ref long atMs, long nowMs)
	{
		tokens = Math.Min(Burst, tokens + (nowMs - atMs) / 1000.0 * PerSecond);
		atMs = nowMs;
		if (tokens < 1) return false;
		tokens--;
		return true;
	}
}

/// <summary>Player side: the server's poses for the player's own character, taken by the client's own-motion tick.</summary>
public static class OwnMotionInbox
{
	public static readonly ConcurrentQueue<OwnPose> Poses = new();
}
