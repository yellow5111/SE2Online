using System;
using System.Collections.Generic;

namespace Y51_SE2Online.Core;

/// <summary>
/// SE2 2.4's LogicalFloraClientSector.Spawn skips the whole sector when <c>renderInstances - removedItems &lt;= 0</c>, but its
/// render instances already leave out every item removed before the sector was generated, so those are counted twice.
/// A sector that lost about half its trees (cut, run over, cleared for a contract or encounter) is never drawn, while
/// the host's colliders for the rest stay: invisible trees that block players. Only removals still among the render
/// instances may be subtracted.
/// </summary>
internal static class FloraRemovals
{
	/// <summary>The removed ids that are still render instances, or null when that is all of them already.</summary>
	internal static HashSet<short>? StillRendered(ReadOnlySpan<short> renderIds, HashSet<short> removed)
	{
		if (removed.Count == 0) return null;
		var still = new HashSet<short>();
		foreach (short id in renderIds)
		{
			if (removed.Contains(id)) still.Add(id);
		}
		return still.Count == removed.Count ? null : still;
	}

	internal static void SelfTest()
	{
		short[] render = { 1, 2, 3 };
		var still = StillRendered(render, new HashSet<short> { 2, 7, 8, 9 });
		if (still == null || still.Count != 1 || !still.Contains(2) || render.Length - still.Count <= 0)
			throw new Exception("flora: removals made before generation must not hide the rest of the sector");
		if (StillRendered(render, new HashSet<short> { 1, 3 }) != null || StillRendered(render, new HashSet<short>()) != null)
			throw new Exception("flora: removals that are all render instances are left as they are");
	}
}
