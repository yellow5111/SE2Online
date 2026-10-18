using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.Multiplayer;
using Keen.VRage.Multiplayer.NetworkStories;
using Keen.VRage.Multiplayer.Replications.InProcess;
using Y51_SE2Online.Core;
using Y51_SE2Online.Net;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

internal sealed class HostStories
{
	internal sealed class Entry
	{
		public PeerId Peer;

		public uint Id;

		public string Name;

		public NetworkStoryContext Context;

		public long LastMs;
	}

	private sealed class HostStoryNarrator : INetworkStoryNarrator
	{
		private readonly HostStories _owner;

		private readonly Entry _entry;

		private readonly IStorySink _host;

		private readonly ObjectSpace _space;

		private readonly Func<ReplicationEventContext> _asker;

		public HostStoryNarrator(HostStories owner, Entry entry, IStorySink host, ObjectSpace space, Func<ReplicationEventContext> asker)
		{
			_owner = owner;
			_entry = entry;
			_host = host;
			_space = space;
			_asker = asker;
		}

		public void Start<TResult>(NetworkStoryContext context, IAsyncStateMachine stateMachine) => stateMachine.MoveNext();

		// Every MoveTo.Server of a joiner's story answers as that joiner, never as the host itself.
		public void MoveToServer(NetworkStoryContext context, IAsyncStateMachine stateMachine, ServerRequest server)
		{
			server.Result = _asker();
			stateMachine.MoveNext();
		}

		// State never travels back: the client's tail (in 2.4 only "return x") runs here and only the result goes to the joiner.
		public void MoveToClient(NetworkStoryContext context, IAsyncStateMachine stateMachine, ClientRequest client)
		{
			_owner.TailsRunHere++;
			if (_owner.FirstOf("tail " + _entry.Name))
			{
				_owner._log.Info($"host world sync: STORY {_entry.Name} #{_entry.Id} moves back to the client; its tail runs here and only the result travels");
			}
			stateMachine.MoveNext();
		}

		public void Finish<TResult>(NetworkStoryContext context, TResult result, Exception? exception)
		{
			_owner.Done(_entry);
			_owner.Finished++;
			string outcome = exception == null ? "ok" + StoryState.Describe(result) : exception.GetType().Name;
			if (_owner.FirstOf("finished " + _entry.Name + (exception == null ? "" : " " + outcome)) | (_owner.Finished <= 20))
			{
				_owner._log.Info($"host world sync: STORY {_entry.Name} #{_entry.Id} finished on the server ({outcome}), result to {_entry.Peer}");
			}
			var finish = new StoryFinish
			{
				StoryId = _entry.Id
			};
			if (exception != null)
			{
				finish.Status = (byte)(exception is not Keen.VRage.Multiplayer.NetworkStories.SkipNetworkStoryException ? 1 : 2);
				finish.Text = exception.GetType().Name + ": " + exception.Message;
			}
			else if (result != null && result is not DBNull)
			{
				string? fail = null;
				_space.ClearLocalIds();
				_space.Observed.Clear();
				object? sent = HostWorld.OreListFor(_entry.Peer, result);
				finish.Result = ObjectCodec.Encode(sent, typeof(TResult), _space, ref fail);
				if (fail == null && _space.LocalIds.Count > 0)
				{
					_owner.Defer(_entry, sent, typeof(TResult));
					return;
				}
				if (fail != null)
				{
					finish.Status = 2;
					finish.Text = "result cannot travel: " + fail;
				}
				else
				{
					_owner.Roots(_entry.Peer, _space);
				}
			}
			_host.SendStoryFinish(_entry.Peer, finish);
		}
	}

	private readonly IMpLog _log;

	private readonly ConcurrentQueue<(PeerId Peer, ushort ClientId, StoryMove Move)> _in = new ConcurrentQueue<(PeerId, ushort, StoryMove)>();

	private readonly ConcurrentQueue<PeerId> _forget = new ConcurrentQueue<PeerId>();

	private readonly ConcurrentQueue<(Entry Entry, object? Result, Type Type, long SinceMs)> _deferred = new ConcurrentQueue<(Entry, object, Type, long)>();

	public const int ResultWaitMs = 3000;

	private readonly ConcurrentDictionary<(PeerId, uint), Entry> _entries = new ConcurrentDictionary<(PeerId, uint), Entry>();

	private readonly ConcurrentDictionary<string, long> _counts = new ConcurrentDictionary<string, long>();

	public long Received;

	public long Started;

	public long TailsRunHere;

	public long Finished;

	public long Failed;

	public long ReleasedNoOps;

	private readonly ConcurrentDictionary<string, byte> _logged = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);

	public Func<ushort, ushort>? SourceFor;

	public Action<PeerId, IReadOnlyList<uint>>? RootsFor;

	public Func<ushort, object, Session?, string?>? Gate;

	public long Refused;

	public const int IdleForgetMs = 300000;

	private long _lastPurgeMs;

	public bool Pending => !_in.IsEmpty || !_forget.IsEmpty || !_deferred.IsEmpty;

	internal bool FirstOf(string key) => _logged.TryAdd(key, 0);

	public HostStories(IMpLog log)
	{
		_log = log;
	}

	internal void Roots(PeerId peer, ObjectSpace space)
	{
		if (space.Observed.Count > 0)
		{
			RootsFor?.Invoke(peer, space.Observed.ToList());
		}
	}

	public void Count(string key)
	{
		_counts.AddOrUpdate(key, 1L, (string _, long c) => c + 1);
	}

	public void Enqueue(PeerId peer, ushort clientId, StoryMove move)
	{
		Received++;
		_in.Enqueue((peer, clientId, move));
	}

	public void Forget(PeerId peer) => _forget.Enqueue(peer);

	internal void Defer(Entry e, object? result, Type type)
	{
		_deferred.Enqueue((e, result, type, MpClock.NowMs()));
	}

	internal void RetryDeferred(IStorySink host, ObjectSpace space)
	{
		int count = _deferred.Count;
		long now = MpClock.NowMs();
		(Entry Entry, object? Result, Type Type, long SinceMs) deferred;
		for (int i = 0; i < count && _deferred.TryDequeue(out deferred); i++)
		{
			string? fail = null;
			space.ClearLocalIds();
			space.Observed.Clear();
			WireValue encoded = ObjectCodec.Encode(deferred.Result, deferred.Type, space, ref fail);
			if (fail == null && space.LocalIds.Count > 0)
			{
				if (now - deferred.SinceMs < 3000)
				{
					_deferred.Enqueue(deferred);
					continue;
				}
				fail = "refers to entities the joiner does not have";
			}
			if (fail == null)
			{
				Roots(deferred.Entry.Peer, space);
			}
			host.SendStoryFinish(deferred.Entry.Peer, fail == null
				? new StoryFinish
				{
					StoryId = deferred.Entry.Id,
					Result = encoded
				}
				: new StoryFinish
				{
					StoryId = deferred.Entry.Id,
					Status = 2,
					Text = "result cannot travel: " + fail
				});
		}
	}

	public void Drain(Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent hostClient, Func<IStorySink?> host, Func<ObjectSpace> serverSpace, Session serverSession, bool serverOnly = false)
	{
		Entry? removed;
		while (_forget.TryDequeue(out var gone))
		{
			foreach (var key in _entries.Keys.Where(k => k.Item1 == gone).ToList())
			{
				_entries.TryRemove(key, out removed);
			}
		}
		long now = MpClock.NowMs();
		if (now - _lastPurgeMs > 1000)
		{
			_lastPurgeMs = now;
			foreach (var key in (from kv in _entries
				where now - kv.Value.LastMs > 300000
				select kv.Key).ToList())
			{
				_entries.TryRemove(key, out removed);
			}
		}
		(PeerId, ushort, StoryMove) queued;
		while (_in.TryDequeue(out queued))
		{
			(PeerId Peer, ushort ClientId, StoryMove Move) it = queued;
			Core.LatencyTrace.Next(Core.LatencyTrace.HostApplied, 41, it.Peer.Value);
			ushort source = SourceFor?.Invoke(it.ClientId) ?? 1;
			if (serverOnly)
			{
				Run(it.Peer, it.Move, host(), serverSpace(), serverSession, source, it.ClientId);
				continue;
			}
			hostClient.PostInvoke(_ => Run(it.Peer, it.Move, host(), serverSpace(), serverSession, source, it.ClientId));
		}
		if (!_deferred.IsEmpty)
		{
			IStorySink? sink = host();
			if (sink != null)
			{
				RetryDeferred(sink, serverSpace());
			}
		}
	}

	internal void Run(PeerId peer, StoryMove move, IStorySink? host, ObjectSpace space, Session? serverSession, ushort sourceClientId = 1, ushort clientId = 0)
	{
		if (host == null)
		{
			return;
		}
		(PeerId, uint) key = (peer, move.StoryId);
		StoryType? story = StoryState.Find(move.StateMachineType, out string why);
		string name = story?.Name ?? StoryState.Clip(move.StateMachineType);
		if (story == null || !story.ClientMayStart)
		{
			Refuse(host, peer, move.StoryId, story, name, story == null ? why : "clients do not start this story");
			return;
		}
		if (_entries.ContainsKey(key))
		{
			Refuse(host, peer, move.StoryId, story, name, "story already running; only its arguments may come from the client");
			return;
		}
		Entry? removed;
		try
		{
			var context = (NetworkStoryContext)Activator.CreateInstance(story.Builder.FieldType)!;
			var entry = new Entry
			{
				Peer = peer,
				Id = move.StoryId,
				Name = name,
				Context = context,
				LastMs = MpClock.NowMs()
			};
			context.Narrator = new HostStoryNarrator(this, entry, host, space, () => new ReplicationEventContext
			{
				IsLocal = false,
				Session = serverSession,
				SourceId = new ClientId(sourceClientId)
			});
			space.ClearLocalIds();
			space.LocalStubs.Clear();
			object? sm = StoryState.Start(story, move, space, context, StoryState.EntryState, out string? refused, out string? fail);
			if (refused != null)
			{
				Refuse(host, peer, move.StoryId, story, name, refused);
				return;
			}
			if (sm == null)
			{
				if (fail != null && ObjectCodec.IsUnknownModel(fail))
				{
					AlreadyReleased(host, peer, move.StoryId, name, context, fail);
				}
				else
				{
					Fail(host, peer, move.StoryId, name, fail ?? "?");
				}
				return;
			}
			string? refusal = Gate?.Invoke(clientId, sm, serverSession);
			if (refusal != null)
			{
				Refused++;
				Count(name + (refusal.StartsWith("passenger", StringComparison.Ordinal) ? ": refused (passenger seat)" : ": refused (not allowed)"));
				_log.Info($"story: refused {name} #{move.StoryId} from {peer} (not allowed: {refusal})");
				host.SendStoryFinish(peer, new StoryFinish
				{
					StoryId = move.StoryId,
					Status = 2,
					Text = "not allowed: " + refusal
				});
				return;
			}
			_entries[key] = entry;
			Started++;
			Count(name);
			if (AudioRouting.IsBuildStory(name))
			{
				AudioRouting.Instance.NoteJoinerBuild(clientId != 0 ? clientId : sourceClientId, MpClock.NowMs());
			}
			if (FirstOf("run " + name) | (Started <= 20))
			{
				_log.Info($"host world sync: STORY {name} #{move.StoryId} from {peer} runs on the server as ClientId {sourceClientId}");
			}
			ushort outer = HostWorld.StoryActor;
			HostWorld.StoryActor = clientId != 0 ? clientId : sourceClientId;
			try
			{
				((IAsyncStateMachine)sm).MoveNext();
			}
			finally
			{
				HostWorld.StoryActor = outer;
			}
		}
		catch (Exception ex)
		{
			_entries.TryRemove(key, out removed);
			Fail(host, peer, move.StoryId, name, ex.GetType().Name + ": " + ex.Message);
		}
	}

	// Keyed by the listed story (or "unlisted") so a peer cannot grow the log-once set with made-up names.
	private void Refuse(IStorySink host, PeerId peer, uint id, StoryType? story, string name, string why)
	{
		Refused++;
		string kind = story?.Name ?? "unlisted";
		Count(kind + ": refused");
		_log.WarnOnce("story-refused-" + kind, $"story: refused {name} #{id} from {peer} ({why}) (further ones of this story only counted)");
		host.SendStoryFinish(peer, new StoryFinish
		{
			StoryId = id,
			Status = 2,
			Text = "refused: " + why
		});
	}

	private void Fail(IStorySink host, PeerId peer, uint id, string name, string why)
	{
		Failed++;
		Count(name + ": failed");
		_log.WarnOnce("host-story-" + name + StoryState.Shape(why), $"host world sync: story {name} from {peer} cannot run here: {why} (further ones of this kind only counted)");
		host.SendStoryFinish(peer, new StoryFinish
		{
			StoryId = id,
			Status = 2,
			Text = why
		});
	}

	private void AlreadyReleased(IStorySink host, PeerId peer, uint id, string name, NetworkStoryContext context, string why)
	{
		ReleasedNoOps++;
		bool isVoid = StoryState.ResultType(context) == typeof(DBNull);
		Count(name + ": model already released (no-op)");
		if (FirstOf("released " + name))
		{
			_log.Info($"host world sync: STORY {name} #{id} from {peer} names a model this host already released ({why}); {(isVoid ? "nothing to do, answered as done" : "skipped, as Keen does")} (further ones only counted)");
		}
		host.SendStoryFinish(peer, isVoid
			? new StoryFinish
			{
				StoryId = id
			}
			: new StoryFinish
			{
				StoryId = id,
				Status = 2,
				Text = "model already released: " + why
			});
	}

	internal void Done(Entry e)
	{
		_entries.TryRemove((e.Peer, e.Id), out Entry _);
	}

	public string Describe()
	{
		return $"stories: received {Received}, started {Started}, refused {Refused}, client tails run here {TailsRunHere}, finished {Finished}, failed {Failed}, released-model no-ops {ReleasedNoOps}" + (_counts.IsEmpty ? "" : "; " + string.Join(", ",
			from kv in _counts.OrderByDescending(kv => kv.Value).Take(8)
			select $"{kv.Key} x{kv.Value}"));
	}
}
