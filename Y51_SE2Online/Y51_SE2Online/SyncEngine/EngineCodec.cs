using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Keen.Game2.Simulation.RuntimeSystems.Saves;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.Builders;
using Keen.VRage.DCS.Components;
using Keen.VRage.DCS.Definitions;
using Keen.VRage.DCS.Internal;
using Keen.VRage.DCS.ObjectBuilders;
using Keen.VRage.DCS.Scenes;
using Keen.VRage.Library.Collections;
using Keen.VRage.Library.Definitions;
using Keen.VRage.Library.Memory;
using Keen.VRage.Library.Serialization;
using Keen.VRage.Library.Threading;
using Keen.VRage.Library.Utils;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

internal static class EngineCodec
{
	public readonly struct KeyedListChildren(KeyedList<Guid, EntityObjectBuilder>? list)
	{
		private readonly KeyedList<Guid, EntityObjectBuilder>? _list = list;

		public int Count => _list?.Count ?? 0;

		public EntityObjectBuilder this[int i] => _list[i];
	}

	private static readonly FieldInfo? PendingCloseField = typeof(Session).GetField("PendingEntitiesForClose", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

	private static readonly FieldInfo? CloseField = typeof(Session).GetField("EntitiesForClose", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

	private static readonly FieldInfo?[] CloseFields = new[] { PendingCloseField, CloseField };

	public static bool CloseQueuesReachable => PendingCloseField != null && CloseField != null;

	private static SerializationContext Context(Stream s, string name, NetIdProxyContext proxies, HashSet<Blob>? blobs = null)
	{
		if (blobs != null)
		{
			return new SerializationContext(s, name, proxies, new DefinitionManagerDefinitionSerializationContext(Singleton<DefinitionManager>.Instance), new BlobSetIndexer(blobs));
		}
		return new SerializationContext(s, name, proxies, new DefinitionManagerDefinitionSerializationContext(Singleton<DefinitionManager>.Instance));
	}

	// The engine serializers allocate from the bump allocator; set it up for the call unless the caller already did.
	private static T WithBump<T>(Func<T> f)
	{
		BumpAllocator allocator = BumpAllocator.Instance;
		bool ownsAllocator = !allocator.Initialized;
		if (ownsAllocator)
		{
			allocator.Initialize();
		}
		try
		{
			return f();
		}
		finally
		{
			if (ownsAllocator)
			{
				allocator.Dispose();
			}
		}
	}

	public static byte[] SerializeBundle(EntityBundle bundle, NetIdProxyContext proxies, HashSet<Blob>? blobs = null)
	{
		return WithBump(() =>
		{
			using var stream = new MemoryStream();
			using (SerializationContext context = Context(stream, "y51online-bundle.vrb", proxies, blobs))
			{
				SerializationHelper.Serialize(context, in bundle, SerializerFormat.Binary);
			}
			return stream.ToArray();
		});
	}

	public static EntityBundle DeserializeBundle(byte[] bytes, NetIdProxyContext proxies, HashSet<Blob>? blobs = null)
	{
		return WithBump(() =>
		{
			using var s = new MemoryStream(bytes);
			using SerializationContext context = Context(s, "y51online-bundle.vrb", proxies, blobs);
			return SerializationHelper.Deserialize<EntityBundle>(context, SerializerFormat.Binary) ?? throw new InvalidDataException("bundle deserialized to null");
		});
	}

	public static byte[] SerializeSnapshot(SessionComponentsSnapshot snapshot, NetIdProxyContext proxies, HashSet<Blob>? blobs = null)
	{
		return WithBump(() =>
		{
			using var stream = new MemoryStream();
			using (SerializationContext context = Context(stream, "y51online-snapshot.vrb", proxies, blobs))
			{
				SerializationHelper.Serialize(context, in snapshot, SerializerFormat.Binary);
			}
			return stream.ToArray();
		});
	}

	public static SessionComponentsSnapshot DeserializeSnapshot(byte[] bytes, NetIdProxyContext proxies, HashSet<Blob>? blobs = null)
	{
		return WithBump(() =>
		{
			using var s = new MemoryStream(bytes);
			using SerializationContext context = Context(s, "y51online-snapshot.vrb", proxies, blobs);
			return SerializationHelper.Deserialize<SessionComponentsSnapshot>(context, SerializerFormat.Binary);
		});
	}

	public static byte[] SerializeObject(object o, Type t, NetIdProxyContext proxies, MethodInfo serializeGeneric)
	{
		return WithBump(() =>
		{
			using var stream = new MemoryStream();
			using (SerializationContext context = Context(stream, "y51online-object.vrb", proxies))
			{
				serializeGeneric.MakeGenericMethod(t).Invoke(null, new object[] { context, o, SerializerFormat.Binary });
			}
			return stream.ToArray();
		});
	}

	public static object? DeserializeObject(byte[] b, Type t, NetIdProxyContext proxies, MethodInfo deserializeGeneric)
	{
		return WithBump(() =>
		{
			using var s = new MemoryStream(b);
			using SerializationContext context = Context(s, "y51online-object.vrb", proxies);
			return deserializeGeneric.MakeGenericMethod(t).Invoke(null, new object[] { context, SerializerFormat.Binary });
		});
	}

	public static Guid Id(Blob b) => Guid.Parse(b.ToString());

	public static List<Guid> Ids(IEnumerable<Blob> blobs)
	{
		var ids = new List<Guid>();
		foreach (Blob blob in blobs)
		{
			ids.Add(Id(blob));
		}
		return ids;
	}

	// Serializes each session component on its own to find which blobs it references, keeping only the
	// blobs of components the client also runs.
	public static HashSet<Blob> SnapshotBlobs(SessionComponentsSnapshot snapshot, Func<Type, bool> clientUses, out int serverOnly, out List<string> owners)
	{
		var needed = new HashSet<Blob>();
		serverOnly = 0;
		owners = new List<string>();
		var proxies = new NetIdProxyContext((Entity _) => 0, null);
		foreach (SessionComponentsSnapshot.ComponentInfo component in snapshot.Components ?? Array.Empty<SessionComponentsSnapshot.ComponentInfo>())
		{
			if (component.ObjectBuilder == null)
			{
				continue;
			}
			var referenced = new HashSet<Blob>();
			var single = new SessionComponentsSnapshot();
			single.Components = new[] { component };
			SerializeSnapshot(single, proxies, referenced);
			if (referenced.Count != 0)
			{
				if (clientUses(component.ObjectBuilder.GetType()))
				{
					needed.UnionWith(referenced);
					owners.Add($"{component.Type?.Name ?? "?"} {referenced.Count}");
				}
				else
				{
					serverOnly += referenced.Count;
					owners.Add($"{component.Type?.Name ?? "?"} {referenced.Count} (server-only, not sent)");
				}
			}
		}
		return needed;
	}

	public static HashSet<Type> SessionObjectBuilderTypes(Session session)
	{
		var types = new HashSet<Type>();
		foreach (Component component in session.SessionComponents.Components)
		{
			Type objectBuilder = RuntimeComponentInfo.For(component.GetType()).ObjectBuilder;
			if (objectBuilder != null)
			{
				types.Add(objectBuilder);
			}
		}
		return types;
	}

	public static Dictionary<Guid, byte[]?> LoadBlobs(IReadOnlyDataOffloading offloading, IEnumerable<Blob> blobs, TimeSpan timeout, out int failed, out string? firstError)
	{
		var loaded = new Dictionary<Guid, byte[]?>();
		failed = 0;
		firstError = null;
		foreach (Blob blob in blobs)
		{
			Guid id = Id(blob);
			if (loaded.ContainsKey(id))
			{
				continue;
			}
			byte[]? data = null;
			try
			{
				Task<byte[]> task = offloading.LoadData(blob);
				if (task.IsCompleted)
				{
					data = task.GetAwaiter().GetResult();
				}
				else if (!task.WaitBlocking(timeout, out data))
				{
					throw new TimeoutException($"LoadData did not finish in {timeout.TotalSeconds:0} s");
				}
			}
			catch (Exception ex)
			{
				failed++;
				firstError ??= $"{id}: {ex.GetType().Name}: {ex.Message}";
				data = null;
			}
			loaded[id] = data;
		}
		return loaded;
	}

	public static void Register(IStreamedOutBlobs target, Guid id, byte[] data)
	{
		target.AddKnownBlob(Blob.FromGuid(id), () => new Task<Stream>(new MemoryStream(data, writable: false)));
	}

	public static bool IsDead(Entity? e)
	{
		try
		{
			if (e == null || EntityAllocator.IsStub(e))
			{
				return true;
			}
			Scene scene = e.Data.Scene;
			return scene == null || !scene.IsEntityAlive(e.DEntity);
		}
		catch (Exception)
		{
			return true;
		}
	}

	public static bool IsClosing(Session session, Entity e)
	{
		var pending = PendingCloseField?.GetValue(session) as HashSet<Entity>;
		if (pending == null || !pending.Contains(e))
		{
			return (CloseField?.GetValue(session) as HashSet<Entity>)?.Contains(e) ?? false;
		}
		return true;
	}

	public static int PurgeDeadFromCloseQueues(Session session, out string? sample)
	{
		sample = null;
		int purged = 0;
		foreach (FieldInfo? field in CloseFields)
		{
			if (field?.GetValue(session) is not HashSet<Entity> { Count: not 0 } queue)
			{
				continue;
			}
			List<Entity>? dead = null;
			foreach (Entity entity in queue)
			{
				if (IsDead(entity))
				{
					(dead ??= new List<Entity>()).Add(entity);
				}
			}
			if (dead == null)
			{
				continue;
			}
			foreach (Entity entity in dead)
			{
				queue.Remove(entity);
				purged++;
				sample ??= Describe(entity);
			}
		}
		return purged;
	}

	public static int RemoveFromCloseQueues(Session session, IEnumerable<Entity> entities)
	{
		var pending = PendingCloseField?.GetValue(session) as HashSet<Entity>;
		var closing = CloseField?.GetValue(session) as HashSet<Entity>;
		if ((pending == null || pending.Count == 0) && (closing == null || closing.Count == 0))
		{
			return 0;
		}
		int removed = 0;
		foreach (Entity entity in entities)
		{
			if (pending != null && pending.Remove(entity))
			{
				removed++;
			}
			if (closing != null && closing.Remove(entity))
			{
				removed++;
			}
		}
		return removed;
	}

	public static string Describe(Entity e)
	{
		try
		{
			if (EntityAllocator.IsStub(e))
			{
				return "a never-built entity stub";
			}
			return $"a destroyed entity (composition {(e.CompositionData != null ? "set" : "none")}, DEntity {e.DEntity})";
		}
		catch (Exception ex)
		{
			return "an unreadable entity (" + ex.GetType().Name + ")";
		}
	}

	public static KeyedListChildren Children(EntityObjectBuilder eob)
	{
		EntityCompositeDefinition definition = eob.Definition;
		if (definition == null || !definition.TryGetIndexOf<HierarchyComponent>(out var index) || index < 0 || index >= eob.ObjectBuilders.Count)
		{
			return default;
		}
		return new KeyedListChildren((eob.ObjectBuilders[index] as HierarchyComponentObjectBuilder)?.Children);
	}

	public static List<EntityLocator> Locate(EntityBundle bundle, IReadOnlyDictionary<Entity, EntityObjectBuilder> eobs, Func<Entity, uint> netId, out int unlocated)
	{
		var eobToEntity = new Dictionary<EntityObjectBuilder, Entity>(ReferenceEqualityComparer.Instance);
		foreach (KeyValuePair<Entity, EntityObjectBuilder> eob in eobs)
		{
			eobToEntity[eob.Value] = eob.Key;
		}
		var result = new List<EntityLocator>(eobs.Count);
		var seen = new HashSet<Entity>(ReferenceEqualityComparer.Instance);
		var path = new List<int>();
		for (int i = 0; i < bundle.Builders.Count; i++)
		{
			Walk(bundle.Builders[i], i, 0);
		}
		// Entities not reachable through a builder hierarchy are located by their bundle index alone.
		for (int i = 0; i < bundle.Entities.Count; i++)
		{
			Entity entity = bundle.Entities[i];
			if (entity != null && eobs.ContainsKey(entity) && seen.Add(entity))
			{
				result.Add(new EntityLocator(netId(entity), i, Array.Empty<int>()));
			}
		}
		unlocated = eobs.Count - seen.Count;
		return result;

		void Walk(EntityObjectBuilder? eob, int root, int depth)
		{
			if (eob != null && depth <= 64)
			{
				if (eobToEntity.TryGetValue(eob, out Entity entity) && seen.Add(entity))
				{
					result.Add(new EntityLocator(netId(entity), root, path.ToArray()));
				}
				KeyedListChildren children = Children(eob);
				for (int child = 0; child < children.Count; child++)
				{
					path.Add(child);
					Walk(children[child], root, depth + 1);
					path.RemoveAt(path.Count - 1);
				}
			}
		}
	}

	public static EntityObjectBuilder? Resolve(EntityBundle bundle, EntityLocator loc)
	{
		if (loc.Root < 0 || loc.Root >= bundle.Builders.Count)
		{
			return null;
		}
		EntityObjectBuilder? eob = bundle.Builders[loc.Root];
		foreach (int index in loc.Path)
		{
			KeyedListChildren children = Children(eob);
			if (index < 0 || index >= children.Count)
			{
				return null;
			}
			eob = children[index];
			if (eob == null)
			{
				return null;
			}
		}
		return eob;
	}

	public static (string Name, int Size) DescribeType(int typeId)
	{
		RuntimeDataInfo.Info info = RuntimeDataInfo.Of(typeId);
		Type runtimeType = info.RuntimeType;
		return (runtimeType == null ? $"typeId#{typeId}" : runtimeType.FullName + ", " + runtimeType.Assembly.GetName().Name, info.Size);
	}

	// The Data the game itself lets a client write to the server: the structs it marks [SyncFromClients].
	public static readonly Type[] ClientInputTypes =
	{
		typeof(Keen.Game2.Simulation.WorldObjects.Movement.ControlData),
		typeof(Keen.Game2.Simulation.WorldObjects.Movement.AngularControlData),
		typeof(Keen.Game2.Simulation.WorldObjects.Movement.ControlReferenceOrientation),
		typeof(Keen.Game2.Simulation.WorldObjects.Movement.LookOffsetData),
		typeof(Keen.Game2.Simulation.WorldObjects.Movement.TargetControlData)
	};

	private static readonly HashSet<string> ClientInputNames = new HashSet<string>(ClientInputTypes.Select(t => t.FullName + ", " + t.Assembly.GetName().Name));

	private static HashSet<int>? _clientInputIds;

	public static bool IsClientInput(string name) => ClientInputNames.Contains(name);

	/// <summary>The movement and body-turn inputs (not head look), which a fight scene zeroes.</summary>
	public static bool IsMovementInput(int typeId) => typeId == Keen.VRage.DCS.Internal.RuntimeDataInfo.Of(typeof(Keen.Game2.Simulation.WorldObjects.Movement.ControlData)).TypeId
		|| typeId == Keen.VRage.DCS.Internal.RuntimeDataInfo.Of(typeof(Keen.Game2.Simulation.WorldObjects.Movement.AngularControlData)).TypeId
		|| typeId == Keen.VRage.DCS.Internal.RuntimeDataInfo.Of(typeof(Keen.Game2.Simulation.WorldObjects.Movement.TargetControlData)).TypeId;

	public static bool IsClientInput(int typeId) => (_clientInputIds ??= new HashSet<int>(ClientInputTypes.Select(t => RuntimeDataInfo.Of(t).TypeId))).Contains(typeId);

	public static (int TypeId, int Size)? ResolveType(string name)
	{
		Type? type = Core.WireTypes.FindLoaded(name);
		if (type == null || !type.IsValueType)
		{
			return null;
		}
		// The peer's raw bytes are copied into the Data as is: a reference or pointer in it would be forged.
		if (!IsPlainData(type, 0))
		{
			throw new InvalidDataException("contains references or pointers, raw bytes cannot be applied to it");
		}
		RuntimeDataInfo.Info info = RuntimeDataInfo.Of(type);
		return (info.TypeId, info.Size);
	}

	internal static bool IsPlainData(Type type, int depth)
	{
		if (type.IsPointer || type.IsFunctionPointer || type == typeof(IntPtr) || type == typeof(UIntPtr))
		{
			return false;
		}
		if (type.IsPrimitive || type.IsEnum)
		{
			return true;
		}
		if (!type.IsValueType || type.IsByRefLike || depth > 32)
		{
			return false;
		}
		foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
		{
			if (!IsPlainData(field.FieldType, depth + 1))
			{
				return false;
			}
		}
		return true;
	}

	public static List<DataBlob> Blobs(ref DEntityBuilder builder, TypeKeyTable keys)
	{
		var blobs = new List<DataBlob>();
		foreach (KeyValuePair<int, Memory<byte>?> entry in builder.Data)
		{
			Memory<byte>? value = entry.Value;
			if (value is Memory<byte> data)
			{
				blobs.Add(new DataBlob(keys.GetOrAdd(entry.Key, DescribeType).Key, data.ToArray()));
			}
		}
		return blobs;
	}
}
