using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Y51_SE2Online.Sync;

/// <summary>
/// Host side, per player and kind (suit colour, suit skin). SE2's suit menu applies every entry the pointer hovers,
/// so a burst is normal; past the budget the newest change waits and goes out when the budget returns, so the look
/// the player settled on is never lost.
/// </summary>
public sealed class SuitRate<T> where T : class
{
	public const double Burst = 20, PerSecond = 8;

	private readonly Dictionary<(ushort Client, byte Kind), (double Tokens, long AtMs, T? Waiting)> _state = new();

	/// <summary>True: apply <paramref name="change"/> now. False: it waits, replacing an older waiting change.</summary>
	public bool Offer(ushort client, byte kind, T change, long nowMs)
	{
		lock (_state)
		{
			var (tokens, _) = Refill(client, kind, nowMs);
			bool now = tokens >= 1;
			_state[(client, kind)] = (now ? tokens - 1 : tokens, nowMs, now ? null : change);
			return now;
		}
	}

	/// <summary>The waiting change, once the budget allows it.</summary>
	public T? Due(ushort client, byte kind, long nowMs)
	{
		lock (_state)
		{
			var (tokens, waiting) = Refill(client, kind, nowMs);
			if (waiting == null || tokens < 1)
			{
				return null;
			}
			_state[(client, kind)] = (tokens - 1, nowMs, null);
			return waiting;
		}
	}

	private (double Tokens, T? Waiting) Refill(ushort client, byte kind, long nowMs)
	{
		var (tokens, at, waiting) = _state.TryGetValue((client, kind), out var s) ? s : (Burst, nowMs, null);
		return (Math.Min(Burst, tokens + Math.Max(0, nowMs - at) * PerSecond / 1000.0), waiting);
	}

	/// <summary>The suit colour is a packed sRGB uint (R in the low byte); only opaque colours are valid.</summary>
	public static void ClampColor(Span<byte> packed) =>
		BinaryPrimitives.WriteUInt32LittleEndian(packed, BinaryPrimitives.ReadUInt32LittleEndian(packed) | 0xFF000000u);
}
