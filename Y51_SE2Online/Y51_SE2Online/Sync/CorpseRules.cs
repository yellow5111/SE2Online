namespace Y51_SE2Online.Sync;

/// <summary>
/// f75: who moves a dead character on a joined game. SE2 4.0.0.3165 lets every game simulate its own corpse: the server marks
/// it NoAutoSync and stops its motion, each game's CharacterDeathClientComponent marks it IsSimulated and turns the death
/// ragdoll on with RagdollDrivesWorldTransform. Without that data Havok drives the ragdoll to the entity every tick and reads its
/// pose back only when the (switched off) animation gives a new one, so the corpse froze in its last living pose.
/// </summary>
public static class CorpseRules
{
	public const string KillSwitch = "ragdollfix:off";

	/// <summary>A joined game's own death ragdoll moves the body. The kill switch keeps 0.18.1: the body follows the server.</summary>
	public static bool SimulateHere(bool killSwitch, bool localRagdollsArg) => !killSwitch || localRagdollsArg;

	/// <summary>A server motion sample is not applied to a body this game's ragdoll drives: the playback would write the server's
	/// transform over the ragdoll's every tick.</summary>
	public static bool IgnoreSample(bool server, bool ragdollDrives) => !server && ragdollDrives;
}
