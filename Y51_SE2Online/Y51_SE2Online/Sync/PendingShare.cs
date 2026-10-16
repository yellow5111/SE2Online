using System.Collections.Generic;

namespace Y51_SE2Online.Sync;

/// <summary>
/// Admission to a shared host work queue: a total cap plus a per-client cap on requests still waiting, so one client
/// can't fill the queue and crowd out other players' requests. Admit on enqueue, Release on dequeue.
/// </summary>
public sealed class PendingShare
{
	private readonly int _total, _perClient;
	private readonly Dictionary<ushort, int> _pending = new();
	private int _count;

	public PendingShare(int total, int perClient) { _total = total; _perClient = perClient; }

	public bool Admit(ushort client)
	{
		lock (_pending)
		{
			_pending.TryGetValue(client, out int mine);
			if (_count >= _total || mine >= _perClient) return false;
			_pending[client] = mine + 1;
			_count++;
			return true;
		}
	}

	public void Release(ushort client)
	{
		lock (_pending)
		{
			if (!_pending.TryGetValue(client, out int mine)) return;
			if (mine <= 1) _pending.Remove(client); else _pending[client] = mine - 1;
			_count--;
		}
	}
}
