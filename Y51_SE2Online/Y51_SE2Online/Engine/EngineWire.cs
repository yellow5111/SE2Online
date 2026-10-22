using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.Engine;

/// <summary>
/// Wire format of the engine transport. A frame is one peer's replication for one dispatch, sent reliably and applied
/// in sequence: [kind][u32 seq] then units in order. Archive units carry the game's own serialization (signals,
/// stories, entity creation and destruction); data units carry raw unmanaged entity data by network id and a per
/// direction type key, announced by a TypeDef unit before its first use. State messages carry the newest sampled data
/// (the game's unreliable motion samples), stamped so a late or reordered one never overwrites a newer value.
/// </summary>
internal static class EngineWire
{
	public enum Unit : byte
	{
		ReplicationArchive = 1,
		TypeDef = 2,
		Data = 3,
		DataRemove = 4,
		Sampled = 5,
		SampledRemove = 6,
		SequenceArchive = 7,
	}

	// [kind][u32 seq][flags]; flag 1: the units are deflated on their own; flag 2: [u32 length] then the units deflated as
	// the next part of this direction's continuous stream, so they share a dictionary with every earlier such frame and
	// must be read in sequence (the sequencer guarantees that; frames are reliable).
	public const int HeaderBytes = 6;

	private const int StreamFrom = 64, StreamTo = 1 << 20;

	/// <summary>One direction's continuous deflate stream (sender side).</summary>
	public sealed class StreamDeflater
	{
		private readonly MemoryStream _out = new();
		private readonly System.IO.Compression.DeflateStream _z;

		public StreamDeflater() => _z = new System.IO.Compression.DeflateStream(_out, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true);

		/// <summary>A frame from <see cref="Writer.Finish"/> without per-frame compression; frames outside the range stay as they are.</summary>
		public byte[] Pack(byte[] frame)
		{
			int body = frame.Length - HeaderBytes;
			if (body < StreamFrom || body > StreamTo || frame[5] != 0) return frame;
			_out.SetLength(0);
			_z.Write(frame, HeaderBytes, body);
			_z.Flush();
			var packed = new byte[HeaderBytes + 4 + _out.Length];
			Buffer.BlockCopy(frame, 0, packed, 0, HeaderBytes);
			packed[5] = 2;
			BinaryPrimitives.WriteInt32LittleEndian(packed.AsSpan(HeaderBytes), body);
			_out.Position = 0;
			_out.Read(packed, HeaderBytes + 4, (int)_out.Length);
			return packed;
		}
	}

	/// <summary>One direction's continuous inflate stream (receiver side); frames must be given in sequence.</summary>
	public sealed class StreamInflater
	{
		private readonly FeedStream _in = new();
		private readonly System.IO.Compression.DeflateStream _z;

		public StreamInflater() => _z = new System.IO.Compression.DeflateStream(_in, System.IO.Compression.CompressionMode.Decompress);

		public byte[] Unpack(byte[] frame)
		{
			if ((frame[5] & 2) == 0) return frame;
			int body = BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(HeaderBytes));
			if (body < 0 || body > MaxInflated) throw new InvalidDataException("an engine stream frame claims " + body + " bytes");
			_in.Feed(frame, HeaderBytes + 4, frame.Length - HeaderBytes - 4);
			var plain = new byte[HeaderBytes + body];
			Buffer.BlockCopy(frame, 0, plain, 0, HeaderBytes);
			plain[5] = 0;
			int at = HeaderBytes;
			while (at < plain.Length)
			{
				int n = _z.Read(plain, at, plain.Length - at);
				if (n <= 0) throw new InvalidDataException($"an engine stream frame inflated to {at - HeaderBytes} of {body} bytes");
				at += n;
			}
			return plain;
		}

		private sealed class FeedStream : Stream
		{
			private byte[] _buf = Array.Empty<byte>();
			private int _start, _count;

			public void Feed(byte[] bytes, int offset, int count)
			{
				if (_start + _count + count > _buf.Length)
				{
					var grown = new byte[Math.Max(_count + count, _buf.Length * 2)];
					Buffer.BlockCopy(_buf, _start, grown, 0, _count);
					_buf = grown;
					_start = 0;
				}
				Buffer.BlockCopy(bytes, offset, _buf, _start + _count, count);
				_count += count;
			}

			public override int Read(byte[] buffer, int offset, int count)
			{
				int n = Math.Min(count, _count);
				Buffer.BlockCopy(_buf, _start, buffer, offset, n);
				_start += n;
				_count -= n;
				if (_count == 0) _start = 0;
				return n;
			}

			public override bool CanRead => true;
			public override bool CanSeek => false;
			public override bool CanWrite => false;
			public override long Length => throw new NotSupportedException();
			public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
			public override void Flush() { }
			public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
			public override void SetLength(long value) => throw new NotSupportedException();
			public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
		}
	}

	private const int DeflateFrom = 384, DeflateTo = 1 << 20, MaxInflated = 8 << 20;

	public sealed class Writer
	{
		private readonly MemoryStream _ms = new();
		private readonly BinaryWriter _w;
		public int Units { get; private set; }

		public Writer(SyncMsg kind)
		{
			_w = new BinaryWriter(_ms, Encoding.UTF8, leaveOpen: true);
			_w.Write((byte)kind);
			_w.Write(0u);
			_w.Write((byte)0);
		}

		public bool IsEmpty => Units == 0;

		public void Archive(Unit unit, byte[] bytes)
		{
			Units++;
			_w.Write((byte)unit);
			_w.Write(bytes.Length);
			_w.Write(bytes);
		}

		public void TypeDef(ushort key, string name)
		{
			Units++;
			_w.Write((byte)Unit.TypeDef);
			_w.Write(key);
			_w.Write(name);
		}

		public void Data(uint netId, ushort key, ReadOnlySpan<byte> bytes)
		{
			Units++;
			_w.Write((byte)Unit.Data);
			_w.Write(netId);
			_w.Write(key);
			_w.Write((ushort)bytes.Length);
			_w.Write(bytes);
		}

		public void DataRemove(uint netId, ushort key)
		{
			Units++;
			_w.Write((byte)Unit.DataRemove);
			_w.Write(netId);
			_w.Write(key);
		}

		public void Sampled(long stamp, uint netId, ushort key, ReadOnlySpan<byte> bytes)
		{
			Units++;
			_w.Write((byte)Unit.Sampled);
			_w.Write(stamp);
			_w.Write(netId);
			_w.Write(key);
			_w.Write((ushort)bytes.Length);
			_w.Write(bytes);
		}

		public void SampledRemove(long stamp, uint netId, ushort key)
		{
			Units++;
			_w.Write((byte)Unit.SampledRemove);
			_w.Write(stamp);
			_w.Write(netId);
			_w.Write(key);
		}

		public byte[] Finish(uint seq, bool compress = true)
		{
			_w.Flush();
			byte[] bytes = _ms.ToArray();
			BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(1), seq);
			if (!compress || bytes.Length < DeflateFrom || bytes.Length > DeflateTo) return bytes;
			var packed = new MemoryStream(bytes.Length);
			packed.Write(bytes, 0, HeaderBytes);
			using (var deflate = new System.IO.Compression.DeflateStream(packed, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
				deflate.Write(bytes, HeaderBytes, bytes.Length - HeaderBytes);
			if (packed.Length >= bytes.Length * 9L / 10) return bytes;
			byte[] result = packed.ToArray();
			result[5] = 1;
			return result;
		}
	}

	public static uint SeqOf(byte[] frame) => BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(1));

	public interface IUnitSink
	{
		void OnArchive(Unit unit, byte[] bytes);
		void OnTypeDef(ushort key, string name);
		void OnData(uint netId, ushort key, ReadOnlySpan<byte> bytes);
		void OnDataRemove(uint netId, ushort key);
		void OnSampled(long stamp, bool reliable, uint netId, ushort key, ReadOnlySpan<byte> bytes);
		void OnSampledRemove(long stamp, uint netId, ushort key);
	}

	public static void Read(byte[] frame, IUnitSink sink)
	{
		Stream body = new MemoryStream(frame, HeaderBytes, frame.Length - HeaderBytes);
		if ((frame[5] & 1) != 0)
		{
			var inflated = new MemoryStream();
			using (var deflate = new System.IO.Compression.DeflateStream(body, System.IO.Compression.CompressionMode.Decompress))
			{
				var buffer = new byte[16384];
				int n;
				while ((n = deflate.Read(buffer, 0, buffer.Length)) > 0)
				{
					if (inflated.Length + n > MaxInflated) throw new InvalidDataException("an engine frame inflates past " + MaxInflated + " bytes");
					inflated.Write(buffer, 0, n);
				}
			}
			inflated.Position = 0;
			body = inflated;
		}
		using var r = new BinaryReader(body, Encoding.UTF8);
		while (r.BaseStream.Position < r.BaseStream.Length)
		{
			var unit = (Unit)r.ReadByte();
			switch (unit)
			{
			case Unit.ReplicationArchive:
			case Unit.SequenceArchive:
				sink.OnArchive(unit, r.ReadBytes(r.ReadInt32()));
				break;
			case Unit.TypeDef:
				sink.OnTypeDef(r.ReadUInt16(), r.ReadString());
				break;
			case Unit.Data:
			{
				uint id = r.ReadUInt32();
				ushort key = r.ReadUInt16();
				sink.OnData(id, key, r.ReadBytes(r.ReadUInt16()));
				break;
			}
			case Unit.DataRemove:
				sink.OnDataRemove(r.ReadUInt32(), r.ReadUInt16());
				break;
			case Unit.Sampled:
			{
				long stamp = r.ReadInt64();
				uint id = r.ReadUInt32();
				ushort key = r.ReadUInt16();
				sink.OnSampled(stamp, true, id, key, r.ReadBytes(r.ReadUInt16()));
				break;
			}
			case Unit.SampledRemove:
				sink.OnSampledRemove(r.ReadInt64(), r.ReadUInt32(), r.ReadUInt16());
				break;
			default:
				throw new InvalidDataException($"unknown engine unit {(byte)unit}");
			}
		}
	}

	/// <summary>Unreliable sampled state: [kind][i64 stamp][u16 n]{u32 id, u16 key, u16 len, bytes}, split to fit one state packet each.</summary>
	public static List<byte[]> EncodeState(long stamp, IReadOnlyList<(uint NetId, ushort Key, byte[] Bytes)> entries, int maxBytes)
	{
		var messages = new List<byte[]>();
		int i = 0;
		while (i < entries.Count)
		{
			using var ms = new MemoryStream();
			using var w = new BinaryWriter(ms);
			w.Write((byte)SyncMsg.EngineState);
			w.Write(stamp);
			w.Write((ushort)0);
			ushort n = 0;
			while (i < entries.Count && (n == 0 || ms.Length + 8 + entries[i].Bytes.Length <= maxBytes))
			{
				var e = entries[i++];
				w.Write(e.NetId);
				w.Write(e.Key);
				w.Write((ushort)e.Bytes.Length);
				w.Write(e.Bytes);
				n++;
			}
			w.Flush();
			byte[] bytes = ms.ToArray();
			BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(9), n);
			messages.Add(DeflateState(bytes));
		}
		return messages;
	}

	// The entry count's top bit: the entries after the 11-byte header are deflated (each state message on its own; they are
	// unreliable, so no stream is shared).
	private const ushort StateDeflated = 0x8000;

	private static byte[] DeflateState(byte[] bytes)
	{
		var packed = new MemoryStream(bytes.Length);
		packed.Write(bytes, 0, 11);
		using (var deflate = new System.IO.Compression.DeflateStream(packed, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
			deflate.Write(bytes, 11, bytes.Length - 11);
		if (packed.Length >= bytes.Length * 9L / 10) return bytes;
		byte[] result = packed.ToArray();
		BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(9), (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(9)) | StateDeflated));
		return result;
	}

	public static void ReadState(byte[] message, IUnitSink sink)
	{
		int n = BinaryPrimitives.ReadUInt16LittleEndian(message.AsSpan(9));
		if ((n & StateDeflated) != 0)
		{
			n &= ~StateDeflated;
			var plain = new MemoryStream();
			plain.Write(message, 0, 11);
			using (var inflate = new System.IO.Compression.DeflateStream(new MemoryStream(message, 11, message.Length - 11), System.IO.Compression.CompressionMode.Decompress))
			{
				var buffer = new byte[4096];
				int read;
				while ((read = inflate.Read(buffer, 0, buffer.Length)) > 0)
				{
					if (plain.Length + read > 1 << 16) throw new InvalidDataException("an engine state message inflates past 64 KiB");
					plain.Write(buffer, 0, read);
				}
			}
			message = plain.ToArray();
		}
		long stamp = BinaryPrimitives.ReadInt64LittleEndian(message.AsSpan(1));
		int at = 11;
		for (int k = 0; k < n; k++)
		{
			uint id = BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(at));
			ushort key = BinaryPrimitives.ReadUInt16LittleEndian(message.AsSpan(at + 4));
			int len = BinaryPrimitives.ReadUInt16LittleEndian(message.AsSpan(at + 6));
			sink.OnSampled(stamp, false, id, key, message.AsSpan(at + 8, len));
			at += 8 + len;
		}
	}
}
