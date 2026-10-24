using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Keen.VRage.Core.Services.Data;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.UI;
using Keen.VRage.Library.Utils;
using Keen.VRage.Library.Threading;
using Keen.VRage.Multiplayer;
using Keen.VRage.Multiplayer.NetworkStories;
using Keen.VRage.Multiplayer.Replications;
using Keen.VRage.Multiplayer.Transport;
using Y51_SE2Online.Core;
using Y51_SE2Online.Net;
using Y51_SE2Online.Sync;
using KTask = Keen.VRage.Library.Threading.Task;

namespace Y51_SE2Online.Engine;

/// <summary>One remote player on the server: SE2 Online's peer transport for that player in the game's replication.</summary>
[WithLifetimeAdapter(typeof(DefaultLifetimeAdapter))]
internal sealed class Y51ServerTransport : Y51PeerTransport, IServerPeerTransport
{
	private readonly OwnTracker _tracker;
	private readonly Dictionary<ulong, Keen.VRage.Library.Threading.TaskCompletionSource> _batches = new();
	private readonly Func<byte[], bool, bool> _send;
	private readonly Serializer _serializer;

	public readonly GameServiceUser User;
	public readonly PeerId Peer;

	public long BatchesOut, BatchesAcked, DestroysOut, SendFailures;

	public Y51ServerTransport(OwnHost host, GameServiceUser user, ClientId clientId, PeerId peer, Func<byte[], bool, bool> send)
		: base(host, clientId, $"server->{peer} (ClientId {clientId.Id})")
	{
		_tracker = new OwnTracker(host.Replication.Entity.Scene, host);
		_serializer = new Serializer(this);
		User = user;
		Peer = peer;
		_send = send;
	}

	internal OwnTracker Tracker => _tracker;

	protected override SyncMsg UpstreamKind => SyncMsg.EngineFrame;

	protected override void Send(byte[] message, bool state)
	{
		// A peer that connected again gets only its new transport's stream (sequence numbers of its own).
		if (!Y51Replication.IsCurrent(this)) return;
		if (!_send(message, state)) SendFailures++;
	}

	public IReplicationSerializer ReplicationSerializer => _serializer;

	// --- object batches ---

	/// <summary>An object batch as SE2 Online sends it: its objects' ids and kinds, and the payload the game's handler on the
	/// player's side builds them from.</summary>
	private abstract class Batch : ISerializedReplicationBatch
	{
		public readonly ulong Id;
		public readonly byte[] Bytes;
		public readonly Type ObjectType;

		protected Batch(ulong id, byte[] bytes, Type objectType) => (Id, Bytes, ObjectType) = (id, bytes, objectType);

		public abstract object[] Objects { get; }

		public abstract void Track(OwnTracker tracker, KTask task);

		public void Dispose() { }
	}

	private sealed class Batch<TObject>(ulong id, byte[] bytes, TObject[] instances) : Batch(id, bytes, typeof(TObject)) where TObject : class
	{
		public override object[] Objects => instances;

		public override void Track(OwnTracker tracker, KTask task) => tracker.BeginReplicating<TObject>(instances, task);
	}

	private sealed class Serializer(Y51ServerTransport transport) : IReplicationSerializer
	{
		public ISerializedReplicationBatch SerializePayload<TObject, TPayload>(ReadOnlySpan<TObject> instances, TPayload payload, ServiceLocatorContext.Builder services = default) where TObject : class
			=> transport.Serialize(instances, payload);
	}

	private Batch Serialize<TObject, TPayload>(ReadOnlySpan<TObject> instances, TPayload payload) where TObject : class
	{
		if (payload == null) throw new ArgumentNullException(nameof(payload), "an object batch without a payload");
		var inPayload = new HashSet<object>(ReferenceEqualityComparer.Instance);
		var stubs = new List<OwnUnits.Stub>(instances.Length);
		foreach (TObject instance in instances)
		{
			inPayload.Add(instance);
			stubs.Add(StubOf(instance, Host.GetOrRegisterId(instance).Value));
		}
		ulong id = Host.NextBatchId();
		byte[] bytes = OwnUnits.WriteBatch(id, typeof(TObject), typeof(TPayload), stubs, payload, Host.Contexts(TargetContext, new OwnBatchIds(Host.Ids, inPayload)));
		return new Batch<TObject>(id, bytes, instances.ToArray());
	}

	private static OwnUnits.Stub StubOf(object instance, ulong id) => instance switch
	{
		Entity => new OwnUnits.Stub(OwnUnits.StubKind.Entity, id, null),
		ObservableObject observable => new OwnUnits.Stub(OwnUnits.StubKind.Observable, id, OwnUnits.TypeName(
			ObservableObjectReplicationSessionComponent.TryGetTypeAnnotatedWithReplicateAttribute(observable.GetType()) ?? observable.GetType())),
		IObservedCollection => new OwnUnits.Stub(OwnUnits.StubKind.Collection, id, null),
		_ => throw new InvalidOperationException($"{instance.GetType().Name} is no object a player's game can build"),
	};

	/// <summary>
	/// The game hands each player the same payload in turn (a spawn, a staged world area, an observable model); it is
	/// serialized once (ids are server-wide) and served to every player; blobs it names are scheduled for each player as
	/// its own serialization would have (SharedBlobs). The batch is released once every player that used it is done.
	/// </summary>
	// Explicit: the compiler then guarantees this replaces the interface's default (an implicit one with other constraints would not).
	KTask IServerPeerTransport.ReplicateObjectBatchAsync<TObject, TPayload>(ReadOnlySpan<TObject> instances, TPayload initialPayload)
	{
		SharedBatch shared;
		try { shared = SharedBatch.For(this, instances, initialPayload); }
		catch (Exception ex) when (ex.CanHandle()) { return KTask.FromException(ex); }
		return ReplicateShared(shared);
	}

	private async KTask ReplicateShared(SharedBatch shared)
	{
		try { await ReplicateObjectBatchAsync(shared.Batch); }
		finally { shared.Release(); }
	}

	public static long BatchesSerialized, BatchesShared;

	private sealed class SharedBatch
	{
		private static readonly ConditionalWeakTable<object, SharedBatch> ByPayload = new();

		public readonly Batch Batch;
		private readonly int _count;
		private readonly object? _first, _last, _key;
		private readonly List<(Keen.VRage.Multiplayer.Replications.DataOffloading.ReplicatedDataOffloadingServerSessionComponent, Keen.VRage.Core.Game.Data.Blob)>? _blobs;
		private int _users;

		private SharedBatch(Batch batch, int count, object? first, object? last, object? key, List<(Keen.VRage.Multiplayer.Replications.DataOffloading.ReplicatedDataOffloadingServerSessionComponent, Keen.VRage.Core.Game.Data.Blob)>? blobs)
		{
			Batch = batch;
			_count = count;
			(_first, _last, _key, _blobs) = (first, last, key, blobs);
		}

		public static SharedBatch For<TObject, TPayload>(Y51ServerTransport transport, ReadOnlySpan<TObject> instances, TPayload payload) where TObject : class
		{
			// The blob push itself is never shared: it is built per player from what that player still lacks.
			object? key = typeof(TPayload).IsValueType || payload is Keen.VRage.Multiplayer.Replications.DataOffloading.BlobReplicationData ? null : payload;
			object? first = instances.Length > 0 ? instances[0] : null, last = instances.Length > 0 ? instances[^1] : null;
			if (key != null && ByPayload.TryGetValue(key, out var known) && known.Matches(instances.Length, first, last) && known.TryUse())
			{
				// The blobs the batch names, scheduled for this player as its own serialization would have.
				SharedBlobs.Replay(known._blobs, transport.PeerClientId);
				System.Threading.Interlocked.Increment(ref BatchesShared);
				return known;
			}
			object? recording = SharedBlobs.Begin();
			Batch batch;
			List<(Keen.VRage.Multiplayer.Replications.DataOffloading.ReplicatedDataOffloadingServerSessionComponent, Keen.VRage.Core.Game.Data.Blob)>? blobs;
			try { batch = transport.Serialize(instances, payload); }
			finally { blobs = SharedBlobs.End(recording); }
			System.Threading.Interlocked.Increment(ref BatchesSerialized);
			var shared = new SharedBatch(batch, instances.Length, first, last, key, blobs);
			shared.TryUse();
			if (key != null) ByPayload.AddOrUpdate(key, shared);
			return shared;
		}

		private bool Matches(int count, object? first, object? last) => count == _count && ReferenceEquals(first, _first) && ReferenceEquals(last, _last);

		// -1 once released: a player that comes after everyone finished serializes its own.
		private bool TryUse()
		{
			while (true)
			{
				int users = System.Threading.Volatile.Read(ref _users);
				if (users < 0) return false;
				if (System.Threading.Interlocked.CompareExchange(ref _users, users + 1, users) == users) return true;
			}
		}

		public void Release()
		{
			if (System.Threading.Interlocked.Decrement(ref _users) != 0) return;
			if (System.Threading.Interlocked.CompareExchange(ref _users, -1, 0) != 0) return;
			if (_key != null && ByPayload.TryGetValue(_key, out var current) && ReferenceEquals(current, this)) ByPayload.Remove(_key);
			Batch.Dispose();
		}
	}

	public KTask ReplicateObjectBatchAsync(ISerializedReplicationBatch batch)
	{
		if (batch is not Batch own)
		{
			GameLog.Instance.Error($"engine {Name}: object batch {batch?.GetType().Name} is not SE2 Online's; not sent");
			return default;
		}
		var tcs = new Keen.VRage.Library.Threading.TaskCompletionSource($"Y51 object batch #{own.Id} to {Peer}");
		_batches[own.Id] = tcs;
		var roots = own.Objects.OfType<Entity>().ToArray();
		_batchRoots[own.Id] = (roots, MpClock.NowMs());
		EnqueueReplicationPacket(own.Bytes);
		EngineCoverage.Note("object batch", own.ObjectType.Name, "server->client", own.Bytes.Length);
		BatchesOut++;
		own.Track(_tracker, tcs.Task);
		// Values no object builder carries (a door's state, a battery's slot fill) go right behind the batch: the player's game
		// holds signals for entities it is still building and applies them as each one enters its scene.
		if (roots.Length > 0) RefreshSyncProperties(roots, 0, notSavedOnly: true);
		return tcs.Task;
	}

	public KTask DestroyObjectBatchAsync<TObject>(ReadOnlySpan<TObject> instances, bool flushQueues = false) where TObject : class
	{
		ulong batch = Host.NextBatchId();
		var ids = new List<ulong>(instances.Length);
		foreach (TObject instance in instances)
		{
			// An object without an id never went to anyone (and 0 is the session itself: never destroyed).
			if (Host.Ids.TryGetId(instance) is { Value: > 0 } id) ids.Add(id.Value);
			else if (Host.Ids.TryGetRetiredId(instance) is { Value: > 0 } retired) ids.Add(retired.Value);
			ForgetSent(instance);
		}
		var tcs = new Keen.VRage.Library.Threading.TaskCompletionSource($"Y51 destroy #{batch} to {Peer}");
		_batches[batch] = tcs;
		EnqueueReplicationPacket(OwnUnits.WriteDestroy(batch, typeof(TObject), ids));
		DestroysOut++;
		_tracker.StopReplicating(instances, tcs.Task);
		return tcs.Task;
	}

	public bool IsReplicated(object instance) => _tracker.IsReplicated(instance) && !_tracker.IsPendingRemoval(instance);

	public void FlushReplicationQueues() { }

	void IServerPeerTransport.BuildContexts(ref ServiceLocatorContext.Builder services) => services.Add(TargetContext);

	private readonly OwnDesync.Server _desync = new();
	private readonly HashSet<Entity> _ownCharacters = new(ReferenceEqualityComparer.Instance), _ownRoots = new(ReferenceEqualityComparer.Instance);

	protected override void BeforeDispatch()
	{
		long now = MpClock.NowMs();
		if (!OwnDesync.Server.Enabled || !_desync.Due(now) || !Y51Replication.IsCurrent(this)) return;
		Y51Replication.OwnBodies(PeerClientId, _ownCharacters, _ownRoots);
		_ownRoots.UnionWith(_ownCharacters);
		if (_desync.Compose(_tracker, Host.Ids, _ownRoots, now) is { } check) EnqueueDesync(check);
	}

	protected override void OnDesync(byte[] bytes) => _desync.OnReport(bytes, Host.Ids, Name, MpClock.NowMs());

	protected override void AfterDispatch()
	{
		_tracker.ProcessRemoved();
		ReleaseLostBatches();
		Y51Replication.FlushRefused();
	}

	// The game waits for every player to confirm a staged batch before that spawn completes (world areas, stations at a
	// fast travel destination, encounters, respawn ships): one batch a player never confirms (its game failed to build it,
	// the frame that carried it could not be read) held them for everyone, and fast travel looped forever (F22). A batch
	// counts as lost when newer ones were confirmed after it for LostMs, or after GiveUpMs in any case.
	private const long LostMs = 60_000, GiveUpMs = 300_000;
	// A player whose game confirmed nothing for StalledMs (PingPerfect: one game stopped confirming while the others went on)
	// is stalled: its batches older than StalledReleaseMs stop holding everyone's spawns. Its own confirmation, when it
	// comes, still refreshes those objects for it.
	private const long StalledMs = 30_000, StalledReleaseMs = 45_000;
	private long _lastAckMs;
	private bool _stalledLogged, _playing;
	private ulong _newestAcked;
	private long _lostCheckMs;
	public long BatchesReleased;

	private void ReleaseLostBatches()
	{
		long now = MpClock.NowMs();
		if (now < _lostCheckMs || _batchRoots.Count == 0) return;
		_lostCheckMs = now + 5000;
		List<ulong>? lost = null;
		// Not while the player is still joining (a slow first load keeps the 300 s rule), and counted from the later of its
		// last confirmation and the oldest batch it was sent: a player idle with nothing to confirm is not stalled.
		_playing = _playing || SyncEngine.HostWorld.Current?.IsPlaying((long)KeenNetcode.ServerAccess.IdentityOf(PeerClientId)) == true;
		long oldestPending = long.MaxValue;
		foreach (var pending in _batchRoots.Values) oldestPending = Math.Min(oldestPending, pending.AtMs);
		bool stalled = _playing && _newestAcked > 0 && now - Math.Max(_lastAckMs, oldestPending) > StalledMs;
		if (stalled && !_stalledLogged)
		{
			_stalledLogged = true;
			GameLog.Instance.Warn($"engine {Name}: the player has confirmed no object batch for {(now - _lastAckMs) / 1000} s (newest #{_newestAcked}); its batches stop holding spawns for everyone after {StalledReleaseMs / 1000} s");
		}
		foreach (var (id, batch) in _batchRoots)
			if (stalled && now - batch.AtMs > StalledReleaseMs) (lost ??= new()).Add(id);
			else
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
		GameLog.Instance.Warn($"engine {Name}: the player never confirmed {lost.Count} object batch(es) (oldest #{lost.Min()}, newest confirmed #{_newestAcked}); the server stops waiting for them so spawns and fast travel go on");
	}

	private readonly Dictionary<ulong, (Entity[] Roots, long AtMs)> _batchRoots = new(), _released = new();

	public override void Dispose()
	{
		base.Dispose();
		foreach (var tcs in _batches.Values) tcs.Dispose();
		_batches.Clear();
		_tracker.Dispose();
	}

	protected override void HandleReplication(byte[] unit)
	{
		if (OwnUnits.OpOf(unit) != OwnUnits.Op.Ack)
		{
			GameLog.Instance.WarnOnce("engine-server-msg-" + Name, $"engine {Name}: a player sent an object batch or destruction, which only the server sends; ignored");
			return;
		}
		ulong batch = OwnUnits.AckOf(unit);
		if (batch > _newestAcked) _newestAcked = batch;
		_lastAckMs = MpClock.NowMs();
		if (_stalledLogged) { _stalledLogged = false; GameLog.Instance.Info($"engine {Name}: the player confirms object batches again (#{batch})"); }
		if (_batches.Remove(batch, out var tcs))
		{
			BatchesAcked++;
			// The player's game has these entities now: every sync property goes to it at the server's current value.
			if (_batchRoots.Remove(batch, out var sent) && sent.Roots.Length > 0) RefreshSyncProperties(sent.Roots, sent.AtMs);
			tcs.SetResult();
			tcs.Dispose();
		}
		else if (_released.Remove(batch, out var late) && late.Roots.Length > 0) RefreshSyncProperties(late.Roots, late.AtMs);
	}

	private readonly Y51Replication.WriteCache _writes = new();

	protected override bool AcceptData(int typeId, Entity target, bool removal) => Y51Replication.ClientMayWrite(PeerClientId, typeId, target, _writes, FramesIn);

	// Doors, on/off and landing gear need the block's use access (SE2 4.0 checks none of them). The player's game already
	// flipped a refused on/off itself: the block's values go back to it.
	protected override bool AcceptSignal(OwnSignal signal)
	{
		if (signal.Target is not Entity target || !KeenNetcode.ServerAccess.RefusesSignal(PeerClientId, signal.Name, target, out bool predicted)) return true;
		if (predicted)
		{
			long at = MpClock.NowMs();
			SyncProperties.Touch(target);
			RefreshSyncProperties(new[] { target }, at);
		}
		return false;
	}

	// A player's request whose arguments did not survive the trip arrives with nulls and faults inside the game's own code;
	// this names the request and the argument, once per kind.
	private static void NoteNullArguments(IAsyncStateMachine machine)
	{
		try
		{
			foreach (var field in ArgumentFields.GetValue(machine.GetType(), t => t.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
				.Where(f => !f.Name.StartsWith('<') && !f.FieldType.IsValueType).ToArray()))
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

	private static readonly ConditionalWeakTable<Type, System.Reflection.FieldInfo[]> ArgumentFields = new();

	protected override IAsyncStateMachine RestoreStory(ClientId sender, StoryId storyId, OwnStoryMove move)
	{
		var machine = OwnStories.Restore(move, storyId, Host.Replication, server: true);
		OwnStories.CompleteServerRequest(machine, ReplicationEventContext.ForRemoteClient(Session, sender));
		NoteNullArguments(machine);
		KeenNetcode.ServerAccess.CheckRestoredStory(sender, ref machine);
		return machine;
	}
}
