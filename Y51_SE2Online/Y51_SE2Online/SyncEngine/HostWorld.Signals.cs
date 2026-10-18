using System;
using System.Runtime.CompilerServices;
using Keen.VRage.Library.Utils;
using Keen.VRage.Physics.Character;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// Which per-entity S2C signals go through <see cref="SignalCoalescer"/>. Each client handler was read before it was
/// listed: it must keep only the newest value, or (physics state) react only to a change.
/// </summary>
internal sealed partial class HostWorld
{
	public const int MovementCapMs = ContactCapMs;

	public const int BlackboardCapMs = 50;

	private readonly SignalCoalescer _coalescer = new();

	private static readonly int[]? BlackboardKeyOffsets = KeyOffset<float>() is int f && KeyOffset<int>() is int i && KeyOffset<StringHash>() is int s && KeyOffset<bool>() is int b ? new[] { f, i, s, b } : null;

	// CharacterMovementEffectsComponent.OnSyncSupportingMaterial and ContainerBlockEffectsComponent store the values;
	// OnCharacterPhysicsStateChanged acts on a State change, plus the magboots proximity cue while floating;
	// AnimationBlackboardComponent.BlackboardReceive sets each key it is given.
	private static SignalCoalescer.Policy? CoalescePolicy(string name)
	{
		if (name.EndsWith("CharacterMovementComponent+SyncSupportingMaterialSignal", StringComparison.Ordinal))
			return new(name, MovementCapMs);
		if (name.EndsWith("CharacterMovementComponent+OnCharacterPhysicsStateChangedSignal", StringComparison.Ordinal))
			return new(name, 0, LiveRepeat: p => p.Length >= 8 && p[4] == (byte)CharacterState.Floating);
		if (name.EndsWith("ResourceContainerComponent+NotifyContainerMassChangedSignal", StringComparison.Ordinal))
			return new(name, MovementCapMs, Echo: true);
		if (name.EndsWith("AnimationBlackboardComponent+BlackboardSendSignal", StringComparison.Ordinal) && BlackboardKeyOffsets != null)
			return new(name, BlackboardCapMs, KeyOffsets: BlackboardKeyOffsets);
		return null;
	}

	// ValueTuple is auto layout: (StringHash, float) puts the float first, (StringHash, StringHash) the key first.
	private static unsafe int? KeyOffset<T>() where T : unmanaged
	{
		var element = default((StringHash, T));
		long key = Unsafe.ByteOffset(ref Unsafe.As<(StringHash, T), byte>(ref element), ref Unsafe.As<StringHash, byte>(ref element.Item1));
		long value = Unsafe.ByteOffset(ref Unsafe.As<(StringHash, T), byte>(ref element), ref Unsafe.As<T, byte>(ref element.Item2));
		return sizeof((StringHash, T)) == SignalCoalescer.ElementBytes && sizeof(StringHash) == 4 && (key, value) is (0, 4) or (4, 0) ? (int)key : null;
	}

	/// <summary>Caller holds <see cref="_signalSync"/>.</summary>
	private void QueueSignal(CommandLayout layout, WireCommand command)
	{
		if (!_coalescer.Registered(command.Key))
		{
			_coalescer.Register(command.Key, CoalescePolicy(layout.Name));
		}
		if (!_coalescer.Offer(command, MpClock.NowMs(), _pendingSignals))
		{
			_pendingSignals.Add(command);
		}
	}

	/// <summary>Caller holds <see cref="_signalSync"/>.</summary>
	private void FlushCoalesced() => _coalescer.Flush(MpClock.NowMs(), _pendingSignals);

	private void ForgetCoalesced(uint id)
	{
		lock (_signalSync) _coalescer.Forget(id);
	}
}
