using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Y51_SE2Online.Sync;

/// <summary>An oriented box in world space: centre, unit-quaternion rotation and half extents in metres.</summary>
public readonly record struct GhostBox(Vec3D Center, float Qx, float Qy, float Qz, float Qw, float Hx, float Hy, float Hz)
{
	public const int Bytes = 3 * 8 + 4 * 4 + 3 * 4;

	/// <summary>Half of a 512 m paste; anything bigger is not a real placement.</summary>
	public const float MaxHalfMeters = 256;

	public double Radius => Math.Sqrt((double)Hx * Hx + (double)Hy * Hy + (double)Hz * Hz);

	public bool Near(GhostBox o) =>
		(Center - o.Center).Length <= BuildGhost.MoveMeters && Wire.SameTurn(Qx, Qy, Qz, Qw, o.Qx, o.Qy, o.Qz, o.Qw)
		&& Math.Abs(Hx - o.Hx) < 0.005f && Math.Abs(Hy - o.Hy) < 0.005f && Math.Abs(Hz - o.Hz) < 0.005f;

	internal void Write(Span<byte> s)
	{
		Wire.Vec(s, Center);
		Wire.Floats(s.Slice(24), Qx, Qy, Qz, Qw, Hx, Hy, Hz);
	}

	internal static bool TryRead(ReadOnlySpan<byte> s, out GhostBox box)
	{
		box = default;
		Span<float> f = stackalloc float[7];
		if (!Wire.TryVec(s, out var c) || !Wire.TryFloats(s.Slice(24), f)) return false;
		for (int i = 4; i < 7; i++) if (f[i] <= 0 || f[i] > MaxHalfMeters) return false;
		if (!Wire.Unit(f)) return false;
		box = new GhostBox(c, f[0], f[1], f[2], f[3], f[4], f[5], f[6]);
		return true;
	}
}

/// <summary>Where the builder's preview grid sits in the world, and which <see cref="GhostShape"/> it shows.</summary>
public readonly record struct GhostPose(uint ShapeId, Vec3D Position, float Qx, float Qy, float Qz, float Qw)
{
	public const int Bytes = 4 + 24 + 16;

	public bool Near(GhostPose o) => ShapeId == o.ShapeId && (Position - o.Position).Length <= BuildGhost.MoveMeters && Wire.SameTurn(Qx, Qy, Qz, Qw, o.Qx, o.Qy, o.Qz, o.Qw);

	internal void Write(Span<byte> s)
	{
		BinaryPrimitives.WriteUInt32LittleEndian(s, ShapeId);
		Wire.Vec(s.Slice(4), Position);
		Wire.Floats(s.Slice(28), Qx, Qy, Qz, Qw);
	}

	internal static bool TryRead(ReadOnlySpan<byte> s, out GhostPose pose)
	{
		pose = default;
		Span<float> q = stackalloc float[4];
		if (!Wire.TryVec(s.Slice(4), out var p) || !Wire.TryFloats(s.Slice(28), q) || !Wire.Unit(q)) return false;
		pose = new GhostPose(BinaryPrimitives.ReadUInt32LittleEndian(s), p, q[0], q[1], q[2], q[3]);
		return true;
	}
}

/// <summary>One block of a shape: index into <see cref="GhostShape.Types"/> and its transform in the preview grid.</summary>
public readonly record struct GhostBlock(byte Type, float X, float Y, float Z, float Qx, float Qy, float Qz, float Qw)
{
	public const int Bytes = 1 + 7 * 4;
}

/// <summary>
/// The blocks of a preview, so viewers can build SE2's own hologram of it: block definitions (entity composites,
/// deduplicated) and per-block grid transforms. Above <see cref="MaxBlocks"/> only the outline box is shown.
/// </summary>
public sealed class GhostShape
{
	/// <summary>SE2 itself warns that the game may lag once a preview passes 100 entities (BlockPlacementPreview).</summary>
	public const int MaxBlocks = 100;

	/// <summary>Farthest a block may sit from the preview grid origin.</summary>
	public const float MaxOffsetMeters = 512;

	public readonly Guid[] Types;

	public readonly GhostBlock[] Blocks;

	public readonly uint Id;

	private GhostShape(Guid[] types, GhostBlock[] blocks, uint id)
	{
		Types = types;
		Blocks = blocks;
		Id = id;
	}

	public int Bytes => 1 + 16 * Types.Length + 2 + GhostBlock.Bytes * Blocks.Length;

	/// <summary>Null when there is nothing to show or it is too big; the outline is used then.</summary>
	public static GhostShape Create(IReadOnlyList<(Guid Type, float X, float Y, float Z, float Qx, float Qy, float Qz, float Qw)> blocks)
	{
		if (blocks.Count == 0 || blocks.Count > MaxBlocks) return null;
		var types = new List<Guid>();
		var list = new GhostBlock[blocks.Count];
		for (int i = 0; i < blocks.Count; i++)
		{
			var b = blocks[i];
			int t = types.IndexOf(b.Type);
			if (t < 0) { t = types.Count; types.Add(b.Type); }
			list[i] = new GhostBlock((byte)t, b.X, b.Y, b.Z, b.Qx, b.Qy, b.Qz, b.Qw);
		}
		var shape = new GhostShape(types.ToArray(), list, 0);
		var bytes = new byte[shape.Bytes];
		shape.Write(bytes);
		return TryRead(bytes, out var read) ? read : null;
	}

	internal void Write(Span<byte> s)
	{
		s[0] = (byte)Types.Length;
		int at = 1;
		foreach (Guid t in Types) { t.TryWriteBytes(s.Slice(at)); at += 16; }
		BinaryPrimitives.WriteUInt16LittleEndian(s.Slice(at), (ushort)Blocks.Length);
		at += 2;
		foreach (var b in Blocks)
		{
			s[at] = b.Type;
			Wire.Floats(s.Slice(at + 1), b.Qx, b.Qy, b.Qz, b.Qw, b.X, b.Y, b.Z);
			at += GhostBlock.Bytes;
		}
	}

	/// <summary>Reads a shape that fills <paramref name="s"/> exactly.</summary>
	internal static bool TryRead(ReadOnlySpan<byte> s, out GhostShape shape)
	{
		shape = null;
		if (s.Length < 3) return false;
		int types = s[0], at = 1 + 16 * types;
		if (types == 0 || s.Length < at + 2) return false;
		int count = BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(at));
		if (count < types || count > MaxBlocks || s.Length != at + 2 + GhostBlock.Bytes * count) return false;
		var guids = new Guid[types];
		for (int i = 0; i < types; i++)
		{
			guids[i] = new Guid(s.Slice(1 + 16 * i, 16));
			if (guids[i] == Guid.Empty) return false;
		}
		at += 2;
		var blocks = new GhostBlock[count];
		Span<float> f = stackalloc float[7];
		for (int i = 0; i < count; i++, at += GhostBlock.Bytes)
		{
			if (s[at] >= types || !Wire.TryFloats(s.Slice(at + 1), f) || !Wire.Unit(f)) return false;
			for (int k = 4; k < 7; k++) if (Math.Abs(f[k]) > MaxOffsetMeters) return false;
			blocks[i] = new GhostBlock(s[at], f[4], f[5], f[6], f[0], f[1], f[2], f[3]);
		}
		shape = new GhostShape(guids, blocks, Fnv(s));
		return true;
	}

	private static uint Fnv(ReadOnlySpan<byte> s)
	{
		uint h = 2166136261;
		foreach (byte b in s) h = (h ^ b) * 16777619;
		return h == 0 ? 1 : h;
	}
}

/// <summary>
/// What a player in build mode shows everyone else: SE2's placement box around the block or paste they are about to
/// place, the block they are aiming at, and where SE2's hologram of it sits (with its blocks now and then).
/// </summary>
public readonly record struct BuildGhost(byte Flags, GhostBox? Box, GhostBox? Target, GhostPose? Pose = null, GhostShape Shape = null)
{
	public const byte Paste = 1, Blocked = 2;

	private const byte KnownFlags = Paste | Blocked;

	private const byte BoxBit = 1, TargetBit = 2, PoseBit = 4, ShapeBit = 8;

	public const double MoveMeters = 0.02;

	public bool Active => Box != null || Target != null || Pose != null;

	public bool Near(BuildGhost o) => Flags == o.Flags && Same(Box, o.Box) && Same(Target, o.Target)
		&& (Pose is { } p ? o.Pose is { } q && p.Near(q) : o.Pose == null);

	private static bool Same(GhostBox? a, GhostBox? b) => a is { } x ? b is { } y && x.Near(y) : b == null;

	private byte Mask => (byte)((Box != null ? BoxBit : 0) | (Target != null ? TargetBit : 0) | (Pose != null ? PoseBit : 0) | (Pose != null && Shape != null ? ShapeBit : 0));

	internal int Size => 2 + (Box != null ? GhostBox.Bytes : 0) + (Target != null ? GhostBox.Bytes : 0) + (Pose != null ? GhostPose.Bytes : 0) + ((Mask & ShapeBit) != 0 ? Shape.Bytes : 0);

	internal void Write(Span<byte> s)
	{
		byte mask = Mask;
		s[0] = (byte)(Flags & KnownFlags);
		s[1] = mask;
		int at = 2;
		if (Box is { } box) { box.Write(s.Slice(at)); at += GhostBox.Bytes; }
		if (Target is { } target) { target.Write(s.Slice(at)); at += GhostBox.Bytes; }
		if (Pose is { } pose) { pose.Write(s.Slice(at)); at += GhostPose.Bytes; }
		if ((mask & ShapeBit) != 0) Shape.Write(s.Slice(at));
	}

	internal static bool TryRead(ReadOnlySpan<byte> s, out BuildGhost ghost)
	{
		ghost = default;
		if (s.Length < 2 || (s[0] & ~KnownFlags) != 0) return false;
		byte mask = s[1];
		if ((mask & ~(BoxBit | TargetBit | PoseBit | ShapeBit)) != 0 || (mask & ShapeBit) != 0 && (mask & PoseBit) == 0) return false;
		GhostBox? box = null, target = null;
		GhostPose? pose = null;
		GhostShape shape = null;
		int at = 2;
		if ((mask & BoxBit) != 0)
		{
			if (s.Length < at + GhostBox.Bytes || !GhostBox.TryRead(s.Slice(at), out var b)) return false;
			box = b;
			at += GhostBox.Bytes;
		}
		if ((mask & TargetBit) != 0)
		{
			if (s.Length < at + GhostBox.Bytes || !GhostBox.TryRead(s.Slice(at), out var t)) return false;
			target = t;
			at += GhostBox.Bytes;
		}
		if ((mask & PoseBit) != 0)
		{
			if (s.Length < at + GhostPose.Bytes || !GhostPose.TryRead(s.Slice(at), out var p)) return false;
			pose = p;
			at += GhostPose.Bytes;
		}
		if ((mask & ShapeBit) != 0)
		{
			if (!GhostShape.TryRead(s.Slice(at), out shape) || shape.Id != pose.Value.ShapeId) return false;
		}
		else if (at != s.Length) return false;
		ghost = new BuildGhost(s[0], box, target, pose, shape);
		return true;
	}
}

internal static class Wire
{
	/// <summary>|dot| of two unit quaternions about 1 degree apart.</summary>
	private const float TurnDot = 0.99996f;

	public static bool SameTurn(float ax, float ay, float az, float aw, float bx, float by, float bz, float bw) => Math.Abs(ax * bx + ay * by + az * bz + aw * bw) >= TurnDot;

	public static void Vec(Span<byte> s, Vec3D v)
	{
		BinaryPrimitives.WriteDoubleLittleEndian(s, v.X);
		BinaryPrimitives.WriteDoubleLittleEndian(s.Slice(8), v.Y);
		BinaryPrimitives.WriteDoubleLittleEndian(s.Slice(16), v.Z);
	}

	public static bool TryVec(ReadOnlySpan<byte> s, out Vec3D v)
	{
		v = new Vec3D(BinaryPrimitives.ReadDoubleLittleEndian(s), BinaryPrimitives.ReadDoubleLittleEndian(s.Slice(8)), BinaryPrimitives.ReadDoubleLittleEndian(s.Slice(16)));
		return double.IsFinite(v.X) && double.IsFinite(v.Y) && double.IsFinite(v.Z);
	}

	public static void Floats(Span<byte> s, params float[] f)
	{
		for (int i = 0; i < f.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(s.Slice(4 * i), f[i]);
	}

	public static bool TryFloats(ReadOnlySpan<byte> s, Span<float> into)
	{
		for (int i = 0; i < into.Length; i++)
		{
			into[i] = BinaryPrimitives.ReadSingleLittleEndian(s.Slice(4 * i));
			if (!float.IsFinite(into[i])) return false;
		}
		return true;
	}

	/// <summary>The first four floats must be a near-unit quaternion; they are normalised in place.</summary>
	public static bool Unit(Span<float> f)
	{
		double q = Math.Sqrt((double)f[0] * f[0] + (double)f[1] * f[1] + (double)f[2] * f[2] + (double)f[3] * f[3]);
		if (q < 0.9 || q > 1.1) return false;
		for (int i = 0; i < 4; i++) f[i] = (float)(f[i] / q);
		return true;
	}
}

/// <summary>BuildGhost on the wire: a player sends it up, the host relays it down with the builder's client id.</summary>
public static class BuildGhostMessage
{
	/// <summary>Ghosts for the local player, from the host's own relay (listen host) or a down message.</summary>
	/// <summary>Builder, ghost, whether it came on the reliable lane.</summary>
	public static Action<ushort, BuildGhost, bool> Received;

	/// <summary>The host's "show build previews" setting, read live.</summary>
	public static Func<bool> HostEnabled = () => true;

	/// <summary>Host: whether a shape type is a block definition this game has; shapes naming anything else are dropped.</summary>
	public static Func<Guid, bool> IsBlockType = _ => true;

	public static byte[] EncodeUp(BuildGhost g)
	{
		var data = new byte[1 + g.Size];
		data[0] = (byte)SyncMsg.BuildGhost;
		g.Write(data.AsSpan(1));
		return data;
	}

	public static byte[] EncodeDown(ushort from, BuildGhost g)
	{
		var data = new byte[3 + g.Size];
		data[0] = (byte)SyncMsg.BuildGhost;
		BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(1), from);
		g.Write(data.AsSpan(3));
		return data;
	}

	public static bool TryDecodeUp(byte[] data, out BuildGhost g)
	{
		g = default;
		return data != null && data.Length > 1 && data[0] == (byte)SyncMsg.BuildGhost && BuildGhost.TryRead(data.AsSpan(1), out g);
	}

	public static bool TryDecodeDown(byte[] data, out ushort from, out BuildGhost g)
	{
		from = 0;
		g = default;
		if (data == null || data.Length < 3 || data[0] != (byte)SyncMsg.BuildGhost) return false;
		from = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(1));
		return BuildGhost.TryRead(data.AsSpan(3), out g);
	}
}

/// <summary>Sender side: send on change at most 10 Hz, a keep-alive each second while shown, a clear once; the shape
/// rides along when it changes and with each keep-alive, so a viewer who walks into range gets it within a second.</summary>
public sealed class GhostThrottle
{
	public const long MinIntervalMs = 100, KeepAliveMs = 1000;

	private BuildGhost _sent;

	private long _sentMs = long.MinValue / 2, _shapeMs = long.MinValue / 2;

	private uint _shapeId;

	/// <summary>What to send now, or null.</summary>
	public BuildGhost? Next(BuildGhost now, long ms)
	{
		bool due = !now.Active ? _sent.Active
			: ms - _sentMs >= (now.Near(_sent) ? KeepAliveMs : MinIntervalMs);
		if (!due) return null;
		_sent = now;
		_sentMs = ms;
		// After a clear the first preview carries its shape again (viewers drop the old one), and so goes reliable.
		if (!now.Active) { _shapeId = 0; _shapeMs = long.MinValue / 2; }
		if (now.Pose is not { } pose || now.Shape == null) return now with { Shape = null };
		if (pose.ShapeId == _shapeId && ms - _shapeMs < KeepAliveMs) return now with { Shape = null };
		_shapeId = pose.ShapeId;
		_shapeMs = ms;
		return now;
	}

	public void Reset()
	{
		_sent = default;
		_sentMs = _shapeMs = long.MinValue / 2;
		_shapeId = 0;
	}
}

/// <summary>Host side: per-builder message budget, plausibility and range.</summary>
public sealed class GhostRelay
{
	/// <summary>Well inside the 1 km minimum streaming radius, so every viewer in range also has the builder streamed.</summary>
	public const double RangeMeters = 500;

	/// <summary>Farthest a box surface may be from the builder's character; SE2's paste distance stays well inside it.</summary>
	public const double ReachMeters = 300;

	public const double PerSecond = 15, Burst = 30;

	private readonly Dictionary<ushort, (double Tokens, long AtMs)> _buckets = new();

	public bool Allow(ushort builder, long nowMs)
	{
		lock (_buckets)
		{
			var (tokens, at) = _buckets.TryGetValue(builder, out var bucket) ? bucket : (Burst, nowMs);
			tokens = Math.Min(Burst, tokens + Math.Max(0, nowMs - at) * PerSecond / 1000.0);
			bool ok = tokens >= 1;
			_buckets[builder] = (ok ? tokens - 1 : tokens, nowMs);
			return ok;
		}
	}

	public static bool Plausible(Vec3D builder, BuildGhost g) =>
		Reach(builder, g.Box) && Reach(builder, g.Target) && (g.Pose is not { } p || (p.Position - builder).Length <= ReachMeters + GhostShape.MaxOffsetMeters);

	private static bool Reach(Vec3D builder, GhostBox? box) => box is not { } b || (b.Center - builder).Length - b.Radius <= ReachMeters;

	public static bool InRange(Vec3D builder, Vec3D viewer) => (builder - viewer).Length <= RangeMeters;

	/// <summary>The ghost as relayed: a shape naming anything but this game's blocks is dropped (viewers keep the outline).</summary>
	public static BuildGhost Vetted(BuildGhost g, Func<Guid, bool> isBlockType)
	{
		if (g.Shape == null) return g;
		foreach (Guid t in g.Shape.Types)
			if (!isBlockType(t)) return g with { Shape = null };
		return g;
	}
}

/// <summary>
/// Which lane a ghost goes on. Poses without a shape stream on the unreliable State lane, so one lost packet does not
/// hold every later pose for a round trip; clears and shapes stay on the reliable Control lane. Handlers on both ends
/// read the message whatever lane it came on, so the bytes are the same as before.
/// </summary>
public static class GhostLane
{
	public static bool Reliable(BuildGhost g) => !g.Active || g.Shape != null;

	/// <summary>An unreliable pose this soon after a reliable clear may be one sent before it: it would bring a cancelled preview back.</summary>
	public const long AfterClearMs = 300;
}

/// <summary>
/// Viewer side: eases a remote preview toward each new pose (time constant <see cref="TauMs"/>) instead of stepping at
/// the 10 Hz send rate. It snaps on the first pose, a new shape, a jump over <see cref="JumpMeters"/> or a turn over
/// <see cref="TurnDegrees"/>. The wire carries no time, so this is smoothing, not timed interpolation: about one tau late,
/// like remote characters' own playback.
/// </summary>
public sealed class GhostSmoother
{
	public const double TauMs = 60, JumpMeters = 8, TurnDegrees = 60;

	private static readonly double TurnDot = Math.Cos(TurnDegrees / 2 * Math.PI / 180);

	private bool _has;
	private uint _key;
	private long _ms;
	private Vec3D _p;
	private float _x, _y, _z, _w;

	public void Reset() => _has = false;

	public (Vec3D Position, float Qx, float Qy, float Qz, float Qw) Step(uint key, Vec3D target, float qx, float qy, float qz, float qw, long nowMs)
	{
		float dot = _x * qx + _y * qy + _z * qz + _w * qw;
		if (!_has || key != _key || (target - _p).Length > JumpMeters || Math.Abs(dot) < TurnDot)
		{
			(_has, _key, _ms, _p, _x, _y, _z, _w) = (true, key, nowMs, target, qx, qy, qz, qw);
			return (_p, _x, _y, _z, _w);
		}
		double a = 1 - Math.Exp(-Math.Max(0, nowMs - _ms) / TauMs);
		_ms = nowMs;
		_p += (target - _p) * a;
		if (dot < 0) (qx, qy, qz, qw) = (-qx, -qy, -qz, -qw);
		float k = (float)a, x = _x + (qx - _x) * k, y = _y + (qy - _y) * k, z = _z + (qz - _z) * k, w = _w + (qw - _w) * k;
		float n = MathF.Sqrt(x * x + y * y + z * z + w * w);
		if (n > 1e-6f) (_x, _y, _z, _w) = (x / n, y / n, z / n, w / n);
		return (_p, _x, _y, _z, _w);
	}

	/// <summary>A box eased as a whole (centre and rotation; its size snaps).</summary>
	public GhostBox Step(GhostBox target, long nowMs)
	{
		var (p, x, y, z, w) = Step(0, target.Center, target.Qx, target.Qy, target.Qz, target.Qw, nowMs);
		return target with { Center = p, Qx = x, Qy = y, Qz = z, Qw = w };
	}
}

/// <summary>Viewer side: the latest ghost and shape per builder; one not refreshed by the keep-alive is gone.</summary>
public sealed class GhostBoard
{
	public const long ExpireMs = 3 * GhostThrottle.KeepAliveMs;

	private readonly Dictionary<ushort, (BuildGhost Ghost, long AtMs, GhostShape Shape)> _ghosts = new();

	private readonly Dictionary<ushort, long> _clearedMs = new();

	public void Put(ushort builder, BuildGhost g, long ms) => Put(builder, g, ms, reliable: true);

	/// <summary><paramref name="reliable"/>: it came on the Control lane (<see cref="GhostLane"/>).</summary>
	public void Put(ushort builder, BuildGhost g, long ms, bool reliable)
	{
		lock (_ghosts)
		{
			if (!g.Active)
			{
				_ghosts.Remove(builder);
				if (reliable) _clearedMs[builder] = ms;
				return;
			}
			if (!reliable && _clearedMs.TryGetValue(builder, out long cleared) && ms - cleared < GhostLane.AfterClearMs) return;
			GhostShape shape = g.Shape ?? (_ghosts.TryGetValue(builder, out var old) ? old.Shape : null);
			_ghosts[builder] = (g with { Shape = null }, ms, shape);
		}
	}

	public void Clear()
	{
		lock (_ghosts) { _ghosts.Clear(); _clearedMs.Clear(); }
	}

	/// <summary>Live ghosts; Shape is set only when it is the one the ghost's pose shows.</summary>
	public int Live(long ms, List<(ushort Builder, BuildGhost Ghost, GhostShape Shape)> into)
	{
		into.Clear();
		lock (_ghosts)
		{
			if (_ghosts.Count == 0) return 0;
			List<ushort> stale = null;
			foreach (var (id, (g, at, shape)) in _ghosts)
				if (ms - at > ExpireMs) (stale ??= new()).Add(id);
				else into.Add((id, g, g.Pose is { } p && shape?.Id == p.ShapeId ? shape : null));
			if (stale != null) foreach (ushort id in stale) _ghosts.Remove(id);
		}
		return into.Count;
	}
}
