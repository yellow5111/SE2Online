using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Keen.VRage.DCS.Components;
using Keen.VRage.DCS.Utils;
using Keen.VRage.Library.Definitions;
using Keen.VRage.Library.Memory;
using Keen.VRage.Library.Reflection;
using Keen.VRage.Library.Utils;
using Keen.VRage.Multiplayer.Data;
using Keen.VRage.Multiplayer.Replay;
using Keen.VRage.Multiplayer.Replications.InProcess;
using Keen.VRage.Multiplayer.Sessions;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.Engine;

/// <summary>How one signal argument sits in the game's command buffer, and so how it crosses.</summary>
internal enum ArgKind : byte
{
	/// <summary>A null object reference.</summary>
	Null = 0,
	/// <summary>An object reference (a class, a struct with references, a boxed value): serialized by its type's own serializer.</summary>
	Object = 1,
	/// <summary>An unmanaged value written inline: serialized by its type's serializer, written back as its bytes.</summary>
	Inline = 2,
	/// <summary>An unmanaged nullable written inline; the value (if any) as <see cref="Inline"/>.</summary>
	Nullable = 3,
	/// <summary>A definition, by its id.</summary>
	Definition = 4,
	/// <summary>A span of unmanaged elements, serialized as an array and written back as the span's bytes.</summary>
	Span = 5,
	/// <summary>An entity, by its network id (written back as an object reference).</summary>
	Entity = 6,
}

/// <summary>One argument: its kind, its value, and the declared type the command buffer layout needs for inline values.</summary>
internal readonly record struct OwnArg(ArgKind Kind, object? Value, Type? Inline = null);

/// <summary>One recorded signal: the game's signal key (its replay trampoline's name) and its arguments in order.</summary>
internal sealed class OwnSignal(string name, OwnArg[] args)
{
	public readonly string Name = name;
	public readonly OwnArg[] Args = args;

	/// <summary>The entity the signal is raised on (the first argument).</summary>
	public object? Target => Args.Length > 0 ? Args[0].Value : null;

	/// <summary>Whether every entity the signal names has a network id (else no peer could resolve it).</summary>
	public bool NamesOnly(OwnIds ids)
	{
		foreach (OwnArg arg in Args)
			if (arg.Kind == ArgKind.Entity && ids.TryGetId(arg.Value!) == null && ids.TryGetRetiredId(arg.Value!) == null) return false;
		return true;
	}
}

/// <summary>
/// Reads a command buffer's signals into <see cref="OwnSignal"/>s and writes received ones back into a command buffer,
/// through the game's own per-signal reader table (the replay recorder's) and command layout. The values are SE2 Online's
/// to carry; nothing of the game's network transport is involved.
/// </summary>
internal sealed class OwnSignals : IFrameSerializer
{
	private static readonly Lazy<(ReplicationCommandBuffer.ReplayTable<FrameSerializerRef> Read, Dictionary<string, SignalInfo> Write)> Tables =
		new(ReplayRecorderTableBuilder.BuildReaderTables, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

	private readonly List<OwnSignal> _frame = new();
	private readonly List<OwnArg> _args = new();
	private string? _name;
	private bool _recording;

	public long Discarded;

	/// <summary>Every signal of a command buffer this side would forward, in order.</summary>
	public List<OwnSignal> Record(ReplicationCommandBuffer buffer)
	{
		_frame.Clear();
		_recording = true;
		try
		{
			var context = new FrameSerializerRef(this);
			buffer.Replay(ref context, Tables.Value.Read);
			return new List<OwnSignal>(_frame);
		}
		finally
		{
			_recording = false;
			_frame.Clear();
			Clear();
		}
	}

	/// <summary>Whether this game knows the signal (a newer or modded peer may raise one it lacks).</summary>
	public static bool Knows(string name) => Tables.Value.Write.ContainsKey(name);

	/// <summary>
	/// Writes the signal into the buffer in the game's command layout; false when this game has no such signal or the
	/// arguments are not exactly what the game's own writer for it takes (count, kind and type of each), so a peer's bytes
	/// can never make the command buffer read past a command or another command out of it.
	/// </summary>
	public static bool Write(ReplicationCommandBuffer buffer, OwnSignal signal)
	{
		if (!Tables.Value.Write.TryGetValue(signal.Name, out SignalInfo info) || !Shapes.Fits(signal)) return false;
		int size = 8;
		foreach (OwnArg arg in signal.Args) size += SizeOf(arg);
		ref byte target = ref buffer.AllocWriteBuffer(size);
		nint offset = 0;
		CBMarshal.Write(ref target, ref offset, size, info.CommandEnumValue);
		CBMarshal.Write(ref target, ref offset, size, info.SignalId);
		foreach (OwnArg arg in signal.Args)
		{
			switch (arg.Kind)
			{
			case ArgKind.Null:
				CBMarshal.WriteObject(ref target, ref offset, size, buffer, null);
				break;
			case ArgKind.Object:
			case ArgKind.Definition:
			case ArgKind.Entity:
				CBMarshal.WriteObject(ref target, ref offset, size, buffer, arg.Value);
				break;
			case ArgKind.Inline:
			case ArgKind.Nullable:
				CBMarshal.WriteDynamic(ref target, ref offset, size, Raw.Of(arg));
				break;
			case ArgKind.Span:
				ReplicationCommandBuffer.BlittableSpanPayload.Serialize(ref target, ref offset, size, Raw.OfArray((Array)arg.Value!));
				break;
			default:
				throw new InvalidOperationException("unknown argument kind " + arg.Kind);
			}
		}
		return true;
	}

	private static int SizeOf(OwnArg arg) => arg.Kind switch
	{
		ArgKind.Null or ArgKind.Object or ArgKind.Definition or ArgKind.Entity => CBMarshal.OBJECT_PAYLOAD_SIZE,
		ArgKind.Inline or ArgKind.Nullable => Raw.SizeOf(arg),
		ArgKind.Span => ReplicationCommandBuffer.BlittableSpanPayload.GetPayloadSize(Raw.OfArray((Array)arg.Value!)),
		_ => throw new InvalidOperationException("unknown argument kind " + arg.Kind),
	};

	/// <summary>Replays one signal into the session (scene thread), as the game replays its own inbound signals; false when
	/// this game has no such signal.</summary>
	public static bool Replay(OwnHost host, OwnSignal signal)
	{
		Singleton<PoolManager>.Instance.Borrow<ReplicationCommandBuffer>(out var buffer);
		try
		{
			if (!Write(buffer, signal)) return false;
			var context = new ReplayContextRef(host.Replay, host.IsServer);
			buffer.Replay(ref context, host.Core.ReplayTable);
			return true;
		}
		finally
		{
			buffer.Clear();
			Singleton<PoolManager>.Instance.Return(buffer);
		}
	}

	// --- IFrameSerializer: the game's reader table hands every forwarded signal's arguments here ---

	public bool StartSignal(string signalName, Entity owningEntity)
	{
		_name = signalName;
		_args.Clear();
		return false;
	}

	public void SetStorage(IRecordingStorage recording) { }

	public void ClearStorage() { }

	public bool AddArgument<T>(in T value)
	{
		Type declared = typeof(T);
		Type type = value?.GetType() ?? declared;
		if (!ReplicatedDataValidator.IsSerializable(type)) return Refuse("argument of type " + type.FullName);
		if (Nullable.GetUnderlyingType(declared) != null)
		{
			if (Raw.IsUnmanaged(declared)) { _args.Add(new OwnArg(ArgKind.Nullable, value, declared)); return true; }
			_args.Add(value == null ? new OwnArg(ArgKind.Null, null) : new OwnArg(ArgKind.Object, value));
			return true;
		}
		if (value == null) { _args.Add(new OwnArg(ArgKind.Null, null)); return true; }
		if (value is Definition definition) { _args.Add(new OwnArg(ArgKind.Definition, definition)); return true; }
		if (value is Entity entity) { _args.Add(new OwnArg(ArgKind.Entity, entity)); return true; }
		// A value type in an object slot is stored boxed; a value type slot without references is stored inline.
		if (declared.IsValueType && Raw.IsUnmanaged(declared)) { _args.Add(new OwnArg(ArgKind.Inline, value, declared)); return true; }
		_args.Add(new OwnArg(ArgKind.Object, value));
		return true;
	}

	bool IFrameSerializer.AddArgument<T>(in T value) => AddArgument(in value);

	// Only the game's in-process data signal carries a payload, and it never crosses to another game.
	public bool AddPayload(int typeId, ReadOnlySpan<byte> bytes) => Refuse("payload");

	public bool AddSpanPayload<T>(ReadOnlySpan<T> value) where T : unmanaged
	{
		if (!ReplicatedDataValidator.IsSerializable(typeof(T[]))) return Refuse("span of " + typeof(T).FullName);
		_args.Add(new OwnArg(ArgKind.Span, value.ToArray(), typeof(T)));
		return true;
	}

	public void Serialize()
	{
		if (_recording && _name != null) _frame.Add(new OwnSignal(_name, _args.ToArray()));
		Clear();
	}

	public void Discard()
	{
		Discarded++;
		Clear();
	}

	public void Deserialize(ReplicationCommandBuffer buffer, string signalName, List<object?> arguments)
		=> throw new NotSupportedException("SE2 Online writes signals with OwnSignals.Write");

	private bool Refuse(string what)
	{
		GameLog.Instance.WarnOnce("own-signal-refused-" + _name + what, $"engine: signal {Short(_name)} has a {what} that cannot cross; it stays on this side");
		return false;
	}

	private void Clear()
	{
		_name = null;
		_args.Clear();
	}

	public static string Short(string? name) => name == null ? "?" : name.StartsWith("Replay_", StringComparison.Ordinal) ? name[7..].Replace("_Trampoline", "") : name;

	/// <summary>The parameters of the game's own writer of each signal (its [Command] trampoline): what a received signal must match.</summary>
	internal static class Shapes
	{
		private static Dictionary<string, Type[]>? _byName;
		private static readonly object Gate = new();

		public static long Refused;

		public static Type[]? Of(string signal)
		{
			lock (Gate)
			{
				_byName ??= Build();
				if (_byName.TryGetValue(signal, out var parameters)) return parameters;
				// A module loaded since (a mod) may have brought it.
				if (!Knows(signal)) return null;
				_byName = Build();
				return _byName.GetValueOrDefault(signal);
			}
		}

		private static Dictionary<string, Type[]> Build()
		{
			var table = new Dictionary<string, Type[]>(StringComparer.Ordinal);
			foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
			{
				if (assembly.IsDynamic) continue;
				Module[] modules;
				try { modules = assembly.GetModules(); }
				catch (Exception) { continue; }
				foreach (var module in modules)
				{
					Type? writers;
					try { writers = module.GetCustomAttribute<Keen.VRage.Multiplayer.Replications.ReplicationCommandBufferModule>()?.WritersReadersType; }
					catch (Exception) { continue; }
					if (writers == null) continue;
					foreach (var method in writers.GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public))
					{
						if (!method.Name.StartsWith("Write_", StringComparison.Ordinal) || !method.Name.EndsWith("_Trampoline", StringComparison.Ordinal)
							|| !method.IsDefined(typeof(Keen.VRage.DCS.Contracts.CommandAttribute), false)) continue;
						table["Replay_" + method.Name["Write_".Length..]] = method.GetParameters().Select(p => p.ParameterType.IsByRef ? p.ParameterType.GetElementType()! : p.ParameterType).ToArray();
						// (Sync properties take their value as `in T`: the command layout is the same as for T.)
					}
				}
			}
			return table;
		}

		/// <summary>Whether every argument is what the writer's parameter at its place takes.</summary>
		public static bool Fits(OwnSignal signal)
		{
			var parameters = Of(signal.Name);
			bool fits = parameters != null && parameters.Length == signal.Args.Length;
			for (int i = 0; fits && i < parameters!.Length; i++) fits = Fits(parameters[i], signal.Args[i]);
			if (!fits && System.Threading.Interlocked.Increment(ref Refused) == 1)
				GameLog.Instance.Warn($"engine: a signal ({Short(signal.Name)}) did not have the arguments the game's signal takes; refused (further ones counted)");
			return fits;
		}

		private static bool Fits(Type parameter, OwnArg arg)
		{
			if (parameter.IsByRefLike && parameter.IsGenericType)
			{
				var definition = parameter.GetGenericTypeDefinition();
				return (definition == typeof(ReadOnlySpan<>) || definition == typeof(Span<>)) && arg.Kind == ArgKind.Span && arg.Inline == parameter.GetGenericArguments()[0];
			}
			if (Nullable.GetUnderlyingType(parameter) is { } inner)
			{
				if (Raw.IsUnmanaged(inner)) return arg.Kind == ArgKind.Nullable && arg.Inline == parameter;
				return arg.Kind == ArgKind.Null || arg.Kind == ArgKind.Object && arg.Value?.GetType() == inner;
			}
			if (parameter.IsValueType)
			{
				if (Raw.IsUnmanaged(parameter)) return arg.Kind == ArgKind.Inline && arg.Value?.GetType() == parameter;
				return arg.Kind == ArgKind.Object && arg.Value?.GetType() == parameter;
			}
			return arg.Kind switch
			{
				ArgKind.Null => true,
				ArgKind.Object or ArgKind.Entity or ArgKind.Definition => parameter.IsInstanceOfType(arg.Value),
				_ => false,
			};
		}
	}

	/// <summary>Raw bytes of unmanaged values, as the command buffer stores them inline.</summary>
	internal static class Raw
	{
		private static readonly ConcurrentDictionary<Type, bool> Unmanaged = new();
		private static readonly ConcurrentDictionary<Type, Func<object?, byte[]>> Writers = new();
		private static readonly ConcurrentDictionary<Type, Func<Array, byte[]>> ArrayWriters = new();
		private static readonly MethodInfo ValueDef = typeof(Raw).GetMethod(nameof(ValueBytes), BindingFlags.Static | BindingFlags.NonPublic)!;
		private static readonly MethodInfo NullableDef = typeof(Raw).GetMethod(nameof(NullableBytes), BindingFlags.Static | BindingFlags.NonPublic)!;
		private static readonly MethodInfo ArrayDef = typeof(Raw).GetMethod(nameof(ArrayBytes), BindingFlags.Static | BindingFlags.NonPublic)!;

		/// <summary>A value type without references (what the game's command layout writes inline).</summary>
		public static bool IsUnmanaged(Type type) => Unmanaged.GetOrAdd(type, t =>
		{
			var inner = Nullable.GetUnderlyingType(t) ?? t;
			return inner.IsValueType && !(bool)typeof(RuntimeHelpers).GetMethod(nameof(RuntimeHelpers.IsReferenceOrContainsReferences))!.MakeGenericMethod(inner).Invoke(null, null)!;
		});

		public static byte[] Of(OwnArg arg)
		{
			Type declared = arg.Inline ?? throw new InvalidOperationException("an inline argument without its type");
			return Writers.GetOrAdd(declared, t =>
			{
				var inner = Nullable.GetUnderlyingType(t);
				var method = inner != null ? NullableDef.MakeGenericMethod(inner) : ValueDef.MakeGenericMethod(t);
				return method.CreateDelegate<Func<object?, byte[]>>();
			})(arg.Value);
		}

		public static int SizeOf(OwnArg arg) => Of(arg).Length;

		public static byte[] OfArray(Array array) => ArrayWriters.GetOrAdd(array.GetType().GetElementType()!, t => ArrayDef.MakeGenericMethod(t).CreateDelegate<Func<Array, byte[]>>())(array);

		private static byte[] ValueBytes<T>(object? boxed) where T : struct
		{
			T value = boxed is T v ? v : default;
			return MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in value)).ToArray();
		}

		private static byte[] NullableBytes<T>(object? boxed) where T : struct
		{
			T? value = boxed is T v ? v : null;
			return MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<T?, byte>(ref value), Unsafe.SizeOf<T?>()).ToArray();
		}

		private static byte[] ArrayBytes<T>(Array array) where T : struct => MemoryMarshal.AsBytes(((T[])array).AsSpan()).ToArray();
	}
}
