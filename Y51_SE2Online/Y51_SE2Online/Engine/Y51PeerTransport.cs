using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.Accessors;
using Keen.VRage.DCS.Components;
using Keen.VRage.DCS.Internal;
using Keen.VRage.DCS.Scenes;
using Keen.VRage.Library.Collections;
using Keen.VRage.Library.Diagnostics;
using Keen.VRage.Library.Memory;
using Keen.VRage.Library.Serialization;
using Keen.VRage.Library.Serialization.Binary;
using Keen.VRage.Library.Serialization.Binary.Archive;
using Keen.VRage.Library.Utils;
using Keen.VRage.Library.Reflection;
using Keen.VRage.Library.Threading;
using Keen.VRage.Multiplayer;
using Keen.VRage.Multiplayer.NetworkStories;
using Keen.VRage.Multiplayer.Sessions;
using Keen.VRage.Multiplayer.Transport;
using Keen.VRage.Multiplayer.Transport.OutOfProcess;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;
using KTask = Keen.VRage.Library.Threading.Task;
using OopTransport = Keen.VRage.Multiplayer.Transport.OutOfProcess.OutOfProcessPeerTransport;

namespace Y51_SE2Online.Engine;

/// <summary>
/// The engine transport shared by both ends: what the game hands a peer transport each frame (signals, stories,
/// entity data, sampled data, object batches) goes out on SE2 Online's own connection instead of Keen's channel 1.
/// The game's own serializers still encode signals, stories and object batches (they cover every generated type);
/// entity data travels raw by network id. The game's replication session components keep deciding what each peer
/// gets; this owns the wire, ordering, coalescing and timing. Inbound frames are applied on the session thread in the
/// game's inbound replication job, outbound ones flushed in its peer dispatch job (Y51Replication hooks both).
/// </summary>
internal abstract class Y51PeerTransport : IPeerTransport, IDisposable, EngineWire.IUnitSink
{
	protected readonly OutOfProcessPeerTransportSessionComponent Component;
	protected readonly ReplicationTargetContext TargetContext;
	protected IReplicationSerializationContext Context = null!;
	private readonly OutOfProcessSignalPipeline _pipeline;

	public readonly ClientId PeerClientId;
	public readonly string Name;

	// Outbound, scene thread only.
	private readonly List<byte[]> _replicationPackets = new();
	private readonly List<object> _sequence = new();
	private readonly DataLane _lane = new() { Raw = MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "lane:raw") };
	private readonly List<DataLane.Op> _drained = new();
	private readonly Dictionary<ushort, string> _keyNames = new();
	private readonly Dictionary<(uint, ushort), (uint NetId, ushort Key, byte[] Bytes)> _unreliable = new();
	private readonly Dictionary<Type, ushort> _outKeys = new();
	private readonly List<(ushort Key, string Name)> _newKeys = new();
	private uint _nextSeq = 1;
	private long _sampledStamp;

	// Inbound: the network thread offers, the scene thread applies.
	private readonly LifecycleSequencer<byte[]> _frames = new();
	private readonly ConcurrentQueue<byte[]> _state = new();
	private readonly Dictionary<ushort, (Type Type, int TypeId, int Size)?> _inKeys = new();
	private readonly Dictionary<(uint, ushort), long> _lastApplied = new();

	private readonly Dictionary<StoryId, Keen.VRage.Library.Threading.TaskCompletionSource<StoryProgress>> _storiesInFlight = new();
	// The state machine each in-flight story left as: one that comes back under its id must be the same story.
	private readonly Dictionary<StoryId, Type> _storyTypes = new();

	public long FramesOut, FramesIn, BytesOut, BytesIn, UnitsIn, UnitsFailed, SignalsOut, SignalsIn, DataOut, DataIn, SampledOut, SampledIn, StaleSampled, StoriesOut, StoriesIn, Dropped;
	private bool _disposed;

	public bool Disposed => _disposed;

	protected Y51PeerTransport(OutOfProcessPeerTransportSessionComponent component, ClientId peer, string name)
	{
		Component = component;
		PeerClientId = peer;
		Name = name;
		TargetContext = new ReplicationTargetContext(peer);
		_pipeline = new OutOfProcessSignalPipeline(component.GetSession(), component.IsServer, Trace.CreateChannel("Y51 " + name));
		if (component.IsServer) _frames.MaxBuffered = MaxEarlyFramesFromClient;
	}

	protected Session Session => Component.GetSession();

	public OutOfProcessPeerTransportSessionComponent Owner => Component;

	protected Scene Scene => Component.Entity.Scene;

	public virtual void Dispose()
	{
		_disposed = true;
		foreach (var tcs in _storiesInFlight.Values) tcs.Dispose();
		_storiesInFlight.Clear();
		_storyTypes.Clear();
		TaskLifetime.EndLifetimeOnTime(this, TimeSpan.FromSeconds(1L));
	}

	// --- outbound: what the game hands this peer ---

	// Signals only server-side components listen to, whose arguments a player's game cannot even read: a grid's turrets
	// finding a new priority target names the character's PriorityTargetComponent, which only the server's character
	// composition has (AutoTargetingComponent, the only listener, is server-only too). Every player joining a world with
	// turrets lost that chunk as "could not read" (IndexOutOfRange in ComponentFunctions.GetMatchingComponent).
	// The 4.0.0.3155 sweep (every replicated signal against every server/client composition pair, by component key) found
	// this one; rerun Tools/sweep_server_only_signals.py after a game update.
	private static int _serverSideOnlyLogged;

	private static readonly HashSet<string> ServerSideOnly = new(StringComparer.Ordinal)
	{
		"Replay_Keen_Game2_Simulation_WorldObjects_CubeGrids_Weapons_TargetingGridComponent_OnNewPriorityTargetSignal_Trampoline",
	};

	public void FilterAndSendCommandBuffer(ReplicationCommandBuffer localCommandBuffer)
	{
		// Keen may still call a peer it has not dropped yet; nothing queues for one that is gone.
		if (_disposed) { Dropped++; return; }
		var frame = _pipeline.GetOutboundFrame(localCommandBuffer);
		foreach (RecordedSignal signal in frame.Signals)
		{
			if (Component.IsServer && ServerSideOnly.Contains(signal.Signal.String))
			{
				if (System.Threading.Interlocked.Exchange(ref _serverSideOnlyLogged, 1) == 0)
					GameLog.Instance.Info("engine: signals only the server listens to stay on the server: " + string.Join(", ", ServerSideOnly));
				continue;
			}
			if (Component.IsServer && _signalDedupe.IsRepeat(in signal, IsOwn)) continue;
			_sequence.Add(signal);
			EngineCoverage.Note("signal", signal.Signal.String, Out);
		}
		SignalsOut += frame.Signals.Count;
	}

	public void SendEntityData<T>(Entity entity, in T data) where T : unmanaged
	{
		if (_disposed) { Dropped++; return; }
		if (!OopTransport.CanTypeCrossNetwork(typeof(T)) || IdOf(entity) is not uint id) { Dropped++; return; }
		ushort key = KeyOf(typeof(T));
		int rate = -1;
		bool own = Component.IsServer && IsOwn(entity);
		if (Component.IsServer && !own && DataRates.ByDistance(typeof(T)) && entity.Data.TryGet<Keen.VRage.Core.WorldTransform>(out var at))
			rate = MotionRelevance.IntervalFrames(_observer, in at.Position) > 1 ? DataRates.FarMs : 0;
		// The player's own character and ship are simulated on their game as well, so its copy can drift from what it was
		// last sent: every value the server has for them goes, as on Keen's transport.
		_lane.Set(id, key, BytesOf(in data), sampled: false, MpClock.NowMs(), rate, exact: own);
		DataOut++;
	}

	public void RemoveEntityData<T>(Entity entity) where T : unmanaged
	{
		if (_disposed) { Dropped++; return; }
		if (IdOf(entity) is not uint id) { Dropped++; return; }
		ushort key = KeyOf(typeof(T));
		_lane.Remove(id, key, sampled: false);
	}

	public void SendSampledEntityData<T>(Entity entity, in T data, SendMode mode) where T : unmanaged
	{
		if (_disposed) { Dropped++; return; }
		if (!OopTransport.CanTypeCrossNetwork(typeof(T)) || IdOf(entity) is not uint id) { Dropped++; return; }
		ushort key = KeyOf(typeof(T));
		byte[] bytes = BytesOf(in data);
		SampledOut++;
		if (typeof(T) == typeof(Keen.VRage.Game.Multiplayer.EntityMotionState) && Component.IsServer)
		{
			ref readonly var motion = ref Unsafe.As<T, Keen.VRage.Game.Multiplayer.EntityMotionState>(ref Unsafe.AsRef(in data));
			if (motion.ServerTime > CorpseMotion.LatestServerTime) CorpseMotion.LatestServerTime = motion.ServerTime;
			_motionPos[id] = motion.Transform.Position;
			if (mode == SendMode.Unreliable)
			{
				IsOwn(entity);
				int interval = MotionRelevance.IntervalFrames(_observer, in motion.Transform.Position, entity.TryGet<Keen.Game2.Simulation.WorldObjects.Characters.CharacterLifeComponent>() != null);
				if (interval > 1 && _motionSentAt.TryGetValue(id, out long sentAt) && _dispatches - sentAt < interval)
				{
					// The newest value goes when this entity's interval is up, even if nothing newer comes.
					_motionHeld[id] = (sentAt + interval, key, bytes);
					MotionThinned++;
					return;
				}
				_motionHeld.Remove(id);
				_motionSentAt[id] = _dispatches;
			}
			else
			{
				_motionHeld.Remove(id);
				_motionSentAt.Remove(id);
			}
		}
		if (mode == SendMode.Unreliable)
		{
			_unreliable[(id, key)] = (id, key, bytes);
			return;
		}
		// A reliable value (the body came to rest, a teleport) supersedes any unreliable one still pending.
		_unreliable.Remove((id, key));
		_lane.Set(id, key, bytes, sampled: true, MpClock.NowMs());
	}

	public void RemoveSampledEntityData<T>(Entity entity) where T : unmanaged
	{
		if (_disposed) { Dropped++; return; }
		if (IdOf(entity) is not uint id) { Dropped++; return; }
		ushort key = KeyOf(typeof(T));
		_unreliable.Remove((id, key));
		_lane.Remove(id, key, sampled: true);
	}

	public Keen.VRage.Library.Threading.Task<StoryProgress> MoveNetworkStory(StoryId id, StoryProgress.Move storyMove, ReplicationCommandBuffer.Position signalSyncPoint)
	{
		IStoryData story;
		try { story = ToStoryData(storyMove, id); }
		catch (Exception ex) when (ex.CanHandle()) { return KTask.FromException<StoryProgress>(ex); }
		var tcs = new Keen.VRage.Library.Threading.TaskCompletionSource<StoryProgress>($"Y51 story #{id} to {PeerClientId}");
		_storiesInFlight[id] = tcs;
		_storyTypes[id] = storyMove.StateMachine.GetType();
		_sequence.Add(story);
		StoriesOut++;
		EngineCoverage.Note("story", StoryName(story), Out);
		return tcs.Task;
	}

	protected void EnqueueReplicationPacket(byte[] archive) => _replicationPackets.Add(archive);

	protected string Out => Component.IsServer ? "server->client" : "client->server";

	protected string In => Component.IsServer ? "client->server applied" : "server->client applied";

	private static string StoryName(IStoryData story) => story is StateMachineEnvelope e ? e.StateMachineTypeName.Split(',')[0] : "story completion";

	protected void EnqueueStoryData(IStoryData story) => _sequence.Add(story);

	/// <summary>Scene thread, once per peer dispatch.</summary>
	public void Dispatch()
	{
		if (_disposed) return;
		_dispatches++;
		if (_motionHeld.Count > 0)
		{
			List<uint>? due = null;
			foreach (var (id, held) in _motionHeld)
				if (_dispatches >= held.Due)
				{
					_unreliable[(id, held.Key)] = (id, held.Key, held.Bytes);
					_motionSentAt[id] = _dispatches;
					(due ??= new()).Add(id);
				}
			if (due != null) foreach (uint id in due) _motionHeld.Remove(id);
		}
		long stamp = ++_sampledStamp, reliableStamp = ++_sampledStamp;
		var w = new EngineWire.Writer(UpstreamKind);
		foreach (var (key, name) in _newKeys) w.TypeDef(key, name);
		_newKeys.Clear();
		foreach (byte[] packet in _replicationPackets) w.Archive(EngineWire.Unit.ReplicationArchive, packet);
		_replicationPackets.Clear();
		_lane.Drain(MpClock.NowMs(), _drained);
		foreach (var op in _drained)
		{
			switch (op.Kind)
			{
			case DataLane.Kind.Set: w.Data(op.NetId, op.Key, op.Bytes); break;
			case DataLane.Kind.Remove: w.DataRemove(op.NetId, op.Key); break;
			case DataLane.Kind.SampledSet: w.Sampled(reliableStamp, op.NetId, op.Key, op.Bytes); break;
			case DataLane.Kind.SampledRemove: w.SampledRemove(reliableStamp, op.NetId, op.Key); break;
			}
			EngineCoverage.Note(op.Kind is DataLane.Kind.Set or DataLane.Kind.SampledSet ? "data" : "data-remove", _keyNames[op.Key], Out, 8 + (op.Bytes?.Length ?? 0));
		}
		_drained.Clear();
		if (_sequence.Count > 0)
		{
			try
			{
				byte[] archive = SerializeSequence(_sequence);
				w.Archive(EngineWire.Unit.SequenceArchive, archive);
				EngineCoverage.Note("archive", "signals and stories", Out, archive.Length + 5);
				// Once in a while each signal alone, for what each kind costs on the wire.
				if (EngineCoverage.On && ++_sizeSample % 120 == 0)
					foreach (object unit in _sequence)
						if (unit is RecordedSignal one) EngineCoverage.Note("signal-bytes", one.Signal.String, Out, SerializeSequence(new List<object> { one }).Length);
			}
			catch (Exception ex)
			{
				Dropped += _sequence.Count;
				GameLog.Instance.WarnOnce("engine-seq-" + Name, $"engine {Name}: a frame's signals and stories could not be serialized and were dropped ({ex.GetType().Name}: {ex.Message})");
			}
			_sequence.Clear();
		}
		if (!w.IsEmpty)
		{
			byte[] frame = _deflater != null ? _deflater.Pack(w.Finish(_nextSeq++, compress: false)) : w.Finish(_nextSeq++);
			FramesOut++;
			BytesOut += frame.Length;
			FrameBytesOut += frame.Length;
			Send(frame, state: false);
		}
		if (_unreliable.Count > 0)
		{
			var entries = new List<(uint, ushort, byte[])>(_unreliable.Values);
			_unreliable.Clear();
			foreach (var e in entries)
			{
				EngineCoverage.Note("sampled", _keyNames[e.Item2], Out, 8 + e.Item3.Length);
				if (Component.IsServer) CollectionsMarshal.GetValueRefOrAddDefault(_motionPerEntity, e.Item1, out _)++;
			}
			foreach (byte[] message in EngineWire.EncodeState(stamp, entries, SyncProtocol.StateMessageBytes))
			{
				BytesOut += message.Length;
				StateBytesOut += message.Length;
				Send(message, state: true);
			}
		}
		AfterDispatch();
		EngineCoverage.MaybeWrite();
	}

	protected abstract SyncMsg UpstreamKind { get; }

	protected abstract void Send(byte[] message, bool state);

	protected virtual void AfterDispatch() { }

	private static readonly CompressionType ChunkCompression = MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "lane:raw") ? CompressionType.Brotli : CompressionType.None;

	private byte[] SerializeSequence(List<object> sequence)
	{
		var builder = BuildContexts();
		using var stream = new MemoryStream();
		using var context = new SerializationContext(stream, "Y51 engine frame", builder);
		using (var writer = new BinaryArchiveWriter(context))
		{
			int n = 0;
			foreach (object unit in sequence)
			{
				string chunk = n++ == 0 ? "[Main]" : (n - 1).ToString();
				switch (unit)
				{
				// Uncompressed: the frame is deflated whole, so the archive's type table and every small chunk share one dictionary.
				case RecordedSignal signal: writer.AddChunk(chunk, in signal, ChunkCompression); break;
				case StateMachineEnvelope move: writer.AddChunk(chunk, in move, ChunkCompression); break;
				case StoryComplete complete: writer.AddChunk(chunk, in complete, ChunkCompression); break;
				default: throw new InvalidOperationException("unknown sequence unit " + unit.GetType());
				}
			}
		}
		return stream.ToArray();
	}

	internal ServiceLocatorContext.Builder BuildContexts()
	{
		var builder = ServiceLocatorContext.Builder.Create(default(ReadOnlySpan<object>));
		BuildThisPeerContext(ref builder);
		Component.BuildContexts(ref builder, Context);
		return builder;
	}

	protected virtual void BuildThisPeerContext(ref ServiceLocatorContext.Builder builder) => builder.Add(TargetContext);

	private uint? IdOf(Entity entity)
	{
		if (Component.NetworkIdMapper.TryGetId(entity) is not { } id) return null;
		return id.Value <= uint.MaxValue ? (uint)id.Value : null;
	}

	private ushort KeyOf(Type type)
	{
		if (_outKeys.TryGetValue(type, out ushort key)) return key;
		key = (ushort)(_outKeys.Count + 1);
		_outKeys[type] = key;
		_keyNames[key] = type.FullName!;
		if (DataRates.MinIntervalMs(type) is int rate) _lane.Rates[key] = rate;
		_newKeys.Add((key, type.FullName + ", " + type.Assembly.GetName().Name));
		return key;
	}

	private static byte[] BytesOf<T>(in T data) where T : unmanaged => MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in data)).ToArray();

	private IStoryData ToStoryData(StoryProgress story, StoryId id)
	{
		if (story is StoryProgress.Complete complete) return new StoryComplete { StoryId = id, Completion = complete.Completion };
		var move = (StoryProgress.Move)story;
		var plan = MetadataIndexerBase<NetworkStoryStateMachineIndexer>.Instance.GetSerializationPlan(move.StateMachine.GetType());
		var envelope = plan.Capture(id, move.StateMachine, Context, out bool skipped);
		if (envelope == null || skipped) throw new SkipNetworkStoryException();
		return envelope;
	}

	// --- inbound ---

	// What a player may have the server hold for it before the scene thread reads it: frames that came early (out of
	// sequence) and frames waiting to be read, by count and by bytes. A player past either is disconnected.
	private const int MaxEarlyFramesFromClient = 1024;
	private const long MaxPendingBytesFromClient = 64L << 20;
	private long _pendingBytes;
	private volatile bool _overflowed;

	/// <summary>Network thread. False when the peer sent more than the server holds for one player (disconnect it).</summary>
	public bool Offer(byte[] message)
	{
		BytesIn += message.Length;
		if ((SyncMsg)message[0] == SyncMsg.EngineState)
		{
			if (_state.Count < 4096) _state.Enqueue(message);
			return true;
		}
		bool server = Component.IsServer;
		if (_overflowed) return false;
		if (server && System.Threading.Interlocked.Read(ref _pendingBytes) + message.Length > MaxPendingBytesFromClient)
		{
			_overflowed = true;
			GameLog.Instance.Warn($"engine {Name}: the player sent more than {MaxPendingBytesFromClient >> 20} MiB the server had not read yet; disconnecting it");
			return false;
		}
		long overflowBefore = _frames.Overflow;
		if (_frames.Offer(EngineWire.SeqOf(message), message))
		{
			System.Threading.Interlocked.Add(ref _pendingBytes, message.Length);
			return true;
		}
		if (server && _frames.Overflow != overflowBefore)
		{
			_overflowed = true;
			GameLog.Instance.Warn($"engine {Name}: more than {MaxEarlyFramesFromClient} frames came out of sequence (waiting for #{_frames.NextExpected}); disconnecting the player");
			return false;
		}
		GameLog.Instance.WarnOnce("engine-frame-dup-" + Name, $"engine {Name}: frame #{EngineWire.SeqOf(message)} was a duplicate or overflowed the reorder buffer (waiting for #{_frames.NextExpected})");
		return true;
	}

	/// <summary>Scene thread, in the game's inbound replication job.</summary>
	public void ProcessInbound()
	{
		if (_disposed) return;
		while (_frames.TryTake(out _, out byte[] frame))
		{
			FramesIn++;
			System.Threading.Interlocked.Add(ref _pendingBytes, -frame.Length);
			try { EngineWire.Read(_inflater.Unpack(frame), this); }
			catch (Exception ex)
			{
				UnitsFailed++;
				GameLog.Instance.WarnOnce("engine-frame-" + Name, $"engine {Name}: a frame could not be read ({ex.GetType().Name}: {ex.Message}); the rest of it is skipped");
			}
		}
		ApplyParked();
		while (_state.TryDequeue(out byte[] message))
		{
			try { EngineWire.ReadState(message, this); }
			catch (Exception ex) { GameLog.Instance.WarnOnce("engine-state-" + Name, $"engine {Name}: a state message could not be read ({ex.GetType().Name}: {ex.Message})"); }
		}
		if (!Component.IsServer && _newestMotion != 0)
		{
			KeenNetcode.AnimPlayout.NoteSamples(_newestMotion);
#if !PULSAR
			KeenNetcode.AnimSyncProbe.NoteSample(_newestMotion);
#endif
			_newestMotion = 0;
		}
	}

	public void OnTypeDef(ushort key, string name)
	{
		(Type, int, int)? resolved = null;
		try
		{
			var type = Type.GetType(name, throwOnError: false);
			if (type != null && type.IsValueType && OopTransport.CanTypeNameCrossNetwork(type))
			{
				var info = RuntimeDataInfo.Of(type);
				resolved = (type, info.TypeId, info.Size);
			}
		}
		catch (Exception) { }
		if (resolved == null) GameLog.Instance.WarnOnce("engine-type-" + name, $"engine {Name}: data type {name} is not known here; its values are skipped");
		_inKeys[key] = resolved;
	}

	public void OnArchive(EngineWire.Unit unit, byte[] bytes)
	{
		using Buffer<object> objects = OopTransport.DeserializeFromNetwork(bytes, BuildContexts());
		foreach (object o in objects)
		{
			UnitsIn++;
			try { Handle(o); }
			catch (Exception ex) when (ex.CanHandle())
			{
				UnitsFailed++;
				GameLog.Instance.WarnOnce("engine-unit-" + o.GetType().Name, $"engine {Name}: applying {o.GetType().Name} failed ({ex.GetType().Name}: {ex.Message})");
			}
		}
	}

	private void Handle(object o)
	{
		switch (o)
		{
		case RecordedSignal signal:
			SignalsIn++;
			EngineCoverage.Note("signal", signal.Signal.String, In);
			if (!AcceptSignal(in signal)) break;
			if (!Component.IsServer && !NoSignalHold && Building(in signal)) { _parkedSignals.Add((signal, MpClock.NowMs())); SignalsParked++; break; }
			if (!Component.IsServer) { Component.ProcessSignal(signal, _pipeline); break; }
			var sender = KeenNetcode.ServerAccess.EnterSignal(PeerClientId);
			try { Component.ProcessSignal(signal, _pipeline); }
			finally { KeenNetcode.ServerAccess.LeaveSignal(sender); }
			break;
		case IStoryData story:
			StoriesIn++;
			EngineCoverage.Note("story", StoryName(story), In);
			ProcessStory(story);
			break;
		default:
			if (EngineCoverage.On) EngineCoverage.Note("object batch", o.GetType().IsGenericType ? o.GetType().Name + "<" + string.Join(",", o.GetType().GetGenericArguments().Select(a => a.Name)) + ">" : o.GetType().Name, In);
			HandleReplication(o);
			break;
		}
	}

	protected virtual bool AcceptSignal(in RecordedSignal signal) => true;

	protected abstract void HandleReplication(object message);

	public void OnData(uint netId, ushort key, ReadOnlySpan<byte> bytes)
	{
		DataIn++;
		if (Resolve(netId, key, bytes.Length, out var entity, out int typeId) && AcceptData(typeId, entity, removal: false))
		{
			Write(entity, typeId, bytes);
			EngineCoverage.Note("data", RuntimeDataInfo.Of(typeId).RuntimeType.FullName!, In);
		}
	}

	public void OnDataRemove(uint netId, ushort key)
	{
		if (Resolve(netId, key, -1, out var entity, out int typeId) && AcceptData(typeId, entity, removal: true))
			DataMarshal.TryRemove(Scene, entity.DEntity, typeId);
	}

	public void OnSampled(long stamp, bool reliable, uint netId, ushort key, ReadOnlySpan<byte> bytes)
	{
		SampledIn++;
		if (!Fresh(netId, key, stamp, reliable)) { StaleSampled++; return; }
		if (!Resolve(netId, key, bytes.Length, out var entity, out int typeId) || !AcceptData(typeId, entity, removal: false)) return;
		if (!InScene(entity))
		{
			// Still being built or staged (a join, a ship streaming in): it gets this value the moment it is in the scene, not one
			// stale frame at the position its bundle was captured at.
			_parked[(netId, key)] = (bytes.ToArray(), MpClock.NowMs());
			return;
		}
		_parked.Remove((netId, key));
		ApplySampled(netId, entity, typeId, bytes);
	}

	private void ApplySampled(uint netId, Entity entity, int typeId, ReadOnlySpan<byte> bytes)
	{
		if (typeId != MotionTypeId)
		{
			Write(entity, typeId, bytes);
			return;
		}
		var motion = MemoryMarshal.Read<Keen.VRage.Game.Multiplayer.EntityMotionState>(bytes);
		long ticks = motion.ServerTime.Ticks;
		// The first sample since this body was built from its bundle (a join, streaming in): playback starts here. Blending
		// from the bundle's state put a ship seconds of travel (kilometres) behind for a frame. A body that rested and moves
		// again is not restarted: its resting sample is where it really was.
		bool first = !_motionTicks.ContainsKey(netId);
		if (first)
		{
			motion.Flags |= Keen.VRage.Game.Multiplayer.MotionStateFlags.Teleported;
			MotionRestarts++;
		}
		// Other characters play a few frames back from a buffer (MotionSmoothing), as their samples do on Keen's transport.
		if (!Component.IsServer && KeenNetcode.MotionSmoothing.TryHold(entity, in motion)) { }
		else if (first)
		{
			Span<byte> fresh = stackalloc byte[bytes.Length];
			bytes.CopyTo(fresh);
			MemoryMarshal.Write(fresh, in motion);
			Write(entity, typeId, fresh);
		}
		else Write(entity, typeId, bytes);
		_motionTicks[netId] = ticks;
		NoteMotion(ticks);
	}

	private readonly Dictionary<uint, long> _motionTicks = new();

	public long MotionRestarts;

	private readonly Dictionary<(uint NetId, ushort Key), (byte[] Bytes, long AtMs)> _parked = new();

	public long ParkedApplied, SignalsParked, ParkedSignalsApplied;

	// Signals for an entity this game is still building (a join, a grid streaming in) or holds staged (a world area, a
	// contract's spawn: built, but outside the scene until the server completes the staged batch): applied in arrival
	// order once it is in the scene. A change applied to a staged entity queued render commands for render objects that
	// do not exist yet, and the renderer died on them (F7: SetRenderFlagsState / SetMaterials / SetPartVisibility).
	private readonly List<(RecordedSignal Signal, long AtMs)> _parkedSignals = new();

	// Kill switch for bisecting (-y51online:signalhold:off): signals reach entities still being built or staged at once.
	private static readonly bool NoSignalHold = MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "signalhold:off");

	private bool Building(in RecordedSignal signal)
	{
		if (signal.Arguments is not { Count: > 0 } args) return false;
		object? first = args[0].Value is PayloadArg payload ? payload.Payload : args[0].Value;
		try { return first is Entity target && !InScene(target); }
		catch (Exception) { return false; }
	}

	// Built and in the scene: a staged entity is alive but keeps StagingTag until its staged batch completes.
	private bool InScene(Entity e) => Scene.IsEntityAlive(e.DEntity) && !e.Data.Has<Keen.VRage.DCS.CoreData.StagingTag>();

	private void ApplyParkedSignals()
	{
		if (_parkedSignals.Count == 0) return;
		long now = MpClock.NowMs();
		var still = new List<(RecordedSignal, long)>();
		foreach (var (signal, at) in _parkedSignals)
		{
			if (Building(in signal))
			{
				if (now - at <= 120_000) still.Add((signal, at));
				continue;
			}
			try { Component.ProcessSignal(signal, _pipeline); ParkedSignalsApplied++; }
			catch (Exception ex) when (ex.CanHandle()) { UnitsFailed++; GameLog.Instance.WarnOnce("engine-parked-signal", $"engine {Name}: a held signal failed ({ex.GetType().Name}: {ex.Message})"); }
		}
		_parkedSignals.Clear();
		_parkedSignals.AddRange(still);
	}

	/// <summary>Sampled values that arrived before their entity was in the scene, applied once it is (scene thread).</summary>
	protected void ApplyParked()
	{
		ApplyParkedSignals();
		if (_parked.Count == 0) return;
		long now = MpClock.NowMs();
		List<(uint, ushort)>? done = null;
		foreach (var (id, value) in _parked)
		{
			if (now - value.AtMs > 30_000) { (done ??= new()).Add(id); continue; }
			if (!Resolve(id.NetId, id.Key, value.Bytes.Length, out var entity, out int typeId) || !InScene(entity)) continue;
			ApplySampled(id.NetId, entity, typeId, value.Bytes);
			ParkedApplied++;
			(done ??= new()).Add(id);
		}
		if (done != null) foreach (var id in done) _parked.Remove(id);
	}

	private static readonly int MotionTypeId = RuntimeDataInfo.Of(typeof(Keen.VRage.Game.Multiplayer.EntityMotionState)).TypeId;

	private long _newestMotion;

	private long _sizeSample;

	public long PropertiesRefreshed;

	protected void RefreshSyncProperties(IEnumerable<Entity> roots, long sinceMs, bool notSavedOnly = false)
	{
		try
		{
			var signals = SyncProperties.Current(Component.GetSession(), Component._replicationCore, _pipeline, roots, sinceMs, notSavedOnly);
			foreach (var signal in signals) _sequence.Add(signal);
			PropertiesRefreshed += signals.Count;
		}
		catch (Exception ex) when (ex.CanHandle()) { GameLog.Instance.WarnOnce("syncprop-refresh-" + Name, $"engine {Name}: sync property refresh failed ({ex.GetType().Name}: {ex.Message})"); }
	}

	private readonly SignalDedupe _signalDedupe = new();

	private readonly EngineWire.StreamDeflater? _deflater = MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "lane:raw") ? null : new();
	private readonly EngineWire.StreamInflater _inflater = new();

	private long _dispatches;
	private readonly Dictionary<uint, long> _motionSentAt = new();
	private readonly Dictionary<uint, (long Due, ushort Key, byte[] Bytes)> _motionHeld = new();
	public long MotionThinned, FrameBytesOut, StateBytesOut;
	private readonly Dictionary<uint, int> _motionPerEntity = new();
	private readonly Dictionary<uint, Keen.VRage.Library.Mathematics.Vector3D> _motionPos = new();

	/// <summary>The entities whose motion went to this peer most often since the last call (stats log).</summary>
	public string TopMotion()
	{
		var top = _motionPerEntity.OrderByDescending(kv => kv.Value).Take(8)
			.Select(kv => $"{kv.Key} x{kv.Value} ({(_motionPos.TryGetValue(kv.Key, out var at) ? MotionRelevance.DistanceTo(_observer, at) : "?")})");
		string text = $"{_motionPerEntity.Count} entities, top {string.Join(", ", top)}";
		_motionPerEntity.Clear();
		_motionPos.Clear();
		return text;
	}

	/// <summary>An entity this peer is getting fresh or no longer has: what it was sent before does not count.</summary>
	protected void ForgetSent(object instance)
	{
		if (instance is not Entity entity) return;
		_signalDedupe.Forget(entity);
		if (IdOf(entity) is uint id)
		{
			_lane.Forget(id);
			_motionSentAt.Remove(id);
			_motionHeld.Remove(id);
		}
	}

	private readonly HashSet<Entity> _ownCharacters = new(), _ownRoots = new(), _ownWas = new();
	private long _ownAt = -1;
	private Keen.VRage.Library.Mathematics.Vector3D? _observer;
	public long OwnChanges;

	/// <summary>Server: a body this player's game simulates (their character, the ship they sit in, its subgrids).</summary>
	protected bool IsOwn(Entity entity)
	{
		if (_ownAt != _dispatches)
		{
			_ownAt = _dispatches;
			_ownWas.Clear();
			_ownWas.UnionWith(_ownCharacters);
			_ownWas.UnionWith(_ownRoots);
			Y51Replication.OwnBodies(PeerClientId, _ownCharacters, _ownRoots);
			// Where this player is, for how often far bodies go to them (identity based: a player that rejoined has a new
			// ClientId but the same character).
			_observer = _ownCharacters.Count > 0 && _ownCharacters.First().Data.TryGet<Keen.VRage.Core.WorldTransform>(out var seen) ? seen.Position : null;
			// A body the player stopped simulating (left the seat, died, changed hands): what it was sent is forgotten, so
			// the server's next value replaces whatever the player's game left in it.
			foreach (var was in _ownWas)
				if (!_ownCharacters.Contains(was) && !_ownRoots.Contains(was))
				{
					ForgetSent(was);
					OwnChanges++;
				}
		}
		if (_ownCharacters.Count == 0) return false;
		return _ownCharacters.Contains(entity) || _ownRoots.Count > 0 && _ownRoots.Contains(Keen.VRage.Core.Game.Data.ParentDataFunctions.GetTopLevelParent(entity));
	}

	public string DescribeLane() => $"wire: frames {FrameBytesOut / 1024} KiB, state {StateBytesOut / 1024} KiB; data coalesced {_lane.Coalesced}, unchanged skipped {_lane.Unchanged}, rate-deferred {_lane.Deferred}, motion thinned {MotionThinned}, repeated signals skipped {_signalDedupe.Skipped}, own-body changes {OwnChanges}, sync properties refreshed {PropertiesRefreshed}, signals held for entities being built {SignalsParked} (applied {ParkedSignalsApplied}), motion restarts {MotionRestarts}";

	private void NoteMotion(long serverTicks)
	{
		if (serverTicks > _newestMotion) _newestMotion = serverTicks;
	}

	public void OnSampledRemove(long stamp, uint netId, ushort key)
	{
		if (!Fresh(netId, key, stamp, reliable: true)) return;
		if (Resolve(netId, key, -1, out var entity, out int typeId) && Scene.IsEntityAlive(entity.DEntity) && AcceptData(typeId, entity, removal: true))
			DataMarshal.TryRemove(Scene, entity.DEntity, typeId);
	}

	// Reliable values win ties: a final reliable value supersedes in-flight unreliable ones from the same dispatch.
	private bool Fresh(uint netId, ushort key, long stamp, bool reliable)
	{
		ref long last = ref CollectionsMarshal.GetValueRefOrAddDefault(_lastApplied, (netId, key), out bool exists);
		if (exists && (reliable ? stamp < last : stamp <= last)) return false;
		last = stamp;
		return true;
	}

	protected virtual bool AcceptData(int typeId, Entity target, bool removal) => true;

	private bool Resolve(uint netId, ushort key, int length, out Entity entity, out int typeId)
	{
		entity = null!;
		typeId = 0;
		if (!_inKeys.TryGetValue(key, out var type) || type is not { } t) { Dropped++; return false; }
		if (length >= 0 && length != t.Size)
		{
			Dropped++;
			GameLog.Instance.WarnOnce("engine-size-" + t.Type.Name, $"engine {Name}: {t.Type.Name} is {length} bytes on the sender and {t.Size} here; skipped");
			return false;
		}
		if (Component.NetworkIdMapper.TryGetObject(new NetworkId(netId)) is not Entity e) { Dropped++; return false; }
		entity = e;
		typeId = t.TypeId;
		return true;
	}

	private unsafe void Write(Entity entity, int typeId, ReadOnlySpan<byte> bytes)
	{
		ref byte target = ref DataMarshal.GetDataPointer(Scene, entity.DEntity, typeId);
		bytes.CopyTo(MemoryMarshal.CreateSpan(ref target, bytes.Length));
	}

	// --- stories (what Keen's transport does with them) ---

	private void ProcessStory(IStoryData story)
	{
		if (_storiesInFlight.Remove(story.StoryId, out var tcs))
		{
			_storyTypes.Remove(story.StoryId, out var sent);
			if (story is StateMachineEnvelope back && KeenNetcode.ServerAccess.ReturningStoryRefusal(PeerClientId, sent, back) is { } refused)
			{
				tcs.SetResult(new StoryProgress.Complete(new StoryFault(refused)));
				return;
			}
			tcs.SetResult(story is StateMachineEnvelope envelope
				? new StoryProgress.Move(RestoreStoryStateMachine(PeerClientId, story.StoryId, envelope))
				: new StoryProgress.Complete(((StoryComplete)story).Completion));
			return;
		}
		if (story is not StateMachineEnvelope move)
		{
			GameLog.Instance.WarnOnce("engine-story-unexpected-" + Name, $"engine {Name}: completion for story {story.StoryId} that was never sent");
			return;
		}
		if (!Component.ReplicationComponent.IsValidForeignStoryId(move.StoryId))
		{
			GameLog.Instance.WarnOnce("engine-story-id-" + Name, $"engine {Name}: moved story {move.StoryId} is not one this side may host");
			return;
		}
		HostStory(move).SkipWait();
	}

	private async KTask HostStory(StateMachineEnvelope move)
	{
		await KTask.SetLifetime(Component);
		StoryId original = move.StoryId;
		StoryId local = Component.ReplicationComponent.AllocateStoryId();
		StoryProgress result;
		try
		{
			// The story's server part runs inside HostNetworkStory up to its first wait: the access checks know its caller.
			var caller = Component.IsServer ? KeenNetcode.ServerAccess.EnterStory(PeerClientId) : default;
			Keen.VRage.Library.Threading.Task<StoryProgress> hosted;
			try
			{
				var machine = RestoreStoryStateMachine(PeerClientId, local, move);
				hosted = Component.ReplicationComponent.HostNetworkStory(PeerClientId, local, machine);
			}
			finally { if (Component.IsServer) KeenNetcode.ServerAccess.LeaveStory(caller); }
			result = await hosted;
		}
		catch (Exception ex) when (ex.CanHandle())
		{
			result = new StoryProgress.Complete(new StoryFault(ex));
		}
		IStoryData back;
		try { back = ToStoryData(result, original); }
		catch (Exception ex) when (ex.CanHandle()) { back = new StoryComplete { StoryId = original, Completion = new StoryFault(ex) }; }
		if (!_disposed) EnqueueStoryData(back);
	}

	protected abstract IAsyncStateMachine RestoreStoryStateMachine(ClientId sender, StoryId storyId, StateMachineEnvelope envelope);

	public string Describe() => $"{Name}: frames out {FramesOut} in {FramesIn} ({MpLogExtensions.Bytes(BytesOut)} / {MpLogExtensions.Bytes(BytesIn)}), signals {SignalsOut}/{SignalsIn}, data {DataOut}/{DataIn}, sampled {SampledOut}/{SampledIn} ({StaleSampled} stale), stories {StoriesOut}/{StoriesIn}, units failed {UnitsFailed}, dropped {Dropped}, waiting for frame #{_frames.NextExpected} ({_frames.Buffered} early)";
}
