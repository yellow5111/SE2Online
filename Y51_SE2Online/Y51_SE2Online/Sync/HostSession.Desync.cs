using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Y51_SE2Online.Net;

namespace Y51_SE2Online.Sync;

public sealed partial class HostSession
{
	private readonly ConcurrentQueue<(PeerId Peer, uint CheckId, List<DesyncMismatch> Mismatches)> _desyncReports = new();
	private int _desyncQueued;

	/// <summary>Synced players the desync checks run for, with the time they became synced and their character.</summary>
	public List<(PeerId Peer, ushort ClientId, string Name, long SyncedMs, uint CharacterNetId)> DesyncPeers()
	{
		lock (_sync)
			return _clients.Values.Where(c => c.Status == ClientStatus.Synced && c.Player != null)
				.Select(c => (c.Peer, c.Player.ClientId, c.Name, c.SyncedMs, c.Player.CharacterNetId)).ToList();
	}

	public bool SendDesyncCheck(PeerId peer, byte[] encoded) => PublishLifecycleOnlyTo(peer, encoded);

	public List<(PeerId Peer, uint CheckId, List<DesyncMismatch> Mismatches)> TakeDesyncReports()
	{
		var taken = new List<(PeerId, uint, List<DesyncMismatch>)>();
		while (_desyncReports.TryDequeue(out var report))
		{
			System.Threading.Interlocked.Add(ref _desyncQueued, -report.Mismatches.Count);
			taken.Add(report);
		}
		return taken;
	}

	private void HandleDesyncReport(RemoteClient client, byte[] data)
	{
		if (client.Status != ClientStatus.Synced) return;
		var (checkId, mismatches) = DesyncReportMessage.Decode(data);
		DesyncStats.Count(ref DesyncStats.Reports);
		DesyncStats.Count(ref DesyncStats.Mismatches, mismatches.Count);
		// The host only acts on what it can confirm itself; a flood just waits for the next checks.
		if (_desyncQueued + mismatches.Count > 4096) return;
		System.Threading.Interlocked.Add(ref _desyncQueued, mismatches.Count);
		_desyncReports.Enqueue((client.Peer, checkId, mismatches));
	}
}
