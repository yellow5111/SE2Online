using System;

namespace Y51_SE2Online.Net;

/// <summary>
/// A host's Steam send rate floor (SendRateMin) for one player's connection. Steam's own estimate never rose above the
/// floor on any WAN join on record (1 MiB/s, so a 37 MiB world took 38 s at quality 1.00 and no loss), so a host starts
/// each player at <see cref="StartBytes"/> and backs off where the link shows it cannot carry that: packets lost (the
/// player's view of the connection under <see cref="MinQuality"/>) or ping <see cref="QueuePingMs"/> over the lowest of
/// the last <see cref="BaselineLooks"/> (queueing; a relay re-route that adds ping for good becomes the new baseline once
/// it has lasted that long). One bad look halves the floor, down to the configured one; <see cref="HealthyLooks"/> good
/// looks in a row double it back toward the start. A rate that failed is only tried again after <see cref="RetryLooks"/>
/// good looks, twice as many after each failure there (at most <see cref="MaxRetryLooks"/>), and forgotten after
/// <see cref="ForgetLooks"/> good looks in a row. Above the floor Steam still raises the rate itself, up to SendRateMax.
/// Not thread-safe; the network thread looks.
/// </summary>
public sealed class SendRatePolicy
{
	public const int StartBytes = 4 << 20;

	public const float MinQuality = 0.98f;

	public const int QueuePingMs = 100, HealthyLooks = 5, BaselineLooks = 30, RetryLooks = 10, MaxRetryLooks = 60, ForgetLooks = 120;

	public const long LookEveryMs = 1000;

	private readonly int _floor, _start;
	private readonly int[] _pings = new int[BaselineLooks];
	private int _pingCount, _pingAt, _healthy, _clean, _failedAt, _needed;

	public int Current { get; private set; }

	public SendRatePolicy(int floor, int max)
	{
		_floor = Math.Min(floor, max);
		_start = Math.Clamp(StartBytes, _floor, max);
		Current = _start;
	}

	/// <summary>One look at the connection (quality 0..1 and ping in ms; Steam reports -1, or 0 before its first
	/// measurement, when it does not know yet); true when <see cref="Current"/> changed.</summary>
	public bool Look(float remoteQuality, int pingMs)
	{
		bool pingKnown = pingMs > 0;
		if (pingKnown)
		{
			_pings[_pingAt] = pingMs;
			_pingAt = (_pingAt + 1) % BaselineLooks;
			_pingCount = Math.Min(_pingCount + 1, BaselineLooks);
		}
		bool congested = (remoteQuality > 0 && remoteQuality < MinQuality) || (pingKnown && pingMs > Baseline() + QueuePingMs);
		int next = Current;
		if (congested)
		{
			_healthy = _clean = 0;
			if (Current > _floor)
			{
				_needed = Current == _failedAt ? Math.Min(MaxRetryLooks, Math.Max(RetryLooks, _needed * 2)) : RetryLooks;
				_failedAt = Current;
				next = Math.Max(_floor, Current / 2);
			}
		}
		else
		{
			_healthy++;
			if (++_clean >= ForgetLooks) _failedAt = _needed = 0;
			int up = Math.Min(_start, Current * 2);
			if (up > Current && _healthy >= (_failedAt != 0 && up >= _failedAt ? _needed : HealthyLooks))
			{
				_healthy = 0;
				next = up;
			}
		}
		bool changed = next != Current;
		Current = next;
		return changed;
	}

	private int Baseline()
	{
		int best = int.MaxValue;
		for (int i = 0; i < _pingCount; i++) best = Math.Min(best, _pings[i]);
		return best;
	}
}
