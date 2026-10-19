using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Keen.Game2.Simulation.WorldObjects.Characters;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.DCS.Accessors;
using Keen.VRage.DCS.Components;
using Keen.VRage.DCS.Definitions;
using Keen.VRage.DCS.Internal;
using Keen.VRage.Multiplayer.Replications.InProcess;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// The per-root digest both sides build the same way: the replicated descendants (characters excluded, they are roots of
/// their own) and a per-type sum over the raw replicated Data of each, the bytes the host sends and the client applies as is.
/// </summary>
internal static class DesyncHash
{
	private readonly record struct HashedType(int TypeId, int Size, uint Name);

	private static readonly ConcurrentDictionary<EntityCompositeDefinition, HashedType[]> Types = new(ReferenceEqualityComparer.Instance);

	/// <summary>Entities visited above this count are not hashed: existence and position only.</summary>
	public static int MaxEntities = 5000;

	private static HashedType[] TypesOf(ReplicationStrategy strategy, EntityCompositeDefinition serverComposite) => Types.GetOrAdd(serverComposite, composite =>
	{
		// ReplicationStrategy keeps each composition's replicated (TypeId, Size) list only inside its capture closure.
		if (!strategy._entityDataCaptures.TryGetValue(composite, out var capture) || capture.Target == null) return Array.Empty<HashedType>();
		var field = capture.Target.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault(f => f.FieldType == typeof((int, int)[]));
		if (field?.GetValue(capture.Target) is not (int TypeId, int Size)[] list) return Array.Empty<HashedType>();
		var hashed = new List<HashedType>(list.Length);
		foreach (var (typeId, size) in list)
		{
			var type = RuntimeDataInfo.Of(typeId).RuntimeType;
			if (type != null && DesyncDigest.IsHashed(type.Name)) hashed.Add(new HashedType(typeId, size, DesyncDigest.NameHash(type.FullName ?? type.Name)));
		}
		return hashed.ToArray();
	});

	public static bool IsCharacter(Entity e) => e.TryGet<CharacterMovementComponent>() != null;

	/// <summary>A digest walk over a root and its replicated descendants that can stop and resume across frames.</summary>
	public sealed class Walk
	{
		public readonly Stack<Entity> Stack = new();
		public readonly ClientRoot Into = new();
		public Entity Root = null!;
		public int Visited;

		public void Begin(Entity root)
		{
			Stack.Clear();
			Stack.Push(root);
			Into.Types.Clear();
			Into.Children = 0;
			Root = root;
			Visited = 0;
		}
	}

	/// <summary>Counts and hashes <paramref name="root"/> and its replicated descendants in one go; false if it was too big to hash.</summary>
	public static bool Fill(Entity root, ReplicationStrategy strategy, Func<Entity, EntityCompositeDefinition?> serverComposite, Func<Entity, bool> replicated, ClientRoot into, out int visited)
	{
		var walk = new Walk();
		walk.Begin(root);
		bool done = Step(walk, strategy, serverComposite, replicated, int.MaxValue) == true;
		visited = walk.Visited;
		into.Children = walk.Into.Children;
		into.Types.Clear();
		foreach (var (type, sum) in walk.Into.Types) into.Types[type] = sum;
		return done;
	}

	/// <summary>Visits up to <paramref name="steps"/> entities: null while more remain, true when finished, false if the root is too big to hash.</summary>
	public static bool? Step(Walk w, ReplicationStrategy strategy, Func<Entity, EntityCompositeDefinition?> serverComposite, Func<Entity, bool> replicated, int steps)
	{
		var into = w.Into;
		while (w.Stack.Count > 0)
		{
			if (steps-- <= 0) return null;
			var e = w.Stack.Pop();
			if (++w.Visited > MaxEntities)
			{
				into.Types.Clear();
				return false;
			}
			if (e != w.Root)
			{
				if (IsCharacter(e) || !replicated(e)) continue;
				into.Children++;
			}
			if (serverComposite(e) is { } composite)
			{
				var scene = e.Scene;
				var dEntity = e.DEntity;
				foreach (var t in TypesOf(strategy, composite))
				{
					ref byte data = ref DataMarshal.TryGetDataPointer(scene, dEntity, t.TypeId, writeAccess: false);
					if (Unsafe.IsNullRef(ref data)) continue;
					into.Types.TryGetValue(t.Name, out uint sum);
					into.Types[t.Name] = sum + DesyncDigest.Bytes(MemoryMarshal.CreateReadOnlySpan(ref data, t.Size));
				}
			}
			if (e.TryGet<HierarchyComponent>()?.Children is { } children)
			{
				foreach (var child in children) w.Stack.Push(child);
			}
		}
		return true;
	}

	public static bool TryPosition(Entity e, out double x, out double y, out double z)
	{
		x = y = z = 0;
		if (EngineCodec.IsDead(e) || !e!.Data.TryGet<WorldTransform>(out var t)) return false;
		(x, y, z) = (t.Position.X, t.Position.Y, t.Position.Z);
		return true;
	}

	public static EntityCompositeDefinition? ClientToServer(ReplicationStrategy strategy, Entity clientEntity)
	{
		var composite = clientEntity.GetComposition();
		return composite != null && strategy._clientToServerParings.TryGetValue(composite, out var server) ? server : null;
	}

#if !PULSAR
	/// <summary>Test corruption: flips the first byte of the first hashed Data the root or a descendant has; its type name, or null.</summary>
	public static string? Corrupt(Entity root, ReplicationStrategy strategy, Func<Entity, EntityCompositeDefinition?> serverComposite)
	{
		var stack = new Stack<Entity>();
		stack.Push(root);
		while (stack.Count > 0)
		{
			var e = stack.Pop();
			if (serverComposite(e) is { } composite)
			{
				foreach (var t in TypesOf(strategy, composite))
				{
					ref byte data = ref DataMarshal.TryGetDataPointer(e.Scene, e.DEntity, t.TypeId, writeAccess: true);
					if (Unsafe.IsNullRef(ref data)) continue;
					data ^= 0x5A;
					return RuntimeDataInfo.Of(t.TypeId).RuntimeType?.Name;
				}
			}
			if (e.TryGet<HierarchyComponent>()?.Children is { } children)
			{
				foreach (var child in children) stack.Push(child);
			}
		}
		return null;
	}
#endif

	/// <summary>Type names behind the name hashes, for bug reports.</summary>
	public static string NameOf(uint nameHash)
	{
		foreach (var list in Types.Values)
		{
			foreach (var t in list)
			{
				if (t.Name == nameHash) return RuntimeDataInfo.Of(t.TypeId).RuntimeType?.Name ?? $"typeId#{t.TypeId}";
			}
		}
		return $"type#{nameHash:x8}";
	}
}
