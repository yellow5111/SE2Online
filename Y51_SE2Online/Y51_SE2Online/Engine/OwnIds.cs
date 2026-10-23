using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Keen.VRage.DCS.Scenes;
using Keen.VRage.Multiplayer.Transport;

namespace Y51_SE2Online.Engine;

/// <summary>
/// SE2 Online's network ids for one side of a session: which object every id names. The server gives an object its id
/// the first time anything about it goes to a player; a player's game learns them from the server's object batches.
/// Ids are never reused: a retired id resolves to nothing, so a late reference to a removed object fails cleanly under
/// whatever failure strategy the game's serializers have in force.
/// </summary>
internal sealed class OwnIds : ReplicationSerializationContextBase
{
	private readonly Dictionary<object, NetworkId> _toId = new(ReferenceEqualityComparer.Instance);
	private readonly Dictionary<NetworkId, object> _toObject = new();
	private readonly ConditionalWeakTable<object, object> _retired = new();
	private ulong _next;

	public int Count => _toId.Count;

	/// <summary>Every id that resolves now, with its object.</summary>
	public IEnumerable<KeyValuePair<NetworkId, object>> Resolved => _toObject;

	public override ReplicatedValuePlacement GetPlacement(object obj) => ReplicatedValuePlacement.Reference;

	public override NetworkId? TryGetId(object instance) => _toId.TryGetValue(instance, out var id) ? id : null;

	public override object? TryGetObject(NetworkId id) => _toObject.GetValueOrDefault(id);

	public override NetworkId? TryGetRetiredId(object instance) => _retired.TryGetValue(instance, out object? id) ? (NetworkId)id : null;

	/// <summary>Server: the object's id, allocated now if it has none.</summary>
	public NetworkId GetOrAdd(object instance, out bool added)
	{
		if (_toId.TryGetValue(instance, out var id))
		{
			added = false;
			return id;
		}
		// The game's entity references are written as 32-bit ints.
		if (_next >= int.MaxValue) throw new InvalidOperationException("SE2 Online ran out of network ids for this session");
		id = new NetworkId(++_next);
		_toId.Add(instance, id);
		_toObject.Add(id, instance);
		added = true;
		return id;
	}

	/// <summary>A pairing this side did not allocate (a player's game: the server's id for an object it now has).</summary>
	public bool Add(object instance, NetworkId id)
	{
		if (_toId.ContainsKey(instance) || _toObject.ContainsKey(id)) return false;
		_toId.Add(instance, id);
		_toObject.Add(id, instance);
		if (id.Value > _next) _next = id.Value;
		return true;
	}

	/// <summary>The id stops resolving to the object while the object keeps its id (an entity leaving the scene this frame).</summary>
	public void StopResolving(object instance)
	{
		if (_toId.TryGetValue(instance, out var id)) _toObject.Remove(id);
	}

	public void ResumeResolving(object instance)
	{
		if (_toId.TryGetValue(instance, out var id)) _toObject.TryAdd(id, instance);
	}

	/// <summary>Removes the pairing, remembering the id it was known by.</summary>
	public void Retire(object instance)
	{
		if (!_toId.TryGetValue(instance, out var id)) return;
		_retired.AddOrUpdate(instance, id);
		Remove(instance);
	}

	public void Remove(object instance)
	{
		if (!_toId.Remove(instance, out var id)) return;
		if (_toObject.TryGetValue(id, out object? mapped) && ReferenceEquals(mapped, instance)) _toObject.Remove(id);
	}

	public object? Remove(NetworkId id)
	{
		if (!_toObject.Remove(id, out object? instance)) return null;
		_toId.Remove(instance);
		return instance;
	}

	public void Clear()
	{
		_toId.Clear();
		_toObject.Clear();
	}
}

/// <summary>A server serialization of an object batch: the batch's own objects travel in its payload, everything else by id.</summary>
internal sealed class OwnBatchIds(OwnIds ids, HashSet<object> payloadObjects) : ReplicationSerializationContextBase
{
	public override ReplicatedValuePlacement GetPlacement(object obj) => payloadObjects.Contains(obj) ? ReplicatedValuePlacement.InPayload : ReplicatedValuePlacement.Reference;

	public override NetworkId? TryGetId(object instance) => ids.TryGetId(instance);

	public override object? TryGetObject(NetworkId id) => ids.TryGetObject(id);

	public override NetworkId? TryGetRetiredId(object instance) => ids.TryGetRetiredId(instance);
}

/// <summary>Which objects one player holds or is about to receive (the game's own bookkeeping, keyed by SE2 Online's ids).</summary>
internal sealed class OwnTracker(Scene scene, OwnHost host) : ObjectReplicationTracker<NetworkId>(scene, EqualityComparer<NetworkId>.Default)
{
	protected override NetworkId AllocateRemotePair<TObject>(TObject instance) => host.GetOrRegisterId(instance);
}
