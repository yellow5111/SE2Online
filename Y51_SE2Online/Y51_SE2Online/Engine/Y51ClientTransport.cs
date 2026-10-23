using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Threading;
using Keen.VRage.Library.UI;
using Keen.VRage.Library.Utils;
using Keen.VRage.Multiplayer;
using Keen.VRage.Multiplayer.NetworkStories;
using Keen.VRage.Multiplayer.Replications;
using Keen.VRage.Multiplayer.Transport;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;
using KTask = Keen.VRage.Library.Threading.Task;

namespace Y51_SE2Online.Engine;

/// <summary>A joined player's side: SE2 Online's transport to the server in the game's replication.</summary>
[WithLifetimeAdapter(typeof(DefaultLifetimeAdapter))]
internal sealed class Y51ClientTransport : Y51PeerTransport, IClientPeerTransport
{
	private readonly ReplicationClientSessionComponent _client;
	private readonly Func<byte[], bool> _send;

	public long Creations, Destructions, Acks, SendFailures, BatchesUnreadable;

	public event IClientPeerTransport.ReplicationCallback? AfterObjectsReplicated;

	public event IClientPeerTransport.ReplicationCallback? AfterObjectsDestroyed;

	public Y51ClientTransport(OwnHost host, Func<byte[], bool> send)
		: base(host, ClientId.Server, "client")
	{
		_client = (ReplicationClientSessionComponent)host.Replication;
		_send = send;
	}

	protected override SyncMsg UpstreamKind => SyncMsg.EngineUp;

	protected override void Send(byte[] message, bool state)
	{
		// A player's sampled data is rare (the server samples); it rides the reliable lane with everything else.
		if (state) return;
		if (!_send(message)) SendFailures++;
	}

	private static readonly ConcurrentDictionary<(Type, Type), MethodInfo> Creators = new();
	private static readonly ConcurrentDictionary<Type, MethodInfo> Destroyers = new();

	private static readonly MethodInfo CreateDef = typeof(Y51ClientTransport).GetMethod(nameof(Create), BindingFlags.Instance | BindingFlags.NonPublic)!;
	private static readonly MethodInfo DestroyDef = typeof(Y51ClientTransport).GetMethod(nameof(Destroy), BindingFlags.Instance | BindingFlags.NonPublic)!;

	protected override void HandleReplication(byte[] unit)
	{
		switch (OwnUnits.OpOf(unit))
		{
		case OwnUnits.Op.Batch:
		{
			var header = OwnUnits.ReadBatchHeader(unit);
			var create = Creators.GetOrAdd((header.Object, header.Payload), k => CreateDef.MakeGenericMethod(k.Item1, k.Item2));
			try { create.Invoke(this, new object[] { header.Batch, header.Stubs, unit, header.ArchiveAt }); }
			catch (TargetInvocationException ex) when (ex.InnerException != null) { throw ex.InnerException; }
			break;
		}
		case OwnUnits.Op.Destroy:
		{
			var (batch, objectType, ids) = OwnUnits.ReadDestroy(unit);
			try { Destroyers.GetOrAdd(objectType, t => DestroyDef.MakeGenericMethod(t)).Invoke(this, new object[] { batch, ids }); }
			catch (TargetInvocationException ex) when (ex.InnerException != null) { throw ex.InnerException; }
			break;
		}
		default:
			GameLog.Instance.WarnOnce("engine-client-msg-" + Name, $"engine {Name}: the server sent an acknowledgement, which only players send; ignored");
			break;
		}
	}

	private void Create<TObject, TPayload>(ulong batch, OwnUnits.Stub[] stubs, byte[] unit, int archiveAt) where TObject : class
	{
		var objects = new TObject[stubs.Length];
		var fresh = new List<(object Instance, NetworkId Id)>();
		for (int i = 0; i < stubs.Length; i++)
		{
			var id = new NetworkId(stubs[i].Id);
			if (Host.Ids.TryGetObject(id) is { } known)
			{
				objects[i] = known as TObject ?? throw new InvalidOperationException($"object #{id.Value} is a {known.GetType().Name} here, not a {typeof(TObject).Name}");
				continue;
			}
			object stub = AllocateStub(stubs[i]);
			objects[i] = (TObject)stub;
			Host.Ids.Add(stub, id);
			Host.MapToReplay(stub);
			fresh.Add((stub, id));
		}
		object? payload;
		try
		{
			payload = OwnUnits.ReadPayload(unit, archiveAt, Contexts());
			if (payload is not TPayload) throw new InvalidOperationException($"the payload read as {payload?.GetType().Name ?? "nothing"}, not {typeof(TPayload).Name}");
		}
		catch (Exception ex) when (ex.CanHandle())
		{
			// Nothing of the batch is built: its objects stay unknown here (references to them fail cleanly instead of
			// landing on empty stubs), and the server is told so it does not hold staged spawns for this player.
			foreach (var (instance, id) in fresh)
			{
				Host.Ids.Remove(id);
				Host.UnmapFromReplay(instance);
			}
			BatchesUnreadable++;
			KeenNetcode.UnitFailureLog.NoteError("object batch unreadable", typeof(TObject).Name + " batch", ex);
			GameLog.Instance.Warn($"engine {Name}: object batch #{batch} ({stubs.Length} {typeof(TObject).Name}) could not be read ({OwnArchive.Innermost(ex)}); those objects are missing in this game until it rejoins");
			Acknowledge(batch);
			return;
		}
		Creations++;
		var operation = _client.GetHandler<TObject, TPayload>().InitializeObjectsAsync(objects, (TPayload)payload!);
		Confirm(operation, batch).SkipWait();
	}

	private object AllocateStub(OwnUnits.Stub stub)
	{
		switch (stub.Kind)
		{
		case OwnUnits.StubKind.Entity:
			return _client.GetHandler<Entity>().AllocateStub(typeof(Entity));
		case OwnUnits.StubKind.Observable:
		{
			Type? type = stub.ObservableType == null ? null : OwnUnits.Find(stub.ObservableType);
			if (type == null || !typeof(ObservableObject).IsAssignableFrom(type) || type.IsAbstract)
				throw new InvalidOperationException($"observable model {stub.ObservableType} is not known here");
			return _client.GetHandler<ObservableObject>().AllocateStub(type);
		}
		case OwnUnits.StubKind.Collection:
			return _client.GetHandler<IObservedCollection>().AllocateStub(typeof(IObservedCollection));
		default:
			throw new InvalidOperationException("unknown object kind " + stub.Kind);
		}
	}

	private void Destroy<TObject>(ulong batch, ulong[] ids) where TObject : class
	{
		var objects = new List<TObject>(ids.Length);
		foreach (ulong raw in ids)
		{
			// 0 is the session itself; an id this side never had (its batch could not be read) has nothing to destroy.
			if (raw == 0) continue;
			var id = new NetworkId(raw);
			if (Host.Ids.TryGetObject(id) is TObject instance)
			{
				Host.Ids.Remove(id);
				objects.Add(instance);
			}
			ForgetHeld(raw);
		}
		Destructions++;
		var operation = objects.Count > 0 ? _client.GetHandler<TObject>().DestroyObjectsAsync(objects.ToArray()) : default;
		Confirm(operation, batch).SkipWait();
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
		Acknowledge(batch);
	}

	private void Acknowledge(ulong batch)
	{
		Acks++;
		EnqueueReplicationPacket(OwnUnits.WriteAck(batch));
	}

	// This player's own predicted character keeps its own control flags, physics state and predicted states
	// (KeenNetcode/OwnMotion.cs): the server's copy comes back a round trip late and would undo a key let go of since.
	protected override bool AcceptData(int typeId, Entity target, bool removal) => !KeenNetcode.OwnMotion.KeepsOwn(typeId, target, removal);

	protected override void OnDesync(byte[] bytes)
	{
		if (OwnDesync.Answer(bytes, Host.Ids) is { } report) EnqueueDesync(report);
	}
}
