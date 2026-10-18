using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;
using Keen.VRage.Library.Localization;
using Keen.VRage.Library.Reflection;
using Keen.VRage.Library.Utils;
using Keen.VRage.Multiplayer.Replications;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

internal static class CommandLayouts
{
	public const string PostInvokeSignal = "Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent+PostInvokeInternalSignal";

	public const string PostDataSignal = "Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent+PostDataSignal";

	public const string RemoveDataSignal = "Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent+RemoveDataSignal";

	private static CommandLayout?[]? _byStub;

	private static Dictionary<string, CommandLayout>? _byName;

	private static readonly object Sync = new();

	public static int CallbacksAnalysed;

	public static int CallbacksMissing;

	private static readonly HashSet<Type> ForwardableStructs = new() { typeof(LocalizableText), typeof(Keen.Game2.Simulation.GameSystems.Explosions.AudioParameter?) };

	// Host to joiner only: voxel destruction craters, thruster flame classes, and the explosion decals, grid damage status,
	// camera shake and character hit effects ([Clients] handlers only). A joiner must never send these to the server.
	private static readonly HashSet<Type> DownstreamStructs = new()
	{
		typeof(Keen.VRage.Voxels.Storage.Operations.CutOutCapsulesBuilder), typeof(Keen.Game2.Simulation.WorldObjects.CubeGrids.Movement.ThrusterSyncData),
		typeof(Keen.Game2.Simulation.WorldObjects.CubeGrids.Damage.GridExplosionHitInfo), typeof(Keen.Game2.Simulation.GameSystems.ProjectileSystems.ExplosionHit),
		typeof(Keen.Game2.Simulation.WorldObjects.Characters.Stats.StatChangeData),
		// User message popups (contract completed, undo/redo, respawn notices): sent only to the player they are for.
		typeof(Keen.Game2.Simulation.GameSystems.UserMessages.UserMessage),
		// Item pickup / inventory full toasts (InventoryComponent.ItemTransferSignal): sent only to the character's player.
		typeof(Keen.Game2.Simulation.StreamedUI.Terminal.Inventories.ItemTransferData?)
	};

	private static readonly MethodInfo SizeOfMethod = typeof(Unsafe).GetMethod("SizeOf");

	private static readonly MethodInfo IsRefMethod = typeof(RuntimeHelpers).GetMethod("IsReferenceOrContainsReferences");

	public static int Count => _byStub?.Length ?? 0;

	public static CommandLayout?[] All
	{
		get
		{
			Build();
			return _byStub;
		}
	}

	public static CommandLayout? ByStub(int stub)
	{
		CommandLayout?[] all = All;
		if ((uint)stub >= (uint)all.Length)
		{
			return null;
		}
		return all[stub];
	}

	public static CommandLayout? ByName(string name)
	{
		Build();
		return _byName.TryGetValue(name, out CommandLayout? layout) ? layout : null;
	}

	public static string? Check(SignalKeyDef def)
	{
		try
		{
			CommandLayout? layout = ByName(def.Name);
			if (layout == null)
			{
				return "unknown signal in this game build";
			}
			if (!layout.Forwardable)
			{
				return "not forwardable here: " + layout.Why;
			}
			SignalKeyDef local = layout.Def(def.Key);
			if (local.PayloadBytes != def.PayloadBytes || local.RefOffsets != def.RefOffsets)
			{
				return $"layout differs (here {local.PayloadBytes} B [{local.RefOffsets}], peer {def.PayloadBytes} B [{def.RefOffsets}])";
			}
			return null;
		}
		catch (Exception ex)
		{
			return "check failed: " + ex.GetType().Name + ": " + ex.Message;
		}
	}

	private static void Build()
	{
		if (_byStub != null)
		{
			return;
		}
		lock (Sync)
		{
			if (_byStub != null)
			{
				return;
			}
			ReplicationCommandBufferIndexer.CommandBufferCommand[] table = MetadataIndexerBase<ReplicationCommandBufferIndexer>.Instance.Table;
			var byStub = new CommandLayout[table.Length];
			var byName = new Dictionary<string, CommandLayout>(StringComparer.Ordinal);
			Dictionary<Type, Dictionary<int, int>> callbacks;
			try
			{
				callbacks = ReaderCallbacks.Analyse();
			}
			catch (Exception)
			{
				callbacks = new Dictionary<Type, Dictionary<int, int>>();
			}
			for (int i = 0; i < table.Length; i++)
			{
				Type signalType = table[i].SignalType;
				if (signalType is not null && signalType.FullName != null)
				{
					CommandLayout layout = For(i, signalType.FullName, table[i].WriterInfo);
					layout.SignalType = signalType;
					if (callbacks.TryGetValue(signalType, out var translated))
					{
						layout.Translated = translated;
						CallbacksAnalysed++;
					}
					else if (layout.Kind == CommandKind.Signal)
					{
						CallbacksMissing++;
					}
					byStub[i] = layout;
					byName.TryAdd(layout.Name, layout);
				}
			}
			_byName = byName;
			_byStub = byStub;
		}
	}

	private static CommandLayout For(int stub, string name, MethodInfo? writer)
	{
		switch (name)
		{
		case PostDataSignal:
			return new CommandLayout
			{
				Stub = stub,
				Name = name,
				Kind = CommandKind.PostData,
				RefOffsets = new[] { 0, 4 }
			};
		case RemoveDataSignal:
			return new CommandLayout
			{
				Stub = stub,
				Name = name,
				Kind = CommandKind.RemoveData,
				PayloadBytes = 12,
				RefOffsets = new[] { 0, 4 }
			};
		case PostInvokeSignal:
			return new CommandLayout
			{
				Stub = stub,
				Name = name,
				Kind = CommandKind.PostInvoke,
				PayloadBytes = 8,
				RefOffsets = new[] { 0, 4 },
				Why = "closure"
			};
		default:
		{
			var layout = new CommandLayout
			{
				Stub = stub,
				Name = name,
				Kind = CommandKind.Signal
			};
			if (writer == null)
			{
				layout.Why = "no writer";
				return layout;
			}
			ParameterInfo[] parameters = writer.GetParameters();
			if (parameters.Length == 0)
			{
				layout.Why = "writer has no entity parameter";
				return layout;
			}
			if (name.StartsWith("Keen.VRage.Multiplayer.", StringComparison.Ordinal))
			{
				layout.Why = "replication infrastructure (observables, pairing): not forwarded";
				layout.PayloadBytes = -1;
			}
			var segments = new List<Segment>
			{
				new Segment(SegmentKind.Ref, 4, 0, parameters[0].ParameterType)
			};
			bool hasSpan = false;
			int offset = 4;
			var stringIdOffsets = new List<int>();
			for (int i = 1; i < parameters.Length; i++)
			{
				Type type = parameters[i].ParameterType;
				if (type.IsByRef)
				{
					type = type.GetElementType();
				}
				if (!type.IsValueType || IsGeneric(type, typeof(ImmutableArray<>)))
				{
					segments.Add(new Segment(SegmentKind.Ref, 4, i, type));
					offset += 4;
					continue;
				}
				if (IsGeneric(type, typeof(ReadOnlySpan<>)) || IsGeneric(type, typeof(Span<>)))
				{
					Type elementType = type.GetGenericArguments()[0];
					if (RuntimeHelpersIsReferenceOrContainsReferences(elementType) || ProcessLocal(elementType, 0) != null)
					{
						layout.Why = $"span of {elementType.Name} ({parameters[i].Name})";
						return layout;
					}
					var elementStringIds = new List<int>();
					CollectStringIds(elementType, 0, elementStringIds, 0);
					if (elementStringIds.Count > 0)
					{
						layout.Why = $"span of {elementType.Name} ({parameters[i].Name}) holds StringIds";
						return layout;
					}
					segments.Add(new Segment(SegmentKind.Span, 0, i, type));
					hasSpan = true;
					continue;
				}
				if (RuntimeHelpersIsReferenceOrContainsReferences(type))
				{
					offset += 4;
					segments.Add(new Segment(SegmentKind.Ref, 4, i, type));
					if (DownstreamStructs.Contains(type))
					{
						layout.DownstreamOnly = true;
					}
					else if (!ForwardableStructs.Contains(type))
					{
						layout.Why ??= $"managed struct argument {parameters[i].Name} ({type.Name})";
					}
					continue;
				}
				string? local = ProcessLocal(type, 0);
				if (local != null)
				{
					layout.Why = "argument " + parameters[i].Name + " carries " + local;
					return layout;
				}
				int size = SizeOf(type);
				var stringIds = new List<int>();
				CollectStringIds(type, 0, stringIds, 0);
				if (stringIds.Count > 0)
				{
					// Offsets after a variable-length span are not fixed, so these ids could not be patched.
					if (hasSpan)
					{
						layout.Why = "argument " + parameters[i].Name + " holds a StringId after a span";
						return layout;
					}
					foreach (int stringId in stringIds)
					{
						stringIdOffsets.Add(offset + stringId);
					}
				}
				offset += size;
				if (segments[^1].Kind == SegmentKind.Bytes)
				{
					segments[^1] = segments[^1] with { Size = segments[^1].Size + size };
				}
				else
				{
					segments.Add(new Segment(SegmentKind.Bytes, size, i, type));
				}
			}
			layout.Segments = segments.ToArray();
			layout.StringIdOffsets = stringIdOffsets.ToArray();
			if (!hasSpan)
			{
				int payloadBytes = 0;
				var refOffsets = new List<int>();
				foreach (Segment segment in segments)
				{
					if (segment.Kind == SegmentKind.Ref)
					{
						refOffsets.Add(payloadBytes);
					}
					payloadBytes += segment.Kind == SegmentKind.Ref ? 4 : segment.Size;
				}
				layout.PayloadBytes = payloadBytes;
				layout.RefOffsets = refOffsets.ToArray();
			}
			layout.Forwardable = layout.Why == null;
			return layout;
		}
		}
	}

	internal static void CollectStringIds(Type t, int baseOffset, List<int> found, int depth)
	{
		if (t == typeof(StringId))
		{
			found.Add(baseOffset);
			return;
		}
		if (!t.IsValueType || t.IsPrimitive || t.IsEnum || t.IsPointer || depth > 6)
		{
			return;
		}
		foreach (FieldInfo field in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
		{
			Type fieldType = field.FieldType;
			if (fieldType.IsValueType && !fieldType.IsPrimitive && !fieldType.IsEnum)
			{
				CollectStringIds(fieldType, baseOffset + (int)field.GetFieldOffset(), found, depth + 1);
			}
		}
	}

	private static bool IsGeneric(Type t, Type def) => t.IsGenericType && t.GetGenericTypeDefinition() == def;

	public static int SizeOf(Type t) => (int)SizeOfMethod.MakeGenericMethod(t).Invoke(null, null);

	private static bool RuntimeHelpersIsReferenceOrContainsReferences(Type t) => (bool)IsRefMethod.MakeGenericMethod(t).Invoke(null, null);

	// Names what makes a value of this type meaningless in another process (a pointer or an entity id), or null.
	private static string? ProcessLocal(Type t, int depth)
	{
		if (t.IsPointer || t == typeof(nint) || t == typeof(nuint))
		{
			return "a pointer";
		}
		if (t.FullName is "Keen.VRage.DCS.Accessors.DEntity" or "Keen.VRage.Multiplayer.EntityId" || t.Name == "EntityId")
		{
			return t.Name;
		}
		if (t.IsPrimitive || t.IsEnum || depth > 4)
		{
			return null;
		}
		foreach (FieldInfo field in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
		{
			string? found = ProcessLocal(field.FieldType, depth + 1);
			if (found != null)
			{
				return found;
			}
		}
		return null;
	}

	// Evaluates the constant size expression (ldc/sizeof combined with add/mul) the generated writer
	// computes before its first stloc, or returns -1 when the IL is anything else.
	public static int WriterAllocationSize(MethodInfo trampoline)
	{
		string writerName = trampoline.Name.EndsWith("_Trampoline", StringComparison.Ordinal) ? trampoline.Name[..^"_Trampoline".Length] : trampoline.Name;
		MethodInfo? writer = trampoline.DeclaringType?.GetMethod(writerName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		byte[]? il = writer?.GetMethodBody()?.GetILAsByteArray();
		if (writer == null || il == null || il.Length < 2)
		{
			return -1;
		}
		var stack = new Stack<long>();
		int pos = 0;
		while (pos < il.Length)
		{
			byte op = il[pos++];
			switch (op)
			{
			case 0x20: // ldc.i4
				stack.Push(BitConverter.ToInt32(il, pos));
				pos += 4;
				continue;
			case 0x16: // ldc.i4.0 .. ldc.i4.8
			case 0x17:
			case 0x18:
			case 0x19:
			case 0x1A:
			case 0x1B:
			case 0x1C:
			case 0x1D:
			case 0x1E:
				stack.Push(op - 0x16);
				continue;
			case 0x15: // ldc.i4.m1
				stack.Push(-1L);
				continue;
			case 0x1F: // ldc.i4.s
				stack.Push((sbyte)il[pos++]);
				continue;
			case 0x58: // add
			case 0x5A: // mul
			{
				if (stack.Count < 2)
				{
					return -1;
				}
				long right = stack.Pop();
				long left = stack.Pop();
				stack.Push(op == 0x58 ? left + right : left * right);
				continue;
			}
			case 0xFE: // two-byte opcode; only sizeof (FE 1C) is understood
				if (pos < il.Length && il[pos] == 0x1C)
				{
					int token = BitConverter.ToInt32(il, pos + 1);
					pos += 5;
					Type t;
					try
					{
						t = writer.Module.ResolveType(token, writer.DeclaringType?.GetGenericArguments(), writer.GetGenericArguments());
					}
					catch (Exception)
					{
						return -1;
					}
					stack.Push(SizeOf(t));
					continue;
				}
				break;
			case 0x0A: // stloc.0 .. stloc.3, stloc.s: the size expression is complete
			case 0x0B:
			case 0x0C:
			case 0x0D:
			case 0x13:
				if (stack.Count != 1)
				{
					return -1;
				}
				return (int)stack.Pop();
			}
			return -1;
		}
		return -1;
	}
}
