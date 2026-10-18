using System;

namespace Y51_SE2Online.Sync;

/// <summary>
/// An even clock for something SE2 advances one fixed step at a time (a server tick, a client frame). Each call moves it
/// by the average step, learnt over about half a second, instead of by that call's wall-clock delta: SE2 moves its own
/// world by the same amount every frame, so anything drawn against it must too, and a phase-locked clock that passed
/// 10 % of each frame's wobble straight through still made bodies advance 3-5 % unevenly. The clock stays on the wall
/// clock through a slow pull (at most 0.5 % of a step while within one step of it), a faster but smooth one when an fps
/// change leaves it further out, a catch-up of at most 0.4 of a step per step after a hitch (half a step for server stamps; the own body, drawn on the frame clock, blends the hitch in too, and half a step showed as 1.6 steps), and a forward snap past
/// <see cref="ResyncMs"/>. It never goes back.
/// </summary>
public sealed class TickClock
{
	public const double ResyncMs = 250.0;

	private const double RateGain = 1.0 / 30.0, PhaseGain = 0.02, InBandCap = 0.005, MaxGain = 0.15, SlowDown = 0.25;

	private double _last = -1.0, _prev = -1.0;

	private bool _followed;

	private double _error;

	private readonly double _errorGain, _catchUp;

	/// <param name="paced">For server tick stamps. SE2 paces its frames, so a late tick is mostly followed by early ones,
	/// and chasing each one ran the stamps ahead afterwards: remote bodies, which move one tick per stamp step, then
	/// moved 10 % fast for half a second after every server hitch. Paced follows only what persists (about 8 ticks).
	/// A client's frame clock keeps the plain clock: its catch-up after a hitch must start at once to keep the own body in step.</param>
	public TickClock(bool paced = false)
	{
		_errorGain = paced ? 1.0 / 8.0 : 1.0;
		_catchUp = paced ? 0.5 : 0.4;
	}

	public double PeriodMs { get; private set; } = 1000.0 / 60.0;

	public long Resyncs { get; private set; }

	/// <summary>How far the clock is behind the wall clock it was last given (negative: ahead).</summary>
	public double BehindMs => _last < 0.0 ? 0.0 : _prev - _last;

	/// <summary>Takes <paramref name="nowMs"/> as this step's time (the rate is kept), never going back.</summary>
	public double Follow(double nowMs)
	{
		_followed = true;
		_error = 0.0;
		_prev = nowMs;
		return _last = _last < 0.0 ? nowMs : Math.Max(nowMs, _last + 0.001);
	}

	public double Next(double nowMs)
	{
		if (_last < 0.0)
		{
			_prev = nowMs;
			return _last = nowMs;
		}
		double step = nowMs - _prev;
		_prev = nowMs;
		// Back from following: the caller's time is already even and is where the bodies around it are drawn.
		if (_followed)
		{
			_followed = false;
			return _last = Math.Max(nowMs, _last + 0.001);
		}
		double period = PeriodMs;
		// A hitch is caught up below, not learnt as a frame rate (long frames that keep coming are, through the nudge below).
		if (step > 0.0 && step <= 2.5 * period) PeriodMs = Math.Clamp(period + RateGain * (step - period), 1000.0 / 240.0, ResyncMs);
		double error = nowMs - (_last + period);
		if (error > ResyncMs)
		{
			Resyncs++;
			_error = 0.0;
			return _last = nowMs;
		}
		error = _error += _errorGain * (error - _error);
		double away = Math.Abs(error);
		// Persistently more than a step out (the frame rate changed, or long frames the rate above under-counts): the rate
		// itself is off, so it is nudged too.
		if (away > period && away < 3.0 * period) PeriodMs = Math.Clamp(PeriodMs + 0.002 * error, 1000.0 / 240.0, ResyncMs);
		double pull = away <= period
			? Math.Clamp(PhaseGain * error, -InBandCap * period, InBandCap * period)
			: Math.Clamp(Math.Min(MaxGain, PhaseGain + (away - period) / (20.0 * period)) * error, -SlowDown * period, _catchUp * period);
		return _last = Math.Max(_last + period + pull, _last + 0.001);
	}
}
