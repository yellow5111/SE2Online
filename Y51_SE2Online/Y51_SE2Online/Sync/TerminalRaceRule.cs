using System;
using System.Collections.Generic;
using System.Linq;

namespace Y51_SE2Online.Sync;

/// <summary>Decisions for the terminal screen races (KeenNetcode.TerminalRaces), kept free of game types so the tests can run them.</summary>
internal static class TerminalRaceRule
{
	/// <summary>Puts the inventory filled for an entity into the screen's map unless the entity already has one. Returns the
	/// inventory to let go: null when it went in (or is the one already there), else this later one; the first one stays.</summary>
	public static TValue Spare<TKey, TValue>(Dictionary<TKey, TValue> map, TKey key, TValue container) where TKey : notnull where TValue : class
		=> map.TryAdd(key, container) || ReferenceEquals(map[key], container) ? null : container;

	/// <summary>The removed blocks whose grid the panel still shows, in order. The caller's sequence is read once.</summary>
	public static List<T> Shown<T>(IEnumerable<T> blocks, Func<T, bool> gridShown, out int orphans)
	{
		var all = blocks.ToList();
		var shown = all.FindAll(b => gridShown(b));
		orphans = all.Count - shown.Count;
		return shown;
	}
}
