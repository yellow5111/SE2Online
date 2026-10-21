using System.Linq;
using Keen.VRage.Multiplayer.Replications;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>
/// SE2 4.0 ships its client motion playback switched off: <see cref="MotionInterpolationSettings.Interpolate"/> is a
/// debug-menu toggle that defaults to false, so a client snaps every replicated character, grid and ragdoll to each
/// motion sample as it arrives. Samples come in network bursts, not one per client frame, so remote players, ships
/// (one's own too: SE2 4.0 is server-authoritative) and ragdolls (keyframed to that transform) moved in steps and
/// jerks. The game's own playback (interpolation between the two newest samples, capped extrapolation, error fade,
/// parent-relative motion for characters on ships) is turned on for every SE2 Online session. Its playout delay is
/// the sample gap minus one frame: none while samples arrive every frame. -y51online:interpolate:off keeps it off.
/// </summary>
internal static class MotionSmoothing
{
	public static void Install()
	{
		bool on = !MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "interpolate:off");
		MotionInterpolationSettings.Instance.Interpolate = on;
		GameLog.Instance.Info(on ? "motion: the game's motion playback (interpolation) is on" : "motion: playback left off (-y51online:interpolate:off); replicated motion snaps to each sample");
	}
}
