using System;
using System.Linq;
using HarmonyLib;
using Keen.Game2.Simulation.GameSystems.Identities;
using Keen.Game2.Simulation.GameSystems.Ownership;
using Keen.Game2.Simulation.GameSystems.Permissions;
using Keen.Game2.Simulation.GameSystems.Respawn;
using Keen.Game2.Simulation.GameSystems.Player;
using Keen.VRage.Core.Game.Data;

using Keen.Game2.Simulation.RuntimeSystems.Validators;
using Keen.Game2.Simulation.StreamedUI;
using Keen.Game2.Simulation.StreamedUI.Respawn;
using Keen.Game2.Simulation.StreamedUI.Terminal.ControlPanel;
using Keen.Game2.Simulation.WorldObjects.Characters;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Definitions;
using Keen.VRage.Library.Utils;
using Keen.VRage.Multiplayer;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;
using Y51_SE2Online.SyncEngine;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>
/// SE2 4.0's server never checks access for its terminal screens: opening a block's control panel, its inventory or its
/// production queue, moving items between inventories and binding to a medbay all validate only that the caller is a
/// player (an empty check), so SE2 Online's "access denied" showed in the UI while the action still went through. The
/// server now refuses them with the same rules the UI shows: grid sharing, factions, block allow and deny lists.
/// </summary>
internal static partial class ServerAccess
{
	public static long Refused;

	public static void Install(Harmony harmony)
	{
		// Each check on its own, so a game update that renames one target only turns that check off.
		Patch(harmony, "terminal screens", AccessTools.Method(typeof(StreamedBaseSessionComponent), "AsEntity"), postfix: nameof(AsEntityPostfix));
		Patch(harmony, "item transfers", AccessTools.Method(typeof(CommonValidators), nameof(CommonValidators.ValidateInteractedEntities)), prefix: nameof(InteractedPrefix));
		Patch(harmony, "control panel", AccessTools.Method(typeof(StreamedControlPanelSessionComponent), nameof(StreamedControlPanelSessionComponent.Attach),
			new[] { typeof(Entity), typeof(ControlPanelListModel), typeof(ReplicationEventContext) }), prefix: nameof(ControlPanelPrefix));
		Patch(harmony, "medbays", AccessTools.Method(typeof(RespawnSessionComponent), nameof(RespawnSessionComponent.ActivateRespawnPoint)), prefix: nameof(ActivateRespawnPrefix), postfix: nameof(ActivateRespawnPostfix));
		Patch(harmony, "respawn requests (home, closest)", AccessTools.Method(typeof(RespawnSessionComponent), "TryRespawnAtType"), prefix: nameof(RespawnAtTypePrefix), finalizer: nameof(RespawnAtTypeFinalizer));
		Patch(harmony, "automatic home base", AccessTools.Method(typeof(RespawnPointTrackerSessionComponent), "AddRespawnPoint"), prefix: nameof(AutoHomePrefix), postfix: nameof(AutoHomePostfix));
		var toolbar = typeof(Keen.Game2.Simulation.WorldObjects.CubeBlocks.Toolbar.BlockToolbarServerComponent);
		Patch(harmony, "button panels", System.Linq.Enumerable.FirstOrDefault(AccessTools.GetDeclaredMethods(toolbar), m => m.Name == "ActivateTile" && m.GetParameters().Length == 3), prefix: nameof(ToolbarPrefix));
		// A press arrives through the one-argument overload, which the JIT inlines the three-argument one into.
		Patch(harmony, "button panel presses", System.Linq.Enumerable.FirstOrDefault(AccessTools.GetDeclaredMethods(toolbar), m => m.Name == "ActivateTile" && m.GetParameters().Length == 1), prefix: nameof(ToolbarPrefix));
		Patch(harmony, "button panel edits", AccessTools.Method(toolbar, "ClearTile"), prefix: nameof(ToolbarPrefix));
		foreach (var story in ToolbarStories())
			Patch(harmony, "button panel requests", AccessTools.Method(story, "MoveNext"), prefix: nameof(ToolbarStoryPrefix), finalizer: nameof(ToolbarStoryFinalizer));
		Patch(harmony, "story caller (host's own)", AccessTools.Method(typeof(Keen.VRage.Multiplayer.Transport.InProcess.InProcessPeerTransportSessionComponent), "HostStory"),
			prefix: nameof(InProcessStoryPrefix), finalizer: nameof(StoryFinalizer));
		// The game's admin check answers yes for everyone. Both validators are patched, not only HasAdminAccess: a one-line
		// method like that can be inlined into its callers, where a patch never runs. The validators are called through
		// delegates (MoveTo.Server(ValidateAdminAccess)), so their patches always run.
		Patch(harmony, "admin tools", AccessTools.Method(typeof(Keen.VRage.Game.Utilities.CoreValidators), nameof(Keen.VRage.Game.Utilities.CoreValidators.HasAdminAccess)), prefix: nameof(AdminPrefix));
		Patch(harmony, "admin tools (game validator)", AccessTools.Method(typeof(Keen.VRage.Game.Utilities.CoreValidators), nameof(Keen.VRage.Game.Utilities.CoreValidators.ValidateAdminAccess)), prefix: nameof(ValidateAdminPrefix));
		Patch(harmony, "admin tools (simulation validator)", AccessTools.Method(typeof(CommonValidators), nameof(CommonValidators.ValidateAdminAccess)), prefix: nameof(ValidateAdminPrefix));
		Patch(harmony, "admin tools (simulation check)", AccessTools.Method(typeof(CommonValidators), nameof(CommonValidators.HasAdminAccess)), prefix: nameof(AdminPrefix));
		var inventories = typeof(Keen.Game2.Simulation.StreamedUI.Terminal.Inventories.StreamedInventorySessionComponent);
		var inventoryList = typeof(Keen.Game2.Simulation.StreamedUI.Terminal.Inventories.InventoryListModelStreamed);
		Patch(harmony, "inventory lists", AccessTools.Method(inventories, "Attach", new[] { typeof(Entity), inventoryList, typeof(ReplicationEventContext) }), prefix: nameof(InventoryListPrefix));
		Patch(harmony, "inventory list entries", AccessTools.Method(inventories, "AddEntity"), prefix: nameof(InventoryEntryPrefix));
		Patch(harmony, "mission ownership", AccessTools.Method(typeof(Keen.Game2.Simulation.GameSystems.Missions.Triggers.Actions.SetEntityOwnershipActionComponent), "Activate"), prefix: nameof(MissionOwnerPrefix));
		InstallRequests(harmony);
	}

	// A mission step that hands a ship or crate "to the player" gives it to SE2's first player identity: the co-op host, or
	// on a dedicated server the server's own identity, which no player is, so nobody could use what the quest gave out.
	// In an online world it goes to the player who set the step off, else to the first player who is connected.
	private static bool MissionOwnerPrefix(Keen.Game2.Simulation.GameSystems.Missions.Triggers.ITriggerContext context,
		Keen.Game2.Simulation.GameSystems.Missions.Triggers.Actions.SetEntityOwnershipActionObjectBuilder ____ob,
		Keen.Game2.Simulation.GameSystems.Missions.MissionSessionComponent ____missions, IdentitiesSessionComponent ____identities, OwnershipSessionComponent ____ownership)
	{
		if (!Enforcing || ____ob.TargetOwnership != Keen.Game2.Simulation.GameSystems.Missions.Triggers.Actions.SetEntityOwnershipActionComponent.OwnershipType.Player) return true;
		var entity = ____missions.TryGetEntityWithId(____ob.EntityId);
		var ownership = entity?.TryGet<OwnershipComponent>();
		if (entity == null || ownership == null) return true;
		IdentityId who = default;
		if (context.TryGetValue("TriggeringEntity") is Entity trigger && trigger.TryGet<OwnershipComponent>()?.Owner is { } by && ____identities.TryGetClientFromIdentity(by) != null) who = by;
		if (!who.IsValid()) who = ____identities.GetPlayerIdentityIds().FirstOrDefault(i => ____identities.TryGetClientFromIdentity(i) != null);
		if (!who.IsValid()) return true;
		GameLog.Instance.Info($"access: mission gives {entity.GetComposition()?.DebugNameShort} to player identity {(long)who}");
		if ((long)ownership.Owner == (long)who || !____ownership.TryTransferOwnership(entity, who) || !____ob.ApplyToSubgrids) return false;
		if (entity.TryGet<CubeGridComponent>() is { } grid)
			foreach (var sub in grid.GridGraphHandler.GetAllConnectedEntities(entity))
				if (sub != entity) ____ownership.TryTransferOwnership(sub, who);
		return false;
	}

	private static void Patch(Harmony harmony, string what, System.Reflection.MethodBase? target, string? prefix = null, string? postfix = null, string? finalizer = null)
	{
		try
		{
			if (target == null) throw new MissingMethodException(what);
			harmony.Patch(target, prefix: prefix == null ? null : new HarmonyMethod(typeof(ServerAccess), prefix), postfix: postfix == null ? null : new HarmonyMethod(typeof(ServerAccess), postfix),
				finalizer: finalizer == null ? null : new HarmonyMethod(typeof(ServerAccess), finalizer));
		}
		catch (Exception ex) { GameLog.Instance.Warn($"access: {what} check not installed ({ex.GetType().Name}: {ex.Message})"); }
	}

	// Keen runs a client's story on the server inside that client's transport; stories declared [Server(null)] get no
	// validator and cannot see their caller, so the caller is kept here while one runs.
	[ThreadStatic] private static Keen.VRage.Multiplayer.ClientId? _storyClient;

	// The client started this story (it is not one of the server's own coming back from that client).
	[ThreadStatic] private static bool _hostingStory;

	// The player's story running now, once restored: the game admin tool it uses is named after it (game.admin.*).
	[ThreadStatic] private static Type? _storyType;

	/// <summary>
	/// Kill switch for one check, for a server whose players hit a false refusal: -y51online:access-off:&lt;check&gt;.
	/// </summary>
	internal static bool Off(string check)
	{
		bool off = MpPlugin.LaunchArgs.Any(a => a.Equals(MpOptions.Prefix + "access-off:" + check, StringComparison.OrdinalIgnoreCase));
		if (off) GameLog.Instance.Info($"access: the {check} check is off (launch option)");
		return off;
	}

	private static readonly bool ReturningStoriesOff = Off("returning stories"), ToolbarUnknownOff = Off("toolbar callers");

	/// <summary>
	/// A story this side moved to the peer may come back under its id only as the same state machine: the peer swapping in
	/// another story there would get it run without the checks a story the peer starts goes through. Both transports.
	/// </summary>
	internal static Exception? ReturningStoryRefusal(Keen.VRage.Multiplayer.ClientId peer, Type? sent, string backTypeName)
	{
		if (ReturningStoriesOff || sent == null) return null;
		// Compared by name, as SE2 Online writes it (AssemblyQualifiedName): a name the peer chose is never resolved here.
		if (!ReturningStories.Refuses(sent.AssemblyQualifiedName, backTypeName)) return null;
		Refused++;
		if (LogDue(peer, "returning-story")) GameLog.Instance.Info($"access: refused ClientId {peer.Id}'s answer to {sent.DeclaringType?.Name}.{StoryName(sent)}: it came back as another story ({backTypeName}; further ones within 5 s not logged)");
		// A cancellation, not a fault: the server's own story usually runs unawaited, and the game's monitor for those sends
		// any other exception to the crash handler, so a refused answer closed the server.
		return new System.Threading.Tasks.TaskCanceledException("Access denied: the answer is another story");
	}

	/// <summary>
	/// SE2 Online's own transport hosts a player's story itself (not through the game's ProcessStory): the same caller
	/// and argument checks apply while it is restored and runs its server part.
	/// </summary>
	internal static (Keen.VRage.Multiplayer.ClientId? Client, bool Hosting, Type? Story) EnterStory(Keen.VRage.Multiplayer.ClientId client)
	{
		var before = (_storyClient, _hostingStory, _storyType);
		_storyClient = client;
		_hostingStory = true;
		_storyType = null;
		return before;
	}

	internal static void LeaveStory((Keen.VRage.Multiplayer.ClientId? Client, bool Hosting, Type? Story) before) => (_storyClient, _hostingStory, _storyType) = before;

	internal static void CheckRestoredStory(Keen.VRage.Multiplayer.ClientId sender, ref System.Runtime.CompilerServices.IAsyncStateMachine machine) => RestoredStoryPostfix(sender, ref machine);

	// A story arrives with whatever arguments the client wrote, and SE2 4.0 checks at most the story's own component: never
	// whose character an argument is (seating, unseating or feeding someone else's character) nor which identity an
	// ownership transfer names (taking any grid). 0.15.3 refused both on its own story path. A refused story is swapped
	// for one that faults, so the client's request fails the way a failed validator fails it.
	private static void RestoredStoryPostfix(Keen.VRage.Multiplayer.ClientId sender, ref System.Runtime.CompilerServices.IAsyncStateMachine __result)
	{
		// For everyone, admins too: GetTile would fault on the missing block. Only here, after the transport completed the
		// server request in the restored machine (it writes into the machine's own fields).
		if (__result != null && sender != Keen.VRage.Multiplayer.ClientId.Server && ToolbarRequests.NamesNoHintBlock(__result))
		{
			__result = new CancelledStory("a request naming an entity the player's game could not name");
			return;
		}
		if (__result != null && _hostingStory) _storyType = __result.GetType();
		if (!Enforcing || !_hostingStory || sender == Keen.VRage.Multiplayer.ClientId.Server || __result == null) return;
		if (BlockUseRefused(sender, __result) || ToolbarUseRefused(sender, ref __result)) return;
		string? refused;
		try { refused = StoryRefusal(sender, __result); }
		catch (Exception ex)
		{
			GameLog.Instance.WarnOnce("story-check-failed", $"access: could not check a story ({ex.GetType().Name}: {ex.Message}); allowed");
			return;
		}
		if (refused == null) return;
		Refused++;
		if (LogDue(sender, "story")) GameLog.Instance.Info($"access: refused ClientId {sender.Id}'s {__result.GetType().DeclaringType?.Name}.{StoryName(__result.GetType())} ({refused}; further ones within 5 s not logged)");
		if (!AnswersFalse(__result)) __result = new RefusedStory(refused);
	}

	private static string? StoryRefusal(Keen.VRage.Multiplayer.ClientId sender, System.Runtime.CompilerServices.IAsyncStateMachine story)
	{
		long caller = (long)IdentityOf(sender);
		var type = story.GetType();
		// Building, removing, copying and giving grids away are allowed by default; a group can be denied them.
		if (AdminRules.RequestKey(type.DeclaringType?.Name, StoryName(type)) is { } key && !HasClient(sender, key, unknownAllowed: true)) return "not allowed for the player's group (" + key + ")";
		if (PasteOnce.PasteBlocks(story) is int blocks and > 0)
		{
			if (!HasClient(sender, "blueprint.paste", unknownAllowed: true)) return "not allowed for the player's group (blueprint.paste)";
			double max = AdminAccess.Host is { } h && SteamOfCaller(h, sender) is var steam && steam != 0 ? h.Perms.Value(steam, "blueprint.max_blocks") : -1;
			if (max >= 0 && blocks > max) return $"a blueprint of {blocks} blocks (the player's group may paste at most {max:0})";
		}
		if (!HasClient(sender, "bypass.paste_once") && PasteOnce.HostGate((ushort)sender.Id, story) is { } paste) return paste;
		if (!HasClient(sender, "bypass.requests") && RequestRefusal(sender, caller, story) is { } request) return request;
		var arguments = StoryArguments.GetValue(type, Arguments);
		if (arguments.Initiator?.GetValue(story) is IdentityId initiator && (long)initiator != caller && !HasClient(sender, "bypass.ownership_initiator")) return "an ownership transfer in another player's name";
		if (HasClient(sender, "bypass.foreign_character")) return null;
		foreach (var field in arguments.Entities)
		{
			Entity? named = field.GetValue(story) switch
			{
				Entity e => e,
				Component c => c.Entity,
				Keen.VRage.Multiplayer.Replications.FailureStrategies.SkipOnReplicationFail<Entity> s => s.Value,
				Keen.VRage.Multiplayer.Replications.FailureStrategies.NullOnReplicationFail<Entity> n => n.Value,
				Keen.VRage.Multiplayer.Replications.FailureStrategies.NullOnAnyReplicationFail<Entity> n => n.Value,
				Keen.VRage.Multiplayer.Replications.FailureStrategies.ThrowOnReplicationFail<Entity> t => t.Value,
				_ => null,
			};
			if (named != null && IsForeignCharacter(named, caller)) return "another player's character";
		}
		return null;
	}

	/// <summary>Whether the player behind a connection has a permission: by ClientId, not identity, because with identities
	/// off every player shares the host's identity. The server's own requests (no player) have none.</summary>
	internal static bool HasClient(Keen.VRage.Multiplayer.ClientId client, string key, bool unknownAllowed = false)
	{
		var host = AdminAccess.Host;
		if (host == null || client == Keen.VRage.Multiplayer.ClientId.Server) return unknownAllowed;
		ulong steam = SteamOfCaller(host, client);
		return steam == 0 ? unknownAllowed : host.Allowed(steam, key);
	}

	/// <summary>
	/// The Steam account behind a request. The game's ClientId numbers remote connections for the whole process and changes
	/// on every reconnect (SE2 Online's own ClientId is another number), so a remote player is found through their
	/// connection; the co-op host's own game is the in-process client (ClientId 1). 0 when it is neither.
	/// </summary>
	internal static ulong SteamOfCaller(HostSession host, Keen.VRage.Multiplayer.ClientId client)
	{
		if (client.Id == 1) return host.SteamOfClient(SyncProtocol.HostClientId);
		return Engine.Y51Replication.ServerOf(client) is { } connection ? Y51_SE2Online.Net.PeerAlias.SteamOf(connection.Peer.Value) : 0;
	}

	/// <summary>Whether the player behind an identity has a permission. Identities that are no player of this session
	/// (NPCs, unknown) have none, unless <paramref name="unknownAllowed"/> (keys everyone has by default).</summary>
	internal static bool HasIdentity(long identity, string key, bool unknownAllowed = false)
	{
		var host = AdminAccess.Host;
		if (host == null || identity == 0) return unknownAllowed;
		ulong steam = host.SteamOfIdentity(identity);
		return steam == 0 ? unknownAllowed : host.Allowed(steam, key);
	}

	private sealed record StoryFields(System.Reflection.FieldInfo? Initiator, System.Reflection.FieldInfo[] Entities);

	private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Type, StoryFields> StoryArguments = new();

	// The async method's parameters are the state machine's plainly named fields; <>4__this is the component it runs on.
	private static StoryFields Arguments(Type type)
	{
		var fields = AccessTools.GetDeclaredFields(type).Where(f => !f.IsStatic && (f.Name == "<>4__this" || !f.Name.StartsWith('<'))).ToArray();
		var initiator = type.DeclaringType == typeof(OwnershipSessionComponent) ? fields.FirstOrDefault(f => f.Name == "initiator" && f.FieldType == typeof(IdentityId)) : null;
		return new StoryFields(initiator, fields.Where(f => typeof(Entity).IsAssignableFrom(f.FieldType) || typeof(Component).IsAssignableFrom(f.FieldType)
			|| (f.FieldType.IsGenericType && f.FieldType.GetGenericArguments()[0] == typeof(Entity))).ToArray());
	}

	private static string StoryName(Type type) => type.Name.StartsWith('<') && type.Name.IndexOf('>') is > 1 and var end ? type.Name[1..end] : type.Name;

	// A cancellation reaches the player's game as a skipped story, which its unawaited requests treat as nothing to show.
	private sealed class CancelledStory(string reason) : System.Runtime.CompilerServices.IAsyncStateMachine
	{
		public void MoveNext() => throw new System.Threading.Tasks.TaskCanceledException("Access denied: " + reason);

		public void SetStateMachine(System.Runtime.CompilerServices.IAsyncStateMachine stateMachine) { }
	}

	private sealed class RefusedStory(string reason) : System.Runtime.CompilerServices.IAsyncStateMachine
	{
		public void MoveNext() => throw new InvalidOperationException("Access denied: " + reason);

		public void SetStateMachine(System.Runtime.CompilerServices.IAsyncStateMachine stateMachine) { }
	}

	private static readonly System.Collections.Concurrent.ConcurrentDictionary<(int Client, string Kind), long> _lastLogged = new();

	/// <summary>True at most once per client and kind every 5 s, so a modified client cannot flood the log.</summary>
	internal static bool LogDue(Keen.VRage.Multiplayer.ClientId client, string kind)
	{
		long now = MpClock.NowMs();
		var key = (client.Id, kind);
		if (_lastLogged.TryGetValue(key, out long last) && now - last < 5000) return false;
		_lastLogged[key] = now;
		return true;
	}

	/// <summary>The client's identity, or 0 for a client that has none yet (GetIdentityFromClient throws for those).</summary>
	internal static IdentityId IdentityOf(Keen.VRage.Multiplayer.ClientId client)
		=> MpPlugin.App?.ServerSession?.SessionComponents.TryGet<IdentitiesSessionComponent>() is { } identities && identities._clientIds.TryGetValue(client, out var identity) ? identity : default;

	/// <summary>Another human player's living character. Bodies stay lootable and NPCs stay usable.</summary>
	internal static bool IsForeignCharacter(Entity entity, long identity)
		=> entity.TryGet<CharacterLifeComponent>() is { IsAlive: true } && TeamPermissions.OwnerOf(entity) is long owner && owner != 0 && owner != identity && PlayerIdentities.IsHuman(owner);

	// The players' toolbar requests: pressing a tile, and clearing one from the toolbar or the control panel.
	private static System.Collections.Generic.IEnumerable<Type> ToolbarStories()
	{
		static Type? Machine(Type owner, string story) => owner.GetNestedTypes(System.Reflection.BindingFlags.NonPublic).FirstOrDefault(t => t.Name.StartsWith("<" + story + ">", StringComparison.Ordinal));
		var toolbar = typeof(Keen.Game2.Simulation.WorldObjects.CubeBlocks.Toolbar.BlockToolbarServerComponent);
		foreach (var machine in new[] { Machine(toolbar, "RequestActivateTile"), Machine(toolbar, "RequestClearTile"), Machine(typeof(StreamedControlPanelSessionComponent), "RequestClearTile") })
			if (machine != null) yield return machine;
	}

	// Inside a player's toolbar request a press or edit with no known caller is refused; the server's own presses (timers,
	// sensors, event controllers, mission steps) run outside one and stay allowed.
	[ThreadStatic] private static int _toolbarStories;

	private static void ToolbarStoryPrefix() => _toolbarStories++;

	private static Exception? ToolbarStoryFinalizer(Exception? __exception)
	{
		_toolbarStories--;
		return __exception;
	}

	private static Exception? StoryFinalizer(Exception? __exception, (Keen.VRage.Multiplayer.ClientId? Client, bool Hosting, Type? Story) __state)
	{
		(_storyClient, _hostingStory, _storyType) = __state;
		return __exception;
	}

	// The co-op host's own player reaches the server in process, not through a transport that names a client.
	private static void InProcessStoryPrefix(out (Keen.VRage.Multiplayer.ClientId? Client, bool Hosting, Type? Story) __state)
	{
		__state = (_storyClient, _hostingStory, _storyType);
		_storyClient = Keen.VRage.Multiplayer.ClientId.Server;
		_hostingStory = false;
		_storyType = null;
	}

	// Button panels and every other block toolbar: pressing or clearing a tile needs the block's use access (2.4 checked
	// grid sharing on the host; 4.0's RequestActivateTile has no validator).
	private static bool ToolbarPrefix(Component __instance)
	{
		if (!Enforcing) return true;
		if (_storyClient is not { } client)
		{
			if (_toolbarStories <= 0 || ToolbarUnknownOff) return true;
			Refused++;
			GameLog.Instance.WarnOnce("toolbar-unknown-caller", $"access: refused a toolbar request on {__instance.Entity.GetComposition()?.DebugNameShort} from an unknown caller (further ones not logged)");
			return false;
		}
		if (client == Keen.VRage.Multiplayer.ClientId.Server) return true;
		var who = IdentityOf(client);
		if ((long)who != 0 && MayUse(null, who, __instance.Entity, useKey: "use.toolbar")) return true;
		Refused++;
		GameLog.Instance.Info($"access: refused identity {(long)who} on {__instance.Entity.GetComposition()?.DebugNameShort} toolbar (no access)");
		return false;
	}

	// SE2 4.0's admin check returns true for everyone, so any player could teleport, spawn ships, edit voxels with the
	// voxel hand, make safe zones and use every other admin tool. On an SE2 Online server each tool needs its game.admin.*
	// permission (owners have all): the key is named after the story or signal that asks (AdminRules.GameAdminKey).
	private static bool AdminPrefix(ReplicationEventContext context, ref bool __result)
	{
		if (!Enforcing) return true;
		__result = context.IsLocal || context.IsFromServer || MayAdmin(context);
		return false;
	}

	private static bool ValidateAdminPrefix(ReplicationEventContext context)
	{
		if (!Enforcing || context.IsLocal || context.IsFromServer || MayAdmin(context)) return false;
		throw context.Throw("Access denied: game admin tools need " + GameAdminKey());
	}

	private static bool MayAdmin(ReplicationEventContext context)
	{
		string key = GameAdminKey();
		bool allowed = HasClient(context.ClientId, key);
		if (!allowed)
		{
			Refused++;
			if (LogDue(context.ClientId, "admin-tool")) GameLog.Instance.Info($"access: refused identity {(long)IdentityOf(context)}'s game admin tool ({key}; further ones within 5 s not logged)");
		}
		return allowed;
	}

	/// <summary>The game admin key for what is running: the player's story, else the signal being replayed, else game.admin.debug.</summary>
	private static string GameAdminKey()
	{
		if (_storyType is { } story) return AdminRules.GameAdminKey(story.DeclaringType?.Name, StoryName(story));
		if (_signalName is { } signal) { var (declaring, member) = AdminRules.SplitSignal(signal); return AdminRules.GameAdminKey(declaring, member); }
		GameLog.Instance.WarnOnce("admin-tool-unnamed", "access: a game admin check ran outside a known story or signal; treated as game.admin.debug");
		return "game.admin.debug";
	}

	// Opening a container lists every inventory on its ship, and the client then asks for each one at once; one refused
	// container failed that whole request (the server refused it as "not there"), and on the listen host the failure
	// reached the crash handler. A container the player may not use is left out of the list instead, so it is never asked
	// for. Blocks added to the ship later are checked against the player the list was made for.
	private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Keen.Game2.Simulation.StreamedUI.Terminal.Inventories.InventoryListModelStreamed, System.Runtime.CompilerServices.StrongBox<IdentityId>> _listOwners = new();

	private static void InventoryListPrefix(Keen.Game2.Simulation.StreamedUI.Terminal.Inventories.InventoryListModelStreamed model, ReplicationEventContext? context)
	{
		if (context != null) _listOwners.AddOrUpdate(model, new System.Runtime.CompilerServices.StrongBox<IdentityId>(IdentityOf(context)));
	}

	private static bool InventoryEntryPrefix(Entity entity, Keen.Game2.Simulation.StreamedUI.Terminal.Inventories.InventoryListModelStreamed model, ReplicationEventContext? context)
	{
		if (!Enforcing || entity == null) return true;
		IdentityId who;
		if (context != null) who = IdentityOf(context);
		else if (_listOwners.TryGetValue(model, out var owner)) who = owner.Value;
		else return true;
		if (MayUse(context, who, entity, useKey: "use.inventory")) return true;
		Refused++;
		return false;
	}

	private static bool Enforcing => AdminAccess.Hosting;

	/// <summary>Whether this identity may use the entity a terminal screen or request names. Never throws: a check that
	/// cannot decide allows, because an exception here faults the player's request and their game.</summary>
	internal static bool MayUse(ReplicationEventContext? context, IdentityId identity, Entity entity, bool basicUse = false, string? useKey = null)
	{
		try
		{
			// No admin or host exemption: only a player's own access bypass (Admin Panel) lifts the rules. A request with no
			// player behind it (the game's own server stories) is not a player's use. A group denied a kind of use
			// (use.terminal, use.inventory...) may not do it anywhere, its own grids included.
			if ((long)identity == 0) return true;
			if (useKey != null && !HasIdentity((long)identity, useKey, unknownAllowed: true)) return false;
			if (AccessBypass.IsOn((long)identity)) return true;
			// NPC and unowned grids keep SE2's own rule, which is no check: stations' trade terminals, encounter loot and
			// vehicles are there to be used, and SE2's owner-only answer refused them all.
			if (!PlayerIdentities.IsHuman(TeamPermissions.OwnerOf(entity))) return true;
			var permissions = MpPlugin.App?.ServerSession?.SessionComponents.TryGet<PermissionsSessionComponent>();
			if (permissions == null) return true;
			if (entity.TryGet<CubeBlockComponent>() is { } block)
			{
				var grid = block.Grid.Entity;
				// Armor and other blocks without their own access settings follow their grid.
				if (entity.TryGet<BlockPermissionsComponent>() == null || grid.TryGet<OwnershipComponent>() == null) return MayUseGrid(permissions, identity, grid);
				var configuration = Singleton<DefinitionManager>.Instance.GetConfiguration<PermissionsConfiguration>();
				var use = basicUse ? configuration.BasicUse : configuration.AdvancedUse;
				return permissions.CanUseBlockAction(entity, grid, identity, use);
			}
			if (entity.TryGet<CubeGridComponent>() != null) return MayUseGrid(permissions, identity, entity);
			// Another player's living character (their suit inventory); bodies and backpacks stay lootable.
			return !IsForeignCharacter(entity, (long)identity);
		}
		catch (Exception ex)
		{
			GameLog.Instance.WarnOnce("access-check-failed", $"access: could not check {entity.GetComposition()?.DebugNameShort ?? entity.ToString()} ({ex.GetType().Name}: {ex.Message}); allowed");
			return true;
		}
	}

	private static bool MayUseGrid(PermissionsSessionComponent permissions, IdentityId identity, Entity grid)
		=> grid.TryGet<OwnershipComponent>() == null || permissions.IsOwner(grid, identity) || permissions.CanPlaceBlock(grid, identity);

	// Not GetIdentityFromClient: it throws for a request the server makes itself (ClientId 0), such as the item transfer
	// inside every trade at a trade terminal, and the throw ended the server.
	private static IdentityId IdentityOf(ReplicationEventContext context)
		=> context.Session.SessionComponents.Get<IdentitiesSessionComponent>()._clientIds.TryGetValue(context.ClientId, out var identity) ? identity : default;

	/// <summary>Our refusal. On the co-op host the host's own request fails in-process with the raw exception (a player gets a
	/// remote fault), and the game's screens await these in async void methods, so a refusal reached the crash handler.</summary>
	internal sealed class AccessDeniedException(string message) : Exception(message);

	private static long _localRefusedMs = long.MinValue / 2;

	/// <summary>This game's own request was refused in the last few seconds (SE2 then throws its own "No Entity found").</summary>
	internal static bool RefusedLocalRecently => MpClock.NowMs() - System.Threading.Interlocked.Read(ref _localRefusedMs) < 3000;

	private static void Note(ReplicationEventContext context, Entity entity)
	{
		if (context.IsLocal || (long)IdentityOf(context) is var who && who != 0 && who == (long)context.Session.SessionComponents.Get<IdentitiesSessionComponent>().LocalIdentity)
			System.Threading.Interlocked.Exchange(ref _localRefusedMs, MpClock.NowMs());
		Refused++;
		GameLog.Instance.Info($"access: refused identity {(long)IdentityOf(context)} on {entity.GetComposition()?.DebugNameShort ?? entity.ToString()} (no access)");
	}

	private static Exception Refuse(ReplicationEventContext context, Entity entity)
	{
		Note(context, entity);
		return new AccessDeniedException("Access denied");
	}

	// A refused block reads as "not there": the screen skips it instead of the request failing.
	private static void AsEntityPostfix(StreamedBaseSessionComponent __instance, ReplicationEventContext? replicationContext, ref Entity? __result)
	{
		if (!Enforcing || replicationContext == null || __result == null || __instance is StreamedRespawnScreenSessionComponent) return;
		if (MayUse(replicationContext, IdentityOf(replicationContext), __result, useKey: "use.terminal")) return;
		Note(replicationContext, __result);
		__result = null;
	}

	private static void InteractedPrefix(ReplicationEventContext eventContext, Entity? interacted)
	{
		if (!Enforcing || interacted == null) return;
		if (!MayUse(eventContext, IdentityOf(eventContext), interacted, useKey: "use.inventory")) throw Refuse(eventContext, interacted);
	}

	private static bool ControlPanelPrefix(Entity root, ReplicationEventContext? context)
	{
		if (!Enforcing || context == null || root == null || MayUse(context, IdentityOf(context), root, useKey: "use.terminal")) return true;
		Note(context, root);
		return false;
	}

	// __state: -1 refused, 1 the point was the home base before, 0 it was not.
	private static bool ActivateRespawnPrefix(IdentityId identity, Entity respawnPoint, ref StringId __result, out int __state)
	{
		__state = respawnPoint != null && HomeOf(identity) == respawnPoint ? 1 : 0;
		if (!Enforcing || respawnPoint == null) return true;
		if (MayUse(null, identity, respawnPoint, useKey: "use.medbay"))
		{
			// On a medbay the player owns, the game's action makes it the home base (or clears it, which stays allowed).
			if (respawnPoint.AsInterface<Keen.Game2.Simulation.GameSystems.Ownership.IOwnerProvider>()?.Owner is not { } owner || (long)owner != (long)identity
				|| __state == 1 || HasIdentity((long)identity, "respawn.home.set", unknownAllowed: true)) return true;
			GameLog.Instance.Info($"access: identity {(long)identity} may not set a home base (respawn.home.set)");
		}
		else GameLog.Instance.Info($"access: identity {(long)identity} may not bind to the medbay on {respawnPoint.GetComposition()?.DebugNameShort}");
		Refused++;
		__result = default;
		__state = -1;
		return false;
	}

	private static RespawnPerPlayerData? RespawnData(IdentityId identity)
		=> MpPlugin.App?.ServerSession is { } session ? Keen.VRage.Core.Game.Components.GameEntityExtensions.Get<IPerPlayerData>(session).GetPerPlayerData<RespawnPerPlayerData>(identity) : null;

	private static Entity? HomeOf(IdentityId identity) => RespawnData(identity)?.HomePoint;

	// SE2 Online keeps the player's home base by SteamID beside the game's own record, so a destroyed one is still named.
	private static void ActivateRespawnPostfix(IdentityId identity, Entity respawnPoint, int __state)
	{
		try
		{
			if (__state < 0 || !Enforcing || respawnPoint == null || AdminAccess.Host is not { } host || RespawnData(identity) is not { } data) return;
			// Unclaiming a faction or shared home base (the game only toggles the claim on medbays the player does not own)
			// gives it up: the game respawns only at claimed points.
			if (data.HomePoint == respawnPoint && !data.KnownPoints.Contains(respawnPoint))
			{
				data.HomePoint = null;
				respawnPoint.TryGet<RespawnPointComponent>()?.OnStatusChanged(identity, isClaimed: false, isHome: false);
			}
			var home = data.HomePoint;
			if (home != respawnPoint && __state == 0) return;
			ulong steam = host.SteamOfIdentity((long)identity);
			if (steam == 0) return;
			if (home == null) { host.Access.SetHome(steam, null); return; }
			var position = home.Data.GetWorldTransform().Position;
			string name = home.TryGet<RespawnPointComponent>()?.DisplayName.EvaluateText() ?? "";
			host.Access.SetHome(steam, new Y51_SE2Online.Sync.HomeEntry { World = HostWorld.Current?.WorldKey ?? "", Name = string.IsNullOrWhiteSpace(name) || name.Contains('{') ? "Medbay" : name, X = position.X, Y = position.Y, Z = position.Z,
				Grid = WorldSocial.GridOf(home)?.TryGet<Keen.Game2.Simulation.WorldObjects.CubeGrids.CubeGridComponent>()?.DisplayName.ToString() ?? "" });
		}
		catch (Exception ex) { GameLog.Instance.WarnOnce("home-mirror", "access: recording the home base failed: " + ex.Message); }
	}

	// The game's own respawn request (its respawn screen, which SE2 Online falls back to): Home needs respawn.home.use and
	// access to the medbay, and Closest skips medbays the player may no longer use, put back afterwards.
	private static bool RespawnAtTypePrefix(IdentityId identity, RespawnRequestType request, ref Entity? __result, out System.Collections.Generic.List<Entity>? __state)
	{
		__state = null;
		if (!Enforcing || (long)identity == 0 || RespawnData(identity) is not { } data) return true;
		if (request == RespawnRequestType.Home && data.HomePoint is { } home
			&& (!HasIdentity((long)identity, "respawn.home.use", unknownAllowed: true) || !MayUse(null, identity, home, useKey: "use.medbay")))
		{
			Refused++;
			GameLog.Instance.Info($"access: identity {(long)identity} may not respawn at its home base");
			__result = null;
			return false;
		}
		if (request != RespawnRequestType.Closest) return true;
		foreach (var point in data.KnownPoints)
			if (point != null && !MayUse(null, identity, point, useKey: "use.medbay")) (__state ??= new()).Add(point);
		if (__state != null) foreach (var point in __state) data.KnownPoints.Remove(point);
		return true;
	}

	private static Exception? RespawnAtTypeFinalizer(IdentityId identity, Exception? __exception, System.Collections.Generic.List<Entity>? __state)
	{
		if (__state != null && RespawnData(identity) is { } data)
			foreach (var point in __state) if (!data.KnownPoints.Contains(point)) data.KnownPoints.Add(point);
		return __exception;
	}

	// The game makes a player's first medbay their home base by itself; a group without respawn.home.set keeps none.
	private static void AutoHomePrefix(IdentityId owner, out bool __state) => __state = RespawnData(owner)?.HomePoint == null;

	private static void AutoHomePostfix(IdentityId owner, Entity respawn, bool __state)
	{
		if (!__state || !Enforcing || RespawnData(owner) is not { } data || data.HomePoint != respawn || HasIdentity((long)owner, "respawn.home.set", unknownAllowed: true)) return;
		data.HomePoint = null;
		respawn.TryGet<RespawnPointComponent>()?.OnStatusChanged(owner, isClaimed: true, isHome: false);
	}
}
