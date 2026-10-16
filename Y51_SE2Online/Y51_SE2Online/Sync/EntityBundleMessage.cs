using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Y51_SE2Online.Net;

namespace Y51_SE2Online.Sync;

public sealed class EntityBundleMessage
{
	public const byte KindWorld = 0;

	public const byte KindSpawn = 1;

	public uint LifeSeq;

	public byte Kind;

	public uint HostTick;

	public byte[] Bundle = Array.Empty<byte>();

	public List<EntityLocator> Entities = new();

	public List<(uint NetId, List<DataBlob> Data)> Data = new();

	public List<(uint Child, uint Parent)> Parents = new();

	public List<uint> Staged = new();

	public List<uint> Skipped = new();

	public List<Guid> RequiredBlobs = new();

	public const byte CompressedFlag = 128;

	public const int CompressAboveBytes = 65536;

	public int RawBodyBytes;

	public int WireBodyBytes;

	public int DataBytes
	{
		get
		{
			int total = 0;
			foreach (var (_, blobs) in Data)
			{
				foreach (DataBlob blob in blobs)
				{
					total += blob.Bytes.Length;
				}
			}
			return total;
		}
	}

	public byte[] Encode()
	{
		var bodyWriter = new WireWriter(64 + Bundle.Length + Entities.Count * 12 + DataBytes + Data.Count * 8);
		EncodeBody(bodyWriter);
		byte[] raw = bodyWriter.ToArray();
		RawBodyBytes = raw.Length;
		byte kind = Kind;
		byte[] body = raw;
		if (raw.Length >= CompressAboveBytes)
		{
			using var ms = new MemoryStream(raw.Length / 4 + 64);
			using (var deflate = new DeflateStream(ms, CompressionLevel.Fastest, leaveOpen: true))
			{
				deflate.Write(raw, 0, raw.Length);
			}
			// Compressed body is prefixed with the raw length; only used when it actually saves bytes.
			if (ms.Length + 4 < raw.Length && !BulkCompression.OverRatio(raw.Length, (int)ms.Length + 4))
			{
				byte[] compressed = new byte[4 + ms.Length];
				BinaryPrimitives.WriteInt32LittleEndian(compressed, raw.Length);
				ms.Position = 0L;
				ms.Read(compressed, 4, (int)ms.Length);
				body = compressed;
				kind |= CompressedFlag;
			}
		}
		WireBodyBytes = body.Length;
		var w = new WireWriter(16 + body.Length);
		w.U8(49).U32(LifeSeq).U8(kind)
			.U32(HostTick)
			.Bytes(body);
		return w.ToArray();
	}

	private void EncodeBody(WireWriter w)
	{
		w.I32(Bundle.Length).Bytes(Bundle);
		w.I32(Entities.Count);
		foreach (EntityLocator entity in Entities)
		{
			w.U32(entity.NetId).I32(entity.Root).U8((byte)entity.Path.Length);
			foreach (int index in entity.Path)
			{
				w.I32(index);
			}
		}
		w.I32(Data.Count);
		foreach (var (netId, blobs) in Data)
		{
			w.U32(netId).U8((byte)blobs.Count);
			foreach (DataBlob blob in blobs)
			{
				w.U16(blob.Key).U16((ushort)blob.Bytes.Length).Bytes(blob.Bytes);
			}
		}
		w.I32(Parents.Count);
		foreach (var (child, parent) in Parents)
		{
			w.U32(child).U32(parent);
		}
		w.I32(Staged.Count);
		foreach (uint netId in Staged)
		{
			w.U32(netId);
		}
		w.I32(Skipped.Count);
		foreach (uint netId in Skipped)
		{
			w.U32(netId);
		}
		WireGuids.Write(w, RequiredBlobs);
	}

	public static EntityBundleMessage Decode(ReadOnlySpan<byte> d)
	{
		var header = new WireReader(d.Slice(1));
		var msg = new EntityBundleMessage
		{
			LifeSeq = header.U32(),
			Kind = header.U8(),
			HostTick = header.U32()
		};
		ReadOnlySpan<byte> body = d.Slice(10);
		byte[] inflated = null;
		if ((msg.Kind & CompressedFlag) != 0)
		{
			msg.Kind &= 127;
			int rawLength = BinaryPrimitives.ReadInt32LittleEndian(body);
			if (rawLength < 0 || rawLength > 536870912 || BulkCompression.OverRatio(rawLength, body.Length))
			{
				throw new InvalidDataException($"bundle body {rawLength} B from {body.Length} B");
			}
			byte[] deflated = body.Slice(4).ToArray();
			inflated = BulkCompression.Inflate(deflated, 0, deflated.Length, default, rawLength) ?? throw new InvalidDataException($"bundle body is not {rawLength} B of deflated data");
			msg.WireBodyBytes = body.Length;
			body = inflated;
		}
		msg.RawBodyBytes = body.Length;
		if (inflated is null)
		{
			msg.WireBodyBytes = body.Length;
		}
		var r = new WireReader(body);
		msg.Bundle = r.Bytes(r.I32()).ToArray();
		int count = r.I32();
		msg.Entities = new List<EntityLocator>(count);
		for (int i = 0; i < count; i++)
		{
			uint netId = r.U32();
			int root = r.I32();
			int[] path = new int[r.U8()];
			for (int k = 0; k < path.Length; k++)
			{
				path[k] = r.I32();
			}
			msg.Entities.Add(new EntityLocator(netId, root, path));
		}
		count = r.I32();
		msg.Data = new List<(uint, List<DataBlob>)>(count);
		for (int i = 0; i < count; i++)
		{
			uint netId = r.U32();
			int blobCount = r.U8();
			var blobs = new List<DataBlob>(blobCount);
			for (int k = 0; k < blobCount; k++)
			{
				ushort key = r.U16();
				int length = r.U16();
				blobs.Add(new DataBlob(key, r.Bytes(length).ToArray()));
			}
			msg.Data.Add((netId, blobs));
		}
		count = r.I32();
		for (int i = 0; i < count; i++)
		{
			msg.Parents.Add((r.U32(), r.U32()));
		}
		count = r.I32();
		for (int i = 0; i < count; i++)
		{
			msg.Staged.Add(r.U32());
		}
		count = r.I32();
		for (int i = 0; i < count; i++)
		{
			msg.Skipped.Add(r.U32());
		}
		msg.RequiredBlobs = WireGuids.Read(ref r);
		return msg;
	}

	public static void SetLifeSeq(byte[] encoded, uint seq) => BinaryPrimitives.WriteUInt32LittleEndian(encoded.AsSpan(1), seq);
}
