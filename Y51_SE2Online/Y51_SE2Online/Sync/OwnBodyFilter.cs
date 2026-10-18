using System;
using System.Globalization;
using System.Numerics;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.Sync;

public sealed class OwnBodyFilter
{
	public long Frames;

	public long Blended;

	public long BlendedBig;

	public long Snaps;

	public long Restarts;

	public long Logged;

	public double MaxJumpM;

	public float MaxJumpDeg;

	private readonly IMpLog? _log;

	private bool _has;

	private bool _startFromShown;

	private double _lastMs;

	private long _lastLogMs = -4611686018427387904L;

	private int _suppressed;

	private Vec3D _src;

	private Vec3D _off;

	private Vec3D _shown;

	private Vec3D _vEst;

	private Vec3D _lastJump;

	private long _lastJumpFrame = -10L;

	private Quaternion _srcQ = Quaternion.Identity;

	private Quaternion _offQ = Quaternion.Identity;

	private Quaternion _shownQ = Quaternion.Identity;

	public double TauMs { get; set; } = 80.0;

	public double SnapMeters { get; set; } = 3.0;

	public float SnapDeg { get; set; } = 100f;

	public double MaxOffsetMeters { get; set; } = 10.0;

	public float MaxOffsetDeg { get; set; } = 45f;

	public double LogMeters { get; set; } = 0.25;

	public float LogDeg { get; set; } = 3f;

	public bool Enabled { get; set; } = true;

	public double PassMeters { get; set; } = 0.002;

	public float PassDeg { get; set; } = 0.5f;

	public OwnBodyFilter(IMpLog? log = null)
	{
		_log = log;
	}

	public void Reset()
	{
		_has = false;
		_startFromShown = false;
		_off = default;
		_vEst = default;
		_offQ = Quaternion.Identity;
	}

	public void NoteShown(double now, double x, double y, double z, Quaternion q)
	{
		_shown = new Vec3D(x, y, z);
		_shownQ = Quaternion.Normalize(q);
		_startFromShown = true;
		_lastMs = now;
	}

	public void Rebase(Quaternion delta)
	{
		delta = Quaternion.Normalize(delta);
		_srcQ = Quaternion.Normalize(delta * _srcQ);
		_shownQ = Quaternion.Normalize(delta * _shownQ);
		_offQ = Quaternion.Normalize(delta * _offQ * Quaternion.Conjugate(delta));
	}

	// Fractional: whole-ms frame times made the velocity prediction miss by speed x 0.5 ms each frame, and that miss was
	// blended into the shown body as if it had jumped (10 mm at 20 m/s, a third of the own body's frame-to-frame error).
	public (double X, double Y, double Z, Quaternion Q) Filter(double now, double x, double y, double z, Quaternion q, Vector3? velocity, Quaternion ownTurn, string source, string? why, bool smoothedSource = false)
	{
		Frames++;
		var pos = new Vec3D(x, y, z);
		q = Quaternion.Normalize(q);
		if (!Enabled)
		{
			_src = _shown = pos;
			_srcQ = _shownQ = q;
			_has = true;
			_lastMs = now;
			return (x, y, z, q);
		}
		if (!_has || now - _lastMs > 1000)
		{
			Restarts++;
			_off = default;
			_offQ = Quaternion.Identity;
			if (_startFromShown && now - _lastMs <= 1000)
			{
				Vec3D off = _shown - pos;
				if (off.Length <= SnapMeters)
				{
					_off = off;
				}
				Quaternion offQ = Quaternion.Normalize(_shownQ * Quaternion.Conjugate(q));
				if (LookMath.AngleDeg(offQ, Quaternion.Identity) <= SnapDeg)
				{
					_offQ = offQ;
				}
				Report(now, off.Length, LookMath.AngleDeg(_shownQ, q), source, why ?? "own body hand-over (seat / smoothed -> this source)", snapped: false);
			}
			_startFromShown = false;
			_has = true;
		}
		else
		{
			_startFromShown = false;
			double dtMs = Math.Clamp(now - _lastMs, 0.0, 100.0);
			double decay = Math.Exp((0.0 - dtMs) / TauMs);
			_off *= decay;
			_offQ = Quaternion.Normalize(Quaternion.Slerp(Quaternion.Identity, _offQ, (float)decay));
			if (dtMs > 0.0)
			{
				Vec3D measuredVel = (pos - _src) * (1000.0 / dtMs);
				if (measuredVel.Length < 500.0)
				{
					_vEst += (measuredVel - _vEst) * 0.25;
				}
			}
			Vec3D velocityOrEstimate;
			if (velocity.HasValue)
			{
				Vector3 v = velocity.GetValueOrDefault();
				velocityOrEstimate = new Vec3D(v.X, v.Y, v.Z);
			}
			else
			{
				velocityOrEstimate = _vEst;
			}
			Vec3D vel = velocityOrEstimate;
			Vec3D predicted = _src + vel * (dtMs / 1000.0);
			Vec3D jump = pos - predicted;
			double jumpM = jump.Length;
			Quaternion predictedQ = Quaternion.Normalize(_srcQ * ownTurn);
			Quaternion turnError = Quaternion.Normalize(q * Quaternion.Conjugate(predictedQ));
			float jumpDeg = LookMath.AngleDeg(turnError, Quaternion.Identity);
			bool snapPos = jumpM > SnapMeters;
			bool snapRot = jumpDeg > SnapDeg;
			if (snapPos)
			{
				_off = default;
				Snaps++;
			}
			// Raw host samples: every frame's deviation from the velocity prediction is blended (a speed-scaled pass
			// limit, about 0.3 m at a walk, let all of their frame-to-frame noise through). A smoothed timeline (seat,
			// moving deck, fast flight) is already smooth and is what the ship itself is drawn from: blending it made the
			// body trail its seat whenever the ship accelerated, so there only real jumps are blended.
			else if (jumpM > (smoothedSource ? 0.2 + 0.25 * vel.Length * dtMs / 1000.0 : PassMeters))
			{
				_off -= jump;
				Blended++;
				if (_lastJumpFrame == Frames - 1 && (jump + _lastJump).Length < 0.3 * _lastJump.Length + PassMeters)
				{
					_off -= _lastJump * (1.0 - decay);
					_lastJumpFrame = -10L;
				}
				else
				{
					_lastJump = jump;
					_lastJumpFrame = Frames;
				}
			}
			if (snapRot)
			{
				_offQ = Quaternion.Identity;
				Snaps++;
			}
			else if (jumpDeg > PassDeg)
			{
				_offQ = Quaternion.Normalize(_offQ * Quaternion.Conjugate(turnError));
				Blended++;
			}
			if (jumpM > MaxJumpM)
			{
				MaxJumpM = jumpM;
			}
			if (jumpDeg > MaxJumpDeg)
			{
				MaxJumpDeg = jumpDeg;
			}
			if (jumpM >= LogMeters || jumpDeg >= LogDeg)
			{
				BlendedBig++;
				Report(now, jumpM, jumpDeg, source, why, snapPos | snapRot);
			}
		}
		double offM = _off.Length;
		if (offM > MaxOffsetMeters)
		{
			_off *= MaxOffsetMeters / offM;
		}
		float offDeg = LookMath.AngleDeg(_offQ, Quaternion.Identity);
		if (offDeg > MaxOffsetDeg)
		{
			_offQ = Quaternion.Normalize(Quaternion.Slerp(Quaternion.Identity, _offQ, MaxOffsetDeg / offDeg));
		}
		_src = pos;
		_srcQ = q;
		_shown = pos + _off;
		_shownQ = Quaternion.Normalize(_offQ * q);
		_lastMs = now;
		return (_shown.X, _shown.Y, _shown.Z, _shownQ);
	}

	private void Report(double now, double m, float deg, string source, string? why, bool snapped)
	{
		if (_log != null)
		{
			if (now - _lastLogMs < 1000)
			{
				_suppressed++;
				return;
			}
			Logged++;
			string more = _suppressed > 0 ? $" (+{_suppressed} more in the last second)" : "";
			_suppressed = 0;
			_lastLogMs = (long)now;
			IMpLog log = _log;
			string jump = $"client: own body: frame discontinuity {m.ToString("0.00", CultureInfo.InvariantCulture)} m / {deg.ToString("0.0", CultureInfo.InvariantCulture)} deg from source '{source}'";
			string reason = why != null ? " (" + why + ")" : "";
			string outcome;
			if (snapped)
			{
				outcome = " -> shown as is (beyond the snap limit)";
			}
			else
			{
				outcome = Enabled ? " -> blended out" : " -> shown as is (bodyfilter off)";
			}
			log.Info(jump + reason + outcome + more);
		}
	}

	public string Describe()
	{
		return $"own-body blend {(Enabled ? "on" : "off")}: {Frames} frames, {Blended} blended ({BlendedBig} big), {Snaps} snaps, {Restarts} restarts, largest source jump {MaxJumpM.ToString("0.00", CultureInfo.InvariantCulture)} m / {MaxJumpDeg.ToString("0.0", CultureInfo.InvariantCulture)} deg";
	}
}
