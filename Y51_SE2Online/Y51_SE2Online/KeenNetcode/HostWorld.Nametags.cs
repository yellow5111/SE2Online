using System;
using System.Collections.Generic;
using System.Linq;
using Keen.Game2.Simulation.GameSystems.Ownership;
using Keen.Game2.Simulation.Utils;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.Components;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

// Who is behind each character, for players' name tags (PlayerNamesPublisher sends it); carried over from the 2.4 engine.
internal sealed partial class HostWorld
{
	private const int RosterIntervalMs = 500;

	private long _lastRosterMs;

	private bool _rosterFailed;

	private volatile Dictionary<uint, long> _tagOwners = new();

	private NametagOverlay? _hostTags;

	private readonly List<(ushort ClientId, string Name, Entity? Target)> _hostTagList = new();

	private long _hostTagVersion = -1;

	private int _hostTagTick;

	/// <summary>Main thread, listen host: other players' name tags over the characters in the host's own client scene.</summary>
	internal void MainTick(Keen.Game2.GameAppComponent app)
	{
		var client = app.ClientSession;
		if (!NametagOverlay.Enabled || client == null || app._isTransitioning.IsSet || Y51_SE2Online.Dedicated.DedicatedRuntime.Requested) return;
		try
		{
			var (version, tags) = HostNametags.Roster.Snapshot();
			if (version != _hostTagVersion || _hostTagTick++ % 30 == 0)
			{
				_hostTagVersion = version;
				_hostTagList.Clear();
				var byOwner = new Dictionary<long, Entity>();
				foreach (Entity character in client.GetEntitiesOfType<Keen.Game2.Simulation.WorldObjects.Characters.CharacterLifeComponent>())
					if (character.Get<Keen.Game2.Simulation.WorldObjects.Characters.CharacterLifeComponent>().IsAlive) byOwner.TryAdd(TeamPermissions.OwnerOf(character), character);
				var owners = _tagOwners;
				foreach (var tag in tags)
				{
					if (tag.ClientId == SyncProtocol.HostClientId) continue;
					Entity? target = owners.TryGetValue(tag.NetId, out long owner) && byOwner.TryGetValue(owner, out Entity found) ? found : null;
					_hostTagList.Add((tag.ClientId, tag.Name, target));
				}
			}
			var sessionEntity = client.SessionComponents.Get<Keen.Game2.Client.GameSystems.PlayerControl.ClientPlayersSessionComponent>().Entity;
			(_hostTags ??= new NametagOverlay(_log, "host")).Update(client, sessionEntity, _hostTagList);
		}
		catch (Exception ex)
		{
			_log.WarnOnce("host-nametags", $"nametags(host): update failed ({ex.GetType().Name}: {ex.Message})");
		}
	}

	private void UpdateNametagRoster(Session session)
	{
		if (_rosterFailed || !NametagOverlay.Enabled)
		{
			return;
		}
		long now = MpClock.NowMs();
		if (now - _lastRosterMs < RosterIntervalMs && !_rosterSilentChanged)
		{
			return;
		}
		_lastRosterMs = now;
		_rosterSilentChanged = false;
		try
		{
			var online = new List<(ushort, uint, long, string)>();
			var onlineCharacters = new HashSet<Entity>(ReferenceEqualityComparer.Instance);
			foreach (var joiner in _joiners.Values)
			{
				var character = joiner.Character;
				bool live = character != null && !EngineCodec.IsDead(character);
				if (live) onlineCharacters.Add(character);
				online.Add((joiner.ClientId, live ? joiner.NetId : 0, IdentityOf(joiner.ClientId), Tagged(IdentityOf(joiner.ClientId), joiner.Name)));
			}
			// Players still loading in have no joiner yet; their parked body must not read as offline to them. A record whose
			// player left (no peer) stays for the rejoin: counting it online hid that body's "(offline)" tag altogether.
			foreach (var player in _host?.Players() ?? Array.Empty<HostSession.PlayerRecord>())
			{
				if (player.Peer != null && !_joiners.ContainsKey(player.ClientId)) online.Add((player.ClientId, 0, IdentityOf(player.ClientId), ""));
			}
			var hostCharacter = _hostCharacterServer;
			if (hostCharacter != null && !EngineCodec.IsDead(hostCharacter) && !onlineCharacters.Contains(hostCharacter) && _ids.TryGet(hostCharacter, out var hostId))
			{
				online.Add((SyncProtocol.HostClientId, hostId, _hostIdentity, Tagged(_hostIdentity, HostNametags.HostName)));
			}
			var bodies = new List<(uint, long)>();
			var alive = new List<Entity>();
			if (session.TryFillAliveCharactersHotfix(alive, preserveOrder: false))
			{
				foreach (var character in alive)
				{
					if (onlineCharacters.Contains(character) || EngineCodec.IsDead(character) || !_ids.TryGet(character, out var netId)) continue;
					bodies.Add((netId, (long?)character.TryGet<OwnershipComponent>()?.Owner ?? 0));
				}
			}
			var names = new Dictionary<long, string>();
			HashSet<long> silent;
			var social = Social;
			if (social == null) silent = new HashSet<long>();
			else lock (social.Sync)
			{
				silent = new HashSet<long>(social.State.Silent);
				var owners = new HashSet<long>(bodies.Select(b => b.Item2));
				foreach (var player in social.State.Players)
				{
					if (owners.Contains(player.Identity)) names[player.Identity] = Tagged(player.Identity, player.Name);
				}
			}
			HostNametags.Roster.Set(PlayerTag.Build(NametagOverlay.Enabled, online, bodies, names, silent, Y51_SE2Online.Dedicated.DedicatedRuntime.Requested ? 0 : _hostIdentity));
			// The host's own overlay finds these characters in its client scene by owner (it does not share the server's ids).
			var tagOwners = new Dictionary<uint, long>();
			foreach (var (_, netId, identity, _) in online) if (netId != 0) tagOwners[netId] = identity;
			foreach (var (netId, owner) in bodies) tagOwners[netId] = owner;
			_tagOwners = tagOwners;
		}
		catch (Exception ex)
		{
			_rosterFailed = true;
			_log.Error($"nametags(host): building the player list failed, players get no nametags this session (game unaffected): {ex}");
		}
	}
}
