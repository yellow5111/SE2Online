#if !PULSAR
using System;
using System.Collections.Generic;
using System.Linq;
using Keen.Game2.Simulation.GameSystems.Encounters;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.Pilotable;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.Game2.Simulation.WorldObjects.CubeGrids.Physicss;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Physics.Data;
using Keen.VRage.Voxels.Components;
using Y51_SE2Online.Core;
using Y51_SE2Online.SyncEngine;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>
/// Test rig (-y51online:selftest-cruise[:m/s], server): 40 s after the first player has a character, a space respawn ship
/// is spawned for it, the player is seated in its cockpit and the ship flies at the given speed (300, the game's limit)
/// out of the nearest planet's gravity and on to the nearest other planet, stopping 5 km above it. The ship keeps flying
/// with its pilot gone, so a player who rejoins comes back into a ship at full speed (F11: a long max-speed flight between
/// planets crashed the player's game and the player could not rejoin).
/// </summary>
internal static class CruiseProbe
{
	// A property: a static field could be read before the launch arguments are set.
	private static double Speed => MpPlugin.LaunchArgs.Where(a => a.StartsWith(MpOptions.Prefix + "selftest-cruise", StringComparison.Ordinal))
		.Select(a => a.Length > MpOptions.Prefix.Length + 16 && double.TryParse(a[(MpOptions.Prefix.Length + 16)..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : 300)
		.DefaultIfEmpty(0).First();

	// -y51online:selftest-cruise-far:<km>: the far planet (over 1000 km away) is the target instead, and the seated ship is
	// first put that far above it on the line from the home planet, so the flight ends a 4000 km trip as a player's would.
	private static double FarKm => MpPlugin.LaunchArgs.Where(a => a.StartsWith(MpOptions.Prefix + "selftest-cruise-far:", StringComparison.Ordinal))
		.Select(a => double.TryParse(a[(MpOptions.Prefix.Length + 20)..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : 0)
		.DefaultIfEmpty(0).First();

	/// <summary>-y51online:selftest-cruise-host: a listen host's own player flies too (no network, the closest to single player).</summary>
	internal static bool HostToo => MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "selftest-cruise-host");

	private static int _step;
	private static long _atMs, _logMs;
	private static Entity? _ship, _target, _home;
	private static volatile Entity[]? _roots;
	private static bool _spawning;

	public static void ServerTick(Session session, IEnumerable<(ushort ClientId, Entity? Character)> players)
	{
		if (Speed <= 0) return;
		long now = MpClock.NowMs();
		var (clientId, character) = players.FirstOrDefault(p => p.Character != null && !EngineCodec.IsDead(p.Character));
		try
		{
			switch (_step)
			{
				case 0:
					if (character == null) return;
					_atMs = now + 40_000;
					_step = 1;
					var at = character.Data.GetWorldTransform().Position;
					foreach (Entity p in session.GetEntitiesOfType<VoxelPlanetComponent>())
						GameLog.Instance.Info($"selftest: cruise: planet {p} radius {p.Get<VoxelPlanetComponent>().Radius:0} m, {Vector3D.Distance(p.Data.GetWorldTransform().Position, at) / 1000:0} km from the player");
					return;
				case 1:
					if (now < _atMs || character == null || _spawning) return;
					_spawning = true;
					Spawn(session, character.Data.GetWorldTransform().Position);
					_step = 2;
					return;
				case 2:
					if (_roots is not { } roots || character == null) return;
					var grids = HostWorld.ReleaseEncounter(session, roots).Where(e => e.TryGet<CubeGridComponent>() != null).ToList();
					_ship = grids.FirstOrDefault(g => g.TryGet<GridPhysicsComponent>() != null);
					var seat = HostWorld.FindCockpit(grids);
					if (_ship == null || seat == null) { GameLog.Instance.Warn("selftest: cruise: the ship has no free cockpit; no cruise"); _step = 9; return; }
					HostWorld.Seat(seat, character);
					GameLog.Instance.Info($"selftest: cruise: ClientId {clientId} seated in {_ship.Get<CubeGridComponent>().DisplayName} ({grids.Count} grids); flying in 10 s");
					_atMs = now + 10_000;
					_step = 3;
					return;
				case 3:
					if (now < _atMs) return;
					if (_ship == null || EngineCodec.IsDead(_ship)) { GameLog.Instance.Warn("selftest: cruise: the ship is gone"); _step = 9; return; }
					var from = _ship.Data.GetWorldTransform().Position;
					var planets = session.GetEntitiesOfType<VoxelPlanetComponent>().Where(p => !EngineCodec.IsDead(p)).OrderBy(p => Vector3D.Distance(p.Data.GetWorldTransform().Position, from)).ToList();
					if (planets.Count < 2) { GameLog.Instance.Warn("selftest: cruise: fewer than two planets"); _step = 9; return; }
					_home = planets[0];
					_target = FarKm > 0 ? planets.FirstOrDefault(p => Vector3D.Distance(p.Data.GetWorldTransform().Position, from) > 1_000_000) ?? planets[1] : planets[1];
					_ship.Get<GridPhysicsComponent>().SetGridMotion(false);
					if (FarKm > 0)
					{
						var targetCentre = _target.Data.GetWorldTransform().Position;
						var put = targetCentre + Vector3D.Normalize(_home.Data.GetWorldTransform().Position - targetCentre) * (_target.Get<VoxelPlanetComponent>().Radius + FarKm * 1000);
						_ship.Data.Teleport(new WorldTransform(put, _ship.Data.GetWorldTransform().Orientation));
						GameLog.Instance.Info($"selftest: cruise: ship put {FarKm:0} km above {_target}, {Vector3D.Distance(put, _home.Data.GetWorldTransform().Position) / 1000:0} km from {_home}");
						from = put;
					}
					GameLog.Instance.Info($"selftest: cruise: flying from {_home} to {_target} ({Vector3D.Distance(_target.Data.GetWorldTransform().Position, from) / 1000:0} km) at {Speed:0} m/s");
					_step = 4;
					return;
				case 4:
					Fly(now, clientId, character);
					return;
			}
		}
		catch (Exception ex)
		{
			GameLog.Instance.Warn("selftest: cruise: " + ex);
			_step = 9;
		}
	}

	private static void Fly(long now, ushort clientId, Entity? character)
	{
		if (_ship == null || EngineCodec.IsDead(_ship) || _target == null || _home == null) { GameLog.Instance.Warn("selftest: cruise: the ship is gone"); _step = 9; return; }
		var at = _ship.Data.GetWorldTransform().Position;
		var home = _home.Get<VoxelPlanetComponent>().GetSphereOfInfluence();
		var target = _target.Get<VoxelPlanetComponent>();
		var targetAt = _target.Data.GetWorldTransform().Position;
		double toTarget = Vector3D.Distance(at, targetAt) - target.Radius;
		// Out of the home planet's gravity first (a straight line could run through it), then at the target.
		var direction = Vector3D.Distance(at, home.Center) < home.Radius + 10_000 && toTarget > Vector3D.Distance(at, home.Center)
			? Vector3D.Normalize(at - home.Center) : Vector3D.Normalize(targetAt - at);
		double speed = toTarget < 5_000 ? 0 : Math.Min(Speed, Math.Max(20, (toTarget - 5_000) / 2));
		_ship.Data.Set(new RigidBodyData((Vector3)(direction * speed), Vector3.Zero));
		if (now < _logMs) return;
		_logMs = now + 10_000;
		bool seated = character?.AsInterface<ISeatable>()?.CurrentSeat != null;
		var body = _ship.Data.TryGet<RigidBodyData>(out var b) ? b.LinearVelocity.Length() : 0;
		GameLog.Instance.Info($"selftest: cruise: at {at.X:0}, {at.Y:0}, {at.Z:0}, {body:0} m/s, {toTarget / 1000:0.0} km above the target, {Vector3D.Distance(at, home.Center) / 1000:0} km from the home centre; ClientId {clientId} {(character == null ? "has no character" : seated ? "seated" : "not seated")}");
	}

	private static async void Spawn(Session session, Vector3D near)
	{
		try
		{
			var encounters = session.SessionComponents.TryGet<EncounterSessionComponent>();
			var ship = Y51_SE2Online.Sync.RespawnShips.Catalog.FirstOrDefault(s => s.Space);
			if (encounters == null || ship == null || !Keen.VRage.Library.Utils.Singleton<Keen.VRage.Library.Definitions.DefinitionManager>.Instance.TryGetDefinition(ship.Prefab, out Keen.VRage.Core.Game.Definitions.PrefabDefinition prefab))
			{
				GameLog.Instance.Warn("selftest: cruise: no space respawn ship");
				return;
			}
			var spawn = session.SessionComponents.TryGet<Keen.Game2.Simulation.GameSystems.Respawn.RespawnSessionComponent>()?.WorldSpawn ?? WorldTransform.Identity;
			_roots = await HostWorld.SpawnEncounter(session, encounters, prefab, new WorldTransform(HostWorld.OpenSpace(session, near), spawn.Orientation));
			GameLog.Instance.Info($"selftest: cruise: spawned {ship.Name}");
		}
		catch (Exception ex) { GameLog.Instance.Warn("selftest: cruise: spawning the ship failed: " + ex.Message); }
	}
}
#endif
