namespace Y51_SE2Online.Core;

/// <summary>
/// When the server takes a remote player's hand drill claim on a planet's boulder that its own check refused (F13). The
/// server re-checks every claim with a sweep from its copy of the player's head and wants the claimed entity as the first
/// thing hit; a boulder sits on its planet, so a head a little off the player's own grazes the ground first. The claim is
/// taken only when that sweep stopped on the same planet (or the boulder) right at the claimed point, and that point lies
/// on the claimed boulder (its bounds, padded by the drill's sweep radius and tolerance): a claim naming one boulder with
/// a point on the ground elsewhere is refused.
/// </summary>
public static class DrillReachRule
{
	/// <summary>How far from the claimed point the server's sweep may stop, in metres.</summary>
	public const double PointSlack = 1.0;

	public static bool Accepts(bool remotePlayer, bool claimIsPlanetBoulder, bool refusedBySweep, bool pointOnBoulder, bool pointInFront, bool withinReach,
		bool hitIsVoxel, bool hitIsSamePlanetOrBoulder, double hitDistanceFromClaim)
		=> remotePlayer && claimIsPlanetBoulder && refusedBySweep && pointOnBoulder && pointInFront && withinReach && hitIsVoxel && hitIsSamePlanetOrBoulder
			&& hitDistanceFromClaim >= 0 && hitDistanceFromClaim <= PointSlack;
}
