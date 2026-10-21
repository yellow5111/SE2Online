namespace Y51_SE2Online.Sync;

public sealed partial class HostSession
{
	// SE2 4.0 sends no desync checks, so a report answers nothing the host asked: it is only counted.
	private void HandleDesyncReport(RemoteClient client, byte[] data)
	{
		if (client.Status != ClientStatus.Synced) return;
		var (_, mismatches) = DesyncReportMessage.Decode(data);
		DesyncStats.Count(ref DesyncStats.Reports);
		DesyncStats.Count(ref DesyncStats.Mismatches, mismatches.Count);
	}
}
