using System;
using System.Collections.Generic;
using Keen.VRage.Library.Diagnostics;
using Y51_SE2Online.Core;
using Y51_SE2Online.NetHost;
using Y51_SE2Online.Net;

namespace Y51_SE2Online.SyncEngine;

/// <summary>Game update hook; also publishes multiplayer diagnostics as the "Y51_SE2Online" page of the game's F11 stats.</summary>
internal static class StatusOverlay
{
	private static long _nextMs;
	private static bool _failed;
	private static StatKey _root, _mode, _players, _rtt, _avgRtt, _maxRtt, _sent, _received, _queued, _lost, _gaps, _errors, _peers;
	private static readonly Dictionary<string, StatKey> PeerRtt = new();
	private static StatKey _desyncRepairs, _desyncMismatches;
	private static StatKey _frames, _frameP50, _frameP95, _frameMax, _frameGame, _framePlugin, _frameGc, _worstQueue;

	public static void GameUpdatePrefix(Keen.Game2.GameAppComponent __instance)
	{
		try
		{
			MpPlugin.Tick(__instance);
			if (Y51_SE2Online.Dedicated.DedicatedRuntime.Headless) return;
			MpMenu.Tick(__instance);
			UpdateStats(MpClock.NowMs());
		}
		catch (Exception ex)
		{
			GameLog.Instance.WarnOnce("status-overlay", $"ui: multiplayer update failed ({ex.GetType().Name}: {ex.Message})");
		}
	}

	private static void UpdateStats(long now)
	{
		if (_failed || now < _nextMs) return;
		_nextMs = now + 500;
		try
		{
			if (_root.Id == 0)
			{
				_root = Stats.RegisterStat("Space Engineers 2 Online " + MpPlugin.Version, StatKind.None);
				_mode = _root.RegisterChild("Session (0 offline, 1 hosting, 2 dedicated, 3 joined, 4 joining)", StatKind.Raw, "0");
				_players = _root.RegisterChild("Players connected", StatKind.Raw, "0");
				_rtt = _root.RegisterChild("Ping (ms, worst link)", StatKind.Raw, "0");
				_avgRtt = _root.RegisterChild("Ping average (ms)", StatKind.Raw, "0");
				_maxRtt = _root.RegisterChild("Ping max (ms)", StatKind.Raw, "0");
				_sent = _root.RegisterChild("Sent", StatKind.Binary, "0.0", "B");
				_received = _root.RegisterChild("Received", StatKind.Binary, "0.0", "B");
				_queued = _root.RegisterChild("Bulk queued", StatKind.Binary, "0.0", "B");
				_lost = _root.RegisterChild("State packets lost", StatKind.Raw, "0");
				_gaps = _root.RegisterChild("Sequence gaps", StatKind.Raw, "0");
				_errors = _root.RegisterChild("Protocol errors", StatKind.Raw, "0");
				_desyncRepairs = _root.RegisterChild("Stale replication entries and signals skipped", StatKind.Raw, "0");
				_desyncMismatches = _root.RegisterChild("Access refused (server)", StatKind.Raw, "0");
				_peers = _root.RegisterChild("Ping per player (ms)", StatKind.None);
				_frames = _root.RegisterChild("Server frames (host, last second)", StatKind.None);
				_frameP50 = _frames.RegisterChild("Frame p50 (ms)", StatKind.Raw, "0.0");
				_frameP95 = _frames.RegisterChild("Frame p95 (ms)", StatKind.Raw, "0.0");
				_frameMax = _frames.RegisterChild("Frame max (ms)", StatKind.Raw, "0");
				_frameGame = _frames.RegisterChild("Game per frame (ms)", StatKind.Raw, "0.00");
				_framePlugin = _frames.RegisterChild("Plugin per frame (ms)", StatKind.Raw, "0.00");
				_frameGc = _frames.RegisterChild("GC pause (ms)", StatKind.Raw, "0");
				_worstQueue = _frames.RegisterChild("Deepest player send queue", StatKind.Binary, "0.0", "B");
			}
			var net = NetSession.Current;
			var controller = MpPlugin.Controller?.Current;
			_mode.Set(controller == null || net == null ? 0 : controller.Host ? (controller.Dedicated ? 2 : 1) : AdminAccess.Joined ? 3 : 4);
			double worstQueue = 0;
			double rtt = 0, avg = 0, max = 0, sent = 0, received = 0, queued = 0, lost = 0, gaps = 0, errors = 0;
			int count = 0;
			foreach (var (name, stats) in net?.LinkStats() ?? new List<(string Name, PeerStats Stats)>())
			{
				count++;
				rtt = Math.Max(rtt, stats.LastRttMs); avg = Math.Max(avg, stats.AvgRttMs); max = Math.Max(max, stats.MaxRttMs);
				foreach (var channel in stats.Channels) { sent += channel.BytesSent; received += channel.BytesReceived; }
				worstQueue = Math.Max(worstQueue, stats.BulkQueuedBytes + stats.LinkPendingBytes);
				queued += stats.BulkQueuedBytes; lost += stats.StateLost; gaps += stats.SeqGaps; errors += stats.ProtocolErrors;
				string key = string.IsNullOrWhiteSpace(name) ? stats.Peer.ToString() : name;
				if (!PeerRtt.TryGetValue(key, out var stat) && PeerRtt.Count < OnlineSettings.PlayerLimit) PeerRtt[key] = stat = _peers.RegisterChild(key, StatKind.Raw, "0");
				if (stat.Id != 0) stat.Set((float)stats.LastRttMs);
			}
			_players.Set(count);
			_rtt.Set((float)rtt); _avgRtt.Set((float)avg); _maxRtt.Set((float)max);
			_sent.Set((float)sent); _received.Set((float)received); _queued.Set((float)queued);
			_lost.Set((float)lost); _gaps.Set((float)gaps); _errors.Set((float)errors);
			_desyncRepairs.Set(KeenNetcode.ResilientReplication.Dropped + KeenNetcode.ResilientReplication.SignalsSkipped); _desyncMismatches.Set(KeenNetcode.ServerAccess.Refused);
			_worstQueue.Set((float)worstQueue);
			if (ServerProfiler.LastSecond is { Frames: > 0 } second)
			{
				_frameP50.Set((float)second.Percentile(0.5)); _frameP95.Set((float)second.Percentile(0.95)); _frameMax.Set((float)second.Percentile(1));
				_frameGame.Set((float)(second.GameSumMs / second.Frames)); _framePlugin.Set((float)(second.PluginMs / second.Frames)); _frameGc.Set((float)second.GcPauseMs);
			}
		}
		catch (Exception ex)
		{
			_failed = true;
			GameLog.Instance.Warn($"ui: the F11 multiplayer stats page is unavailable ({ex.GetType().Name}: {ex.Message})");
		}
	}
}
