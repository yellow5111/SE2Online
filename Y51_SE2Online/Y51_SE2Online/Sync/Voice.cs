using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Y51_SE2Online.Sync;

/// <summary>
/// Proximity voice on the wire. A joiner sends Steam-compressed voice frames up (VoiceUp); the host forwards each
/// frame, with the speaker's position, only to players within the voice radius (VoiceDown). The host never decodes.
/// </summary>
public static class VoiceMessage
{
	/// <summary>Largest compressed frame accepted; Steam voice frames are a few hundred bytes.</summary>
	public const int MaxFrameBytes = 4096;

	private const int UpHeader = 1 + 4;

	private const int DownHeader = 1 + 2 + 24 + 4;

	/// <summary>Frames for the local player, from the host's own relay (listen host) or a VoiceDown message.</summary>
	public static Action<ushort, Vec3D, byte[]> Received;

	public static byte[] EncodeUp(uint seq, ReadOnlySpan<byte> frame)
	{
		var data = new byte[UpHeader + frame.Length];
		data[0] = (byte)SyncMsg.VoiceUp;
		BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(1), seq);
		frame.CopyTo(data.AsSpan(UpHeader));
		return data;
	}

	public static bool TryDecodeUp(byte[] data, out uint seq, out byte[] frame)
	{
		seq = 0;
		frame = null;
		if (data == null || data.Length <= UpHeader || data.Length - UpHeader > MaxFrameBytes || data[0] != (byte)SyncMsg.VoiceUp) return false;
		seq = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(1));
		frame = data.AsSpan(UpHeader).ToArray();
		return true;
	}

	public static byte[] EncodeDown(ushort speaker, Vec3D at, uint seq, ReadOnlySpan<byte> frame)
	{
		var data = new byte[DownHeader + frame.Length];
		data[0] = (byte)SyncMsg.VoiceDown;
		var span = data.AsSpan(1);
		BinaryPrimitives.WriteUInt16LittleEndian(span, speaker);
		BinaryPrimitives.WriteDoubleLittleEndian(span.Slice(2), at.X);
		BinaryPrimitives.WriteDoubleLittleEndian(span.Slice(10), at.Y);
		BinaryPrimitives.WriteDoubleLittleEndian(span.Slice(18), at.Z);
		BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(26), seq);
		frame.CopyTo(data.AsSpan(DownHeader));
		return data;
	}

	public static bool TryDecodeDown(byte[] data, out ushort speaker, out Vec3D at, out byte[] frame)
	{
		speaker = 0;
		at = default;
		frame = null;
		if (data == null || data.Length <= DownHeader || data.Length - DownHeader > MaxFrameBytes || data[0] != (byte)SyncMsg.VoiceDown) return false;
		var span = data.AsSpan(1);
		speaker = BinaryPrimitives.ReadUInt16LittleEndian(span);
		at = new Vec3D(BinaryPrimitives.ReadDoubleLittleEndian(span.Slice(2)), BinaryPrimitives.ReadDoubleLittleEndian(span.Slice(10)), BinaryPrimitives.ReadDoubleLittleEndian(span.Slice(18)));
		if (!double.IsFinite(at.X) || !double.IsFinite(at.Y) || !double.IsFinite(at.Z)) return false;
		frame = data.AsSpan(DownHeader).ToArray();
		return true;
	}
}

/// <summary>Host-side voice budget per speaker and the radius check; pure so it can be unit tested.</summary>
public sealed class VoiceRelay
{
	/// <summary>Steam voice runs at roughly 1-4 KB/s per speaker; this allows ample headroom and nothing more.</summary>
	public const double BytesPerSecond = 12 * 1024;

	public const double BurstBytes = 24 * 1024;

	public const double MinRadius = 5, MaxRadius = 1000;

	private readonly Dictionary<ushort, (double Tokens, long AtMs)> _buckets = new();

	public static bool ValidRadius(double radius) => radius == 0 || (radius >= MinRadius && radius <= MaxRadius && double.IsFinite(radius));

	public bool Allow(ushort speaker, int bytes, long nowMs)
	{
		if (bytes <= 0 || bytes > VoiceMessage.MaxFrameBytes) return false;
		lock (_buckets)
		{
			var (tokens, at) = _buckets.TryGetValue(speaker, out var bucket) ? bucket : (BurstBytes, nowMs);
			tokens = Math.Min(BurstBytes, tokens + Math.Max(0, nowMs - at) * BytesPerSecond / 1000.0);
			if (tokens < bytes)
			{
				_buckets[speaker] = (tokens, nowMs);
				return false;
			}
			_buckets[speaker] = (tokens - bytes, nowMs);
			return true;
		}
	}

	public void Forget(ushort speaker)
	{
		lock (_buckets) _buckets.Remove(speaker);
	}

	public static bool InRange(Vec3D speaker, Vec3D listener, double radius) => radius > 0 && (speaker - listener).Length <= radius;
}

/// <summary>Listener-side loudness of decoded voice; pure so it can be unit tested.</summary>
public static class VoiceLevel
{
	/// <summary>Steam's decoded voice sits well below the game's mix: +6 dB at the default 100 % volume.</summary>
	public const float MakeUpGain = 2f;

	/// <summary>Full volume within this distance of the speaker; it fades to silence at the voice radius.</summary>
	public const float FullVolumeMeters = 4f;

	private const float Knee = 0.7f;

	/// <summary>The sample gain for the player's 0-200 % voice volume.</summary>
	public static float Gain(int volumePercent) => MakeUpGain * Math.Clamp(volumePercent, 0, 200) / 100f;

	/// <summary>Scales one sample, soft-clipping above the knee so loud speech compresses instead of distorting.</summary>
	public static short Apply(short sample, float gain)
	{
		float x = sample / 32768f * gain, a = Math.Abs(x);
		if (a > Knee) a = Knee + (1 - Knee) * MathF.Tanh((a - Knee) / (1 - Knee));
		return (short)MathF.Round(MathF.CopySign(a, x) * 32767f);
	}
}
