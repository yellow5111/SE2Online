namespace Y51_SE2Online.Sync;

/// <summary>
/// SE2's SeatComponent.CanSeat only checks the seat. A character already in a seat (or still being put down beside the
/// one it just left) that sits again gets a second seat: SE2 asserts, keeps the first seat's charging sink running with
/// no seat behind it, and the dedicated server crashed on the next stand-up.
/// </summary>
public static class SeatRules
{
	public static bool MaySit(bool pilotSeated, bool pilotUnseating) => !pilotSeated && !pilotUnseating;
}
