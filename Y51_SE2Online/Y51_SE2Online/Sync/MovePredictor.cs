using System;
using System.Numerics;

namespace Y51_SE2Online.Sync;

// Position lead for the joiner's own character: shown = newest host position + the velocity predicted for the
// inputs the host had not applied yet (host stamp minus the round trip, up to now). Nothing accumulates, so a
// wrong model only offsets the body by (error x round trip) until the input changes.
public sealed class MovePredictor
{
	private readonly (long T, Vector3 Model, Vector3 Other)[] _hist = new (long, Vector3, Vector3)[512];

	private int _head;

	private int _count;

	private long _hostHigh = -1L;

	private long _offset = long.MinValue;

	private long _offsetSetMs;

	private double _anchor = double.NaN;

	// The newest sample's tick on a straight line of ticks (see OnHost): _lineTick is that tick's host time.
	private double _lineTick = double.NaN, _tickMs = 1000.0 / 60.0;

	private (double X, double Y, double Z) _linePos;

	// The velocity the samples' positions moved at. The host's reported velocity dipped 2-3 m/s for a few ticks at the
	// jetpack's speed limit while the body kept moving 1/3 m a tick, and the lead shrank by that x the window (90 mm).
	private Vector3 _posVel;

	private bool _posVelOk;

	// The offset estimate steps (+2 ms a second, down at once on a quick packet); anchoring on it directly moved the
	// lead by speed x step (0.1-0.6 m at jetpack/ship speeds, a snap back or forward). Slewed like playout instead.
	private double _play = double.NaN;

	private long _playSetMs = long.MinValue;

	private readonly WindowMin _offsetWindow = new();

	private double _late;

	private Vector3 _model;

	private long _lastStepMs;

	private bool _ground;

	// Free flight (jetpack, drift): the host's velocity is all there is, so its measured deceleration is tracked too.
	private bool _free;

	private Vector3 _freeVel;

	private Vector3 _accFrom;

	private long _accAt = long.MinValue;

	private long _newestHostT = long.MinValue;

	private Vector3 _acc;
	// Jetpack: per-frame thrust inputs are simulated across the lead window (StepJet).
	private bool _jet;

	// Scale on DampenerDecel, learned from real stops (the distance the host needed once a release had reached it):
	// other movement definitions, modded thrust, a different mass than the model's.
	public float BrakeScale { get; private set; } = 1f;

	private bool _jetInput;

	private long _releaseAt = long.MinValue;

	private double _lastLagMs;

	private long _stopHostT = long.MinValue;

	private bool _stopping;

	private float _stopV0;

	private double _stopDist;

	public int MaxLeadMs { get; set; } = 500;

	public float Gain { get; private set; } = 1f;

	public Vector3 Velocity { get; private set; }

	/// <summary>The newest sample's local time on the tick line (see OnHost), or null before one.</summary>
	public double? SampleLocalMs => double.IsNaN(_anchor) ? null : _anchor;

	/// <summary>The velocity the newest samples moved at (see _posVel), or null when too slow to tell.</summary>
	public Vector3? SampleVelocity => _posVelOk ? _posVel : null;

	public bool Active => !double.IsNaN(_anchor) && _count > 0;

	public void Reset()
	{
		_count = 0;
		_anchor = double.NaN;
		_lineTick = double.NaN;
		_posVelOk = false;
		_ground = false;
		_free = false;
		_jet = false;
		_jetInput = false;
		_stopping = false;
		_releaseAt = long.MinValue;
		_stopHostT = long.MinValue;
		_accAt = long.MinValue;
		_newestHostT = long.MinValue;
		Gain = 1f;
		Velocity = default;
	}

	// Walking: the model velocity chases the input's target at the controller's MaxAcceleration; it starts
	// from the host's horizontal velocity whenever walking (re)starts.
	public void StepGround(long now, Vector3 target, Vector3 hostHorizontal, Vector3 rest, float maxAcceleration, bool inputChanged)
	{
		_free = false;
		_jet = false;
		if (!_ground || _count == 0)
		{
			_model = hostHorizontal;
		}
		else
		{
			_model = Approach(_model, target, maxAcceleration * Math.Clamp(now - _lastStepMs, 0L, 100L) / 1000f);
		}
		Step(now, _model, rest, inputChanged);
		_ground = true;
	}

	// Free flight: no input model, the body carries the host's velocity across the lead window. A dampener stop
	// decelerates the host while its samples are a lag old; leading by that velocity overshot the stop by up to
	// speed x lag, and the body slid back onto it (the bump-back on releasing keys). See Lead.
	public void StepFree(long now, Vector3 hostVel)
	{
		_jet = false;
		// Measured on the host's own stamps: arrival jitter on the client's clock made the estimate wobble.
		long hostT = _newestHostT;
		if (_accAt == long.MinValue || hostT - _accAt > 500 || hostT < _accAt)
		{
			_accAt = hostT;
			_accFrom = hostVel;
			_acc = Vector3.Zero;
		}
		else if (hostT - _accAt >= 16)
		{
			// Blended in over ~100 ms of host time: a sudden full estimate moved the lead by a = 12 m/s^2 x lag^2 / 2 in
			// one frame, a visible hitch when braking starts.
			_acc += Math.Min(1f, (hostT - _accAt) / 100f) * ((hostVel - _accFrom) / ((hostT - _accAt) / 1000f) - _acc);
			_accAt = hostT;
			_accFrom = hostVel;
		}
		Step(now, Vector3.Zero, hostVel, inputChanged: false);
		_free = true;
		_freeVel = _posVelOk ? _posVel : hostVel;
	}

	// Jetpack: the inputs the host has not applied yet decide where the body goes next, not the host's last velocity:
	// carrying that velocity alone showed every thrust start and stop a round trip late. thrustAccel is this frame's
	// input acceleration (world), brakeDecel the dampeners' (0 when off), speedLimit the soft limit (0 = none).
	public void StepJet(long now, Vector3 hostVel, Vector3 thrustAccel, float brakeDecel, float speedLimit)
	{
		StepFree(now, hostVel);
		_hist[(_head - 1 + _hist.Length) % _hist.Length] = (now, thrustAccel, new Vector3(brakeDecel, speedLimit, 0f));
		_jet = true;
		bool input = thrustAccel != Vector3.Zero;
		if (_jetInput && !input) _releaseAt = now;
		_jetInput = input;
		LearnBraking(hostVel, input, brakeDecel);
	}

	private void LearnBraking(Vector3 hostVel, bool input, float brakeDecel)
	{
		if (_newestHostT == _stopHostT || double.IsNaN(_anchor)) return;
		long dtHost = _stopHostT == long.MinValue ? 0 : _newestHostT - _stopHostT;
		_stopHostT = _newestHostT;
		float speed = hostVel.Length();
		if (input || brakeDecel <= 0f || dtHost > 200)
		{
			_stopping = false;
			return;
		}
		if (!_stopping)
		{
			// The newest host sample reflects the inputs up to InputTime: once that passes the release, the host brakes.
			if (_releaseAt != long.MinValue && InputTime(_lastLagMs) >= _releaseAt && speed > 3f)
			{
				_stopping = true;
				_stopV0 = speed;
				_stopDist = 0;
				_releaseAt = long.MinValue;
			}
			return;
		}
		_stopDist += speed * dtHost / 1000.0;
		if (speed > 0.3f) return;
		_stopping = false;
		if (_stopDist < 0.2) return;
		float learned = (float)(StopDistance(_stopV0, brakeDecel) / _stopDist);
		BrakeScale = Math.Clamp(BrakeScale + 0.5f * (learned - BrakeScale), 0.5f, 3f);
	}

	// The host's newest velocity carried from the start of the lead window to now under each frame's inputs.
	private Vector3 JetLead(double from, double now, float measuredDecel)
	{
		int n = 0;
		while (n < _count && _hist[(_head - 1 - n + _hist.Length) % _hist.Length].T > from) n++;
		n = Math.Min(n + 1, _count);
		Vector3 v = _freeVel;
		Vector3 lead = Vector3.Zero;
		for (int i = n - 1; i >= 0; i--)
		{
			var (t, a, o) = _hist[(_head - 1 - i + _hist.Length) % _hist.Length];
			double start = Math.Max(t, from);
			double end = i == 0 ? now : _hist[(_head - i + _hist.Length) % _hist.Length].T;
			if (end <= start) continue;
			float dt = (float)((end - start) / 1000.0);
			if (a != Vector3.Zero)
			{
				v += a * SpeedLimitFalloff(a, v, o.Y) * dt;
				if (o.Y > 0f && v.Length() > o.Y) v = Vector3.Normalize(v) * o.Y;
			}
			else if (o.X > 0f || measuredDecel > 0.5f)
			{
				// The dampeners' braking law; the host's measured braking only without one. Measured braking lags
				// (it blends in over ~100 ms of host time), and the low figure it shows right after a release
				// under-braked the lead: the body overshot the stop and slid ~1 m back.
				float speed = v.Length();
				float step = (o.X > 0f ? DampenerDecel(speed, o.X) * BrakeScale : measuredDecel) * dt;
				v = speed <= step || speed < DampenerMinSpeed ? Vector3.Zero : v * ((speed - step) / speed);
			}
			lead += v * dt;
		}
		// The body filter carries the shown body along this between samples: the host's velocity (still full speed for
		// a round trip after a release) held the body ~speed x 80 ms ahead of the braking lead, the overshoot and
		// pull-back seen at 300 ms.
		Velocity = v;
		return lead;
	}

	// ThrustComponent.ComputeInertiaDampening with Character_MovementDefinition (2.4.0.95: ThrusterDampenerMaxSpeedFactor 65,
	// MinSpeedFactor 4, CurveMinSpeed 0.25), capped at the braking thrust x ThrusterBrakingPowerMultiplierMin 2.5: hard at mid
	// speeds (33 m/s^2 at 9 m/s), soft near the stop (6 m/s^2 at 1.4 m/s), the profile the host shows.
	const float DampenerMinSpeed = 0.25f;

	public static float DampenerDecel(float speed, float brakingThrust) => Math.Min(brakingThrust * 2.5f, 0.003f * 65f * (speed + 4f) * (speed + 4f));

	// ThrustComponent.GetSpeedLimitedInputThrust (ThrusterAccelerationBias 0.25, SpeedLimitFalloff 3,
	// MinThrusterAccelerationRatio 0.25): thrust fades as the velocity nears the soft limit along the input. At 16 of
	// 20 m/s the host accelerated at ~8 m/s^2, not the full 12.5; the full figure over-led a 300 ms window.
	public static float SpeedLimitFalloff(Vector3 thrust, Vector3 velocity, float limit)
	{
		if (limit <= 0f) return 1f;
		float closeness = 1f - Vector3.Distance(Vector3.Normalize(thrust) * limit, velocity) / limit;
		return closeness > 0.25f ? 1f - MathF.Pow((closeness - 0.25f) / 0.75f, 3f) * 0.75f : 1f;
	}

	static double StopDistance(float speed, float brakingThrust)
	{
		double distance = 0;
		for (int i = 0; i < 6000 && speed >= DampenerMinSpeed; i++)
		{
			speed = Math.Max(0f, speed - DampenerDecel(speed, brakingThrust) / 60f);
			distance += speed / 60.0;
		}
		return distance;
	}

	// Distance a body at speed slowing at decel covers in seconds, stopping for good when it reaches zero.
	public static float StoppingLead(float speed, float decel, double seconds)
	{
		if (seconds <= 0) return 0f;
		return speed >= decel * seconds ? (float)(speed * seconds - decel * seconds * seconds / 2) : speed * speed / (2f * decel);
	}

	// model: the input-driven part (scaled by Gain); rest: taken from the host as is (gravity, jetpack drift).
	public void Step(long now, Vector3 model, Vector3 rest, bool inputChanged)
	{
		_ground = false;
		_lastStepMs = now;
		if (inputChanged)
		{
			Gain = 1f;
		}
		Velocity = model * Gain + rest;
		_hist[_head] = (now, model, rest);
		_head = (_head + 1) % _hist.Length;
		if (_count < _hist.Length)
		{
			_count++;
		}
	}

	// Anchors on the host's own stamp (min-filtered clock offset, as TransformSmoother does) so arrival jitter
	// does not move the window; _late is the mean jitter, which LookPredictor.LagMs contains and Lead removes.
	/// <param name="hostTickMs">The stamp's de-quantized tick time (whole-ms stamps moved the lead window by up to 0.5 ms).</param>
	/// <param name="pos">The sample's position and <paramref name="vel"/> the body's velocity: how many ticks it moved.</param>
	public void OnHost(long now, uint hostMs, double hostTickMs = double.NaN, (double X, double Y, double Z) pos = default, Vector3 vel = default)
	{
		if (hostMs == 0)
		{
			_anchor = now - _late;
			return;
		}
		long hostT = Unwrap(hostMs);
		_newestHostT = Math.Max(_newestHostT, hostT);
		long transit = now - hostT;
		_offset = _offsetWindow.Add(now, transit);
		_late += 0.05 * ((double)(transit - _offset) - _late);
		double dt = _playSetMs == long.MinValue ? 0 : Math.Clamp(now - _playSetMs, 0L, 100L);
		_playSetMs = now;
		_play = double.IsNaN(_play) || Math.Abs(_offset - _play) > 250.0 ? _offset : _play + Math.Clamp(_offset - _play, -0.02 * dt, 0.02 * dt);
		// The window start is the sample's place in time, and the body moves one fixed tick per host tick, but the stamps
		// wander 1-3 ms a tick around the host's frame pacing (15-20 ms after a server hitch), and about one sample in
		// ten carries a position a tick older or newer than its stamp. Following each stamp moved the lead by speed x
		// that (18 mm a frame at 20 m/s). The start steps by the ticks the body moved instead (by the stamps when it
		// is too slow to tell) and drifts onto the stamps.
		double tick = double.IsNaN(hostTickMs) ? hostT : hostTickMs;
		double raw = tick + _play;
		double step = tick - _lineTick;
		if (double.IsNaN(_anchor) || double.IsNaN(_lineTick) || step < 0.0 || Math.Abs(raw - (_anchor + step)) > 50.0)
		{
			_anchor = raw;
			_lineTick = tick;
			_linePos = pos;
			_posVelOk = false;
			return;
		}
		float speed = vel.Length();
		double n = speed > 2f
			? Math.Clamp(Math.Round(((pos.X - _linePos.X) * vel.X + (pos.Y - _linePos.Y) * vel.Y + (pos.Z - _linePos.Z) * vel.Z) / speed / (speed / 60.0)), 0.0, 8.0)
			: step > 0.0 ? Math.Max(1.0, Math.Round(step / _tickMs)) : 0.0;
		double ticks = Math.Round(step / _tickMs);
		if (ticks >= 1.0 && Math.Abs(step - ticks * _tickMs) < 0.35 * _tickMs) _tickMs = Math.Clamp(_tickMs + 0.01 * (step / ticks - _tickMs), 1000.0 / 240.0, 250.0);
		if (n > 0.0 || step > 0.0)
		{
			double predicted = _anchor + n * _tickMs;
			_anchor = predicted + 0.02 * (raw - predicted);
		}
		if (speed > 2f && n >= 1.0)
		{
			float perSecond = (float)(60.0 / n);
			_posVel = new Vector3((float)(pos.X - _linePos.X) * perSecond, (float)(pos.Y - _linePos.Y) * perSecond, (float)(pos.Z - _linePos.Z) * perSecond);
			_posVelOk = true;
		}
		else if (speed <= 2f)
		{
			_posVelOk = false;
		}
		_lineTick = tick;
		_linePos = pos;
	}

	// How much of the modelled velocity the host achieved (walls, slopes). Only steady stretches count, so the
	// start/stop ramp and a slightly off lag estimate are not taken for a wall.
	public void Observe(Vector3 hostModelPart, double lagMs)
	{
		if (double.IsNaN(_anchor))
		{
			return;
		}
		long then = (long)Math.Round(InputTime(lagMs));
		Vector3 model = ModelAt(then);
		float speedSq = model.LengthSquared();
		if (speedSq < 0.25f || (ModelAt(then - 100) - model).LengthSquared() > 0.01f * speedSq)
		{
			return;
		}
		float ratio = Math.Clamp(Vector3.Dot(hostModelPart, model) / speedSq, 0f, 1f);
		Gain += 0.2f * (ratio - Gain);
	}

	public Vector3 Lead(double now, double lagMs)
	{
		_lastLagMs = lagMs;
		if (!Active)
		{
			return Vector3.Zero;
		}
		double from = Math.Max(InputTime(lagMs), now - MaxLeadMs);
		if (_free)
		{
			// Current velocity across the window (integrating older frames left their speed in the lead after a
			// stop); while the host is slowing, only as far as it will still travel.
			double seconds = Math.Max(0.0, now - from) / 1000.0;
			float speed = _freeVel.Length();
			float decel = speed < 1e-3f ? 0f : -Vector3.Dot(_acc, _freeVel) / speed;
			if (_jet) return JetLead(from, now, decel);
			if (speed < 1e-3f) return Vector3.Zero;
			if (decel > 0.5f) Velocity = _freeVel / speed * Math.Max(0f, speed - decel * (float)seconds);
			return decel > 0.5f ? _freeVel / speed * StoppingLead(speed, decel, seconds) : _freeVel * (float)seconds;
		}
		Vector3 model = Vector3.Zero;
		Vector3 rest = Vector3.Zero;
		double end = now;
		for (int i = 0; i < _count && end > from; i++)
		{
			var (t, m, r) = _hist[(_head - 1 - i + _hist.Length) % _hist.Length];
			double start = Math.Max(t, from);
			if (end > start)
			{
				float seconds = (float)((end - start) / 1000.0);
				model += m * seconds;
				rest += r * seconds;
			}
			end = Math.Min(end, t);
		}
		return model * Gain + rest;
	}

	// CharacterMovementComponent.ApplyControlData (2.4.0.95) feeding CharacterPhysicsData: speed is
	// max(|FB|, |LR|) x BaseGroundVelocity along the input direction; local forward is -Z, right is +X.
	public static Vector3 GroundVelocity(Quaternion orientation, float moveX, float moveZ, bool sprinting, bool crouched, float jogMultiplier, float crouchMultiplier, float baseVelocity)
	{
		float fb = 0f - moveZ;
		float fbScale = 1f;
		float lrScale = jogMultiplier;
		if (crouched)
		{
			fbScale = (lrScale = crouchMultiplier);
		}
		else if (!sprinting || fb < 0f || MathF.Abs(moveX) > 1E-05f)
		{
			fbScale = jogMultiplier;
		}
		else if (fb > 1E-05f)
		{
			fb = 1f;
		}
		fb *= fbScale;
		float lr = moveX * lrScale;
		float speed = MathF.Max(MathF.Abs(fb), MathF.Abs(lr)) * baseVelocity;
		if (speed < 1E-06f)
		{
			return Vector3.Zero;
		}
		return Vector3.Transform(Vector3.Normalize(new Vector3(lr, 0f, 0f - fb)) * speed, orientation);
	}

	public static Vector3 Approach(Vector3 current, Vector3 target, float maxStep)
	{
		Vector3 delta = target - current;
		float length = delta.Length();
		if (length <= maxStep || length < 1E-06f)
		{
			return target;
		}
		return current + delta * (maxStep / length);
	}

	private double InputTime(double lagMs)
	{
		return _anchor - Math.Max(0.0, lagMs - _late);
	}

	private Vector3 ModelAt(long t)
	{
		for (int i = 0; i < _count; i++)
		{
			var entry = _hist[(_head - 1 - i + _hist.Length) % _hist.Length];
			if (entry.T <= t)
			{
				return entry.Model;
			}
		}
		return Vector3.Zero;
	}

	private long Unwrap(uint hostMs)
	{
		if (_hostHigh < 0)
		{
			_hostHigh = hostMs;
			return _hostHigh;
		}
		long t = _hostHigh + ((int)hostMs - (int)_hostHigh);
		if (t > _hostHigh)
		{
			_hostHigh = t;
		}
		return t;
	}
}
