using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Y51_SE2Online.Sync;

public sealed class TransformSmoother
{
	private sealed class Track
	{
		public readonly List<(long T, byte[] V)> Samples = new List<(long, byte[])>();

		public long LastArrivalMs;

		public bool Active = true;

		public bool FinalApplied;

		public bool WasExtrapolating;

		public long LastOutMs;

		public Vec3D LastRaw, LastShown, Velocity, Offset;

		// Speed and heading along the real (not extrapolated) track: a body held at the end of its extrapolation has Velocity 0.
		public double Speed;

		public Vec3D Heading;

		public long PaybackUntilMs;

		public bool HasOut;
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

	private double _targetDelay = -1.0;

	private long _lastSampleMs = long.MinValue;

	private long _lastLateArrivalMs = long.MinValue;

	private long _lastTargetMs = long.MinValue;

	private readonly int[] _late = new int[512];

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
			long t = Unwrap(hostMs);
			long arrivalOffset = NoteClockLocked(t, localNowMs);
			if (localNowMs != _lastLateArrivalMs)
			{
				_lastLateArrivalMs = localNowMs;
				_late[_lateHead] = (int)Math.Clamp(arrivalOffset - _offset, 0L, 5000L);
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
			return (_offset == long.MinValue) ? null : (Unwrap(hostMs) + (double.IsNaN(_playOffset) ? _offset : _playOffset));
		}
	}

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

	public void Sample(long localNowMs, List<(uint NetId, byte[] Value)> output)
	{
		lock (_sync)
		{
			if (_offset == long.MinValue)
			{
				return;
			}
			UpdateDelay(localNowMs);
			// Fractional: rounding the playout time to whole milliseconds jittered fast bodies by speed x 0.5 ms.
			double playTime = localNowMs - (_playOffset + CurrentDelayMs);
			foreach (var (netId, track) in _tracks)
			{
				if (!track.Active || track.Samples.Count == 0)
				{
					continue;
				}
				var samples = track.Samples;
				var newest = samples[samples.Count - 1];
				if ((double)(playTime - newest.T) > CurrentDelayMs + MaxExtrapolateMs + IdleMs)
				{
					if (!track.FinalApplied)
					{
						output.Add((netId, newest.V));
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
							output.Add((netId, Settle(track, localNowMs, Lerp(previous.V, newest.V, 1.0 + Math.Min(ahead, MaxExtrapolateMs) / interval, 1.0 + (double)MaxExtrapolateMs / interval), extrapolated: true)));
							if (ahead <= MaxExtrapolateMs) Extrapolated++;
							else Held++;
							continue;
						}
					}
					output.Add((netId, Settle(track, localNowMs, newest.V, extrapolated: false)));
					Held++;
				}
				else
				{
					int i = 1;
					while (i < samples.Count && samples[i].T < playTime) i++;
					var from = samples[i - 1];
					var to = samples[i];
					double t = to.T == from.T ? 1.0 : (double)(playTime - from.T) / (double)(to.T - from.T);
					output.Add((netId, Settle(track, localNowMs, Lerp(from.V, to.V, t), extrapolated: false)));
					Interpolated++;
					if (i - 1 > 0)
					{
						samples.RemoveRange(0, i - 1);
					}
				}
			}
		}
	}

	private void UpdateDelay(long now)
	{
		double elapsed = _lastSampleMs == long.MinValue ? 0 : Math.Clamp(now - _lastSampleMs, 0L, 100L);
		_lastSampleMs = now;
		// Offset estimate steps +-2 ms; stepping playout with it jerks every remote body by speed x step, so slew instead.
		double drift = _offset - _playOffset;
		_playOffset = (double.IsNaN(_playOffset) || Math.Abs(drift) > 250.0) ? _offset : (_playOffset + Math.Clamp(drift, -0.02 * elapsed, 0.02 * elapsed));
		if (!Adaptive)
		{
			_curDelay = _targetDelay = DelayMs;
			return;
		}
		if (_curDelay < 0.0)
		{
			_curDelay = DelayMs;
		}
		if (_lastTargetMs == long.MinValue || now - _lastTargetMs >= 250)
		{
			_lastTargetMs = now;
			if (_lateCount >= 30)
			{
				int[] sorted = new int[_lateCount];
				Array.Copy(_late, sorted, _lateCount);
				Array.Sort(sorted);
				// p99, not p97: the 1-3% late tail (Steam bursts) is what pulled fast bodies backwards once the floor came down.
				int p99 = sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * 0.99))];
				_targetDelay = Math.Clamp(p99 + MarginMs, DelayMs, Math.Max(DelayMs, MaxDelayMs));
			}
			else
			{
				_targetDelay = DelayMs;
			}
		}
		double step = _targetDelay - _curDelay;
		_curDelay += step > 0.0 ? Math.Min(step, 0.06 * elapsed) : Math.Max(step, -0.03 * elapsed);
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
	private static byte[] Settle(Track track, long now, byte[] raw, bool extrapolated)
	{
		var position = new Vec3D(BinaryPrimitives.ReadDoubleLittleEndian(raw.AsSpan(0)), BinaryPrimitives.ReadDoubleLittleEndian(raw.AsSpan(8)), BinaryPrimitives.ReadDoubleLittleEndian(raw.AsSpan(16)));
		double dt = track.HasOut ? Math.Clamp(now - track.LastOutMs, 0L, 100L) / 1000.0 : 0.0;
		track.Offset *= Math.Exp(-dt * 10.0);
		if (track.HasOut && dt > 0.0)
		{
			if (track.WasExtrapolating && !extrapolated)
			{
				Vec3D jump = track.LastShown + track.Velocity * dt - position;
				track.Offset = jump.Length > TeleportMeters(track.Speed) ? default : jump;
				track.PaybackUntilMs = now + PaybackMs;
			}
			else
			{
				track.Velocity = (position - track.LastRaw) * (1.0 / dt);
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
		if (track.Offset != default && track.HasOut && now < track.PaybackUntilMs && track.Heading != default)
		{
			Vec3D step = position + track.Offset - track.LastShown;
			double back = step.X * track.Heading.X + step.Y * track.Heading.Y + step.Z * track.Heading.Z;
			if (back < 0.0) track.Offset -= track.Heading * back;
		}
		track.WasExtrapolating = extrapolated;
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
