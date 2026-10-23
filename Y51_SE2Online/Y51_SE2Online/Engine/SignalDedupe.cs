using System;
using System.Collections.Generic;
using System.Linq;
using Keen.VRage.DCS.Components;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.Engine;

/// <summary>
/// The server re-sends some state signals every frame with the same arguments (a standing character's ground material and
/// physics state, a turret holding its aim, a container's mass). Each one sets state on the player's game, so a repeat of
/// what this player last got for the same entity is not sent again. Only arguments that compare by value are trusted
/// (numbers, structs, strings, entities, definitions); a signal carrying anything else always goes.
/// -y51online:signals:all sends every repeat.
/// </summary>
internal sealed class SignalDedupe
{
	private static readonly HashSet<string> StateSignals = new(StringComparer.Ordinal)
	{
		"Replay_Keen_Game2_Simulation_WorldObjects_Characters_CharacterMovementComponent_SyncSupportingMaterialSignal_Trampoline",
		"Replay_Keen_Game2_Simulation_WorldObjects_Characters_CharacterMovementComponent_OnCharacterPhysicsStateChangedSignal_Trampoline",
		"Replay_Keen_Game2_Simulation_WorldObjects_Characters_Stats_StatCollectionComponent_UpdateStatSignal_Trampoline",
		"Replay_Keen_Game2_Simulation_WorldObjects_CubeBlocks_Weapons_AutoTargetingComponent_SyncAzimuthElevationSignal_Trampoline",
		"Replay_Keen_Game2_Simulation_WorldObjects_Weapons_ProjectileWeaponComponent_ProjectileTransformProperty_Trampoline",
		"Replay_Keen_Game2_Simulation_WorldObjects_CubeBlocks_MechanicalBlocks_MechanicalBlockCounterpartDetectionComponent_DetectedCounterpartChangedSignal_Trampoline",
		"Replay_Keen_VRage_Water_Components_WaterDomainComponent_DomainGravityUpdatedSignal_Trampoline",
		"Replay_Keen_Game2_Simulation_WorldObjects_CubeBlocks_Lcd_LcdMultiPanelComponent_SurfaceStatesProperty_Trampoline",
		"Replay_Keen_Game2_Simulation_WorldObjects_CubeBlocks_ResourceDistribution_Resources_ResourceContainerComponent_NotifyContainerMassChangedSignal_Trampoline",
	};

	private static readonly bool Off = MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "signals:all");

	/// <summary>A setter the server repeats with the same values (what IsRepeat may skip for a player).</summary>
	public static bool IsStateSignal(string name) => StateSignals.Contains(name);

	private readonly Dictionary<(string Signal, object Target), object?[]> _last = new();
	private readonly Dictionary<Entity, List<string>> _signalsOf = new(ReferenceEqualityComparer.Instance);

	public long Skipped;

	/// <summary>An entity this player no longer has, or got again: its signals go again.</summary>
	public void Forget(Entity entity)
	{
		if (!_signalsOf.Remove(entity, out var signals)) return;
		foreach (string signal in signals) _last.Remove((signal, entity));
	}

	public bool IsRepeat(OwnSignal signal, Func<Entity, bool> exact)
	{
		var args = signal.Args;
		if (Off || args.Length == 0) return false;
		string name = signal.Name;
		if (!StateSignals.Contains(name)) return false;
		var values = new object?[args.Length];
		for (int i = 0; i < values.Length; i++)
		{
			object? value = args[i].Value;
			if (!ByValue(value)) return false;
			values[i] = value;
		}
		// The first argument is the entity the signal is raised on; anything else and the repeat cannot be told apart.
		if (values[0] is not Entity target || exact(target)) return false;
		var key = (name, (object)target);
		if (_last.TryGetValue(key, out var last) && last.AsSpan().SequenceEqual(values, Comparer.Instance))
		{
			Skipped++;
			return true;
		}
		// ponytail: one entry per entity and signal for the peer's life, cleared wholesale past 20000 (removed entities
		// are never pruned one by one).
		if (_last.Count > 20000) { _last.Clear(); _signalsOf.Clear(); }
		if (_last.TryAdd(key, values))
		{
			if (!_signalsOf.TryGetValue(target, out var signals)) _signalsOf[target] = signals = new List<string>(2);
			signals.Add(name);
		}
		else _last[key] = values;
		return false;
	}

	private static bool ByValue(object? value) => value is null or string or Entity
		|| value.GetType().IsValueType
		|| value.GetType().Name.EndsWith("Definition", StringComparison.Ordinal);

	private sealed class Comparer : IEqualityComparer<object?>
	{
		public static readonly Comparer Instance = new();
		public new bool Equals(object? a, object? b) => object.Equals(a, b);
		public int GetHashCode(object? o) => o?.GetHashCode() ?? 0;
	}
}
