namespace Y51_SE2Online.Sync;

/// <summary>Which queued projectile impact effects and tracers the client's effects jobs may play (KeenGuards).</summary>
internal static class ImpactRequestRule
{
	/// <summary>Null when the request can be played; otherwise what it lacks, which is also its log line's kind.</summary>
	public static string? Refusal(bool hasHitEntity, bool hitEntityAlive, bool hasProjectile, bool hasMaterial) =>
		!hasHitEntity ? "no hit entity"
		: !hitEntityAlive ? "a hit entity no longer in the world"
		: !hasProjectile ? "no projectile definition"
		: !hasMaterial ? "no hit material"
		: null;

	/// <summary>The same for a queued tracer (KeenGuards.TracerEffectPrefix).</summary>
	public static string? TracerRefusal(bool hasSource, bool sourceAlive) =>
		!hasSource ? "no shooter" : !sourceAlive ? "a shooter no longer in the world" : null;
}
