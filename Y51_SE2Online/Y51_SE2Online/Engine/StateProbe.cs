#if !PULSAR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Keen.Game2.Simulation.WorldObjects.Characters;
using Keen.Game2.Simulation.WorldObjects.Characters.Stats;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.Lcd;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.ResourceDistribution.Resources;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.Weapons;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Multiplayer.Replications;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;
using Y51_SE2Online.SyncEngine;

namespace Y51_SE2Online.Engine;

/// <summary>
/// Test rig only. -y51online:stateprobe: every 3 s each game logs state the server sends as repeated signals (character
/// stats, turret aim, container mass, LCD surfaces) for what is within 600 m of the player, "stateprobe S|C ms kind netId
/// value" lines for stateprobe.py. -y51online:selftest-trip (server): 60 s after the first player has a character it is
/// teleported 2 km away (out of a 1 km streaming radius) and 40 s later back, so the player's game drops and gets back
/// everything around the start.
/// </summary>
internal static class StateProbe
{
	private static bool Requested => MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "stateprobe");
	private static bool Trip => MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "selftest-trip");
	private const double Range = 600;

	private static long _serverNextMs, _clientNextMs, _tripAtMs;
	private static int _tripStep;
	private static WorldTransform _home;
	private static Entity? _traveller;

	public static void Tick(Session session, bool server)
	{
		if (server && Trip) TripTick(session);
		if (server && Hurt) HurtTick(session);
		if (server && GoTo != null) GoToTick(session);
		if (!Requested && !PropProbe.Requested && !ObProbe.Requested) return;
		var mapper = OwnHost.Existing(session)?.Ids;
		if (mapper == null) return;
		long now = MpClock.NowMs();
		ref long next = ref server ? ref _serverNextMs : ref _clientNextMs;
		if (now < next) return;
		next = now + 3000;
		var around = References(session, server);
		if (around.Count == 0) return;
		PropProbe.Log(session, server, around, e => mapper.TryGetId(e)?.Value);
		ObProbe.Log(session, server, around, e => mapper.TryGetId(e)?.Value);
		if (!Requested) return;
		var sb = new StringBuilder();
		string side = server ? "S" : "C";
		void Add(Entity e, string kind, string value)
		{
			if (mapper.TryGetId(e) is not { } id || PositionOf(e) is not { } p || around.All(at => Vector3D.Distance(p, at) > Range)) return;
			sb.Append($"stateprobe {side} {now} {kind} {id.Value} {value}\n");
		}
		foreach (Entity e in session.GetEntitiesOfType<StatCollectionComponent>())
			if (e.TryGet<StatCollectionComponent>() is { } stats)
				Add(e, "stats", string.Join(";", stats._values.OrderBy(kv => kv.Key.ToString()).Select(kv => $"{Short(kv.Key)}={kv.Value:0.00}")));
		foreach (Entity e in session.GetEntitiesOfType<AutoTargetingComponent>())
			if (e.TryGet<AutoTargetingComponent>() is { } turret)
				Add(e, "aim", $"{turret._currentAzimuth:0.000},{turret._currentElevation:0.000}");
		foreach (Entity e in session.GetEntitiesOfType<ResourceContainerComponent>())
			if (e.TryGet<ResourceContainerComponent>() is { } container)
				Add(e, "mass", $"{container.DynamicMass:0.0}");
		foreach (Entity e in session.GetEntitiesOfType<LcdMultiPanelComponent>())
			if (e.TryGet<LcdMultiPanelComponent>() is { } lcd)
				Add(e, "lcd", string.Join("|", (lcd.SurfaceStates ?? Array.Empty<LcdPanelSurfaceState>()).Select(s => $"{s.CurrentImageIndex},{s.FontSize:0.0},{s.Padding:0.0},{s.BackgroundColor},{s.TextColor},{s.Images?.Length ?? 0}")));
		foreach (Entity e in session.GetEntitiesOfType<Keen.Game2.Simulation.WorldObjects.CubeBlocks.PowerableBlockComponent>())
			if (e.TryGet<Keen.Game2.Simulation.WorldObjects.CubeBlocks.PowerableBlockComponent>() is { } powered)
				Add(e, "on", powered.Enabled ? "1" : "0");
		void Inventory(Entity e)
		{
			if (e.FirstOrDefault<Keen.Game2.Simulation.WorldObjects.Items.InventoryComponent>() is not { } inventory) return;
			int stacks = 0;
			double amount = 0;
			foreach (var stack in inventory.Items) { stacks++; amount += (double)stack.Amount; }
			Add(e, "inv", $"{stacks},{amount:0.##},{inventory.DynamicMass:0.#}");
		}
		foreach (Entity c in session.GetEntitiesOfType<CharacterLifeComponent>()) Inventory(c);
		foreach (Entity grid in session.GetEntitiesOfType<Keen.Game2.Simulation.WorldObjects.CubeGrids.CubeGridComponent>())
		{
			if (PositionOf(grid) is not { } g || around.All(at => Vector3D.Distance(g, at) > Range)) continue;
			grid.Get<Keen.Game2.Simulation.WorldObjects.CubeGrids.CubeGridComponent>().VisitAllBlocksWithComponent((Keen.Game2.Simulation.WorldObjects.CubeBlocks.CubeBlockComponent b) => Inventory(b.Entity));
		}
		if (sb.Length > 0) GameLog.Instance.Info(sb.ToString().TrimEnd('\n'));
	}

	private static string Short(object definition) => definition.ToString()!.Split('\\', '/').Last();

	private static Vector3D? PositionOf(Entity e)
	{
		if (e.Data.TryGet<WorldTransform>(out var t)) return t.Position;
		var root = e.GetTopLevelParent();
		return root.Data.TryGet<WorldTransform>(out var r) ? r.Position : null;
	}

	// The server measures around every joined player's character (and where the trip started), a player's game around its own.
	private static List<Vector3D> References(Session session, bool server)
	{
		var around = new List<Vector3D>();
		long me = server ? 0 : (long)(session.SessionComponents.TryGet<Keen.Game2.Simulation.GameSystems.Identities.IdentitiesSessionComponent>()?.LocalIdentity ?? default);
		foreach (Entity c in session.GetEntitiesOfType<CharacterLifeComponent>())
			if (c.Get<CharacterLifeComponent>().IsAlive && (server ? PlayerIdentities.IsHuman(TeamPermissions.OwnerOf(c)) : TeamPermissions.OwnerOf(c) == me))
				around.Add(c.Data.GetWorldTransform().Position);
		if (server && _tripStep == 1) around.Add(_home.Position);
		return around;
	}

	private static bool Hurt => MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "selftest-hurt");
	private static long _hurtAtMs;

	// Test rig (-y51online:selftest-hurt, server): 50 s after the first player has a character, the server sets every player
	// character's health to 37 and its other stats (oxygen, energy, hydrogen, food) to half, creative or not, so players that
	// join or rejoin later must see those, not full bars.
	private static void HurtTick(Session session)
	{
		if (_hurtAtMs < 0) return;
		// Characters of players that are online now (a world keeps its offline players' bodies).
		var bodies = (Y51_SE2Online.SyncEngine.HostWorld.Current?.JoinedCharacters ?? new List<Entity>()).Where(c => !EngineCodec.IsDead(c) && c.Get<CharacterLifeComponent>().IsAlive).ToList();
		if (bodies.Count == 0) return;
		long now = MpClock.NowMs();
		if (_hurtAtMs == 0) { _hurtAtMs = now + 50_000; GameLog.Instance.Info($"selftest: hurt: {bodies.Count} player character(s); hurting them in 50 s"); return; }
		if (now < _hurtAtMs) return;
		_hurtAtMs = -1;
		foreach (var body in bodies)
		{
			var life = body.Get<CharacterLifeComponent>();
			var stats = body.Get<StatCollectionComponent>();
			// The collection holds only stats that left their default, so health is set by its definition.
			foreach (var stat in stats._values.Keys.Where(k => k != life._definition.HealthStat).ToList())
				stats.TrySetValue(stat, stats.GetValue(stat) / 2, ignoreCreative: true);
			stats.TrySetValue(life._definition.HealthStat, 37, ignoreCreative: true);
			GameLog.Instance.Info($"selftest: hurt: {body} of identity {TeamPermissions.OwnerOf(body)} set to health 37 (now {life.Health}), stats now {string.Join(";", stats._values.Select(kv => $"{Short(kv.Key)}={kv.Value:0.00}"))}");
		}
	}

	// Test rig (-y51online:selftest-goto:<x>:<y>:<z>, server): 60 s after the first player has a character, that character (or
	// the grid it sits in) is moved to the point and stays: a world area there spawns for it.
	private static readonly string? GoTo = MpPlugin.LaunchArgs.FirstOrDefault(a => a.StartsWith(MpOptions.Prefix + "selftest-goto:", StringComparison.Ordinal));
	private static long _goToAtMs, _goToLogMs;

	private static void GoToTick(Session session)
	{
		long now = MpClock.NowMs();
		if (_goToAtMs < 0)
		{
			if (now < _goToLogMs) return;
			_goToLogMs = now + 10_000;
			GameLog.Instance.Info($"selftest: goto: {session.GetEntitiesOfType<Keen.Game2.Simulation.WorldObjects.CubeGrids.CubeGridComponent>().Count()} grids; trash removal held back {KeenNetcode.FreshGridGuard.Protected} fresh grid checks");
			return;
		}
		var bodies = (Y51_SE2Online.SyncEngine.HostWorld.Current?.JoinedCharacters ?? new List<Entity>()).Where(c => !EngineCodec.IsDead(c)).ToList();
		if (bodies.Count == 0) return;
		if (_goToAtMs == 0) { _goToAtMs = now + 60_000; return; }
		if (now < _goToAtMs) return;
		_goToAtMs = -1;
		var w = GoTo!.Substring((MpOptions.Prefix + "selftest-goto:").Length).Split(':').Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
		var mover = bodies[0].GetTopLevelParent();
		var from = mover.Data.GetWorldTransform();
		mover.Data.Teleport(new WorldTransform(new Vector3D(w[0], w[1], w[2]), from.Orientation));
		if (mover.Data.Has<Keen.VRage.Physics.Data.RigidBodyData>()) mover.Data.Set<Keen.VRage.Physics.Data.RigidBodyData>(default);
		GameLog.Instance.Info($"selftest: goto: moved {mover} from {from.Position.X:0}, {from.Position.Y:0}, {from.Position.Z:0} to {w[0]:0}, {w[1]:0}, {w[2]:0}; {session.GetEntitiesOfType<Keen.Game2.Simulation.WorldObjects.CubeGrids.CubeGridComponent>().Count()} grids");
	}

	private static void TripTick(Session session)
	{
		long now = MpClock.NowMs();
		if (_tripStep >= 2) return;
		if (_traveller == null)
		{
			var players = Y51Replication.ServerTransports.Select(t => (long)KeenNetcode.ServerAccess.IdentityOf(t.PeerClientId)).Where(i => i != 0).ToHashSet();
			foreach (Entity c in session.GetEntitiesOfType<CharacterLifeComponent>())
				if (c.Get<CharacterLifeComponent>().IsAlive && players.Contains(TeamPermissions.OwnerOf(c))) { _traveller = c; break; }
			if (_traveller == null) return;
			_tripAtMs = now + 60_000;
			GameLog.Instance.Info($"selftest: trip: {_traveller} of identity {TeamPermissions.OwnerOf(_traveller)} goes in 60 s");
			return;
		}
		if (now < _tripAtMs) return;
		if (EngineCodec.IsDead(_traveller)) { GameLog.Instance.Info("selftest: trip: the traveller is gone; no trip"); _tripStep = 2; return; }
		var data = _traveller.Data;
		if (_tripStep == 0)
		{
			_home = data.GetWorldTransform();
			data.Teleport(new WorldTransform(_home.Position + new Vector3D(0, 2000, 0), _home.Orientation));
			GameLog.Instance.Info($"selftest: trip: the player's character went 2 km away from {_home.Position.X:0}, {_home.Position.Y:0}, {_home.Position.Z:0}");
			_tripStep = 1;
			_tripAtMs = now + 40_000;
		}
		else
		{
			data.Teleport(_home);
			GameLog.Instance.Info("selftest: trip: the player's character is back");
			_tripStep = 2;
		}
		if (data.Has<Keen.VRage.Physics.Data.RigidBodyData>()) data.Set<Keen.VRage.Physics.Data.RigidBodyData>(default);
	}
}
#endif
