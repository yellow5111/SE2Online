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

	/// <summary>The envelope, or null when the message is small or deflating saves under a fifth.</summary>
	public static byte[]? Pack(ReadOnlySpan<byte> message)
	{
		if (message.Length < MinBytes) return null;
		var output = new MemoryStream(message.Length / 3 + Prefix);
		output.WriteByte(Marker);
		Span<byte> length = stackalloc byte[4];
		BinaryPrimitives.WriteInt32LittleEndian(length, message.Length);
		output.Write(length);
		output.Write(message[..Head]);
		using (var deflate = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true)) deflate.Write(message[Head..]);
		return output.Length <= message.Length * 4L / 5 ? output.ToArray() : null;
	}

	public static int DeclaredLength(ReadOnlySpan<byte> envelope) => envelope.Length >= Prefix ? BinaryPrimitives.ReadInt32LittleEndian(envelope.Slice(1)) : -1;

	/// <summary>The original message, or null if the envelope is malformed, larger than declared or over the limit.</summary>
	public static byte[]? Unpack(byte[] envelope, int maxBytes)
	{
		int length = DeclaredLength(envelope);
		if (length < Head || length > maxBytes) return null;
		var message = new byte[length];
		envelope.AsSpan(1 + 4, Head).CopyTo(message);
		try
		{
			using var deflate = new DeflateStream(new MemoryStream(envelope, Prefix, envelope.Length - Prefix), CompressionMode.Decompress);
			int read = Head;
			while (read < length)
			{
				int n = deflate.Read(message, read, length - read);
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
