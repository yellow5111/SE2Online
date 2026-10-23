#if !PULSAR
using System;
using System.Linq;
using Keen.Game2.Simulation.GameSystems.Ownership;
using Keen.Game2.Simulation.WorldObjects.Characters;
using Keen.Game2.Simulation.WorldObjects.Tools.Voxels;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Voxels.Components;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;
using Y51_SE2Online.SyncEngine;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>
/// F13 rig (-y51online:selftest-boulder[:name], server or co-op host): 40 s after the first player character appears on a
/// planet, the server puts it 1.5 m from the surface of the nearest entity whose composition name holds "Boulder" (or the
/// given text), facing it, and logs what the boulder is (drill kind, voxel or flora, network id). Every game then logs, every
/// 20 s, whether it holds that boulder and what its drill would call it, so a drill test can start at once (pair with
/// -y51online:drillprobe). "selftest: boulder: ..." lines.
/// </summary>
internal static class BoulderProbe
{
	private static string? Wanted => MpPlugin.LaunchArgs.FirstOrDefault(a => a.StartsWith(MpOptions.Prefix + "selftest-boulder", StringComparison.OrdinalIgnoreCase)) is { } arg
		? arg.Length > (MpOptions.Prefix + "selftest-boulder:").Length ? arg[(MpOptions.Prefix + "selftest-boulder:").Length..] : "Boulder" : null;

	private static long _seenMs, _logMs;
	private static bool _placed;
	private static Vector3D _at;
	private static Entity? _me;

	public static void Tick(Session session)
	{
		if (Wanted is not { } wanted) return;
		long now = MpClock.NowMs();
		try
		{
			bool server = ReferenceEquals(session, MpPlugin.App?.ServerSession);
			if (server && !_placed)
			{
				var me = session.GetEntitiesOfType<CharacterLifeComponent>().FirstOrDefault(c => PlayerIdentities.IsHuman(TeamPermissions.OwnerOf(c)));
				if (me == null) return;
				if (_seenMs == 0) { _seenMs = now; return; }
				if (now - _seenMs < 40_000) return;
				_placed = true;
				_me = me;
				Place(session, me, wanted);
				return;
			}
			if (now < _logMs) return;
			_logMs = now + 20_000;
			var self = server ? _me : session.SessionComponents.Get<Keen.Game2.Client.GameSystems.PlayerControl.ClientPlayersSessionComponent>().LocalPlayerController?.ControlledEntities
				.Select(c => (c.Controllable as Component)?.Entity).FirstOrDefault(e => e?.TryGet<CharacterLifeComponent>() != null);
			if (self == null) return;
			var at = self.Data.GetWorldTransform().Position;
			var all = Matching(session, wanted).ToList();
			var near = all.Select(e => (Entity: e, Distance: Vector3D.Distance(Centre(e), at))).OrderBy(e => e.Distance).Take(4).ToList();
			int networked = server ? 0 : all.Count(e => ClientWorld.Current?.NetIdOf(e) != null);
			GameLog.Instance.Info($"selftest: boulder: {(server ? "server" : "player")} holds {all.Count} '{wanted}' entities{(server ? "" : $", {networked} of them from the server")}; nearest to {self}: " +
				string.Join("; ", near.Select(n => $"{n.Entity} {n.Entity.GetComposition()?.DebugNameShort} centre {Round(Centre(n.Entity))} {n.Distance:0} m{(server ? "" : $", network id {ClientWorld.Current?.NetIdOf(n.Entity)?.ToString() ?? "none (this game's own copy)"}")}")));
		}
		catch (Exception ex) { GameLog.Instance.WarnOnce("boulder-probe", "selftest: boulder: FAIL " + ex); }
	}

	private static System.Collections.Generic.IEnumerable<Entity> Matching(Session session, string wanted)
		=> session.Entities.Where(e => e.GetComposition()?.DebugNameShort?.Contains(wanted, StringComparison.OrdinalIgnoreCase) == true);

	private static string Round(Vector3D v) => $"{v.X:0}, {v.Y:0}, {v.Z:0}";

	private static Vector3D Centre(Entity e) => WorldTransform.Transform((Vector3D)BoundingBoxData.Get(e).Center, e.Data.GetWorldTransform());

	private static void Place(Session session, Entity me, string wanted)
	{
		var from = me.Data.GetWorldTransform().Position;
		var boulder = Matching(session, wanted).OrderBy(e => Vector3D.Distance(Centre(e), from)).FirstOrDefault();
		if (boulder == null) { GameLog.Instance.Info($"selftest: boulder: FAIL the server holds no '{wanted}' entity"); return; }
		var planet = session.GetEntitiesOfType<VoxelPlanetComponent>().OrderBy(p => Vector3D.Distance(p.Data.GetWorldTransform().Position, from)).FirstOrDefault();
		var centre = Centre(boulder);
		Vector3 up = planet == null ? Vector3.Up : (Vector3)Vector3D.Normalize(centre - planet.Data.GetWorldTransform().Position);
		// Beside the boulder on the ground, out from its centre along the old line to the player, flattened to the horizon.
		Vector3D side = Vector3D.Normalize(from - centre);
		side -= Vector3D.Dot(side, (Vector3D)up) * (Vector3D)up;
		if (side.LengthSquared() < 1e-6) side = (Vector3D)Vector3.CalculatePerpendicularVector(up);
		side = Vector3D.Normalize(side);
		// Boulder64_...: a 64 m boulder (its bounding box is the voxel storage, larger than the rock).
		var size = System.Text.RegularExpressions.Regex.Match(boulder.GetComposition()?.DebugNameShort ?? "", @"(\d+)_");
		float radius = size.Success ? float.Parse(size.Groups[1].Value) / 2 : BoundingBoxData.Get(boulder).Extents.Length();
		_at = centre + side * (radius + 1.5);
		me.Data.Teleport(new WorldTransform(_at, Quaternion.CreateFromForwardUp((Vector3)(-side), up)));
		GameLog.Instance.Info($"selftest: boulder: put {me} {radius + 1.5:0.0} m from the centre of {boulder} ({boulder.GetComposition()?.DebugNameShort}, drill kind {HandDrillContactResolver.GetEntityKind(boulder)}, " +
			$"voxel {boulder.TryGet<VoxelStorageComponentBase>() != null}, procedural {boulder.Data.Has<Keen.VRage.Core.Game.GameSystems.ProceduralGeneration.ProcedurallyGeneratedTag>()}, planet entity {boulder.TryGet<PlanetEntityComponent>() != null}, {Vector3D.Distance(centre, from):0} m from where the player was), facing it");
	}
}
#endif
