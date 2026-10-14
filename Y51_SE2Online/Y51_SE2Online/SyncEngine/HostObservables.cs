using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Serialization;
using Keen.VRage.Library.Reflection;
using Keen.VRage.Library.UI;
using Keen.VRage.Multiplayer.Replications;
using Keen.VRage.Multiplayer.Replications.FailureStrategies;
using Keen.VRage.Multiplayer.Replications.InProcess;
using Y51_SE2Online.Core;
using Y51_SE2Online.Dedicated;
using Y51_SE2Online.Net;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

internal sealed class HostObservables
{
	private sealed class Entry
	{
		public uint Oid;

		public object Replica;

		public bool IsList;

		public AccessibleTypeInfo? Info;

		public bool Subscribed;

		public PropertyChangedEventHandler? OnProperty;

		public NotifyCollectionChangedEventHandler? OnList;
	}

	private readonly IMpLog _log;

	private readonly object _sync = new();

	private readonly ConcurrentDictionary<object, Entry> _byReplica = new(ReferenceEqualityComparer.Instance);

	private readonly ConcurrentDictionary<uint, Entry> _byOid = new();

	private readonly ObservableRelevance<PeerId> _relevance;

	private readonly ConcurrentQueue<(PeerId Peer, uint Oid)> _roots = new();

	private readonly ConcurrentQueue<(PeerId Peer, ushort ClientId, IReadOnlyList<MemberEdit> Edits)> _edits = new();

	private readonly ConcurrentQueue<PeerId> _forget = new();

	private readonly ConcurrentDictionary<string, long> _counts = new();

	private readonly ObjectSpace _encode;

	// The peer whose edit is being applied, and which member: only that member is not echoed back to it. Changes the
	// edit causes elsewhere (other members, the same player's other views of a model) still reach it.
	private (PeerId Peer, uint Oid, string Name)? _origin;

	private int _nextOid;

	public long Created;

	public long Released;

	public long ChangesSent;

	public long EncodeFailures;

	public long EditsApplied;

	public long EditsRefused;

	public long Batches;

	public long BatchOps;

	public long BatchBytes;

	public int Count => _byOid.Count;

	public HostObservables(IMpLog log)
	{
		_log = log;
		_relevance = new ObservableRelevance<PeerId>(Snapshot);
		_encode = new ObjectSpace
		{
			Name = "host-observables",
			IdOf = _ => null,
			Resolve = (_, _) => null,
			ObservableId = o => GetOrAdd(o, isList: false).Oid,
			ListId = l => GetOrAdd(l, isList: true).Oid,
			OpaqueHandles = true
		};
	}

	private void Note(string what) => _counts.AddOrUpdate(what, 1L, (string _, long c) => c + 1);

	private Entry GetOrAdd(object replica, bool isList)
	{
		if (_byReplica.TryGetValue(replica, out var existing))
		{
			return existing;
		}
		var entry = new Entry
		{
			Oid = (uint)Interlocked.Increment(ref _nextOid),
			Replica = replica,
			IsList = isList,
			Info = isList ? null : (replica as IAccessible)?.GetTypeInfo()
		};
		if (HeadlessReplication.Active && replica is ObservableObject model)
		{
			var type = ObservableObjectReplicationServerSessionComponent.ObservedHierarchy.TryGetObservableObjectReplicatedType(model);
			if (type == null || !MetadataIndexerBase<AccessibleTypeIndexer>.Instance.TryGetAccessibleTypeInfo(type, out var info))
				throw new InvalidOperationException("No native replicated model schema for " + replica.GetType().FullName);
			entry.Info = info;
		}
		existing = _byReplica.GetOrAdd(replica, entry);
		if (existing == entry)
		{
			_byOid[existing.Oid] = existing;
			Interlocked.Increment(ref Created);
		}
		return existing;
	}

	public uint? OidOnServer(object model, ReplayContext server)
	{
		if (HeadlessReplication.Active && model is ObservableObject) return GetOrAdd(HeadlessServerModel(model, server), isList: false).Oid;
		if (HostWorld.IsOwnOreList(model)) return GetOrAdd(model, isList: false).Oid;
		if (_byReplica.TryGetValue(model, out var known))
		{
			return known.Oid;
		}
		object? remote = null;
		if (server.HasPairingRemote(model))
		{
			remote = model;
		}
		else if (server.HasPairingLocal(model))
		{
			remote = server.TranslateLocalToRemote(model);
		}
		if (remote is not ObservableObject)
		{
			return null;
		}
		return GetOrAdd(remote, isList: false).Oid;
	}

	// Native stories hand out the replication's copy of a server model. A headless server has no client session, so
	// that copy is never updated and its edits never reach the server (warheads would not arm, panels froze); joiners
	// get the server model itself instead.
	private static object HeadlessServerModel(object model, ReplayContext server)
	{
		if (!server.HasPairingRemote(model)) return model;
		bool skip = false;
		return server.TranslateObject(model, ReplicationFailureStrategy.NullOnAnyFail, ref skip) is ObservableObject local ? local : model;
	}

	public object? ResolveOnServer(uint oid, bool inCollection, ReplayContext server)
	{
		if (!_byOid.TryGetValue(oid, out var entry))
		{
			return null;
		}
		if (entry.IsList)
		{
			if (!inCollection)
			{
				return null;
			}
			return entry.Replica;
		}
		if (inCollection)
		{
			return entry.Replica;
		}
		if (HeadlessReplication.Active) return entry.Replica;
		bool skip = false;
		return server.TranslateObject(entry.Replica, ReplicationFailureStrategy.NullOnAnyFail, ref skip);
	}

	public void AddRoots(PeerId peer, IReadOnlyList<uint> oids)
	{
		foreach (uint oid in oids)
		{
			_roots.Enqueue((peer, oid));
		}
	}

	public void QueueEdits(PeerId peer, ushort clientId, IReadOnlyList<MemberEdit> edits) => _edits.Enqueue((peer, clientId, edits));

	public void Forget(PeerId peer) => _forget.Enqueue(peer);

	public void OnRelease(object? serverKey, bool list, ReplayContext? hostClient)
	{
		if (serverKey == null || hostClient == null || _byReplica.IsEmpty)
		{
			return;
		}
		try
		{
			bool skip = false;
			object? replica = HeadlessReplication.Active
				? (list ? serverKey.GetType().GetProperty("Source")?.GetValue(serverKey) : serverKey)
				: hostClient.TranslateObject(serverKey, ReplicationFailureStrategy.NullOnAnyFail, ref skip);
			if (list && replica is ObservableObjectReplicationSessionComponent.ClientObservedCollection observed)
			{
				replica = observed.Source;
			}
			else if (HeadlessReplication.Active && replica != null)
			{
				replica = HeadlessServerModel(replica, hostClient);
			}
			if (replica == null || !_byReplica.TryRemove(replica, out var entry))
			{
				return;
			}
			lock (_sync)
			{
				Unsubscribe(entry);
				_byOid.TryRemove(entry.Oid, out _);
				_relevance.OnRelease(entry.Oid);
				Released++;
			}
		}
		catch (Exception ex)
		{
			_log.WarnOnce("obs-release", $"host observables: handling a release failed ({ex.GetType().Name}: {ex.Message}); the model stays known");
		}
	}

	private void Subscribe(Entry e)
	{
		if (e.Subscribed)
		{
			return;
		}
		e.Subscribed = true;
		if (e.IsList)
		{
			if (e.Replica is INotifyCollectionChanged collection)
			{
				e.OnList = (object? _, NotifyCollectionChangedEventArgs a) => OnListChanged(e, a);
				collection.CollectionChanged += e.OnList;
			}
		}
		else if (e.Replica is INotifyPropertyChanged model)
		{
			e.OnProperty = (object? _, PropertyChangedEventArgs a) => OnPropertyChanged(e, a);
			model.PropertyChanged += e.OnProperty;
		}
	}

	private static void Unsubscribe(Entry e)
	{
		if (e.OnList != null && e.Replica is INotifyCollectionChanged collection)
		{
			collection.CollectionChanged -= e.OnList;
		}
		if (e.OnProperty != null && e.Replica is INotifyPropertyChanged model)
		{
			model.PropertyChanged -= e.OnProperty;
		}
		e.OnList = null;
		e.OnProperty = null;
	}

	private void OnPropertyChanged(Entry e, PropertyChangedEventArgs a)
	{
		string? propertyName = a.PropertyName;
		if (string.IsNullOrEmpty(propertyName) || e.Info == null)
		{
			return;
		}
		try
		{
			lock (_sync)
			{
				if (_relevance.AnyHas(e.Oid) && ObservableRules.TryReplicating(e.Info, propertyName, out var member))
				{
					var value = EncodeValue(member.Get(e.Replica), member.Type, e.Info.Type.Name + "." + propertyName);
					bool echo = _origin is { } origin && origin.Oid == e.Oid && origin.Name == propertyName;
					_relevance.OnChange(ObservableOp.Member(e.Oid, propertyName, value), _origin.GetValueOrDefault().Peer, echo);
					ChangesSent++;
				}
			}
		}
		catch (Exception ex)
		{
			Note("member change failed: " + ex.GetType().Name);
			_log.WarnOnce("obs-prop-" + e.Info.Type.Name, $"host observables: forwarding {e.Info.Type.Name}.{propertyName} failed ({ex.GetType().Name}: {ex.Message})");
		}
	}

	private void OnListChanged(Entry e, NotifyCollectionChangedEventArgs a)
	{
		IList list;
		Type et;
		try
		{
			lock (_sync)
			{
				if (!_relevance.AnyHas(e.Oid))
				{
					return;
				}
				list = (IList)e.Replica;
				et = ObservableRules.ElementType(list.GetType());
				var ops = new List<ObservableOp>(2);
				switch (a.Action)
				{
				case NotifyCollectionChangedAction.Add:
					ops.Add(a.NewStartingIndex < 0 ? Reset() : ObservableOp.ListChange(e.Oid, 0, a.NewStartingIndex, 0, Items(a.NewItems)));
					break;
				case NotifyCollectionChangedAction.Remove:
					ops.Add(a.OldStartingIndex < 0 ? Reset() : ObservableOp.ListChange(e.Oid, 1, a.OldStartingIndex, a.OldItems?.Count ?? 1, null));
					break;
				case NotifyCollectionChangedAction.Replace:
					ops.Add(a.NewStartingIndex < 0 ? Reset() : ObservableOp.ListChange(e.Oid, 2, a.NewStartingIndex, 0, Items(a.NewItems)));
					break;
				case NotifyCollectionChangedAction.Move:
					if (a.OldStartingIndex < 0 || a.NewStartingIndex < 0)
					{
						ops.Add(Reset());
						break;
					}
					ops.Add(ObservableOp.ListChange(e.Oid, 1, a.OldStartingIndex, a.OldItems?.Count ?? 1, null));
					ops.Add(ObservableOp.ListChange(e.Oid, 0, a.NewStartingIndex, 0, Items(a.NewItems)));
					break;
				default:
					ops.Add(Reset());
					break;
				}
				foreach (var op in ops)
				{
					_relevance.OnChange(op);
				}
				ChangesSent += ops.Count;
			}
		}
		catch (Exception ex)
		{
			Note("list change failed: " + ex.GetType().Name);
			_log.WarnOnce("obs-list-" + e.Replica.GetType().Name, $"host observables: forwarding a change of a {e.Replica.GetType().Name} failed ({ex.GetType().Name}: {ex.Message})");
		}

		List<WireValue> Items(IList? items)
		{
			if (items != null)
			{
				return items.Cast<object>().Select(i => EncodeValue(i, et, "list item")).ToList();
			}
			return new List<WireValue>();
		}

		// A change without an index cannot be replayed step by step, so resend the whole list.
		ObservableOp Reset() => ObservableOp.ListChange(e.Oid, 3, 0, 0, Items(list));
	}

	private WireValue EncodeValue(object? value, Type declared, string what)
	{
		if (value is IList list && list is not INotifyCollectionChanged && ObservableRules.ElementType(value.GetType()).Name == "AttachObservablePropertyBindingOp")
		{
			return WireValue.NullValue;
		}
		if (value is Delegate || typeof(Delegate).IsAssignableFrom(declared))
		{
			return WireValue.NullValue;
		}
		string? fail = null;
		_encode.ClearLocalIds();
		_encode.Observed.Clear();
		var result = ObjectCodec.Encode(value, declared, _encode, ref fail);
		if (fail == null && _encode.LocalIds.Count > 0)
		{
			fail = "refers to an entity (Keen never replicates those)";
		}
		if (fail == null)
		{
			return result;
		}
		EncodeFailures++;
		Note(what + ": " + fail);
		_log.WarnOnce("obs-encode-" + what, $"host observables: {what} cannot travel ({fail}); joiners get null there");
		return WireValue.NullValue;
	}

	private (ObservableOp Op, IReadOnlyList<uint> Children)? Snapshot(uint oid)
	{
		if (!_byOid.TryGetValue(oid, out var entry))
		{
			return null;
		}
		try
		{
			Subscribe(entry);
			ObservableOp op;
			if (entry.IsList)
			{
				var list = (IList)entry.Replica;
				var elementType = ObservableRules.ElementType(list.GetType());
				var items = new List<WireValue>(list.Count);
				foreach (object item in list)
				{
					items.Add(EncodeValue(item, elementType, "list item"));
				}
				op = ObservableOp.NewList(oid, ObjectCodec.TypeName(list.GetType()), items);
			}
			else
			{
				var info = entry.Info ?? throw new InvalidOperationException(entry.Replica.GetType().Name + " is not IAccessible");
				var members = new List<(string, WireValue)>();
				foreach (var member in info.OrderedMembers)
				{
					if (!ObservableRules.IsNoReplicate(member))
					{
						object value;
						try
						{
							value = member.Get(entry.Replica);
						}
						catch (Exception) when (!member.CanWrite)
						{
							continue;
						}
						if (ObservableRules.IsObservedList(value))
						{
							members.Add((member.Accessor.Name, WireValue.List(GetOrAdd(value, isList: true).Oid)));
						}
						else if (member.CanWrite)
						{
							members.Add((member.Accessor.Name, EncodeValue(value, member.Type, info.Type.Name + "." + member.Accessor.Name)));
						}
					}
				}
				op = ObservableOp.NewObject(oid, ObjectCodec.TypeName(info.Type), members);
			}
			return (op, op.Refs());
		}
		catch (Exception ex)
		{
			Note("snapshot failed: " + ex.GetType().Name);
			_log.WarnOnce("obs-snap-" + entry.Replica.GetType().Name, $"host observables: cannot snapshot a {entry.Replica.GetType().Name} ({ex.GetType().Name}: {ex.Message}); joiners do not get it");
			return null;
		}
	}

	public void Flush(Func<PeerId, byte[], bool> publish, object? exchangeLock)
	{
		if (_roots.IsEmpty && _edits.IsEmpty && _forget.IsEmpty && _relevance.PeerCount == 0)
		{
			return;
		}
		List<(PeerId, List<ObservableOp>)> output;
		lock (_sync)
		{
			PeerId gone;
			while (_forget.TryDequeue(out gone))
			{
				_relevance.Forget(gone);
			}
			long nowMs = MpClock.NowMs();
			(PeerId Peer, uint Oid) root;
			while (_roots.TryDequeue(out root))
			{
				_relevance.AddRoot(root.Peer, root.Oid, nowMs);
			}
			_relevance.ProcessPendingRoots(nowMs);
			if (!_edits.IsEmpty)
			{
				if (exchangeLock != null)
				{
					lock (exchangeLock)
					{
						ApplyEdits();
					}
				}
				else
				{
					ApplyEdits();
				}
			}
			output = _relevance.TakeOutput();
		}
		foreach (var (peer, ops) in output)
		{
			byte[] batch = ObservableBatchMessage.Encode(0u, ops);
			if (!publish(peer, batch))
			{
				_forget.Enqueue(peer);
				continue;
			}
			Batches++;
			BatchOps += ops.Count;
			BatchBytes += batch.Length;
			if (Batches <= 3 || ops.Count >= 200)
			{
				_log.Info($"host observables: batch for {peer}: {ops.Count} step(s), {MpLogExtensions.Bytes(batch.Length)} ({Summary(ops)})");
			}
		}
	}

	private static string Summary(List<ObservableOp> ops)
	{
		string counts = string.Join(", ", ops.GroupBy(o => o.Kind).Select(g => $"{g.Count()} {g.Key}"));
		var firstNew = ops.FirstOrDefault(o => o.Kind == ObservableOpKind.NewObject);
		return counts + (firstNew != null ? "; first new: " + ObservableOp.ShortType(firstNew.Text) : "");
	}

	private void ApplyEdits()
	{
		var sp = new ObjectSpace
		{
			Name = "host-edit",
			IdOf = _ => null,
			Resolve = (_, _) => null
		};
		(PeerId Peer, ushort ClientId, IReadOnlyList<MemberEdit> Edits) batch;
		while (_edits.TryDequeue(out batch))
		{
			Core.LatencyTrace.Next(Core.LatencyTrace.HostApplied, 59);
			foreach (var edit in batch.Edits)
			{
				string? refused = null;
				AccessibleTypeInfo.MemberInfo member;
				if (!_byOid.TryGetValue(edit.Oid, out var entry) || entry.IsList || entry.Info == null)
				{
					refused = "unknown model";
				}
				else if (!_relevance.Has(batch.Peer, edit.Oid))
				{
					refused = "not one of this player's models";
				}
				else if (HostWorld.Current?.IdentityOf(batch.ClientId) is long actor && actor != 0 && SocialGuard.CheckObject(entry.Replica, actor) is string denied)
				{
					refused = denied;
				}
				else if (!ObservableRules.TryReplicating(entry.Info, edit.Name, out member) || !ObservableRules.IsSyncFromClients(member))
				{
					refused = entry.Info.Type.Name + "." + edit.Name + " is not a [SyncFromClients] member";
				}
				else
				{
					var kind = edit.Value.Kind;
					if (kind is WireKind.Entity or WireKind.Component or WireKind.Observable or WireKind.ObservedList)
					{
						refused = "clients only send constants (Keen's ClientObservedObject.EnsureMemberValueCanReplicate)";
					}
					else
					{
						string? fail = null;
						object value = ObjectCodec.Decode(edit.Value, member.Type, sp, translated: false, ref fail);
						if (fail != null)
						{
							refused = fail;
						}
						else
						{
							_origin = (batch.Peer, edit.Oid, edit.Name);
							try
							{
								member.Set(entry.Replica, value);
								EditsApplied++;
								if (EditsApplied <= 20)
								{
									_log.Info($"host observables: ClientId {batch.ClientId} set {entry.Info.Type.Name}.{edit.Name} = {value ?? "null"} (observable #{edit.Oid}); the server applies it like the host's own edit");
								}
							}
							catch (Exception ex)
							{
								refused = ex.GetType().Name + ": " + ex.Message;
							}
							finally
							{
								_origin = null;
							}
						}
					}
				}
				if (refused != null)
				{
					EditsRefused++;
					Note("edit refused: " + refused);
					_log.WarnOnce("obs-edit-" + refused, $"host observables: ClientId {batch.ClientId}: edit of {edit.Name} on observable #{edit.Oid} refused ({refused})");
				}
			}
		}
	}

	public string Describe()
	{
		string relevance;
		lock (_sync)
		{
			relevance = _relevance.Describe();
		}
		return $"observables: {_byOid.Count} known (created {Created}, released {Released}); {relevance}; changes {ChangesSent}, batches {Batches} ({BatchOps} steps, {MpLogExtensions.Bytes(BatchBytes)}), edits applied {EditsApplied} refused {EditsRefused}, encode failures {EncodeFailures}"
			+ (_counts.IsEmpty ? "" : "; " + string.Join(", ", from kv in _counts.OrderByDescending(kv => kv.Value).Take(6) select $"{kv.Key} x{kv.Value}"));
	}
}
