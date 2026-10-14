using System;
using System.Collections;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.Builders;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Definitions;
using Keen.VRage.Library.Threading;
using Keen.VRage.Library.Utils;
using Keen.VRage.Multiplayer.Data;
using Keen.VRage.Multiplayer;
using Keen.VRage.Multiplayer.NetworkStories;
using Keen.VRage.Multiplayer.Replications;
using Keen.VRage.Multiplayer.Replications.InProcess;
using Keen.VRage.Multiplayer.Sessions;
using Y51_SE2Online.Core;
using Y51_SE2Online.SyncEngine;
using Task = Keen.VRage.Library.Threading.Task;

namespace Y51_SE2Online.Dedicated;

internal static class HeadlessReplication
{
    private sealed class LocalClient : ILocalClientProvider
    {
        public ClientId LocalClientId => new ClientId(1);
    }

    private sealed class Pairing : IReplicationPairingProvider
    {
        public T GetPairedObject<T>(T localObject) where T : class => localObject;
    }

    private static IMpLog _log;
    private static bool _installed;
    private static readonly List<(int Offset, Segment Seg)> FrameScratch = new();
    private static readonly Dictionary<int, Segment[]> Frames = new();
    private static readonly MethodInfo SizeOf = typeof(Unsafe).GetMethod(nameof(Unsafe.SizeOf));
    private static readonly MethodInfo HasReferences = typeof(RuntimeHelpers).GetMethod(nameof(RuntimeHelpers.IsReferenceOrContainsReferences));
    public static bool Active => DedicatedRuntime.Headless;
    public static long FramesPublished { get; private set; }
    public static long CommandsPublished { get; private set; }

    public static void Install(Harmony harmony, IMpLog log)
    {
        if (_installed) return;
        _installed = true;
        _log = log;
        Patch(harmony, AccessTools.TypeByName("Keen.Game2.OutOfProcess.DedicatedServerReplicationMockComponent"), "ConfigureSession", nameof(ConfigurePrefix));
        Patch(harmony, typeof(InProcessReplicationSessionComponent), "CommitFrame", nameof(CommitPrefix), Priority.Last);
        Patch(harmony, typeof(ServerSceneContext), "EnsureReplicationToClientIsScheduled", nameof(SchedulePrefix));
        Patch(harmony, typeof(ServerSceneContext), InterfaceMethod(nameof(IReplicationContext.CommitFrame)), nameof(ContextCommitPrefix));
        Patch(harmony, typeof(ServerSceneContext), InterfaceMethod(nameof(IReplicationContext.FlushReplication)), nameof(FlushPrefix));
        Patch(harmony, typeof(ServerSceneContext), InterfaceMethod(nameof(IReplicationContext.ReplicateStagedEntitiesAsync)), nameof(StagedPrefix));
        harmony.Patch(AccessTools.Method(typeof(ServerSceneContext), InterfaceMethod(nameof(IReplicationContext.OnBeforeEntityDestroyed))),
            postfix: new HarmonyMethod(typeof(HeadlessReplication), nameof(DestroyPostfix)));
        Patch(harmony, typeof(ServerSceneContext.ServerSessionComponent), "Keen.VRage.Multiplayer.NetworkStories.INetworkStoryNarrator.MoveToClient", nameof(MoveToClientPrefix));
        Patch(harmony, typeof(ObservableObjectReplicationSessionComponent), "SendReplicateNewCollection", nameof(CollectionPrefix));
        log.Info("dedicated: native server replication adapter installed; no local client session is created");
    }

    private static string InterfaceMethod(string name) => typeof(IReplicationContext).FullName + "." + name;

    private static void Patch(Harmony harmony, Type type, string method, string prefix, int priority = Priority.Normal)
    {
        MethodInfo original = type == null ? null : AccessTools.Method(type, method);
        if (original == null) throw new MissingMethodException(type?.FullName ?? "DedicatedServerReplicationMockComponent", method);
        harmony.Patch(original, prefix: new HarmonyMethod(typeof(HeadlessReplication), prefix) { priority = priority });
    }

    private static bool ConfigurePrefix(SessionBuilder sessionBuilder)
    {
        if (!Active) return true;
        if (!sessionBuilder.IsServer) throw new InvalidOperationException("Headless replication requires a server session.");
        Y51_SE2Online.SyncEngine.PassengerSeats.Apply();
        var replication = new InProcessReplication(Singleton<DefinitionManager>.Instance.GetDefinitionsOfType<PrefabBindingDefinition>().ToImmutableArray());
        ((ISessionConfigurator)replication.ServerContext).ConfigureSession(sessionBuilder);
        sessionBuilder.ExternalServices.Add((ILocalClientProvider)new LocalClient());
        sessionBuilder.ExternalServices.Add((IReplicationPairingProvider)new Pairing());
        sessionBuilder.SessionComponents.WithComponent((ComponentType: typeof(SimpleReplicationMappingSessionComponent), ObjectBuilder: new SimpleReplicationMappingSessionComponentObjectBuilder { InProcessReplication = replication }));
        _log.Info("dedicated: server session configured with native data, observable, signal and story replication");
        return false;
    }

    private static bool SchedulePrefix(ServerSceneContext __instance)
    {
        if (!Active) return true;
        __instance._replicationIsScheduled = true;
        return false;
    }

    private static bool ContextCommitPrefix(ServerSceneContext __instance)
    {
        if (!Active) return true;
        DrainEntities(__instance);
        return false;
    }

    private static void DrainEntities(ServerSceneContext context)
    {
        if (!context._replicationBundleBuilder.IsDirty)
        {
            context._replicationIsScheduled = false;
            return;
        }
        var data = context._replicationBundleBuilder.BuildAndReset();
        try
        {
            foreach (Entity entity in data.EntityObjectBuilders.Keys)
                if (!data.SkippedEntities.Contains(entity) && !context.ReplayContext.HasPairingLocal(entity))
                    context.ReplayContext.AddObjectPairing(entity, entity);
        }
        finally
        {
            data.ReturnToPool();
            context._replicationIsScheduled = false;
        }
    }

    private static bool FlushPrefix(ServerSceneContext __instance, ref Task __result)
    {
        if (!Active) return true;
        DrainEntities(__instance);
        __result = Task.FromResult(true).StripResult();
        return false;
    }

    private static bool StagedPrefix(ServerSceneContext __instance, Entity[] serverEntities, ref Keen.VRage.Library.Threading.Task<Action> __result)
    {
        if (!Active) return true;
        var data = new ServerSceneContext.ServerToClientEntityReplicationData();
        foreach (Entity entity in serverEntities)
        {
            if (!__instance.IsReplicatable(entity)) continue;
            __instance.Replication.ReplicationStrategy.SerializeForClientAsBundle(entity, data.ServerBundle, data.EntityObjectBuilders);
            if (entity.Data.TryGet<ParentData>(out var parent))
            {
                Entity parentEntity = parent.GetEntity(entity.Scene);
                if (__instance.ReplayContext.HasPairingLocal(parentEntity)) data.EntityParents[entity] = parentEntity;
            }
        }
        foreach (var entry in data.EntityObjectBuilders)
        {
            DEntityBuilder builder = default;
            __instance.Replication.ReplicationStrategy.CaptureEntityData(entry.Key, entry.Value.Definition, ref builder);
            data.EntityData.Add(entry.Key, builder);
            if (!__instance.ReplayContext.HasPairingLocal(entry.Key)) __instance.ReplayContext.AddObjectPairing(entry.Key, entry.Key);
        }
        HostWorld.BundlePostfix(data);
        __result = Task.FromResult<Action>(() => { });
        return false;
    }

    private static void DestroyPostfix(ServerSceneContext __instance, Entity entity)
    {
        if (Active && __instance.ReplayContext.HasPairingRemote(entity)) __instance.ReplayContext.RemoveObjectPairing(entity);
    }

    private static bool MoveToClientPrefix(NetworkStoryContext context, IAsyncStateMachine stateMachine, ClientRequest client)
    {
        if (!Active) return true;
        // Harmony skips every later prefix once one returns false, so a joiner's stories are routed from here.
        if (client.ClientId.Id > 1 && !HostIdentities.MoveToClientPrefix(context, stateMachine, client)) return false;
        if (!context.Skipped) context.SkipStory();
        return false;
    }

    private static void CollectionPrefix(ObservableObjectReplicationSessionComponent __instance, InProcessPairingKey<IObservedCollection> pairingKey, object data)
    {
        if (!Active) return;
        if (data is ObservableObjectReplicationSessionComponent.ClientObservedCollection) return;
        var replay = __instance.ReplayContext;
        replay.RemoveObjectPairing(data);
        replay.AddObjectPairing(new ObservableObjectReplicationSessionComponent.ClientObservedCollection((IList)data), pairingKey.Value);
    }

    private static bool CommitPrefix(InProcessReplicationSessionComponent __instance)
    {
        if (!Active || !__instance.IsServer) return true;
        if (__instance.IsUnloading) return false;
        var context = (ServerSceneContext)__instance._context;
        if (!context.ReplayContext.HasPairingLocal(__instance.Entity)) context.ReplayContext.AddObjectPairing(__instance.Entity, __instance.Entity);
        DrainEntities(context);
        var outgoing = __instance.Buffer;
        lock (__instance._exchangeSync) __instance.Buffer = InProcessReplicationSessionComponent.AllocateCB();
        try
        {
            Publish(outgoing);
            FramesPublished++;
        }
        finally { InProcessReplicationSessionComponent.ReturnCB(outgoing); }
        HostWorld.Current?.HeadlessServerWork(__instance);
        return false;
    }

    private static void Publish(ReplicationCommandBuffer buffer)
    {
        HostWorld world = HostWorld.Current;
        if (world == null) return;
        for (int chunk = 0; chunk < CommandBuffers.Count(buffer); chunk++)
        {
            ReadOnlySpan<byte> bytes = CommandBuffers.Bytes(buffer, chunk);
            while (bytes.Length > 0)
            {
                if (bytes.Length < 8) throw new InvalidOperationException("Incomplete native replication command header.");
                int stub = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(4));
                var layout = CommandLayouts.ByStub(stub);
                if (layout == null) throw new InvalidOperationException("Unknown native replication command " + stub);
                ReadOnlySpan<byte> payload = bytes.Slice(8);
                int length;
                switch (layout.Kind)
                {
                    case CommandKind.PostData:
                        length = checked(16 + BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(12)));
                        world.OnPostData(ReadEntity(buffer, payload, 4), BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(8)), payload.Slice(16, length - 16), buffer);
                        break;
                    case CommandKind.RemoveData:
                        length = 12;
                        world.OnRemoveData(ReadEntity(buffer, payload, 4), BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(8)));
                        break;
                    case CommandKind.PostInvoke:
                        length = 8;
                        int slot = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(4));
                        if (slot >= 0 && buffer.ReadObject(slot) is Delegate callback) world.OnInvoke(callback);
                        break;
                    default:
                        length = Frame(stub, payload);
                        world.OnSignal(buffer, stub, payload.Slice(0, length));
                        break;
                }
                if (length < 0 || length > payload.Length) throw new InvalidOperationException("Invalid native replication payload length for " + layout.Name);
                bytes = bytes.Slice(8 + length);
                CommandsPublished++;
            }
        }
    }

    private static Entity ReadEntity(ReplicationCommandBuffer buffer, ReadOnlySpan<byte> payload, int offset)
    {
        int slot = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(offset));
        return slot < 0 ? null : buffer.ReadObject(slot) as Entity;
    }

    internal static int Frame(int stub, ReadOnlySpan<byte> payload)
    {
        if (!Frames.TryGetValue(stub, out var segments))
        {
            var command = Keen.VRage.Library.Reflection.MetadataIndexerBase<ReplicationCommandBufferIndexer>.Instance.Table[stub];
            var list = new List<Segment>();
            foreach (ParameterInfo parameter in command.WriterInfo.GetParameters())
            {
                Type type = parameter.ParameterType;
                if (type.IsByRef) type = type.GetElementType();
                bool span = type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(ReadOnlySpan<>) || type.GetGenericTypeDefinition() == typeof(Span<>));
                if (span) list.Add(new Segment(SegmentKind.Span, 0, 0, type));
                else
                {
                    bool reference = !type.IsValueType || (bool)HasReferences.MakeGenericMethod(type).Invoke(null, null);
                    int size = reference ? 4 : (int)SizeOf.MakeGenericMethod(type).Invoke(null, null);
                    list.Add(new Segment(SegmentKind.Bytes, size, 0, type));
                }
            }
            segments = list.ToArray();
            Frames.Add(stub, segments);
        }
        return new CommandLayout { Segments = segments }.Frame(payload, FrameScratch);
    }
}
