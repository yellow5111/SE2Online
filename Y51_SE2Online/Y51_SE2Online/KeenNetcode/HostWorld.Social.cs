using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Keen.Game2.Simulation.GameSystems.Ownership;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.Components;
using Y51_SE2Online.Core;
using Y51_SE2Online.Net;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

// Factions, grid sharing, block access lists and the world rules (PCU, sun, multipliers), as on SE2 2.4; this file
// carries the 2.4 engine's code for them onto the SE2 4.0 core (KeenNetcode/HostWorld.cs).
internal sealed partial class HostWorld
{
	/// <summary>The hosting player's own identity (0 on a dedicated server, where nobody plays as the host).</summary>
	private long _hostIdentity;

	private double _socialMs;

	private bool _socialLogged;

	private volatile bool _rosterSilentChanged;

	private readonly ConcurrentQueue<(PeerId? Peer, ushort ClientId, SocialRequest Request)> _socialRequests = new();
	private readonly Dictionary<string, (int Count, long Bytes)> _frameSignalBytes = new();
	private long _heavyLoggedMs = long.MinValue / 2;
	private readonly Dictionary<long, SocialRequest> _socialViews = new();
	private readonly Dictionary<long, string> _socialMessages = new();
	private readonly ConcurrentDictionary<ushort, (long Start, int Count)> _socialRate = new();

	private long _socialNextPublish, _socialPublishedMs;

	private bool _socialDirty;

	// Every grid's name is cleaned at every social publish; names rarely change, and the cleaner walks each character.
	private readonly Dictionary<string, string> _cleanGridNames = new(StringComparer.Ordinal);

	private string CleanGridName(string raw)
	{
		if (_cleanGridNames.TryGetValue(raw, out var clean)) return clean;
		if (_cleanGridNames.Count >= 65536) _cleanGridNames.Clear();
		return _cleanGridNames[raw] = SocialState.Clean(raw, 100);
	}
	private string Tagged(long identity, string name)
	{
		string tag = FactionTagOf(identity);
		return FactionTag.Prefix(tag, name);
	}

	// Read from the net thread while HostSession holds its own lock, so it must not take social.Sync.
	private volatile Dictionary<long, string> _factionTags = new();

	internal string? FactionTagOf(long identity) => identity != 0 && _factionTags.TryGetValue(identity, out string tag) ? tag : null;

	internal void QueueSocial(SocialRequest request) => _socialRequests.Enqueue((null, 1, request));
	public void OnSocialRequest(PeerId peer, ushort clientId, SocialRequest request)
	{
		long now = MpClock.NowMs();
		// A burst of five a second: a single 200 ms slot dropped clicks that landed next to a periodic request (suit broadcast, terminal open).
		var (start, count) = _socialRate.TryGetValue(clientId, out var rate) && now - rate.Start < 1000 ? rate : (now, 0);
		if (_socialRequests.Count >= 1024 || count >= 5) return;
		_socialRate[clientId] = (start, count + 1);
		_socialRequests.Enqueue((peer, clientId, request));
	}

	private long _accessRevision = -1;

	// A ban, whitelist or admin change must also stop a player who was admitted moments before but has not finished joining.
	private void ForgetAdmissionsOnAccessChange()
	{
		if (_host is not { } host || host.Access.Revision == _accessRevision) return;
		_accessRevision = host.Access.Revision;
	}

	private void ApplyPcuLimit(Session session)
	{
		var host = _host;
		var pcu = session.SessionComponents.TryGet<Keen.Game2.Simulation.GameSystems.PCU.PCUSessionComponent>();
		if (host == null || pcu == null) return;
		host.Access.RememberWorldPcu(pcu.MaxPerPlayerPCU);
		int want = host.PcuLimit is int limit ? (limit == 0 ? int.MaxValue : limit) : host.Access.WorldPcu ?? pcu.MaxPerPlayerPCU;
		if (pcu.MaxPerPlayerPCU != want)
		{
			pcu.MaxPerPlayerPCU = want;
			_log.Info($"host world sync: per-player PCU limit {(want == int.MaxValue ? "removed" : want.ToString("N0"))}");
		}
		host.PcuInForce = want;
	}

	private double _sunGlide, _sunGlideDone, _sunGlideMs;
	private long _sunGlideStartMs;

	private void ApplySun(Session session)
	{
		var host = _host;
		var sun = session.SessionComponents.TryGet<Keen.Game2.Simulation.GameSystems.Sun.SunSessionComponent>();
		if (host == null || sun == null) return;
		bool rotate = host.SunRotation ?? sun.SunRotation;
		var period = host.SunPeriodMinutes is double minutes ? TimeSpan.FromMinutes(minutes) : sun.SunPeriod;
		double nudge = host.TakeSunNudge() / 360.0;
		if (host.TakeSunTarget() is double target) { nudge += target / 360.0 - Angle(); _sunGlideMs = 0; }
		if (host.TakeSunGlide() is (bool absolute, double deg))
		{
			double turns = absolute ? deg / 360.0 - Angle() : deg / 360.0;
			if (absolute) turns -= Math.Round(turns);
			// About 30 degrees a second, eased in and out; a new move starts from wherever the sun is now.
			_sunGlide = turns; _sunGlideDone = 0; _sunGlideStartMs = MpClock.NowMs();
			_sunGlideMs = Math.Clamp(Math.Abs(turns) * 12000, 1500, 12000);
		}
		if (_sunGlideMs > 0)
		{
			double t = Math.Min(1, (MpClock.NowMs() - _sunGlideStartMs) / _sunGlideMs), eased = t * t * (3 - 2 * t);
			nudge += _sunGlide * (eased - _sunGlideDone);
			_sunGlideDone = eased;
			if (t >= 1) _sunGlideMs = 0;
		}
		if (rotate != sun.SunRotation || period != sun.SunPeriod || nudge != 0)
		{
			// The sun's angle is (game time of day / period + offset) turns, with the time term dropped while it is
			// stopped: carry the change into the offset so stopping or changing the day length leaves the sun put.
			double offset = sun.TimeOfDayOffset + Phase(sun.SunRotation, sun.SunPeriod) - Phase(rotate, period) + nudge;
			sun.TimeOfDayOffset = (float)(offset - Math.Floor(offset));
			sun.SunRotation = rotate;
			sun.SunPeriod = period;
			if (_sunGlideMs == 0) _log.Info($"host world sync: sun rotation {(rotate ? "on" : "off")}, one turn every {period.TotalMinutes:0.#} min, offset {sun.TimeOfDayOffset:0.###}");
		}
		host.SunInForce = (sun.SunRotation, sun.SunPeriod.TotalMinutes);
		host.SunDegrees = Angle() * 360;

		double Angle() { double turns = sun.TimeOfDayOffset + Phase(sun.SunRotation, sun.SunPeriod); return turns - Math.Floor(turns); }

		double Phase(bool rotating, TimeSpan turn) => rotating && turn.TotalSeconds > 0 ? sun._time.CurrentGameTime.TimeOfDay.TotalSeconds / turn.TotalSeconds % 1.0 : 0;
	}

	private long _socialTableVersion = -1;

	private void UpdateSocial(Session session)
	{
		long socialStarted = Stopwatch.GetTimestamp();
		try { UpdateSocialCore(session); }
		finally { _socialMs += Stopwatch.GetElapsedTime(socialStarted).TotalMilliseconds; }
	}

	private void UpdateSocialCore(Session session)
	{
		ApplyPcuLimit(session);
		ForgetAdmissionsOnAccessChange();
		ApplySun(session);
		ResourceMultiplierRules.Apply(session);
		InventoryMultiplierRules.Apply(session);
		BlockSizeFilter.EnsureHost();
		var social = Social;
		if (social == null || _host == null) return;
		lock (social.Sync)
		{
			_factionTags = social.State.Factions.SelectMany(f => f.Members.Select(m => (m, f.Tag))).GroupBy(x => x.m).ToDictionary(g => g.Key, g => g.First().Tag);
			PvpRules.SetPlayers(social.State.Players.Select(p => p.Identity),
				_joiners.Values.Select(j => IdentityOf(j.ClientId)).Append(_hostIdentity),
				social.State.Factions.Select(f => (IReadOnlyCollection<long>)f.Members));
			OfflineStats.Enabled = HackSettings.PauseOfflineStats;
			OfflineStats.SetPlayers(social.State.Players.Select(p => p.Identity), _joiners.Values.Where(j => j.Character != null).Select(j => IdentityOf(j.ClientId)).Append(_hostIdentity));
			if (TurretRelations.SetRelations(social.State.Factions, social.State.Pacts) | TurretRelations.TakeRefresh())
				_log.Info($"turrets: factions or PvP changed; {TurretRelations.Refresh(session)} turret grid(s) re-evaluated their targets; {PvpRules.Describe()}");
			social.InitializeGrids(_worldSession);
			var roster = _host.PlayerTable();
			long now = MpClock.NowMs();
			// Registering is O(players^2); the roster only changes with the player table, and once a second catches the rest.
			long tableVersion = Interlocked.Read(ref _host.PlayerTableVersion);
			if (tableVersion != _socialTableVersion || now >= _socialNextPublish)
			{
				_socialTableVersion = tableVersion;
				foreach (var player in roster) social.State.Register(player.Identity, player.Name, player.Identity == _hostIdentity && _hostIdentity != 0 ? _host.HostKey : null);
			}
			bool changed = false;
			while (_socialRequests.TryDequeue(out var command))
			{
				long actor = command.Peer.HasValue ? IdentityOf(command.ClientId) : _hostIdentity;
				if (command.Peer.HasValue && !_host.Players().Any(p => p.ClientId == command.ClientId && p.Peer == command.Peer)) continue;
				if (command.Request.Action == SocialAction.Broadcast)
				{
					// Kept per identity in the world's side file, so a parked character's tag follows its owner's last choice.
					if (PlayerIdentities.IsHuman(actor) && (command.Request.Offset == 0 ? social.State.Silent.Add(actor) : social.State.Silent.Remove(actor))) _rosterSilentChanged = true;
					continue;
				}
				if (!roster.Any(p => p.Identity == actor)) continue;
				if (command.Request.Action is SocialAction.Hack or SocialAction.Lockdown) { HackCommand(session, actor, command.ClientId, command.Request); continue; }
				var request = WithSecureTarget(session, actor, command.Request);
				if (request.Action == SocialAction.Refresh) _socialViews[actor] = request;
				else _socialMessages[actor] = social.Apply(actor, request, id => _ids.TryGet(id, out Entity entity) && !EngineCodec.IsDead(entity) ? entity : null);
				changed = true;
			}
			// Requests publish within a quarter second, not once each: every publish walks all grids for every player.
			_socialDirty |= changed;
			if (!(_socialDirty && now - _socialPublishedMs >= 250) && now < _socialNextPublish) return;
			_socialDirty = false;
			_socialPublishedMs = now;
			_socialNextPublish = now + 1000;
			social.TagAntennas(session, FactionTagOf);
			foreach (var (clientId, text) in social.DueFactionMotds(roster)) _host.GpsNotice(clientId, text);
			var grids = session.GetEntitiesOfType<CubeGridComponent>().Where(e => e.TryGet<OwnershipComponent>() != null && !EngineCodec.IsDead(e))
				.Select(e => new SocialGrid(_ids.GetOrAssign(e), CleanGridName(e.TryGet<CubeGridComponent>().DisplayName.ToString()), TeamPermissions.OwnerOf(e), WorldSocial.SharingOf(e)))
				.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.NetId).ToArray();
			var (listed, open) = BlockLists(session);
			// Everything every player sees alike is built and serialized once here, not once per player.
			var common = social.Common(open);
			var commonDoc = JsonDoc.Of(common);
			var pages = new Dictionary<(string, int), JsonDoc>();
			var peers = new Dictionary<ushort, PeerId>();
			foreach (var p in _host.Players()) if (p.Peer is PeerId id) peers.TryAdd(p.ClientId, id);
			var docs = new List<(PeerId, JsonDoc)>(peers.Count);
			foreach (var player in roster)
			{
				bool remote = peers.TryGetValue(player.ClientId, out var peer);
				if (!remote && player.Identity != _hostIdentity) continue;
				var view = _socialViews.TryGetValue(player.Identity, out var current) ? current : new SocialRequest(SocialAction.Refresh);
				var message = _socialMessages.TryGetValue(player.Identity, out var text) ? text : "";
				var snapshot = social.Snapshot(player.Identity, grids, view, message, common);
				snapshot.FocusBlock = FocusBlockOf(player.Identity);
				snapshot.AllowedBlocks = listed.Where(b => b.Acl.Allow.Contains(player.Identity)).Select(b => b.Id).ToArray();
				snapshot.DeniedBlocks = listed.Where(b => b.Acl.Deny.Contains(player.Identity)).Select(b => b.Id).ToArray();
				snapshot.Hacks = HackViewsFor(player.Identity);
				if (!remote) continue;
				var pageKey = (view.Name?.Trim() ?? "", snapshot.Offset);
				if (!pages.TryGetValue(pageKey, out var page)) pages[pageKey] = page = JsonDoc.Of(new SocialSnapshot { Grids = snapshot.Grids });
				docs.Add((peer, JsonDoc.Of(snapshot.OwnPart()).With(commonDoc, SocialSnapshot.Common).With(page, new[] { nameof(SocialSnapshot.Grids) })));
			}
			if (docs.Count > 0 && !_socialLogged)
			{
				_socialLogged = true;
				_log.Info($"social: first publish to {docs.Count} player(s): {social.State.Players.Count} players, {social.State.Factions.Count} factions, {grids.Length} owned grids");
			}
			_host.SendSocial(docs);
		}
	}
}
