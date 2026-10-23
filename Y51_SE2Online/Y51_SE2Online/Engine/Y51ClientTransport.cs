using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using Keen.VRage.Library.Threading;
using Keen.VRage.Multiplayer;
using Keen.VRage.Multiplayer.NetworkStories;
using Keen.VRage.Multiplayer.Transport;
using Keen.VRage.Multiplayer.Transport.OutOfProcess;
using Keen.VRage.Multiplayer.Transport.OutOfProcess.Client;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;
using KTask = Keen.VRage.Library.Threading.Task;
using OopTransport = Keen.VRage.Multiplayer.Transport.OutOfProcess.OutOfProcessPeerTransport;

namespace Y51_SE2Online.Engine;

/// <summary>A joined player's side: replaces Keen's OutOfProcessClientPeerTransport.</summary>
[WithLifetimeAdapter(typeof(DefaultLifetimeAdapter))]
internal sealed class Y51ClientTransport : Y51PeerTransport, IClientPeerTransport
{
	private readonly OutOfProcessClientPeerTransportSessionComponent _client;
	private readonly Func<byte[], bool> _send;

	public long Creations, Destructions, Acks, SendFailures;

	public event IClientPeerTransport.ReplicationCallback? AfterObjectsReplicated;

	public event IClientPeerTransport.ReplicationCallback? AfterObjectsDestroyed;

	public Y51ClientTransport(OutOfProcessClientPeerTransportSessionComponent client, Func<byte[], bool> send)
		: base(client, ClientId.Server, "client")
	{
		_client = client;
		_send = send;
		Context = new OutOfProcessClientPeerTransport.ReplicationContext(client);
	}

	protected override SyncMsg UpstreamKind => SyncMsg.EngineUp;

	protected override void Send(byte[] message, bool state)
	{
		// A player's sampled data is rare (the server samples); it rides the reliable lane with everything else.
		if (state) return;
		if (!_send(message)) SendFailures++;
	}

	private static readonly ConcurrentDictionary<Type, MethodInfo> Creators = new(), Destroyers = new();

	private static readonly MethodInfo CreateDef = typeof(Y51ClientTransport).GetMethod(nameof(Create), BindingFlags.Instance | BindingFlags.NonPublic)!;
	private static readonly MethodInfo DestroyDef = typeof(Y51ClientTransport).GetMethod(nameof(Destroy), BindingFlags.Instance | BindingFlags.NonPublic)!;

	protected override void HandleReplication(object message)
	{
		Type type = message.GetType();
		if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(OopTransport.ReplicateOperationData<,>))
		{
			Creators.GetOrAdd(type, t => CreateDef.MakeGenericMethod(t.GetGenericArguments())).Invoke(this, new[] { message });
			return;
		}
		if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(OopTransport.DestroyOperationData<>))
		{
			Destroyers.GetOrAdd(type, t => DestroyDef.MakeGenericMethod(t.GetGenericArguments())).Invoke(this, new[] { message });
			return;
		}
		GameLog.Instance.WarnOnce("engine-client-msg-" + type.Name, $"engine {Name}: the server sent {type.Name}, which this side does not handle; ignored");
	}

	private void Create<TObject, TPayload>(OopTransport.ReplicateOperationData<TObject, TPayload> message) where TObject : class
	{
		var objects = new TObject[message.ObjectsToReplicate.Length];
		for (int i = 0; i < objects.Length; i++) objects[i] = (TObject)message.ObjectsToReplicate[i].Instance;
		Creations++;
		var operation = _client.GetHandler<TObject, TPayload>().InitializeObjectsAsync(objects, message.Payload);
		Confirm(operation, message.BatchId).SkipWait();
	}

	private void Destroy<TObject>(OopTransport.DestroyOperationData<TObject> message) where TObject : class
	{
		var objects = new TObject[message.ObjectsToDestroy.Length];
		for (int i = 0; i < objects.Length; i++)
		{
			objects[i] = _client.NetworkIdMapper.GetObject<TObject>(message.ObjectsToDestroy[i]);
			_client.NetworkIdMapper.Remove(objects[i]);
		}
		Destructions++;
		var operation = _client.GetHandler<TObject>().DestroyObjectsAsync(objects);
		Confirm(operation, message.BatchNumber).SkipWait();
	}

	private async KTask Confirm(KTask operation, ulong batch)
	{
		await KTask.SetLifetime(_client);
		// Confirmed even when building it failed: the server holds staged spawns (world areas, a fast travel destination)
		// until every player confirms, and an unconfirmed batch held them for everyone.
		try { await operation; }
		catch (Exception ex) when (Keen.VRage.Library.Utils.ExceptionUnrollHelper.CanHandle(ex))
		{
			GameLog.Instance.Warn($"engine {Name}: building object batch #{batch} failed ({ex.GetType().Name}: {ex.Message}); confirmed anyway so the server does not wait for it; those objects are missing in this game until it rejoins");
		}
		// The batch's entities are in the scene now: their motion that came while they were built applies in this frame.
		ApplyParked();
		Acks++;
		EnqueueReplicationPacket(OopTransport.SerializeForNetwork(default, new OopTransport.BatchAck(batch)));
	}

	// This player's own predicted character keeps its own sprint/jump/boost flags (KeenNetcode/OwnMotion.cs): the server's
	// copy comes back a round trip late and would undo a key let go of since.
	protected override bool AcceptData(int typeId, Keen.VRage.DCS.Components.Entity target, bool removal)
		=> !(KeenNetcode.OwnMotion.Driving is { } own && ReferenceEquals(own, target) && typeId == Keen.VRage.DCS.Internal.RuntimeDataInfo.Of(typeof(Keen.Game2.Simulation.WorldObjects.Characters.AdvancedCharacterControlData)).TypeId);

	protected override IAsyncStateMachine RestoreStoryStateMachine(ClientId sender, StoryId storyId, StateMachineEnvelope envelope)
		=> OopTransport.PlanFor(envelope).Restore(envelope, storyId, _client.ReplicationComponent);
}
