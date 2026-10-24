using System;

namespace Y51_SE2Online.Sync;

public static class SyncProtocol
{
	public const ushort Version = 21;

	public const ushort HostClientId = 1;

	public const int BlobMessageBytes = 1048576;

	public const int ControlLifecycleLimit = 8192;

	public const int StateMessageBytes = 1100;

	public const uint SessionEntityNetId = 1u;

	public static bool IsSyncMessage(ReadOnlySpan<byte> data)
	{
		if (data.Length > 0 && data[0] >= 32)
		{
			return data[0] <= 95;
		}
		return false;
	}
}
