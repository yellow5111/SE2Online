#if !PULSAR
using System;
using System.Linq;
using Keen.Game2.Game.SessionComponents;
using Keen.Game2.Simulation.GameSystems.Blueprints;
using Keen.Game2.Simulation.Replication;
using Keen.Game2.Simulation.RuntimeSystems.Saves;
using Keen.Game2.Simulation.WorldObjects.Characters;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.Game2.Simulation.WorldObjects.Items;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.Core.Game.Definitions;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.Components;
using Keen.VRage.DCS.ObjectBuilders;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.Library.Definitions;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Library.Utils;
using Keen.VRage.Multiplayer.NetworkStories;
using Y51_SE2Online.Core;
using Y51_SE2Online.SyncEngine;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>
/// Test rig (-y51online:selftest-access, a non-admin player): 45 s after joining, sends the requests the server now holds
/// to what the game's client sends, and logs what came back: an armor block spawned from a player's builder (refused), two
/// autosaves in a row (first runs, second refused), a save of the server's world (refused), a blueprint of a grid the
/// player owns (runs) and of one it does not (refused), and "deposit all" from another player's character, into an
/// inventory out of reach and into the nearest one (the server log says which it refused). "selftest: access: ..." lines.
/// </summary>
internal static class AccessProbe
{
	private static long _atMs;
	private static bool _done;

	public static void ClientTick(Session session)
	{
		if (_done || !MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "selftest-access")) return;
		long now = MpClock.NowMs();
		if (_atMs == 0) { _atMs = now + 45_000; return; }
		if (now < _atMs) return;
		_done = true;
		Run(session);
	}

	private static async System.Threading.Tasks.Task<(bool Ran, string Text)> Outcome(NetworkStory story)
	{
		var waiting = story.Task.GetAwaiter();
		for (int i = 0; i < 50 && !waiting.IsCompleted; i++) await Keen.VRage.Library.Threading.Task.Delay(TimeSpan.FromMilliseconds(200));
		if (!waiting.IsCompleted) return (false, "no answer within 10 s");
		try { waiting.GetResult(); return (true, "ran"); }
		catch (Exception ex) { return (false, "refused (" + ex.GetBaseException().Message.Split('\n')[0] + ")"); }
	}

	private static Entity? Me(Session session) => session.SessionComponents.Get<Keen.Game2.Client.GameSystems.PlayerControl.ClientPlayersSessionComponent>().LocalPlayerController?.ControlledEntities
		.Select(c => (c.Controllable as Component)?.Entity).FirstOrDefault(e => e?.TryGet<CharacterLifeComponent>() != null);

	private static void Expect(string what, (bool Ran, string Text) outcome, bool run)
		=> GameLog.Instance.Info($"selftest: access: {what}: {(outcome.Ran == run ? "PASS" : "FAIL")} {outcome.Text} (expected {(run ? "to run" : "a refusal")})");

	private static async void Run(Session session)
	{
		try
		{
			if (session.SessionComponents.TryGet<InProcessGameServerSessionComponent>() is not { } server) { GameLog.Instance.Info("selftest: access: FAIL no game server component"); return; }
			for (int i = 0; i < 60 && Me(session) == null; i++) await Keen.VRage.Library.Threading.Task.Delay(TimeSpan.FromSeconds(2));
			var me = Me(session);
			long own = me == null ? 0 : TeamPermissions.OwnerOf(me);
			var at = me?.Data.GetWorldTransform().Position ?? default;
			GameLog.Instance.Info($"selftest: access: running as identity {own}");

			Singleton<DefinitionManager>.Instance.TryGetDefinition(new Guid("2eacbbf2-d8fb-4a78-91dc-7b492517ef97"), out PrefabDefinition? armor);
			var block = armor!.Get();
			Expect("spawning an armor block from a player's builder", await Outcome(server.RequestSpawnEntity(block)), run: false);

			Expect("first autosave", await Outcome(SaveSessionComponent.RequestAutosaveAsync(session)), run: true);
			Expect("second autosave right after", await Outcome(SaveSessionComponent.RequestAutosaveAsync(session)), run: false);
			Expect("saving the server's world", await Outcome(server.TrySaveGame("access probe")), run: false);

			var grids = session.GetEntitiesOfType<CubeGridComponent>().OrderBy(g => Vector3D.Distance(g.Data.GetWorldTransform().Position, at)).ToList();
			if (grids.FirstOrDefault(g => TeamPermissions.OwnerOf(g) == own) is { } mine)
				Expect($"blueprint of own grid {mine}", await Outcome(BlueprintHelpers.SerializeGrid(mine.Get<CubeGridComponent>(), null!)), run: true);
			else GameLog.Instance.Info("selftest: access: no own grid for the allowed blueprint case");
			// The server allows what the game's copy tool allows (build access to the grid): a team-shared grid copies.
			var permissions = session.SessionComponents.TryGet<Keen.Game2.Simulation.GameSystems.Permissions.PermissionsSessionComponent>();
			if (grids.FirstOrDefault(g => TeamPermissions.OwnerOf(g) is long o && o != own && Sync.PlayerIdentities.IsHuman(o)) is { } theirs)
			{
				bool may = permissions?.CanCopy(theirs, new Keen.Game2.Simulation.GameSystems.Ownership.IdentityId(own)) ?? false;
				Expect($"blueprint of another player's grid {theirs} ({(may ? "shared with this player" : "not shared")})", await Outcome(BlueprintHelpers.SerializeGrid(theirs.Get<CubeGridComponent>(), null!)), run: may);
			}
			else GameLog.Instance.Info("selftest: access: no other player's grid for the blueprint case");

			// Inventories live on the server; a player's game knows the blocks that hold them (cargo containers).
			var inventories = session.GetEntitiesOfType<CubeBlockComponent>().Where(e => e.GetComposition()?.DebugNameShort?.Contains("Container", StringComparison.OrdinalIgnoreCase) == true)
				.OrderBy(e => Vector3D.Distance(e.Data.GetWorldTransform().Position, at)).ToList();
			var other = session.GetEntitiesOfType<CharacterLifeComponent>().FirstOrDefault(c => c != me && TeamPermissions.OwnerOf(c) is long o && o != own && Sync.PlayerIdentities.IsHuman(o));
			if (me != null && inventories.Count > 0)
			{
				var near = inventories[0];
				var far = inventories.LastOrDefault(e => Vector3D.Distance(e.Data.GetWorldTransform().Position, at) > 30);
				if (other != null) { server.TryDepositAllNonEssentialItems(other, near); GameLog.Instance.Info($"selftest: access: sent deposit all from another player's character into {near} (expect a server refusal)"); }
				if (far != null) { server.TryDepositAllNonEssentialItems(me, far); GameLog.Instance.Info($"selftest: access: sent deposit all into {far} {Vector3D.Distance(far.Data.GetWorldTransform().Position, at):0} m away (expect a server refusal)"); }
				server.TryDepositAllNonEssentialItems(me, near);
				GameLog.Instance.Info($"selftest: access: sent deposit all into the nearest inventory {near} {Vector3D.Distance(near.Data.GetWorldTransform().Position, at):0.0} m away (expect it to run if the player may use it and stands within reach)");
			}
			else GameLog.Instance.Info("selftest: access: no inventory block for the deposit cases");
			// Seats: sitting in another player's cockpit follows the seat's use access (a team-shared one seats), getting out always works.
			var seat = session.GetEntitiesOfType<Keen.Game2.Simulation.WorldObjects.CubeBlocks.Pilotable.SeatComponent>()
				.Where(e => TeamPermissions.OwnerOf(e) is long o && o != own && Sync.PlayerIdentities.IsHuman(o) && e.Get<Keen.Game2.Simulation.WorldObjects.CubeBlocks.Pilotable.SeatComponent>().Pilot == null)
				.Where(e => Vector3D.Distance(e.Data.GetWorldTransform().Position, at) < 150)
				.OrderBy(e => Vector3D.Distance(e.Data.GetWorldTransform().Position, at)).FirstOrDefault();
			if (me != null && seat != null)
			{
				var component = seat.Get<Keen.Game2.Simulation.WorldObjects.CubeBlocks.Pilotable.SeatComponent>();
				// Block access lives on the server; a player's game knows only whether the grid builds for it (team sharing).
				bool canSit = permissions?.CanCopy(seat.Get<CubeBlockComponent>().Grid.Entity, new Keen.Game2.Simulation.GameSystems.Ownership.IdentityId(own)) ?? true;
				bool sat;
				try { sat = await component.TrySetPilotAsync(me); } catch (Exception ex) { sat = false; GameLog.Instance.Info("selftest: access: seat request faulted: " + ex.GetBaseException().Message.Split('\n')[0]); }
				GameLog.Instance.Info($"selftest: access: sitting in another player's {seat.GetComposition()?.DebugNameShort} ({(canSit ? "team-shared" : "not shared to build; the server decides by its use access")}): {(canSit ? (sat ? "PASS" : "FAIL") : "INFO")} seated {sat}");
				if (sat)
				{
					await Keen.VRage.Library.Threading.Task.Delay(TimeSpan.FromSeconds(2));
					bool left = await component.TryClearPilotAsync();
					GameLog.Instance.Info($"selftest: access: getting out again: {(left ? "PASS" : "FAIL")} left {left}");
				}
			}
			else GameLog.Instance.Info("selftest: access: no free seat of another player for the seat cases");
			GameLog.Instance.Info("selftest: access: done");
		}
		catch (Exception ex) { GameLog.Instance.Info("selftest: access: FAIL " + ex.GetType().Name + ": " + ex.Message); }
	}
}
#endif
