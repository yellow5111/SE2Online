using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Keen.Game2.Simulation.GameSystems.BlockPlacement;
using Keen.Game2.Simulation.GameSystems.Camera;
using Keen.Game2.Simulation.GameSystems.Colonization;
using Keen.Game2.Simulation.GameSystems.Contracts;
using Keen.Game2.Simulation.GameSystems.Discoveries;
using Keen.Game2.Simulation.GameSystems.Economy;
using Keen.Game2.Simulation.GameSystems.GPS;
using Keen.Game2.Simulation.GameSystems.Ownership;
using Keen.Game2.Simulation.GameSystems.PCU;
using Keen.Game2.Simulation.GameSystems.Player;
using Keen.Game2.Simulation.GameSystems.Progression;
using Keen.Game2.Simulation.GameSystems.Respawn;
using Keen.Game2.Simulation.GameSystems.Toolbars;
using Keen.Game2.Simulation.GameSystems.Toolbars.VoxelHand;
using Keen.Game2.Simulation.WorldObjects.Tools;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Collections;
using Keen.VRage.Library.Utils;
using Keen.VRage.Multiplayer;
using Keen.VRage.Multiplayer.NetworkStories;
using Y51_SE2Online.Core;
using Y51_SE2Online.Dedicated;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

internal sealed class HostIdentities
{
	public readonly record struct Result(long Identity, long Player, bool NewPlayer, string Detail);

	private readonly IMpLog _log;

	public readonly HostSettings Settings;

	private readonly ConcurrentDictionary<long, string?> _known = new();

	private readonly ConcurrentDictionary<string, long> _skippedStories = new();

	public long StoriesSkipped;

	public long StoriesForwarded;

	public long Registered;

	public long RestoredAtLoad;

	internal static readonly Type[] ResearchTypes = new Type[]
	{
		typeof(PlayerProgressionData),
		typeof(ColonizationProgressData),
		typeof(ContractPerPlayerData),
		typeof(PlayerCreditsData)
	};

	// Welder/grinder mode and the paint palette are changed on the host (the mode request signal, the palette
	// story); without the live copy the joiner never saw a mode change, and a palette-less paint tool crashed it.
	internal static readonly Type[] LiveTypes = ResearchTypes.Append(typeof(GPSMarkersData)).Append(typeof(DiscoveriesPlayerData))
		.Append(typeof(IntegrityToolPerPlayerData)).Append(typeof(PaintToolPerPlayerData)).ToArray();

	// Server stories addressed to one player that only touch that player's own client: notifications, contract and
	// undo history, and scenario actions (dialog, music, video, tutorial overlays). Saves, thumbnails, possession,
	// respawn and debug stories stay skipped.
	internal static readonly HashSet<string> ForwardedStoryOwners = new HashSet<string>(StringComparer.Ordinal)
	{
		"DiscoveriesServerSessionComponent", "ColonizationProgressServerSessionComponent", "EncounterNotificationSessionComponent",
		"StreamedContractsScreenSessionComponent", "UndoSessionComponent", "PlayDialogActionComponent", "PlayMusicActionComponent",
		"StopMusicActionComponent", "PlayVideoActionComponent", "SetUiTutorialActionComponent", "ClearUiTutorialActionComponent",
		"ShowOnboardingTooltipActionComponent", "OpenTerminalScreenActionComponent"
	};

	// Server stories that name no client (MoveTo.Client()) but answer whoever caused them: Keen runs them on the host's
	// own client, so the host saw a player's safe-zone refusals and the player saw nothing. Started inside a player's
	// story they go to that player (HostWorld.StoryActor), otherwise to the host as before.
	internal static readonly HashSet<string> ActorStories = new HashSet<string>(StringComparer.Ordinal)
	{
		"GameModeSessionComponent.SendSafeZoneProtectionNotification"
	};

	public static HostIdentities? Current { get; private set; }

	public static IdentityId Hotfix => InProcessPerPlayerDataSessionComponent.HotfixIdentity;

	public int KnownCount => _known.Count;

	private HostIdentities(IMpLog log, HostSettings settings)
	{
		_log = log;
		Settings = settings;
	}

	public static void Reset()
	{
		// Dedicated player data is restored before networking starts and survives network resets.
		if (!DedicatedRuntime.Headless) Current = null;
	}

	public static HostIdentities Install(IMpLog log, HostSettings settings) => Current ??= new HostIdentities(log, settings);

	public bool IsJoiner(long identity) => _known.ContainsKey(identity);

	public Result Register(Session server, ushort clientId, string key, string name)
	{
		Entity sessionComponents = server.SessionComponents;
		PlayersSessionComponent players = sessionComponents.TryGet<PlayersSessionComponent>(default(StringId)) ?? throw new InvalidOperationException("the server session has no PlayersSessionComponent");
		InProcessPerPlayerDataSessionComponent ppd = sessionComponents.TryGet<InProcessPerPlayerDataSessionComponent>() ?? throw new InvalidOperationException("the server session has no InProcessPerPlayerDataSessionComponent");
		OwnershipSessionComponent ownership = sessionComponents.TryGet<OwnershipSessionComponent>();
		long identity = PlayerIdentities.Derive(key, id => _known.TryGetValue(id, out string owner) && owner != null && owner != key);
		var identityId = new IdentityId(identity);
		// A null key marks an identity restored from the save at load that nobody has claimed yet.
		bool fromSave = _known.TryGetValue(identity, out string previous) && previous == null;
		_known[identity] = key;
		if (ownership != null)
		{
			ownership._identities.Add(Hotfix);
			ownership._identities.Add(identityId);
		}
		EnsureWrapper(ppd);
		var notes = new List<string>();
		if (fromSave)
		{
			notes.Add("per-player data from the save");
		}
		if (Settings.Research == ResearchMode.Shared)
		{
			notes.Add($"research shared with the host ({ShareResearch(ppd, identityId)} records)");
		}
		int created = PrecreateData(ppd, identityId);
		PlayerIdentity player;
		bool newPlayer;
		if (players.IsPlayerIdentity(identityId))
		{
			player = players.GetPlayerIdentity(identityId);
			PlayerData playerData = players.GetPlayerData(player);
			if (playerData.ClientId.Id != clientId)
			{
				throw new InvalidOperationException($"identity {identity} is registered for ClientId {playerData.ClientId.Id}, not {clientId}");
			}
			newPlayer = false;
		}
		else
		{
			if (players._clientIds.ContainsKey(new ClientId(clientId)))
			{
				throw new InvalidOperationException($"ClientId {clientId} already belongs to another player in this world session");
			}
			player = players.AllocatePlayer();
			players.PropagateCreatedPlayer(new PlayerData
			{
				ClientId = new ClientId(clientId),
				Player = player,
				Identity = identityId
			});
			newPlayer = true;
		}
		ToolbarPerPlayerData toolbar = ppd.GetPerPlayerData<ToolbarPerPlayerData>(identityId);
		if (StartingToolbar.IsBlank(toolbar))
		{
			var (startingToolbar, source) = StartingToolbar.For(server, identityId, sessionComponents.TryGet<PlayerToolbarServerSessionComponent>()?._config?.DefaultToolbar);
			if (startingToolbar != null)
			{
				startingToolbar.ResetToolbar(toolbar);
				notes.Add("starting toolbar (" + source + ")");
			}
		}
		PlayerCharacterData character = ppd.GetPerPlayerData<PlayerCharacterData>(identityId);
		if (!string.IsNullOrWhiteSpace(name))
		{
			character.PlayerName = name;
		}
		if (Settings.Grids == GridPermissionMode.Team)
		{
			RespawnPerPlayerData respawn = ppd.GetPerPlayerData<RespawnPerPlayerData>(identityId);
			if (respawn.KnownPoints.Count == 0)
			{
				List<Entity> hostPoints = ppd.GetPerPlayerData<RespawnPerPlayerData>(Hotfix).KnownPoints;
				respawn.KnownPoints.AddRange(hostPoints);
				if (hostPoints.Count > 0)
				{
					notes.Add($"{hostPoints.Count} respawn point(s) of the host known");
				}
			}
		}
		if (Settings.Research == ResearchMode.Shared && SharedDiscoveries.CatchUp(server, ppd, identityId) is > 0 and int shared)
		{
			notes.Add($"{shared} discover{(shared == 1 ? "y" : "ies")} of other players shared");
		}
		notes.Add($"{created} per-player record(s) created");
		Registered++;
		return new Result(identity, player._id, newPlayer, string.Join(", ", notes));
	}

	internal static int PrecreateData(InProcessPerPlayerDataSessionComponent ppd, IdentityId identity)
	{
		int count = (((CollectionDictionary<IdentityId, List<object>, object>)(object)ppd._perPlayerData).TryGet(identity, out List<object> source) ? source.Select(o => o.GetType()).ToHashSet() : new HashSet<Type>()).Count;
		ppd.GetPerPlayerData<PaintToolPerPlayerData>(identity);
		ppd.GetPerPlayerData<ToolbarPerPlayerData>(identity);
		ppd.GetPerPlayerData<GPSMarkersData>(identity);
		ppd.GetPerPlayerData<DiscoveriesPlayerData>(identity);
		ppd.GetPerPlayerData<ContractPerPlayerData>(identity);
		ppd.GetPerPlayerData<PlayerProgressionData>(identity);
		ppd.GetPerPlayerData<ColonizationProgressData>(identity);
		ppd.GetPerPlayerData<CameraStatePerPlayerData>(identity);
		ppd.GetPerPlayerData<PlayerCharacterData>(identity);
		ppd.GetPerPlayerData<IntegrityToolPerPlayerData>(identity);
		ppd.GetPerPlayerData<BlockPlacerPerPlayerData>(identity);
		ppd.GetPerPlayerData<VoxelHandData>(identity);
		ppd.GetPerPlayerData<PlayerCreditsData>(identity);
		// The joiner's own client reads it when its character dies (death analytics); the host only creates it on respawn.
		ppd.GetPerPlayerData<LastDeathPerPlayerData>(identity);
		// Anything else the host has (mod or DLC data) is created for the joiner too, through the generic getter.
		if (((CollectionDictionary<IdentityId, List<object>, object>)(object)ppd._perPlayerData).TryGet(Hotfix, out List<object> hostData))
		{
			MethodInfo getPerPlayerData = typeof(InProcessPerPlayerDataSessionComponent).GetMethod("GetPerPlayerData");
			foreach (Type type in hostData.Select(o => o.GetType()).Distinct().ToList())
			{
				if (typeof(PerPlayerData).IsAssignableFrom(type) && (!((CollectionDictionary<IdentityId, List<object>, object>)(object)ppd._perPlayerData).TryGet(identity, out List<object> joinerData) || !joinerData.Any(type.IsInstanceOfType)))
				{
					getPerPlayerData.MakeGenericMethod(type).Invoke(ppd, new object[] { identity });
				}
			}
		}
		return (((CollectionDictionary<IdentityId, List<object>, object>)(object)ppd._perPlayerData).TryGet(identity, out List<object> after) ? after.Count : 0) - count;
	}

	internal static int ShareResearch(InProcessPerPlayerDataSessionComponent ppd, IdentityId identity)
	{
		return Share<PlayerProgressionData>(ppd, identity) + Share<ColonizationProgressData>(ppd, identity) + Share<ContractPerPlayerData>(ppd, identity) + Share<PlayerCreditsData>(ppd, identity);
	}

	private static int Share<T>(InProcessPerPlayerDataSessionComponent ppd, IdentityId identity) where T : PerPlayerData
	{
		// The joiner's collection holds the host's own record object, so both read and write the same progress.
		T shared = ppd.GetPerPlayerData<T>(Hotfix);
		List<object> records = ppd.GetOrCreateCollectionForIdentity(identity);
		int index = records.FindIndex(o => o is T);
		if (index >= 0 && records[index] == (object)shared)
		{
			return 0;
		}
		if (index >= 0)
		{
			PerPlayerData replaced = (PerPlayerData)records[index];
			records[index] = shared;
			ppd._clientVisibleData.Data.FirstOrDefault(d => (long)d.IdentityId == (long)identity)?.Data.Remove(replaced);
		}
		else
		{
			records.Add(shared);
		}
		return 1;
	}

	internal bool EnsureWrapper(InProcessPerPlayerDataSessionComponent ppd)
	{
		INpcIdentityProvider npc = ppd._npc;
		if (npc is JoinerAwareNpcProvider)
		{
			return true;
		}
		if (npc == null)
		{
			return false;
		}
		Unsafe.AsRef(in ppd._npc) = (INpcIdentityProvider)(object)new JoinerAwareNpcProvider(npc, IsJoiner);
		_log.Info("host identities: per-player data keeps joiner identities separate from now on (NPC provider wrapped)");
		return true;
	}

	public static (int Pcu, int Max)? ReadPcu(Session server, long identity)
	{
		PCUSessionComponent pcu = server.SessionComponents.TryGet<PCUSessionComponent>();
		if (pcu == null)
		{
			return null;
		}
		return (pcu.GetPCU(new IdentityId(identity)), pcu.MaxPerPlayerPCU);
	}

	public static void InitPrefix(PerPlayerDataSessionComponentObjectBuilder ob, out List<(IdentityId Id, List<object> Items)>? __state)
	{
		__state = null;
		HostIdentities current = Current;
		if (current == null || !current.Settings.Identities)
		{
			return;
		}
		try
		{
			// Take the joiners' records out of the builder, or the game folds every identity's data into the host's.
			foreach (KeyValuePair<IdentityId, List<object>> entry in (CollectionDictionary<IdentityId, List<object>, object>)(object)ob.PerPlayerData)
			{
				long id = entry.Key;
				if (id != PlayerIdentities.HotfixIdentity && !PlayerIdentities.IsNpc(id))
				{
					(__state ??= new List<(IdentityId, List<object>)>()).Add((entry.Key, new List<object>(entry.Value)));
				}
			}
			if (__state == null)
			{
				return;
			}
			foreach (var kept in __state)
			{
				IdentityId id = kept.Id;
				((CollectionDictionary<IdentityId, List<object>, object>)(object)ob.PerPlayerData).Remove(id);
			}
		}
		catch (Exception ex)
		{
			__state = null;
			current._log.Error($"host identities: keeping joiners' saved per-player data failed at load ({ex.GetType().Name}: {ex.Message}); the game folds it into the host's as without the plugin");
		}
	}

	public static void InitPostfix(InProcessPerPlayerDataSessionComponent __instance, List<(IdentityId Id, List<object> Items)>? __state)
	{
		HostIdentities current = Current;
		if (current == null || __state == null)
		{
			return;
		}
		try
		{
			var restored = new List<string>();
			foreach (var entry in __state)
			{
				var (id, items) = entry;
				current._known.TryAdd(id, null);
				List<object> records = __instance.GetOrCreateCollectionForIdentity(id);
				InProcessPerPlayerDataBaseSessionComponent.ClientVisiblePerPlayerData visible = __instance._clientVisibleData.Data.First(d => (long)d.IdentityId == (long)id);
				foreach (object record in items)
				{
					records.Add(record);
					__instance.ObserveAndReplicateData(record, visible);
				}
				restored.Add($"{id} ({items.Count} records)");
			}
			current.RestoredAtLoad += __state.Count;
			bool wrapped = current.EnsureWrapper(__instance);
			current._log.Info($"host identities: per-player data of {__state.Count} joiner identit{(__state.Count == 1 ? "y" : "ies")} kept at world load: {string.Join(", ", restored)}" + (wrapped ? "" : " (NPC provider not injected yet; wrapped at the first registration)"));
		}
		catch (Exception ex)
		{
			current._log.Error($"host identities: restoring joiners' saved per-player data failed ({ex.GetType().Name}: {ex.Message}); they start fresh");
		}
	}

	public static bool MoveToClientPrefix(NetworkStoryContext context, IAsyncStateMachine stateMachine, ClientRequest client)
	{
		HostIdentities current = Current;
		if (current == null || !current.Settings.Identities)
		{
			return true;
		}
		int id = client.ClientId.Id;
		bool byActor = false;
		if (id == 0 && HostWorld.StoryActor > 1 && ActorStories.Contains(StoryState.Name(ObjectCodec.TypeName(stateMachine.GetType()))))
		{
			id = HostWorld.StoryActor;
			byActor = true;
		}
		if (id <= 1)
		{
			return true;
		}
		string story = "?";
		try
		{
			story = StoryState.Name(ObjectCodec.TypeName(stateMachine.GetType()));
			if ((byActor || ForwardedStoryOwners.Contains(stateMachine.GetType().DeclaringType?.Name ?? "")) && HostWorld.Current?.ForwardServerStory((ushort)id, stateMachine) == true)
			{
				current.StoriesForwarded++;
			}
			else
			{
				current.StoriesSkipped++;
				if (current._skippedStories.AddOrUpdate(story, 1L, (string _, long c) => c + 1) == 1)
				{
					current._log.Info($"host identities: server story {story} addressed to ClientId {id} skipped (stories the server starts for a joiner cannot run on its PC yet; Keen would have run it on the host's own client)");
				}
			}
			if (!context.Skipped)
			{
				context.SkipStory();
			}
		}
		catch (Exception ex)
		{
			current._log.WarnOnce("host-skip-" + story, $"host identities: skipping server story {story} for ClientId {id} threw {ex.GetType().Name}: {ex.Message}");
		}
		return false;
	}

	public string Describe()
	{
		return $"identities: {Settings}; registered {Registered}, joiner identities {_known.Count} ({RestoredAtLoad} restored at load), server stories for joiners forwarded {StoriesForwarded}, skipped {StoriesSkipped}" + (_skippedStories.IsEmpty ? "" : " (" + string.Join(", ", from kv in _skippedStories.OrderByDescending(kv => kv.Value).Take(6) select $"{kv.Key} x{kv.Value}") + ")");
	}
}
