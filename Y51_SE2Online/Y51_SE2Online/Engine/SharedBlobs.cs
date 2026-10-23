using System;
using System.Collections.Generic;
using HarmonyLib;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.Multiplayer;
using Keen.VRage.Multiplayer.Replications.DataOffloading;
using Keen.VRage.Multiplayer.Transport;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.Engine;

/// <summary>
/// Offloaded blobs (large world data sent separately) are scheduled for a player when an archive naming them is
/// serialized for that player. An archive serialized once and sent to several players (Y51PeerTransport shared signals,
/// Y51ServerTransport shared batches) records the blobs its serialization indexed, and each further player gets them
/// scheduled the same way, through the game's own indexer for that player.
/// </summary>
internal static class SharedBlobs
{
	[ThreadStatic] private static List<(ReplicatedDataOffloadingServerSessionComponent Server, Blob Blob)>? _recording;

	public static void Install(Harmony harmony)
	{
		var context = AccessTools.Inner(typeof(ReplicatedDataOffloadingServerSessionComponent), "ServerBlobReplicationContext");
		harmony.Patch(AccessTools.Method(context, "Index"), postfix: new HarmonyMethod(typeof(SharedBlobs), nameof(IndexPostfix)));
	}

	private static void IndexPostfix(ReplicatedDataOffloadingServerSessionComponent.ServerBlobReplicationContext __instance, Blob blob)
	{
		_recording?.Add((__instance._server, blob));
	}

	/// <summary>Starts recording the blobs this thread's serializations index; hand the result to <see cref="End"/>.</summary>
	public static object? Begin()
	{
		var previous = _recording;
		_recording = new();
		return previous;
	}

	/// <summary>Stops recording; the blobs indexed since <see cref="Begin"/>, or null when none were.</summary>
	public static List<(ReplicatedDataOffloadingServerSessionComponent, Blob)>? End(object? previous)
	{
		var recorded = _recording;
		_recording = (List<(ReplicatedDataOffloadingServerSessionComponent, Blob)>?)previous;
		return recorded is { Count: > 0 } ? recorded : null;
	}

	/// <summary>Schedules recorded blobs for another player exactly as serializing for that player would have.</summary>
	public static void Replay(List<(ReplicatedDataOffloadingServerSessionComponent Server, Blob Blob)>? blobs, ClientId client)
	{
		if (blobs == null) return;
		foreach (var (server, blob) in blobs)
		{
			try { new ReplicatedDataOffloadingServerSessionComponent.ServerBlobReplicationContext(server, new ReplicationTargetContext(client)).Index(blob); }
			catch (Exception ex) { GameLog.Instance.WarnOnce("shared-blob", $"engine: scheduling a blob for ClientId {client.Id} failed ({ex.GetType().Name}: {ex.Message})"); }
		}
	}
}
