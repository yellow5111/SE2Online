namespace Y51_SE2Online.Sync;

public enum ClientPhase : byte
{
	JoinSent = 1,
	SnapshotReceived = 2,
	ClientGameStarting = 3,
	SessionReady = 4,
	WorldApplied = 5,
	CameraReady = 6,
	CharacterReady = 8,
	Progress = 7,
	Warning = 9,
	Error = 10,
	/// <summary>The player's character is not seated on this client although it rejoined seated; the host stands it up.</summary>
	NotSeated = 11
}
