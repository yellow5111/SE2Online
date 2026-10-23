using System;
using System.Collections.Generic;

namespace Y51_SE2Online.Sync;

/// <summary>
/// After the game's own load ends (<see cref="JoinStage.Playing"/>) the joining player's world is still arriving: object
/// batches, the character, grids, terrain physics and planet detail around it. The loading screen stays up until every
/// part is ready, in order. Each part has its own cap and the whole wait an overall cap, so a join never hangs: a part
/// past its cap is skipped (and named in the log), the next one is waited for.
/// </summary>
public sealed class WorldReadiness
{
	public enum Part { World, Character, Grids, Terrain, Details }

	public const int Parts = 5;

	/// <summary>One look at a part: whether it is ready, how far along (0..1) and a short line for the screen.</summary>
	public readonly record struct Probe(bool Ready, double Fraction = 0, string? Detail = null)
	{
		public static Probe Done => new(true, 1);

		/// <summary>Cannot be measured yet (no character to measure around): not waited for, and no progress either.</summary>
		public bool Moot { get; init; }
	}

	public static readonly string[] Titles = { "Receiving world", "Spawning your engineer", "Building grids", "Loading terrain", "Loading planet details" };

	public static readonly long[] CapMs = { 60_000, 45_000, 30_000, 45_000, 30_000 };

	public const long OverallCapMs = 180_000;

	/// <summary>Everything ready this long in a row: a batch arriving just after the last one was built would otherwise drop the screen early.</summary>
	public const long SettleMs = 1_000;

	private readonly long[] _waitedMs = new long[Parts];
	private readonly bool[] _skipped = new bool[Parts];
	private readonly List<string> _notes = new();
	private long _startMs = -1, _lastMs, _readySinceMs = -1;
	private double _shown;

	public Part Current { get; private set; }

	public bool Done { get; private set; }

	/// <summary>Why the wait ended: everything ready, or what was not after its cap.</summary>
	public string Outcome { get; private set; } = "";

	/// <summary>0..1 for the bar; never goes back.</summary>
	public double Fraction => _shown;

	public string? Detail { get; private set; }

	public long WaitedMs(long now) => _startMs < 0 ? 0 : now - _startMs;

	/// <summary>One look at every part (index = <see cref="Part"/>). Returns true once the screen may go.</summary>
	public bool Step(long now, IReadOnlyList<Probe> probes)
	{
		if (probes.Count != Parts) throw new ArgumentException($"{Parts} probes expected", nameof(probes));
		if (Done) return true;
		if (_startMs < 0) _startMs = _lastMs = now;
		long dt = Math.Max(0, now - _lastMs);
		_lastMs = now;
		int blocking = -1;
		double sum = 0;
		for (int i = 0; i < Parts; i++)
		{
			bool ok = probes[i].Ready || _skipped[i] || probes[i].Moot;
			sum += probes[i].Ready || _skipped[i] ? 1 : probes[i].Moot ? 0 : Math.Clamp(probes[i].Fraction, 0, 0.99);
			if (!ok && blocking < 0) blocking = i;
		}
		_shown = Math.Max(_shown, sum / Parts);
		if (blocking >= 0)
		{
			_readySinceMs = -1;
			Current = (Part)blocking;
			Detail = probes[blocking].Detail;
			_waitedMs[blocking] += dt;
			if (_waitedMs[blocking] >= CapMs[blocking])
			{
				_skipped[blocking] = true;
				_notes.Add($"{Titles[blocking].ToLowerInvariant()} not ready after {CapMs[blocking] / 1000} s" + (probes[blocking].Detail is { Length: > 0 } d ? $" ({d})" : ""));
			}
		}
		else
		{
			if (_readySinceMs < 0) _readySinceMs = now;
			if (now - _readySinceMs >= SettleMs) return Finish(_notes.Count == 0 ? "everything ready" : "ready except " + string.Join("; ", _notes));
		}
		if (now - _startMs >= OverallCapMs)
		{
			var missing = new List<string>(_notes);
			for (int i = 0; i < Parts; i++)
				if (!probes[i].Ready && !_skipped[i] && !probes[i].Moot) missing.Add(Titles[i].ToLowerInvariant() + (probes[i].Detail is { Length: > 0 } d ? $" ({d})" : ""));
			return Finish($"gave up waiting after {OverallCapMs / 1000} s; not ready: " + (missing.Count == 0 ? "nothing" : string.Join("; ", missing)));
		}
		return false;
	}

	private bool Finish(string outcome)
	{
		Done = true;
		Outcome = outcome;
		_shown = 1;
		return true;
	}

	/// <summary>The line the loading screen shows for the part waited on.</summary>
	public string Title => Titles[(int)Current] + "...";
}
