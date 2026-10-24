using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.Annotations.Signals;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Memory;
using Keen.VRage.Library.Utils;
using Keen.VRage.Multiplayer.Annotations;
using Keen.VRage.Multiplayer.Replications;
using Keen.VRage.Multiplayer.Replications.InProcess;
using Keen.VRage.Multiplayer.Sessions;
using Keen.VRage.Multiplayer.Transport;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.Engine;

/// <summary>
/// Sync properties the server sets itself, for every player.
/// (a) SE2 4.0 drops the server's replication handler for a sync property when the server also has a plain local handler
/// for it (HandlerConstructor.InspectLocalHandler marks it local-only, PostProcess removes the Write trampoline), so such a
/// property never reached players: ConnectedStatusComponent.Connected (KillOnDisconnectServerComponent listens to it), water
/// pumps and dumps. The server keeps the handler for every sync property that is not [LocalOnly].
/// (b) A player that receives an entity (join, streaming in) builds it from the server's object builder, which some
/// properties are not in (CubeGridComponent.ParkingState) or which is older than a change sent while the player's game was
/// still building it (the change named an entity it did not have yet and was dropped). Once the player acknowledges the
/// batch, it gets the server's current value of every sync property of the grids, characters and other non-block entities
/// in it, of the block properties that changed since the batch was made (all of them on every block would be hundreds
/// of thousands of values for a large world), and of every block property the server's component does not save (a value it
/// computes when the block enters the scene, such as a battery's slot fill mask, never reaches a player's object builder).
/// -y51online:syncprops:off turns both off.
/// </summary>
internal static class SyncProperties
{
	internal static readonly bool Off = MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "syncprops:off");

	// Each player's game computes these for itself by design (OreDetectorBlockClientComponent / OreDetectorToolClientComponent
	// call SetEnabledLocally); the server's value would fight it.
	private static readonly HashSet<string> ClientComputed = new(StringComparer.Ordinal)
	{
		"Keen.VRage.Voxels.Components.OreDetectorComponent+EnabledProperty",
	};

	public static long Relayed;
	private static readonly HashSet<Type> Kept = new();

	// When the server last changed any sync property of an entity (server scene thread).
	private static readonly Dictionary<Entity, long> ChangedMs = new();
	private static bool _tracking;
	private static long _pruneMs;

	/// <summary>Once, with the server's replication tables: every sync property writer notes when it ran for an entity.</summary>
	public static void Track(ReplicationCoreComponent core)
	{
		if (Off || _tracking || _harmony is not { } harmony) return;
		_tracking = true;
		int patched = 0;
		var postfix = new HarmonyMethod(typeof(SyncProperties), nameof(WritePostfix));
		foreach (var (type, writer) in core.WriterTable)
		{
			if (type.GetCustomAttribute<SignalSignature>()?.SyncProperty != true || writer.Info.GetParameters().FirstOrDefault()?.ParameterType != typeof(Entity)) continue;
			try { harmony.Patch(writer.Info, postfix: postfix); patched++; }
			catch (Exception ex) { GameLog.Instance.WarnOnce("syncprop-track-" + type.Name, $"sync properties: changes of {type} are not tracked ({ex.GetType().Name}: {ex.Message})"); }
		}
		GameLog.Instance.Info($"sync properties: {patched} property writers tracked for players that join or stream in");
	}

	private static void WritePostfix(Entity __0)
	{
		long now = MpClock.NowMs();
		lock (ChangedMs)
		{
			ChangedMs[__0] = now;
			if (now < _pruneMs) return;
			_pruneMs = now + 60_000;
			foreach (var stale in ChangedMs.Where(kv => now - kv.Value > 300_000 || kv.Key.Scene == null).Select(kv => kv.Key).ToList()) ChangedMs.Remove(stale);
		}
	}

	/// <summary>The entity's values go to players at the next refresh as if they had just changed.</summary>
	internal static void Touch(Entity entity)
	{
		lock (ChangedMs) ChangedMs[entity] = MpClock.NowMs();
	}

	private static bool ChangedSince(Entity entity, long sinceMs)
	{
		lock (ChangedMs) return ChangedMs.TryGetValue(entity, out long at) && at >= sinceMs;
	}

	private static Harmony? _harmony;

	public static void Install(Harmony harmony)
	{
		if (Off) return;
		_harmony = harmony;
		var postProcess = typeof(HandlerConstructor).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
			.Single(m => m.Name.EndsWith("PostProcess", StringComparison.Ordinal));
		harmony.Patch(postProcess, prefix: new HarmonyMethod(typeof(SyncProperties), nameof(PostProcessPrefix)));
	}

	private static void PostProcessPrefix(HandlerConstructor __instance)
	{
		if (__instance._scene is not ServerSceneContext) return;
		var kept = __instance._localOnlySignals.Where(t => t.GetCustomAttribute<SignalSignature>()?.SyncProperty == true
			&& !t.IsDefined(typeof(LocalOnlyAttribute), false) && !ClientComputed.Contains(t.FullName ?? "")).ToList();
		foreach (var type in kept)
		{
			__instance._localOnlySignals.Remove(type);
			lock (Kept)
				if (Kept.Add(type)) GameLog.Instance.Info($"sync properties: {type.FullName} now reaches players (the game kept it on the server)");
		}
		Relayed += kept.Count;
	}

	/// <summary>Scene thread: the signals that set every sync property of the roots and everything under them to the
	/// server's current value, as this peer's recorder would send them.</summary>
	public static List<OwnSignal> Current(Session session, ReplicationCoreComponent core, OwnSignals recorder, IEnumerable<Entity> roots, long sinceMs, bool notSavedOnly = false)
	{
		var signals = new List<OwnSignal>();
		if (Off) return signals;
		var commands = session.SessionComponents.Get<ReplicationCommandBufferSessionComponent>();
		Singleton<PoolManager>.Instance.Borrow<ReplicationCommandBuffer>(out var refresh);
		var live = commands.Buffer;
		commands.Buffer = refresh;
		try { WriteValues(core, roots, sinceMs, notSavedOnly); }
		finally
		{
			commands.Buffer = live;
		}
		try { signals.AddRange(recorder.Record(refresh)); }
		finally
		{
			refresh.Clear();
			Singleton<PoolManager>.Instance.Return(refresh);
		}
		return signals;
	}

	/// <summary>Scene thread: every sync property of the roots and everything under them written at the server's current value
	/// into the session's command buffer (Keen's Write trampolines; they skip exclusive and unobserved entities).</summary>
	/// <param name="notSavedOnly">Only the values no object builder carries (sent with the batch itself, so the player's game
	/// has them as the entity is built; the rest follows the batch's acknowledgement).</param>
	public static int WriteValues(ReplicationCoreComponent core, IEnumerable<Entity> roots, long sinceMs, bool notSavedOnly = false)
	{
		int written = 0;
		List<string>? failed = null;
		{
			using var subtree = new Buffer<Entity>(Allocator.Pool, "Y51 sync property refresh");
			var args = new object?[2];
			foreach (var root in roots)
			{
				if (root.Scene == null) continue;
				subtree.Clear();
				ReplicationServerSessionComponent.CollectSubtree(root, subtree);
				foreach (var entity in subtree.Data)
				{
					bool unchangedBlock = notSavedOnly || entity.TryGet<Keen.Game2.Simulation.WorldObjects.CubeBlocks.CubeBlockComponent>() != null && !ChangedSince(entity, sinceMs);
					if (!unchangedBlock) written += WriteState(core, entity, args);
					var properties = entity.CompositionData.Properties.Properties;
					if (properties == null) continue;
					foreach (var property in properties)
					{
						if (unchangedBlock && !NotSaved(property.PropertyType)) continue;
						if (ClientComputed.Contains(property.PropertyType.FullName ?? "") || !core.WriterTable.TryGetValue(property.PropertyType, out var writer)) continue;
						args[0] = entity;
						args[1] = property.Storage.GetValue(entity.Components[property.ComponentIndex]);
						// A block's unsaved value still at its type's default is what the player's game starts from too.
						if (unchangedBlock && IsDefault(args[1])) continue;
						try { writer.Info.Invoke(null, args); written++; }
						catch (Exception ex)
						{
							lock (RefreshFailed)
								if (RefreshFailed.Add(property.PropertyType)) (failed ??= new()).Add($"{property.PropertyType.Name} ({ex.GetType().Name}: {ex.InnerException?.Message ?? ex.Message})");
						}
					}
				}
			}
		}
		// One line for the property types that failed for the first time in this pass (it was one line each).
		if (failed != null) GameLog.Instance.Warn($"sync properties: {failed.Count} property type(s) could not be refreshed (each listed once): {string.Join("; ", failed)}");
		return written;
	}

	private static readonly HashSet<Type> RefreshFailed = new();

	private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, object?> Defaults = new();

	private static bool IsDefault(object? value) => value == null
		|| value.GetType().IsValueType && value.Equals(Defaults.GetOrAdd(value.GetType(), t => Activator.CreateInstance(t)));

	private static readonly Dictionary<Type, bool> NotSavedCache = new();

	/// <summary>A sync property whose server component has no object builder member of its name (the BitMask of
	/// ResourceContainerInventorySyncServerComponent, set in OnAddedToScene): a player only ever gets it as a change.</summary>
	internal static bool NotSaved(Type property)
	{
		lock (NotSavedCache)
		{
			if (NotSavedCache.TryGetValue(property, out bool known)) return known;
			bool notSaved = false;
			if (property.DeclaringType is { } component && property.Name.EndsWith("Property", StringComparison.Ordinal))
			{
				string name = property.Name[..^"Property".Length];
				var saved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				for (var t = component; t != null && t != typeof(object); t = t.BaseType)
					foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
						if ((m.IsDefined(typeof(Keen.VRage.DCS.Annotations.SerializerAttribute), false) || m.IsDefined(typeof(Keen.VRage.DCS.Annotations.InitAttribute), false))
							&& m.GetParameters().FirstOrDefault()?.ParameterType is { } ob)
							for (var o = ob; o != null && o != typeof(object); o = o.BaseType)
							{
								foreach (var f in o.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)) saved.Add(f.Name.TrimStart('_').Replace("<", "").Split('>')[0]);
								foreach (var pr in o.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)) saved.Add(pr.Name);
							}
				notSaved = !saved.Contains(name);
				if (notSaved) GameLog.Instance.Info($"sync properties: {component.Name}.{name} is not in the object builder; it goes to players with every block they receive");
			}
			return NotSavedCache[property] = notSaved;
		}
	}

	// State the game keeps outside sync properties and sends only as a change signal, with no (or a stale) copy in what a
	// player builds the entity from: the same signal at the current value. Character stats (health, oxygen, energy,
	// hydrogen, food): a player that joined after a character was hurt saw it at full health.
	private static int WriteState(ReplicationCoreComponent core, Entity entity, object?[] args)
	{
		int written = 0;
		if (entity.TryGet<Keen.Game2.Simulation.WorldObjects.Characters.Stats.StatCollectionComponent>() is { } stats
			&& core.WriterTable.TryGetValue(typeof(Keen.Game2.Simulation.WorldObjects.Characters.Stats.StatCollectionComponent.UpdateStatSignal), out var update))
		{
			var three = new object?[3];
			foreach (var (stat, value) in stats._values.ToList())
			{
				three[0] = entity;
				three[1] = stat;
				three[2] = value;
				try { update.Info.Invoke(null, three); written++; }
				catch (Exception ex) { GameLog.Instance.WarnOnce("syncprop-stats", $"sync properties: stats could not be refreshed ({ex.GetType().Name}: {ex.InnerException?.Message ?? ex.Message})"); }
			}
		}
		return written;
	}


}
