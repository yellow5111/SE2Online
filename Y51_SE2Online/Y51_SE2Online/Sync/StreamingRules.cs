namespace Y51_SE2Online.Sync;

/// <summary>Per-joiner streaming radius: what a joiner keeps, with hysteresis so a grid on the edge does not flap.</summary>
public static class StreamingRules
{
	/// <summary>A streamed-in group leaves only past this multiple of the radius.</summary>
	public const double OutFactor = 1.2;

	public const double MinRadius = 1000;

	public const double MaxRadius = 1_000_000;

	/// <summary>Dedicated servers send the whole world by default, as SE2 4.0 itself does; server.json Network.StreamingRadiusMeters opts in to streaming.</summary>
	public const double DedicatedDefaultRadius = 0;

	/// <summary>Whether a joiner should have a group at <paramref name="distance"/> metres; 0 or less turns streaming off.</summary>
	public static bool Keep(bool present, double distance, double radius) =>
		radius <= 0 || distance <= (present ? radius * OutFactor : radius);

	public static bool Valid(double radius) => radius == 0 || (radius >= MinRadius && radius <= MaxRadius);
}
