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
using Keen.VRage.Multiplayer.Data;
using Keen.VRage.Multiplayer.Replications;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;
using KTask = Keen.VRage.Library.Threading.Task;

namespace Y51_SE2Online.Engine;

/// <summary>
/// SE2 Online's netcode between the two games' replication cores: what the game's replication hands a peer each frame
/// (signals, stories, entity data, sampled data, object batches) is recorded, encoded, ordered, sent and applied by
/// SE2 Online (OwnSignals, OwnStories, OwnUnits, OwnIds) on its own connection. The game's replication core decides what
/// each player gets, as it does for a single-player game's own client; the values inside a unit are written by the
/// game's per-type serializers with SE2 Online's ids for references. Inbound frames are applied on the scene thread at
/// the start of the frame, outbound ones flushed after the frame's replication (Y51Replication).
/// </summary>
internal abstract class Y51PeerTransport : IPeerTransport, IDisposable, EngineWire.IUnitSink
{
	protected readonly OwnHost Host;
	protected readonly ReplicationTargetContext TargetContext;
	private readonly OwnSignals _signals = new();

	public readonly ClientId PeerClientId;
	public readonly string Name;

	// Outbound, scene thread only.
	private readonly List<byte[]> _replicationPackets = new(), _desyncOut = new();
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

	/// <summary>A story leaving this side: a move (its hop's fields) or a completion (a result or a fault).</summary>
	protected sealed class StoryOut
	{
		public StoryId Id;
		public OwnStoryMove? Move;
		public IStoryCompletion? Completion;

		public string Name => Move?.Name ?? "story completion";
	}

	public long FramesOut, FramesIn, BytesOut, BytesIn, UnitsIn, UnitsFailed, SignalsOut, SignalsIn, DataOut, DataIn, SampledOut, SampledIn, StaleSampled, StoriesOut, StoriesIn, Dropped;
	private bool _disposed;

	public bool Disposed => _disposed;

	protected Y51PeerTransport(OwnHost host, ClientId peer, string name)
	{
		Host = host;
		PeerClientId = peer;
		Name = name;
		TargetContext = new ReplicationTargetContext(peer);
		if (host.IsServer) _frames.MaxBuffered = MaxEarlyFramesFromClient;
	}

	protected Session Session => Host.Session;

	public OwnHost Owner => Host;

	protected Scene Scene => Host.Replication.Entity.Scene;

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
		// The game may still call a peer it has not dropped yet; nothing queues for one that is gone.
		if (_disposed) { Dropped++; return; }
		if (Host.IsServer)
		{
			FilterShared(localCommandBuffer);
			return;
		}
		var frame = _signals.Record(localCommandBuffer);
		foreach (OwnSignal signal in frame)
		{
			// An object of this game only (the server never had it): the signal has nothing to name there.
			if (!signal.NamesOnly(Host.Ids)) { Unnamed++; continue; }
			_sequence.Add(signal);
			EngineCoverage.Note("signal", signal.Name, Out);
		}
		SignalsOut += frame.Count;
	}

	// --- server: one recording and one archive of a tick's signals for every player that gets all of them ---

	/// <summary>Bumped before the game hands a tick's signals to the peers (ReplicationSessionComponent.CommitFrame).</summary>
	internal static int CommitGeneration;

	private static ReplicationCommandBuffer? _sharedBuffer;
	private static int _sharedGeneration = -1;
	private static SharedSignals? _shared;

	public static long SharedArchives, SharedUses, Unnamed;

	/// <summary>After the peers' dispatch: a tick's shared signals never serve a later tick, whatever the generation says.</summary>
	internal static void EndSharedFrame()
	{
		_shared = null;
		_sharedBuffer = null;
	}

	/// <summary>A tick's signals, minus the server-only ones: recorded once, serialized once for every peer that sends them unchanged.</summary>
	private sealed class SharedSignals
	{
		public readonly List<OwnSignal> Units = new();
		public readonly List<OwnSignal> State = new();
		private byte[]? _archive;
		private bool _failed;
		private List<(Keen.VRage.Multiplayer.Replications.DataOffloading.ReplicatedDataOffloadingServerSessionComponent, Keen.VRage.Core.Game.Data.Blob)>? _blobs;

		// Serialized for the first player as its own archive; every further player gets the same bytes and the blobs they
		// name scheduled for it (SharedBlobs), as its own serialization would have done.
		public byte[]? Archive(Y51PeerTransport peer)
		{
			if (_failed) return null;
			if (_archive != null)
			{
				SharedBlobs.Replay(_blobs, peer.PeerClientId);
				return _archive;
			}
			try
			{
				object? recording = SharedBlobs.Begin();
				try { _archive = peer.SerializeSignals(Units); }
				finally { _blobs = SharedBlobs.End(recording); }
				SharedArchives++;
			}
			catch (Exception ex) when (ex.CanHandle())
			{
				_failed = true;
				GameLog.Instance.WarnOnce("engine-shared-archive", $"engine: a tick's signals could not be serialized together ({ex.GetType().Name}: {ex.Message}); each player's frame is written on its own");
			}
			return _archive;
		}
	}

	private void FilterShared(ReplicationCommandBuffer buffer)
	{
		if (!ReferenceEquals(_sharedBuffer, buffer) || _sharedGeneration != CommitGeneration || _shared == null)
		{
			var frame = _signals.Record(buffer);
			var shared = new SharedSignals();
			foreach (OwnSignal signal in frame)
			{
				if (ServerSideOnly.Contains(signal.Name))
				{
					if (System.Threading.Interlocked.Exchange(ref _serverSideOnlyLogged, 1) == 0)
						GameLog.Instance.Info("engine: signals only the server listens to stay on the server: " + string.Join(", ", ServerSideOnly));
					continue;
				}
				// An object no player has been sent (no network id): no player could resolve the signal.
				if (!signal.NamesOnly(Host.Ids)) { Unnamed++; continue; }
				if (SignalDedupe.IsStateSignal(signal.Name)) shared.State.Add(signal);
				else shared.Units.Add(signal);
			}
			(_sharedBuffer, _sharedGeneration, _shared) = (buffer, CommitGeneration, shared);
		}
		SignalsOut += _shared.Units.Count + _shared.State.Count;
		if (_shared.Units.Count > 0)
		{
			_sequence.Add(_shared);
			SharedUses++;
			if (EngineCoverage.On) foreach (OwnSignal signal in _shared.Units) EngineCoverage.Note("signal", signal.Name, Out);
		}
		// The state setters the server repeats every tick go after the tick's other signals, each player only what it
		// did not get last (SignalDedupe); they are idempotent setters, so their place within the tick does not matter.
		foreach (OwnSignal signal in _shared.State)
		{
			if (_signalDedupe.IsRepeat(signal, IsOwn)) continue;
			_sequence.Add(signal);
			EngineCoverage.Note("signal", signal.Name, Out);
		}
	}

	public void SendEntityData<T>(Entity entity, in T data) where T : unmanaged
	{
		if (_disposed) { Dropped++; return; }
		if (!ReplicatedDataValidator.IsSerializable(typeof(T)) || IdOf(entity) is not uint id) { Dropped++; return; }
		ushort key = KeyOf(typeof(T));
		int rate = -1;
		bool own = Host.IsServer && IsOwn(entity);
		if (Host.IsServer && !own && DataRates.ByDistance(typeof(T)) && entity.Data.TryGet<Keen.VRage.Core.WorldTransform>(out var at))
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
		if (!ReplicatedDataValidator.IsSerializable(typeof(T)) || IdOf(entity) is not uint id) { Dropped++; return; }
		ushort key = KeyOf(typeof(T));
		byte[] bytes = BytesOf(in data);
		SampledOut++;
		if (typeof(T) == typeof(Keen.VRage.Game.Multiplayer.EntityMotionState) && Host.IsServer)
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
		StoryOut story;
		try { story = ToStoryOut(storyMove, id); }
		catch (Exception ex) when (ex.CanHandle()) { return KTask.FromException<StoryProgress>(ex); }
		var tcs = new Keen.VRage.Library.Threading.TaskCompletionSource<StoryProgress>($"Y51 story #{id} to {PeerClientId}");
		_storiesInFlight[id] = tcs;
		_storyTypes[id] = storyMove.StateMachine.GetType();
		_sequence.Add(story);
		StoriesOut++;
		EngineCoverage.Note("story", story.Name, Out);
		return tcs.Task;
	}

	protected void EnqueueReplicationPacket(byte[] archive) => _replicationPackets.Add(archive);

	/// <summary>A desync check or report: it goes last in the next frame, after everything queued before it.</summary>
	protected void EnqueueDesync(byte[] bytes) => _desyncOut.Add(bytes);

	protected string Out => Host.IsServer ? "server->client" : "client->server";

	protected string In => Host.IsServer ? "client->server applied" : "server->client applied";

	private void EnqueueStoryData(StoryOut story) => _sequence.Add(story);

	/// <summary>Scene thread, once per peer dispatch.</summary>
	public void Dispatch()
	{
		if (_disposed) return;
		_dispatches++;
		BeforeDispatch();
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
		// Stories of this side that could not go out: their waits end after the frame is sent (a wait's continuation runs
		// inline and may queue new units, which then belong to the next frame).
		List<StoryOut>? unsent = null;
		if (_sequence.Count > 0)
		{
			var all = new List<object>(_sequence);
			_sequence.Clear();
			// In order: this player's own signals as one unit, a tick's shared signals as the unit every player got, each
			// story in a unit of its own.
			var units = new List<OwnSignal>();
			foreach (object unit in all)
			{
				if (unit is StoryOut story)
				{
					WriteUnits(w, units);
					units.Clear();
					WriteStory(w, story, ref unsent);
					continue;
				}
				if (unit is not SharedSignals shared) { units.Add((OwnSignal)unit); continue; }
				WriteUnits(w, units);
				units.Clear();
				if (shared.Archive(this) is { } bytes) w.Archive(EngineWire.Unit.SequenceArchive, bytes);
				else units.AddRange(shared.Units);
			}
			WriteUnits(w, units);
		}
		foreach (byte[] desync in _desyncOut) w.Archive(EngineWire.Unit.Desync, desync);
		_desyncOut.Clear();
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
				if (Host.IsServer) CollectionsMarshal.GetValueRefOrAddDefault(_motionPerEntity, e.Item1, out _)++;
			}
			foreach (byte[] message in EngineWire.EncodeState(stamp, entries, SyncProtocol.StateMessageBytes))
			{
				BytesOut += message.Length;
				StateBytesOut += message.Length;
				Send(message, state: true);
			}
		}
		if (unsent != null) EndUnsentWaits(unsent);
		AfterDispatch();
		EngineCoverage.MaybeWrite();
	}

	// A story goes in a unit of its own, headed by its id: the other side answers it even if it cannot read it.
	private void WriteStory(EngineWire.Writer w, StoryOut story, ref List<StoryOut>? unsent)
	{
		try
		{
			byte[] bytes = SerializeStory(story);
			w.Story(story.Id.Value, story.Id.IsFromServer, story.Completion != null, bytes);
			EngineCoverage.Note("archive", "story", Out, bytes.Length + 14);
			return;
		}
		catch (Exception ex) when (ex.CanHandle())
		{
			Dropped++;
			if (SkippedAnswer(story) is { } answer)
			{
				try { w.Story(answer.Id.Value, answer.Id.IsFromServer, true, SerializeStory(answer)); }
				catch (Exception again) when (again.CanHandle()) { GameLog.Instance.WarnOnce("engine-skip-answer-" + Name, $"engine {Name}: the skipped answer to {story.Name} could not be written either ({Innermost(again)})"); }
			}
			GameLog.Instance.WarnOnce("engine-unit-" + Name + story.Name, $"engine {Name}: {story.Name} could not be serialized ({Innermost(ex)}); {(story.Completion != null || Host.Replication.IsValidForeignStoryId(story.Id) ? "answered as skipped" : "not sent")}");
			if (story.Move != null) (unsent ??= new()).Add(story);
		}
	}

	public long StoriesLost;

	// A player's answer that ends a server story with an error would reach the game's crash handler like any unawaited
	// faulted story; on the server a player's fault counts as a skip.
	// The game casts a story's result to the waiting story's result type; one of another type (only a hostile peer sends
	// that) would throw where nothing catches it.
	private IStoryCompletion FitsStory(IStoryCompletion completion, Type? sent)
	{
		var type = completion.GetType();
		if (sent == null || !type.IsGenericType || type.GetGenericTypeDefinition() != typeof(StoryResult<>)) return completion;
		if (OwnStories.ResultTypeOf(sent) is not { } expected || expected == type.GetGenericArguments()[0]) return completion;
		GameLog.Instance.WarnOnce("engine-story-result-" + Name, $"engine {Name}: a story came back with a {OwnStories.Short(type.GetGenericArguments()[0].FullName ?? "?")} result where {OwnStories.Short(expected.FullName ?? "?")} was expected; it ends as skipped");
		return new StoryFault(new SkipNetworkStoryException());
	}

	private IStoryCompletion ServerSafe(IStoryCompletion completion)
		=> Host.IsServer && completion is StoryFault { Exception: not SkipNetworkStoryException } ? new StoryFault(new SkipNetworkStoryException()) : completion;

	// One frame of a player's that names many stories this side cannot read is answered for the first few only, and a
	// player that keeps sending them is disconnected (each answer costs the server a serialization).
	private const int LostAnswersPerFrame = 32, LostStoriesPerMinute = 256;
	private int _lostThisFrame;
	private long _lostWindowMs;
	private int _lostInWindow;

	/// <summary>A story whose archive this side could not read or apply (an object it names is not here): answered as skipped by its id,
	/// or this side's own wait for it ends as skipped, instead of the story waiting forever.</summary>
	public void OnStory(long id, bool fromServer, bool completion, byte[] bytes)
	{
		var storyId = new StoryId(id, fromServer);
		bool handled = false;
		try
		{
			object unit = OwnUnits.ReadStory(storyId, bytes, Contexts());
			UnitsIn++;
			handled = true;
			try { ProcessStory(storyId, unit); }
			catch (Exception ex) when (ex.CanHandle())
			{
				UnitsFailed++;
				string name = unit is OwnStoryMove move ? move.Name : "story completion";
				if (!Host.IsServer) KeenNetcode.UnitFailureLog.NoteError("apply failed", name + " story", ex);
				GameLog.Instance.WarnOnce("engine-unit-story-" + Name, $"engine {Name}: applying {name} failed ({ex.GetType().Name}: {ex.Message}); further ones not logged");
			}
		}
		catch (Exception ex) when (ex.CanHandle())
		{
			UnitsFailed++;
			if (!Host.IsServer) KeenNetcode.UnitFailureLog.NoteError("story unreadable", "story", ex);
		}
		if (handled) return;
		StoriesLost++;
		if (Host.IsServer)
		{
			long now = MpClock.NowMs();
			if (now - _lostWindowMs > 60_000) { _lostWindowMs = now; _lostInWindow = 0; }
			if (++_lostInWindow > LostStoriesPerMinute && !_overflowed)
			{
				_overflowed = true;
				GameLog.Instance.Warn($"engine {Name}: the player sent more than {LostStoriesPerMinute} stories a minute the server cannot read; disconnecting it");
			}
		}
		if (_storiesInFlight.Remove(storyId, out var tcs))
		{
			_storyTypes.Remove(storyId);
			GameLog.Instance.WarnOnce("engine-story-lost-own-" + Name, $"engine {Name}: the answer to this side's story {storyId} could not be read here; it ends as skipped (further ones counted in the stats line)");
			try { tcs.SetResult(new StoryProgress.Complete(new StoryFault(new SkipNetworkStoryException()))); }
			catch (Exception ex) { GameLog.Instance.WarnOnce("engine-story-lost-end-" + Name, $"engine {Name}: ending the wait of an unreadable story failed ({Innermost(ex)})"); }
		}
		else if (!completion && ++_lostThisFrame <= LostAnswersPerFrame && Host.Replication.IsValidForeignStoryId(storyId))
		{
			GameLog.Instance.WarnOnce("engine-story-lost-" + Name, $"engine {Name}: a story ({storyId}) names something this side does not have; answered as skipped (further ones counted in the stats line)");
			EnqueueStoryData(new StoryOut { Id = storyId, Completion = new StoryFault(new SkipNetworkStoryException()) });
		}
	}

	private void WriteUnits(EngineWire.Writer w, List<OwnSignal> units)
	{
		if (units.Count == 0) return;
		try
		{
			byte[] bytes = SerializeSignals(units);
			w.Archive(EngineWire.Unit.SequenceArchive, bytes);
			EngineCoverage.Note("archive", "signals", Out, bytes.Length + 5);
			// Once in a while each signal alone, for what each kind costs on the wire.
			if (EngineCoverage.On && ++_sizeSample % 120 == 0)
				foreach (OwnSignal one in units) EngineCoverage.Note("signal-bytes", one.Name, Out, SerializeSignals(new List<OwnSignal> { one }).Length);
		}
		catch (Exception ex)
		{
			// One signal that could not be written used to drop the whole frame.
			Salvage(w, units, ex);
		}
	}

	/// <summary>Writes what can be written of a frame's signals that failed as a whole; the rest is dropped and logged.</summary>
	private void Salvage(EngineWire.Writer w, List<OwnSignal> units, Exception first)
	{
		(List<OwnSignal> Kept, List<SequenceSalvage.Failure<OwnSignal>> Failed)? salvage;
		try { salvage = SequenceSalvage.Keep<OwnSignal>(units, TryWrite, _ => null); }
		catch (Exception ex) { GameLog.Instance.WarnOnce("engine-salvage-" + Name, $"engine {Name}: sorting out a frame that could not be written failed ({Innermost(ex)})"); salvage = null; }
		if (salvage is not { } result)
		{
			Dropped += units.Count;
			GameLog.Instance.WarnOnce("engine-seq-" + Name, $"engine {Name}: a frame's signals could not be serialized and were dropped ({Innermost(first)})");
			return;
		}
		try
		{
			if (result.Kept.Count > 0) w.Archive(EngineWire.Unit.SequenceArchive, SerializeSignals(result.Kept));
		}
		catch (Exception again)
		{
			Dropped += result.Kept.Count;
			GameLog.Instance.WarnOnce("engine-seq-kept-" + Name, $"engine {Name}: a frame's signals could not be serialized and were dropped ({Innermost(again)})");
		}
		foreach (var failure in result.Failed)
		{
			Dropped++;
			string name = OwnSignals.Short(failure.Unit.Name);
			GameLog.Instance.WarnOnce("engine-unit-" + Name + name, $"engine {Name}: signal {name} could not be serialized ({Innermost(failure.Error)}); dropped");
		}
	}

	private Exception? TryWrite(List<OwnSignal> units)
	{
		try { SerializeSignals(units); return null; }
		catch (Exception ex) { return ex; }
	}

	// A story that cannot go out is answered as skipped, the way the game skips one in its own replication, when the peer
	// waits for it (an answer to its story, or any completion); this side's own wait ends in EndUnsentWaits.
	private StoryOut? SkippedAnswer(StoryOut story)
	{
		bool peerWaits = story.Completion != null || Host.Replication.IsValidForeignStoryId(story.Id);
		return peerWaits ? new StoryOut { Id = story.Id, Completion = new StoryFault(new SkipNetworkStoryException()) } : null;
	}

	private void EndUnsentWaits(List<StoryOut> unsent)
	{
		foreach (StoryOut story in unsent)
		{
			if (!_storiesInFlight.Remove(story.Id, out var tcs)) continue;
			_storyTypes.Remove(story.Id);
			try { tcs.SetResult(new StoryProgress.Complete(new StoryFault(new SkipNetworkStoryException()))); }
			catch (Exception ex) { GameLog.Instance.WarnOnce("engine-unsent-" + Name, $"engine {Name}: ending the wait of a story that was not sent failed ({Innermost(ex)})"); }
		}
	}

	protected abstract SyncMsg UpstreamKind { get; }

	protected abstract void Send(byte[] message, bool state);

	protected virtual void BeforeDispatch() { }

	protected virtual void AfterDispatch() { }

	/// <summary>A desync check (on a player's side) or the report answering one (on the server).</summary>
	protected virtual void OnDesync(byte[] bytes) { }

	private static string Innermost(Exception ex) => OwnArchive.Innermost(ex);

	private byte[] SerializeSignals(List<OwnSignal> signals) => OwnUnits.WriteSignals(signals, Host.Ids, Contexts());

	private byte[] SerializeStory(StoryOut story) => story.Move != null ? OwnUnits.WriteMove(story.Move, Contexts()) : OwnUnits.WriteCompletion(story.Completion!, Contexts());

	/// <summary>The services this peer's units are written and read with (its target, SE2 Online's ids).</summary>
	internal ServiceLocatorContext.Builder Contexts() => Host.Contexts(TargetContext);

	private uint? IdOf(Entity entity)
	{
		if (Host.Ids.TryGetId(entity) is not { } id) return null;
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

	private StoryOut ToStoryOut(StoryProgress story, StoryId id)
	{
		if (story is StoryProgress.Complete complete) return new StoryOut { Id = id, Completion = complete.Completion };
		var move = (StoryProgress.Move)story;
		var captured = OwnStories.Capture(id, move.StateMachine, Host.Ids, out string? why);
		if (captured == null)
		{
			string name = OwnStories.Short(move.StateMachine.GetType().FullName ?? "?");
			GameLog.Instance.WarnOnce("engine-story-skip-" + name, $"engine {Name}: story {name} stays on this side as skipped ({why}); further ones not logged");
			throw new SkipNetworkStoryException();
		}
		return new StoryOut { Id = id, Move = captured };
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
		bool server = Host.IsServer;
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
		TakeFrames();
		ApplyParked();
		while (_state.TryDequeue(out byte[] message))
		{
			try { EngineWire.ReadState(message, this); }
			catch (Exception ex) { GameLog.Instance.WarnOnce("engine-state-" + Name, $"engine {Name}: a state message could not be read ({ex.GetType().Name}: {ex.Message})"); }
		}
		if (!Host.IsServer && _newestMotion != 0)
		{
			KeenNetcode.AnimPlayout.NoteSamples(_newestMotion);
#if !PULSAR
			KeenNetcode.AnimSyncProbe.NoteSample(_newestMotion);
#endif
			_newestMotion = 0;
		}
	}

	private void TakeFrames()
	{
		// Within the job's budget (the server's, OwnArchive): this player's first frame always, further ones while it lasts.
		bool first = true;
		while ((first || !OwnArchive.BudgetSpent) && _frames.TryTake(out _, out byte[] frame))
		{
			first = false;
			FramesIn++;
			_lostThisFrame = 0;
			System.Threading.Interlocked.Add(ref _pendingBytes, -frame.Length);
			try { EngineWire.Read(_inflater.Unpack(frame), this); }
			catch (Exception ex)
			{
				UnitsFailed++;
				if (!Host.IsServer) KeenNetcode.UnitFailureLog.NoteError("frame unreadable", Name, ex);
				GameLog.Instance.WarnOnce("engine-frame-" + Name, $"engine {Name}: a frame could not be read ({ex.GetType().Name}: {ex.Message}); the rest of it is skipped");
			}
		}
	}

	public void OnTypeDef(ushort key, string name)
	{
		(Type, int, int)? resolved = null;
		try
		{
			var type = OwnUnits.Find(name);
			if (type != null && type.IsValueType && OwnSignals.Raw.IsUnmanaged(type))
			{
				var info = RuntimeDataInfo.Of(type);
				resolved = (type, info.TypeId, info.Size);
			}
		}
		catch (Exception) { }
		if (resolved == null) GameLog.Instance.WarnOnce("engine-type-" + Name, $"engine {Name}: a data type ({(name.Length > 200 ? name[..200] : name)}) is not known here; its values are skipped (further ones not logged)");
		_inKeys[key] = resolved;
	}

	public void OnArchive(EngineWire.Unit unit, byte[] bytes)
	{
		if (unit == EngineWire.Unit.ReplicationArchive)
		{
			UnitsIn++;
			try { HandleReplication(bytes); }
			catch (Exception ex) when (ex.CanHandle())
			{
				UnitsFailed++;
				if (!Host.IsServer) KeenNetcode.UnitFailureLog.NoteError("apply failed", "object batch", ex);
				GameLog.Instance.WarnOnce("engine-batch-" + Name, $"engine {Name}: an object batch could not be applied ({Innermost(ex)})");
			}
			return;
		}
		if (unit == EngineWire.Unit.Desync)
		{
			try { OnDesync(bytes); }
			catch (Exception ex) when (ex.CanHandle()) { GameLog.Instance.WarnOnce("engine-desync-" + Name, $"engine {Name}: a desync check or report could not be read ({Innermost(ex)})"); }
			return;
		}
		List<OwnSignal> signals;
		int unreadable;
		string? firstError;
		try { signals = OwnUnits.ReadSignals(bytes, Host.Ids, Contexts(), out unreadable, out firstError); }
		catch (Exception ex) when (ex.CanHandle())
		{
			// One unreadable unit used to end the frame: the data, removals and sampled values after it were lost too.
			UnitsFailed++;
			if (!Host.IsServer) KeenNetcode.UnitFailureLog.NoteError("archive unreadable", unit.ToString(), ex);
			GameLog.Instance.WarnOnce("engine-archive-" + Name, $"engine {Name}: a signal unit could not be read ({Innermost(ex)}); the rest of the frame applies");
			return;
		}
		if (unreadable > 0)
		{
			// A signal naming an object this side does not have (the game skips such a signal in its own replication too).
			UnitsFailed += unreadable;
			if (!Host.IsServer) KeenNetcode.UnitFailureLog.Note("signal unreadable", firstError ?? "signal", firstError ?? "");
		}
		foreach (OwnSignal signal in signals)
		{
			UnitsIn++;
			try { HandleSignal(signal); }
			catch (Exception ex) when (ex.CanHandle())
			{
				UnitsFailed++;
				string name = OwnSignals.Short(signal.Name);
				if (!Host.IsServer) KeenNetcode.UnitFailureLog.NoteError("apply failed", name, ex);
				GameLog.Instance.WarnOnce("engine-unit-" + name, $"engine {Name}: applying {name} failed ({ex.GetType().Name}: {ex.Message})");
			}
		}
	}

	private void HandleSignal(OwnSignal signal)
	{
		SignalsIn++;
		EngineCoverage.Note("signal", signal.Name, In);
		if (!AcceptSignal(signal)) return;
		if (!Host.IsServer && !NoSignalHold && Building(signal)) { _parkedSignals.Add((signal, MpClock.NowMs())); SignalsParked++; return; }
		if (!Host.IsServer) { Replay(signal); return; }
		var sender = KeenNetcode.ServerAccess.EnterSignal(PeerClientId);
		try { Replay(signal); }
		finally { KeenNetcode.ServerAccess.LeaveSignal(sender); }
	}

	private void Replay(OwnSignal signal)
	{
		if (!OwnSignals.Replay(Host, signal))
			GameLog.Instance.WarnOnce("engine-signal-unknown-" + Name, $"engine {Name}: a signal ({OwnSignals.Short(signal.Name)}) is not known to this game or does not fit it; skipped (further ones not logged)");
	}

	protected virtual bool AcceptSignal(OwnSignal signal) => true;

	/// <summary>An object batch, destruction or acknowledgement (<see cref="OwnUnits"/>).</summary>
	protected abstract void HandleReplication(byte[] unit);

	public void OnData(uint netId, ushort key, ReadOnlySpan<byte> bytes)
	{
		DataIn++;
		try
		{
			if (!Resolve(netId, key, bytes.Length, out var entity, out int typeId) || !AcceptData(typeId, entity, removal: false)) return;
			// An entity still being built (no live entity yet) takes its newest value once it exists; writing to it threw and
			// lost the rest of the frame.
			if (!Host.IsServer && !Scene.IsEntityAlive(entity.DEntity))
			{
				_parkedData[(netId, key)] = (bytes.ToArray(), MpClock.NowMs());
				return;
			}
			_parkedData.Remove((netId, key));
			Write(entity, typeId, bytes);
			EngineCoverage.Note("data", RuntimeDataInfo.Of(typeId).RuntimeType.FullName!, In);
		}
		catch (Exception ex) when (ex.CanHandle()) { UnitFailed("data", ex); }
	}

	public void OnDataRemove(uint netId, ushort key)
	{
		try
		{
			_parkedData.Remove((netId, key));
			if (Resolve(netId, key, -1, out var entity, out int typeId) && Scene.IsEntityAlive(entity.DEntity) && AcceptData(typeId, entity, removal: true))
				DataMarshal.TryRemove(Scene, entity.DEntity, typeId);
		}
		catch (Exception ex) when (ex.CanHandle()) { UnitFailed("data removal", ex); }
	}

	private readonly Dictionary<(uint NetId, ushort Key), (byte[] Bytes, long AtMs)> _parkedData = new();

	/// <summary>An object destroyed here: values held for its id must not land on whatever gets that id next.</summary>
	protected void ForgetHeld(ulong netId)
	{
		if (_parkedData.Count > 0) foreach (var key in _parkedData.Keys.Where(k => k.NetId == netId).ToList()) _parkedData.Remove(key);
		if (_parked.Count > 0) foreach (var key in _parked.Keys.Where(k => k.NetId == netId).ToList()) _parked.Remove(key);
	}

	private void UnitFailed(string what, Exception ex)
	{
		UnitsFailed++;
		if (!Host.IsServer) KeenNetcode.UnitFailureLog.NoteError(what + " failed", what, ex);
		GameLog.Instance.WarnOnce("engine-" + what + "-" + Name, $"engine {Name}: applying {what} failed ({ex.GetType().Name}: {ex.Message}); only that value is skipped");
	}

	public void OnSampled(long stamp, bool reliable, uint netId, ushort key, ReadOnlySpan<byte> bytes)
	{
		try { OnSampledCore(stamp, reliable, netId, key, bytes); }
		catch (Exception ex) when (ex.CanHandle()) { UnitFailed("sampled value", ex); }
	}

	private void OnSampledCore(long stamp, bool reliable, uint netId, ushort key, ReadOnlySpan<byte> bytes)
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
		if (!Host.IsServer && KeenNetcode.MotionSmoothing.TryHold(entity, in motion)) { }
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
	private readonly List<(OwnSignal Signal, long AtMs)> _parkedSignals = new();

	// Kill switch for bisecting (-y51online:signalhold:off): signals reach entities still being built or staged at once.
	private static readonly bool NoSignalHold = MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "signalhold:off");

	private bool Building(OwnSignal signal)
	{
		try { return signal.Target is Entity target && !InScene(target); }
		catch (Exception) { return false; }
	}

	// Built and in the scene: a staged entity is alive but keeps StagingTag until its staged batch completes.
	private bool InScene(Entity e) => Scene.IsEntityAlive(e.DEntity) && !e.Data.Has<Keen.VRage.DCS.CoreData.StagingTag>();

	private void ApplyParkedSignals()
	{
		if (_parkedSignals.Count == 0) return;
		long now = MpClock.NowMs();
		var still = new List<(OwnSignal, long)>();
		foreach (var (signal, at) in _parkedSignals)
		{
			if (Building(signal))
			{
				if (now - at <= 120_000) still.Add((signal, at));
				continue;
			}
			try { Replay(signal); ParkedSignalsApplied++; }
			catch (Exception ex) when (ex.CanHandle()) { UnitsFailed++; KeenNetcode.UnitFailureLog.NoteError("held signal failed", "signal", ex); GameLog.Instance.WarnOnce("engine-parked-signal", $"engine {Name}: a held signal failed ({ex.GetType().Name}: {ex.Message})"); }
		}
		_parkedSignals.Clear();
		_parkedSignals.AddRange(still);
	}

	/// <summary>Sampled values that arrived before their entity was in the scene, applied once it is (scene thread).</summary>
	protected void ApplyParked()
	{
		// Data first, as a live frame applies it before its signals.
		ApplyParkedData();
		ApplyParkedSignals();
		if (_parked.Count == 0) return;
		long now = MpClock.NowMs();
		List<(uint, ushort)>? done = null;
		foreach (var (id, value) in _parked)
		{
			if (now - value.AtMs > 30_000) { (done ??= new()).Add(id); continue; }
			if (!Resolve(id.NetId, id.Key, value.Bytes.Length, out var entity, out int typeId) || !InScene(entity)) continue;
			(done ??= new()).Add(id);
			try { ApplySampled(id.NetId, entity, typeId, value.Bytes); ParkedApplied++; }
			catch (Exception ex) when (ex.CanHandle()) { UnitFailed("held sampled value", ex); }
		}
		if (done != null) foreach (var id in done) _parked.Remove(id);
	}

	// Entity data for entities that were still being built when it came: the newest value per entity and type, applied once
	// the entity exists; dropped after 120 s (an entity that never arrived, or was removed meanwhile).
	private void ApplyParkedData()
	{
		if (_parkedData.Count == 0) return;
		long now = MpClock.NowMs();
		List<(uint, ushort)>? done = null;
		foreach (var (id, value) in _parkedData)
		{
			try
			{
				if (now - value.AtMs > 120_000) { (done ??= new()).Add(id); continue; }
				if (!Resolve(id.NetId, id.Key, value.Bytes.Length, out var entity, out int typeId))
				{
					(done ??= new()).Add(id);
					continue;
				}
				if (!Scene.IsEntityAlive(entity.DEntity)) continue;
				Write(entity, typeId, value.Bytes);
				ParkedApplied++;
				(done ??= new()).Add(id);
			}
			catch (Exception ex) when (ex.CanHandle())
			{
				(done ??= new()).Add(id);
				UnitFailed("held data", ex);
			}
		}
		if (done != null) foreach (var id in done) _parkedData.Remove(id);
	}

	private static readonly int MotionTypeId = RuntimeDataInfo.Of(typeof(Keen.VRage.Game.Multiplayer.EntityMotionState)).TypeId;

	private long _newestMotion;

	private long _sizeSample;

	public long PropertiesRefreshed;

	protected void RefreshSyncProperties(IEnumerable<Entity> roots, long sinceMs, bool notSavedOnly = false)
	{
		try
		{
			var signals = SyncProperties.Current(Host.Session, Host.Core, _signals, roots, sinceMs, notSavedOnly);
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

	public string DescribeLane() => $"shared signal archives {SharedArchives} used {SharedUses} times; wire: frames {FrameBytesOut / 1024} KiB, state {StateBytesOut / 1024} KiB; data coalesced {_lane.Coalesced}, unchanged skipped {_lane.Unchanged}, rate-deferred {_lane.Deferred}, motion thinned {MotionThinned}, repeated signals skipped {_signalDedupe.Skipped}, own-body changes {OwnChanges}, sync properties refreshed {PropertiesRefreshed}, signals held for entities being built {SignalsParked} (applied {ParkedSignalsApplied}), motion restarts {MotionRestarts}";

	private void NoteMotion(long serverTicks)
	{
		if (serverTicks > _newestMotion) _newestMotion = serverTicks;
	}

	public void OnSampledRemove(long stamp, uint netId, ushort key)
	{
		try
		{
			if (!Fresh(netId, key, stamp, reliable: true)) return;
			_parked.Remove((netId, key));
			if (Resolve(netId, key, -1, out var entity, out int typeId) && Scene.IsEntityAlive(entity.DEntity) && AcceptData(typeId, entity, removal: true))
				DataMarshal.TryRemove(Scene, entity.DEntity, typeId);
		}
		catch (Exception ex) when (ex.CanHandle()) { UnitFailed("sampled removal", ex); }
	}

	// Reliable values win ties: a final reliable value supersedes in-flight unreliable ones from the same dispatch.
	private bool Fresh(uint netId, ushort key, long stamp, bool reliable)
	{
		// A player only writes to objects the server has: an id that names nothing is not remembered.
		if (Host.IsServer && Host.Ids.TryGetObject(new NetworkId(netId)) == null) return false;
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
		if (Host.Ids.TryGetObject(new NetworkId(netId)) is not Entity e) { Dropped++; return false; }
		entity = e;
		typeId = t.TypeId;
		return true;
	}

	private unsafe void Write(Entity entity, int typeId, ReadOnlySpan<byte> bytes)
	{
		ref byte target = ref DataMarshal.GetDataPointer(Scene, entity.DEntity, typeId);
		bytes.CopyTo(MemoryMarshal.CreateSpan(ref target, bytes.Length));
	}

	// --- stories ---

	private void ProcessStory(StoryId id, object unit)
	{
		if (_storiesInFlight.Remove(id, out var tcs))
		{
			_storyTypes.Remove(id, out var sent);
			if (unit is OwnStoryMove back && KeenNetcode.ServerAccess.ReturningStoryRefusal(PeerClientId, sent, back.TypeName) is { } refused)
			{
				tcs.SetResult(new StoryProgress.Complete(new StoryFault(refused)));
				return;
			}
			// A returning story that cannot be restored here ends this side's wait (it used to wait forever).
			StoryProgress progress;
			try
			{
				progress = unit is OwnStoryMove move
					? new StoryProgress.Move(RestoreStory(PeerClientId, id, move))
					: new StoryProgress.Complete(ServerSafe(FitsStory((IStoryCompletion)unit, sent)));
			}
			catch (Exception ex) when (ex.CanHandle())
			{
				UnitsFailed++;
				GameLog.Instance.WarnOnce("engine-story-restore-" + Name, $"engine {Name}: a story coming back could not be restored ({ex.GetType().Name}: {ex.Message}); it ends as skipped");
				// As skipped, not with the error: a story nobody awaits that ends in an error goes to the game's crash handler
				// (a client could close the server by sending back a story of the right type that does not restore).
				progress = new StoryProgress.Complete(new StoryFault(new SkipNetworkStoryException()));
			}
			tcs.SetResult(progress);
			return;
		}
		if (unit is not OwnStoryMove moved)
		{
			GameLog.Instance.WarnOnce("engine-story-unexpected-" + Name, $"engine {Name}: completion for story {id} that was never sent");
			return;
		}
		if (!Host.Replication.IsValidForeignStoryId(id))
		{
			GameLog.Instance.WarnOnce("engine-story-id-" + Name, $"engine {Name}: moved story {id} is not one this side may host");
			return;
		}
		HostStory(moved).SkipWait();
	}

	private async KTask HostStory(OwnStoryMove move)
	{
		await KTask.SetLifetime(Host.Replication);
		StoryId original = move.Id;
		StoryId local = Host.Replication.AllocateStoryId();
		StoryProgress result;
		try
		{
			// The story's server part runs inside HostNetworkStory up to its first wait: the access checks know its caller.
			var caller = Host.IsServer ? KeenNetcode.ServerAccess.EnterStory(PeerClientId) : default;
			Keen.VRage.Library.Threading.Task<StoryProgress> hosted;
			IAsyncStateMachine? machine = null;
			try
			{
				try { machine = RestoreStory(PeerClientId, local, move); }
				catch (Exception ex) when (ex.CanHandle())
				{
					// Not the story's own fault: the other side's wait ends as skipped (a fault it does not await would reach its crash handler).
					UnitsFailed++;
					GameLog.Instance.WarnOnce("engine-story-host-" + Name, $"engine {Name}: a moved story could not be restored ({ex.GetType().Name}: {ex.Message}); answered as skipped");
				}
				hosted = machine == null ? default : Host.Replication.HostNetworkStory(PeerClientId, local, machine);
			}
			finally { if (Host.IsServer) KeenNetcode.ServerAccess.LeaveStory(caller); }
			result = machine == null ? new StoryProgress.Complete(new StoryFault(new SkipNetworkStoryException())) : await hosted;
		}
		catch (Exception ex) when (ex.CanHandle())
		{
			result = new StoryProgress.Complete(new StoryFault(ex));
		}
		StoryOut back;
		try { back = ToStoryOut(result, original); }
		catch (Exception ex) when (ex.CanHandle()) { back = new StoryOut { Id = original, Completion = new StoryFault(ex) }; }
		if (!_disposed) EnqueueStoryData(back);
	}

	/// <summary>A moved story rebuilt on this side and bound to its narrator (the server also fills in the request's caller).</summary>
	protected virtual IAsyncStateMachine RestoreStory(ClientId sender, StoryId storyId, OwnStoryMove move) => OwnStories.Restore(move, storyId, Host.Replication, Host.IsServer);

	public string Describe() => $"{Name}: frames out {FramesOut} in {FramesIn} ({MpLogExtensions.Bytes(BytesOut)} / {MpLogExtensions.Bytes(BytesIn)}), signals {SignalsOut}/{SignalsIn}, data {DataOut}/{DataIn}, sampled {SampledOut}/{SampledIn} ({StaleSampled} stale), stories {StoriesOut}/{StoriesIn}, units failed {UnitsFailed}, stories unreadable {StoriesLost}, dropped {Dropped}, waiting for frame #{_frames.NextExpected} ({_frames.Buffered} early)";
}
