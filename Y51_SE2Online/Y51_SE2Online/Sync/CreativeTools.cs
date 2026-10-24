using System;
using System.Collections.Generic;
using System.Linq;

namespace Y51_SE2Online.Sync;

/// <summary>
/// Admin creative tools (Admin panel, per player) on a survival world. The game's welder only looks at the world's own
/// game mode, so a creative admin still needed every component. The server decides from its own state alone: the
/// identity is in the game's personal creative set (the server set it on an admin's request) and is, right now, a
/// connected SE2 Online admin or the hosting player. Nothing a client sends turns it on.
/// </summary>
public static class CreativeTools
{
	/// <summary>Whether a weld by this tool may skip components: a hand tool, held by a free welder (<see cref="FreeWelders"/>).</summary>
	public static bool FreeWeld(bool hosting, bool handTool, long owner, IReadOnlySet<long> freeWelders) => hosting && handTool && owner != 0 && freeWelders.Contains(owner);

	/// <summary>Identities that are personally creative and also connected admins (or the hosting player) right now.</summary>
	public static HashSet<long> FreeWelders(IEnumerable<long> creative, Func<long, bool> connectedAdmin) => new(creative.Where(i => i != 0 && connectedAdmin(i)));

	/// <summary>Personally creative identities that are no longer admins (demoted, or not known as admins on this server):
	/// the server turns their creative tools off, so placing and pasting without resources ends with the role.</summary>
	public static List<long> Revoke(IEnumerable<long> creative, Func<long, bool> admin) => creative.Where(i => !admin(i)).ToList();
}
