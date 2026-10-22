#if !PULSAR
using System;
using System.Collections.Generic;
using System.Linq;
using Keen.Game2.Client.GameSystems.PlayerControl;
using Keen.Game2.Simulation.GameSystems.Contracts;
using Keen.Game2.Simulation.GameSystems.Missions;
using Keen.Game2.Simulation.StreamedUI.Contracts;
using Keen.Game2.Simulation.WorldObjects.Characters;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.Components;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;
using Y51_SE2Online.SyncEngine;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>
/// Test rig only (`-y51online:selftest-contracts`): a player's game reports which mission and contract components it has,
/// opens the contracts screen (the nearest contract block, else the global one) and, with `-y51online:selftest-contracts-accept`,
/// accepts the first available contract; the server logs every player's contract state every 20 s. "selftest-contracts: ..." lines.
/// </summary>
internal static class ContractsProbe
{
	private static bool Requested => MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "selftest-contracts");
	private static bool Accept => MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "selftest-contracts-accept");

	private static long _clientAtMs, _serverNextMs;

	public static void Tick(Session session)
	{
		if (!Requested) return;
		try
		{
			if (ReferenceEquals(session, MpPlugin.App?.ServerSession)) Server(session);
			else if (ReferenceEquals(session, MpPlugin.App?.ClientSession) && MpPlugin.App?.ServerSession == null) Client(session);
		}
		catch (Exception ex) { GameLog.Instance.WarnOnce("contracts-probe", "selftest-contracts: tick failed: " + ex); }
	}

	private static void Server(Session session)
	{
		long now = MpClock.NowMs();
		if (now < _serverNextMs) return;
		_serverNextMs = now + 20000;
		var mission = session.SessionComponents.TryGet<MissionSessionComponent>();
		var contracts = session.SessionComponents.TryGet<ContractModelServerSessionComponent>() as IColonizationContracts;
		var lines = new List<string>();
		foreach (long who in session.GetEntitiesOfType<CharacterLifeComponent>().Select(TeamPermissions.OwnerOf).Where(PlayerIdentities.IsHuman).Distinct())
		{
			if (contracts == null) break;
			var st = contracts.GetContractsStateFor(new Keen.Game2.Simulation.GameSystems.Ownership.IdentityId(who));
			lines.Add($"{who}: accepted [{string.Join(",", st.AcceptedContracts)}] finished [{string.Join(",", st.FinishedContracts)}] completed [{string.Join(",", st.CompletedContracts)}] focused [{string.Join(",", st.FocusedContracts)}]");
		}
		GameLog.Instance.Info($"selftest-contracts: server mission {(mission == null ? "none" : "present")}, contracts {(contracts == null ? "none" : "present")}; " + string.Join(" | ", lines));
	}

	private static Entity? Me(Session session) => session.SessionComponents.Get<ClientPlayersSessionComponent>().LocalPlayerController?.ControlledEntities
		.Select(c => (c.Controllable as Component)?.Entity).FirstOrDefault(e => e?.TryGet<CharacterLifeComponent>() != null);

	private static Session? _clientSession;

	private static void Client(Session session)
	{
		// Again after a rejoin: every join builds a new client world.
		if (!ReferenceEquals(session, _clientSession)) { _clientSession = session; _clientAtMs = 0; }
		if (_clientAtMs < 0 || MpPlugin.App!._isTransitioning.IsSet || Me(session) == null) return;
		long now = MpClock.NowMs();
		if (_clientAtMs == 0) { _clientAtMs = now + 40000; return; }
		if (now < _clientAtMs) return;
		_clientAtMs = -1;
		_ = Run(session);
	}

	// The quest log and objective HUD show this list; the server fills it on every client that has the component.
	private static void Objectives(Session session, string when)
	{
		var notes = session.SessionComponents.Components.OfType<Keen.Game2.Client.GameSystems.Missions.MissionObjectiveNotificationSessionComponent>().FirstOrDefault();
		var list = notes == null ? null : HarmonyLib.Traverse.Create(notes).Field("_objectives").GetValue<Dictionary<string, Keen.Game2.Simulation.GameSystems.Missions.Objectives.Core.MissionObjective>>();
		GameLog.Instance.Info($"selftest-contracts: client objectives {when}: " + (list == null ? "none (no objective component)" : $"{list.Count} [{string.Join("; ", list.Values.Select(o => o.Id + "=" + o.State))}]"));
	}

	private static async System.Threading.Tasks.Task Run(Session session)
	{
		var log = GameLog.Instance;
		var have = new[] { "MissionUISessionComponent", "MissionLogDisplaySessionComponent", "MissionObjectiveNotificationSessionComponent", "ContractModelClientSessionComponent",
			"StreamedContractsScreenSessionComponent", "ContractNotificationsSessionComponent", "InputActionDetectionSessionComponent", "MissionEndClientSessionComponent", "GPSMarkerRenderSessionComponent" }
			.Select(n => n + "=" + session.SessionComponents.Components.OfType<object>().Any(c => c.GetType().Name == n)).ToList();
		var screen = session.TryGet<IContractsScreen>();
		log.Info($"selftest-contracts: client components {string.Join(", ", have)}; IContractsScreen {(screen == null ? "MISSING" : "present")}");
		Objectives(session, "at start");
		if (screen == null) return;
		var me = Me(session)!;
		var at = me.Data.GetWorldTransform().Position;
		var block = session.GetEntitiesOfType<Keen.Game2.Simulation.WorldObjects.CubeBlocks.CubeBlockComponent>()
			.Where(e => e.GetComposition()?.DebugNameShort?.Contains("ContractBlock", StringComparison.Ordinal) == true)
			.OrderBy(e => Keen.VRage.Library.Mathematics.Vector3D.DistanceSquared(e.Data.GetWorldTransform().Position, at)).FirstOrDefault();
		try
		{
			var model = await screen.Attach(block);
			for (int i = 0; i < 25 && model.AvailableContracts.Count + model.ActiveContracts.Count == 0; i++) await Keen.VRage.Library.Threading.Task.Delay(TimeSpan.FromMilliseconds(200));
			string L(IEnumerable<ContractModel> c) => string.Join("; ", c.Select(x => x.Id + "=" + x.Definition?.ToString().Split('\\').Last() + (x.Mission ? " (mission)" : "")));
			log.Info($"selftest-contracts: client screen ({(block == null ? "global" : block.GetComposition()?.DebugNameShort)}): available [{L(model.AvailableContracts)}] active [{L(model.ActiveContracts)}] completed [{L(model.CompletedContracts)}]");
			var pick = model.AvailableContracts.FirstOrDefault(c => !c.Mission) ?? model.AvailableContracts.FirstOrDefault();
			if (Accept && pick != null)
			{
				await screen.AcceptContract(pick.Id);
				await Keen.VRage.Library.Threading.Task.Delay(TimeSpan.FromSeconds(3));
				log.Info($"selftest-contracts: client accepted {pick.Id}; now available [{L(model.AvailableContracts)}] active [{L(model.ActiveContracts)}]");
				Objectives(session, "after accepting");
			}
			await screen.Detach();
		}
		catch (Exception ex) { log.Error($"selftest-contracts: FAIL client screen: {ex.GetType().Name}: {ex.Message.Split('\n')[0]}"); }
	}
}
#endif
