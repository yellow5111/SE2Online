using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Keen.Game2.Simulation.GameSystems.BlockPlacement.Projections;
using Keen.VRage.Library.Serialization.Binary;
using Keen.VRage.Library.Serialization.Serializers;
using Keen.VRage.Library.Utils;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>
/// SE2 4.0's generated serializer for ProjectionSourceComponent.ChildRef has no members (its one field is an inline
/// array), so over Keen's out-of-process transport every projection block reference arrives empty. The server's
/// AddChild/UpdateChild/RemoveChild signals then fail on players (ChildRef.GetParent on an empty hierarchy) and a
/// welded or deleted ghost block stays in the player's projection; a player's DeleteProjectedBlock reaches the server
/// as "Expected Block target". The reference now travels as its depth and indices. Both ends run this plugin.
/// Every binary entry point of the serializer is patched: a signal argument goes through the boxed
/// Serialize(object)/DeserializeInto(ref object) pair, a struct member or story argument through the typed pair, and
/// the typed ones are small enough for the JIT to inline into their callers, past a patch on them alone.
/// </summary>
internal static class ProjectionRefs
{
	public static long Written, Read;

	private static bool _loggedWrite, _loggedRead;

	public static void Install(Harmony harmony)
	{
		Type childRef = typeof(ProjectionSourceComponent.ChildRef), writer = typeof(BinarySerializationContext).MakeByRefType(), reader = typeof(BinaryDeserializationContext).MakeByRefType();
		int patched = 0;
		foreach (MethodInfo method in AccessTools.GetDeclaredMethods(typeof(ProjectionSourceComponent.ChildRef.Serializer)))
		{
			ParameterInfo[] p = method.GetParameters();
			if (p.Length < 2 || p[0].ParameterType != writer && p[0].ParameterType != reader) continue;
			string prefix;
			if (p[0].ParameterType == writer) prefix = p[1].ParameterType == childRef.MakeByRefType() ? nameof(WriteTyped) : nameof(WriteBoxed);
			else if (p[1].ParameterType == childRef.MakeByRefType()) prefix = nameof(ReadTyped);
			else if (p[1].ParameterType == typeof(object).MakeByRefType()) prefix = nameof(ReadBoxed);
			else if (method.ReturnType == childRef) prefix = nameof(ReadResult);
			else continue;
			harmony.Patch(method, prefix: new HarmonyMethod(typeof(ProjectionRefs), prefix));
			patched++;
		}
		if (patched < 6) throw new MissingMethodException("ChildRef.Serializer", $"binary entry points ({patched} found)");
	}

	private static bool WriteTyped(ref BinarySerializationContext destination, ref ProjectionSourceComponent.ChildRef value)
	{
		if (!MpPlugin.Online) return true;
		Write(ref destination, value);
		return false;
	}

	private static bool WriteBoxed(ref BinarySerializationContext destination, object? value)
	{
		if (!MpPlugin.Online) return true;
		Write(ref destination, value is ProjectionSourceComponent.ChildRef r ? r : default);
		return false;
	}

	private static bool ReadTyped(ref BinaryDeserializationContext source, ref ProjectionSourceComponent.ChildRef value)
	{
		if (!MpPlugin.Online) return true;
		value = ReadRef(ref source);
		return false;
	}

	private static bool ReadBoxed(ref BinaryDeserializationContext source, ref object? value)
	{
		if (!MpPlugin.Online) return true;
		value = ReadRef(ref source);
		return false;
	}

	private static bool ReadResult(ref BinaryDeserializationContext source, ref ProjectionSourceComponent.ChildRef __result)
	{
		if (!MpPlugin.Online) return true;
		__result = ReadRef(ref source);
		return false;
	}

	private static void Write(ref BinarySerializationContext destination, ProjectionSourceComponent.ChildRef value)
	{
		var ints = ImmutableSingleton<Int32Serializer>.Instance;
		ReadOnlySpan<int> hierarchy = value.Hierarchy;
		int depth = hierarchy.Length;
		ints.Serialize(ref destination, in depth);
		for (int i = 0; i < depth; i++) ints.Serialize(ref destination, in hierarchy[i]);
		Written++;
		if (_loggedWrite) return;
		_loggedWrite = true;
		GameLog.Instance.Info($"projection refs: first block reference written, depth {depth}");
	}

	private static ProjectionSourceComponent.ChildRef ReadRef(ref BinaryDeserializationContext source)
	{
		var ints = ImmutableSingleton<Int32Serializer>.Instance;
		int depth = ints.Deserialize(ref source);
		if ((uint)depth > 3) throw source.Exception($"projection block reference depth {depth}");
		Span<int> hierarchy = stackalloc int[depth];
		for (int i = 0; i < depth; i++) hierarchy[i] = ints.Deserialize(ref source);
		Read++;
		if (!_loggedRead)
		{
			_loggedRead = true;
			GameLog.Instance.Info($"projection refs: first block reference read, depth {depth}");
		}
		return new ProjectionSourceComponent.ChildRef(hierarchy);
	}
}
