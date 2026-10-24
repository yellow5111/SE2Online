namespace Y51_SE2Online.Net;

public sealed class PeerStats
{
	public PeerId Peer;

	public bool Ready;

	public string RemoteName = "";

	public ulong RemoteSteamId;

	public string RemotePluginVersion = "";

	public string RemoteGameVersion = "";

	public long ConnectedMs;

	public double LastRttMs = -1.0;

	public double MinRttMs = double.MaxValue;

	public double MaxRttMs;

	public double AvgRttMs;

	public long PingsSent;

	public long PongsReceived;

	public long LastPongMs;

	public ChannelStats[] Channels = new ChannelStats[3];

	public int BulkQueuedMessages;

	public long BulkQueuedBytes;

	public int ReassemblyInProgress;

	/// <summary>Bytes in of the messages still being reassembled, and their declared lengths.</summary>
	public long ReassemblyBytes;

	public long ReassemblyTotalBytes;

	public long SeqGaps;

	public long SeqDuplicates;

	public long ProtocolErrors;

	public long StateLate;

	public long StateMissing;

	public long StateDuplicates;

	public long StateAcked;

	public long StateLost;

	public int StateInFlight;

	public long SendBusy;

	/// <summary>Reliable bytes the link (Steam or TCP) still holds for this peer, beyond the transport's bulk queue.</summary>
	public int LinkPendingBytes;

	public PeerStats Clone()
	{
		PeerStats peerStats = (PeerStats)MemberwiseClone();
		peerStats.Channels = (ChannelStats[])Channels.Clone();
		return peerStats;
	}
}
