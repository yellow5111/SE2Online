using System;
using System.Collections.Concurrent;
using System.IO;
using Keen.Game2.Client.GameSystems.Blueprints;
using Keen.Game2.Simulation.GameSystems.Ownership;
using Keen.Game2.Simulation.RuntimeSystems.Serialization;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.Game2.Simulation.WorldObjects.CubeGrids.Physicss;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.Core.Game.Definitions;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Definitions;
using Keen.VRage.DCS.ObjectBuilders;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Library.Utils;
using Keen.VRage.Physics.Data;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// Respawn ships from blueprints the host ships (Content\Ships or a path in server.json): read the way the game reads a
/// blueprint (EntityContainerDeserializer over the folder, with its snapshot and migrations), cloned per spawn so every
/// ship gets fresh entities, given to the player and spawned staged like an encounter. Not the paste path: a survival
/// paste clears build progress and makes a projection to weld.
/// </summary>
internal sealed partial class HostWorld
{
    private sealed record BlueprintTemplate(EntityBundle Bundle, DateTime Written, string Name);

    private static readonly ConcurrentDictionary<string, BlueprintTemplate> BlueprintTemplates = new(StringComparer.OrdinalIgnoreCase);

    // async void only around a VRage task: awaiting Task.SetLifetime anywhere but in a method returning a VRage task throws
    // from the thread pool and ends the game (pre4 crash).
    private async void SpawnBlueprintShip(Session session, string folder, WorldTransform at, Joiner j, long request)
    {
        Entity[] roots;
        try
        {
            var identity = new IdentityId(j.Local ? (long)(session.SessionComponents.TryGet<Keen.Game2.Simulation.GameSystems.Identities.IdentitiesSessionComponent>()?.LocalIdentity ?? default) : IdentityOf(j.ClientId));
            roots = await SpawnBlueprintAsync(session, folder, at, identity);
        }
        catch (Exception ex)
        {
            _log.Warn($"host spawn: spawning the blueprint ship from {folder} failed: {ex}");
            roots = Array.Empty<Entity>();
        }
        try { HandOver(j, request, roots); }
        catch (Exception ex) { _log.Warn("host spawn: handing over the blueprint ship failed: " + ex); }
    }

    private static async Keen.VRage.Library.Threading.Task<Entity[]> SpawnBlueprintAsync(Session session, string folder, WorldTransform at, IdentityId identity)
    {
        await Keen.VRage.Library.Threading.Task.SetLifetime(session.SessionComponents);
        var template = await BlueprintTemplateOf(folder, session.SessionComponents);
        var bundle = PrepareBlueprint(template.Bundle, at, identity, template.Name);
        await session.Scene.MoveToDCS();
        return await session.Get<IEntitySpawner>().SpawnBundleAsync(new GameEntitySerializer.BundleInitArgs { Bundle = bundle, SpawnMode = GameEntitySerializer.SpawnMode.Staged });
    }

    // A blueprint that failed to load is shown unavailable until its file changes.
    private static readonly ConcurrentDictionary<string, DateTime> BrokenBlueprints = new(StringComparer.OrdinalIgnoreCase);

    private static bool BlueprintBroken(string folder)
        => BrokenBlueprints.TryGetValue(folder, out var when) && when == File.GetLastWriteTimeUtc(Path.Combine(folder, "grid.json.vrb"));

    /// <summary>The blueprint read once (again when its file changes).</summary>
    private static async Keen.VRage.Library.Threading.Task<BlueprintTemplate> BlueprintTemplateOf(string folder, object lifetime)
    {
        await Keen.VRage.Library.Threading.Task.SetLifetime(lifetime);
        string grid = Path.Combine(folder, "grid.json.vrb");
        DateTime written = File.GetLastWriteTimeUtc(grid);
        if (BlueprintTemplates.TryGetValue(folder, out var cached) && cached.Written == written) return cached;
        // The game version picks the data migrations; the rest of the metadata is for the blueprint screen.
        var info = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(folder, ".container-info")))?["$Value"]?["Meta"];
        var meta = new BlueprintMetadata { GameVersion = (int?)info?["GameVersion"] ?? 0, GameBuildNumber = (int?)info?["GameBuildNumber"] ?? 0 };
        string? title = (string?)info?["UserInfo"]?["DisplayName"]?["RawText"];
        using var reader = new EntityContainerDeserializer<BlueprintMetadata>(new BlueprintContainer(folder, meta), Singleton<DefinitionManager>.Instance.GetPostProcessor<DefaultPrefabsPostProcessor>());
        EntityBundle bundle;
        try { bundle = await reader.Deserialize(); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Only a blueprint that cannot be read is shown unavailable (until its file changes); a cancelled or failed spawn is not.
            try { BrokenBlueprints[folder] = written; } catch (Exception) { }
            throw;
        }
        if (reader.PrunedEntities is { } pruned && pruned.PrunedOBs.Count > 0)
            GameLog.Instance.Warn($"host spawn: blueprint {folder}: {pruned.PrunedOBs.Count} parts use blocks this game does not have; they are left out");
        string name = !string.IsNullOrWhiteSpace(title) ? title.Trim() : Path.GetFileName(folder.TrimEnd('/', '\\'));
        var template = new BlueprintTemplate(bundle, written, name);
        BlueprintTemplates[folder] = template;
        GameLog.Instance.Info($"host spawn: blueprint {name} read from {folder} ({bundle.Roots} grids)");
        return template;
    }

    // Synchronous: the object builders are reached through ref locals. What the game's paste does to a blueprint before it
    // spawns (BlockPlacerNetworkStories.PlaceStandalone), without the survival clearing.
    private static EntityBundle PrepareBlueprint(EntityBundle template, WorldTransform at, IdentityId owner, string name)
    {
        var bundle = template.CloneBundle();
        bundle.TransformInvBundle(bundle.RootBuilders[0].OB<WorldTransformComponent, WorldTransformComponentObjectBuilder>().Transform);
        bundle.TransformBundle(at);
        for (int i = 0; i < bundle.Roots; i++)
        {
            ref var physics = ref bundle.Builders[i].OB<GridPhysicsComponent, GridPhysicsObjectBuilder>();
            physics.LinearVelocity = default;
            physics.AngularVelocity = default;
            physics.MotionType = BodyArgs.Motion.Dynamic;
        }
        bundle.Builders[0].OB<CubeGridComponent, CubeGridObjectBuilder>().DisplayName = name;
        OwnershipSessionComponent.ChangeOwnership(bundle, owner);
        return bundle;
    }

    /// <summary>A blueprint folder as the game's container: read-only.</summary>
    private sealed class BlueprintContainer(string folder, BlueprintMetadata meta) : IEntityContainerAdapter<BlueprintMetadata>
    {
        public string Id => folder;
        public BlueprintMetadata Meta => meta;

        public Keen.VRage.Library.Threading.Task<Stream?> TryOpenReadStream(string name)
        {
            string path = Path.Combine(folder, name);
            if (!File.Exists(path)) path += ".vrb";
            return new Keen.VRage.Library.Threading.Task<Stream?>(File.Exists(path) ? File.OpenRead(path) : null);
        }

        public Keen.VRage.Library.Threading.Task<Stream> OpenWriteStream(string name) => throw new NotSupportedException("blueprint ships are read-only");
        public Keen.VRage.Library.Threading.Task StoreBlobs(BlobSetIndexer blobs, IDataOffloadingSnapshot snapshot) => throw new NotSupportedException("blueprint ships are read-only");
        public Keen.VRage.Library.Threading.Task ExportBlobs(BlobSetIndexer blobs, IStreamedOutBlobs streamed) => default;
        public Keen.VRage.Library.Threading.Task DeleteUnrelatedBlobs(BlobSetIndexer blobs, IStreamedOutBlobs? streamed = null) => default;
        public Keen.VRage.Library.Threading.Task DeleteFileIfPresent(string name) => default;
        public void Dispose() { }
    }
}
