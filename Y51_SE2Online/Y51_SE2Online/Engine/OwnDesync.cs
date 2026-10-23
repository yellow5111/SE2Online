using System;
using System.Collections.Generic;
using System.Linq;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.DCS.Components;
using Keen.VRage.Multiplayer.Transport;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.Engine;

/// <summary>
/// SE2 Online's desync checks. Every few seconds the server sends each player, at the end of an engine frame, the entities
/// of one id range its replication has for that player: confirmed ones must exist on the player's side, ones still on
/// their way (or their way out) may or may not, and any other id there that the player's side holds is extra. Still
/// top-level entities carry their position. Frames are reliable and in order, so the player compares against exactly
/// what the server had sent when it wrote the check. A mismatch is checked again a few seconds later and logged only
/// when it is still there (DesyncPolicy).
/// </summary>
internal static class OwnDesync
{
	public const long IntervalMs = 5000;

	/// <summary>The server's half, one per player.</summary>
	internal sealed class Server
	{
		private DesyncPolicy _policy = new();
		private readonly Dictionary<uint, (double X, double Y, double Z)> _last = new();
		// Ranges with a mismatch to check again (each once), and the checks sent and not answered yet.
		private readonly List<(long DueMs, uint Lo, uint Hi)> _recheck = new();
		private readonly Dictionary<uint, (uint Lo, uint Hi)> _outstanding = new();
		private readonly List<(uint Id, Entity Entity, bool Young)> _window = new();
		private long _nextMs = MpClock.NowMs() + 3 * IntervalMs;
		private uint _checkId, _cursor = 1, _span = 1024;
		private bool _recheckTurn;
		private int _logged;

		public static bool Enabled => SyncEngine.HostWorld.Current?.DesyncChecks ?? true;

		public bool Due(long now) => now >= _nextMs;

		/// <summary>
		/// Scene thread, before a frame goes out: a check to append to it, or null. One pass over what the player's tracker
		/// holds picks the next id window (sized to about <see cref="DesyncCheckMessage.MaxRoots"/> entities); every other
		/// check is a recheck of a range that had a mismatch, when one is due.
		/// </summary>
		public byte[]? Compose(OwnTracker tracker, OwnIds ids, HashSet<Entity> exempt, long now)
		{
			if (!Enabled || now < _nextMs) return null;
			_nextMs = now + IntervalMs;
			uint lo, hi;
			int due = (_recheckTurn = !_recheckTurn) ? _recheck.FindIndex(r => r.DueMs <= now) : -1;
			if (due >= 0)
			{
				(_, lo, hi) = _recheck[due];
				_recheck.RemoveAt(due);
				DesyncStats.Count(ref DesyncStats.Rechecks);
			}
			else
			{
				lo = _cursor;
				hi = (uint)Math.Min(uint.MaxValue, (ulong)lo + _span - 1);
			}
			_window.Clear();
			ulong highest = 0;
			foreach (var (key, instance) in tracker._remoteToLocalPairing)
			{
				if (instance is not Entity entity || key.Value is 0 or > uint.MaxValue) continue;
				highest = Math.Max(highest, key.Value);
				bool young = tracker._pendingObjects.ContainsKey(entity);
				// The tracker keeps the id it paired first; an entity that left and came back before its removal was
				// confirmed has a new one in the shared table, and the player may hold either.
				if (ids.TryGetId(entity) is { } current && current.Value != key.Value)
				{
					young = true;
					if (current.Value >= lo && current.Value <= hi) _window.Add(((uint)current.Value, entity, true));
				}
				if (key.Value >= lo && key.Value <= hi) _window.Add(((uint)key.Value, entity, young));
			}
			if (due < 0)
			{
				// Size the next window from this one; start over past the highest id.
				if (_window.Count > DesyncCheckMessage.MaxRoots) _span = Math.Max(16, _span / 2);
				else if (_window.Count < DesyncCheckMessage.MaxRoots / 4 && _span < 1u << 30) _span *= 2;
			}
			_window.Sort((a, b) => a.Id.CompareTo(b.Id));
			var check = new DesyncCheck { CheckId = ++_checkId, Lo = lo, Hi = hi };
			uint previous = 0;
			foreach (var (id, entity, young) in _window)
			{
				if (id == previous)
				{
					if (young) check.Roots[^1] = new RootDigest(id, DigestFlags.NoDetail | DigestFlags.Young, 0, 0, 0, 0, 0);
					continue;
				}
				if (check.Roots.Count == DesyncCheckMessage.MaxRoots) { check.Hi = id - 1; break; }
				check.Roots.Add(Digest(id, entity, young, exempt));
				previous = id;
			}
			if (due < 0) _cursor = check.Hi >= highest || check.Hi == uint.MaxValue ? 1 : check.Hi + 1;
			if (_outstanding.Count >= 8) _outstanding.Clear();
			_outstanding[check.CheckId] = (check.Lo, check.Hi);
			DesyncStats.Count(ref DesyncStats.ChecksSent);
			DesyncStats.Count(ref DesyncStats.RootsChecked, check.Roots.Count);
			return DesyncCheckMessage.Encode(check);
		}

		private RootDigest Digest(uint id, Entity entity, bool pending, HashSet<Entity> exempt)
		{
			var flags = DigestFlags.NoDetail;
			if (pending || entity.Scene == null) return new RootDigest(id, flags | DigestFlags.Young, 0, 0, 0, 0, 0);
			if (exempt.Contains(entity) || exempt.Contains(entity.GetTopLevelParent())) return new RootDigest(id, flags | DigestFlags.Exempt, 0, 0, 0, 0, 0);
			if (!ReferenceEquals(entity.GetTopLevelParent(), entity) || !entity.Data.TryGet<WorldTransform>(out var at))
				return new RootDigest(id, flags | DigestFlags.NoPosition, 0, 0, 0, 0, 0);
			var p = at.Position;
			bool moved = !_last.TryGetValue(id, out var was) || Math.Abs(was.X - p.X) >= DesyncDigest.StillMeters || Math.Abs(was.Y - p.Y) >= DesyncDigest.StillMeters || Math.Abs(was.Z - p.Z) >= DesyncDigest.StillMeters;
			if (_last.Count > 100_000) _last.Clear();
			_last[id] = (p.X, p.Y, p.Z);
			return new RootDigest(id, moved ? flags | DigestFlags.Busy : flags, 0, 0, p.X, p.Y, p.Z);
		}

		/// <summary>Scene thread: the player's answer to a check.</summary>
		public void OnReport(byte[] bytes, OwnIds ids, string name, long now)
		{
			var (checkId, mismatches) = DesyncReportMessage.Decode(bytes);
			// Only an answer to a check this server sent and has not had answered, about ids in that check's range.
			if (!_outstanding.Remove(checkId, out var range)) return;
			DesyncStats.Count(ref DesyncStats.Reports);
			DesyncStats.Count(ref DesyncStats.Mismatches, mismatches.Count);
			if (_policy.Tracked > 4096) _policy = new DesyncPolicy();
			bool recheck = false;
			foreach (var m in mismatches)
			{
				if (m.NetId < range.Lo || m.NetId > range.Hi) continue;
				switch (_policy.Report(m.NetId, now))
				{
				case DesyncPolicy.Verdict.Recheck:
					recheck = true;
					break;
				case DesyncPolicy.Verdict.Repair:
				case DesyncPolicy.Verdict.Bug:
					// ponytail: confirmed mismatches are logged and counted, not repaired; a repair needs the game's
					// replication to resend (or the player's side to drop) one entity, which it has no call for.
					DesyncStats.Count(ref DesyncStats.Bugs);
					_policy.Repaired(m.NetId, now);
					if (_logged++ < 50)
						GameLog.Instance.Warn($"engine {name}: desync, still there after a recheck: entity {m.NetId} is {What(m)} on the player's side ({Describe(ids, m.NetId)}){(_logged == 50 ? "; further ones only counted" : "")}");
					break;
				}
			}
			if (recheck && _recheck.Count < 16 && !_recheck.Any(r => r.Lo == range.Lo && r.Hi == range.Hi)) _recheck.Add((now + DesyncPolicy.RecheckDelayMs, range.Lo, range.Hi));
		}

		private static string What(in DesyncMismatch m) => m.Kind switch
		{
			DesyncKind.Missing => "missing",
			DesyncKind.Extra => "extra (the server does not have it there)",
			DesyncKind.Position => $"at another place ({m.X:0.0}, {m.Y:0.0}, {m.Z:0.0})",
			_ => m.Kind.ToString(),
		};

		private static string Describe(OwnIds ids, uint id)
		{
			if (ids.TryGetObject(new NetworkId(id)) is not Entity entity) return "no longer on the server";
			try
			{
				string text = entity.DEntity + " [" + string.Join(",", entity.Components.IsDefaultOrEmpty ? Enumerable.Empty<string>() : entity.Components.Take(6).Select(c => c.GetType().Name)) + "]";
				return entity.Data.TryGet<WorldTransform>(out var at) ? $"{text} at ({at.Position.X:0.0}, {at.Position.Y:0.0}, {at.Position.Z:0.0}) on the server" : text;
			}
			catch (Exception ex) { return "not describable (" + ex.GetType().Name + ")"; }
		}
	}

	/// <summary>A player's game: compares a check with what it holds; the report to send back, or null when all matches.</summary>
	public static byte[]? Answer(byte[] bytes, OwnIds ids)
	{
		long started = System.Diagnostics.Stopwatch.GetTimestamp();
		var check = DesyncCheckMessage.Decode(bytes);
		var inRange = new List<uint>();
		foreach (var (id, instance) in ids.Resolved)
			if (id.Value >= check.Lo && id.Value <= check.Hi && instance is Entity { Scene: not null }) inRange.Add((uint)id.Value);
		var mismatches = DesyncDigest.Compare(check, (id, _) => Root(ids, id), inRange, own: 0);
		DesyncStats.Count(ref DesyncStats.ChecksIn);
		DesyncStats.Count(ref DesyncStats.ReportedOut, mismatches.Count);
		DesyncStats.NoteClientMs(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
		return mismatches.Count == 0 ? null : DesyncReportMessage.Encode(check.CheckId, mismatches);
	}

	private static ClientRoot? Root(OwnIds ids, uint id)
	{
		if (ids.TryGetObject(new NetworkId(id)) is not Entity { Scene: not null } entity) return null;
		var root = new ClientRoot();
		if (entity.Data.TryGet<WorldTransform>(out var at)) (root.HasPosition, root.X, root.Y, root.Z) = (true, at.Position.X, at.Position.Y, at.Position.Z);
		return root;
	}
}
