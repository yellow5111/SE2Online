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

	protected override void AfterDispatch()
	{
		_tracker.ProcessRemoved();
		ReleaseLostBatches();
	}

	// The game waits for every player to confirm a staged batch before that spawn completes (world areas, stations at a
	// fast travel destination, encounters, respawn ships): one batch a player never confirms (its game failed to build it,
	// the frame that carried it could not be read) held them for everyone, and fast travel looped forever (F22). A batch
	// counts as lost when newer ones were confirmed after it for LostMs, or after GiveUpMs in any case.
	private const long LostMs = 60_000, GiveUpMs = 300_000;
	private ulong _newestAcked;
	private long _lostCheckMs;
	public long BatchesReleased;

	private void ReleaseLostBatches()
	{
		long now = MpClock.NowMs();
		if (now < _lostCheckMs || _batchRoots.Count == 0) return;
		_lostCheckMs = now + 5000;
		List<ulong>? lost = null;
		foreach (var (id, batch) in _batchRoots)
			// Not before the player confirmed anything: a big world's first batches can take longer than GiveUpMs to build on
			// a slow PC, and releasing them would send updates for objects that game does not have yet.
			if ((now - batch.AtMs > GiveUpMs && _newestAcked > 0) || (now - batch.AtMs > LostMs && _newestAcked > id)) (lost ??= new()).Add(id);
		if (lost == null) return;
		if (_released.Count > 1024) _released.Clear();
		foreach (ulong id in lost)
		{
			// A slow game may still be building it: its confirmation, when it comes, refreshes those objects then.
			if (_batchRoots.Remove(id, out var roots)) _released[id] = roots;
			if (!_batches.Remove(id, out var tcs)) continue;
			BatchesReleased++;
			tcs.SetResult();
			tcs.Dispose();
		}
		GameLog.Instance.Warn($"engine {Name}: the player never confirmed {lost.Count} object batch(es) (oldest #{System.Linq.Enumerable.Min(lost)}, newest confirmed #{_newestAcked}); the server stops waiting for them so spawns and fast travel go on");
	}

	private readonly Dictionary<ulong, (Entity[] Roots, long AtMs)> _batchRoots = new(), _released = new();

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
			if (ack.BatchNumber > _newestAcked) _newestAcked = ack.BatchNumber;
			if (_batches.Remove(ack.BatchNumber, out var tcs))
			{
				BatchesAcked++;
				// The player's game has these entities now: every sync property goes to it at the server's current value.
				if (_batchRoots.Remove(ack.BatchNumber, out var batch) && batch.Roots.Length > 0) RefreshSyncProperties(batch.Roots, batch.AtMs);
				tcs.SetResult();
				tcs.Dispose();
			}
			else if (_released.Remove(ack.BatchNumber, out var late) && late.Roots.Length > 0) RefreshSyncProperties(late.Roots, late.AtMs);
			return;
		}
		GameLog.Instance.WarnOnce("engine-server-msg-" + message.GetType().Name, $"engine {Name}: a player sent {message.GetType().Name}, which only the server sends; ignored");
	}

	private readonly Y51Replication.WriteCache _writes = new();

	protected override bool AcceptData(int typeId, Entity target, bool removal) => Y51Replication.ClientMayWrite(PeerClientId, typeId, target, _writes, FramesIn);

	// A player's request whose arguments did not survive the trip arrives with nulls and faults inside the game's own code;
	// this names the request and the argument, once per kind.
	private static void NoteNullArguments(IAsyncStateMachine machine)
	{
		try
		{
			foreach (var field in ArgumentFields.GetValue(machine.GetType(), t => System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Where(
				t.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public), f => !f.Name.StartsWith('<') && !f.FieldType.IsValueType))))
			{
				object? value = field.GetValue(machine);
				int nullItems = 0;
				if (value is System.Collections.IList list) foreach (object? item in list) if (item == null) nullItems++;
				if (value != null && nullItems == 0) continue;
				string story = machine.GetType().DeclaringType?.Name + "." + machine.GetType().Name;
				GameLog.Instance.WarnOnce("story-null-" + story + field.Name, $"engine: a player's {story} arrived with {(value == null ? "no " + field.Name : nullItems + " empty entries in " + field.Name)} ({field.FieldType.Name})");
			}
		}
		catch (Exception ex) { GameLog.Instance.WarnOnce("story-null-check", "engine: could not check a story's arguments: " + ex.Message); }
	}

	private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Type, System.Reflection.FieldInfo[]> ArgumentFields = new();

	private static int _groupNotes;

	// F16 diagnosis: what RequestCreateNewGroup will find on this server (the first few per run).
	private void NoteGroupRequest(IAsyncStateMachine machine, ClientId sender)
	{
		if (System.Threading.Interlocked.Increment(ref _groupNotes) > 5) return;
		try
		{
			var t = HarmonyLib.Traverse.Create(machine);
			var panel = t.Field("<>4__this").GetValue<Keen.Game2.Simulation.StreamedUI.Terminal.ControlPanel.StreamedControlPanelSessionComponent>();
			var grid = t.Field("gridModel").GetValue<Keen.Game2.Simulation.StreamedUI.Terminal.ControlPanel.ControlPanelGridModel>();
			var context = t.Field("context").GetValue<Keen.Game2.Simulation.StreamedUI.Terminal.ControlPanel.ControlPanelListModel>();
			var blocks = t.Field("blockModels").GetValue<System.Collections.IList>();
			var rec = ReplicationEventContext.ForRemoteClient(Session, sender);
			var entity = HarmonyLib.Traverse.Create(panel).Method("AsEntity", new[] { typeof(Keen.VRage.Multiplayer.SecureIds.EntityId), typeof(object), typeof(ReplicationEventContext) }).GetValue(grid.EntityId, context, rec) as Entity;
			object? groups = entity?.TryGet<Keen.Game2.Simulation.WorldObjects.CubeBlocks.CubeGridGroupsComponent>();
			var config = HarmonyLib.Traverse.Create(panel).Field("_configuration").GetValue();
			GameLog.Instance.Info($"engine: group request: panel {(panel == null ? "null" : "set")}, grid model {(grid == null ? "null" : grid.EntityId.ToString())}, list {(context == null ? "null" : "set")}, {blocks?.Count ?? -1} blocks, grid entity {entity?.ToString() ?? "null"}, groups component {(groups == null ? "null" : "set")}, configuration {(config == null ? "null" : "set")}, text manager {(Keen.VRage.Library.Utils.Singleton<Keen.VRage.Library.Localization.TextManager>.Instance == null ? "null" : "set")}");
		}
		catch (Exception ex) { GameLog.Instance.Info("engine: group request check failed: " + ex); }
	}

	protected override IAsyncStateMachine RestoreStoryStateMachine(ClientId sender, StoryId storyId, StateMachineEnvelope envelope)
	{
		var plan = OopTransport.PlanFor(envelope);
		var machine = plan.Restore(envelope, storyId, _server.ReplicationComponent);
		plan.CompleteServerRequest(machine, ReplicationEventContext.ForRemoteClient(Session, sender));
		NoteNullArguments(machine);
		if (machine.GetType().Name.Contains("RequestCreateNewGroup")) NoteGroupRequest(machine, sender);
		KeenNetcode.ServerAccess.CheckRestoredStory(sender, ref machine);
		return machine;
	}
}
