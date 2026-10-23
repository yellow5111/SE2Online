using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Keen.VRage.Library.Reflection;
using Keen.VRage.Library.UI;
using Keen.VRage.Library.Utils;
using Keen.VRage.Multiplayer;
using Keen.VRage.Multiplayer.Data;
using Keen.VRage.Multiplayer.NetworkStories;
using Keen.VRage.Multiplayer.Replications;
using Keen.VRage.Multiplayer.Replications.FailureStrategies;
using Keen.VRage.Multiplayer.Transport;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.Engine;

/// <summary>A story (a request between the two games) moving to the other side: its state machine type, the hop it is
/// suspended on and the fields that hop carries.</summary>
internal sealed class OwnStoryMove
{
	public StoryId Id;
	public string TypeName = "";
	public int State;
	public readonly List<(int Index, Type Declared, object? Value)> Fields = new();

	public string Name => OwnStories.Short(TypeName);
}

/// <summary>
/// Moves the game's network stories between the two games: which fields a hop carries comes from the game's generated
/// story metadata (the same fields its single-player plumbing carries), the values travel as SE2 Online serializes
/// them, and a story rebuilt on this side is bound to this side's narrator.
/// </summary>
internal static class OwnStories
{
	private sealed class Plan
	{
		public readonly NetworkStoryStateMachineLayout Layout;
		public readonly Dictionary<int, FieldInfo[]> Carried = new();
		public readonly FieldInfo[] Closures;

		public Plan(Type machine)
		{
			Layout = MetadataIndexerBase<NetworkStoryStateMachineIndexer>.Instance.GetLayout(machine);
			foreach (var (state, hop) in Layout.Hops) Carried[state] = hop.CarriedFields.Select(i => Layout.Fields[i]).ToArray();
			// The compiler's closure object a story makes before its first wait is no field a hop carries: the receiver
			// would resume with it null and fault on its first use (block groups). It gets a fresh one, as the code before
			// the hop would have made (KeenNetcode/StoryClosures).
			Closures = machine.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
				.Where(f => f.Name.StartsWith("<>8__", StringComparison.Ordinal) && f.FieldType.IsClass && f.FieldType.GetConstructor(Type.EmptyTypes) != null).ToArray();
		}
	}

	private static readonly ConcurrentDictionary<Type, Plan> Plans = new();

	public static long ClosuresFilled;

	private static Plan PlanOf(Type machine) => Plans.GetOrAdd(machine, t => new Plan(t));

	public static string Short(string typeName)
	{
		string name = typeName.Split(',')[0];
		int plus = name.LastIndexOf('+');
		return plus >= 0 ? name[(plus + 1)..] : name;
	}

	/// <summary>
	/// The hop's fields of a story about to move, or null when it cannot cross: a field the other side has no counterpart
	/// for under a skip strategy (the game drops such a story), or a value no serializer handles.
	/// </summary>
	public static OwnStoryMove? Capture(StoryId id, IAsyncStateMachine machine, IReplicationSerializationContext ids, out string? why)
	{
		why = null;
		var plan = PlanOf(machine.GetType());
		int state = plan.Layout.ReadState(machine);
		if (!plan.Carried.TryGetValue(state, out var fields))
		{
			why = $"state {state} has no hop";
			return null;
		}
		var move = new OwnStoryMove { Id = id, TypeName = machine.GetType().AssemblyQualifiedName!, State = state };
		foreach (var field in fields)
		{
			object? value = field.GetValue(machine);
			if (IsUnpairedSkip(field.FieldType, value, ids) || ObservableObjectReplicationDeltas.ContainsUnpairedReplicatedReference(value, ids))
			{
				why = field.Name + " names something the other side does not have";
				return null;
			}
			if (value != null && !ReplicatedDataValidator.IsSerializable(value.GetType()))
			{
				why = $"{field.Name} is a {value.GetType().Name}, which cannot cross";
				return null;
			}
			move.Fields.Add((plan.Layout.Fields.IndexOf(field), field.FieldType, value));
		}
		return move;
	}

	/// <summary>The state machine type a peer names: only a generated network story (one the metadata describes).</summary>
	public static Type? Resolve(string typeName)
	{
		var type = OwnUnits.Find(typeName);
		if (type == null || !type.IsValueType && !type.IsClass || !typeof(IAsyncStateMachine).IsAssignableFrom(type)) return null;
		try { return PlanOf(type).Carried.Count > 0 ? type : null; }
		catch (Exception) { return null; }
	}

	/// <summary>
	/// Rebuilds a moved story on this side, bound to this side's narrator (its builder is never carried). It resumes only at
	/// a hop the game's metadata has moving to this side (to the server on the server, to a player's game there): a peer
	/// cannot run a player's step on the server. (Which of a story's server hops it resumes at is the sender's, as in the game.)
	/// </summary>
	public static IAsyncStateMachine Restore(OwnStoryMove move, StoryId localId, INetworkStoryNarrator narrator, bool server)
	{
		var type = Resolve(move.TypeName) ?? throw new InvalidOperationException("the story is not a network story this game has");
		var plan = PlanOf(type);
		if (!plan.Layout.Hops.TryGetValue(move.State, out var hop) || hop.Kind != (server ? NetworkStoryHopKind.ToServer : NetworkStoryHopKind.ToClient))
			throw new InvalidOperationException($"story {Short(type.FullName ?? "?")} cannot resume at step {move.State} on this side");
		var machine = (IAsyncStateMachine)Activator.CreateInstance(type)!;
		plan.Layout.WriteState(machine, move.State);
		if (plan.Carried.TryGetValue(move.State, out var carried))
		{
			foreach (var (index, _, value) in move.Fields)
			{
				if (index < 0 || index >= plan.Layout.Fields.Length) continue;
				var field = plan.Layout.Fields[index];
				// Only a field this hop carries, of a type the value fits: anything else in the message is ignored.
				if (Array.IndexOf(carried, field) < 0) continue;
				if (!Fits(field.FieldType, value)) continue;
				// A boxed struct state machine: SetValue writes into the box the story runs from.
				field.SetValue(machine, value);
			}
		}
		foreach (var closure in plan.Closures)
		{
			if (closure.GetValue(machine) != null) continue;
			try
			{
				closure.SetValue(machine, Activator.CreateInstance(closure.FieldType));
				if (System.Threading.Interlocked.Increment(ref ClosuresFilled) == 1)
					GameLog.Instance.Info($"engine: story {move.Name} arrived without its closure object; gave it a new one");
			}
			catch (Exception ex) { GameLog.Instance.WarnOnce("own-story-closure-" + closure.FieldType.Name, $"engine: could not make {closure.FieldType.Name} for story {move.Name}: {ex.Message}"); }
		}
		var builder = (NetworkStoryContext)Activator.CreateInstance(plan.Layout.BuilderField.FieldType)!;
		builder.Narrator = narrator;
		builder.Story = new RemoteNetworkStory(localId);
		builder.RebindStateMachine(machine);
		plan.Layout.SetBuilder(machine, builder);
		KeenNetcode.ToolbarRequests.Restored(machine);
		return machine;
	}

	private static bool Fits(Type slot, object? value)
	{
		if (value == null) return !slot.IsValueType || Nullable.GetUnderlyingType(slot) != null;
		return slot.IsInstanceOfType(value) || Nullable.GetUnderlyingType(slot) == value.GetType();
	}

	/// <summary>The result type of a network story (its builder's TResult), or null when the story has no layout.</summary>
	public static Type? ResultTypeOf(Type machine)
	{
		try
		{
			for (var t = PlanOf(machine).Layout.BuilderField.FieldType; t != null; t = t.BaseType)
				if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(NetworkStoryMethodBuilder<>)) return t.GetGenericArguments()[0];
		}
		catch (Exception) { }
		return null;
	}

	/// <summary>Server: the result of the request the story waits on, as the player who sent it.</summary>
	public static void CompleteServerRequest(IAsyncStateMachine machine, ReplicationEventContext result) => PlanOf(machine.GetType()).Layout.CompleteServerRequest(machine, result);

	// The game drops a story whose skip-on-fail field names an observable the other side has no pairing for, before it is
	// staged (dropping it later would take the whole frame).
	private static bool IsUnpairedSkip(Type fieldType, object? value, IReplicationSerializationContext ids)
	{
		if (value == null) return false;
		ReplicationFailureStrategyHelpers.GetStrategy(fieldType, out ReplicationFailureStrategy strategy, out Type wrapped);
		if (strategy != ReplicationFailureStrategy.Skip || wrapped == fieldType) return false;
		object? inner = ReplicationFailureStrategyHelpers.GetWrappedValue(value);
		return inner is ObservableObject or IObservedCollection && !ids.TryGetId(inner).HasValue;
	}
}
