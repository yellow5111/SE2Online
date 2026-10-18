using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Keen.VRage.Multiplayer.Annotations;
using Keen.VRage.Multiplayer.NetworkStories;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

/// <summary>A network story the game declares, and what a remote side may set when it starts one.</summary>
internal sealed class StoryType
{
	public Type Type;

	public string Name;

	/// <summary>The fields holding the async method's parameters, plus &lt;&gt;4__this.</summary>
	public FieldInfo[] Arguments;

	public FieldInfo State;

	public FieldInfo Builder;

	/// <summary>A client may start it on the host: it is [Server] or awaits MoveTo.Server.</summary>
	public bool ClientMayStart;

	/// <summary>The host may start it on a client: a forwarded notification story whose only awaits are MoveTo.Client.</summary>
	public bool HostMayStart;
}

internal static class StoryState
{
	public static bool IsBuilder(Type t) => typeof(NetworkStoryContext).IsAssignableFrom(t);

	// The compiler's kick-off stub starts every async state machine in this state.
	public const int EntryState = -1;

	// A host-started story enters the client at its first await, a MoveTo.Client: everything before it is server code.
	public const int FirstAwaitState = 0;

	private const string This = "<>4__this";

	private static readonly MethodInfo MoveToServer = typeof(MoveTo).GetMethod(nameof(MoveTo.Server))!;

	private static readonly ConcurrentDictionary<Type, StoryType?> Stories = new();

	// The plugin's only story: joiners resolve host EntityIds (terminal inventories, trade, respawn) through it.
	private static readonly Type OwnStory = typeof(SecureEntities).GetMethod(nameof(SecureEntities.ResolveSecureEntity))!.GetCustomAttribute<AsyncStateMachineAttribute>()!.StateMachineType;

	public static StoryType? Find(string wireName, out string why)
	{
		Type? type = GameType(wireName);
		StoryType? story = type == null ? null : Find(type);
		why = type == null ? "not a type of the game's own assemblies" : story == null ? "not a network story the game declares" : "";
		return story;
	}

	public static StoryType? Find(Type type)
	{
		if (type.DeclaringType == null || type.ContainsGenericParameters || !typeof(IAsyncStateMachine).IsAssignableFrom(type) || !type.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false))
		{
			return null;
		}
		return Stories.GetOrAdd(type, Inspect);
	}

	private static Type? GameType(string wireName)
	{
		int comma = wireName.IndexOf(',');
		if (comma <= 0 || wireName.Length > 1024 || wireName.IndexOf('[') >= 0)
		{
			return null;
		}
		// Matched by name only: Pulsar gives each install's plugin assembly its own random name.
		if (wireName[..comma] == OwnStory.FullName)
		{
			return OwnStory;
		}
		string assembly = wireName[(comma + 1)..].Trim();
		if (!assembly.StartsWith("Game2.", StringComparison.Ordinal) && !assembly.StartsWith("VRage.", StringComparison.Ordinal))
		{
			return null;
		}
		try
		{
			return AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == assembly)?.GetType(wireName[..comma], throwOnError: false);
		}
		catch (Exception)
		{
			return null;
		}
	}

	// Anything this cannot read leaves the type off both allow-lists.
	private static StoryType? Inspect(Type t)
	{
		try
		{
			MethodInfo? method = t.DeclaringType!.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
				.FirstOrDefault(m => m.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType == t);
			if (method == null || !typeof(NetworkStory).IsAssignableFrom(method.ReturnType))
			{
				return null;
			}
			FieldInfo[] fields = ObjectCodec.Fields(t);
			FieldInfo? state = fields.FirstOrDefault(f => f.Name == "<>1__state" && f.FieldType == typeof(int));
			FieldInfo? builder = fields.FirstOrDefault(f => f.Name == "<>t__builder" && IsBuilder(f.FieldType));
			if (state == null || builder == null)
			{
				return null;
			}
			var parameters = new HashSet<string>(method.GetParameters().Select(p => p.Name ?? ""), StringComparer.Ordinal);
			if (!method.IsStatic)
			{
				parameters.Add(This);
			}
			FieldInfo[] awaiters = fields.Where(f => f.Name.StartsWith("<>u__", StringComparison.Ordinal)).ToArray();
			string name = Name(ObjectCodec.TypeName(t));
			return new StoryType
			{
				Type = t,
				Name = name,
				Arguments = fields.Where(f => !f.IsStatic && parameters.Contains(f.Name)).ToArray(),
				State = state,
				Builder = builder,
				ClientMayStart = method.IsDefined(typeof(Server), inherit: false) || Calls(t.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic), MoveToServer),
				HostMayStart = (HostIdentities.ForwardedStoryOwners.Contains(t.DeclaringType.Name) || HostIdentities.ActorStories.Contains(name)) && awaiters.Length > 0 && awaiters.All(f => f.FieldType == typeof(ClientRequest))
			};
		}
		catch (Exception)
		{
			return null;
		}
	}

	private static bool Calls(MethodBase? method, MethodInfo target)
	{
		byte[]? il = method?.GetMethodBody()?.GetILAsByteArray();
		if (il == null)
		{
			return false;
		}
		for (int i = 0; i + 4 < il.Length; i++)
		{
			if (il[i] != 0x28 && il[i] != 0x6F)
			{
				continue;
			}
			try
			{
				if (method!.Module.ResolveMethod(BitConverter.ToInt32(il, i + 1)) == target)
				{
					return true;
				}
			}
			catch (Exception)
			{
			}
		}
		return false;
	}

	// Only the arguments travel; the receiver builds the state machine itself.
	public static List<(string, WireValue)>? EncodeArguments(StoryType story, object box, ObjectSpace sp, out string? fail)
	{
		fail = null;
		var members = new List<(string, WireValue)>();
		foreach (FieldInfo field in story.Arguments)
		{
			WireValue encoded = ObjectCodec.Encode(field.GetValue(box), field.FieldType, sp, ref fail);
			if (fail != null)
			{
				fail = field.Name + ": " + fail;
				return null;
			}
			members.Add((field.Name, encoded));
		}
		return members;
	}

	/// <summary>
	/// Builds a fresh state machine in <paramref name="state"/> with a fresh builder, setting only the story's arguments from the wire.
	/// <paramref name="refused"/> says the move asked for more than that; <paramref name="fail"/> that an argument does not decode here.
	/// </summary>
	public static object? Start(StoryType story, StoryMove m, ObjectSpace sp, NetworkStoryContext builder, int state, out string? refused, out string? fail)
	{
		refused = null;
		fail = null;
		var seen = new HashSet<string>(StringComparer.Ordinal);
		foreach (var (name, wire) in m.Fields)
		{
			if (Array.Find(story.Arguments, f => f.Name == name) == null)
			{
				refused = "field " + Clip(name) + " is not an argument";
				return null;
			}
			if (!seen.Add(name))
			{
				refused = "argument " + name + " sent twice";
				return null;
			}
			if (wire.Kind is WireKind.Builder or WireKind.ServerRequest or WireKind.Default)
			{
				refused = $"argument {name} is a {wire.Kind} marker, not a value";
				return null;
			}
		}
		object stateMachine = Activator.CreateInstance(story.Type)!;
		story.State.SetValue(stateMachine, state);
		story.Builder.SetValue(stateMachine, builder);
		foreach (var (name, wire) in m.Fields)
		{
			FieldInfo field = Array.Find(story.Arguments, f => f.Name == name)!;
			object? value = ObjectCodec.Decode(wire, field.FieldType, sp, translated: false, ref fail);
			if (fail != null)
			{
				fail = name + ": " + fail;
				return null;
			}
			if (value != null && !field.FieldType.IsInstanceOfType(value))
			{
				refused = $"argument {name} is a {value.GetType().Name}, not a {field.FieldType.Name}";
				return null;
			}
			field.SetValue(stateMachine, value);
		}
		FieldInfo? self = Array.Find(story.Arguments, f => f.Name == This);
		if (self != null && self.GetValue(stateMachine) == null)
		{
			refused = "no instance to run on";
			return null;
		}
		return stateMachine;
	}

	public static string Clip(string text) => text.Length > 120 ? text.Substring(0, 120) + "..." : text;

	public static string Name(string stateMachineType)
	{
		string fullName = stateMachineType;
		int comma = fullName.IndexOf(',');
		if (comma >= 0)
		{
			fullName = fullName.Substring(0, comma);
		}
		int plus = fullName.LastIndexOf('+');
		string owner = plus > 0 ? fullName.Substring(0, plus) : fullName;
		int dot = owner.LastIndexOf('.');
		owner = dot < 0 ? owner : owner[(dot + 1)..];
		string method = plus <= 0 ? fullName : fullName[(plus + 1)..];
		int open = method.IndexOf('<');
		int close = method.IndexOf('>');
		if (open >= 0 && close > open)
		{
			method = method[(open + 1)..close];
		}
		return owner + "." + method;
	}

	public static string Shape(string text) => Regex.Replace(text, @"\d+", "N");

	public static string Describe(object? result)
	{
		if (result == null)
		{
			return " null";
		}
		if (result is DBNull)
		{
			return "";
		}
		if (result is string text)
		{
			return " \"" + (text.Length > 40 ? text.Substring(0, 40) + "..." : text) + "\"";
		}
		if (result is ICollection collection)
		{
			return $" {collection.GetType().Name} ({collection.Count})";
		}
		if (result.GetType().IsPrimitive || result.GetType().IsEnum)
		{
			return " " + result;
		}
		if (result is ITuple tuple)
		{
			string? shown = tuple.ToString();
			return " " + (shown != null && shown.Length <= 80 ? shown : result.GetType().Name);
		}
		return " " + result.GetType().Name;
	}

	public static Type ResultType(object context)
	{
		Type type = context.GetType();
		while (type != null)
		{
			if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(NetworkStoryMethodBuilder<>))
			{
				return type.GetGenericArguments()[0];
			}
			type = type.BaseType;
		}
		return typeof(DBNull);
	}
}
