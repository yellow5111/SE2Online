using System;
using System.Collections.Generic;

namespace Y51_SE2Online.Sync;

public sealed class AudioRouting
{
	public const long JoinerBuildWindowMs = 2000L;

	public const long OwnBuildWindowMs = 4000L;

	private readonly object _sync = new object();

	private readonly Queue<(ushort ClientId, long Ms)> _joinerBuilds = new Queue<(ushort, long)>();

	private readonly Queue<long> _ownBuilds = new Queue<long>();

	public long HostMuted;

	public long HostPlayed;

	public long JoinerMuted;

	public long JoinerPlayed;

	public long CharacterMuted;

	public static AudioRouting Instance { get; } = new AudioRouting();

	/// <summary>SE2's own range rule for a 3D sound (AudioEventInstance3D.SetTransform): silent from its max distance on.
	/// A max of 0 means not range-limited; a distance of 0 means the sound has not been placed yet.</summary>
	public static bool OutOfRange(double distance, double maxDistance) => maxDistance > 0 && distance > 0 && distance >= maxDistance;

	/// <summary>Within a hand tool's reach a builder's own 2D build sounds keep full volume.</summary>
	public const double FlatSoundFullVolumeMeters = 6;

	/// <summary>Gain for a 2D event played at a position: full volume within reach, then SE2's usual linear-squared
	/// spatializer rolloff to silence at the event's max distance.</summary>
	public static float FlatSoundGain(double distance, double maxDistance)
	{
		if (!(maxDistance > FlatSoundFullVolumeMeters) || distance <= FlatSoundFullVolumeMeters) return 1f;
		double t = 1 - Math.Min(1, (distance - FlatSoundFullVolumeMeters) / (maxDistance - FlatSoundFullVolumeMeters));
		return (float)(t * t);
	}

	/// <summary>How far SE2 lets a player build or remove blocks (creative interaction distance): a client's own-build
	/// window (its own build story) is trusted that far.</summary>
	public const double OwnBuildReachMeters = 100;

	/// <summary>The listen host has no story for its own builds and none for a client's grinder removing a block, so
	/// it only claims a build sound as its own when the block is within a hand tool's reach of its player.</summary>
	public const double HostOwnBuildReachMeters = 10;

	/// <summary>Range of another player's place/remove sound, the 20 m SE2 gives its block and build-stage events.</summary>
	public const double BuildSoundRangeMeters = 20;

	/// <summary>Volume of a place/remove sound: this player's own at full volume (also when the block is unknown), anyone
	/// else's falling off with the distance to the block and silent when that is unknown. distance &lt; 0 = unknown.</summary>
	public static float BuildSoundGain(bool own, double distance, double ownReach = OwnBuildReachMeters)
	{
		if (own && (distance < 0 || distance <= ownReach)) return 1f;
		return distance < 0 ? 0f : FlatSoundGain(distance, BuildSoundRangeMeters);
	}

	/// <summary>Distance from a point to the nearest point of an axis-aligned box (0 inside it).</summary>
	public static double DistanceToBox(Vec3D point, Vec3D min, Vec3D max)
	{
		double dx = Math.Max(0, Math.Max(min.X - point.X, point.X - max.X));
		double dy = Math.Max(0, Math.Max(min.Y - point.Y, point.Y - max.Y));
		double dz = Math.Max(0, Math.Max(min.Z - point.Z, point.Z - max.Z));
		return Math.Sqrt(dx * dx + dy * dy + dz * dz);
	}

	public static bool IsBuildStory(string? storyName)
	{
		return storyName?.StartsWith("BlockPlacerNetworkStories.", StringComparison.Ordinal) ?? false;
	}

	public static bool IsCharacterSound(string shortName)
	{
		if (shortName == "CharacterMovementComponent+OnMagneticBootStateChangedSignal" || shortName == "CharacterMovementComponent+OnCharacterPhysicsStateChangedSignal")
		{
			return true;
		}
		return false;
	}

	public void NoteJoinerBuild(ushort clientId, long nowMs)
	{
		lock (_sync)
		{
			_joinerBuilds.Enqueue((clientId, nowMs));
			while (_joinerBuilds.Count > 64)
			{
				_joinerBuilds.Dequeue();
			}
		}
	}

	public ushort TakeJoinerBuild(long nowMs)
	{
		lock (_sync)
		{
			while (_joinerBuilds.Count > 0)
			{
				var (result, num) = _joinerBuilds.Dequeue();
				if (nowMs - num <= 2000)
				{
					HostMuted++;
					return result;
				}
			}
			HostPlayed++;
			return 0;
		}
	}

	public void NoteOwnBuild(long nowMs)
	{
		lock (_sync)
		{
			_ownBuilds.Enqueue(nowMs);
			while (_ownBuilds.Count > 64)
			{
				_ownBuilds.Dequeue();
			}
		}
	}

	public bool TakeOwnBuild(long nowMs)
	{
		lock (_sync)
		{
			while (_ownBuilds.Count > 0)
			{
				long num = _ownBuilds.Dequeue();
				if (nowMs - num <= 4000)
				{
					JoinerPlayed++;
					return true;
				}
			}
			JoinerMuted++;
			return false;
		}
	}

	public void Reset()
	{
		lock (_sync)
		{
			_joinerBuilds.Clear();
			_ownBuilds.Clear();
		}
	}

	public string Describe()
	{
		return $"audio routing: build sounds host played {HostPlayed} / left to joiners {HostMuted}, joiner played own {JoinerPlayed} / muted others' {JoinerMuted}; other characters' 2D sounds muted {CharacterMuted}";
	}
}
