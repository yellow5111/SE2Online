using System;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Keen.Game2.Simulation.WorldObjects.Characters;
using Keen.Game2.Simulation.WorldObjects.Characters.Stats;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.BlockStateModifiers;
using Keen.Game2.Simulation.WorldObjects.CubeGrids.ResourceDistribution.Resources;
using Keen.Game2.Simulation.WorldObjects.Tools;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.Library.Definitions;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Library.Utils;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// Applies ResourceMultipliers on the hosting game. Suits: the per-stat rates in the life support, jetpack fuel and
/// tool definitions, which the server-only components read every tick. Blocks: every powerable block's grid demand
/// (base use plus thrusters, production and wheels), scaled in PowerableBlockComponent.UpdateConsumedResource by the
/// block's consumed resource type.
/// </summary>
internal static class ResourceMultiplierRules
{
	// Block demand never drops to exactly zero: a zero-demand sink would change how the grid reports supply.
	private static readonly FixedPoint MinimumDemand = (FixedPoint)0.001f;

	private static readonly ConditionalWeakTable<object, float[]> Originals = new();
	private static ResourceMultipliers _applied = ResourceMultipliers.Default;
	private static DefinitionManager? _appliedTo;

	internal static void Install(Harmony harmony) => harmony.Patch(
		AccessTools.Method(typeof(PowerableBlockComponent), nameof(PowerableBlockComponent.UpdateConsumedResource)),
		prefix: new HarmonyMethod(typeof(ResourceMultiplierRules), nameof(UpdateConsumedResourcePrefix)));

	/// <summary>Host world tick: re-applies when the multipliers or the loaded definitions change.</summary>
	internal static void Apply(Session session)
	{
		var current = ResourceMultipliers.Current;
		var defs = Singleton<DefinitionManager>.Instance;
		if (current == _applied && ReferenceEquals(defs, _appliedTo)) return;
		if (defs == null) return;
		int definitions = ScaleDefinitions(defs, current);
		_applied = current;
		_appliedTo = defs;
		int blocks = Refresh(session);
		GameLog.Instance.Info($"resource usage: {current.Describe()} ({definitions} suit/tool definitions, {blocks} powered blocks updated)");
	}

	/// <summary>
	/// Hosting ended: puts the game's own rates back so single player afterwards is unchanged. The definitions are plain
	/// field writes; a world that stays loaded needs <see cref="Refresh"/> on its server thread. False if nothing was scaled.
	/// </summary>
	internal static bool Restore()
	{
		var defs = _appliedTo;
		if (_applied.IsDefault || defs == null) return false;
		ScaleDefinitions(defs, ResourceMultipliers.Default);
		_applied = ResourceMultipliers.Default;
		GameLog.Instance.Info("resource usage: hosting ended, game defaults restored");
		return true;
	}

	internal static int Refresh(Session session)
	{
		int blocks = 0;
		foreach (var entity in session.GetEntitiesOfType<PowerableBlockComponent>().ToList())
		{
			entity.Get<PowerableBlockComponent>().UpdateConsumedResource();
			blocks++;
		}
		return blocks;
	}

	private static int ScaleDefinitions(DefinitionManager defs, ResourceMultipliers m)
	{
		int count = 0;
		foreach (var life in defs.GetDefinitionsOfTypeAndDescendants<CharacterLifeSupportComponentDefinition>())
		{
			float[] original = Original(life, () => life.LifeSupportStats.Select(s => s.Consumption).ToArray());
			if (original.Length != life.LifeSupportStats.Length) continue;
			var scaled = life.LifeSupportStats.ToBuilder();
			for (int i = 0; i < scaled.Count; i++)
			{
				var stat = scaled[i];
				stat.Consumption = original[i] * (float)m.ForStat(stat.Stat?.Guid ?? Guid.Empty);
				scaled[i] = stat;
			}
			life.LifeSupportStats = scaled.MoveToImmutable();
			count++;
		}
		foreach (var jetpack in defs.GetDefinitionsOfTypeAndDescendants<JetpackFuelComponentDefinition>())
		{
			count += Scale(jetpack, new[] { jetpack.StationaryFuel, jetpack.PrecisionFlightFuel, jetpack.BoostFlightFuel }, m);
		}
		foreach (var tool in defs.GetDefinitionsOfTypeAndDescendants<ToolConsumesStatsDefinition>())
		{
			count += Scale(tool, tool.StatsToConsume.ToArray(), m);
		}
		return count;
	}

	private static int Scale(object owner, StatAmount?[] amounts, ResourceMultipliers m)
	{
		float[] original = Original(owner, () => amounts.Select(a => a?.Amount ?? 0).ToArray());
		if (original.Length != amounts.Length) return 0;
		for (int i = 0; i < amounts.Length; i++)
		{
			if (amounts[i] is { } amount) amount.Amount = original[i] * (float)m.ForStat(amount.Stat?.Guid ?? Guid.Empty);
		}
		return 1;
	}

	private static float[] Original(object owner, Func<float[]> read)
	{
		lock (Originals) return Originals.GetValue(owner, _ => read());
	}

	// SE2's UpdateConsumedResource with the summed demand scaled; keep in step with the game's copy.
	private static bool UpdateConsumedResourcePrefix(PowerableBlockComponent __instance)
	{
		var m = ResourceMultipliers.Current;
		if (m.IsDefault) return true;
		ResourceUnitTyped? consumed = __instance.Definition.ConsumedResource;
		if (!consumed.HasValue) return false;
		ResourceUnitTyped unit = consumed.GetValueOrDefault();
		if (__instance.Functional && __instance.Enabled)
		{
			FixedPoint sum = FixedPoint.Zero;
			foreach (int index in __instance._resourceModifiers)
			{
				sum += ((IConsumedResourceModifier)__instance.Entity.Components[index]).RecomputeConsumedResource();
			}
			double scale = m.ForResource(unit.Type?.Guid ?? Guid.Empty);
			if (scale != 1 && sum > FixedPoint.Zero)
			{
				FixedPoint scaled = sum * (float)scale;
				sum = scaled > MinimumDemand ? scaled : MinimumDemand;
			}
			if (!(__instance._appliedConsumption == sum))
			{
				unit.Amount = sum;
				__instance._sink.Set(unit.ToResourceUnit().MakeSink(__instance.Definition.ConsumptionPriority));
				__instance._appliedConsumption = sum;
			}
		}
		else
		{
			__instance._sink.TryRemove<ResourceNode>();
			__instance._appliedConsumption = -1;
		}
		return false;
	}
}
