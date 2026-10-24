using System.Threading;

namespace Y51_SE2Online.Core;

/// <summary>
/// How far this player's own HUD shows other players' name tags: the player's saved setting, read every frame. It used to be
/// copied from the session's options when a mode started, so a save while playing changed nothing and a rejoin or a join the
/// game started (join screen, IP join) kept or fell back to the 200 m default.
/// </summary>
public static class NametagRange
{
	public const double Min = 1, Max = 100000;

	private static double _metres = MpOptions.DefaultNametagRange;

	public static double Metres => Volatile.Read(ref _metres);

	public static bool Valid(double metres) => metres >= Min && metres <= Max;

	/// <summary>True when the range changed; out-of-range values (NaN included) are ignored.</summary>
	public static bool Set(double metres)
	{
		if (!Valid(metres)) return false;
		return Interlocked.Exchange(ref _metres, metres) != metres;
	}

	/// <summary>Whether a tag this far from the player's anchor (character, or the ship it pilots) is hidden.</summary>
	public static bool TooFar(double distance) => !(distance <= Metres);
}
