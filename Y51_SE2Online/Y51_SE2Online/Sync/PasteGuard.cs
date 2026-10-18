using System;
using System.Collections.Generic;

namespace Y51_SE2Online.Sync;

/// <summary>
/// Host side of "one paste per projection": a player's paste that overlaps one of their own accepted in the last
/// <see cref="WindowMs"/> is refused. The first paste of a big blueprint can still be spawning when a repeated click
/// arrives, so SE2's own placement check cannot see it yet.
/// </summary>
public sealed class PasteGuard
{
	public const long WindowMs = 5000;

	/// <summary>Grids that only touch or graze (snapped side by side) are not stacked.</summary>
	public const double SlackMeters = 0.25;

	private readonly List<(ushort Client, GhostBox Box, long Ms)> _recent = new();

	public bool TryAccept(ushort client, GhostBox box, long nowMs)
	{
		lock (_recent)
		{
			_recent.RemoveAll(r => nowMs - r.Ms > WindowMs);
			foreach (var r in _recent)
			{
				if (r.Client == client && Overlaps(r.Box, box)) return false;
			}
			_recent.Add((client, box, nowMs));
			return true;
		}
	}

	/// <summary>Separating-axis test of two oriented boxes, with <see cref="SlackMeters"/> of allowed overlap.</summary>
	public static bool Overlaps(GhostBox a, GhostBox b)
	{
		Span<Vec3D> axes = stackalloc Vec3D[15];
		Axes(a, axes);
		Axes(b, axes.Slice(3));
		for (int i = 0; i < 3; i++)
			for (int j = 0; j < 3; j++)
				axes[6 + i * 3 + j] = Cross(axes[i], axes[3 + j]);
		Vec3D t = b.Center - a.Center;
		foreach (Vec3D raw in axes)
		{
			double len = raw.Length;
			if (len < 1e-6) continue;
			Vec3D l = raw * (1.0 / len);
			double ra = a.Hx * Math.Abs(Dot(axes[0], l)) + a.Hy * Math.Abs(Dot(axes[1], l)) + a.Hz * Math.Abs(Dot(axes[2], l));
			double rb = b.Hx * Math.Abs(Dot(axes[3], l)) + b.Hy * Math.Abs(Dot(axes[4], l)) + b.Hz * Math.Abs(Dot(axes[5], l));
			if (Math.Abs(Dot(t, l)) > ra + rb - SlackMeters) return false;
		}
		return true;
	}

	private static void Axes(GhostBox b, Span<Vec3D> o)
	{
		double x = b.Qx, y = b.Qy, z = b.Qz, w = b.Qw;
		o[0] = new Vec3D(1 - 2 * (y * y + z * z), 2 * (x * y + w * z), 2 * (x * z - w * y));
		o[1] = new Vec3D(2 * (x * y - w * z), 1 - 2 * (x * x + z * z), 2 * (y * z + w * x));
		o[2] = new Vec3D(2 * (x * z + w * y), 2 * (y * z - w * x), 1 - 2 * (x * x + y * y));
	}

	private static double Dot(Vec3D a, Vec3D b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

	private static Vec3D Cross(Vec3D a, Vec3D b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
}
