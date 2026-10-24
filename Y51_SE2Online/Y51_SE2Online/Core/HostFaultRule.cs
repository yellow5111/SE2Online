using System;

namespace Y51_SE2Online.Core;

/// <summary>
/// Which story faults the server hands back to the co-op host's own game are converted the way a player's are on the
/// wire (KeenGuards.InProcessCompletionPrefix): every fault not already in that form. The game's own StoryFaultDescriptor
/// then decides skip or remote fault, exactly as for a player. Kept free of game types so the tests can run it.
/// </summary>
public static class HostFaultRule
{
	public static bool AsRemoteFault(Exception fault)
		=> fault != null && fault.GetType().Name is not ("NetworkStoryRemoteFaultException" or "SkipNetworkStoryException");
}
