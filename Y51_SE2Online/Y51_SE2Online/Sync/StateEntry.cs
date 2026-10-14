namespace Y51_SE2Online.Sync;

/// <param name="Root">Host-side only: the NetId of the entity's top-level root, for per-joiner streaming (0 = unknown, always sent).</param>
public readonly record struct StateEntry(uint NetId, ushort Key, uint Version, byte[] Bytes, uint HostMs = 0u, uint Root = 0u)
{
	public int WireSize => 16 + Bytes.Length;

	public const int Overhead = 16;
}
