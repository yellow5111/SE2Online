using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Keen.VRage.DCS.Builders;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Serialization.Binary;
using Keen.VRage.Multiplayer.Replications.OutOfProcess;
using Keen.VRage.Multiplayer.Transport.OutOfProcess;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>
/// SE2 4.0 sends every reliable data change of a tick (block state, inventories, data removals) to a peer as one unit; if
/// any entry names an entity the peer no longer (or not yet) has, the whole unit is skipped ("Transport unit failed network
/// pairing, skipping"). The server routinely does that: an entity destroyed in the same tick its data is removed (a weather
/// event, a crashed ship's blocks moving to new grids), so a player silently lost whole ticks of changes and the world
/// drifted apart until nothing they did took effect. While a data unit is read, an unknown entity now becomes a
/// placeholder and only the entries naming it are dropped.
/// </summary>
internal static class ResilientReplication
{
	[ThreadStatic] private static int _readingData;

	[ThreadStatic] private static bool _placeholderUsed;

	private static Entity? _placeholder;

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
		GameLog.Instance.WarnOnce("collection-change-skipped", $"replication: skipped a list change ({change.Kind} at {change.StartingIndex}) that does not fit this game's copy ({list.Count} items)");
		return false;
	}

	// A network batch is a list of chunks (data changes, signals, stories). Keen skips a chunk that names an object it has
	// no pair for, but any other chunk it cannot read (a signal naming a part of an object this game only holds as a stub,
	// as turrets retargeting after a faction change did) threw out of the whole batch and closed the game. Such a chunk is
	// now skipped the same way.
	private static Exception? ChunkReadFinalizer(Exception? __exception)
	{
		if (!MpPlugin.Online) return __exception;
		if (__exception == null || __exception is Keen.VRage.Library.Serialization.SerializationException { InnerException: Keen.VRage.Multiplayer.Replications.InProcess.NoMatchingLocalPairingException })
			return __exception;
		System.Threading.Interlocked.Increment(ref SignalsSkipped);
		string what = (__exception.InnerException ?? __exception).GetType().Name + ": " + __exception.Message.Split('\n')[0];
		GameLog.Instance.WarnOnce("chunk-unreadable", "replication: skipped a network chunk this game could not read (" + what + "); the rest of the batch applies");
		return new Keen.VRage.Library.Serialization.SerializationException("skipped unreadable chunk",
			new Keen.VRage.Multiplayer.Replications.InProcess.NoMatchingLocalPairingException(what));
	}

	public static long Dropped;

	private static long _pairingSkips, _pairingLogMs;

	// Keen logs every skipped chunk with its full exception; a stream naming an entity this game lacks did that ~77 times
	// a second (40 MB in 5 minutes). The first is logged in full, then one count line per 30 s. (A transpiler on the
	// logging method's caller broke its exception block and with it every guard here; this filters the log call itself.)
	private static bool PairingWarningPrefix(string __1)
	{
		if (!MpPlugin.Online) return true;
		string message = __1;
		if (message == null || !message.StartsWith("Transport unit failed network pairing", StringComparison.Ordinal)) return true;
		long count = System.Threading.Interlocked.Increment(ref _pairingSkips), now = MpClock.NowMs();
		if (count == 1) { _pairingLogMs = now; return true; }
		if (now - _pairingLogMs < 30_000) return false;
		_pairingLogMs = now;
		GameLog.Instance.Warn($"replication: Keen skipped {count} network unit(s) naming objects this game does not have so far (one line per 30 s); latest: {message.Split('\n')[0]}");
		return false;
	}

	private static readonly ConcurrentDictionary<Type, FieldInfo?> TargetFields = new();

	public static void Install(Harmony harmony)
	{
		var batch = AccessTools.Inner(typeof(OutOfProcessPeerTransport), "OutOfProcessDataBatch");
		var serializer = AccessTools.Inner(batch, "Serializer");
		int patched = 0;
		foreach (var method in AccessTools.GetDeclaredMethods(serializer))
		{
			var first = method.GetParameters().FirstOrDefault()?.ParameterType;
			if (!method.Name.StartsWith("Deserialize", StringComparison.Ordinal) || first?.GetElementType() != typeof(BinaryDeserializationContext)) continue;
			harmony.Patch(method, prefix: new HarmonyMethod(typeof(ResilientReplication), nameof(EnterData)), finalizer: new HarmonyMethod(typeof(ResilientReplication), nameof(LeaveData)));
			patched++;
		}
		if (patched == 0) throw new MissingMethodException("OutOfProcessDataBatch binary deserializers not found");
		// Each guard on its own: one that a game update (or a bad patch) breaks must not take the others down with it.
		Guard(harmony, "placeholder entities", AccessTools.Method(typeof(ReplicationEntitySerializationContext), nameof(ReplicationEntitySerializationContext.GetEntity)),
			finalizer: nameof(GetEntityFinalizer));
		Guard(harmony, "stale data entries", AccessTools.Method(typeof(OutOfProcessPeerTransport), "ProcessIncomingData"), prefix: nameof(ProcessIncomingDataPrefix));
		Guard(harmony, "unreadable chunks", AccessTools.Method(AccessTools.Inner(typeof(OutOfProcessPeerTransport), "BinaryArchiveChunksSequenceReader"), "Read"),
			finalizer: nameof(ChunkReadFinalizer));
		Guard(harmony, "list changes", AccessTools.Method(AccessTools.Inner(typeof(Keen.VRage.Multiplayer.Replications.ObservableObjectReplicationSessionComponent), "ClientObservedCollection"), "UpdateFromRemote"),
			prefix: nameof(CollectionChangePrefix));
		// The pipeline's ProcessSignal, not the transport's ProcessRecordedSignal: that small wrapper is inlined into
		// ProcessMessage, so a patch on it never ran.
		Guard(harmony, "pairing warning rate", AccessTools.Method(typeof(Keen.VRage.Library.Diagnostics.LogExtensions), nameof(Keen.VRage.Library.Diagnostics.LogExtensions.Warning),
			new[] { typeof(Keen.VRage.Library.Diagnostics.Log), typeof(string) }), prefix: nameof(PairingWarningPrefix));
		Guard(harmony, "signals", AccessTools.Method(typeof(OutOfProcessSignalPipeline), nameof(OutOfProcessSignalPipeline.ProcessSignal)), finalizer: nameof(SignalFinalizer));
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

	// A signal replayed onto an entity that changed under it (a crashed ship's block that lost its power component in the
	// same tick) threw a NullReferenceException in the generated replay and the crash handler closed the player's game.
	// That one signal is skipped; the entity's next state update corrects it.
	private static Exception? SignalFinalizer(Exception? __exception)
	{
		if (!MpPlugin.Online) return __exception;
		if (__exception is null or OutOfMemoryException or StackOverflowException) return __exception;
		SignalsSkipped++;
		GameLog.Instance.WarnOnce("signal-skipped-" + __exception.GetType().Name, $"replication: skipped a signal that failed to apply ({__exception.GetType().Name}: {__exception.Message}); further ones are counted, not logged");
		return null;
	}

	private static void EnterData() => _readingData++;

	private static Exception? LeaveData(Exception? __exception)
	{
		_readingData--;
		return __exception;
	}

	private static Exception? GetEntityFinalizer(Exception? __exception, int entityId, ref Entity __result)
	{
		if (!MpPlugin.Online) return __exception;
		if (_readingData <= 0 || __exception is not Keen.VRage.Multiplayer.Replications.InProcess.NoMatchingLocalPairingException) return __exception;
		__result = _placeholder ??= EntityAllocator.AllocateStub();
		_placeholderUsed = true;
		GameLog.Instance.WarnOnce("replication-placeholder", $"replication: a data change named entity NetworkId({entityId}) this game does not have (removed the same tick); that entry is dropped, the rest of the tick applies");
		return null;
	}

	private static void ProcessIncomingDataPrefix(object dataBatch)
	{
		if (!_placeholderUsed || _placeholder == null) return;
		_placeholderUsed = false;
		var placeholder = _placeholder;
		var batchType = dataBatch.GetType();
		if (batchType.GetField("Removals")?.GetValue(dataBatch) is IList removals) Drop(removals, placeholder);
		if (batchType.GetField("DataLists")?.GetValue(dataBatch) is IList lists)
		{
			foreach (object? list in lists)
			{
				if (list?.GetType().GetField("Data")?.GetValue(list) is IList data) Drop(data, placeholder);
			}
		}
	}

	private static void Drop(IList entries, Entity placeholder)
	{
		for (int i = entries.Count - 1; i >= 0; i--)
		{
			object? entry = entries[i];
			if (entry == null) continue;
			var field = TargetFields.GetOrAdd(entry.GetType(), t => t.GetField("Target", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
			if (field != null && ReferenceEquals(field.GetValue(entry), placeholder))
			{
				entries.RemoveAt(i);
				Dropped++;
			}
		}
	}
}
