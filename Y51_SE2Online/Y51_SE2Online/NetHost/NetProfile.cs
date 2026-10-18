using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Y51_SE2Online.Core;
using Y51_SE2Online.Net;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.NetHost;

/// <summary>
/// Host network load for the profiler: per-player send rates and queues, totals and the busiest message types, as a
/// 30 s status line and (when the profile is recording) a JSONL sample every second. Runs on the net thread only.
/// </summary>
internal sealed class NetProfile
{
	private sealed class Mark
	{
		public long AtMs;
		public (long[] Count, long[] Bytes) Sent, Received;
		public readonly Dictionary<PeerId, (long Out, long In)> Peers = new();
		public double LoopSumMs, LoopMaxMs, HostSumMs, HostMaxMs;
		public int Loops;
	}

	private readonly Mark _second = new(), _status = new();

	public void NoteLoop(double loopMs, double hostTickMs)
	{
		foreach (var m in new[] { _second, _status })
		{
			m.Loops++;
			m.LoopSumMs += loopMs;
			m.LoopMaxMs = Math.Max(m.LoopMaxMs, loopMs);
			m.HostSumMs += hostTickMs;
			m.HostMaxMs = Math.Max(m.HostMaxMs, hostTickMs);
		}
	}

	public void Tick(long now, MpTransport transport, HostSession? host, Func<Func<PeerId, string>> names)
	{
		if (!ServerProfiler.Recording || now - _second.AtMs < 1000) return;
		var sample = Sample(now, transport, host, names(), _second);
		var sb = new StringBuilder(1024);
		sb.Append(FormattableString.Invariant($"{{\"t\":{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()},\"kind\":\"net\",\"seconds\":{sample.Seconds:0.000},\"players\":{sample.Peers.Count},\"outBps\":{sample.OutBytes / sample.Seconds:0},\"inBps\":{sample.InBytes / sample.Seconds:0},\"outMsgs\":{sample.OutMessages / sample.Seconds:0},\"inMsgs\":{sample.InMessages / sample.Seconds:0},\"loopAvg\":{sample.LoopAvgMs:0.00},\"loopMax\":{sample.LoopMaxMs:0.0},\"hostTickAvg\":{sample.HostAvgMs:0.00},\"hostTickMax\":{sample.HostMaxMs:0.0}"));
		sb.Append(",\"types\":[").Append(string.Join(",", sample.Top.Select(t => FormattableString.Invariant($"[{ServerProfiler.Quote(TypeName(t.Type))},{t.Count / sample.Seconds:0.0},{t.Bytes / sample.Seconds:0}]")))).Append(']');
		sb.Append(",\"peers\":[").Append(string.Join(",", sample.Peers.Select(p => FormattableString.Invariant($"[{ServerProfiler.Quote(p.Name)},{p.OutBps:0},{p.InBps:0},{p.BulkQueued},{p.LinkPending},{p.StateDirty},{p.RttMs:0},{p.StateLost}]")))).Append("]}");
		ServerProfiler.Json(sb.ToString());
	}

	/// <summary>The 30 s status line: server frames since the last one, then the network.</summary>
	public string Status(long now, MpTransport transport, HostSession? host, Func<Func<PeerId, string>> names)
	{
		var frames = ServerProfiler.TakeWindow();
		var s = Sample(now, transport, host, names(), _status);
		var sb = new StringBuilder(1024);
		sb.Append("server frames: ").Append(frames.Describe());
		sb.Append(FormattableString.Invariant($"; network over {s.Seconds:0} s: {s.Peers.Count} players, out {Rate(s.OutBytes / s.Seconds)} ({s.OutMessages / s.Seconds:0} msgs/s), in {Rate(s.InBytes / s.Seconds)} ({s.InMessages / s.Seconds:0} msgs/s)"));
		if (s.Peers.Count > 0)
		{
			var outs = s.Peers.Select(p => p.OutBps).OrderBy(v => v).ToArray();
			sb.Append(FormattableString.Invariant($", per player out min {Rate(outs[0])} median {Rate(outs[outs.Length / 2])} max {Rate(outs[^1])}"));
			sb.Append("; deepest queues: ").Append(string.Join(", ", s.Peers.OrderByDescending(p => p.BulkQueued + p.LinkPending + p.StateDirty * 64L).Take(3)
				.Select(p => FormattableString.Invariant($"{p.Name} bulk {MpLogExtensions.Bytes(p.BulkQueued)} link {MpLogExtensions.Bytes(p.LinkPending)} state {p.StateDirty} waiting, rtt {p.RttMs:0} ms"))));
		}
		sb.Append("; top sent: ").Append(s.Top.Count == 0 ? "none" : string.Join(", ", s.Top.Take(6).Select(t => FormattableString.Invariant($"{TypeName(t.Type)} {t.Count / s.Seconds:0}/s {Rate(t.Bytes / s.Seconds)}"))));
		sb.Append(FormattableString.Invariant($"; net loop avg {s.LoopAvgMs:0.00} max {s.LoopMaxMs:0} ms, host tick avg {s.HostAvgMs:0.00} max {s.HostMaxMs:0} ms"));
		if (ServerProfiler.DroppedLines > 0) sb.Append($"; profile lines dropped {ServerProfiler.DroppedLines}");
		return sb.ToString();
	}

	private sealed record PeerSample(string Name, double OutBps, double InBps, long BulkQueued, int LinkPending, int StateDirty, double RttMs, long StateLost);

	private sealed record NetSample(double Seconds, long OutBytes, long InBytes, long OutMessages, long InMessages, List<(byte Type, long Count, long Bytes)> Top, List<PeerSample> Peers, double LoopAvgMs, double LoopMaxMs, double HostAvgMs, double HostMaxMs);

	private static NetSample Sample(long now, MpTransport transport, HostSession? host, Func<PeerId, string> name, Mark mark)
	{
		double seconds = mark.AtMs == 0 ? 1 : Math.Max(0.001, (now - mark.AtMs) / 1000.0);
		var sent = transport.Sent.Snapshot();
		var received = transport.Received.Snapshot();
		var top = MessageTally.Top(mark.Sent, sent, 10);
		long outMsgs = 0, inMsgs = 0;
		for (int i = 0; i < 256; i++)
		{
			outMsgs += sent.Count[i] - (mark.Sent.Count?[i] ?? 0);
			inMsgs += received.Count[i] - (mark.Received.Count?[i] ?? 0);
		}
		var peers = new List<PeerSample>();
		var seen = new Dictionary<PeerId, (long, long)>();
		long outBytes = 0, inBytes = 0;
		foreach (PeerId peer in transport.Peers)
		{
			if (!transport.TryGetStats(peer, out PeerStats st)) continue;
			long o = 0, n = 0;
			foreach (var c in st.Channels) { o += c.BytesSent; n += c.BytesReceived; }
			seen[peer] = (o, n);
			var before = mark.Peers.TryGetValue(peer, out var b) ? b : (0L, 0L);
			outBytes += o - before.Item1;
			inBytes += n - before.Item2;
			var backlog = host?.StateBacklog(peer);
			peers.Add(new PeerSample(name(peer), (o - before.Item1) / seconds, (n - before.Item2) / seconds, st.BulkQueuedBytes, st.LinkPendingBytes, backlog?.Dirty ?? 0, st.LastRttMs, st.StateLost));
		}
		var result = new NetSample(seconds, outBytes, inBytes, outMsgs, inMsgs, top, peers,
			mark.Loops == 0 ? 0 : mark.LoopSumMs / mark.Loops, mark.LoopMaxMs, mark.Loops == 0 ? 0 : mark.HostSumMs / mark.Loops, mark.HostMaxMs);
		mark.AtMs = now;
		mark.Sent = sent;
		mark.Received = received;
		mark.Peers.Clear();
		foreach (var kv in seen) mark.Peers[kv.Key] = kv.Value;
		mark.Loops = 0;
		mark.LoopSumMs = mark.LoopMaxMs = mark.HostSumMs = mark.HostMaxMs = 0;
		return result;
	}

	private static string TypeName(byte type) => Enum.IsDefined(typeof(SyncMsg), type) ? ((SyncMsg)type).ToString() : "type " + type.ToString(CultureInfo.InvariantCulture);

	private static string Rate(double bytesPerSec) => MpLogExtensions.Bytes((long)bytesPerSec) + "/s";
}
