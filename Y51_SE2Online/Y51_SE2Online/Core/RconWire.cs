using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Y51_SE2Online.Core;

/// <summary>
/// Source RCON framing, shared by the dedicated server and the SE2 Online RCON tool. Little-endian int32 size (of what
/// follows), int32 id, int32 type, the body, then two zero bytes. A client sends AUTH (3) and EXECCOMMAND (2); the server
/// answers RESPONSE_VALUE (0) and AUTH_RESPONSE (2, the request id on success, -1 on refusal). An empty RESPONSE_VALUE
/// from a client is echoed back empty with the same id after the replies before it, which is how a client finds the end
/// of a reply split over several packets.
/// </summary>
public static class RconWire
{
	public const int Response = 0, ExecOrAuthResponse = 2, Auth = 3;
	/// <summary>Largest body in one packet, either way (4096, as Source servers use).</summary>
	public const int MaxBody = 4096;
	private const int Overhead = 10;
	/// <summary>SE2 Online: an AUTH with this body asks for the server's Steam ID before a Steam sign-in (the tool binds its
	/// ticket to it). The RESPONSE_VALUE body is <see cref="ServerIdReply"/> and the ID in decimal, 0 when there is none.</summary>
	public const string ServerIdQuery = "steam:server-id", ServerIdReply = "server-id:";

	public readonly record struct Packet(int Id, int Type, byte[] Body)
	{
		public string Text => Encoding.UTF8.GetString(Body);
	}

	public static byte[] Encode(int id, int type, ReadOnlySpan<byte> body)
	{
		if (body.Length > MaxBody) throw new ArgumentException($"RCON body over {MaxBody} bytes", nameof(body));
		var packet = new byte[4 + Overhead + body.Length];
		BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(0, 4), Overhead + body.Length);
		BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4, 4), id);
		BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8, 4), type);
		body.CopyTo(packet.AsSpan(12));
		return packet;
	}

	public static byte[] Encode(int id, int type, string body) => Encode(id, type, Encoding.UTF8.GetBytes(body ?? ""));

	/// <summary>
	/// A reply as RESPONSE_VALUE packets of at most <see cref="MaxBody"/> bytes, never splitting a UTF-8 character. An
	/// empty reply is one empty packet.
	/// </summary>
	public static IEnumerable<byte[]> Chunks(int id, string text)
	{
		byte[] bytes = Encoding.UTF8.GetBytes(text ?? "");
		if (bytes.Length == 0) { yield return Encode(id, Response, ReadOnlySpan<byte>.Empty); yield break; }
		int at = 0;
		while (at < bytes.Length)
		{
			int length = Math.Min(MaxBody, bytes.Length - at);
			// Back off continuation bytes (10xxxxxx) so the next chunk starts on a character.
			while (length > 1 && at + length < bytes.Length && (bytes[at + length] & 0xC0) == 0x80) length--;
			yield return Encode(id, Response, bytes.AsSpan(at, length));
			at += length;
		}
	}

	/// <summary>
	/// Reads one packet. Null when the stream ends cleanly before a packet starts. A size outside 10..10+MaxBody, a
	/// stream ending mid-packet or missing terminators throw <see cref="InvalidDataException"/>; the caller drops the
	/// connection. <paramref name="firstByte"/> bounds the wait for the packet to start, <paramref name="rest"/> the
	/// wait for the remainder (a slow trickle cannot hold a connection open).
	/// </summary>
	public static async Task<Packet?> ReadAsync(Stream stream, TimeSpan firstByte, TimeSpan rest, CancellationToken cancel)
	{
		var head = new byte[4];
		if (!await ReadHead(stream, head, firstByte, cancel)) return null;
		int size = BinaryPrimitives.ReadInt32LittleEndian(head);
		if (size < Overhead || size > Overhead + MaxBody) throw new InvalidDataException($"RCON packet size {size} is outside 10..{Overhead + MaxBody}");
		var data = new byte[size];
		await ReadExactly(stream, data, 0, rest, cancel);
		if (data[size - 1] != 0 || data[size - 2] != 0) throw new InvalidDataException("RCON packet is not terminated by two zero bytes");
		int id = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0, 4));
		int type = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4, 4));
		return new Packet(id, type, data.AsSpan(8, size - Overhead).ToArray());
	}

	// False on a clean end of stream before the packet's first byte.
	private static async Task<bool> ReadHead(Stream stream, byte[] head, TimeSpan timeout, CancellationToken cancel)
	{
		using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
		limit.CancelAfter(timeout);
		int got = 0;
		try
		{
			while (got < head.Length)
			{
				int n = await stream.ReadAsync(head.AsMemory(got), limit.Token).ConfigureAwait(false);
				if (n <= 0) return got == 0 ? false : throw new InvalidDataException("RCON stream ended inside a packet");
				got += n;
			}
		}
		catch (OperationCanceledException) when (!cancel.IsCancellationRequested) { throw new TimeoutException("RCON read timed out"); }
		return true;
	}

	private static async Task ReadExactly(Stream stream, byte[] buffer, int offset, TimeSpan timeout, CancellationToken cancel)
	{
		using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
		limit.CancelAfter(timeout);
		try
		{
			while (offset < buffer.Length)
			{
				int n = await stream.ReadAsync(buffer.AsMemory(offset), limit.Token).ConfigureAwait(false);
				if (n <= 0) throw new InvalidDataException("RCON stream ended inside a packet");
				offset += n;
			}
		}
		catch (OperationCanceledException) when (!cancel.IsCancellationRequested) { throw new TimeoutException("RCON read timed out"); }
	}
}
