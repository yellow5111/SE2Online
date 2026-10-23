using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Keen.VRage.DCS.Builders;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Serialization.Binary;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>
/// Guards in the game's replication core for what SE2 Online's units carry (per-unit isolation itself is the transport's:
/// a signal, story, data value or object batch that cannot be read or applied is skipped alone, Engine/Y51PeerTransport).
/// </summary>
internal static class ResilientReplication
{
	// A replicated list (a world's contracts, a screen's rows) changed on the server by index: when this game's copy is
	// shorter (an item that came and went before it arrived), the remove failed half-way, the story faulted and the copy
	// drifted further. A change that does not fit the copy is skipped whole.
	private static bool CollectionChangePrefix(object __instance, Keen.VRage.Multiplayer.Replications.ReplicatedCollectionChange change)
	{
		if (!MpPlugin.Online) return true;
		if (Traverse.Create(__instance).Property("Source").GetValue() is not System.Collections.IList list) return true;
		bool fits = change.Kind switch
		{
			Keen.VRage.Multiplayer.Replications.CollectionChangeKind.Add => change.StartingIndex >= 0 && change.StartingIndex <= list.Count,
			Keen.VRage.Multiplayer.Replications.CollectionChangeKind.Remove => change.StartingIndex >= 0 && change.StartingIndex + change.RemoveCount <= list.Count,
			Keen.VRage.Multiplayer.Replications.CollectionChangeKind.Replace => change.StartingIndex >= 0 && change.StartingIndex + (change.NewItems?.Count ?? 0) <= list.Count,
			_ => true,
		};
		if (fits) return true;
		System.Threading.Interlocked.Increment(ref SignalsSkipped);
		UnitFailureLog.Note("list change skipped", __instance.GetType().Name + " " + change.Kind, $"at {change.StartingIndex}, this game's copy has {list.Count} items");
		GameLog.Instance.WarnOnce("collection-change-skipped", $"replication: skipped a list change ({change.Kind} at {change.StartingIndex}) that does not fit this game's copy ({list.Count} items)");
		return false;
	}

	// A network value naming a component of an entity whose copy here has no component with those tags: Keen indexes the
	// component list with -1 and the whole chunk is lost. Says which entity and component, once per kind.
	private static void ComponentRefPrefix(ref Keen.VRage.DCS.Serialization.SerializedComponent? from)
	{
		if (from is not { Entity: { } entity } value) return;
		var composition = entity.GetComposition();
		if (composition == null || Keen.VRage.DCS.ObjectBuilders.EntityObjectBuilderFunctions.TryGetIndexOf(composition, value.Tags.AsSpan(), out _) >= 0) return;
		string tags = string.Join(",", value.Tags);
		GameLog.Instance.WarnOnce("component-ref-" + composition.DebugNameShort + tags, $"replication: the server named component [{tags}] of {entity} ({composition.DebugNameShort}), which this game's copy of it does not have");
	}

	public static long Dropped;

	public static void Install(Harmony harmony)
	{
		// Each guard on its own: one that a game update (or a bad patch) breaks must not take the others down with it.
		Guard(harmony, "list changes", AccessTools.Method(AccessTools.Inner(typeof(Keen.VRage.Multiplayer.Replications.ObservableObjectReplicationSessionComponent), "ClientObservedCollection"), "UpdateFromRemote"),
			prefix: nameof(CollectionChangePrefix));
		Guard(harmony, "component reference names", AccessTools.Method(typeof(Keen.VRage.DCS.Serialization.ComponentProxySerializer), "Convert",
			new[] { typeof(Keen.VRage.Library.Serialization.SerializationContext), typeof(Keen.VRage.DCS.Serialization.SerializedComponent?).MakeByRefType(), typeof(Component).MakeByRefType() }),
			prefix: nameof(ComponentRefPrefix));
	}

	private static void Guard(Harmony harmony, string what, System.Reflection.MethodBase? target, string? prefix = null, string? finalizer = null)
	{
		try
		{
			if (target == null) throw new MissingMethodException(what);
			harmony.Patch(target, prefix: prefix == null ? null : new HarmonyMethod(typeof(ResilientReplication), prefix),
				finalizer: finalizer == null ? null : new HarmonyMethod(typeof(ResilientReplication), finalizer));
		}
		catch (Exception ex) { GameLog.Instance.Warn($"resilient replication: {what} guard not installed ({ex.GetType().Name}: {ex.Message})"); }
	}

	public static long SignalsSkipped;


}
