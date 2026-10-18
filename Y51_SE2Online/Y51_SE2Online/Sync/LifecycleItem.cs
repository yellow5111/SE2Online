using System.Collections.Generic;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.Sync;

public sealed class LifecycleItem
{
	public SyncMsg Type;

	public EntityBundleMessage? Bundle;

	public List<uint>? NetIds;

	public byte DespawnMode;

	public List<(uint NetId, ushort Key)>? Removes;

	public List<WireCommand>? Signals;

	public List<string>? Strings;

	public List<ObservableOp>? Observables;

	public DesyncCheck? Check;

	public uint HostTick;

	public int WireBytes;

	public override string ToString()
	{
		return Type switch
		{
			SyncMsg.EntityBundle => $"{((Bundle.Kind == 0) ? "world" : "spawn")} bundle #{Bundle.LifeSeq} ({Bundle.Entities.Count} entities, {MpLogExtensions.Bytes(WireBytes)})", 
			SyncMsg.Despawn => $"{((DespawnMode == 0) ? "despawn" : "forget")} {NetIds.Count}", 
			SyncMsg.DataRemove => $"data-remove {Removes.Count}", 
			SyncMsg.DataTag => $"data-tag {Removes.Count}", 
			SyncMsg.SignalBatch => $"signals {Signals.Count} (host tick {HostTick})", 
			SyncMsg.StringTable => $"names {Strings.Count}", 
			SyncMsg.ObservableBatch => $"observables {Observables.Count}", 
			SyncMsg.DesyncCheck => $"desync check {Check.CheckId} ({Check.Roots.Count} roots)", 
			_ => Type.ToString(), 
		};
	}
}
