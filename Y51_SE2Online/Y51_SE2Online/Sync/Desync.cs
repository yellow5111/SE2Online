using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Y51_SE2Online.Net;

namespace Y51_SE2Online.Sync;

public enum DesyncKind : byte
{
	Missing = 1,
	Extra = 2,
	Children = 3,
	State = 4,
	Position = 5
}

[Flags]
public enum DigestFlags : byte
{
	None = 0,
	/// <summary>The host has known this root for only a moment: neither missing nor extra yet.</summary>
	Young = 1,
	/// <summary>Changed on the host since its previous digest: an update may be in flight, compare existence only.</summary>
	Busy = 2,
	/// <summary>The player's own character (client prediction) or something the host never repairs for this player.</summary>
	Exempt = 4,
	/// <summary>Planets, asteroids, very big grids: existence and position only.</summary>
	NoDetail = 8,
	NoPosition = 16
}

/// <summary>One root entity as the host sees it: its replicated descendants, a hash of their replicated data, its position.</summary>
public readonly record struct RootDigest(uint NetId, DigestFlags Flags, uint Children, uint Hash, double X, double Y, double Z)
{
	public bool SameState(in RootDigest other) =>
		Children == other.Children && Hash == other.Hash && (Flags & DigestFlags.NoPosition) == (other.Flags & DigestFlags.NoPosition)
		&& Math.Abs(X - other.X) < DesyncDigest.StillMeters && Math.Abs(Y - other.Y) < DesyncDigest.StillMeters && Math.Abs(Z - other.Z) < DesyncDigest.StillMeters;
}

/// <summary>What the client holds for one root, built the same way as the host's digest.</summary>
public sealed class ClientRoot
{
	public uint Children;
	public bool HasPosition;
	public double X, Y, Z;
	public readonly Dictionary<uint, uint> Types = new();
	public uint Hash => DesyncDigest.Combine(Types);
}

public readonly record struct DesyncMismatch(uint NetId, DesyncKind Kind, uint Children, uint Hash, double X, double Y, double Z, (uint Type, uint Sum)[] Types);

public sealed class DesyncCheck
{
	public uint LifeSeq;
	public uint CheckId;
	/// <summary>Every root the client should hold with a NetId in [Lo, Hi] is listed; anything else there is extra.</summary>
	public uint Lo, Hi;
	public List<RootDigest> Roots = new();
}

/// <summary>Host to client: a slice of the host's digests, sent in the lifecycle order so spawns and despawns before it are applied.</summary>
public static class DesyncCheckMessage
{
	public const int MaxRoots = 256;

	public static byte[] Encode(DesyncCheck check)
	{
		var w = new WireWriter(32 + check.Roots.Count * 29).U8((byte)SyncMsg.DesyncCheck).U32(check.LifeSeq).U32(check.CheckId).U32(check.Lo).U32(check.Hi).U16((ushort)check.Roots.Count);
		double ox = 0, oy = 0, oz = 0;
		var first = check.Roots.FirstOrDefault(r => (r.Flags & DigestFlags.NoPosition) == 0);
		if (first.NetId != 0) (ox, oy, oz) = (first.X, first.Y, first.Z);
		w.U64((ulong)BitConverter.DoubleToInt64Bits(ox)).U64((ulong)BitConverter.DoubleToInt64Bits(oy)).U64((ulong)BitConverter.DoubleToInt64Bits(oz));
		foreach (var r in check.Roots)
		{
			w.U32(r.NetId).U8((byte)r.Flags);
			if ((r.Flags & (DigestFlags.Young | DigestFlags.Exempt)) != 0) continue;
			if ((r.Flags & DigestFlags.NoDetail) == 0) w.U32(r.Children).U32(r.Hash);
			if ((r.Flags & DigestFlags.NoPosition) == 0) w.U32(F(r.X - ox)).U32(F(r.Y - oy)).U32(F(r.Z - oz));
		}
		return w.ToArray();
	}

	public static DesyncCheck Decode(ReadOnlySpan<byte> data)
	{
		var r = new WireReader(data.Slice(1));
		var check = new DesyncCheck { LifeSeq = r.U32(), CheckId = r.U32(), Lo = r.U32(), Hi = r.U32() };
		int count = r.U16();
		if (count > MaxRoots || check.Lo > check.Hi) throw new ArgumentException("Invalid desync check.");
		double ox = D(r.U64()), oy = D(r.U64()), oz = D(r.U64());
		for (int i = 0; i < count; i++)
		{
			uint id = r.U32();
			var flags = (DigestFlags)r.U8();
			uint children = 0, hash = 0;
			double x = 0, y = 0, z = 0;
			if ((flags & (DigestFlags.Young | DigestFlags.Exempt)) == 0)
			{
				if ((flags & DigestFlags.NoDetail) == 0) (children, hash) = (r.U32(), r.U32());
				if ((flags & DigestFlags.NoPosition) == 0) (x, y, z) = (ox + G(r.U32()), oy + G(r.U32()), oz + G(r.U32()));
			}
			check.Roots.Add(new RootDigest(id, flags, children, hash, x, y, z));
		}
		return check;
	}

	private static uint F(double v) => (uint)BitConverter.SingleToInt32Bits((float)v);
	private static double G(uint v) => BitConverter.Int32BitsToSingle((int)v);
	private static double D(ulong v) => BitConverter.Int64BitsToDouble((long)v);
}

/// <summary>Client to host: the roots of one check that did not match, with what the client has.</summary>
public static class DesyncReportMessage
{
	public const int MaxEntries = 256;
	public const int MaxTypes = 64;

	public static byte[] Encode(uint checkId, IReadOnlyList<DesyncMismatch> mismatches)
	{
		var w = new WireWriter(8 + mismatches.Count * 40).U8((byte)SyncMsg.DesyncReport).U32(checkId).U16((ushort)Math.Min(mismatches.Count, MaxEntries));
		foreach (var m in mismatches.Take(MaxEntries))
		{
			var types = m.Types ?? Array.Empty<(uint, uint)>();
			int n = Math.Min(types.Length, MaxTypes);
			w.U32(m.NetId).U8((byte)m.Kind).U32(m.Children).U32(m.Hash)
				.U64((ulong)BitConverter.DoubleToInt64Bits(m.X)).U64((ulong)BitConverter.DoubleToInt64Bits(m.Y)).U64((ulong)BitConverter.DoubleToInt64Bits(m.Z)).U8((byte)n);
			for (int i = 0; i < n; i++) w.U32(types[i].Type).U32(types[i].Sum);
		}
		return w.ToArray();
	}

	public static (uint CheckId, List<DesyncMismatch> Mismatches) Decode(ReadOnlySpan<byte> data)
	{
		var r = new WireReader(data.Slice(1));
		uint checkId = r.U32();
		int count = r.U16();
		if (count > MaxEntries) throw new ArgumentException("Invalid desync report.");
		var list = new List<DesyncMismatch>(count);
		for (int i = 0; i < count; i++)
		{
			uint id = r.U32();
			var kind = (DesyncKind)r.U8();
			uint children = r.U32(), hash = r.U32();
			double x = BitConverter.Int64BitsToDouble((long)r.U64()), y = BitConverter.Int64BitsToDouble((long)r.U64()), z = BitConverter.Int64BitsToDouble((long)r.U64());
			int n = r.U8();
			if (n > MaxTypes || kind is < DesyncKind.Missing or > DesyncKind.Position) throw new ArgumentException("Invalid desync report entry.");
			var types = new (uint, uint)[n];
			for (int t = 0; t < n; t++) types[t] = (r.U32(), r.U32());
			list.Add(new DesyncMismatch(id, kind, children, hash, x, y, z, types));
		}
		return (checkId, list);
	}
}

public static class DesyncDigest
{
	/// <summary>Host-side movement below this between two digests counts as standing still.</summary>
	public const double StillMeters = 0.05;

	/// <summary>A settled root may sit this far from the host's position on the client (the smoother's last sample).</summary>
	public const double PositionToleranceMeters = 1.0;

	// Moving parts, physics, client prediction/input and fast-churning simulation values: they legitimately differ
	// between the host and a client at any moment and are streamed continuously, so a lasting gap in them heals by itself.
	private static readonly HashSet<string> Unhashed = new(StringComparer.Ordinal)
	{
		"WorldTransform", "RelativeTransform", "RigidBodyData", "CharacterPhysicsState", "AdvancedCharacterControlData",
		"SoftSpeedLimitData", "JetpackModeData", "MaxThrustData", "WindGeneratorData", "DampeningData", "VoluntaryThrustData",
		"RelativeDampeningClientData", "GravityEffectData", "CustomGravity", "ThrustStallData", "OverriddenThrustData", "AirData",
		"ControlData", "AngularControlData", "ControlReferenceOrientation", "LookOffsetData", "TargetControlData",
		"ParentData", "EntityParentData"
	};

	public static bool IsHashed(string shortName) => !Unhashed.Contains(shortName);

	public static uint NameHash(string name)
	{
		uint h = 2166136261;
		foreach (char c in name) h = (h ^ c) * 16777619;
		return h == 0 ? 1 : h;
	}

	public static uint Bytes(ReadOnlySpan<byte> bytes)
	{
		ulong h = 14695981039346656037;
		var words = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ulong>(bytes);
		foreach (ulong w in words) h = (h ^ w) * 1099511628211;
		for (int i = words.Length * 8; i < bytes.Length; i++) h = (h ^ bytes[i]) * 1099511628211;
		return Mix((uint)(h ^ (h >> 32)));
	}

	/// <summary>Order-independent: the sum over every type of its mixed per-type total.</summary>
	public static uint Combine(IEnumerable<KeyValuePair<uint, uint>> types)
	{
		uint h = 0;
		foreach (var (type, sum) in types) h += Mix(type ^ Mix(sum + 0x9E3779B9u));
		return h;
	}

	public static uint Mix(uint h)
	{
		h ^= h >> 16; h *= 0x85EBCA6B; h ^= h >> 13; h *= 0xC2B2AE35; h ^= h >> 16;
		return h;
	}

	/// <summary>Client side: what in this check does not match. <paramref name="lookup"/> returns null for a root the client lacks.</summary>
	public static List<DesyncMismatch> Compare(DesyncCheck check, Func<uint, DigestFlags, ClientRoot?> lookup, IEnumerable<uint> clientRootsInRange, uint own)
	{
		var result = new List<DesyncMismatch>();
		var listed = new HashSet<uint>();
		foreach (var d in check.Roots)
		{
			listed.Add(d.NetId);
			if ((d.Flags & (DigestFlags.Young | DigestFlags.Exempt)) != 0 || d.NetId == own) continue;
			var mine = lookup(d.NetId, d.Flags);
			if (mine == null)
			{
				result.Add(new DesyncMismatch(d.NetId, DesyncKind.Missing, 0, 0, 0, 0, 0, Array.Empty<(uint, uint)>()));
				continue;
			}
			if ((d.Flags & DigestFlags.Busy) != 0) continue;
			DesyncKind? kind = null;
			if ((d.Flags & DigestFlags.NoDetail) == 0 && mine.Children != d.Children) kind = DesyncKind.Children;
			else if ((d.Flags & DigestFlags.NoDetail) == 0 && mine.Hash != d.Hash) kind = DesyncKind.State;
			else if ((d.Flags & DigestFlags.NoPosition) == 0 && (!mine.HasPosition || Distance(mine, d) > PositionToleranceMeters)) kind = DesyncKind.Position;
			if (kind is { } k)
			{
				result.Add(new DesyncMismatch(d.NetId, k, mine.Children, mine.Hash, mine.X, mine.Y, mine.Z, mine.Types.Select(kv => (kv.Key, kv.Value)).OrderBy(t => t.Key).ToArray()));
			}
		}
		foreach (uint id in clientRootsInRange)
		{
			if (id >= check.Lo && id <= check.Hi && id != own && !listed.Contains(id))
			{
				result.Add(new DesyncMismatch(id, DesyncKind.Extra, 0, 0, 0, 0, 0, Array.Empty<(uint, uint)>()));
			}
		}
		return result;
	}

	public static double Distance(ClientRoot a, in RootDigest b)
	{
		double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
		return Math.Sqrt(dx * dx + dy * dy + dz * dz);
	}

	/// <summary>Host side: contiguous NetId ranges covering everything, each holding at most <paramref name="perRange"/> roots
	/// and about <paramref name="weightPerRange"/> entities; the first starts at 2, the last ends at uint.MaxValue.</summary>
	public static List<(uint Lo, uint Hi)> Ranges(IReadOnlyList<uint> sortedRoots, Func<uint, int> weight, int perRange, int weightPerRange)
	{
		var ranges = new List<(uint, uint)>();
		uint lo = 2;
		int count = 0, load = 0;
		foreach (uint id in sortedRoots)
		{
			int w = Math.Max(1, weight(id));
			if (count > 0 && (count >= perRange || load + w > weightPerRange))
			{
				ranges.Add((lo, id - 1));
				lo = id;
				count = 0;
				load = 0;
			}
			count++;
			load += w;
		}
		ranges.Add((lo, uint.MaxValue));
		return ranges;
	}
}

/// <summary>Host side, one per player: when a reported mismatch is repaired, and when a repeat is a bug instead.</summary>
public sealed class DesyncPolicy
{
	public enum Verdict
	{
		Ignore,
		Recheck,
		Repair,
		Bug
	}

	private sealed class Track
	{
		public long FirstMs;
		public long RepairedMs;
		public bool Suppressed;
	}

	/// <summary>A mismatch must still be there this long after it was first reported (a second, targeted check).</summary>
	public static long ConfirmMs = 3000;
	public static long RecheckDelayMs = 4000;
	/// <summary>A first report older than this is stale: start over with a new confirmation.</summary>
	public static long StaleMs = 60000;
	/// <summary>After a repair the entity is left alone this long while the fresh copy arrives.</summary>
	public static long CooldownMs = 30000;
	/// <summary>A confirmed mismatch within this long after a repair is a bug: logged, never repaired again.</summary>
	public static long StrikeWindowMs = 600000;
	public static long MinRepairGapMs = 2000;
	public static int RepairsPerMinute = 20;

	private readonly Dictionary<uint, Track> _tracks = new();
	private readonly Queue<long> _recent = new();
	private long _lastRepairMs = long.MinValue / 2;

	public int Suppressed => _tracks.Values.Count(t => t.Suppressed);

	public Verdict Report(uint netId, long now)
	{
		if (!_tracks.TryGetValue(netId, out var t)) _tracks[netId] = t = new Track();
		if (t.Suppressed) return Verdict.Ignore;
		if (t.RepairedMs != 0 && now - t.RepairedMs < CooldownMs) return Verdict.Ignore;
		if (t.FirstMs == 0 || now - t.FirstMs > StaleMs)
		{
			t.FirstMs = now;
			return Verdict.Recheck;
		}
		if (now - t.FirstMs < ConfirmMs) return Verdict.Ignore;
		if (t.RepairedMs != 0 && now - t.RepairedMs < StrikeWindowMs)
		{
			t.Suppressed = true;
			return Verdict.Bug;
		}
		return Verdict.Repair;
	}

	/// <summary>Whether a repair may run now (per-player rate limit); call <see cref="Repaired"/> once it ran.</summary>
	public bool MayRepair(long now)
	{
		while (_recent.Count > 0 && now - _recent.Peek() >= 60000) _recent.Dequeue();
		return now - _lastRepairMs >= MinRepairGapMs && _recent.Count < RepairsPerMinute;
	}

	public void Repaired(uint netId, long now)
	{
		if (!_tracks.TryGetValue(netId, out var t)) _tracks[netId] = t = new Track();
		t.RepairedMs = now;
		t.FirstMs = 0;
		_lastRepairMs = now;
		_recent.Enqueue(now);
	}

	/// <summary>The mismatch went away (or the entity did): forget a pending confirmation.</summary>
	public void Clear(uint netId)
	{
		if (_tracks.TryGetValue(netId, out var t) && !t.Suppressed && t.RepairedMs == 0) _tracks.Remove(netId);
	}

	public bool IsSuppressed(uint netId) => _tracks.TryGetValue(netId, out var t) && t.Suppressed;

	public long RepairedAt(uint netId) => _tracks.TryGetValue(netId, out var t) ? t.RepairedMs : 0;
}

/// <summary>Process-wide counters for the net status line and the F11 page.</summary>
public static class DesyncStats
{
	public static long ChecksSent, RootsChecked, Reports, Mismatches, Rechecks, Repairs, RepairedMissing, RepairedExtra, RepairedState, Bugs, Discarded, ChecksIn, ReportedOut;

	public static void Count(ref long field, long n = 1) => Interlocked.Add(ref field, n);

	public static string DescribeHost() =>
		$"desync: {ChecksSent} checks / {RootsChecked} roots sent, {Reports} reports with {Mismatches} mismatches ({Discarded} no longer true, {Rechecks} rechecked), {Repairs} repairs (missing {RepairedMissing}, extra {RepairedExtra}, state {RepairedState}), {Bugs} repeat mismatches logged as bugs";

	private static double _clientMs, _clientWorstMs;

	public static void NoteClientMs(double ms)
	{
		_clientMs += ms;
		if (ms > _clientWorstMs) _clientWorstMs = ms;
	}

	public static string DescribeClient()
	{
		string line = $"desync: {ChecksIn} host checks applied ({(ChecksIn == 0 ? 0 : _clientMs / ChecksIn):0.00} ms each, worst {_clientWorstMs:0.00} ms), {ReportedOut} mismatches reported";
		_clientWorstMs = 0;
		return line;
	}
}
