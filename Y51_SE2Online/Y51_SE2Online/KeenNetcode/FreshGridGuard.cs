using System;
using System.Collections.Generic;
using HarmonyLib;
using Keen.Game2.Simulation.GameSystems.TrashRemoval;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.VRage.DCS.Components;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>
/// The game's grid PCU trash removal deletes the smallest grids that are not pinned once a world is over its PCU limit. A
/// world area (a station, an encounter) pins its grids only after its spawn completes, and in multiplayer that waits until
/// every player has built them: the cleaner ran in between and deleted parts of a station that had just spawned (vanilla
/// single player pins at once). A grid still staged, or in the scene for less than <see cref="FreshMs"/>, counts as pinned
/// for the cleaner; it never deletes anything vanilla would keep, only later.
/// </summary>
internal static class FreshGridGuard
{
	private const long FreshMs = 60_000;

	private static readonly Dictionary<Entity, long> FirstSeen = new(ReferenceEqualityComparer.Instance);
	private static readonly List<Entity> Added = new();
	private static long _pruneMs;
	public static long Protected;

	public static void Install(Harmony harmony) => harmony.Patch(AccessTools.Method(typeof(GridPCUTrashRemovalSessionComponent), "GridTrashRemoval"),
		prefix: new HarmonyMethod(typeof(FreshGridGuard), nameof(Prefix)), finalizer: new HarmonyMethod(typeof(FreshGridGuard), nameof(Finalizer)));

	private static void Prefix(GridPCUTrashRemovalSessionComponent __instance)
	{
		long now = MpClock.NowMs();
		Added.Clear();
		if (MpPlugin.App?.ServerSession is not { } session) return;
		foreach (Entity grid in session.GetEntitiesOfType<CubeGridComponent>())
		{
			if (!FirstSeen.TryGetValue(grid, out long seen)) FirstSeen[grid] = seen = now;
			bool staged = grid.Data.Has<Keen.VRage.DCS.CoreData.StagingTag>();
			if ((staged || now - seen < FreshMs) && __instance._pinnedGrids.Add(grid)) Added.Add(grid);
		}
		Protected += Added.Count;
		if (now < _pruneMs) return;
		_pruneMs = now + 60_000;
		var gone = new List<Entity>();
		foreach (var (grid, _) in FirstSeen) if (grid.Scene == null) gone.Add(grid);
		foreach (var grid in gone) FirstSeen.Remove(grid);
	}

	private static Exception? Finalizer(GridPCUTrashRemovalSessionComponent __instance, Exception? __exception)
	{
		foreach (var grid in Added) __instance._pinnedGrids.Remove(grid);
		Added.Clear();
		return __exception;
	}
}
