using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Keen.Game2.Simulation.GameSystems.Contracts;
using Keen.Game2.Simulation.GameSystems.Discoveries;
using Keen.Game2.Simulation.GameSystems.Economy;
using Keen.Game2.Simulation.GameSystems.GPS;
using Keen.Game2.Simulation.GameSystems.Ownership;
using Keen.Game2.Simulation.GameSystems.PCU;
using Keen.Game2.Simulation.GameSystems.Player;
using Keen.Game2.Simulation.GameSystems.Progression;
using Keen.Game2.Simulation.GameSystems.Respawn;
using Keen.Game2.Simulation.WorldObjects.Characters;
using Keen.Game2.Simulation.WorldObjects.Characters.Stats;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.Pilotable;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.Game2.Simulation.WorldObjects.Items;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Definitions;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Library.Utils;
using Keen.VRage.Physics.Components;
using Keen.VRage.Physics.Data;
using Keen.VRage.Voxels.Components;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

// Player info for the admin panel's More info and players info: read from the world only when an admin asks.
internal sealed partial class HostWorld
{
    private static readonly string[] SuitStats = { "Health", "Energy", "Oxygen", "Hydrogen" };
    private StatDefinition[] _suitStats;

    /// <summary>Server thread: everything about one player (a SteamID or one of its game instances), or null without a session.</summary>
    private AdminQuery PlayerInfo(Session session, ulong target, int offset)
    {
        var host = _host;
        if (host == null) return null;
        var account = host.AccountInfo(target);
        long identity = account.Identity;
        var id = new IdentityId(identity);
        // GetPerPlayerData creates what is missing (and the save keeps it): only the data the world already has is read.
        List<object> held = null;
        if (identity != 0 && session.Get<IPerPlayerData>() is InProcessPerPlayerDataSessionComponent perPlayer) perPlayer._perPlayerData.TryGet(id, out held);
        T Data<T>() where T : PerIdentityData => held?.OfType<T>().FirstOrDefault();

        Entity body = FindCharacter(session, account);
        Vector3D? at = body?.Data.GetWorldTransform().Position;
        var grids = new List<OwnedGrid>();
        (string Name, string Owner, double Distance)? nearest = null;
        var settings = CleanupConfig;
        if (_cleanupState == null) LoadCleanupState();
        var context = settings.Enabled ? Context(session) : null;
        foreach (var group in Groups(session))
        {
            var main = Main(group);
            var position = main.Data.GetWorldTransform().Position;
            if (at is Vector3D me && Vector3D.Distance(position, me) is double d && (nearest == null || d < nearest.Value.Distance))
                nearest = (GridName(main), OwnerName(TeamPermissions.OwnerOf(main)), d);
            if (identity == 0 || !group.Any(g => TeamPermissions.OwnerOf(g) == identity)) continue;
            int pcu = 0;
            try { pcu = group.Sum(g => PCUExtensions.GetPCU(g.Get<CubeGridComponent>())); } catch (Exception) { }
            string cleanup = !settings.Enabled ? "off on this server"
                : Evaluate(group, settings, context, null, track: false) is { } going ? (settings.DryRun ? "a pass would list it (dry run): " : "the next pass removes it: ") + going.Why : "kept";
            grids.Add(new OwnedGrid(_ids.GetOrAssign(main), GridName(main), group.Sum(g => g.Get<CubeGridComponent>().CubeBlockStorage.CubeBlockCount), pcu, group.Count,
                group.Any(g => g.AsInterface<IPhysicsMotionProvider>()?.Motion == BodyArgs.Motion.Static), Powered(group), Moving(main),
                new Vec3D(position.X, position.Y, position.Z), PlaceOf(session, position), cleanup, _hacks.Values.Any(h => group.Contains(h.Grid) || h.Ship.Any(group.Contains))));
        }

        var sections = new List<DetailSection>(account.Sections)
        {
            Read("CHARACTER", () => Character(session, account, body, nearest)),
            Read("INVENTORY", () => Inventory(body, Data<PlayerCreditsData>())),
            Read("FACTION", () => Faction(identity)),
            Read("PCU", () => Pcu(session, host, account)),
            Read("SPAWN", () => Spawn(session, host, account, Data<RespawnPerPlayerData>(), Data<LastDeathPerPlayerData>())),
            Read("PROGRESS", () => Progress(Data<PlayerProgressionData>(), Data<DiscoveriesPlayerData>(), Data<ContractPerPlayerData>(), Data<GPSMarkersServerData>())),
        };
        return PlayerDetails.Build(account.SteamId, account.Name, sections, grids, at is Vector3D p ? new Vec3D(p.X, p.Y, p.Z) : null, offset);
    }

    private DetailSection Read(string title, Func<List<DetailLine>> read)
    {
        try { return new DetailSection(title, read().ToArray()); }
        catch (Exception ex)
        {
            _log.WarnOnce("player-info-" + title, $"admin: player info could not read {title.ToLowerInvariant()}: {ex}");
            return new DetailSection(title, [new DetailLine("Unavailable", "could not be read: " + ex.Message)]);
        }
    }

    /// <summary>The player's character: their live one when online, else the body the world keeps for them (alive first).</summary>
    private Entity FindCharacter(Session session, HostSession.PlayerAccount account)
    {
        if (account.ClientId != 0 && CharacterOf(account.ClientId) is { } own && !EngineCodec.IsDead(own)) return own;
        if (account.Identity == 0) return null;
        Entity found = null;
        foreach (var e in session.GetEntitiesOfType<CharacterLifeComponent>())
        {
            if (EngineCodec.IsDead(e) || TeamPermissions.OwnerOf(e) != account.Identity) continue;
            if (e.Get<CharacterLifeComponent>().IsAlive) return e;
            found ??= e;
        }
        return found;
    }

    private List<DetailLine> Character(Session session, HostSession.PlayerAccount account, Entity body, (string Name, string Owner, double Distance)? nearest)
    {
        var lines = new List<DetailLine>();
        if (body == null) lines.Add(new("State", account.Identity == 0 ? "never spawned in this world" : "no character in the world (dead, or waiting to respawn)"));
        else
        {
            var life = body.TryGet<CharacterLifeComponent>();
            lines.Add(new("State", (life?.IsAlive == false ? "dead" : "alive") + (account.Online ? "" : " (their body waits in the world while they are offline)")));
            if (body.TryGet<StatCollectionComponent>() is { } stats)
            {
                _suitStats ??= Singleton<DefinitionManager>.Instance.GetDefinitionsOfType<StatDefinition>().Where(s => SuitStats.Contains(s.DebugNameShort)).OrderBy(s => Array.IndexOf(SuitStats, s.DebugNameShort)).ToArray();
                foreach (var def in _suitStats.Concat(stats._values.Keys.Where(k => !_suitStats.Contains(k))))
                {
                    var stat = stats.GetStat(def);
                    lines.Add(new(def.DebugNameShort, $"{stat.Value:0.#} of {stat.MaxValue:0.#} ({PlayerDetails.Percent(stat.Value - stat.MinValue, stat.MaxValue - stat.MinValue)})"));
                }
            }
            if (body.TryGet<CharacterMovementComponent>() is { } move) lines.Add(new("Movement", move.CurrentMovementState.ToString()));
            if (body.AsInterface<ISeatable>()?.CurrentSeat is { } seat)
                lines.Add(new("Seated in", (seat.Entity.GetComposition()?.DebugNameShort ?? "a seat") + (WorldSocial.GridOf(seat.Entity) is { } grid ? " on '" + GridName(grid) + "'" : "")));
            var position = body.Data.GetWorldTransform().Position;
            lines.Add(new("Position", PlayerDetails.Position(new Vec3D(position.X, position.Y, position.Z)), true));
            lines.Add(new("Where", Where(session, position)));
        }
        if (nearest is { } n) lines.Add(new("Nearest grid", $"'{n.Name}' of {n.Owner}, {n.Distance:N0} m"));
        if (account.Identity != 0)
        {
            var (creative, god) = ModeOf(account.Identity);
            lines.Add(new("Creative tools", creative ? "on" : "off"));
            lines.Add(new("God mode", god ? "on" : "off"));
            lines.Add(new("Access bypass", AccessBypass.IsOn(account.Identity) ? "on" : "off"));
        }
        return lines;
    }

    /// <summary>The planet whose gravity holds a point, with the height above its radius, or space.</summary>
    private static string Where(Session session, Vector3D position)
    {
        foreach (Entity planet in session.GetEntitiesOfType<VoxelPlanetComponent>())
        {
            if (EngineCodec.IsDead(planet)) continue;
            var p = planet.Get<VoxelPlanetComponent>();
            var sphere = p.GetSphereOfInfluence();
            double distance = Vector3D.Distance(in position, in sphere.Center);
            if (distance < sphere.Radius) return $"{SocialState.Clean(p.Name, 64)}, about {distance - p.Radius:N0} m above its radius";
        }
        return "space";
    }

    private static List<DetailLine> Inventory(Entity body, PlayerCreditsData credits)
    {
        var lines = new List<DetailLine> { new("Credits", credits == null ? "none yet" : ((double)credits.Credits).ToString("N0", CultureInfo.InvariantCulture)) };
        if (body == null) { lines.Add(new("Items", "no character to carry any")); return lines; }
        var totals = new Dictionary<ItemDefinition, double>();
        foreach (var inventory in body.Components.OfType<InventoryComponent>())
            foreach (var stack in inventory.Items)
                if (stack.Definition != null) totals[stack.Definition] = totals.GetValueOrDefault(stack.Definition) + (double)stack.Amount;
        lines.Add(new("Item kinds", totals.Count == 0 ? "empty" : totals.Count.ToString(CultureInfo.InvariantCulture)));
        foreach (var (item, amount) in totals.OrderByDescending(t => t.Value).Take(25))
            lines.Add(new(ItemLabel(item), amount.ToString("#,0.##", CultureInfo.InvariantCulture)));
        if (totals.Count > 25) lines.Add(new("And", $"{totals.Count - 25} more kinds"));
        return lines;
    }

    private static string ItemLabel(ItemDefinition item)
    {
        string text = null;
        try { text = item.DisplayName.EvaluateText(); } catch (Exception) { }
        return string.IsNullOrWhiteSpace(text) || text.Contains('{') ? item.DebugNameShort : text;
    }

    private List<DetailLine> Faction(long identity)
    {
        var social = Social;
        if (social == null || identity == 0) return [new("Faction", "none")];
        string name, tag; FactionRole role; int members; long leader;
        lock (social.Sync)
        {
            if (social.State.FactionOf(identity) is not { } f) return [new("Faction", "none")];
            (name, tag, role, members, leader) = (f.Name, f.Tag, f.RoleOf(identity), f.Members.Count, f.Leader);
        }
        return [new("Faction", name), new("Tag", tag), new("Role", role.ToString()), new("Members", members.ToString(CultureInfo.InvariantCulture)), new("Leader", OwnerName(leader))];
    }

    private static List<DetailLine> Pcu(Session session, HostSession host, HostSession.PlayerAccount account)
    {
        var pcu = session.SessionComponents.TryGet<PCUSessionComponent>();
        if (pcu == null || account.Identity == 0) return [new("PCU", pcu == null ? "this world counts no PCU" : "no identity in this world yet")];
        int used = pcu.GetPCU(new IdentityId(account.Identity)), world = pcu.MaxPerPlayerPCU;
        double group = host.IsOwner(account.SteamId) ? -1 : host.Perms.Value(account.SteamId, "limits.pcu");
        long limit = world == int.MaxValue ? long.MaxValue : world;
        if (group >= 0 && group < limit) limit = (long)group;
        return
        [
            new("Used", PlayerDetails.N(used) + " (their PCU pool: a faction counts together)"),
            new("Server limit", world == int.MaxValue ? "unlimited" : PlayerDetails.N(world)),
            new("Group limit", group >= 0 ? PlayerDetails.N((long)group) : "none (the server's)"),
            new("In force", limit == long.MaxValue ? "unlimited" : $"{PlayerDetails.N(limit)} ({PlayerDetails.Percent(used, limit)} used, {PlayerDetails.N(Math.Max(0, limit - used))} left)"),
        ];
    }

    private List<DetailLine> Spawn(Session session, HostSession host, HostSession.PlayerAccount account, RespawnPerPlayerData respawn, LastDeathPerPlayerData death)
    {
        var lines = new List<DetailLine>();
        if (respawn?.HomePoint is { } home && !EngineCodec.IsDead(home))
        {
            var at = home.Data.GetWorldTransform().Position;
            lines.Add(new("Home base", $"{PointName(home)}{(GridNameOf(home) is { Length: > 0 } g ? " on '" + g + "'" : "")}, {Place(session, at)}"));
        }
        else if (host.Access.HomeOf(account.SteamId, WorldKey) is { } lost)
            lines.Add(new("Home base", $"destroyed: it was {lost.Name}{(lost.Grid.Length > 0 ? " on '" + lost.Grid + "'" : "")}"));
        else lines.Add(new("Home base", "none"));
        var medbays = respawn?.KnownPoints.Where(p => p != null && !EngineCodec.IsDead(p)).ToList() ?? new List<Entity>();
        lines.Add(new("Claimed medbays", medbays.Count == 0 ? "none" : $"{medbays.Count}: " + string.Join(", ", medbays.Take(10).Select(m => PointName(m) + (GridNameOf(m) is { Length: > 0 } g ? " on '" + g + "'" : "")))));
        if (death != null && death.LastDeathPoint.Position is var place && place != default)
            lines.Add(new("Last death", PlayerDetails.Position(new Vec3D(place.X, place.Y, place.Z)) + ", " + Place(session, place), true));
        return lines;
    }

    private static string Place(Session session, Vector3D at) => PlaceOf(session, at) is { Length: > 0 } planet ? planet : "space";

    private static List<DetailLine> Progress(PlayerProgressionData progression, DiscoveriesPlayerData discoveries, ContractPerPlayerData contracts, GPSMarkersServerData gps)
    {
        string N(int? count) => count?.ToString(CultureInfo.InvariantCulture) ?? "none yet";
        return
        [
            new("Unlocked rules", N(progression?.UnlockedRules.Count)),
            new("Unlocked blocks", N(progression?.UnlockedBlocks.Count)),
            new("Discoveries", N(discoveries?.Discoveries.Count)),
            new("Contracts", contracts == null ? "none yet" : $"{contracts.AcceptedContracts.Count} accepted, {contracts.CompletedContracts.Count} completed, {contracts.FinishedContracts.Count} finished, {contracts.FailedContracts.Count} failed"),
            new("GPS markers", N(gps?.GPSMarkers.Count)),
        ];
    }

    /// <summary>A GPS marker on a grid for the admin who asked (Entities: Mark, player info's grid list).</summary>
    private string MarkGrid(Session session, ushort clientId, long actor, Entity grid, string name, string owner, Vector3D position)
    {
        if (actor == 0) return "You need a player in this world to get a GPS marker.";
        // Marking reveals where a grid is: without the grid list, only grids of lower ranks (as player info shows them).
        // An asker who left while the request waited has no rank to check against: refused, not let through.
        if (_host is { } gone && clientId != 0 && gone.SteamOfClient(clientId) == 0) return "You are no longer in this world.";
        if (_host is { } host && host.SteamOfClient(clientId) is var me && me != 0 && !host.IsOwner(me) && !host.Allowed(me, "admin.entities.list"))
        {
            long ownerId = TeamPermissions.OwnerOf(grid);
            if (PlayerIdentities.IsHuman(ownerId) && ownerId != actor)
            {
                ulong theirs = host.SteamOfIdentity(ownerId);
                if (theirs == 0) theirs = host.Access.SeenCopy().FirstOrDefault(s => s.Identity == ownerId)?.SteamId ?? 0;
                if (theirs == 0 || host.IsOwner(theirs) || host.Perms.RankOf(theirs) >= host.Perms.RankOf(me)) return "You may mark only grids of players ranked below you.";
            }
        }
        var config = Singleton<DefinitionManager>.Instance.GetConfiguration<GPSMarkersConfiguration>();
        var data = session.Get<IPerPlayerData>().GetPerPlayerData<GPSMarkersServerData>(new IdentityId(actor));
        string label = name.Length > config.NameMaxLength ? name[..config.NameMaxLength] : name;
        if (data.GPSMarkers.Any(m => m.Name == label && Vector3D.Distance(m.Position, position) < 50)) return $"'{name}' is already on your GPS.";
        if (data.GPSMarkers.Count >= MaxSharedTargetMarkers) return "Your GPS is full; delete some markers first.";
        data.GPSMarkers.Add(new GPSMarker(config.PlayerMadeMarkerType, label, "Grid of " + owner + ", marked from the admin panel", position, config.PlayerCreatedMarkerIcon,
            new ColorSRGB((byte)255, (byte)170, (byte)60, (byte)255), alwaysShow: true, hidden: false));
        return $"Marked '{name}' on your GPS.";
    }
}
