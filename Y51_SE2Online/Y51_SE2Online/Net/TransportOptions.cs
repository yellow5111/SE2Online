namespace Y51_SE2Online.Net;

public sealed class TransportOptions
{
	public int FragmentPayload = 32768;

	public int BulkHighWaterBytes = 65536;

	public int BulkHighWaterLanesBytes = 1048576;

	public int MaxBulkMessageBytes = 268435456;

	/// <summary>
	/// Largest bulk message a host accepts from a client, and the most a client sends. The largest honest one is a
	/// blueprint loaded into a projector (an EntityBundle in a client signal); a client refuses anything larger.
	/// </summary>
	public int MaxBulkFromClientBytes = 33554432;

	/// <summary>
	/// Largest message a compressed bulk envelope from a client may inflate to. Client bulk traffic is signal, edit and
	/// story batches, a few KiB to a few hundred KiB; anything larger is sent uncompressed.
	/// </summary>
	public int MaxInflatedFromClientBytes = 4194304;

	/// <summary>The same for envelopes from the host: world parts and snapshots run to a few MiB.</summary>
	public int MaxInflatedFromHostBytes = 33554432;

	public int PingIntervalMs = 1000;

	public int PingTimeoutMs = 30000;

	public int HelloTimeoutMs = 15000;

	public int StateAckDelayMs = 30;

	public int StateAckTimeoutMs = 2000;

	public int MaxEarlyFrames = 16;

	public int MaxEarlyBytes = 65536;

	/// <summary>Largest bulk message a peer may send while <see cref="MpTransport.BulkGate"/> says it is not admitted yet.</summary>
	public int MaxBulkBytesBeforeAdmit = 65536;

	public int MaxProtocolErrors = 16;

	public ulong LocalId;

	/// <summary>Random per game process; tells a reconnect apart from a second instance on the same account.</summary>
	public ulong InstanceId = (ulong)System.Random.Shared.NextInt64(1, long.MaxValue);

	public byte Role;

	public string LocalName = "";

	public string PluginVersion = "";

	public string GameVersion = "";
}
