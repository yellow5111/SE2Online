using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>
/// A player's request (a network story) moves to the server with the fields its hop carries, but not the compiler's
/// closure object (&lt;&gt;8__N) a story makes before its first wait: the server resumed with it null and faulted on its first
/// use. Creating a block group did exactly that on every server (NullReferenceException in RequestCreateNewGroup), with
/// SE2 4.0's own netcode as well. A restored story gets an empty closure object, as the code before the hop would have
/// made; what the story put in it before moving does not cross either way.
/// </summary>
internal static class StoryClosures
{
	private static readonly ConcurrentDictionary<Type, FieldInfo[]> Closures = new();

	public static long Filled;

	public static void Install(Harmony harmony)
	{
		var plan = AccessTools.TypeByName("Keen.VRage.Multiplayer.NetworkStories.NetworkStorySerializationPlan") ?? throw new TypeLoadException("NetworkStorySerializationPlan");
		var restore = AccessTools.Method(plan, "Restore") ?? throw new MissingMethodException("NetworkStorySerializationPlan.Restore");
		harmony.Patch(restore, postfix: new HarmonyMethod(typeof(StoryClosures), nameof(RestorePostfix)));
	}

	private static void RestorePostfix(IAsyncStateMachine __result)
	{
		if (__result == null) return;
		foreach (var field in Closures.GetOrAdd(__result.GetType(), Find))
		{
			if (field.GetValue(__result) != null) continue;
			try
			{
				// A boxed struct state machine: SetValue writes into the box the story runs from.
				field.SetValue(__result, Activator.CreateInstance(field.FieldType));
				if (System.Threading.Interlocked.Increment(ref Filled) == 1)
					GameLog.Instance.Info($"story closures: {__result.GetType().DeclaringType?.Name}.{__result.GetType().Name} arrived without its closure object; gave it a new one");
			}
			catch (Exception ex) { GameLog.Instance.WarnOnce("story-closure-" + field.FieldType.Name, $"story closures: could not make {field.FieldType.Name}: {ex.Message}"); }
		}
	}

	private static FieldInfo[] Find(Type machine) => machine.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
		.Where(f => f.Name.StartsWith("<>8__", StringComparison.Ordinal) && f.FieldType.IsClass && f.FieldType.GetConstructor(Type.EmptyTypes) != null).ToArray();
}
