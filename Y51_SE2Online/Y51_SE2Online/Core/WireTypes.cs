using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Y51_SE2Online.Core;

// Every type name a peer sends is resolved here. Only assemblies this process already has are searched (a name
// can never make us load one from disk), and only the game's own types or plain data pass.
public static class WireTypes
{
	public const int CacheCap = 4096;

	private const int LogCap = 256;

	public static IMpLog? Log;

	private static readonly ConcurrentDictionary<string, (Type? Type, string? Problem)> Cache = new();

	private static readonly ConcurrentDictionary<string, byte> Logged = new();

	private static readonly HashSet<Type> PlainData = new()
	{
		typeof(object), typeof(string), typeof(decimal), typeof(DateTime), typeof(DateTimeOffset), typeof(TimeSpan), typeof(Guid),
		typeof(Half), typeof(Int128), typeof(UInt128),
		// Only ever a name or a slot: Type is abstract, so it is never constructed from the wire.
		typeof(Type)
	};

	private static readonly HashSet<Type> PlainGenerics = new()
	{
		typeof(Nullable<>), typeof(KeyValuePair<,>),
		typeof(ValueTuple<>), typeof(ValueTuple<,>), typeof(ValueTuple<,,>), typeof(ValueTuple<,,,>),
		typeof(ValueTuple<,,,,>), typeof(ValueTuple<,,,,,>), typeof(ValueTuple<,,,,,,>), typeof(ValueTuple<,,,,,,,>),
		typeof(List<>), typeof(HashSet<>), typeof(Dictionary<,>), typeof(ImmutableArray<>), typeof(ObservableCollection<>)
	};

	public static int CacheCount => Cache.Count;

	// A host model replicates as its [Replicate] base (ToolbarActionHintServerModel -> ToolbarActionHintModel), and Keen's
	// story result translation hands a story declared to return the server type that client replica.
	public static bool IsReplicaOf(Type declared, Type replica) => replica != typeof(object) && declared.IsSubclassOf(replica);

	public static bool IsGameAssembly(string? name)
	{
		if (name == null || !(name == "SpaceEngineers2" || name.StartsWith("Game2.", StringComparison.Ordinal) || name.StartsWith("VRage.", StringComparison.Ordinal)))
		{
			return false;
		}
		foreach (string tool in new[] { "Generator", "Scripting", "CodeAnalysis", "CodeGuard", "ContentPipeline", "ContentBuilder", "Test" })
		{
			if (name.Contains(tool, StringComparison.Ordinal))
			{
				return false;
			}
		}
		return true;
	}

	// declared: the slot the value goes into. construct: the caller will create an instance of the type.
	public static Type? Resolve(string name, Type declared, bool construct = false)
	{
		var (type, problem) = Lookup(name);
		if (type != null && problem == null)
		{
			problem = construct ? ConstructProblem(type) : null;
			Type slot = Nullable.GetUnderlyingType(declared) ?? declared;
			// A slot typed by a generic parameter (an open command layout) cannot be checked; the type rules still apply.
			if (problem == null && !slot.ContainsGenericParameters && !slot.IsAssignableFrom(type))
			{
				problem = "does not fit the slot";
			}
		}
		if (problem == null)
		{
			return type;
		}
		Refuse(name, declared, problem);
		return null;
	}

	public static void Refuse(string name, Type declared, string why)
	{
		IMpLog? log = Log;
		if (log == null || Logged.Count >= LogCap || !Logged.TryAdd(name + "|" + declared.FullName, 0))
		{
			return;
		}
		log.Warn($"codec: refused type {name} for {declared.FullName} ({why})" + (Logged.Count >= LogCap ? "; further refusals are not logged" : ""));
	}

	private static (Type? Type, string? Problem) Lookup(string name)
	{
		if (Cache.TryGetValue(name, out var hit))
		{
			return hit;
		}
		Type? type = FindLoaded(name);
		(Type?, string?) result = type == null ? (null, "not a type in a loaded assembly") : (type, Problem(type));
		if (Cache.Count < CacheCap)
		{
			Cache.TryAdd(name, result);
		}
		return result;
	}

	// Name resolution only, no allow-list (EngineCodec's data type keys apply their own rule).
	public static Type? FindLoaded(string name)
	{
		try
		{
			return Type.GetType(name, LoadedAssembly, (Assembly? a, string n, bool ignoreCase) => (a ?? typeof(object).Assembly).GetType(n, false, ignoreCase), throwOnError: false);
		}
		catch (Exception)
		{
			return null;
		}
	}

	private static Assembly? LoadedAssembly(AssemblyName wanted)
	{
		foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
		{
			if (!a.IsDynamic && string.Equals(a.GetName().Name, wanted.Name, StringComparison.OrdinalIgnoreCase))
			{
				return a;
			}
		}
		return null;
	}

	public static string? Problem(Type t, int depth = 0)
	{
		if (depth > 8)
		{
			return "nested too deeply";
		}
		if (t.IsByRef || t.IsPointer || t.ContainsGenericParameters)
		{
			return "pointer, by-ref or open generic";
		}
		if (t.IsArray)
		{
			return t.IsSZArray ? Problem(t.GetElementType()!, depth + 1) : "multi-dimensional array";
		}
		if (t.IsEnum || PlainData.Contains(t) || (t.IsPrimitive && t != typeof(nint) && t != typeof(nuint)))
		{
			return null;
		}
		if (typeof(Delegate).IsAssignableFrom(t) || typeof(MemberInfo).IsAssignableFrom(t) || typeof(Assembly).IsAssignableFrom(t) || typeof(Module).IsAssignableFrom(t)
			|| typeof(Stream).IsAssignableFrom(t) || typeof(SafeHandle).IsAssignableFrom(t))
		{
			return "delegate, reflection, stream or handle type";
		}
		if (t.IsGenericType)
		{
			Type definition = t.GetGenericTypeDefinition();
			if (!PlainGenerics.Contains(definition) && !IsGameAssembly(definition.Assembly.GetName().Name))
			{
				return definition.Name + " is neither a game type nor plain data";
			}
			foreach (Type argument in t.GetGenericArguments())
			{
				if (Problem(argument, depth + 1) is string problem)
				{
					return problem;
				}
			}
			return null;
		}
		return IsGameAssembly(t.Assembly.GetName().Name) ? null : "neither a game type nor plain data";
	}

	private static string? ConstructProblem(Type t)
	{
		if (t.IsAbstract || t.IsInterface || t.IsArray || t == typeof(string))
		{
			return "abstract or not constructible";
		}
		// GetUninitializedObject still registers the finalizer, which would then run over wire-chosen fields.
		for (Type? current = t; current != null && current != typeof(object); current = current.BaseType)
		{
			if (current.GetMethod("Finalize", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null) != null)
			{
				return "has a finalizer";
			}
		}
		return null;
	}

	private sealed class CaptureLog : IMpLog
	{
		public readonly List<string> Lines = new();

		public void Info(string message) { }

		public void Warn(string message) => Lines.Add(message);

		public void Error(string message) => Lines.Add(message);
	}

	public static void SelfTest()
	{
		static void Check(bool ok, string what)
		{
			if (!ok)
			{
				throw new InvalidOperationException("WireTypes self-test: " + what);
			}
		}
		static string N(Type t) => t.FullName + ", " + t.Assembly.GetName().Name;
		static bool IsLoaded(string assembly) => AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == assembly);
		var capture = new CaptureLog();
		IMpLog? previous = Log;
		Log = capture;
		try
		{
			Check(Resolve(N(typeof(System.Diagnostics.Process)), typeof(object)) == null, "Process accepted");
			Check(Resolve(N(typeof(FileInfo)), typeof(object)) == null, "FileInfo accepted");
			Check(Resolve(N(typeof(Action)), typeof(object)) == null, "Action accepted");
			Check(Resolve(N(typeof(Func<int>)), typeof(object)) == null, "Func<int> accepted");
			Check(Resolve(N(typeof(Type).GetType()), typeof(object)) == null, "RuntimeType accepted");
			Check(Resolve(N(typeof(nint)), typeof(object)) == null, "IntPtr accepted");
			Check(Resolve(N(typeof(List<System.Diagnostics.Process>)), typeof(object)) == null, "List<Process> accepted");
			Check(Resolve(N(typeof(Dictionary<string, Action>)), typeof(object)) == null, "Dictionary<string, Action> accepted");
			Check(Resolve(N(typeof(FileInfo[])), typeof(object)) == null, "FileInfo[] accepted");
			Check(Resolve(N(typeof(List<>)), typeof(object)) == null, "open List<> accepted");
			Check(Resolve(N(typeof(int).MakePointerType()), typeof(object)) == null, "int* accepted");
			Check(Resolve("Evil.Gadget, EvilAssembly", typeof(object)) == null, "unknown assembly accepted");
			bool mailLoaded = IsLoaded("System.Net.Mail");
			Check(Resolve("System.Net.Mail.SmtpClient, System.Net.Mail", typeof(object)) == null, "unloaded SmtpClient accepted");
			Check(mailLoaded || !IsLoaded("System.Net.Mail"), "a wire name loaded an assembly from disk");
			Check(FindLoaded(N(typeof(List<int>.Enumerator))) == typeof(List<int>.Enumerator), "nested generic name not found");
			Check(Resolve(N(typeof(List<int>.Enumerator)), typeof(object)) == null, "List<int>.Enumerator accepted");
			Type stateMachine = typeof(object).Assembly.GetTypes().First(t => t.DeclaringType != null && t.Name.StartsWith('<') && !t.ContainsGenericParameters);
			Check(FindLoaded(N(stateMachine)) == stateMachine, "compiler-generated nested name not found");
			Check(Resolve(N(typeof(System.Collections.IList)), typeof(object), construct: true) == null, "interface accepted for construction");

			Check(Resolve(N(typeof(int)), typeof(object)) == typeof(int), "int refused");
			Check(Resolve(N(typeof(string)), typeof(string)) == typeof(string), "string refused");
			Check(Resolve(N(typeof(Type)), typeof(Type)) == typeof(Type), "Type refused");
			Check(Resolve(N(typeof(Dictionary<string, int[]>)), typeof(IReadOnlyDictionary<string, int[]>)) != null, "Dictionary<string, int[]> refused");
			Check(Resolve(N(typeof(List<(int, string)>)), typeof(IEnumerable<(int, string)>)) != null, "List<(int, string)> refused");
			Check(Resolve(N(typeof(ImmutableArray<Guid>)), typeof(ImmutableArray<Guid>?)) != null, "ImmutableArray<Guid> refused");
			Check(Resolve(N(typeof(HashSet<DayOfWeek>)), typeof(object)) != null, "HashSet<enum> refused");
			Check(Resolve(N(typeof(KeyValuePair<long, decimal?>)), typeof(object)) != null, "KeyValuePair refused");
			Check(Resolve(N(typeof(List<int>)), typeof(string)) == null, "List<int> accepted into a string slot");

			Check(IsGameAssembly("SpaceEngineers2") && IsGameAssembly("Game2.Simulation") && IsGameAssembly("VRage.Library") && IsGameAssembly("VRage.DCS"), "game assembly refused");
			Check(!IsGameAssembly("VRage.Library.Generator") && !IsGameAssembly("VRage.Scripting") && !IsGameAssembly("VRage.ContentPipeline.Scripting") && !IsGameAssembly("Game2.AutoTests") && !IsGameAssembly("Y51_SE2Online") && !IsGameAssembly("System.Private.CoreLib"), "tool assembly accepted");

			Check(capture.Lines.Exists(l => l.StartsWith("codec: refused type " + N(typeof(System.Diagnostics.Process)) + " for System.Object", StringComparison.Ordinal)), "no refusal log line");
			for (int i = 0; i < CacheCap + 100; i++)
			{
				Resolve("Junk.T" + i + ", Junk", typeof(object));
			}
			Check(CacheCount <= CacheCap, "type cache not capped");
			Check(capture.Lines.Count <= LogCap, "refusal log not capped");
		}
		finally
		{
			Log = previous;
		}
	}
}
