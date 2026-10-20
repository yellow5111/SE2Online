using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Y51_SE2Online.Sync;

public sealed class TransformSmoother
{
	private sealed class Track
	{
		public readonly List<(double T, byte[] V)> Samples = new List<(double, byte[])>();

		public long LastArrivalMs;

		public bool Active = true;

		public bool FinalApplied;

		public bool WasExtrapolating;

		// The newest sample the last extrapolation started from.
		public double BaseT = double.NaN;

		public double LastOutMs;

		public Vec3D LastRaw, LastShown, Velocity, Offset;

		// Speed and heading along the real (not extrapolated) track: a body held at the end of its extrapolation has Velocity 0.
		public double Speed;

		public Vec3D Heading;

		public double PaybackUntilMs;

		public bool HasOut;

		// The host sends far bodies less often (StateBands); such a track plays further behind so it still has a sample ahead.
		public double GapMs = -1, ExtraMs;
	}

	public const int TransformBytes = 40;

	private readonly object _sync = new object();

	private readonly Dictionary<uint, Track> _tracks = new();

	private readonly HashSet<uint> _raw = new();

	private long _hostHigh = -1L;

	private long _offset = long.MinValue;

	private long _offsetSetMs;

	private readonly WindowMin _offsetWindow = new();

	private double _playOffset = double.NaN;

	private double _curDelay = -1.0;

	private double _targetDelay = -1.0, _targetLag = double.NaN;

	private double _lastSampleMs = double.NaN;

	private long _lastLateArrivalMs = long.MinValue;

	private double _lastTargetMs = double.NaN;

	private double _elapsedMs;

	// SE2 advances its own world a fixed 1/60 s per frame, so everything local moves evenly from frame to frame, while
	// the moment a frame samples here wanders by several ms (uneven frame work, two games on one PC). At that raw moment a
	// 300 m/s ship, and a camera riding it, advanced 1.2 then 5.1 px a frame; the frame clock keeps the rate, not the wobble.
	private readonly TickClock _frameClock = new();

	/// <summary>Sample spacing above this is a body at rest, not its send rate.</summary>
	public const int MaxGapMs = 300;

	// Arrival minus host stamp, per arrival: the playout lag has to cover nearly all of it.
	private readonly long[] _late = new long[512];

	private double _lag = double.NaN;

	private bool _underrun;

	private int _lateHead;

	private int _lateCount;

	public double MaxDelaySeenMs;

	public long Samples;

	public long Interpolated;

	public long Extrapolated;

	public long Held;

	public long Late;

	public long Dropped;

	public int DelayMs { get; set; } = Core.MpOptions.DefaultInterpMs;

	public bool Adaptive { get; set; } = true;

	public int MaxDelayMs { get; set; } = 250;

	public int MarginMs { get; set; } = 12;

	public double CurrentDelayMs => _curDelay < 0.0 ? DelayMs : _curDelay;

	public double TargetDelayMs => _targetDelay < 0.0 ? DelayMs : _targetDelay;

	// Covers late packets only. A longer gap is a server stall: the host's simulation paused and resumes where it
	// stopped, so coasting through it put bodies ahead and they were pulled back; holding matches what happened.
	public int MaxExtrapolateMs { get; set; } = 100;

	public int IdleMs { get; set; } = 1000;

	public bool Enabled => DelayMs > 0;

	public int ActiveCount
	{
		get
		{
			lock (_sync)
			{
				int count = 0;
				foreach (Track track in _tracks.Values)
				{
					count += track.Active ? 1 : 0;
				}
				return count;
			}
		}
	}

	public void SetRaw(uint netId, bool raw)
	{
		lock (_sync)
		{
			if (raw)
			{
				_raw.Add(netId);
				_tracks.Remove(netId);
			}
			else
			{
				_raw.Remove(netId);
			}
		}
	}

	public double SpeedOf(uint netId)
	{
		lock (_sync)
		{
			if (!_tracks.TryGetValue(netId, out Track track) || !track.Active || track.Samples.Count < 2)
			{
				return 0.0;
			}
			var (t1, b) = track.Samples[^1];
			var (t0, a) = track.Samples[^2];
			if (t1 <= t0 || t1 - t0 > 1000)
			{
				return 0.0;
			}
			double distSq = 0.0;
			for (int i = 0; i < 3; i++)
			{
				double from = BinaryPrimitives.ReadDoubleLittleEndian(a.AsSpan(i * 8));
				double to = BinaryPrimitives.ReadDoubleLittleEndian(b.AsSpan(i * 8));
				distSq += (to - from) * (to - from);
			}
			return Math.Sqrt(distSq) * 1000.0 / (t1 - t0);
		}
	}

	public double TurnRateOf(uint netId)
	{
		lock (_sync)
		{
			if (!_tracks.TryGetValue(netId, out Track track) || !track.Active || track.Samples.Count < 2)
			{
				return 0.0;
			}
			var (t1, b) = track.Samples[track.Samples.Count - 1];
			var (t0, a) = track.Samples[track.Samples.Count - 2];
			if (t1 <= t0 || t1 - t0 > 1000)
			{
				return 0.0;
			}
			return (double)LookMath.AngleDeg(LookMath.ReadTransform(a).O, LookMath.ReadTransform(b).O) * 1000.0 / (t1 - t0);
		}
	}

	public bool IsRaw(uint netId)
	{
		lock (_sync)
		{
			return _raw.Contains(netId);
		}
	}

	public bool Add(uint netId, uint hostMs, byte[] value, long localNowMs)
	{
		if (value.Length != 40)
		{
			return false;
		}
		lock (_sync)
		{
			if (_raw.Contains(netId))
			{
				return false;
			}
			long raw = Unwrap(hostMs);
			long arrivalOffset = NoteClockLocked(raw, localNowMs);
			double t = TickTime(raw);
			if (localNowMs != _lastLateArrivalMs)
			{
				_lastLateArrivalMs = localNowMs;
				_late[_lateHead] = arrivalOffset;
				_lateHead = (_lateHead + 1) % _late.Length;
				if (_lateCount < _late.Length)
				{
					_lateCount++;
				}
			}
			Samples++;
			if (!_tracks.TryGetValue(netId, out Track track))
			{
				track = _tracks[netId] = new Track();
			}
			track.LastArrivalMs = localNowMs;
			track.Active = true;
			track.FinalApplied = false;
			var samples = track.Samples;
			if (samples.Count > 0)
			{
				if (t <= samples[samples.Count - 1].T)
				{
					if (t == samples[samples.Count - 1].T)
					{
						samples[samples.Count - 1] = (t, value);
						return true;
					}
					if (t < samples[0].T)
					{
						Dropped++;
						return false;
					}
					Late++;
					int index = samples.FindIndex(s => s.T >= t);
					if (samples[index].T == t)
					{
						samples[index] = (t, value);
					}
					else
					{
						samples.Insert(index, (t, value));
					}
					return true;
				}
			}
			if (samples.Count > 0)
			{
				// Gaps over MaxGapMs are a body at rest (unchanged values are resent once a second), not its send rate.
				double gap = t - samples[samples.Count - 1].T;
				if (gap <= MaxGapMs) track.GapMs = track.GapMs < 0 ? gap : track.GapMs + 0.25 * (gap - track.GapMs);
			}
			samples.Add((t, value));
			if (samples.Count > 16)
			{
				samples.RemoveRange(0, samples.Count - 16);
			}
			return true;
		}
	}

	/// <summary>Feeds the host clock estimate from a transform that is not smoothed (the own character).</summary>
	public void NoteClock(uint hostMs, long localNowMs)
	{
		lock (_sync)
		{
			NoteClockLocked(Unwrap(hostMs), localNowMs);
		}
	}

	/// <summary>The local time a host stamp maps to (its arrival with the least delay seen), or null before any stamp.</summary>
	public double? LocalOf(uint hostMs)
	{
		lock (_sync)
		{
			// The slewed playout offset where there is one: the raw estimate steps 2 ms at a time (speed x 2 ms jumps).
			return (_offset == long.MinValue) ? null : (TickTime(Unwrap(hostMs)) + (double.IsNaN(_playOffset) ? _offset : _playOffset));
		}
	}

	/// <summary>Host simulation seconds per real second: SE2 moves its world one 1/60 s tick per frame, so a host at
	/// 58 fps moves bodies at 97 % of their velocity in real time.</summary>
	public double HostRate => Math.Clamp(1000.0 / 60.0 / _tickPeriod, 0.25, 1.0);

	/// <summary>The tick time a whole-ms host stamp stands for (see TickTime).</summary>
	public double HostTickMs(uint hostMs)
	{
		lock (_sync)
		{
			return _offset == long.MinValue ? hostMs : TickTime(Unwrap(hostMs));
		}
	}

	// Host stamps travel as whole ms, but ticks are 16.7 ms apart (a stamp up to 0.5 ms off moved a 300 m/s ship up to 0.15 m
	// and gave every body a 17/17/16 three-frame ripple). Stamps of successive ticks lie on a near-straight line, so the
	// line through them, kept inside each stamp's rounding interval, is the tick's real time.
	private double TickTime(long raw)
	{
		for (int i = 0; i < _ticks.Length; i++)
		{
			if (_ticks[i].Raw == raw) return _ticks[i].At;
		}
		double at = raw;
		if (raw > _tickRaw && raw - _tickRaw <= MaxGapMs)
		{
			double n = Math.Max(1.0, Math.Round((raw - _tickAt) / _tickPeriod));
			double error = raw - (_tickAt + n * _tickPeriod);
			if (Math.Abs(error) < 2.0)
			{
				at = Math.Clamp(_tickAt + n * _tickPeriod + 0.3 * error, raw - 0.5, raw + 0.5);
				_tickPeriod = Math.Clamp(_tickPeriod + 0.05 * error / n, 1000.0 / 240.0, 250.0);
			}
			else if (n == 1.0 && raw - _tickRaw >= 4)
			{
				_tickPeriod = raw - _tickRaw;
			}
		}
		if (raw > _tickRaw)
		{
			_tickRaw = raw;
			_tickAt = at;
		}
		_ticks[_tickHead] = (raw, at);
		_tickHead = (_tickHead + 1) % _ticks.Length;
		return at;
	}

	private readonly (long Raw, double At)[] _ticks = new (long, double)[64];

	private int _tickHead;

	private long _tickRaw = long.MinValue;

	private double _tickAt, _tickPeriod = 1000.0 / 60.0;

	private long NoteClockLocked(long t, long localNowMs)
	{
		long arrivalOffset = localNowMs - t;
		_offset = _offsetWindow.Add(localNowMs, arrivalOffset);
		return arrivalOffset;
	}

	private long Unwrap(uint hostMs)
	{
		if (_hostHigh < 0)
		{
			_hostHigh = hostMs;
			return _hostHigh;
		}
		int delta = (int)hostMs - (int)_hostHigh;
		long unwrapped = _hostHigh + delta;
		if (unwrapped > _hostHigh)
		{
			_hostHigh = unwrapped;
		}
		return unwrapped;
	}

	public void Remove(uint netId)
	{
		lock (_sync)
		{
			_tracks.Remove(netId);
		}
	}

	/// <summary>Bodies for the frame sampling at <paramref name="localNowMs"/>; call once a frame, with the fraction (whole ms moved a 300 m/s ship up to 0.3 m).</summary>
	/// <param name="evenTime">The caller's time is already an even frame time (a <see cref="TickClock"/> it also draws the
	/// own body with): taken as is, so remote bodies and the own body step and catch up together.</param>
	public void Sample(double localNowMs, List<(uint NetId, byte[] Value)> output, bool evenTime = false)
	{
		lock (_sync)
		{
			if (_offset == long.MinValue)
			{
				return;
			}
			localNowMs = evenTime ? _frameClock.Follow(localNowMs) : _frameClock.Next(localNowMs);
			UpdateDelay(localNowMs);
			// Fractional: rounding the playout time to whole milliseconds jittered fast bodies by speed x 0.5 ms.
			double sharedPlayTime = localNowMs - _lag;
			_underrun = false;
			double minAhead = double.PositiveInfinity;
			foreach (var (netId, track) in _tracks)
			{
				if (!track.Active || track.Samples.Count == 0)
				{
					continue;
				}
				double extra = track.GapMs < 0 ? 0 : Math.Max(0, track.GapMs + MarginMs - CurrentDelayMs);
				track.ExtraMs += extra > track.ExtraMs ? Math.Min(extra - track.ExtraMs, 0.04 * _elapsedMs) : Math.Max(extra - track.ExtraMs, -0.03 * _elapsedMs);
				double playTime = sharedPlayTime - track.ExtraMs;
				var samples = track.Samples;
				var newest = samples[samples.Count - 1];
				// Moving: sampled at its send rate (not a body at rest resent once a second) and not coming to a stop, which
				// ends its samples too.
				if (samples.Count >= 2 && newest.T - samples[samples.Count - 2].T <= 250.0 && playTime - newest.T < MaxExtrapolateMs && PathVelocity(samples[samples.Count - 2], newest).Length > 1.0) minAhead = Math.Min(minAhead, playTime - newest.T);
				if ((double)(playTime - newest.T) > CurrentDelayMs + track.ExtraMs + MaxExtrapolateMs + IdleMs)
				{
					if (!track.FinalApplied)
					{
						// Coasted ahead (a long server stall, a hard stop): stay put rather than jump back to the newest
						// sample; whatever comes next blends from here.
						if (!track.WasExtrapolating) output.Add((netId, newest.V));
						else track.Velocity = default;
						track.FinalApplied = true;
					}
					track.Active = false;
				}
				else if (playTime <= samples[0].T)
				{
					output.Add((netId, samples[0].V));
					Held++;
				}
				else if (playTime >= newest.T)
				{
					double ahead = playTime - newest.T;
					if (samples.Count >= 2)
					{
						var previous = samples[samples.Count - 2];
						double interval = newest.T - previous.T;
						if (interval > 0.0 && interval <= 250.0)
						{
							// Past the extrapolation window (a server stall) the body waits where the window ended: going
							// back to the newest sample there snapped fast ships tens of metres back and then froze them.
							var coast = ahead <= MaxExtrapolateMs ? PathVelocity(previous, newest) : default;
							output.Add((netId, Settle(track, localNowMs, Lerp(previous.V, newest.V, 1.0 + Math.Min(ahead, MaxExtrapolateMs) / interval, 1.0 + (double)MaxExtrapolateMs / interval), extrapolated: true, coast, newest.T)));
							if (ahead <= MaxExtrapolateMs) { Extrapolated++; _underrun = true; }
							else Held++;
							continue;
						}
					}
					output.Add((netId, Settle(track, localNowMs, newest.V, extrapolated: false, default)));
					Held++;
				}
				else
				{
					int i = 1;
					while (i < samples.Count && samples[i].T < playTime) i++;
					var from = samples[i - 1];
					var to = samples[i];
					double t = to.T == from.T ? 1.0 : (double)(playTime - from.T) / (double)(to.T - from.T);
					output.Add((netId, Settle(track, localNowMs, Lerp(from.V, to.V, t), extrapolated: false, PathVelocity(from, to))));
					Interpolated++;
					if (i - 1 > 0)
					{
						samples.RemoveRange(0, i - 1);
					}
				}
			}
			_minAheadMs = double.IsPositiveInfinity(minAhead) ? double.NegativeInfinity : minAhead;
		}
	}

	// Playout lag (local time minus host stamp) changes only as a slow time scale: the 2 s minimum of the arrival
	// offset steps +-10 ms every few seconds and the old delay slewed 3-6 %, so ships surged 2-6 % at steady flight.
	// It grows faster only while bodies run out of samples, never past what arrivals need, and shrinks very slowly.
	public const double LagRate = 0.005, UnderrunRate = 0.04, ShrinkRate = 0.0025;

	// A server stall (GC, autosave, a slow tick) starves every moving body at once. The playout used to run on into the
	// 100 ms extrapolation window and then hold: a 250 ms stall froze everything for 150 ms and then surged to catch up.
	// Once every moving body is StallFromMs past its newest sample, the playout slows (to 1 - StallRate of real time at
	// StallFullMs), so bodies glide through the stall instead of stopping; the lag that adds goes back at RecoverRate.
	// One body stopping while others still move is not a stall: the others are not starved.
	public const double StallFromMs = 40, StallFullMs = 80, StallRate = 0.85, RecoverRate = 0.01;

	// Lag the stall slowdown added; it alone goes back at RecoverRate (normal shrinking stays slow, see ShrinkRate).
	private double _stallDebt;

	// How far the least starved moving body was past its newest sample last frame (negative: it had one ahead).
	private double _minAheadMs = double.NegativeInfinity;

	/// <summary>The share of real time the playout skips while every moving body is <paramref name="minAheadMs"/> past its newest sample.</summary>
	public static double StallSlowdown(double minAheadMs) => StallRate * Math.Clamp((minAheadMs - StallFromMs) / (StallFullMs - StallFromMs), 0.0, 1.0);

	private void UpdateDelay(double now)
	{
		double elapsed = double.IsNaN(_lastSampleMs) ? 0 : Math.Clamp(now - _lastSampleMs, 0.0, 100.0);
		_elapsedMs = elapsed;
		_lastSampleMs = now;
		// LocalOf (own-character dead reckoning) keeps the slewed minimum offset.
		double drift = _offset - _playOffset;
		_playOffset = (double.IsNaN(_playOffset) || Math.Abs(drift) > 250.0) ? _offset : (_playOffset + Math.Clamp(drift, -0.02 * elapsed, 0.02 * elapsed));
		double target;
		if (!Adaptive)
		{
			target = _playOffset + DelayMs;
		}
		else
		{
			if (double.IsNaN(_lastTargetMs) || now - _lastTargetMs >= 250)
			{
				_lastTargetMs = now;
				if (_lateCount >= 30)
				{
					long[] sorted = new long[_lateCount];
					Array.Copy(_late, sorted, _lateCount);
					Array.Sort(sorted);
					// p99, not p97: the 1-3% late tail (Steam bursts) is what pulled fast bodies backwards once the floor came down.
					_targetLag = sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * 0.99))] + MarginMs;
				}
				else
				{
					_targetLag = double.NaN;
				}
			}
			target = double.IsNaN(_targetLag) ? _playOffset + DelayMs : Math.Clamp(_targetLag, _playOffset + DelayMs, _playOffset + Math.Max(DelayMs, MaxDelayMs));
			_targetDelay = target - _playOffset;
		}
		double slow = Adaptive ? StallSlowdown(_minAheadMs) : 0;
		if (double.IsNaN(_lag) || Math.Abs(target - _lag) > 250.0 || !Adaptive)
		{
			_lag = target;
			_stallDebt = 0;
		}
		else if (slow > 0 && _lag < _playOffset + MaxDelayMs)
		{
			double added = Math.Min(slow * elapsed, _playOffset + MaxDelayMs - _lag);
			_lag += added;
			_stallDebt += added;
		}
		else if (target > _lag)
		{
			_lag += Math.Min(target - _lag, (_underrun ? UnderrunRate : LagRate) * elapsed);
		}
		else if (_lag - target > 5.0)
		{
			double shrink = Math.Min(_lag - target - 5.0, (_stallDebt > 0 ? RecoverRate : ShrinkRate) * elapsed);
			_lag -= shrink;
			_stallDebt = Math.Max(0, _stallDebt - shrink);
		}
		_curDelay = _lag - _playOffset;
		if (_curDelay > MaxDelaySeenMs)
		{
			MaxDelaySeenMs = _curDelay;
		}
	}

	/// <summary>
	/// Extrapolation guesses past the newest sample; when the next sample lands the true path is somewhere else and
	/// the body snapped there (ships rubberbanded, and whoever stood on or piloted them with it). The gap is carried
	/// as an offset that decays over about 100 ms instead. Gaps over <see cref="TeleportMeters"/> are real teleports and are shown at once.
	/// </summary>
	private static Vec3D Position(byte[] v) => new(BinaryPrimitives.ReadDoubleLittleEndian(v.AsSpan(0)), BinaryPrimitives.ReadDoubleLittleEndian(v.AsSpan(8)), BinaryPrimitives.ReadDoubleLittleEndian(v.AsSpan(16)));

	private static Vec3D PathVelocity((double T, byte[] V) from, (double T, byte[] V) to) => to.T > from.T ? (Position(to.V) - Position(from.V)) * (1000.0 / (to.T - from.T)) : default;

	// The velocity of the path being shown (between its samples), not of the shown positions: right after a stall the
	// shown body moves from a held sample to a coasted one in one frame, and that step taken as a speed (391 m/s for a
	// 300 m/s ship) made the next blend run 30 % fast.
	private static byte[] Settle(Track track, double now, byte[] raw, bool extrapolated, Vec3D pathVelocity, double baseT = double.NaN)
	{
		var position = new Vec3D(BinaryPrimitives.ReadDoubleLittleEndian(raw.AsSpan(0)), BinaryPrimitives.ReadDoubleLittleEndian(raw.AsSpan(8)), BinaryPrimitives.ReadDoubleLittleEndian(raw.AsSpan(16)));
		double dt = track.HasOut ? Math.Clamp(now - track.LastOutMs, 0.0, 100.0) / 1000.0 : 0.0;
		Vec3D owed = track.Offset;
		track.Offset *= Math.Exp(-dt * 10.0);
		// The payback runs at most PaybackShare of the path speed: a 10 m overshoot at 100 m/s used to decay faster than the
		// body moved, the backward clamp then held it still, and the ship froze for 200-400 ms under ordinary network jitter.
		double pathSpeed = pathVelocity.Length;
		if (pathSpeed > 1.0)
		{
			Vec3D paid = owed - track.Offset;
			double most = PaybackShare * pathSpeed * dt;
			if (paid.Length > most) track.Offset = owed - paid * (most / paid.Length);
		}
		if (track.HasOut && dt > 0.0)
		{
			// A new sample while still coasting moves the extrapolation's start: on a stall the host spreads, that start
			// lies behind the coasted path, and the body stepped back 9 m in one frame at 300 m/s.
			if (track.WasExtrapolating && (!extrapolated || baseT != track.BaseT))
			{
				Vec3D jump = track.LastShown + track.Velocity * dt - position;
				track.Offset = jump.Length > TeleportMeters(track.Speed) ? default : jump;
				track.PaybackUntilMs = now + PaybackMs;
			}
			else
			{
				track.Velocity = pathVelocity;
				if (!extrapolated)
				{
					track.Speed = track.Velocity.Length;
					// The playout can wobble right after a stall; the heading from before it is the one not to go back on.
					if (track.Speed > 1.0 && now >= track.PaybackUntilMs) track.Heading = track.Velocity * (1.0 / track.Speed);
				}
			}
		}
		if (track.Offset.Length < 1e-4) track.Offset = default;
		// Paying back an overshoot (extrapolated through a server stall) must not drive the body backwards: it waits
		// until the real track catches up instead.
		// The direction is the path's now: a heading kept from before the correction pinned a turning ship in place once
		// its path had turned away from that heading.
		Vec3D heading = pathSpeed > 1.0 ? pathVelocity * (1.0 / pathSpeed) : track.Heading;
		if (track.Offset != default && track.HasOut && now < track.PaybackUntilMs && heading != default)
		{
			Vec3D step = position + track.Offset - track.LastShown;
			double back = step.X * heading.X + step.Y * heading.Y + step.Z * heading.Z;
			if (back < 0.0) track.Offset -= heading * back;
		}
		track.WasExtrapolating = extrapolated;
		track.BaseT = baseT;
		track.LastRaw = position;
		track.LastShown = position + track.Offset;
		track.LastOutMs = now;
		track.HasOut = true;
		if (track.Offset == default) return raw;
		byte[] shown = (byte[])raw.Clone();
		BinaryPrimitives.WriteDoubleLittleEndian(shown.AsSpan(0), track.LastShown.X);
		BinaryPrimitives.WriteDoubleLittleEndian(shown.AsSpan(8), track.LastShown.Y);
		BinaryPrimitives.WriteDoubleLittleEndian(shown.AsSpan(16), track.LastShown.Z);
		return shown;
	}

	private const long PaybackMs = 500;

	/// <summary>Share of the path speed an extrapolation overshoot is paid back at, so the body slows instead of stopping.</summary>
	public const double PaybackShare = 0.5;

	/// <summary>A gap larger than this is a teleport; a fast ship's extrapolation error (its speed over a quarter second) is not.</summary>
	public static double TeleportMeters(double speed) => 5.0 + speed * 0.25;

	public static byte[] Lerp(byte[] a, byte[] b, double t) => Lerp(a, b, t, 2.0);

	public static byte[] Lerp(byte[] a, byte[] b, double t, double maxT)
	{
		t = Math.Clamp(t, 0.0, Math.Max(1.0, maxT));
		byte[] result = new byte[40];
		for (int i = 0; i < 3; i++)
		{
			double from = BinaryPrimitives.ReadDoubleLittleEndian(a.AsSpan(i * 8));
			double to = BinaryPrimitives.ReadDoubleLittleEndian(b.AsSpan(i * 8));
			BinaryPrimitives.WriteDoubleLittleEndian(result.AsSpan(i * 8), from + (to - from) * t);
		}
		Span<float> qa = stackalloc float[4];
		Span<float> qb = stackalloc float[4];
		Span<float> q = stackalloc float[4];
		float dot = 0f;
		for (int i = 0; i < 4; i++)
		{
			qa[i] = BinaryPrimitives.ReadSingleLittleEndian(a.AsSpan(24 + i * 4));
			qb[i] = BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(24 + i * 4));
			dot += qa[i] * qb[i];
		}
		// q and -q are the same rotation; flip b so the blend takes the short way round.
		float sign = !(dot < 0f) ? 1 : -1;
		float qt = (float)Math.Min(t, 1.5);
		double length = 0.0;
		for (int i = 0; i < 4; i++)
		{
			q[i] = qa[i] + (sign * qb[i] - qa[i]) * qt;
			length += (double)q[i] * q[i];
		}
		length = Math.Sqrt(length);
		for (int i = 0; i < 4; i++)
		{
			BinaryPrimitives.WriteSingleLittleEndian(result.AsSpan(24 + i * 4), length > 1E-09 ? (float)(q[i] / length) : qb[i]);
		}
		return result;
	}

	public string Describe()
	{
		return $"smoothing {(!Enabled ? "off" : Adaptive ? $"{CurrentDelayMs:0} ms (adaptive {DelayMs}-{MaxDelayMs}, target {TargetDelayMs:0}, max {MaxDelaySeenMs:0})" : $"{DelayMs} ms")}: {ActiveCount} moving, samples {Samples}, interpolated {Interpolated}, extrapolated {Extrapolated}, held {Held}, late {Late}, too old (dropped) {Dropped}";
	}
}
