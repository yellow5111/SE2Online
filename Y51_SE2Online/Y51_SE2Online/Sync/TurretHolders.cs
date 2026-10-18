using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace Y51_SE2Online.Sync;

/// <summary>
/// Host side: who holds each directly controlled turret. SE2 2.4's AutoTargetingComponent+SyncControlSignal is a
/// [Clients] signal with no addressee, and every client's AutoTargetingClientComponent.SetControl makes its own local
/// player possess the turret, so sent to everyone it moved every player's camera into the turret one player took.
/// It goes to the holder alone; controller-only turret signals are accepted only from the holder.
/// </summary>
public sealed class TurretHolders<T> where T : class
{
	public const ushort Nobody = 0, Host = 1;

	private sealed class Hold
	{
		public volatile ushort Client;
		public volatile bool Active;
	}

	private readonly ConditionalWeakTable<T, Hold> _holds = new();

	// The turrets held right now, with what the host noted about the holder when it took them.
	private readonly Dictionary<T, (ushort Client, object? Tag)> _active = new();

	/// <summary>The server's SetControl. <paramref name="actor"/>: the ClientId whose edit or story runs now (0 or 1: the host).</summary>
	public void SetControl(T turret, bool control, ushort actor, object? tag = null)
	{
		Hold hold = _holds.GetOrCreateValue(turret);
		// Release keeps the holder: its SyncControl(false) is read after this call and must still reach that player.
		if (control)
		{
			hold.Client = actor > Host ? actor : Host;
		}
		hold.Active = control;
		lock (_active)
		{
			if (control) _active[turret] = (hold.Client, tag);
			else _active.Remove(turret);
		}
	}

	/// <summary>Who the turret's SyncControl signal is for: a ClientId, <see cref="Host"/>, or <see cref="Nobody"/>.</summary>
	public ushort AddresseeOf(T? turret) => turret != null && _holds.TryGetValue(turret, out Hold? hold) ? hold.Client : Nobody;

	public bool Controls(T? turret, ushort client) => turret != null && _holds.TryGetValue(turret, out Hold? hold) && hold.Active && hold.Client == client;

	public (T Turret, ushort Client, object? Tag)[] Held()
	{
		lock (_active)
		{
			return _active.Count == 0 ? Array.Empty<(T, ushort, object?)>() : _active.Select(kv => (kv.Key, kv.Value.Client, kv.Value.Tag)).ToArray();
		}
	}

	/// <summary>Drops the turret's entry: its holder left, or the turret is gone.</summary>
	public void Forget(T turret)
	{
		_holds.Remove(turret);
		lock (_active)
		{
			_active.Remove(turret);
		}
	}
}
