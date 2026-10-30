using System;
using System.Collections.Generic;
using System.Linq;

namespace Y51_SE2Online.Sync;

/// <summary>
/// A joined player's playback clock for everything the server moves (other players, grids, the ship this player pilots).
/// Times are server game time in TimeSpan ticks; the clock steps once per client tick (game time is ticks/60 on both
/// sides, so a step is the server time one client tick covers: 1/60 s when both run 60 ticks a second, less on a slower
/// server). The game's own clock chased the newest sample's time (10% nudges, a delay of one sample gap minus a frame), so
/// every late or bunched packet sped it up or slowed it down and its delay flipped with the gap between the last two
/// samples: remote bodies stepped back and forth, and a ship's deck slid under the feet of a predicted character.
/// This one runs at the measured server rate and a delay sized from how late samples really arrive: the newest server
/// time seen each tick is compared with where a steady clock expects it (lead); the earliest lead in the last
/// <see cref="WindowTicks"/> is the line, the 98th percentile of how far behind it arrivals are is the jitter, and the
/// cursor stays <see cref="Delay"/> (jitter plus a margin) behind the line. It eases toward that at most
/// <see cref="MaxNudge"/> off the rate, never steps back, jumps forward only when it is far behind (a join, a long stall),
/// and stops at the newest sample plus <see cref="MaxLead"/> while the stream pauses (a server stall).
/// </summary>
public sealed class PlayoutClock
{
	public const long Second = TimeSpan.TicksPerSecond, Frame = Second / 60;
	public const double MaxNudge = 0.1;
	public const long Margin = Frame / 2, MinDelay = Frame / 2, MaxDelay = Second * 3 / 10, JumpBehind = Second * 3 / 10, MaxLead = Frame * 2;
	public const int WindowTicks = 120, StallTicks = 15, JitterSamples = 240, BeatTicks = 3, QuietTicks = 4;

	/// <summary>pre2's rules (capped at the newest sample whenever no sample came, every quiet tick counted as lateness, the
	/// line started over after every long quiet): -y51online:playbeat:off.</summary>
	public bool LegacyCap;
	public double MinimumDelay = MinDelay;

	/// <summary>The most server time one tick may play (TimeSpan ticks; unlimited by default). While the player stands on a
	/// very fast ship its played-back speed must stay under Havok's 310 m/s character limit: the clock's catch-ups and
	/// the rate estimate's wobble (a few percent with 40 ms of jitter) played a 300 m/s ship at up to 316 m/s. Falling a
	/// little further behind meanwhile costs only delay.</summary>
	public double StepCap = double.PositiveInfinity;
	private bool _easing;

	/// <summary>
	/// Wall-clocked (0.18.2-pre4; off: -y51online:playwall:off): the line every arrival is measured against advances by the
	/// wall time between ticks, not one rate per tick, and only steady ticks measure. Both games stall a few times a minute
	/// (0.2-1.3 s, an autosave, a GC) and then run the missed ticks back to back. Counted per tick, a stall on this side
	/// showed every sample after it as early by the stall (the line, the earliest lead in 2 s, jumped and stayed), the
	/// back-to-back ticks after it as late, and a stall on the server's side as a pause and a burst whose every quiet tick
	/// was late: the 98th percentile sat at the 300 ms maximum, the target ran past the newest sample, and the clock held
	/// and jumped (a joined game on the same PC as its host: 352 catch-ups, 642 held ticks, 1514 blends in a minute). Now:
	/// a tick 0.5-2 frames after the last is steady; only steady ticks bringing a sample at most 3 frames past the newest
	/// record a lead and a lateness (one per arrival, no quiet ticks); ticks off that just play on at the rate; after a jump
	/// or a stall the leads start over and the next arrival is not measured. The rate is server time per wall second over
	/// 1.5 s between steady arrivals (stalls and bursts between them), exactly one frame a tick within 3%.
	/// </summary>
	public bool Wall = true;

	private const double FrameMs = 1000.0 / 60, WallSameRate = 0.03;
	private double _lastWallMs = double.NaN;
	private bool _skipNextArrival;
	private readonly double[] _gaps = new double[4];
	private int _gapAt, _gapCount;
	private double _newestWall;
	private readonly Queue<(double Wall, long Server)> _rateWindow = new();
	private double _rateWindowLast;
	private double _rateCandidateSince = double.NaN;

	/// <summary>The last tick (the trace): wall gap in frames, steady, line, error, jumped, held at the cap.</summary>
	public (double GapFrames, bool Steady, double Line, double Error, bool Jumped, bool HeldAtCap) LastTick { get; private set; }

	public double Expected => _expected;


	/// <summary>Share of arrivals the delay covers.</summary>
	public const double Quantile = 0.98;

	private const int Buckets = 8;
	private const double RateTau = 120, SameRate = 0.015, ErrorTau = 30;

	private bool _primed;
	private long _tick;
	private long _pendingNewest = long.MinValue;
	private long _newest, _newestTick;
	private double _expected, _rate = Frame, _rateServer, _rateTicks, _play, _delay = Frame;
	private readonly double[] _bucketLead = new double[Buckets];
	private readonly long[] _bucketTick = new long[Buckets];
	private readonly double[] _late = new double[JitterSamples];
	private int _lateCount, _lateAt;
	private double _jitter, _error, _newestStep = Frame;
	private readonly double[] _quiet = new double[StallTicks];
	private int _quietCount;
	private long _jitterTick;

	public long Jumps, Stalls, Held;

	/// <summary>The game's MotionHistory.Cadence that makes its cursor (clock - (cadence - frame step) when the cadence is
	/// over a frame step, else the clock) land on <paramref name="cursor"/>.</summary>
	public static long CadenceFor(long clock, long cursor, long step) => Math.Max(0, clock - cursor) + step;

	/// <summary>Server time per client tick (TimeSpan ticks).</summary>
	public double Rate => _rate;

	/// <summary>How far behind the newest expected sample the cursor plays (TimeSpan ticks).</summary>
	public double Delay => _delay;

	/// <summary>The <see cref="Quantile"/> share of arrival lateness (TimeSpan ticks).</summary>
	public double Jitter => _jitter;

	public bool Primed => _primed;

	public double Play => _play;

	/// <summary>The newest server time any sample carried.</summary>
	public long Newest => _newest;

	public void Reset()
	{
		_primed = false;
		_pendingNewest = long.MinValue;
		_rate = Frame;
		_rateServer = _rateTicks = 0;
		_delay = Frame;
		_lateCount = _lateAt = 0;
		_quietCount = 0;
		_jitter = 0;
		Array.Clear(_bucketTick);
		_lastWallMs = double.NaN;
		_rateWindow.Clear();
		_rateCandidateSince = double.NaN;
		_gapAt = _gapCount = 0;
		_skipNextArrival = false;
	}

	/// <summary>A sample with server time <paramref name="serverTime"/> was read this client tick.</summary>
	public void Arrive(long serverTime)
	{
		if (serverTime > _pendingNewest) _pendingNewest = serverTime;
	}

	/// <summary>Something from the server (any frame or state message) was read this client tick: the server runs, even
	/// when nothing it moves changed (no motion samples then).</summary>
	public void Heartbeat() => _beat = true;

	private bool _beat;
	private long _beatTick;
	private double _beatGap = 1;

	/// <summary>Once per client tick, after its update: the play time for the next tick. <paramref name="wallMs"/>: a
	/// steady wall clock in milliseconds (NaN: the tick-counted clock of pre3).</summary>
	public double Advance(double wallMs = double.NaN)
	{
		long tick = ++_tick;
		bool wall = Wall && !LegacyCap && !double.IsNaN(wallMs);
		double gap = 1;
		if (wall)
		{
			gap = double.IsNaN(_lastWallMs) ? 1 : Math.Max(0, (wallMs - _lastWallMs) / FrameMs);
			_lastWallMs = wallMs;
		}
		// Steady: the last four ticks average 0.5-2 frames apart (under 30 frames a second ticks come in pairs, one right on
		// the other: steady too); a stall and the back-to-back ticks after it are not.
		_gaps[_gapAt] = gap;
		_gapAt = (_gapAt + 1) % _gaps.Length;
		if (_gapCount < _gaps.Length) _gapCount++;
		double meanGap = 0;
		for (int i = 0; i < _gapCount; i++) meanGap += _gaps[i];
		meanGap /= _gapCount;
		bool steady = !wall || meanGap >= 0.5 && meanGap <= 2 && gap <= 4;
		bool jumped = false, heldAtCap = false;
		_expected += wall ? _rate * Math.Min(gap, 600) : _rate;
		if (_beat)
		{
			// Frames come only when the server has something to send: how far apart they usually are, so a world where only a
			// 10 Hz body changes still counts as running between them.
			if (_beatTick != 0) _beatGap += (Math.Min(tick - _beatTick, StallTicks) - _beatGap) / 16;
			_beat = false;
			_beatTick = tick;
		}
		if (_pendingNewest != long.MinValue)
		{
			long s = _pendingNewest;
			_pendingNewest = long.MinValue;
			if (!_primed)
			{
				_primed = true;
				_newest = s;
				_newestTick = tick;
				_expected = s;
				_play = s - _delay;
				_error = 0;
				Array.Clear(_bucketTick);
				Lead(tick, 0);
			}
			else if (s > _newest && wall)
			{
				long sinceNewest = tick - _newestTick;
				// More than three of the usual steps at once: a burst (a stall's ticks, or packets held up on the way).
				bool burst = s - _newest > 3 * Math.Max(Math.Max(_rate, _newestStep), Frame);
				if (sinceNewest > StallTicks)
				{
					// A long quiet: the leads start over; when server time barely moved meanwhile (it stood still, no catch-up)
					// the line starts over from here too.
					Array.Clear(_bucketTick);
					Stalls++;
					_skipNextArrival = true;
					// The server's time stood still (no catch-up): the line starts over from here, and the rate is measured
					// from here (a stall in the window would read as a slower server for its length).
					if (s - _newest < 0.5 * (wallMs - _newestWall) / FrameMs * _rate) { _expected = s; _rateWindow.Clear(); _rateCandidateSince = double.NaN; }
				}
				bool measure = steady && !burst && !_skipNextArrival;
				if (measure) WallRate(wallMs, s);
				if (!burst) _skipNextArrival = false;
				_newestStep += (Math.Min(s - _newest, Second / 4) - _newestStep) / 16;
				_newest = s;
				_newestTick = tick;
				_newestWall = wallMs;
				if (measure) Lead(tick, s - _expected);
				_measuredTick = measure ? tick : 0;
			}
			else if (s > _newest)
			{
				long sinceNewest = tick - _newestTick;
				// Server time moved on about as far as our ticks did while nothing came: the samples were held up on the way
				// (or nothing moved); much less: the server itself stood still.
				bool serverRan = s - _newest >= 0.5 * sinceNewest * _rate;
				if (sinceNewest <= StallTicks && serverRan)
					for (int i = 0; i < _quietCount; i++) Late(_quiet[i]);
				_quietCount = 0;
				if (sinceNewest > StallTicks)
				{
					// The leads from before a long quiet say nothing about now (a hitch inside it moved the line). After a server
					// stall (its game time stood still too) the line starts over from here; after a quiet the server ran through
					// (nothing moved) it keeps its place. No rate sample either way.
					Array.Clear(_bucketTick);
					if (LegacyCap || !serverRan) { Stalls++; _expected = s; }
				}
				else
				{
					double decay = Math.Exp(-sinceNewest / RateTau);
					_rateServer = _rateServer * decay + (s - _newest);
					_rateTicks = _rateTicks * decay + sinceNewest;
					if (_rateTicks >= 30)
					{
						// Both sides at 60 ticks a second (within a server's usual wobble): exactly one frame a tick, so the
						// estimate's own noise (arrivals land on whole ticks) never shows as speed changes.
						double measured = Math.Clamp(_rateServer / _rateTicks, Frame * 0.25, Frame * 2.5);
						_rate = Math.Abs(measured / Frame - 1) < SameRate ? Frame : measured;
					}
				}
				_newestStep += (Math.Min(s - _newest, Second / 4) - _newestStep) / 16;
				_newest = s;
				_newestTick = tick;
				Lead(tick, s - _expected);
			}
		}
		if (!_primed) return 0;

		// The pair for the next tick is picked now, from what is here now: the next tick's cursor must not pass the newest
		// sample. How far that newest sample is behind the line is measured every tick (a tick that brought nothing counts).
		double line = _expected + Line(tick);
		// A quiet tick is jitter when the stream was held up on the way (its next samples come in a burst, server time moved
		// on meanwhile) and not when the server itself stood still (a hitch: its time barely moved). Counted always, an
		// autosave kept the delay near its maximum for minutes; never counted, every 70-250 ms network hiccup ran the
		// cursor out of samples. So quiet ticks wait for the next sample to tell which.
		double late = Math.Max(0, line - _newest);
		if (wall) { if (tick == _measuredTick) Late(late); }
		else if (tick == _newestTick) Late(late);
		// A tick that brought frames but no sample: the server runs and nothing it moves changed; not lateness.
		else if (LegacyCap || tick != _beatTick) { if (_quietCount < _quiet.Length) _quiet[_quietCount++] = late; }
		if (tick - _jitterTick >= 30) { _jitterTick = tick; _jitter = LatenessQuantile(); }
		// Bodies sent less often than every tick add their own gap (PlayoutTrack.Cursor).
		double wanted = Math.Clamp(_jitter + Margin, MinimumDelay, MaxDelay);
		// Up as fast as one frame a tick (late packets started: the cursor slows at most 10% anyway), down by half a percent of
		// the difference a tick (about 2.3 s to halve it).
		_delay = wanted > _delay ? Math.Min(wanted, _delay + Frame) : _delay - (_delay - wanted) * 0.005;
		double target = line - _delay;
		double error = target - (_play + _rate);
		double next;
		// Wall-clocked with no lead in the window yet (just after a jump or a stall): no line to steer by.
		if (!steady || wall && !HaveLine(tick))
		{
			// A tick right on the last one (the game catching up after a stall) or long after it: the wall-clocked line
			// says nothing a steady tick would; play on at the rate.
			next = _play + _rate;
			goto Capped;
		}
		// The line moves in steps (an early packet enters or leaves the window): the error is filtered over half a second
		// and corrected over a second, so the speed never changes by more than a few hundredths of a percent a tick.
		_error += (error - _error) / ErrorTau;
		// Capped (a fast deck), a catch-up does not jump: it eases at the cap, and the error filter goes on; once easing it
		// eases on until caught up, also if the deck slows meanwhile (a 300 ms jump at 229 m/s put the deck 69 m on).
		if (error <= JumpBehind) _easing = false;
		else if (!double.IsPositiveInfinity(StepCap)) _easing = true;
		// Easing goes on until caught up even when the cap is lifted (the deck slowed or was left): no jump meanwhile.
		bool capped = _easing && error < 10 * JumpBehind;
		if (error > JumpBehind && !capped)
		{
			next = target;
			_error = 0;
			Jumps++;
			jumped = true;
			if (wall) { Array.Clear(_bucketTick); _skipNextArrival = true; }
		}
		else next = _play + _rate * (1 + Math.Clamp((error > JumpBehind ? error : _error) / Second, -MaxNudge, MaxNudge));
		if (capped && next - _play > StepCap) next = _play + StepCap;
		// While the stream pauses: no further than the game extrapolates (two of its usual steps past the newest sample), so nothing stands at its
		// extrapolation limit and then jumps back when the stream resumes.
		// Not while frames keep coming but no sample has for a while: the server runs and nothing it moves changed, and
		// holding there froze every body, then sent the clock leaping when something moved again (0.18.2-pre2 on a co-op
		// host: 314 catch-ups and 451 held ticks a minute). While samples flow, or nothing at all comes (a stall, a cut),
		// it holds as before. Each body still stops at the end of its own extrapolation (PlayoutTrack).
		Capped:
		if (!steady && next - _play > StepCap) next = _play + StepCap;
		double cap = _newest + Math.Max(MaxLead, 2 * _newestStep);
		bool idleWorld = !LegacyCap && tick - _newestTick > QuietTicks && tick - _beatTick <= Math.Max(BeatTicks, Math.Min(StallTicks, 2 * _beatGap));
		if (!idleWorld && next > cap) { next = Math.Max(_play, cap); Held++; heldAtCap = true; }
		_play = Math.Max(_play, next);
		LastTick = (gap, steady, line, error, jumped, heldAtCap);
		return _play;
	}

	private long _measuredTick;

	// Server time per wall second over at least 1.5 s between steady arrivals (a stall and its burst may lie inside), as
	// server time per client frame; exactly a frame within 3%.
	private void WallRate(double wallMs, long server)
	{
		if (_rateWindow.Count == 0 || wallMs - _rateWindowLast >= 250) { _rateWindow.Enqueue((wallMs, server)); _rateWindowLast = wallMs; }
		// The youngest entry at least 1.5 s old (arrivals at both ends steady, a stall and its burst may lie between).
		while (_rateWindow.Count > 2 && wallMs - _rateWindow.ElementAt(1).Wall >= 1500) _rateWindow.Dequeue();
		var (w, s0) = _rateWindow.Peek();
		if (wallMs - w < 1500) return;
		double measured = Math.Clamp((server - s0) / (wallMs - w) * FrameMs, Frame * 0.25, Frame * 2.5);
		double departure = Math.Abs(measured / Frame - 1);
		// A short packet burst can skew this window by a few percent; only a sustained change slows the visible ship.
		if (departure >= WallSameRate && departure < 0.1)
		{
			if (double.IsNaN(_rateCandidateSince)) _rateCandidateSince = wallMs;
			if (wallMs - _rateCandidateSince < 500) return;
		}
		else _rateCandidateSince = double.NaN;
		_rate = Math.Abs(measured / Frame - 1) < WallSameRate ? Frame : measured;
	}

	private bool HaveLine(long tick)
	{
		for (int i = 0; i < Buckets; i++)
			if (_bucketTick[i] != 0 && tick - _bucketTick[i] < WindowTicks) return true;
		return false;
	}

	// The earliest lead in the window (the line every arrival is measured against).
	private double Line(long tick)
	{
		double best = double.NegativeInfinity;
		for (int i = 0; i < Buckets; i++)
			if (_bucketTick[i] != 0 && tick - _bucketTick[i] < WindowTicks && _bucketLead[i] > best) best = _bucketLead[i];
		return double.IsNegativeInfinity(best) ? 0 : best;
	}

	private void Lead(long tick, double lead)
	{
		int b = (int)(tick / (WindowTicks / Buckets) % Buckets);
		if (_bucketTick[b] == 0 || tick - _bucketTick[b] >= WindowTicks / Buckets) { _bucketTick[b] = tick; _bucketLead[b] = lead; }
		else _bucketLead[b] = Math.Max(_bucketLead[b], lead);
	}

	private void Late(double late)
	{
		_late[_lateAt] = late;
		_lateAt = (_lateAt + 1) % JitterSamples;
		if (_lateCount < JitterSamples) _lateCount++;
	}

	private double LatenessQuantile()
	{
		if (_lateCount == 0) return 0;
		var copy = new double[_lateCount];
		Array.Copy(_late, copy, _lateCount);
		Array.Sort(copy);
		return copy[Math.Min(_lateCount - 1, (int)(_lateCount * Quantile))];
	}
}

/// <summary>
/// One remote body's samples on the <see cref="PlayoutClock"/>: the pair the game's playback interpolates at this body's
/// cursor. A body sent less often than every server tick (far away, thinned) plays its own sample gap further back, the
/// change eased in at most <see cref="LagSlew"/> of a tick's step so its cursor never runs backwards. A teleport is never
/// interpolated into (the old pose holds until the cursor reaches it). A stopped body's old pose holds until one gap
/// before its next sample; a moving body crosses a long sample gap when its velocity predicts the next pose closely.
/// </summary>
public sealed class PlayoutTrack<T>
{
	public const double LagSlew = 0.05;

	/// <summary>A reordered sample splits the gap last counted (off: -y51online:playgap:off, pre2's gaps).</summary>
	public static bool SplitReordered = true;

	/// <summary>How far past its newest sample this body may go on by its velocity, over the game's two gaps (TimeSpan
	/// ticks; the deck the player stands on).</summary>
	public long LongExtrapolation;

	/// <summary>The distance between two pairs' poses at their factors (old pair, its factor, new pair, its factor); with it, a
	/// late pair whose pose is within <see cref="BlendFrom"/> of the old track's is taken without a blend.</summary>
	public Func<T, T, double, T, T, double, double>? BlendGap;
	/// <summary>Distance between poses, used to distinguish a server stall from a body that stopped sending motion.</summary>
	public Func<T, T, double>? PositionGap;

	public const double BlendFrom = 0.05;

	public static long BlendsSkipped;

	/// <summary>The most this body's cursor may move in one tick (TimeSpan ticks; unlimited by default).</summary>
	public double CursorStepCap = double.PositiveInfinity;

	private long _lastGapTime = long.MinValue;
	public const int PauseGaps = 4, GapSamples = 8;

	private readonly List<(long Time, bool Teleport, bool Resting, T Value)> _items = new();
	private readonly Func<T, long, T> _retime;
	private readonly Func<T, long, T>? _extrapolate;
	private readonly long[] _gaps = new long[GapSamples], _sorted = new long[GapSamples];
	private int _gapCount, _gapAt;
	private double _lag;

	/// <param name="extrapolate">A sample carried forward to a later time by its own velocity (null: the game's
	/// extrapolation from the last two samples).</param>
	/// <param name="byVelocity">Whether a sample may be carried by its velocity (else the game's line through the last two).</param>
	public PlayoutTrack(Func<T, long, T> retime, Func<T, long, T>? extrapolate = null, Func<T, bool>? byVelocity = null) => (_retime, _extrapolate, _byVelocity) = (retime, extrapolate, byVelocity);

	private readonly Func<T, bool>? _byVelocity;

	public int Count => _items.Count;

	/// <summary>This body's typical sample gap (median of the last few).</summary>
	public long Gap { get; private set; } = PlayoutClock.Frame;

	/// <summary>How much further back than the clock this body plays.</summary>
	public double Lag => _lag;

	public long NewestTime => _items.Count > 0 ? _items[^1].Time : 0;

	/// <summary>The newest sample (its playback went: it gets that one, as the game would).</summary>
	public bool Last(out T value)
	{
		value = _items.Count > 0 ? _items[^1].Value : default!;
		return _items.Count > 0;
	}

	private int LastAtOrBefore(double time)
	{
		for (int i = _items.Count - 1; i >= 0; i--)
			if (_items[i].Time <= time) return i;
		return -1;
	}

	public void Add(long time, bool teleport, bool rest, T value)
	{
		int at = LastAtOrBefore(time);
		if (at >= 0 && _items[at].Time == time) { _items[at] = (time, teleport, rest, value); return; }
		if (at == _items.Count - 1 && at >= 0 && !teleport && !_items[at].Resting && !_items[at].Teleport)
		{
			long gap = time - _items[at].Time;
			if (gap > 0 && gap < PlayoutClock.Second) { AddGap(gap, replaceLast: false); _lastGapTime = time; }
		}
		// A sample that came after the newer one (reordered on the way) splits the gap last counted: counted whole, every
		// reorder made a two-tick gap and flipped the median, and the body's cursor slewed 5% off the clock for it (a
		// 300 m/s ship played at 285-315 m/s).
		else if (SplitReordered && at >= 0 && at == _items.Count - 2 && _gapCount > 0 && !teleport && !_items[at].Resting && !_items[at].Teleport
			&& _lastGapTime == _items[at + 1].Time)
		{
			AddGap(time - _items[at].Time, replaceLast: true);
			AddGap(_items[at + 1].Time - time, replaceLast: false);
		}
		_items.Insert(at + 1, (time, teleport, rest, value));
	}

	private void AddGap(long gap, bool replaceLast)
	{
		if (replaceLast) _gapAt = (_gapAt + GapSamples - 1) % GapSamples;
		else if (_gapCount < GapSamples) _gapCount++;
		_gaps[_gapAt] = gap;
		_gapAt = (_gapAt + 1) % GapSamples;
		Array.Copy(_gaps, _sorted, _gapCount);
		Array.Sort(_sorted, 0, _gapCount);
		Gap = _sorted[_gapCount / 2];
	}

	/// <summary>This body's cursor for clock time <paramref name="play"/> (one call per client tick, <paramref name="step"/>
	/// the clock's step).</summary>
	public double Cursor(double play, double step, double rate = 0)
	{
		double want = Math.Max(0, Gap - PlayoutClock.Frame);
		double most = Math.Abs(step) * LagSlew;
		_lag += Math.Clamp(want - _lag, -most, most);
		// A body with a long extrapolation (the deck the player stands on) goes on at the clock's rate while the clock holds
		// at the end of the stream (a burst held it up), up to its long extrapolation, and gives that lead back as the clock
		// catches up. The clock itself holds for every other body as before.
		if (LongExtrapolation <= 0 || rate <= 0) Lead = 0;
		else if (step < 0.5 * rate) Lead = Math.Min(LongExtrapolation, Lead + rate - Math.Max(0, step));
		else if (step > rate) Lead = Math.Max(0, Lead - (step - rate));
		return play - _lag + Lead;
	}

	/// <summary>How far ahead of the clock this body plays (its cursor's lead while the clock held; see Cursor).</summary>
	public double Lead { get; private set; }

	/// <summary>A switch from an extrapolated pose to a pair that came in late, eased like the game's own blend: the old
	/// pair's track fades out over <see cref="Duration"/> from <see cref="Start"/> (cursor time).</summary>
	public readonly record struct Blend(T A, T B, double Start, double Duration);

	/// <summary>Longest blend (the game's own is capped the same way since 0.17).</summary>
	public const long MaxBlend = PlayoutClock.Second / 10;

	/// <summary>MotionInterpolationConfiguration.MaxExtrapolationGaps.</summary>
	public const int ExtrapolationGaps = 2;

	// The pair last played (the velocity-carried one past the newest sample), and the real samples it came from.
	private (long Time, T Value) _lastPrevious, _lastNext;
	private (long Time, T Value) _lastRealPrevious;
	private long _lastRealNext;
	private bool _lastExtrapolating, _havePick;
	private double _lastCursor;
	private Blend? _blend;

	/// <summary>
	/// The pair to play at <paramref name="cursor"/> (kept between this body's last cursor and its extrapolation limit):
	/// previous at or before it, next after it (both the same: hold);
	/// <paramref name="extrapolating"/> when the newest sample is already behind the cursor; <paramref name="blend"/> while a
	/// late pair is eased in. False with no samples. Samples older than the previous one are dropped.
	/// </summary>
	public bool Pick(ref double cursor, out T previous, out T next, out bool extrapolating, out Blend? blend)
	{
		blend = null;
		// Never back past this body's last cursor; never on past where the game's extrapolation of the newest pair stops
		// (it would stand there, then jump back once the stream resumes).
		if (_havePick && cursor < _lastCursor) cursor = _lastCursor;
		if (_havePick && cursor > _lastCursor + CursorStepCap) cursor = _lastCursor + CursorStepCap;
		if (!Choose(cursor, out var p, out var n, out extrapolating, out bool teleport))
		{
			previous = next = default!;
			return false;
		}
		if (extrapolating && p.Time < n.Time)
		{
			double limit = Limit(p.Time, n.Value, n.Time);
			// A late sample between the two just shortened the pair (and the game's extrapolation with it): the previous pick's
			// older sample keeps the cursor inside it.
			if (cursor > limit && _havePick && _lastRealNext == n.Time && _lastRealPrevious.Time < p.Time)
			{
				p = _lastRealPrevious;
				limit = Limit(p.Time, n.Value, n.Time);
			}
			if (cursor > limit) cursor = Math.Max(limit, _havePick ? _lastCursor : limit);
		}
		_lastCursor = cursor;
		var realPrevious = p;
		long realNext = n.Time;
		previous = p.Value;
		next = n.Value;
		// Past the newest sample: carried on by its own velocity, not by the line through the last two (a body that just
		// stopped, braked or turned overshot by up to two of its gaps and then popped back: the split-second ghost).
		if (extrapolating && _extrapolate != null && p.Time < n.Time && (_byVelocity == null || _byVelocity(n.Value)))
		{
			long until = Limit(p.Time, n.Value, n.Time);
			p = n;
			n = (until, _extrapolate(n.Value, until));
			previous = p.Value;
			next = n.Value;
		}
		if (teleport) _blend = null;
		else if (_havePick && _lastExtrapolating && realNext > _lastRealNext && cursor > _lastRealNext && _lastPrevious.Time < _lastNext.Time)
		{
			// As long as the cursor ran past the old newest sample; the old track stops where the game's extrapolation of the
			// pair last played stops: a blend that outlasted that would pull toward a pose standing still.
			double end = _lastNext.Time + ExtrapolationGaps * (_lastNext.Time - _lastPrevious.Time);
			double duration = Math.Min(Math.Min(MaxBlend, cursor - _lastRealNext), end - cursor);
			_blend = duration > 0 ? new Blend(_lastPrevious.Value, _lastNext.Value, cursor, duration) : null;
			// The old track and the new one about the same here (a body carried on by its velocity that kept to it): the new
			// pair is taken at once. Blended, every late sample pulled the body back toward a track that stopped, for 100 ms.
			if (_blend != null && BlendGap != null && n.Time > p.Time && _lastNext.Time > _lastPrevious.Time
				&& BlendGap(_lastPrevious.Value, _lastNext.Value, (cursor - _lastPrevious.Time) / (_lastNext.Time - _lastPrevious.Time), p.Value, n.Value, (cursor - p.Time) / (n.Time - p.Time)) < BlendFrom)
			{
				_blend = null;
				BlendsSkipped++;
			}
		}
		if (_blend is { } b && cursor - b.Start >= b.Duration) _blend = null;
		blend = _blend;
		_lastPrevious = p;
		_lastNext = n;
		_lastRealPrevious = realPrevious;
		_lastRealNext = realNext;
		_lastExtrapolating = extrapolating;
		_havePick = true;
		return true;
	}

	// Where extrapolating the pair stops: two of its gaps (the game's), or this body's long extrapolation by its velocity.
	private long Limit(long previous, T newest, long newestTime)
	{
		long limit = newestTime + ExtrapolationGaps * (newestTime - previous);
		return LongExtrapolation > 0 && _extrapolate != null && (_byVelocity == null || _byVelocity(newest)) ? Math.Max(limit, newestTime + LongExtrapolation) : limit;
	}

	private bool Choose(double cursor, out (long Time, T Value) previous, out (long Time, T Value) next, out bool extrapolating, out bool teleport)
	{
		previous = next = default;
		extrapolating = teleport = false;
		if (_items.Count == 0) return false;
		int i = LastAtOrBefore(cursor);
		if (i < 0)
		{
			previous = next = (_items[0].Time, _items[0].Value);
			return true;
		}
		if (i > 1) { _items.RemoveRange(0, i - 1); i = 1; }
		teleport = _items[i].Teleport;
		if (i == _items.Count - 1)
		{
			extrapolating = !_items[i].Resting;
			next = (_items[i].Time, _items[i].Value);
			previous = i > 0 && !_items[i].Teleport && !Paused(_items[i - 1], _items[i]) ? (_items[i - 1].Time, _items[i - 1].Value) : next;
			return true;
		}
		var a = _items[i];
		var b = _items[i + 1];
		if (b.Teleport) { previous = next = (a.Time, a.Value); return true; }
		if (Paused(a, b))
		{
			long from = b.Time - Gap;
			if (cursor < from) { previous = next = (a.Time, a.Value); return true; }
			previous = (from, _retime(a.Value, from));
			next = (b.Time, b.Value);
			return true;
		}
		previous = (a.Time, a.Value);
		next = (b.Time, b.Value);
		return true;
	}

	private bool Paused(in (long Time, bool Teleport, bool Resting, T Value) a, in (long Time, bool Teleport, bool Resting, T Value) b)
	{
		if (a.Resting) return true;
		if (b.Time - a.Time <= PauseGaps * Math.Max(Gap, PlayoutClock.Frame)) return false;
		if (_extrapolate == null || PositionGap == null || _byVelocity != null && !_byVelocity(a.Value)) return true;
		var continued = _extrapolate(a.Value, b.Time);
		double travel = PositionGap(a.Value, continued);
		return PositionGap(continued, b.Value) > Math.Max(0.05, travel * 0.25);
	}
}
