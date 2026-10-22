#if !PULSAR
using System;
using System.Linq;
using Keen.Game2.Simulation.GameSystems.Ownership;
using Keen.Game2.Simulation.GameSystems.Permissions;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.Game2.Simulation.WorldObjects.Characters;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Utils;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;
using Y51_SE2Online.SyncEngine;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>
/// Test rig only (`-y51online:selftest`): the server checks grid sharing, faction access and block lists against the
/// game's own permission answers; a client sends real share/release/claim requests and waits for the host's answer.
/// Results go to the log as "selftest: PASS ..." / "selftest: FAIL ...".
/// </summary>
internal static class SelfTest
{
	public static bool Requested => MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "selftest");

	private static bool _serverDone;

	/// <summary>Server thread, once social is up and a player-owned grid exists.</summary>
	public static void Server(Session session, WorldSocial social, IMpLog log)
	{
		if (_serverDone || !Requested) return;
		var grid = session.GetEntitiesOfType<CubeGridComponent>().FirstOrDefault(g => PlayerIdentities.IsHuman(TeamPermissions.OwnerOf(g))
			&& g.TryGet<GridPermissionsComponent>() != null && Block(g) != null);
		if (grid == null) return;
		_serverDone = true;
		var permissions = session.SessionComponents.Get<PermissionsSessionComponent>();
		long owner = TeamPermissions.OwnerOf(grid);
		const long member = 900000001, outsider = 900000002;
		var block = Block(grid)!;
		var blockPermissions = block.Get<BlockPermissionsComponent>();
		var sharing = WorldSocial.SharingOf(grid);
		var access = blockPermissions.AccessGroup;
		int passed = 0;
		void Check(bool ok, string what)
		{
			if (!ok) throw new InvalidOperationException(what);
			passed++;
			log.Info("selftest: PASS " + what);
		}
		IdentityId Id(long value) => new(value);
		lock (social.Sync)
		{
			var faction = social.State.FactionOf(owner);
			try
			{
				WorldSocial.SetSharing(grid, GridSharing.Private);
				Check(permissions.IsOwner(grid, Id(owner)) && permissions.CanPlaceBlock(grid, Id(owner)), "owner may build on a private grid");
				Check(!permissions.IsOwner(grid, Id(outsider)) && !permissions.CanPlaceBlock(grid, Id(outsider)), "outsider may not build on a private grid");
				Check(!permissions.CanUseBlockAction(block, grid, Id(outsider), default), "outsider may not use a private grid's block");
				WorldSocial.SetSharing(grid, GridSharing.Everyone);
				Check(permissions.CanPlaceBlock(grid, Id(outsider)), "anyone may build on a grid shared with everyone");
				if (faction != null)
				{
					faction.Members.Add(member);
					WorldSocial.SetSharing(grid, GridSharing.Faction);
					Check(permissions.CanPlaceBlock(grid, Id(member)) && permissions.IsFriendly(Id(member), grid), "faction member may build on a faction-shared grid");
					Check(!permissions.CanPlaceBlock(grid, Id(outsider)), "outsider may not build on a faction-shared grid");
					faction.Members.Remove(member);
					Check(!permissions.CanPlaceBlock(grid, Id(member)), "leaving the faction takes the access away at once");
				}
				WorldSocial.SetSharing(grid, GridSharing.Everyone);
				blockPermissions.AccessGroup = StringId.Get(new BlockAcl(Array.Empty<long>(), new[] { outsider }, Owner: owner).ToString());
				Check(WorldSocial.Access(block, outsider) == false && !permissions.CanUseBlockAction(block, grid, Id(outsider), default), "a block's deny list beats grid sharing");
				blockPermissions.AccessGroup = StringId.Get(new BlockAcl(new[] { outsider }, Array.Empty<long>(), Owner: owner).ToString());
				WorldSocial.SetSharing(grid, GridSharing.Private);
				Check(WorldSocial.Access(block, outsider) == true, "a block's allow list opens one block of a private grid");
				log.Info($"selftest: server permissions {passed} checks passed on grid '{grid.Get<CubeGridComponent>().DisplayName}' owned by {owner}");
			}
			catch (Exception ex) { log.Error($"selftest: FAIL {ex.Message} (after {passed} passed)"); }
			finally
			{
				faction?.Members.Remove(member);
				WorldSocial.SetSharing(grid, sharing);
				blockPermissions.AccessGroup = access;
			}
		}
	}

	private static long _nextCountMs;

	/// <summary>Every 30 s: how many grids and blocks this side's scene holds (compare server and client).</summary>
	public static void CountGrids(Session session)
	{
		if (!Requested) return;
		bool server = ReferenceEquals(session, MpPlugin.App?.ServerSession);
		if (!server && !ReferenceEquals(session, MpPlugin.App?.ClientSession)) return;
		long now = MpClock.NowMs();
		if (now < _nextCountMs) return;
		_nextCountMs = now + 30000;
		int grids = 0, blocks = 0;
		foreach (var grid in session.GetEntitiesOfType<CubeGridComponent>())
		{
			grids++;
			grid.Get<CubeGridComponent>().VisitAllBlocksWithComponent((CubeBlockComponent _) => blocks++);
		}
		var players = session.SessionComponents.TryGet<Keen.Game2.Client.GameSystems.PlayerControl.ClientPlayersSessionComponent>();
		string local = server ? "" : $", local player identity {(long?)players?.LocalPlayerIdentity}";
		if (!server && players?.LocalPlayerController?.GetTopControllable()?.Entity is { } controlled)
		{
			var at = controlled.Data.GetWorldTransform().Position;
			local += $", controls {controlled.GetComposition()?.DebugNameShort} owned by {TeamPermissions.OwnerOf(controlled)} at {at.X:0}, {at.Y:0}, {at.Z:0}";
		}
		GameLog.Instance.Info($"selftest: {(server ? "server" : "client")} scene has {grids} grids, {blocks} blocks{local}");
		if (server && !_charactersListed)
		{
			_charactersListed = true;
			foreach (var character in session.GetEntitiesOfType<CharacterLifeComponent>())
			{
				var at = character.Data.GetWorldTransform().Position;
				GameLog.Instance.Info($"selftest: saved character owned by {TeamPermissions.OwnerOf(character)}, {(character.Get<CharacterLifeComponent>().IsAlive ? "alive" : "dead")}, at {at.X:0}, {at.Y:0}, {at.Z:0}");
			}
		}
	}

	private static bool _charactersListed;

	private static Entity? Block(Entity grid)
	{
		Entity? found = null;
		grid.Get<CubeGridComponent>().VisitAllBlocksWithComponent((BlockPermissionsComponent p) => found ??= p.Entity);
		return found;
	}

	private static int _step = -1;

	private static long _stepSinceMs;

	private static uint _grid;

	/// <summary>Client main thread: share, unshare, release and claim one of this player's grids through the host.</summary>
	private static long _chatSentMs;

	public static void Client(ClientSession session, IMpLog log)
	{
		if (!Requested) return;
		long now = MpClock.NowMs();
		Chat(session, log, now);
		Terminals(log, now);
		Factions(session, log, now);
		if (_step >= 4 || session.Social is not { } social || social.Grids == null) return;
		if (_step < 0)
		{
			var mine = social.Grids.FirstOrDefault(g => g.Owner == social.Actor);
			if (mine == null) return;
			_grid = mine.NetId;
			_step = 0;
			_stepSinceMs = 0;
		}
		var current = social.Grids.FirstOrDefault(g => g.NetId == _grid);
		(SocialRequest Request, Func<SocialGrid?, bool> Done, string What)[] steps =
		{
			(new SocialRequest(SocialAction.ShareGrid, Grid: _grid, Sharing: GridSharing.Everyone), g => g?.Sharing == GridSharing.Everyone, "share a grid with everyone"),
			(new SocialRequest(SocialAction.ShareGrid, Grid: _grid, Sharing: GridSharing.Private), g => g?.Sharing == GridSharing.Private, "make it private again"),
			(new SocialRequest(SocialAction.ReleaseGrid, Grid: _grid), g => g != null && g.Owner == 0, "release the grid"),
			(new SocialRequest(SocialAction.ClaimGrid, Grid: _grid), g => g != null && g.Owner == social.Actor, "claim it back"),
		};
		var step = steps[_step];
		if (_stepSinceMs == 0)
		{
			_stepSinceMs = now;
			session.SendSocial(step.Request);
			return;
		}
		if (step.Done(current))
		{
			log.Info($"selftest: PASS client: {step.What} ({now - _stepSinceMs} ms through the host)");
			_step++;
			_stepSinceMs = 0;
			if (_step == 4) log.Info("selftest: client ownership requests all passed");
		}
		else if (now - _stepSinceMs > 20000)
		{
			log.Error($"selftest: FAIL client: {step.What} (no answer in 20 s; host says {social.Message})");
			_step = 4;
		}
	}

	// Chat goes up to the host and back down in its broadcast.
	private static void Chat(ClientSession session, IMpLog log, long now)
	{
		if (_chatSentMs < 0) return;
		if (_chatSentMs == 0)
		{
			_chatSentMs = now;
			session.SendChat("selftest chat");
			return;
		}
		if (ChatLog.Recent(20).Any(l => l.Line.Text == "selftest chat"))
		{
			log.Info($"selftest: PASS client: chat round trip ({now - _chatSentMs} ms through the host)");
			_chatSentMs = -1;
		}
		else if (now - _chatSentMs > 15000)
		{
			log.Error("selftest: FAIL client: chat round trip (own line not back from the host in 15 s)");
			_chatSentMs = -1;
		}
	}

	private static long _terminalsAtMs;

	private static long _leaveAtMs;

	private static bool _rejoin, _rejoined;

	/// <summary>Main menu after the self-test left: true once, when -y51online:selftest-rejoin asks for a second join.</summary>
	public static bool TakeRejoin()
	{
		if (!_rejoin) return false;
		_rejoin = false;
		_rejoined = true;
		GameLog.Instance.Info("selftest: client joining again (rejoin test)");
		return true;
	}

	// Leaves the session the way the pause menu does, to catch crashes on the way back to the main menu.
	public static void Leave(Keen.Game2.GameAppComponent app, IMpLog log)
	{
		if (_leaveAtMs == 0 || MpClock.NowMs() < _leaveAtMs) return;
		_leaveAtMs = 0;
		log.Info("selftest: client leaving the session");
		app.Disconnect();
		_rejoin = MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "selftest-rejoin") && !_rejoined;
	}

	// Opens what a player opens by hand, through the same requests the screens send: each grid's control panel and its
	// blocks' detail pages, a cargo block's inventory and this player's own inventory. In team mode every one must open.
	private static void Terminals(IMpLog log, long now)
	{
		if (_terminalsAtMs < 0) return;
		if (_terminalsAtMs == 0) { _terminalsAtMs = now + 20000; return; }
		if (now < _terminalsAtMs || MpPlugin.App?.ClientSession is not { } session) return;
		_terminalsAtMs = -1;
		_ = Probe(session, log);
	}

	private static async System.Threading.Tasks.Task Probe(Session session, IMpLog log)
	{
		var panels = session.SessionComponents.Get<Keen.Game2.Simulation.StreamedUI.Terminal.ControlPanel.StreamedControlPanelSessionComponent>();
		var inventories = session.SessionComponents.Get<Keen.Game2.Simulation.StreamedUI.Terminal.Inventories.StreamedInventorySessionComponent>();
		int failed = 0;
		foreach (var grid in session.GetEntitiesOfType<CubeGridComponent>().Take(6).ToList())
		{
			string name = grid.Get<CubeGridComponent>().DisplayName.ToString();
			Entity? block = null; grid.Get<CubeGridComponent>().VisitAllBlocksWithComponent((Keen.Game2.Simulation.WorldObjects.CubeBlocks.FunctionalBlockComponent b) => block ??= b.Entity);
			if (block == null) { log.Warn($"selftest: client sees no functional block on '{name}'"); continue; }
			try
			{
				var list = await panels.Attach(block);
				var ids = list.Blocks.Select(b => b.EntityId).ToList();
				var details = await panels.Attach(new System.Collections.Generic.HashSet<Keen.VRage.Library.Utils.SubclassOf<Keen.Game2.Simulation.StreamedUI.Terminal.ControlPanel.BlockDetails.BlockDetailModel>>(), ids, list);
				log.Info($"selftest: PASS client: control panel of '{name}' (owner {TeamPermissions.OwnerOf(grid)}) lists {ids.Count} blocks, details answered for {details.Count}");
				// Closed the way the terminal closes them: the server releases what it streamed for this screen.
				foreach (var detail in details.Values.SelectMany(d => d)) panels.Detach(detail);
				panels.Detach(list);
			}
			catch (Exception ex) { failed++; log.Error($"selftest: FAIL client: control panel of '{name}': {ex.GetType().Name}: {ex.Message.Split('\n')[0]}"); }
			var cargo = block;
			if (cargo == null) continue;
			try
			{
				var inv = await inventories.Attach(cargo, populateMainEntity: false);
				log.Info($"selftest: PASS client: inventory screen of '{name}' opened");
				inventories.Detach(inv);
			}
			catch (Exception ex) { failed++; log.Error($"selftest: FAIL client: inventory on '{name}': {ex.GetType().Name}: {ex.Message.Split('\n')[0]}"); }
		}
		var me = session.SessionComponents.Get<Keen.Game2.Client.GameSystems.PlayerControl.ClientPlayersSessionComponent>().LocalPlayerController?.ControlledEntities
			.Select(c => (c.Controllable as Component)?.Entity).FirstOrDefault(e => e?.TryGet<CharacterLifeComponent>() != null);
		if (me != null)
		{
			try { var own = await inventories.Attach(me, populateMainEntity: true); log.Info("selftest: PASS client: own inventory opened"); inventories.Detach(own); }
			catch (Exception ex) { failed++; log.Error($"selftest: FAIL client: own inventory: {ex.GetType().Name}: {ex.Message.Split('\n')[0]}"); }
		}
		// The planet rig moves this character itself; a seat here would pull it off its script.
		if (me != null && !PlanetProbe.On)
		{
			var players = session.SessionComponents.Get<Keen.Game2.Client.GameSystems.PlayerControl.ClientPlayersSessionComponent>();
			var permissions = session.SessionComponents.TryGet<PermissionsSessionComponent>();
			foreach (var grid in session.GetEntitiesOfType<CubeGridComponent>().ToList())
			{
				long owner = TeamPermissions.OwnerOf(grid);
				if (PlayerIdentities.IsHuman(owner)) continue;
				Keen.Game2.Simulation.WorldObjects.CubeBlocks.Pilotable.SeatComponent? seat = null;
				grid.Get<CubeGridComponent>().VisitAllBlocksWithComponent((FunctionalBlockComponent b) => seat ??= b.Entity.FirstOrDefault<Keen.Game2.Simulation.WorldObjects.CubeBlocks.Pilotable.SeatComponent>());
				if (seat == null || seat.Pilot != null) continue;
				try
				{
					// NPC and unowned vehicles are SE2's to take (encounters reward stealing them).
					bool sat = await seat.TrySetPilotAsync(me);
					if (sat) { log.Info($"selftest: PASS client: sat in a seat on '{grid.Get<CubeGridComponent>().DisplayName}' (owner {owner})"); await seat.TryClearPilotAsync(); }
					else { failed++; log.Error($"selftest: FAIL client: refused a seat on '{grid.Get<CubeGridComponent>().DisplayName}' (owner {owner})"); }
				}
				catch (Exception ex) { failed++; log.Error($"selftest: FAIL client: seat request: {ex.GetType().Name}: {ex.Message.Split('\n')[0]}"); }
				break;
			}
		}
		log.Info($"selftest: client terminal probe done, {failed} failed");
		if (MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "selftest-leave")) _leaveAtMs = MpClock.NowMs() + 5000;
	}

	private static int _factionStep = -1;

	private static long _factionSinceMs;

	// A faction made, seen by the host and every client's snapshot, then disbanded, all through the host.
	private static void Factions(ClientSession session, IMpLog log, long now)
	{
		if (_factionStep >= 2 || session.Social is not { } social) return;
		if (_factionStep < 0)
		{
			if (social.Factions.Any(f => f.Members.Contains(social.Actor))) { _factionStep = 2; log.Info("selftest: client already in a faction; faction round trip skipped"); return; }
			_factionStep = 0;
			_factionSinceMs = 0;
		}
		var mine = social.Factions.FirstOrDefault(f => f.Tag == "STX");
		(SocialRequest Request, Func<bool> Done, string What)[] steps =
		{
			(new SocialRequest(SocialAction.CreateFaction, Name: "Selftest Faction", Tag: "STX"), () => mine != null && mine.Leader == social.Actor && mine.Members.Contains(social.Actor), "create a faction"),
			(new SocialRequest(SocialAction.DisbandFaction, FactionId: mine?.Id ?? ""), () => mine == null, "disband it"),
		};
		var step = steps[_factionStep];
		if (_factionSinceMs == 0)
		{
			_factionSinceMs = now;
			session.SendSocial(step.Request);
			return;
		}
		if (step.Done())
		{
			log.Info($"selftest: PASS client: {step.What} ({now - _factionSinceMs} ms through the host)");
			_factionStep++;
			_factionSinceMs = 0;
		}
		else if (now - _factionSinceMs > 20000)
		{
			log.Error($"selftest: FAIL client: {step.What} (no answer in 20 s; host says {social.Message})");
			_factionStep = 2;
		}
	}
}
#endif
