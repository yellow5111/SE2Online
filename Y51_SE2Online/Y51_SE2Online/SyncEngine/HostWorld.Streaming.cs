using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Threading;
using System;
using Keen.Game2.Client.GameSystems.PlayerControl;
using Keen.Game2.Client.WorldObjects.Character;
using Keen.Game2.Game.SessionComponents;
using Keen.Game2.Simulation.GameSystems.Control;
using Keen.Game2.Simulation.GameSystems.Ownership;
using Keen.Game2.Simulation.GameSystems.Permissions;
using Keen.Game2.Simulation.GameSystems.Player;
using Keen.Game2.Simulation.GameSystems.Respawn;
using Keen.Game2.Simulation.RuntimeSystems.Saves;
using Keen.Game2.Simulation.Utils;
using Keen.Game2.Simulation.WorldObjects.Characters;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.Pilotable;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.Game2.Simulation.WorldObjects.SafeZones;
using Keen.Game2;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.Core;
using Keen.VRage.DCS.Annotations.Signals;
using Keen.VRage.DCS.Builders;
using Keen.VRage.DCS.Components;
using Keen.VRage.DCS.CoreData;
using Keen.VRage.DCS.ObjectBuilders;
using Keen.VRage.Library.Collections;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Library.Utils;
using Keen.VRage.Multiplayer.Replications.FailureStrategies;
using Keen.VRage.Multiplayer.Replications.InProcess;
using Keen.VRage.Multiplayer.Sessions;
using Keen.VRage.Voxels.Components;
using VRage.Multiplayer.Replication.CB;
using Y51_SE2Online.Core;
using Y51_SE2Online.Dedicated;
using Y51_SE2Online.Net;
using Y51_SE2Online.Probe;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// Per-joiner streaming radius: each joiner only gets the grids, characters and objects within
/// <see cref="HostSession.StreamingRadiusMeters"/> of its character. Groups of mechanically connected grids move in and
/// out together; planets, asteroids, safe zones and session-level entities are always sent. Out of range means removed
/// on that joiner like a destroyed entity, and back in range is a fresh spawn bundle for that joiner only.
/// </summary>
internal sealed partial class HostWorld
{
	private sealed class StreamGroup
	{
		public readonly List<(Entity Root, uint Id)> Roots = new();
		public readonly List<Vector3D> Positions = new();
		public bool Always;

		public double DistanceTo(Vector3D anchor) => Positions.Count == 0 ? 0 : Positions.Min(p => Vector3D.Distance(p, anchor));
	}

	private sealed class StreamState
	{
		/// <summary>Root NetIds this joiner does not have. Guarded by <see cref="_streamSync"/>.</summary>
		public readonly HashSet<uint> Out = new();
		public bool Dirty;
		public long StreamedIn, StreamedOut, EntitiesIn, EntitiesOut;
	}

	private const int StreamIntervalMs = 500;
	private const int StreamInGroupsPerUpdate = 12;
	private const int StreamInEntitiesPerUpdate = 4000;

	private readonly ConcurrentDictionary<PeerId, StreamState> _streams = new();
	private readonly object _streamSync = new();
	private long _nextStreamMs, _nextStreamLogMs;
	private volatile bool _streamingActive;

	/// <summary>True while any joiner may have something streamed out; the per-entity root lookups only run then.</summary>
	internal bool StreamingActive => _streamingActive;

	internal uint RootIdOf(Entity entity)
	{
		try
		{
			for (int depth = 0; depth < 64 && entity.TryGet<HierarchyComponent>()?.Parent is Entity parent; depth++) entity = parent;
			return _ids.TryGet(entity, out uint id) ? id : 0u;
		}
		catch (Exception)
		{
			return 0u;
		}
	}

	private static bool AlwaysStreamed(Entity root) =>
		!root.Data.Has<WorldTransform>()
		|| root.TryGet<PlanetEntityComponent>() != null
		|| root.TryGet<SafeZoneComponent>() != null
		|| root.Components.OfType<VoxelStorageComponentBase>().Any();

	private List<StreamGroup> CollectStreamGroups(Session session, Entity sessionEntity)
	{
		var groups = new List<StreamGroup>();
		var seen = new HashSet<Entity>(ReferenceEqualityComparer.Instance);
		foreach (var entity in session.Entities)
		{
			if (entity == sessionEntity || entity.Data.Has<ParentData>() || !_ids.TryGet(entity, out uint id) || !seen.Add(entity))
			{
				continue;
			}
			var group = new StreamGroup();
			AddToGroup(group, entity, id);
			if (entity.TryGet<CubeGridComponent>()?.GridGraphHandler is { } graph)
			{
				foreach (var other in graph.GetAllConnectedEntities(entity))
				{
					if (other != entity && !other.Data.Has<ParentData>() && _ids.TryGet(other, out uint otherId) && seen.Add(other)) AddToGroup(group, other, otherId);
				}
			}
			groups.Add(group);
		}
		return groups;
	}

	private static void AddToGroup(StreamGroup group, Entity root, uint id)
	{
		group.Roots.Add((root, id));
		group.Always |= AlwaysStreamed(root);
		if (root.Data.TryGet<WorldTransform>(out var transform)) group.Positions.Add(transform.Position);
	}

	private static Vector3D? AnchorOf(Entity? character)
	{
		if (character == null || EngineCodec.IsDead(character)) return null;
		try { return character.Data.GetWorldTransform().Position; }
		catch (Exception) { return null; }
	}

	private void UpdateStreaming(InProcessReplicationSessionComponent comp)
	{
		var host = _host;
		long now = MpClock.NowMs();
		if (host == null || now < _nextStreamMs) return;
		_nextStreamMs = now + StreamIntervalMs;
		double radius = host.StreamingRadiusMeters;
		bool anyOut;
		lock (_streamSync) anyOut = _streams.Values.Any(s => s.Out.Count > 0);
		_streamingActive = radius > 0 || anyOut;
		if (!_streamingActive) return;
		var watch = Stopwatch.StartNew();
		var groups = CollectStreamGroups(comp.Session, comp.Entity);
		double collectMs = watch.Elapsed.TotalMilliseconds, outMs = 0, inMs = 0;
		var strategy = ((SceneContext)comp._context).Replication.ReplicationStrategy;
		foreach (var j in _joiners.Values)
		{
			if (host.StatusOf(j.Peer) != HostSession.ClientStatus.Synced) continue;
			var stream = _streams.GetOrAdd(j.Peer, _ => new StreamState());
			Vector3D? anchor = AnchorOf(j.Character);
			// No character yet (spawn menu, respawning): keep what the joiner has until it is somewhere.
			if (anchor == null && radius > 0) continue;
			var leaving = new List<uint>();
			var arriving = new List<(double Distance, StreamGroup Group)>();
			lock (_streamSync)
			{
				foreach (var group in groups)
				{
					bool present = group.Roots.Any(r => !stream.Out.Contains(r.Id));
					double distance = group.Always || anchor == null ? 0 : group.DistanceTo(anchor.Value);
					bool keep = group.Always || StreamingRules.Keep(present, distance, radius);
					if (present && !keep)
					{
						foreach (var (root, id) in group.Roots)
						{
							if (stream.Out.Add(id)) CollectIds(root, leaving);
						}
						stream.StreamedOut++;
						stream.Dirty = true;
					}
					else if (keep && group.Roots.Any(r => stream.Out.Contains(r.Id)))
					{
						arriving.Add((distance, group));
					}
				}
			}
			double t = watch.Elapsed.TotalMilliseconds;
			if (leaving.Count > 0)
			{
				// Updates for these must stop before the removal reaches the joiner.
				PushStreamedOut(host, j.Peer, stream);
				host.PublishStreamOut(j.Peer, leaving);
				stream.EntitiesOut += leaving.Count;
			}
			outMs += watch.Elapsed.TotalMilliseconds - t;
			t = watch.Elapsed.TotalMilliseconds;
			int groupsIn = 0, entitiesIn = 0;
			foreach (var (_, group) in arriving.OrderBy(a => a.Distance))
			{
				if (groupsIn >= StreamInGroupsPerUpdate || entitiesIn >= StreamInEntitiesPerUpdate) break;
				List<(Entity Root, uint Id)> roots;
				lock (_streamSync) roots = group.Roots.Where(r => stream.Out.Contains(r.Id)).ToList();
				int sent = StreamIn(comp, strategy, host, j.Peer, roots);
				if (sent < 0) continue;
				lock (_streamSync)
				{
					foreach (var r in roots) stream.Out.Remove(r.Id);
					stream.Dirty = true;
				}
				groupsIn++;
				entitiesIn += sent;
				stream.StreamedIn++;
				stream.EntitiesIn += sent;
			}
			PushStreamedOut(host, j.Peer, stream);
			inMs += watch.Elapsed.TotalMilliseconds - t;
		}
		if (watch.ElapsedMilliseconds > 50)
		{
			_log.Warn($"streaming: slow update {watch.ElapsedMilliseconds} ms (collect {groups.Count} groups {collectMs:0} ms, out {outMs:0} ms, in {inMs:0} ms)");
		}
		if (now >= _nextStreamLogMs)
		{
			_nextStreamLogMs = now + 30000;
			lock (_streamSync)
			{
				_log.Info($"streaming: radius {(radius > 0 ? $"{radius:0} m" : "off")}, {groups.Count} root groups; " + string.Join("; ", _streams.Select(kv =>
					$"{kv.Key}: {kv.Value.Out.Count} roots out, streamed in {kv.Value.StreamedIn} groups / {kv.Value.EntitiesIn} entities, out {kv.Value.StreamedOut} groups / {kv.Value.EntitiesOut} entities")));
			}
		}
	}

	private void PushStreamedOut(HostSession host, PeerId peer, StreamState stream)
	{
		HashSet<uint> copy;
		lock (_streamSync)
		{
			if (!stream.Dirty) return;
			stream.Dirty = false;
			copy = new HashSet<uint>(stream.Out);
		}
		host.SetStreamedOut(peer, copy);
	}

	private void CollectIds(Entity entity, List<uint> ids, int depth = 0)
	{
		if (_ids.TryGet(entity, out uint id)) ids.Add(id);
		if (depth > 16 || entity.TryGet<HierarchyComponent>()?.Children is not { } children) return;
		foreach (var child in children) CollectIds(child, ids, depth + 1);
	}

	/// <summary>Sends these roots (with their children) to one joiner as a spawn bundle; the entity count, or -1 if it could not be built.</summary>
	private int StreamIn(InProcessReplicationSessionComponent comp, ReplicationStrategy strategy, HostSession host, PeerId peer, List<(Entity Root, uint Id)> roots)
	{
		var bundle = new EntityBundle();
		var eobs = new Dictionary<Entity, EntityObjectBuilder>(ReferenceEqualityComparer.Instance);
		try
		{
			foreach (var (root, _) in roots)
			{
				if (EngineCodec.IsDead(root)) continue;
				strategy.SerializeForClientAsBundle(root, bundle, eobs);
			}
			if (eobs.Count == 0) return 0;
			var proxy = new NetIdProxyContext(e => (int)_ids.GetOrAssign(e), null);
			var blobs = new HashSet<Blob>();
			var msg = new EntityBundleMessage
			{
				Kind = 1,
				HostTick = _hostTick,
				Bundle = EngineCodec.SerializeBundle(bundle, proxy, blobs),
				Entities = EngineCodec.Locate(bundle, eobs, e => _ids.GetOrAssign(e), out _)
			};
			msg.RequiredBlobs = EngineCodec.Ids(blobs);
			foreach (var eob in eobs)
			{
				DEntityBuilder data = default;
				strategy.CaptureEntityData(eob.Key, eob.Value.Definition, ref data);
				var entityBlobs = EngineCodec.Blobs(ref data, Keys);
				if (entityBlobs.Count > 0) msg.Data.Add((_ids.GetOrAssign(eob.Key), entityBlobs));
			}
			if (!host.PublishLifecycleOnlyTo(peer, msg.Encode())) return -1;
			if (blobs.Count > 0) SendStreamBlobs(host, peer, blobs);
			return eobs.Count;
		}
		catch (Exception ex)
		{
			_log.WarnOnce("stream-in-" + ex.GetType().Name, $"streaming: sending {roots.Count} root(s) to {peer} failed ({ex.GetType().Name}: {ex.Message}); retrying next update");
			return -1;
		}
	}

	private void SendStreamBlobs(HostSession host, PeerId peer, HashSet<Blob> blobs)
	{
		var offloading = _offloading;
		if (offloading == null) return;
		var list = blobs.ToList();
		Task.Run(() =>
		{
			try { host.PublishBlobsTo(peer, EngineCodec.LoadBlobs(offloading, list, BlobLoadTimeout, out _, out _)); }
			catch (Exception ex) { _log.WarnOnce("stream-blobs", $"streaming: loading offloaded blobs for {peer} failed: {ex.Message}"); }
		});
	}

	/// <summary>Join capture: the roots a rejoining player's saved character is too far from, so they are not sent at all.</summary>
	private HashSet<Entity>? CaptureExclusions(InProcessReplicationSessionComponent comp, PeerId peer)
	{
		var host = _host;
		double radius = host?.StreamingRadiusMeters ?? 0;
		if (host == null || radius <= 0) return null;
		var record = host.Players().FirstOrDefault(p => p.Peer.HasValue && p.Peer.Value.Equals(peer));
		long identity = record == null ? 0 : IdentityOf(record.ClientId);
		if (identity == 0) return null;
		var session = comp.Session;
		var alive = new List<Entity>();
		if (!session.TryFillAliveCharactersHotfix(alive, preserveOrder: true)) return null;
		var others = new HashSet<Entity>(_joiners.Values.Select(x => x.Character).Where(x => x != null), ReferenceEqualityComparer.Instance);
		// A new player picks a spawn point after the world arrives; only a returning player's place is known now.
		if (AnchorOf(SavedCharacter(session, new IdentityId(identity), alive, others)) is not { } anchor) return null;
		var excluded = new HashSet<Entity>(ReferenceEqualityComparer.Instance);
		var stream = new StreamState();
		foreach (var group in CollectStreamGroups(session, comp.Entity))
		{
			if (group.Always || StreamingRules.Keep(false, group.DistanceTo(anchor), radius)) continue;
			foreach (var (root, id) in group.Roots)
			{
				excluded.Add(root);
				stream.Out.Add(id);
			}
		}
		stream.Dirty = true;
		_streams[peer] = stream;
		_streamingActive = true;
		PushStreamedOut(host, peer, stream);
		return excluded;
	}

	/// <summary>Spawn bundles: whether this joiner gets one. A new root beyond its radius starts out streamed out.</summary>
	// A freshly spawned server entity can still lack its WorldTransform data; its builder already has the transform.
	private static Vector3D? SpawnPosition(Entity root, EntityObjectBuilder? builder)
	{
		if (root.Data.TryGet<WorldTransform>(out var transform)) return transform.Position;
		if (builder?.ObjectBuilders == null) return null;
		foreach (var ob in builder.ObjectBuilders.Values)
		{
			if (ob is WorldTransformComponentObjectBuilder w) return w.Transform.Position;
		}
		return null;
	}

	private Func<PeerId, bool>? StreamSpawnFilter(Dictionary<Entity, EntityObjectBuilder> builders)
	{
		var entities = builders.Keys;
		var host = _host;
		if (host == null || !_streamingActive) return null;
		double radius = host.StreamingRadiusMeters;
		var roots = new List<(uint Id, Entity Root, bool New, Vector3D? At)>();
		var seen = new HashSet<uint>();
		foreach (var entity in entities)
		{
			bool top = !entity.Data.Has<ParentData>();
			uint root = top ? (_ids.TryGet(entity, out uint own) ? own : 0u) : RootIdOf(entity);
			if (root != 0 && seen.Add(root)) roots.Add((root, entity, top, top ? SpawnPosition(entity, builders[entity]) : null));
		}
		if (roots.Count == 0) return null;
		var anchors = _joiners.Values.ToDictionary(j => j.Peer, j => AnchorOf(j.Character));
		return peer =>
		{
			if (!_streams.TryGetValue(peer, out var stream)) return true;
			lock (_streamSync)
			{
				bool near = false, far = false;
				var always = new List<uint>();
				foreach (var (id, root, isNew, position) in roots)
				{
					if (stream.Out.Contains(id)) continue;
					if (AlwaysStreamed(root))
					{
						always.Add(id);
						continue;
					}
					if (isNew && radius > 0 && position is { } where && anchors.TryGetValue(peer, out var anchor) && anchor is { } at
						&& !StreamingRules.Keep(false, Vector3D.Distance(where, at), radius))
					{
						stream.Out.Add(id);
						stream.Dirty = true;
						far = true;
						continue;
					}
					near = true;
				}
				bool any = near || (always.Count > 0 && !far);
				if (any)
				{
					// The bundle builds every root in it on the joiner, so none of them is streamed out any more.
					foreach (var (id, _, _, _) in roots) stream.Dirty |= stream.Out.Remove(id);
				}
				else if (always.Count > 0)
				{
					// Only far grids plus something every joiner needs (an asteroid spawned beside another player): hold the
					// bundle back and send the needed roots on their own from the next streaming update.
					foreach (var id in always) stream.Dirty |= stream.Out.Add(id);
				}
				if (stream.Dirty)
				{
					stream.Dirty = false;
					host.SetStreamedOut(peer, new HashSet<uint>(stream.Out));
				}
				if (!any && builders.Count >= 200)
				{
					anchors.TryGetValue(peer, out var a);
					_log.Info($"streaming: spawn bundle {builders.Count} entities, {roots.Count} root(s) ({roots.Count(r => r.New)} new, {roots.Count(r => r.At == null)} without a position, {roots.Count(r => AlwaysStreamed(r.Root))} always) for {peer}: {(any ? "sent" : "held back")}; anchor {(a is { } p ? $"{p.X:0},{p.Y:0},{p.Z:0}" : "none")}, nearest {roots.Where(r => r.At != null && a != null).Select(r => Vector3D.Distance(r.At!.Value, a!.Value)).DefaultIfEmpty(-1).Min():0} m");
				}
				return any;
			}
		};
	}

	/// <summary>Root NetIds for each signal command's entity references (0 for none), when streaming is active.</summary>
	private List<uint[]>? SignalRoots(List<WireCommand> signals)
	{
		if (!_streamingActive) return null;
		var roots = new List<uint[]>(signals.Count);
		foreach (var cmd in signals)
		{
			uint[] ids = Array.Empty<uint>();
			foreach (var r in cmd.Refs)
			{
				if (r.Kind is WireKind.Entity or WireKind.Component && _ids.TryGet(r.Id, out Entity entity) && RootIdOf(entity) is uint root && root != 0)
				{
					ids = ids.Length == 0 ? new[] { root } : ids.Append(root).ToArray();
				}
			}
			roots.Add(ids);
		}
		return roots;
	}

	/// <summary>A destroyed root can never come back with this NetId, so no joiner needs to remember it as streamed out.</summary>
	private void ForgetStreamedRoot(uint id)
	{
		if (!_streamingActive) return;
		lock (_streamSync)
		{
			foreach (var stream in _streams.Values)
			{
				stream.Dirty |= stream.Out.Remove(id);
			}
		}
	}

	private void ForgetStream(PeerId peer) => _streams.TryRemove(peer, out _);
}
