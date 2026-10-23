using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Keen.Game2.Simulation.RuntimeSystems.Serialization;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.Core.Worlds;
using Keen.VRage.DCS.Components;
using Y51_SE2Online.Core;
using Y51_SE2Online.SyncEngine;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>
/// A world save writes entity references by id; when something saved still names an entity that is not saved itself
/// (seen on a dedicated server: a destroyed antenna block), EntityContainerSerializer.Serialize throws "Serializing dead
/// Entities" after the bundle is written, the whole save fails, and every later save fails the same way, so nothing is
/// saved until a restart throws the progress away. While online the save now goes through: such an id loads as a stub,
/// which the game's load already prunes as a lost entity (EntityProxySerializationContext.GetEntity, EntityPruner). The
/// entity and its grid are logged so the holder can be found.
/// </summary>
internal static class SaveGuard
{
	private static readonly HashSet<string> Logged = new();

	public static void Install(Harmony harmony)
	{
		harmony.Patch(AccessTools.Method(typeof(EntityContainerSerializer<WorldSessionSaveGameMetaData>), "ThrowInvalidEntities")
			?? throw new MissingMethodException("EntityContainerSerializer.ThrowInvalidEntities"),
			prefix: new HarmonyMethod(typeof(SaveGuard), nameof(Prefix)));
	}

	private static bool Prefix(IEnumerable<Entity> invalidEntities)
	{
		if (!MpPlugin.Online) return true;
		try
		{
			var lines = invalidEntities.Select(Describe).ToList();
			if (lines.Any(Logged.Add))
				GameLog.Instance.Warn("save: the world was saved without " + lines.Count + " entity(ies) something still named; that reference loads as lost: " + string.Join("; ", lines));
		}
		catch (Exception ex) { GameLog.Instance.WarnOnce("save-guard-log", "save: could not describe an unsaved entity (" + ex.GetType().Name + ")"); }
		return false;
	}

	private static string Describe(Entity e)
	{
		string text = e.DEntity + " [" + string.Join(",", e.Components.IsDefaultOrEmpty ? Enumerable.Empty<string>() : e.Components.Select(c => c.GetType().Name)) + "]";
		if (EngineCodec.IsDead(e)) return text + " removed from the world";
		try
		{
			var root = e.GetRoot();
			if (root == e) return text + " in the world, no parent";
			string name = root.TryGet<CubeGridComponent>()?.DisplayName.EvaluateText();
			return text + " in the world on " + root.DEntity + (name == null ? "" : " '" + name + "'") + (EngineCodec.IsDead(root) ? " (that grid is removed)" : "");
		}
		catch (Exception ex) { return text + " (parent unknown: " + ex.GetType().Name + ")"; }
	}
}
