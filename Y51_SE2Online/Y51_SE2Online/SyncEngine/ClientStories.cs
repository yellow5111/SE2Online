using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Keen.Game2.Client.GameSystems.PlayerControl;
using Keen.Game2.Simulation.GameSystems.Ownership;
using Keen.Game2.Simulation.GameSystems.Player;
using Keen.Game2.Simulation.GameSystems.Toolbars;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.Multiplayer.NetworkStories;
using Keen.VRage.Multiplayer.Replications.InProcess;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

internal sealed class ClientStories
{
	public const uint ServerOrigin = 0x80000000u;

	private sealed class ServerStoryNarrator : INetworkStoryNarrator
	{
		public static readonly ServerStoryNarrator Instance = new ServerStoryNarrator();

		public void Start<TResult>(NetworkStoryContext context, IAsyncStateMachine stateMachine) => stateMachine.MoveNext();

		public void MoveToClient(NetworkStoryContext context, IAsyncStateMachine stateMachine, ClientRequest client) => stateMachine.MoveNext();

		// ponytail: a host-started story that moves back to the server ends here; the forwarded notification stories never do.
		public void MoveToServer(NetworkStoryContext context, IAsyncStateMachine stateMachine, ServerRequest server)
		{
		}

		public void Finish<TResult>(NetworkStoryContext context, TResult result, Exception? exception)
		{
		}
	}

	private sealed class Entry
	{
		public uint Id;

		public NetworkStoryContext Context;

		public Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent? Comp;

		public string Name;

		public long LastMs;

		public bool AtHost;
	}

	public enum Keep : byte
	{
		Succeed,
		Hold,
		LocalOnly
	}

	private readonly IMpLog _log;

	private readonly Dictionary<uint, Entry> _byId = new Dictionary<uint, Entry>();

	private readonly Dictionary<object, uint> _byContext = new Dictionary<object, uint>(ReferenceEqualityComparer.Instance);

	private readonly Dictionary<string, long> _counts = new Dictionary<string, long>();

	private uint _next = 1u;

	public const int TimeoutMs = 60000;

	public const int IdleForgetMs = 300000;

	public static readonly Dictionary<string, long> LocalResults = new Dictionary<string, long>(StringComparer.Ordinal) { ["InProcessGameServerSessionComponent.TrySaveGame"] = 128L };

	public static readonly Dictionary<string, Keep> Denied = new Dictionary<string, Keep>(StringComparer.Ordinal)
	{
		["CameraStatePerPlayerData.StoreCameraData"] = Keep.Succeed,
		["CameraPerPlayerData.StoreCameraEntity"] = Keep.Succeed,
		["ControlPerPlayerData.StoreControlledEntity"] = Keep.Succeed,
		["PossessionHelper.PossessInitialEntityAsync"] = Keep.Hold,
		["RespawnSessionComponent.RequestRespawn"] = Keep.Hold,
		["InProcessGameServerSessionComponent.TrySaveGame"] = Keep.LocalOnly
	};

	public long Sent;

	public long Local;

	public long DeniedCount;

	public long CannotTravel;

	public long Refused;

	public long Finished;

	public long TimedOut;

	public long Failed;

	public long CameraCoalesced;

	public long ReleasedNoOps;

	public long ServerStarted;

	private readonly HashSet<string> _logged = new HashSet<string>(StringComparer.Ordinal);

	public bool PerPlayer;

	public const int CameraIntervalMs = 100;

	private long _lastCameraMs = -4611686018427387904L;

	private readonly List<(object Item, long FirstMs)> _waiting = new List<(object, long)>();

	public const int ObservableWaitMs = 10000;

	private readonly List<Action> _local = new List<Action>();

	public const int EntityWaitMs = 3000;

	public int InFlight => _byId.Count;

	private bool FirstOf(string key) => _logged.Add(key);

	public ClientStories(IMpLog log)
	{
		_log = log;
	}

	private void Count(string key)
	{
		_counts[key] = _counts.TryGetValue(key, out var count) ? count + 1 : 1;
	}

	public void OnMove(Delegate d, ClientSession session, ObjectSpace space, Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent? comp)
	{
		if (!ReadClosure(d, out NetworkStoryContext? context, out object? box, out var closureComp))
		{
			Count("unreadable closure");
			_log.WarnOnce("story-unreadable", "client: a network-story closure has an unexpected shape (" + ClosureShape(d) + "); such stories are dropped");
			return;
		}
		StoryMove? move = Prepare(context, box, closureComp ?? comp, space);
		if (move != null)
		{
			session.SendStoryMove(move);
		}
	}

	internal static bool ReadClosure(Delegate d, out NetworkStoryContext? context, out object? box, out Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent? comp)
	{
		context = null;
		box = null;
		comp = null;
		object target = d.Target;
		if (target == null)
		{
			return false;
		}
		FieldInfo[] fields = ObjectCodec.Fields(target.GetType());
		context = fields.FirstOrDefault(f => typeof(NetworkStoryContext).IsAssignableFrom(f.FieldType))?.GetValue(target) as NetworkStoryContext;
		box = fields.FirstOrDefault(f => f.FieldType == typeof(IAsyncStateMachine))?.GetValue(target);
		comp = fields.FirstOrDefault(f => typeof(Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent).IsAssignableFrom(f.FieldType))?.GetValue(target) as Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent;
		return context != null && box != null;
	}

	private static string ClosureShape(Delegate d)
	{
		if (d.Target != null)
		{
			return d.Target.GetType().Name + ": " + string.Join(", ",
				from f in ObjectCodec.Fields(d.Target.GetType())
				select f.FieldType.Name + " " + f.Name);
		}
		return d.Method.Name + ", no target";
	}

	internal StoryMove? Prepare(NetworkStoryContext context, object box, Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent? comp, ObjectSpace space)
	{
		string stateMachineType = ObjectCodec.TypeName(box.GetType());
		string name = StoryState.Name(stateMachineType);
		if (Denied.TryGetValue(name, out var keep) && keep == Keep.LocalOnly)
		{
			DeniedCount++;
			Count(name + ": answered here (never sent to the host)");
			object? result = null;
			if (LocalResults.TryGetValue(name, out var localResult))
			{
				Type type = StoryState.ResultType(context);
				if (type is not null && type.IsEnum)
				{
					result = Enum.ToObject(type, localResult);
				}
			}
			_log.Info($"client: STORY {name} answered here{(result != null ? $" ({result})" : "")}: joiners do not save the host's world");
			_local.Add(() => FinishLocal(comp, context, result, null));
			return null;
		}
		if (Denied.TryGetValue(name, out keep))
		{
			// With per-player identities a Succeed story is the player's own and goes to the host.
			bool send = PerPlayer && keep == Keep.Succeed;
			if (send && name == "CameraStatePerPlayerData.StoreCameraData")
			{
				long now = MpClock.NowMs();
				if (now - _lastCameraMs < 100)
				{
					CameraCoalesced++;
					_local.Add(() => FinishLocal(comp, context, null, null));
					return null;
				}
				_lastCameraMs = now;
			}
			if (!send)
			{
				DeniedCount++;
				Count(name + (keep == Keep.Succeed ? ": kept local (shared identity)" : ": held (the world sync's character flow does this)"));
				if (keep == Keep.Succeed)
				{
					_local.Add(() => FinishLocal(comp, context, null, null));
				}
				return null;
			}
		}
		if (name.StartsWith("PlayerToolbarServerSessionComponent.", StringComparison.Ordinal) && comp != null)
		{
			Local++;
			if (!PerPlayer)
			{
				Count(name + ": applied to this player's own toolbar");
				_local.Add(() => RunToolbarLocally(name, box, comp, context));
				return null;
			}
			Count(name + ": applied locally and sent to the host");
			_local.Add(() => ApplyToolbarLocally(name, box, comp));
		}
		bool isNew = false;
		if (!_byContext.TryGetValue(context, out var id))
		{
			id = _next++;
			_byContext[context] = id;
			_byId[id] = new Entry
			{
				Id = id,
				Context = context,
				Comp = comp,
				Name = name
			};
			isNew = true;
		}
		Entry entry = _byId[id];
		entry.LastMs = MpClock.NowMs();
		space.ClearLocalIds();
		StoryType? story = StoryState.Find(box.GetType());
		string? fail = story == null ? "not a network story the game declares" : null;
		List<(string, WireValue)>? fields = story == null ? null : StoryState.EncodeArguments(story, box, space, out fail);
		if (fields == null || space.LocalIds.Count > 0)
		{
			fail ??= "refers to an entity the host does not have";
			if (fail.Contains("was already released by the host", StringComparison.Ordinal))
			{
				bool isVoid = StoryState.ResultType(context) == typeof(DBNull);
				ReleasedNoOps++;
				Count(name + ": names a model already released (no-op here)");
				if (FirstOf("released " + name))
				{
					_log.Info($"client: STORY {name} names a model the host already released ({fail}); {(isVoid ? "done here as a no-op" : "skipped here like Keen does")} (further ones only counted)");
				}
				Forget(id);
				_local.Add(() => FinishLocal(comp, context, null, isVoid ? null : new Keen.VRage.Multiplayer.NetworkStories.SkipNetworkStoryException()));
				return null;
			}
			CannotTravel++;
			Count(name + ": cannot travel (" + fail + ")");
			_log.WarnOnce("story-travel-" + name, $"client: story {name} cannot travel to the host ({fail}); it is skipped here");
			Forget(id);
			_local.Add(() => FinishLocal(comp, context, null, new Keen.VRage.Multiplayer.NetworkStories.SkipNetworkStoryException()));
			return null;
		}
		Sent++;
		entry.AtHost = true;
		if (isNew && AudioRouting.IsBuildStory(name))
		{
			AudioRouting.Instance.NoteOwnBuild(MpClock.NowMs());
		}
		Count(name + ": sent");
		if (FirstOf("sent " + name) | (Sent <= 20))
		{
			_log.Info($"client: STORY {name} #{id} -> host ({fields.Count} fields){(Sent > 20 ? "; first of its kind" : "")}");
		}
		return new StoryMove
		{
			StoryId = id,
			StateMachineType = stateMachineType,
			Fields = fields
		};
	}

	private static bool IsWaitable(string fail) => fail.Contains("is not on this side", StringComparison.Ordinal);

	private static int WaitMs(string fail)
	{
		if (!fail.Contains("observ", StringComparison.Ordinal))
		{
			return 3000;
		}
		return 10000;
	}

	public void ProcessInbox(ConcurrentQueue<object> inbox, ObjectSpace space, Action<Action> post)
	{
		foreach (Action action in _local)
		{
			post(action);
		}
		_local.Clear();
		var batch = new List<object>();
		long nowMs = MpClock.NowMs();
		foreach (var waited in _waiting)
		{
			batch.Add(waited);
		}
		_waiting.Clear();
		object? received;
		while (inbox.TryDequeue(out received))
		{
			batch.Add(received);
		}
		foreach (object queued in batch)
		{
			object message = queued;
			long since = nowMs;
			if (queued is ValueTuple<object, long> waited)
			{
				(message, since) = waited;
			}
			if (message is not StoryMove move)
			{
				if (message is not StoryFinish finish)
				{
					continue;
				}
				if (!_byId.TryGetValue(finish.StoryId, out Entry? story))
				{
					Count("finish for an unknown story");
					continue;
				}
				object? storyResult = null;
				Exception? ex = null;
				if (finish.Status == 0)
				{
					string? fail = null;
					Type type = StoryState.ResultType(story.Context);
					space.ClearLocalIds();
					space.LocalStubs.Clear();
					storyResult = type == typeof(DBNull) ? DBNull.Value : ObjectCodec.Decode(finish.Result, type, space, translated: false, ref fail, storyResult: true);
					if (fail != null && IsWaitable(fail) && nowMs - since < WaitMs(fail))
					{
						_waiting.Add((finish, since));
						continue;
					}
					if (fail != null)
					{
						ex = new Keen.VRage.Multiplayer.NetworkStories.SkipNetworkStoryException();
						Count(story.Name + ": result unusable here (" + fail + ")");
					}
				}
				else
				{
					ex = new Keen.VRage.Multiplayer.NetworkStories.SkipNetworkStoryException();
					Count($"{story.Name}: {(finish.Status == 2 ? "skipped" : "failed")} on the host ({finish.Text})");
				}
				Forget(story.Id);
				Finished++;
				string outcome;
				if (ex == null)
				{
					outcome = "ok" + StoryState.Describe(storyResult);
				}
				else
				{
					outcome = finish.Text.Length > 0 ? finish.Text : "result unusable here";
				}
				if (FirstOf("finished " + story.Name + (ex == null ? "" : " " + StoryState.Shape(outcome))) | (Finished <= 20))
				{
					_log.Info($"client: STORY {story.Name} #{story.Id} finished on the host ({outcome})");
				}
				NetworkStoryContext ctx = story.Context;
				var comp = story.Comp;
				post(() => FinishLocal(comp, ctx, storyResult, ex));
				continue;
			}
			if (!_byId.TryGetValue(move.StoryId, out Entry? entry))
			{
				if ((move.StoryId & ServerOrigin) != 0)
				{
					StartServerStory(move, space, post, nowMs, since);
					continue;
				}
				Count("move for an unknown story");
				continue;
			}
			// Only results come back for the client's own stories; a host never sends their state.
			Refuse(entry.Name, entry.Name, "the host sent state for a story this client started");
			Forget(entry.Id);
			NetworkStoryContext ctx2 = entry.Context;
			var comp2 = entry.Comp;
			post(() => FinishLocal(comp2, ctx2, null, new Keen.VRage.Multiplayer.NetworkStories.SkipNetworkStoryException()));
		}
		long now = MpClock.NowMs();
		foreach (Entry idle in _byId.Values.Where(x => !x.AtHost && now - x.LastMs > 300000).ToList())
		{
			Forget(idle.Id);
		}
		foreach (Entry stale in _byId.Values.Where(x => x.AtHost && now - x.LastMs > 60000).ToList())
		{
			TimedOut++;
			_log.Warn($"client: story {stale.Name} #{stale.Id} got no answer from the host in {60} s; skipped");
			Forget(stale.Id);
			NetworkStoryContext ctx3 = stale.Context;
			var comp3 = stale.Comp;
			post(() => FinishLocal(comp3, ctx3, null, new Keen.VRage.Multiplayer.NetworkStories.SkipNetworkStoryException()));
		}
	}

	private void StartServerStory(StoryMove move, ObjectSpace space, Action<Action> post, long now, long since)
	{
		StoryType? story = StoryState.Find(move.StateMachineType, out string why);
		string name = story?.Name ?? StoryState.Clip(move.StateMachineType);
		if (story == null || !story.HostMayStart)
		{
			Refuse(story?.Name ?? "unlisted", name, story == null ? why : "hosts do not start this story on clients");
			return;
		}
		var context = (NetworkStoryContext)Activator.CreateInstance(story.Builder.FieldType)!;
		context.Narrator = ServerStoryNarrator.Instance;
		space.ClearLocalIds();
		space.LocalStubs.Clear();
		object? sm = StoryState.Start(story, move, space, context, StoryState.FirstAwaitState, out string? refused, out string? fail);
		if (refused != null)
		{
			Refuse(story.Name, name, refused);
			return;
		}
		if (sm == null && fail != null && IsWaitable(fail) && now - since < WaitMs(fail))
		{
			_waiting.Add((move, since));
			return;
		}
		if (sm == null)
		{
			Failed++;
			_log.WarnOnce("client-host-story-" + name, $"client: host story {name} cannot run here ({fail}); skipped");
			return;
		}
		ServerStarted++;
		if (FirstOf("host-started " + name))
		{
			_log.Info($"client: STORY {name} started by the host runs here");
		}
		post(() =>
		{
			try { ((IAsyncStateMachine)sm).MoveNext(); }
			catch (Exception ex) { _log.Warn($"client: host story {name} threw: {ex.GetType().Name}: {ex.Message}"); }
		});
	}

	// Keyed by a story this client knows (or "unlisted") so a host cannot grow the log-once set with made-up names.
	private void Refuse(string kind, string name, string why)
	{
		Refused++;
		Count(kind + ": refused");
		_log.WarnOnce("story-refused-" + kind, $"story: refused {name} ({why}) (further ones of this story only counted)");
	}

	private void Forget(uint id)
	{
		if (_byId.Remove(id, out Entry? entry))
		{
			_byContext.Remove(entry.Context);
		}
	}

	public static void FinishLocal(Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent? comp, NetworkStoryContext context, object? result, Exception? ex)
	{
		try
		{
			comp?.SetStoryInProgress(context, false);
		}
		catch (Exception)
		{
		}
		try
		{
			NetworkStory story = context.Story;
			if (story != null)
			{
				Type type = StoryState.ResultType(context);
				if (result == null && type.IsValueType)
				{
					result = Activator.CreateInstance(type);
				}
				MethodInfo setResult = story.GetType().GetMethod("SetResult", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
				if (result != null && !type.IsValueType && !type.IsInstanceOfType(result))
				{
					SetReplicaResult.MakeGenericMethod(type).Invoke(null, new object?[] { story, setResult, result, ex });
				}
				else
				{
					setResult.Invoke(story, new object?[] { result, ex });
				}
			}
		}
		catch (Exception)
		{
		}
	}

	private static readonly MethodInfo SetReplicaResult = typeof(ClientStories).GetMethod(nameof(SetReplica), BindingFlags.NonPublic | BindingFlags.Static)!;

	// Reflection refuses a base-typed replica (WireTypes.IsReplicaOf) for a server-typed result; Keen's translation writes it unchecked too.
	private static void SetReplica<T>(object story, MethodInfo setResult, object result, Exception? ex) where T : class =>
		setResult.CreateDelegate<Action<T, Exception?>>(story)(Unsafe.As<T>(result), ex);

	private void RunToolbarLocally(string name, object box, Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent comp, NetworkStoryContext context)
	{
		Exception? ex = ApplyToolbarLocally(name, box, comp) ? null : new Keen.VRage.Multiplayer.NetworkStories.SkipNetworkStoryException();
		FinishLocal(comp, context, null, ex);
	}

	internal static IdentityId LocalIdentity(Session session) =>
		session.SessionComponents.TryGet<ClientPlayersSessionComponent>()?.LocalPlayerIdentity ?? InProcessPerPlayerDataSessionComponent.HotfixIdentity;

	private bool ApplyToolbarLocally(string name, object box, Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent comp)
	{
		try
		{
			Session session = comp.Session;
			IPerPlayerData perPlayer = session.SessionComponents.Components.OfType<IPerPlayerData>().FirstOrDefault() ?? throw new InvalidOperationException("no IPerPlayerData on the client");
			var toolbar = perPlayer.GetPerPlayerData<ToolbarPerPlayerData>(LocalIdentity(session));
			_log.Info("client: " + ApplyToolbarStory(name, box, toolbar));
			return true;
		}
		catch (Exception ex)
		{
			_log.WarnOnce("toolbar-local-" + name, $"client: applying {name} to the local toolbar failed: {ex.GetType().Name}: {ex.Message}");
			return false;
		}
	}

	internal static string ApplyToolbarStory(string name, object box, ToolbarPerPlayerData data)
	{
		FieldInfo[] fields = ObjectCodec.Fields(box.GetType());
		if (name.EndsWith(".RequestSetTile", StringComparison.Ordinal))
		{
			var selection = (ToolbarSelection)fields.First(x => x.FieldType == typeof(ToolbarSelection)).GetValue(box);
			object released = fields.First(x => x.FieldType.Name.StartsWith("ReleaseToServer", StringComparison.Ordinal)).GetValue(box);
			var tile = released.GetType().GetProperty("Value").GetValue(released) as ToolbarTile;
			if (selection.ToolbarIndexIndex >= 0 && selection.ToolbarIndexIndex < data.ToolbarTiles.Count)
			{
				ObservableCollection<ToolbarTile> row = data.ToolbarTiles[selection.ToolbarIndexIndex];
				if (selection.TileIndex >= 0 && selection.TileIndex < row.Count)
				{
					row[selection.TileIndex] = tile;
					return $"toolbar tile {selection.ToolbarIndexIndex}/{selection.TileIndex} set to {tile?.GetType().Name ?? "empty"} on this player's own toolbar";
				}
			}
			return $"toolbar tile {selection.ToolbarIndexIndex}/{selection.TileIndex} is outside this player's toolbar ({data.ToolbarTiles.Count} rows); nothing set";
		}
		if (name.EndsWith(".RequestResetToolbar", StringComparison.Ordinal))
		{
			(fields.First(x => x.FieldType == typeof(DefaultToolbarDefinition)).GetValue(box) as DefaultToolbarDefinition)?.ResetToolbar(data);
			return "toolbar reset to its defaults on this player's own toolbar";
		}
		return name + ": nothing to do on this player's own toolbar";
	}

	public string Describe()
	{
		return $"stories: sent {Sent}, host-started {ServerStarted}, refused {Refused}, finished {Finished}, local toolbar {Local}, kept local {DeniedCount}, camera coalesced {CameraCoalesced}, cannot travel {CannotTravel}, released-model no-ops {ReleasedNoOps}, failed {Failed}, timed out {TimedOut}, in flight {_byId.Count}" + (_counts.Count == 0 ? "" : "; " + string.Join(", ",
			from kv in _counts.OrderByDescending(kv => kv.Value).Take(8)
			select $"{kv.Key} x{kv.Value}"));
	}
}
