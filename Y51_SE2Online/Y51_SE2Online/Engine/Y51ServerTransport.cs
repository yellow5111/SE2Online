using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Keen.VRage.Core.Services.Data;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Utils;
using Keen.VRage.Library.Threading;
using Keen.VRage.Multiplayer;
using Keen.VRage.Multiplayer.NetworkStories;
using Keen.VRage.Multiplayer.Transport;
using Keen.VRage.Multiplayer.Transport.OutOfProcess;
using Keen.VRage.Multiplayer.Transport.OutOfProcess.Server;
using Y51_SE2Online.Core;
using Y51_SE2Online.Net;
using Y51_SE2Online.Sync;
using KTask = Keen.VRage.Library.Threading.Task;
using OopTransport = Keen.VRage.Multiplayer.Transport.OutOfProcess.OutOfProcessPeerTransport;

namespace Y51_SE2Online.Engine;

/// <summary>One remote player on the server: replaces Keen's OutOfProcessServerPeerTransport for that player.</summary>
[WithLifetimeAdapter(typeof(DefaultLifetimeAdapter))]
internal sealed class Y51ServerTransport : Y51PeerTransport, IServerPeerTransport
{
	private readonly OutOfProcessServerPeerTransportSessionComponent _server;
	private readonly OutOfProcessReplicationTracker _tracker;
	private readonly Dictionary<ulong, Keen.VRage.Library.Threading.TaskCompletionSource> _batches = new();
	private readonly Func<byte[], bool, bool> _send;

	public readonly GameServiceUser User;
	public readonly PeerId Peer;

	public long BatchesOut, BatchesAcked, DestroysOut, SendFailures;

	public Y51ServerTransport(OutOfProcessServerPeerTransportSessionComponent server, GameServiceUser user, ClientId clientId, PeerId peer, Func<byte[], bool, bool> send)
		: base(server, clientId, $"server->{peer} (ClientId {clientId.Id})")
	{
		_server = server;
		_tracker = new OutOfProcessReplicationTracker(server);
		Context = server.NetworkIdMapper;
		User = user;
		Peer = peer;
		_send = send;
	}

	protected override SyncMsg UpstreamKind => SyncMsg.EngineFrame;

	protected override void Send(byte[] message, bool state)
	{
		if (!_send(message, state)) SendFailures++;
	}

	public IReplicationSerializer ReplicationSerializer => _server.ReplicationSerializer;

	public KTask ReplicateObjectBatchAsync(ISerializedReplicationBatch batch)
	{
		if (batch is not OopTransport.SerializedReplicationObjectBatch serialized)
		{
			GameLog.Instance.Error($"engine {Name}: object batch {batch?.GetType().Name} is not the game's network form; not sent");
			return default;
		}
		var tcs = new Keen.VRage.Library.Threading.TaskCompletionSource($"Y51 object batch #{serialized.BatchId} to {Peer}");
		_batches[serialized.BatchId] = tcs;
		_batchRoots[serialized.BatchId] = (System.Linq.Enumerable.ToArray(System.Linq.Enumerable.OfType<Entity>(serialized.Objects.ToArray())), MpClock.NowMs());
		EnqueueReplicationPacket(serialized.ReplicateBatchData);
		EngineCoverage.Note("object batch", serialized.Type.Name, "server->client", serialized.ReplicateBatchData.Length);
		BatchesOut++;
		serialized.TrackOperation(_tracker, tcs.Task);
		// Values no object builder carries (a door's state, a battery's slot fill) go right behind the batch: the player's game
		// holds signals for entities it is still building and applies them as each one enters its scene.
		var roots = _batchRoots[serialized.BatchId].Roots;
		if (roots.Length > 0) RefreshSyncProperties(roots, 0, notSavedOnly: true);
		return tcs.Task;
	}

	public KTask DestroyObjectBatchAsync<TObject>(ReadOnlySpan<TObject> instances, bool flushQueues = false) where TObject : class
	{
		ulong batch = _server.NextReplicationBatchId();
		var ids = new NetworkId[instances.Length];
		for (int i = 0; i < instances.Length; i++)
		{
			ids[i] = _server.NetworkIdMapper.TryGetId(instances[i]).GetValueOrDefault();
			ForgetSent(instances[i]);
		}
		byte[] data = OopTransport.SerializeForNetwork(BuildContexts(), new OopTransport.DestroyOperationData<TObject>(batch, ids));
		var tcs = new Keen.VRage.Library.Threading.TaskCompletionSource($"Y51 destroy #{batch} to {Peer}");
		_batches[batch] = tcs;
		EnqueueReplicationPacket(data);
		DestroysOut++;
		_tracker.StopReplicating(instances, tcs.Task);
		return tcs.Task;
	}

	public bool IsReplicated(object instance) => _tracker.IsReplicated(instance) && !_tracker.IsPendingRemoval(instance);

	public void FlushReplicationQueues() { }

	void IServerPeerTransport.BuildContexts(ref ServiceLocatorContext.Builder services) => BuildThisPeerContext(ref services);

	protected override void AfterDispatch() => _tracker.ProcessRemoved();

	private readonly Dictionary<ulong, (Entity[] Roots, long AtMs)> _batchRoots = new();

	public override void Dispose()
	{
		base.Dispose();
		foreach (var tcs in _batches.Values) tcs.Dispose();
		_batches.Clear();
		_tracker.Dispose();
	}

	protected override void HandleReplication(object message)
	{
		if (message is OopTransport.BatchAck ack)
		{
			if (_batches.Remove(ack.BatchNumber, out var tcs))
			{
				BatchesAcked++;
				// The player's game has these entities now: every sync property goes to it at the server's current value.
				if (_batchRoots.Remove(ack.BatchNumber, out var batch) && batch.Roots.Length > 0) RefreshSyncProperties(batch.Roots, batch.AtMs);
				tcs.SetResult();
				tcs.Dispose();
			}
			return;
		}
		GameLog.Instance.WarnOnce("engine-server-msg-" + message.GetType().Name, $"engine {Name}: a player sent {message.GetType().Name}, which only the server sends; ignored");
	}

	private readonly Y51Replication.WriteCache _writes = new();

	protected override bool AcceptData(int typeId, Entity target, bool removal) => Y51Replication.ClientMayWrite(PeerClientId, typeId, target, _writes, FramesIn);

	protected override IAsyncStateMachine RestoreStoryStateMachine(ClientId sender, StoryId storyId, StateMachineEnvelope envelope)
	{
		var plan = OopTransport.PlanFor(envelope);
		var machine = plan.Restore(envelope, storyId, _server.ReplicationComponent);
		plan.CompleteServerRequest(machine, ReplicationEventContext.ForRemoteClient(Session, sender));
		KeenNetcode.ServerAccess.CheckRestoredStory(sender, ref machine);
		return machine;
	}
}
