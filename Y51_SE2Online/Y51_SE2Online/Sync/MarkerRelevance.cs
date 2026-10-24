using System;
using System.Collections.Generic;
using System.Linq;

namespace Y51_SE2Online.Sync;

/// <summary>What the player is doing, for the relevance context term (read locally from the character's tool and seat).</summary>
public enum PlayerActivity : byte { None, Drill, WeldGrind, Weapon, Seated, Jetpack }

/// <summary>How a marker is drawn after the relevance rule.</summary>
public enum MarkerDisplay : byte { Hidden, Dot, Full }

/// <param name="Nametag">A player's name tag (Players category) rather than a ping.</param>
/// <param name="AgeS">Seconds since the ping was made (nametags: 0).</param>
/// <param name="AimDegrees">Angle between the camera forward and the marker, in degrees.</param>
/// <param name="FactionOrOwn">For a nametag: the player is in the viewer's faction (or is the viewer).</param>
public readonly record struct MarkerInput(bool Nametag, PingKind Kind, PingRelation Source, double AgeS, double LifetimeS,
	double DistanceMeters, double MaxRangeMeters, double AimDegrees, bool FactionOrOwn = false);

public readonly record struct MarkerResult(MarkerDisplay Display, double Score, double Alpha);

/// <summary>
/// The declutter thresholds, one set reconciling the design (top 6, danger 15 s) and the glyph README (15 degrees, own,
/// max 4): full when aimed within <see cref="AimDegrees"/>, younger than <see cref="FreshSeconds"/>, your own ping, a danger
/// ping younger than <see cref="DangerSeconds"/>, or in the top <see cref="FullCount"/>. Players can change the count and
/// the aim cone in their settings.
/// </summary>
public sealed record MarkerThresholds(double AimDegrees = 8, double FreshSeconds = 3, double DangerSeconds = 15, int FullCount = MarkerRelevance.DefaultFullCount, bool OwnAlwaysFull = true)
{
	public static readonly MarkerThresholds Default = new();
}

/// <summary>
/// Design 10.3: the relevance of our own markers (pings and player nametags) and how each is drawn. Vanilla markers never
/// go through this; they follow only the category toggles. Pure: the client calls it at 10 Hz with what it sees.
/// </summary>
public static class MarkerRelevance
{
	public const double DotAlpha = 0.55, FadeFrom = 0.7, DotFrom = 0.8, MinFadeAlpha = 0.35;
	public const int DefaultFullCount = 6, MinFullCount = 3, MaxFullCount = 10;
	/// <summary>The recent pings feed above chat: rows, and when a row starts and ends fading (seconds).</summary>
	public const int FeedRows = 5;
	public const double FeedFadeFrom = 6, FeedFadeTo = 10;

	public static double Score(in MarkerInput m, PlayerActivity activity)
	{
		double recency = m.Nametag ? 1 : m.LifetimeS > 0 ? Math.Clamp(1 - m.AgeS / m.LifetimeS, 0, 1) : 0;
		double distance = m.MaxRangeMeters > 0 ? Math.Clamp(1 - m.DistanceMeters / m.MaxRangeMeters, 0, 1) : 0;
		double source = m.Source switch { PingRelation.Own => 1.0, PingRelation.Faction => 0.7, PingRelation.Admin => 0.7, PingRelation.Ally => 0.5, _ => 0.3 };
		double context = Matches(m, activity) ? 1 : 0;
		return 0.35 * recency + 0.25 * distance + 0.20 * source + 0.20 * context;
	}

	/// <summary>Whether the marker's kind fits what the player is doing.</summary>
	public static bool Matches(in MarkerInput m, PlayerActivity activity) => activity switch
	{
		PlayerActivity.Drill => !m.Nametag && m.Kind is PingKind.Rock or PingKind.Mine,
		PlayerActivity.WeldGrind => !m.Nametag && m.Kind is PingKind.Block or PingKind.Ship or PingKind.Base or PingKind.Build,
		PlayerActivity.Weapon => m.Nametag ? !m.FactionOrOwn : m.Kind is PingKind.Danger or PingKind.Player,
		PlayerActivity.Seated => !m.Nametag && m.Kind is PingKind.Ship or PingKind.Base or PingKind.Go or PingKind.Regroup,
		PlayerActivity.Jetpack => !m.Nametag && m.Kind is PingKind.Go or PingKind.Regroup or PingKind.Point,
		_ => false
	};

	/// <summary>
	/// How every marker is drawn. All: everything Full. Relevant: Full by <see cref="MarkerThresholds"/>, else a Dot.
	/// Pings only: nametags hidden. Off: all hidden. A ping fades over its last 30% of life and turns into a Dot for its
	/// last 20% unless aimed at (the native icon cannot shrink).
	/// </summary>
	public static MarkerResult[] Decide(IReadOnlyList<MarkerInput> markers, MarkerMode mode, PlayerActivity activity, MarkerThresholds thresholds = null, bool pingsOn = true, bool playersOn = true)
	{
		var t = thresholds ?? MarkerThresholds.Default;
		var result = new MarkerResult[markers.Count];
		int fullCount = Math.Clamp(t.FullCount, MinFullCount, MaxFullCount);
		var scores = new double[markers.Count];
		for (int i = 0; i < markers.Count; i++) scores[i] = Score(markers[i], activity);
		var top = new HashSet<int>(Enumerable.Range(0, markers.Count).Where(i => Shown(markers[i], mode, pingsOn, playersOn))
			.OrderByDescending(i => scores[i]).ThenBy(i => markers[i].DistanceMeters).Take(fullCount));
		for (int i = 0; i < markers.Count; i++)
		{
			var m = markers[i];
			if (!Shown(m, mode, pingsOn, playersOn)) { result[i] = new(MarkerDisplay.Hidden, scores[i], 0); continue; }
			double life = m.Nametag || m.LifetimeS <= 0 ? 0 : Math.Clamp(m.AgeS / m.LifetimeS, 0, 1);
			double alpha = life <= FadeFrom ? 1 : 1 - (1 - MinFadeAlpha) * (life - FadeFrom) / (1 - FadeFrom);
			bool aimed = m.AimDegrees <= t.AimDegrees;
			bool full = mode == MarkerMode.All
				|| aimed
				|| !m.Nametag && m.AgeS < t.FreshSeconds
				|| !m.Nametag && t.OwnAlwaysFull && m.Source == PingRelation.Own
				|| !m.Nametag && m.Kind == PingKind.Danger && m.AgeS < t.DangerSeconds
				|| top.Contains(i);
			if (!m.Nametag && life >= DotFrom && !aimed) full = false;
			result[i] = full ? new(MarkerDisplay.Full, scores[i], alpha) : new(MarkerDisplay.Dot, scores[i], Math.Min(alpha, DotAlpha));
		}
		return result;
	}

	private static bool Shown(in MarkerInput m, MarkerMode mode, bool pingsOn, bool playersOn)
		=> mode != MarkerMode.Off && (m.Nametag ? playersOn && mode != MarkerMode.PingsOnly : pingsOn);

	/// <summary>The compass strip: the off-screen or collapsed markers to tick, nearest first, at most <paramref name="max"/>.</summary>
	public static int[] CompassTicks(IReadOnlyList<MarkerInput> markers, IReadOnlyList<MarkerResult> results, IReadOnlyList<bool> onScreen, int max = 12)
		=> Enumerable.Range(0, markers.Count).Where(i => results[i].Display != MarkerDisplay.Hidden && (results[i].Display == MarkerDisplay.Dot || !onScreen[i]))
			.OrderBy(i => markers[i].DistanceMeters).Take(max).ToArray();
}
