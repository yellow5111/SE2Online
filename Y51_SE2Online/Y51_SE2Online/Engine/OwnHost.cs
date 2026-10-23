using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.Core.Game.Serialization;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Definitions;
using Keen.VRage.Library.Definitions.Internal;
using Keen.VRage.Library.Serialization;
using Keen.VRage.Library.Utils;
using Keen.VRage.Multiplayer;
using Keen.VRage.Multiplayer.Replications;
using Keen.VRage.Multiplayer.Replications.InProcess;
using Keen.VRage.Multiplayer.Transport;

namespace Y51_SE2Online.Engine;

/// <summary>
/// One side of a session as SE2 Online's netcode sees it: the game's replication core of that session (which decides what
/// replicates, as in a single-player game), SE2 Online's ids for its objects, the replay context remote signals land in,
/// and the serialization services the game's per-type serializers need to write a reference as an id.
/// </summary>
internal sealed class OwnHost
{
	private static readonly ConditionalWeakTable<Session, OwnHost> Hosts = new();

	/// <summary>
	/// The session's host, made on first use (scene thread). A player's game makes it while the game still builds the
	/// session's components (Session.SessionComponents is not set yet): the caller then hands over what that build
	/// already has, the replication component (its entity is the session components' entity), the core and the context.
	/// </summary>
	public static OwnHost For(Session session, ReplicationSessionComponent? replication = null, ReplicationCoreComponent? core = null, IReplicationContext? context = null)
		=> Hosts.GetValue(session, s => new OwnHost(s, replication, core, context));

	public static OwnHost? Existing(Session? session) => session != null && Hosts.TryGetValue(session, out var host) ? host : null;

	public readonly Session Session;
	public readonly bool IsServer;
	public readonly ReplicationSessionComponent Replication;
	public readonly ReplicationCoreComponent Core;
	public readonly ReplayContext Replay;
	public readonly OwnIds Ids = new();
	private readonly ReplayContext? _root;
	private readonly HashSet<Entity> _removed = new(ReferenceEqualityComparer.Instance);
	private readonly DefinitionManagerDefinitionSerializationContext _definitions = new(Singleton<DefinitionManager>.Instance);
	private readonly SessionContext _sessionContext;
	private ulong _nextBatch;
	// Process-wide: a world loaded again never hands out a ClientId a player of the last world still holds somewhere.
	private static int _nextClient = 1;

	private OwnHost(Session session, ReplicationSessionComponent? replication, ReplicationCoreComponent? core, IReplicationContext? context)
	{
		Session = session;
		Entity components = replication?.Entity ?? session.SessionComponents ?? throw new InvalidOperationException("the session has no components yet");
		Replication = replication ?? (ReplicationSessionComponent?)components.TryGet<ReplicationServerSessionComponent>()
			?? components.TryGet<ReplicationClientSessionComponent>()
			?? throw new InvalidOperationException("the session has no replication");
		IsServer = Replication is ReplicationServerSessionComponent;
		Core = core ?? session.TryGet<ReplicationCoreComponent>() ?? KeenOff.CoreOf(components) ?? throw new InvalidOperationException("the session has no replication core");
		context ??= session.TryGet<IReplicationContext>() ?? throw new InvalidOperationException("the session has no replication context");
		_sessionContext = new SessionContext(session);
		// The session's own components are id 0 on both sides (signals raised on them name that entity).
		Ids.Add(components, new NetworkId(0));
		if (context is ClientSceneContext client)
		{
			Replay = client.ReplayContext ?? throw new InvalidOperationException("the player's replay context is not made yet");
			MapToReplay(components);
		}
		else
		{
			_root = ((SceneContext)context).ReplayContext;
			Replay = new ReplayContext(Core.ReplicationStrategy);
			Replay.AddObjectPairing(session, session);
		}
	}

	/// <summary>A new ClientId for a player joining this server (1 is a co-op host's own game).</summary>
	public ClientId NextClientId() => new(System.Threading.Interlocked.Increment(ref _nextClient));

	public ulong NextBatchId() => ++_nextBatch;

	/// <summary>Server: the object's id, allocated (and made addressable by remote signals) the first time it is needed.</summary>
	public NetworkId GetOrRegisterId(object instance)
	{
		if (instance is Entity entity && _removed.Remove(entity))
		{
			// Back in the scene the same frame it left: it keeps its id.
			Ids.ResumeResolving(entity);
			Session.OnBeforeEntityRemovedFromScene.Add(entity, OnMappedEntityRemoved);
		}
		NetworkId id = Ids.GetOrAdd(instance, out bool added);
		if (added)
		{
			MapToReplay(instance);
			if (instance is Entity partition) Session.OnBeforeEntityRemovedFromScene.Add(partition, OnMappedEntityRemoved);
		}
		return id;
	}

	private void OnMappedEntityRemoved(Entity entity)
	{
		Ids.StopResolving(entity);
		_removed.Add(entity);
	}

	/// <summary>Server, after every player's frame of this tick went out: entities that left the scene retire their ids.</summary>
	public void RetireRemoved()
	{
		if (_removed.Count == 0) return;
		try
		{
			foreach (Entity entity in _removed)
			{
				UnmapFromReplay(entity);
				Ids.Retire(entity);
			}
		}
		finally { _removed.Clear(); }
	}

	public void MapToReplay(object instance)
	{
		if (!Replay.HasPairingRemote(instance) && !Replay.HasPairingLocal(instance)) Replay.AddObjectPairing(instance, instance);
	}

	public void UnmapFromReplay(object instance)
	{
		if (Replay.HasPairingRemote(instance)) Replay.RemoveObjectPairing(instance);
	}

	/// <summary>Server, before remote signals replay: the scene's own handlers apply to them too.</summary>
	public void PrepareInbound()
	{
		if (_root != null) Replay.OverrideHandlers(_root);
	}

	/// <summary>
	/// The services the game's serializers look up: definitions by id, the session, and every reference as a network id
	/// of <paramref name="ids"/> (or of this side's table). The replication components add their own reference contexts
	/// (entities, observables, blobs) on top, as they do for any transport.
	/// </summary>
	public ServiceLocatorContext.Builder Contexts(ReplicationTargetContext target, IReplicationSerializationContext? ids = null)
	{
		var builder = ServiceLocatorContext.Builder.Create(default(ReadOnlySpan<object>));
		builder.Add(target);
		builder.Add(_definitions).Add(_sessionContext).Add(ImmutableSingleton<SkipNonSharedObjectValidation>.Instance).Add(SerializationOverrides.AllowConfigurationSerialization);
		var provider = ReplicationSerializationContextProvider.Configure(ref builder, ids ?? Ids);
		Replication.ConfigureSerialization(provider, ref builder);
		return builder;
	}

	/// <summary>A peer has one session to name: this one.</summary>
	private sealed class SessionContext(Session session) : CustomSerializationContext, ISessionProxySerializationContext
	{
		public override ImmutableArray<Type> Keys => ImmutableCollectionsMarshal.AsImmutableArray(new[] { typeof(ISessionProxySerializationContext) });

		public Session GetSession(int sessionId) => session;

		public int GetSessionId(Session other) => 0;
	}
}
