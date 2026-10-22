using System.Linq;
using Keen.VRage.Library.Mathematics;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.Engine;

/// <summary>
/// How often a moving entity's motion goes to a player, by its distance from that player's character: every server frame
/// up close, every second frame past 250 m, every fourth past 1 km (the game's own multipliers for its streaming shells,
/// SceneSubsetCoreConfiguration.ShellSendIntervalMultipliers 1, 2, 4, which it only applies with streaming on), and 4 times
/// a second past 3 km, where the game would not send the body at all with streaming on. The
/// player's game plays the samples back at their own cadence (MotionHistory.Cadence); a resting or teleported body always
/// goes at once (reliable), so nothing is left behind.
/// -y51online:motionrate:full sends every frame to everyone.
/// </summary>
internal static class MotionRelevance
{
	private static readonly bool Full = MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "motionrate:full");

	public static string DistanceTo(Vector3D? observer, in Vector3D at)
		=> observer is { } o ? $"{System.Math.Sqrt(Vector3D.DistanceSquared(o, at)):0} m" : "no observer";

	/// <param name="observer">Where the player's character is (null: none in the world, everything at full rate).</param>
	public static int IntervalFrames(Vector3D? observer, in Vector3D entity, bool character = false)
	{
		if (Full || observer is not { } o) return 1;
		double d2 = Vector3D.DistanceSquared(o, entity);
		// A character turns, stops and jumps within a frame or two; its playback keeps a few frames of samples, and at
		// half rate a lagging player's game ran out of them and guessed (walking host 1.0-1.3 m p95 off at 250 ms lag,
		// against 9 cm at full rate). Characters go every frame up to 3 km.
		if (character) return d2 < 3000.0 * 3000.0 ? 1 : 4;
		return d2 < 250.0 * 250.0 ? 1 : d2 < 1000.0 * 1000.0 ? 2 : d2 < 3000.0 * 3000.0 ? 4 : 15;
	}
}
