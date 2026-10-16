using System.Linq;
using System.Runtime.CompilerServices;
using Keen.Game2.Simulation.WorldObjects.Items;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.Library.Definitions;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Library.Utils;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// Applies InventoryMultipliers on the hosting game. Capacity is an inventory definition's MaxMass, copied into each
/// inventory's replicated IndexData.MassMax when it is created: the definitions are scaled so new inventories get the
/// new size, and existing ones are rewritten, which replicates to players. Items already over a smaller capacity stay.
/// </summary>
internal static class InventoryMultiplierRules
{
	private static readonly ConditionalWeakTable<InventoryDefinition, StrongBox<FixedPoint>> Originals = new();
	private static InventoryMultipliers _applied = InventoryMultipliers.Default;
	private static DefinitionManager? _appliedTo;

	/// <summary>Host world tick: re-applies when the multipliers or the loaded definitions change.</summary>
	internal static void Apply(Session session)
	{
		var current = InventoryMultipliers.Current;
		var defs = Singleton<DefinitionManager>.Instance;
		if (defs == null || (current == _applied && ReferenceEquals(defs, _appliedTo))) return;
		int definitions = 0;
		foreach (var definition in defs.GetDefinitionsOfTypeAndDescendants<InventoryDefinition>())
		{
			double scale = current.For(definition.Guid);
			if (scale == 1 && !Originals.TryGetValue(definition, out _)) continue;
			FixedPoint original = Originals.GetValue(definition, d => new StrongBox<FixedPoint>(d.MaxMass)).Value;
			definition.MaxMass = original * (float)scale;
			definitions++;
		}
		_applied = current;
		_appliedTo = defs;
		int inventories = Refresh(session);
		GameLog.Instance.Info($"inventory size: {current.Describe()} ({definitions} inventory definitions, {inventories} inventories resized)");
	}

	/// <summary>
	/// Hosting ended: puts the game's own capacities back so single player afterwards is unchanged. The definitions are
	/// plain field writes; a world that stays loaded needs <see cref="Refresh"/> on its server thread. False if nothing was scaled.
	/// </summary>
	internal static bool Restore()
	{
		var defs = _appliedTo;
		if (_applied.IsDefault || defs == null) return false;
		foreach (var definition in defs.GetDefinitionsOfTypeAndDescendants<InventoryDefinition>())
			if (Originals.TryGetValue(definition, out var original)) definition.MaxMass = original.Value;
		_applied = InventoryMultipliers.Default;
		GameLog.Instance.Info("inventory size: hosting ended, game defaults restored");
		return true;
	}

	internal static int Refresh(Session session)
	{
		int inventories = 0;
		foreach (var entity in session.GetEntitiesOfType<InventoryComponent>().ToList())
			foreach (var inventory in entity.Components.OfType<InventoryComponent>())
			{
				if (!Originals.TryGetValue(inventory.Definition, out _)) continue;
				ref var data = ref inventory.WriteSharedData;
				if (data.PerInventoryData[inventory.InventoryIndex].MassMax == inventory.Definition.MaxMass) continue;
				data.PerInventoryData[inventory.InventoryIndex].MassMax = inventory.Definition.MaxMass;
				inventories++;
			}
		return inventories;
	}
}
