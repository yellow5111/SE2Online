using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Keen.Game2.Game.SessionComponents;
using Keen.Game2.Simulation.GameSystems.Blueprints;
using Keen.Game2.Simulation.GameSystems.Identities;
using Keen.Game2.Simulation.GameSystems.Interaction;
using Keen.Game2.Simulation.GameSystems.Ownership;
using Keen.Game2.Simulation.GameSystems.Permissions;
using Keen.Game2.Simulation.RuntimeSystems.Saves;
using Keen.Game2.Simulation.WorldObjects.Characters;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.Pilotable;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.DCS.Components;
using Keen.VRage.DCS.ObjectBuilders;
using Keen.VRage.Library.Definitions;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Library.Utils;
using Keen.VRage.Multiplayer;
using Keen.VRage.Multiplayer.Replications.FailureStrategies;
using Y51_SE2Online.Core;
using Y51_SE2Online.SyncEngine;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>
/// Player requests SE2 4.0 runs on the server without checking who sends them: spawning an entity from a builder the
/// player wrote, moving every item between two inventories the player names, serializing any grid, saving the server's
/// world, and handing entities without an owner to whichever identity the world lists first. In an online world each
/// is now held to what the game's own client sends. Every check logs its refusals and has its own kill switch
/// (-y51online:access-off:&lt;check&gt;). Admins are exempt (StoryRefusal returns before these for them).
/// </summary>
internal static partial class ServerAccess
{
	internal static readonly bool SeatOff = Off("seats");
	private static readonly bool SpawnOff = Off("spawn requests"), CopyOff = Off("grid copies"), SaveOff = Off("save requests"), DepositOff = Off("deposit all"), OwnerOff = Off("default owner");

	private const long AutosaveEveryMs = 300_000;
	private static readonly ConcurrentDictionary<long, long> _lastAutosave = new();

	internal static void InstallRequests(Harmony harmony)
	{
		if (!DepositOff)
		{
			Patch(harmony, "deposit all", AccessTools.Method(typeof(InProcessGameServerSessionComponent), "TryDepositAllNonEssentialItemsImpl"), prefix: nameof(DepositPrefix));
		}
		if (!OwnerOff) Patch(harmony, "default owner", AccessTools.Method(typeof(OwnershipSessionComponent), nameof(OwnershipSessionComponent.GetDefaultOwner)), prefix: nameof(DefaultOwnerPrefix));
		Patch(harmony, "refused seats", AccessTools.Method(typeof(SeatComponent), nameof(SeatComponent.CanSeat)), prefix: nameof(CanSeatPrefix));
	}

	// A refused seat story still runs and answers false, as for a seat that is taken: the player's game then undoes its
	// own seating. A faulted one left the player's seat action in progress for good (no seat could be used again).
	private static readonly ConcurrentDictionary<(SeatComponent Seat, Entity? Pilot), long> _refusedSeats = new();

	/// <summary>True when the refused <paramref name="story"/> was turned into one that answers false.</summary>
	internal static bool AnswersFalse(IAsyncStateMachine story)
	{
		var type = story.GetType();
		if (type.DeclaringType != typeof(SeatComponent) || Field<SeatComponent>(story, "<>4__this") is not { } seat) return false;
		switch (StoryName(type))
		{
			case "SetPilotStory":
				long now = MpClock.NowMs();
				foreach (var (key, at) in _refusedSeats)
					if (now - at > 5000) _refusedSeats.TryRemove(key, out _);
				_refusedSeats[(seat, Field<SkipOnReplicationFail<Entity>>(story, "pilot").Value)] = now;
				return true;
			case "TryClearPilotStory":
				// The seat's own entity is never its pilot: the story answers false and clears nothing.
				AccessTools.Field(type, "pilot").SetValue(story, new SkipOnReplicationFail<Entity>(seat.Entity));
				return true;
			default:
				return false;
		}
	}

	private static bool CanSeatPrefix(SeatComponent __instance, Entity pilot, ref bool __result)
	{
		if (_refusedSeats.IsEmpty || !_refusedSeats.TryRemove((__instance, pilot), out long at) || MpClock.NowMs() - at > 5000) return true;
		__result = false;
		return false;
	}

	// The player whose signals are being replayed on the server (Y51PeerTransport around its signal replay).
	[ThreadStatic] private static ClientId? _signalClient;

	internal static ClientId? EnterSignal(ClientId client)
	{
		var before = _signalClient;
		_signalClient = client;
		return before;
	}

	internal static void LeaveSignal(ClientId? before) => _signalClient = before;

	/// <summary>The checks a player's story gets past the generic ones, by story; null lets it run.</summary>
	private static string? RequestRefusal(ClientId sender, long caller, IAsyncStateMachine story)
	{
		var type = story.GetType();
		var owner = type.DeclaringType;
		string name = StoryName(type);
		if (owner == typeof(InProcessGameServerSessionComponent))
		{
			if (name == "RequestSpawnEntity" && !SpawnOff) return SpawnRefusal(sender, caller, Field<EntityObjectBuilder>(story, "entityOb"));
			// The game's client never sends these (its own wrapper answers "not the host"): only an admin saves the server's world.
			if (name is "TrySaveGame" or "TrySaveVariant" && !SaveOff) return "saving the server's world";
		}
		if (owner == typeof(SaveSessionComponent) && name == "RequestAutosaveAsync" && !SaveOff)
		{
			long now = MpClock.NowMs();
			if (_lastAutosave.TryGetValue(caller, out long last) && now - last < AutosaveEveryMs) return $"an autosave {(now - last) / 1000} s after the last one it asked for (one per {AutosaveEveryMs / 60_000} min)";
			_lastAutosave[caller] = now;
			return null;
		}
		// Taking someone out of a seat needs the seat's use access, as sitting down does (SeatGuard, at CanSeat); the game
		// checks only that the seat is taken. Getting out of a seat oneself is always allowed.
		if (owner == typeof(SeatComponent) && name == "TryClearPilotStory" && !SeatOff && Field<SeatComponent>(story, "<>4__this") is { } seat)
		{
			var pilot = Field<SkipOnReplicationFail<Entity>>(story, "pilot").Value;
			bool leaving = pilot != null && TeamPermissions.OwnerOf(pilot) == caller;
			if (!leaving && !MayUse(null, new IdentityId(caller), seat._block.Entity)) return "a seat the player may not use";
		}
		if (owner == typeof(BlueprintHelpers) && name == "SerializeGrid" && !CopyOff)
		{
			var grid = Field<SkipOnReplicationFail<CubeGridComponent>>(story, "grid").Value?.Entity;
			var permissions = MpPlugin.App?.ServerSession?.SessionComponents.TryGet<PermissionsSessionComponent>();
			// The same check the game's own copy story (TryCopy) makes.
			if (grid != null && permissions != null && !permissions.CanCopy(grid, new IdentityId(caller))) return "copying a grid the player may not copy";
		}
		return null;
	}

	private static T? Field<T>(IAsyncStateMachine story, string name) => AccessTools.Field(story.GetType(), name)?.GetValue(story) is T value ? value : default;

	// The only builder the game's client spawns through the server is its own spectator (SpectatingModesSessionComponent),
	// owned by its player: anything else, or one with entities under it, is refused, and the owner is the sender.
	private static string? SpawnRefusal(ClientId sender, long caller, EntityObjectBuilder? builder)
	{
		if (builder == null) return "an empty spawn request";
		var spectators = Singleton<DefinitionManager>.Instance.GetDefinitionsOfType<Keen.Game2.Client.WorldObjects.Spectators.SpectatingModesComponentDefinition>()
			.Select(d => d.SpectatorPrefab?.Get()?.Definition).Where(d => d != null);
		if (!spectators.Any(d => ReferenceEquals(d, builder.Definition))) return $"spawning {builder.Definition?.ToString() ?? "an entity"} (players may spawn only their spectator)";
		if (builder.TryGetDirectChilds().Length > 0) return "a spectator with entities under it";
		OwnershipSessionComponent.ChangeOwnership(builder, new IdentityId(caller));
		return null;
	}

	// Moving every non-essential item (the conveyor "deposit all"): from the player's own character into an inventory the
	// player may use and stands at, as the game's use action sends it.
	private static bool DepositPrefix(SkipOnReplicationFail<Entity> fromInventoryHolder, SkipOnReplicationFail<Entity> toInventoryHolder)
	{
		if (!Enforcing || _signalClient is not { } client || client == ClientId.Server) return true;
		long caller = (long)IdentityOf(client);
		if (IsAdminIdentity(caller)) return true;
		Entity? from = fromInventoryHolder.Value, to = toInventoryHolder.Value;
		string? refused = from == null || to == null ? "an inventory that is gone"
			: from.TryGet<CharacterLifeComponent>() is not { IsAlive: true } || TeamPermissions.OwnerOf(from) != caller ? "items out of someone else's inventory"
			: IsForeignCharacter(to, caller) || !MayUse(null, new IdentityId(caller), to) ? "items into an inventory the player may not use"
			: TooFar(from, to) ? "items into an inventory out of reach" : null;
		if (refused == null) return true;
		Refused++;
		if (LogDue(client, "deposit")) GameLog.Instance.Info($"access: refused ClientId {client.Id}'s deposit all ({refused}; further ones within 5 s not logged)");
		return false;
	}

	private static bool TooFar(Entity interactor, Entity interacted)
	{
		float max = Singleton<DefinitionManager>.Instance.GetConfiguration<InteractionConfiguration>().MaxInteractionDistance;
		var (a, b, extension) = InteractionBoundsEstimator.GetInteractionData(interactor, interacted, includeInteractorBounds: true, includeInteractedBounds: true);
		return Vector3D.DistanceSquared(in a, in b) > (double)((max + extension) * (max + extension));
	}

	// An entity created without an owner went to the first identity the world lists (on a dedicated server an identity no
	// player has or whoever joined first). Online it goes to the player whose request made it. The server's own (no
	// request) keeps the game's choice on a co-op host, whose host owns the world, and goes to nobody on a dedicated server.
	private static bool DefaultOwnerPrefix(IdentitiesSessionComponent ____identities, ref IdentityId __result)
	{
		if (!Enforcing) return true;
		var client = _storyClient ?? _signalClient;
		if (client is not { } who) { if (!Y51_SE2Online.Dedicated.DedicatedRuntime.Requested) return true; __result = default; }
		else __result = who == ClientId.Server ? ____identities.LocalIdentity : IdentityOf(who);
		if (System.Threading.Interlocked.Increment(ref _defaultOwnersLogged) <= 3)
			GameLog.Instance.Info($"access: an entity without an owner goes to {((long)__result == 0 ? "nobody" : $"identity {(long)__result}, whose request made it")}, not to the first identity of the world (first {Math.Min(3, _defaultOwnersLogged)} logged)");
		return false;
	}

	private static long _defaultOwnersLogged;
}
