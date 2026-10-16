using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;

namespace Y51_SE2Online.Net;

/// <summary>
/// Large bulk messages go out deflated: an explosion's signals (500 KiB in one frame) or a control panel's model
/// (600 KiB) took the better part of a second on a 650 KiB/s link, and every ordered message after them waited.
/// Envelope: marker, original length, the original's first <see cref="Head"/> bytes as they were (type and LifeSeq stay
/// readable), then the rest deflated.
/// </summary>
internal static class BulkCompression
{
	/// <summary>Outside the sync message range (32-79), so an envelope is never taken for a message.</summary>
	public const byte Marker = 0xC5;

	public const int Head = 5;

	public const int Prefix = 1 + 4 + Head;

	public const int MinBytes = 8192;

	/// <summary>
	/// Inflated size may be at most this many times the envelope. Real bulk data deflates about 3-9 times (world parts
	/// 8.4x at most in the logs); deflated zeros reach 1000x, which is what a bomb uses.
	/// </summary>
	public const int MaxRatio = 64;

	/// <summary>The envelope, or null when the message is small, over <paramref name="maxBytes"/> (the receiver's
	/// inflate cap; such a message goes as it is), or deflating saves under a fifth or more than <see cref="MaxRatio"/>.</summary>
	public static byte[]? Pack(ReadOnlySpan<byte> message, int maxBytes = int.MaxValue)
	{
		if (message.Length < MinBytes || message.Length > maxBytes) return null;
		var output = new MemoryStream(message.Length / 3 + Prefix);
		output.WriteByte(Marker);
		Span<byte> length = stackalloc byte[4];
		BinaryPrimitives.WriteInt32LittleEndian(length, message.Length);
		output.Write(length);
		output.Write(message[..Head]);
		using (var deflate = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true)) deflate.Write(message[Head..]);
		return output.Length <= message.Length * 4L / 5 && !OverRatio(message.Length, (int)output.Length) ? output.ToArray() : null;
	}

	public static int DeclaredLength(ReadOnlySpan<byte> envelope) => envelope.Length >= Prefix ? BinaryPrimitives.ReadInt32LittleEndian(envelope.Slice(1)) : -1;

	public static bool OverRatio(long inflated, int deflated) => inflated > (long)deflated * MaxRatio;

	/// <summary>The original message, or null if the envelope is malformed, larger than declared or over the limit.</summary>
	public static byte[]? Unpack(byte[] envelope, int maxBytes)
	{
		int length = DeclaredLength(envelope);
		if (length < Head || length > maxBytes || OverRatio(length, envelope.Length)) return null;
		return Inflate(envelope, Prefix, envelope.Length - Prefix, envelope.AsSpan(1 + 4, Head), length);
	}

	/// <summary>
	/// <paramref name="head"/> followed by exactly <paramref name="length"/> - head bytes inflated from the source, or
	/// null if the data is corrupt, shorter or longer. The buffer grows as data arrives, so a header that lies about
	/// the size costs only what the sender really sent.
	/// </summary>
	public static byte[]? Inflate(byte[] source, int offset, int count, ReadOnlySpan<byte> head, int length)
	{
		var message = new byte[Math.Min(length, Math.Max(head.Length, count * 4))];
		head.CopyTo(message);
		int read = head.Length;
		try
		{
			using var deflate = new DeflateStream(new MemoryStream(source, offset, count), CompressionMode.Decompress);
			while (read < length)
			{
				if (read == message.Length) Array.Resize(ref message, (int)Math.Min(length, Math.Max(4096, message.Length * 2L)));
				int n = deflate.Read(message, read, message.Length - read);
				if (n == 0) return null;
				read += n;
			}
			// Anything past the declared length is a lie about the size.
			return deflate.ReadByte() == -1 ? message : null;
		}
		catch (InvalidDataException)
		{
			return null;
		}
	}
}
