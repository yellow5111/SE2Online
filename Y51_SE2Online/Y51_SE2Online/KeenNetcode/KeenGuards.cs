using System;
using HarmonyLib;
using Keen.Game2.Client.WorldObjects.CubeBlocks.MechanicalBlocks.Wheels;
using Keen.Game2.Simulation.GameSystems.Discoveries;
using Keen.Game2.Simulation.GameSystems.Ownership;
using Keen.VRage.DCS.Accessors;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Library.Memory;
using Keen.VRage.Physics.Data;
using Keen.VRage.Physics.Havok;
using Keen.VRage.Physics.Havok.WorldManager;
using Keen.VRage.Voxels.Components.Flora;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>Crashes in SE2 4.0's own code that only multiplayer worlds reach; each guard turns one into a skipped frame.</summary>
internal static class KeenGuards
{
	public static void Install(Harmony harmony)
	{
		int installed = 0, total = 0;
		// Each guard installs on its own: a method this game build renamed or overloaded turns off that guard only.
		void Guard(string name, Action patch)
		{
			total++;
			try { patch(); installed++; }
			catch (Exception ex) { GameLog.Instance.Warn($"keen guards: '{name}' not installed for this game build ({ex.GetType().Name}: {ex.Message.Split('\n')[0]})"); }
		}
		Guard("wheel audio", () => harmony.Patch(AccessTools.Method(AccessTools.Inner(typeof(DrivingWheelAudioComponent), "AudioAdapter"), "TryGetEffectDefinition"),
			finalizer: new HarmonyMethod(typeof(KeenGuards), nameof(WheelAudioFinalizer))));
		Guard("discoveries", () => harmony.Patch(AccessTools.Method(typeof(DiscoveriesServerSessionComponent), nameof(DiscoveriesServerSessionComponent.TryIncreaseDiscoveryState)),
			finalizer: new HarmonyMethod(typeof(KeenGuards), nameof(DiscoveryFinalizer))));
		Guard("body flags", () => harmony.Patch(AccessTools.Method(typeof(HavokSessionComponent), "OnUpdateBodyFlags"), prefix: new HarmonyMethod(typeof(KeenGuards), nameof(BodyFlagsPrefix))));
		Guard("flora sector server-only data", () => harmony.Patch(AccessTools.Method(typeof(PlanetFloraSectorPhysicsComponent), "Init", new[] { typeof(PlanetFloraSectorPhysicsComponentObjectBuilder) }),
			transpiler: new HarmonyMethod(typeof(KeenGuards), nameof(ServerOnlyDataTranspiler))));
		Guard("flora entity server-only data", () => harmony.Patch(AccessTools.Method(typeof(FloraEntityPhysicsComponent), "Init", new[] { typeof(Keen.VRage.Game.Components.Physics.ScalablePhysicsObjectBuilder) }),
			transpiler: new HarmonyMethod(typeof(KeenGuards), nameof(ServerOnlyDataTranspiler))));
		Guard("havok reorder entry", () => harmony.Patch(AccessTools.Method(typeof(HavokLargeWorldManager), "GetEntityReorderEntryIndex",
			new[] { typeof(DEntity), typeof(BufferReference<HavokLargeWorldManager.EntityReorderEntry>), typeof(System.Collections.Generic.Dictionary<DEntity, int>) }),
			prefix: new HarmonyMethod(typeof(KeenGuards), nameof(ReorderEntryPrefix))));
		Guard("motion type", () =>
		{
			harmony.Patch(AccessTools.Method(typeof(HavokLargeWorldManager), nameof(HavokLargeWorldManager.SetBodyMotionType)),
				prefix: new HarmonyMethod(typeof(KeenGuards), nameof(MotionTypePrefix)), finalizer: new HarmonyMethod(typeof(KeenGuards), nameof(MotionTypeFinalizer)));
			harmony.Patch(AccessTools.Method(typeof(HavokSessionComponent), "ProcessMotionTypeChanges"), postfix: new HarmonyMethod(typeof(KeenGuards), nameof(MotionChangesPostfix)));
		});
		Guard("destroy body", () => harmony.Patch(AccessTools.Method(typeof(HavokLargeWorldManager), "DestroyBody", new[] { typeof(DEntity), typeof(bool) }),
			prefix: new HarmonyMethod(typeof(KeenGuards), nameof(DestroyBodyPrefix))));
		Guard("steam display name", () => harmony.Patch(AccessTools.Method(typeof(Keen.VRage.Steam.EngineComponents.SteamSocialInteractionServiceComponent), "GetDisplayName"),
			finalizer: new HarmonyMethod(typeof(KeenGuards), nameof(DisplayNameFinalizer))));
		Guard("replay signals", () => harmony.Patch(AccessTools.Method(typeof(Keen.VRage.Multiplayer.Transport.InProcess.InProcessServerPeerTransportSessionComponent), "ReplaySignals"),
			finalizer: new HarmonyMethod(typeof(KeenGuards), nameof(ReplaySignalsFinalizer))));
		Guard("unawaited exception", () => harmony.Patch(AccessTools.Method(AccessTools.TypeByName("Keen.VRage.Library.Threading.SkipWaitMonitor"), "ProcessException"),
			prefix: new HarmonyMethod(typeof(KeenGuards), nameof(UnawaitedExceptionPrefix))));
		Guard("host's own story faults", () => harmony.Patch(AccessTools.Method(typeof(Keen.VRage.Multiplayer.Transport.InProcess.InProcessPeerTransportSessionComponent), "ProcessCompleteStory"),
			prefix: new HarmonyMethod(typeof(KeenGuards), nameof(InProcessCompletionPrefix))));
		Guard("sort grids", () => harmony.Patch(AccessTools.Method(AccessTools.TypeByName("Keen.Game2.Client.UI.TerminalScreen.ControlPanel.ControlPanelTreeViewModel"), "SortGrids"),
			prefix: new HarmonyMethod(typeof(KeenGuards), nameof(SortGridsPrefix))));
		if (AccessTools.TypeByName("Keen.VRage.Render12.SceneSystem.SceneManager") is { } sceneManager)
			foreach (var method in AccessTools.GetDeclaredMethods(sceneManager))
				if (method.Name.StartsWith("IRootEntity_", StringComparison.Ordinal) && !method.IsAbstract && !method.ContainsGenericParameters)
					Guard("render entity " + method.Name, () => harmony.Patch(method, finalizer: new HarmonyMethod(typeof(KeenGuards), nameof(RenderEntityFinalizer))));
		Guard("camera sync", () => harmony.Patch(AccessTools.Method(AccessTools.TypeByName("Keen.Game2.Client.GameSystems.CameraSystems.CameraSystemComponent"), "TrySyncCameraData"),
			finalizer: new HarmonyMethod(typeof(KeenGuards), nameof(CameraSyncFinalizer))));
		Guard("thrust unsubscribe", () => harmony.Patch(AccessTools.Method(AccessTools.TypeByName("Keen.Game2.Client.WorldObjects.Shared.Movement.Effects.GridThrustEffectsComponent"), "UnsubscribeBlock"),
			prefix: new HarmonyMethod(typeof(KeenGuards), nameof(ThrustUnsubscribePrefix))));
		Guard("refused request", () => harmony.Patch(AccessTools.Method(typeof(Keen.VRage.Core.Platform.CrashReporting.CrashHandler), "HandleUnobservedException"),
			prefix: new HarmonyMethod(typeof(KeenGuards), nameof(RefusedRequestPrefix))));
		Guard("entity id", () => harmony.Patch(AccessTools.Method(typeof(Keen.VRage.Multiplayer.Replications.OutOfProcess.ReplicationEntitySerializationContext), "GetEntityId"),
			finalizer: new HarmonyMethod(typeof(KeenGuards), nameof(EntityIdFinalizer))));
		Guard("support contacts", () => harmony.Patch(AccessTools.Method(typeof(HavokSessionComponent), "UpdateSupportDataFromCharacters"),
			finalizer: new HarmonyMethod(typeof(KeenGuards), nameof(SupportContactsFinalizer))));
		// Recharge is also the name of the static job stub around it; the instance one reads the seat's ownership.
		Guard("seat recharge", () => harmony.Patch(System.Linq.Enumerable.Single(AccessTools.GetDeclaredMethods(AccessTools.TypeByName("Keen.Game2.Simulation.WorldObjects.Characters.CharacterChargeStatFromSeatResourceComponent")), m => m.Name == "Recharge" && !m.IsStatic),
			finalizer: new HarmonyMethod(typeof(KeenGuards), nameof(SeatRechargeFinalizer))));
		Guard("impact effects", () => harmony.Patch(AccessTools.Method(typeof(Keen.Game2.Client.GameSystems.ProjectileSystems.ProjectileVisualsSessionComponent), "CreateImpactEffect"),
			prefix: new HarmonyMethod(typeof(KeenGuards), nameof(ImpactEffectPrefix))));
		Guard("tracer effects", () => harmony.Patch(AccessTools.Method(typeof(Keen.Game2.Client.GameSystems.ProjectileSystems.ProjectileVisualsSessionComponent), "CreateTracerEffect"),
			prefix: new HarmonyMethod(typeof(KeenGuards), nameof(TracerEffectPrefix))));
		GameLog.Instance.Info($"keen guards: {installed} of {total} installed");
	}

	// The client's impact job reads the hit entity, projectile and material of every queued request without a check; a
	// request whose hit entity this player does not have (or that left the world before the job ran) crashed the game.
	private static bool ImpactEffectPrefix(Keen.Game2.Client.GameSystems.ProjectileSystems.ProjectileVisualsSessionComponent.ImpactEffectRequest request)
	{
		var hit = request.HitEntity;
		string? why = Sync.ImpactRequestRule.Refusal(hit != null, hit != null && hit.Data.Scene is { } scene && scene.IsEntityAlive(hit.DEntity),
			request.Projectile != null, request.HitMaterial != null);
		if (why == null) return true;
		GameLog.Instance.WarnOnce("impact-effect-" + why, $"keen guards: skipped a projectile impact effect with {why} (further ones not logged)");
		return false;
	}

	// The tracer job reads the shooter's parents before its own alive check.
	private static bool TracerEffectPrefix(in Keen.Game2.Client.GameSystems.ProjectileSystems.ProjectileVisualsSessionComponent.TracerEffectRequest request)
	{
		var source = request.SourceEntity;
		string? why = Sync.ImpactRequestRule.TracerRefusal(source != null, source != null && source.Data.Scene is { } scene && scene.IsEntityAlive(source.DEntity));
		if (why == null) return true;
		GameLog.Instance.WarnOnce("tracer-effect-" + why, $"keen guards: skipped a projectile tracer with {why} (further ones not logged)");
		return false;
	}

	// A character's controller can still hold a contact with a body that was destroyed this frame (a corpse or grid piece
	// removed under its feet); reading that body's freed owner handle threw and the game died. The character has no
	// contacts for this one frame and the controller drops the stale one on its next step.
	private static Exception? SupportContactsFinalizer(Exception? __exception, ref Keen.VRage.Physics.Character.CharacterSupportData support)
	{
		if (__exception is not InvalidOperationException) return __exception;
		support.CharacterContacts.Clear();
		support.AnyBreachedContacts = false;
		GameLog.Instance.WarnOnce("support-contact", "keen guards: a character stood on a body removed this frame; its contacts were skipped for one frame");
		return null;
	}

	// A client's static-object tree can keep a body whose static data never landed (Keen's command-buffer replay drops a
	// write whose slot is gone: "There should have been someone who already wrote the data"); cluster reordering then read a
	// garbage collider (EntryPointNotFoundException) or missing data ("Data not found") and the client died. The first
	// orphan seen clears every orphan out of the tree, and this one is entered as a point so the current reorder goes on.
	private static bool ReorderEntryPrefix(HavokLargeWorldManager __instance, DEntity entity, BufferReference<HavokLargeWorldManager.EntityReorderEntry> entries,
		System.Collections.Generic.Dictionary<DEntity, int> keys, ref int __result)
	{
		if (keys.ContainsKey(entity)) return true;
		var context = __instance.GetContext(entity);
		if (context.TryGet<ClusterIndex>(out _) || context.TryGet<HavokLargeWorldManager.StaticData>(out _)) return true;
		var position = context.GetWorldTransform().Position;
		// ponytail: a full tree scan per orphan seen (about one every few seconds on a planet world); a point query missed
		// orphans whose box does not hold their origin, and the reorder then crashed on them.
		int purged = PurgeStaticOrphans(__instance);
		int index = entries.Count;
		entries.Add(new HavokLargeWorldManager.EntityReorderEntry(entity, -1, new BoundingBoxD(position, position)));
		keys.Add(entity, index);
		__result = index;
		GameLog.Instance.WarnOnce("reorder-orphan", $"keen guards: physics body of entity {entity} is listed without its physics data; kept as a point (further ones not logged)");
		if (purged > 0) GameLog.Instance.Warn($"keen guards: removed {purged} static-tree entries whose physics data was gone");
		return false;
	}

	// Unloading a world (leaving a server) destroys every body; a cluster's static list can still name an entity that is
	// already gone, and DestroyBody on it threw in the scene lookup and the crash handler closed the game on the way out.
	// The same teardown also reaches entities still alive but already half-removed (no archetype): DestroyBody's first read,
	// Has<BodyProxy>, threw. That read is done here first, before DestroyBody changes anything.
	private static bool DestroyBodyPrefix(HavokLargeWorldManager __instance, DEntity body)
	{
		if (__instance._session.Scene.IsEntityAlive(body))
		{
			try
			{
				__instance.GetContext(body).Has<BodyProxy>();
				return true;
			}
			catch (NullReferenceException) { }
		}
		GameLog.Instance.WarnOnce("destroy-dead-body", $"keen guards: skipped destroying the physics body of entity {body}, which no longer exists");
		return false;
	}

	private static int PurgeStaticOrphans(HavokLargeWorldManager manager)
	{
		var tree = manager._staticObjectTree;
		var scene = manager._session.Scene;
		int purged = 0;
		using Buffer<int> leaves = default;
		tree.CollectLeafIdentifiers(leaves);
		foreach (int leaf in leaves)
		{
			DEntity owner = tree.GetValue(leaf);
			if (scene.IsEntityAlive(owner) && manager.GetContext(owner).Has<HavokLargeWorldManager.StaticData>()) continue;
			tree.Remove(leaf);
			purged++;
		}
		return purged;
	}

	// A planet's flora sector, and each breakable tree, replicates to clients with the server's destruction data (asymmetric
	// destruction, safe-speed override); the client scene has no storage for those types, Data.Set threw, and the joining client crashed (about half
	// of joins on planet worlds). Only the server breaks flora, so the client goes on without them.
	// The finalizer alone was not enough: adding a data type moves the entity to another archetype, the client scene threw
	// in the middle of that move, and the half-moved entity corrupted the scene's storage ("Source entity out of active
	// range"), which crashed the client seconds later (often as a respawn ship arrived). The two writes (the only
	// Data.Set calls in these Init bodies) are now skipped on a client before they start.
	private static System.Collections.Generic.IEnumerable<CodeInstruction> ServerOnlyDataTranspiler(System.Collections.Generic.IEnumerable<CodeInstruction> instructions)
	{
		var helper = AccessTools.Method(typeof(KeenGuards), nameof(ServerOnlySet));
		foreach (var instruction in instructions)
		{
			if (instruction.operand is System.Reflection.MethodInfo { Name: "Set", IsGenericMethod: true } method && method.DeclaringType == typeof(DEntityContext))
				yield return new CodeInstruction(System.Reflection.Emit.OpCodes.Call, helper.MakeGenericMethod(method.GetGenericArguments())).WithLabels(instruction.labels).WithBlocks(instruction.blocks);
			else
				yield return instruction;
		}
	}

	private static void ServerOnlySet<T>(in DEntityContext context, T data) where T : unmanaged
	{
		if (MpPlugin.App?.ClientSession is { } client && ReferenceEquals(context.Scene, client.Scene)) return;
		context.Set(data);
	}

	// On a client, a replicated body can get its contact flags updated before (or after) Havok holds it; the native call
	// then wrote through an invalid body and the process died with an access violation (0xC0000005), no crash report.
	private static unsafe bool BodyFlagsPrefix(DEntity body, BodyProxy proxy)
	{
		if (proxy.World != null && proxy.World->isBodyValid(body) && proxy.World->isBodyAdded(body)) return true;
		GameLog.Instance.WarnOnce("body-flags-invalid", $"keen guards: skipped a contact-flag update for a physics body Havok does not hold (entity {body})");
		return false;
	}

	// Same gap for a motion change: a grid that just arrived (a respawn lander placed and held on its site) can be switched
	// static or dynamic before this client's Havok holds its body; reading that body crashed the client. The change is
	// skipped; the body is built with the grid's current motion when it is added.
	private static unsafe bool MotionTypePrefix(HavokLargeWorldManager __instance, DEntity body, BodyArgs.Motion motionType)
	{
		var context = new DEntityContext(__instance._session.Scene, body);
		// A body that already has its Havok proxy but no cluster yet (a lander's grids arriving while the server switches
		// them dynamic, or back to static as the lander settles): the switch reads the cluster, after it took a static body
		// out of the static tree, so it threw half done ("Data not found") and the client died. The change waits for the
		// next frames instead.
		// Only the two switches that read the cluster wait: static to dynamic/kinematic, and anything to static
		// (HavokLargeWorldManager.SetBodyMotionType); a dynamic/kinematic switch never needs it.
		bool readsCluster = context.Has<HavokLargeWorldManager.StaticData>() ? motionType != BodyArgs.Motion.Static : motionType == BodyArgs.Motion.Static;
		if (readsCluster && context.Has<BodyProxy>() && !context.Has<Keen.VRage.Physics.Havok.ClusterIndex>())
		{
			lock (DeferredMotion)
			{
				int tries = DeferredMotion.TryGetValue(body, out var d) ? d.Tries + 1 : 1;
				if (tries <= MaxMotionTries) DeferredMotion[body] = (__instance._session, motionType, tries);
				else DeferredMotion.Remove(body);
			}
			GameLog.Instance.WarnOnce("motion-type-cluster", $"keen guards: held back a motion change for a static physics body that has no cluster yet (entity {body})");
			return false;
		}
		if (DeferredMotion.Count > 0) lock (DeferredMotion) DeferredMotion.Remove(body);
		if (context.Has<HavokLargeWorldManager.StaticData>() || !context.TryGet<BodyProxy>(out var proxy)) return true;
		if (proxy.World != null && proxy.World->isBodyValid(body) && proxy.World->isBodyAdded(body)) return true;
		GameLog.Instance.WarnOnce("motion-type-invalid", $"keen guards: skipped a motion change for a physics body Havok does not hold yet (entity {body})");
		return false;
	}

	private const int MaxMotionTries = 600;

	private static readonly System.Collections.Generic.Dictionary<DEntity, (HavokSessionComponent Session, BodyArgs.Motion Motion, int Tries)> DeferredMotion = new();

	// After the frame's motion changes ran (and were cleared), the held-back ones go in again for the next frame.
	private static void MotionChangesPostfix(HavokSessionComponent session)
	{
		lock (DeferredMotion)
			foreach (var (body, (owner, motion, _)) in DeferredMotion)
				if (ReferenceEquals(owner, session)) session.SetBodyMotionType(body, motion);
	}

	// Reading the body is the first thing the static switch does (nothing has changed yet when it throws), so a body the
	// client's Havok lost track of skips the switch instead of crashing.
	private static Exception? MotionTypeFinalizer(Exception? __exception, DEntity body)
	{
		if (__exception is not NullReferenceException || __exception.TargetSite?.Name != "getBody") return __exception;
		GameLog.Instance.WarnOnce("motion-type-nre", $"keen guards: skipped a motion change for a physics body Havok could not read (entity {body})");
		return null;
	}

	// A character shot dead in a seat can still be recharged from the seat in the frame it is torn down; the creative check
	// read its torn-down ownership and the host died (co-op, Classic Survival, a turret killing a seated player).
	private static Exception? SeatRechargeFinalizer(Exception? __exception)
	{
		if (__exception is not NullReferenceException) return __exception;
		GameLog.Instance.WarnOnce("seat-recharge", "keen guards: skipped a seat recharge for a character torn down this frame");
		return null;
	}

	// A co-op host's own game hands its signals to the in-process server as a copied command buffer. The copy ends at the
	// buffer's allocated size, which a parallel job (an armor sector remesh, e.g. while a new lander settles) has already
	// bumped before writing its signal, so the server read a half-written signal with a stale object slot and the host died
	// (ArgumentOutOfRange in Read_..._SectorCollidersChangedSignal). That frame of the host game's signals is dropped instead.
	private static Exception? ReplaySignalsFinalizer(Exception? __exception)
	{
		if (__exception is not (ArgumentOutOfRangeException or IndexOutOfRangeException or InvalidCastException)) return __exception;
		GameLog.Instance.WarnOnce("replay-signals-torn", "keen guards: skipped one frame of this host game's signals to its server (a torn command buffer: " + __exception.Message.Split('\n')[0] + ")");
		return null;
	}

	// SE2 4.0 only sets up discovery data for identities with a connected client; an offline player's character standing
	// in a discoverable's bounds threw here every tick and stopped the server (vanilla -localDedicatedServer too).
	private static Exception? DiscoveryFinalizer(Exception? __exception, IdentityId owner, ref bool __result)
	{
		if (__exception is not NullReferenceException) return __exception;
		GameLog.Instance.WarnOnce("discovery-offline-" + (long)owner, $"keen guards: identity {(long)owner} has no discovery data yet (offline player); discovery skipped");
		__result = false;
		return null;
	}

	// Players SE2 Online adds without a Steam session of their own (other game instances of one account on a PC) have no
	// Steam id; asking Steam for their name threw inside a network story, and the fault took the server down with it.
	private static Exception? DisplayNameFinalizer(Exception? __exception, ref string? __result)
	{
		if (__exception is not ArgumentException) return __exception;
		__result = "Player";
		return null;
	}

	// One client's failed network story is that client's problem: SE2 4.0 rethrew it on the server's continuation queue
	// and the crash handler closed the server for everyone.
	// The server turning down a request (no access, the block is gone) faults the story the client awaited; a terminal
	// screen awaiting it handed that to the crash handler and the game closed. In multiplayer it is a refusal, not a crash.
	// The server sends no grids for a control panel the player may not use; Keen's panel then moved "the grid you opened
	// it from" to the top without checking it was there, and the game closed.
	private static bool SortGridsPrefix(object __instance, Keen.VRage.Multiplayer.SecureIds.EntityId mainEntity)
	{
		var grids = (System.Collections.IList)AccessTools.Field(__instance.GetType(), "_grids").GetValue(__instance)!;
		foreach (object grid in grids)
			if (Equals(AccessTools.Property(grid.GetType(), "EntityId").GetValue(grid), mainEntity)) return true;
		if (Y51_SE2Online.SyncEngine.AdminAccess.InSession)
			Y51_SE2Online.Sync.ChatLog.Add(new Y51_SE2Online.Sync.ChatLine("Server", 0, "Access denied.", Y51_SE2Online.Sync.ChatKind.System, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), MpClock.NowMs());
		return false;
	}

	// Leaving a world unloads its scenes on the main thread while the render thread still replays commands queued for
	// their entities ("deactivate this root entity"); looking the entity up then hit the freed scene and the game closed
	// on the way to the main menu. A command for an entity that no longer exists has nothing left to do.
	private static Exception? RenderEntityFinalizer(Exception? __exception, System.Reflection.MethodBase __originalMethod)
	{
		if (__exception is not (NullReferenceException or EntryPointNotFoundException)) return __exception;
		GameLog.Instance.WarnOnce("render-entity-gone", $"keen guards: render command {__originalMethod.Name} for an entity whose world was already unloaded; skipped");
		return null;
	}

	// A body handed over by the host and then seated by it switches the camera from the character to the cockpit within a
	// frame or two; saving the third-person camera's state in between read the camera's not yet set observed entity and
	// the game closed the moment a respawn ship's pilot arrived. The save is only for restoring the camera next session.
	private static Exception? CameraSyncFinalizer(Exception? __exception)
	{
		if (__exception is not NullReferenceException) return __exception;
		GameLog.Instance.WarnOnce("camera-sync", "keen guards: skipped saving the camera state while the camera changed bodies");
		return null;
	}

	// A grid's block-removal commit can run after the removed thruster's own entity was already torn down (replicated
	// removals arrive in one batch); unhooking from a torn-down entity read its freed signal table and the client died.
	// Nothing listens on a torn-down entity, so only the grid's own list needs the thruster out of it.
	private static bool ThrustUnsubscribePrefix(object __instance, Keen.Game2.Simulation.WorldObjects.CubeBlocks.CubeBlockComponent block)
	{
		if (block.Entity != null && Keen.VRage.DCS.Signals.EntityInternalSignalsApi.Constructed(block.Entity)) return true;
		if (block is Keen.Game2.Simulation.WorldObjects.CubeBlocks.FunctionalBlockComponent thruster)
			(Traverse.Create(__instance).Field("_thrusterBlocks").GetValue() as System.Collections.Generic.HashSet<Keen.Game2.Simulation.WorldObjects.CubeBlocks.FunctionalBlockComponent>)?.Remove(thruster);
		return false;
	}

	private static bool RefusedRequestPrefix(Exception ex)
	{
		if (!Y51_SE2Online.SyncEngine.AdminAccess.InSession) return true;
		for (Exception? e = ex; e != null; e = e is AggregateException a && a.InnerExceptions.Count > 0 ? a.InnerExceptions[0] : e.InnerException)
		{
			bool ownRefusal = e is ServerAccess.AccessDeniedException || (ServerAccess.RefusedLocalRecently && e.Message.StartsWith("No Entity found for EntityId", StringComparison.Ordinal));
			if (e is not Keen.VRage.Multiplayer.NetworkStories.NetworkStoryRemoteFaultException && !ownRefusal) continue;
			GameLog.Instance.Warn($"keen guards: the server refused a request ({e.Message.Split('\n')[0]}); the game goes on");
			// Only SE2 Online's own refusals are about access; SE2's faults (a block gone, a bad request) are not.
			string said = e.Message.Contains("Access denied", StringComparison.Ordinal) ? "Access denied." : "The server could not do that.";
			Y51_SE2Online.Sync.ChatLog.Add(new Y51_SE2Online.Sync.ChatLine("Server", 0, said, Y51_SE2Online.Sync.ChatKind.System, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), MpClock.NowMs());
			return false;
		}
		return true;
	}

	// The co-op host's own requests reach the server in process, and one the server turned down there (an access refusal,
	// or SE2's own throw on a block that refusal left out of the screen) came back to the host's game as the raw exception:
	// its terminal and toolbar code handed that to the crash handler (async void) or rethrew it on the continuation queue
	// (an unawaited request), and the host's game closed. Only completions arriving at the host's client transport are
	// converted, exactly as StoryFaultSerializer does for a player's on the wire (descriptor round trip: skip or remote fault).
	private static void InProcessCompletionPrefix(object __instance, ref Keen.VRage.Multiplayer.Transport.IStoryCompletion completion)
	{
		if (__instance is not Keen.VRage.Multiplayer.Transport.InProcess.InProcessClientPeerTransportSessionComponent
			|| completion is not Keen.VRage.Multiplayer.Transport.StoryFault fault || !HostFaultRule.AsRemoteFault(fault.Exception)) return;
		var raw = fault.Exception;
		GameLog.Instance.WarnOnce("in-process-story-fault", $"keen guards: a request of the host's own game failed on the server ({raw.GetType().Name}: {raw.Message.Split('\n')[0]}); it ends the way a player's failed request does (further ones not logged)");
		completion = new Keen.VRage.Multiplayer.Transport.StoryFault(Keen.VRage.Multiplayer.Transport.StoryFaultDescriptor.FromException(raw).ToException());
	}

	private static bool UnawaitedExceptionPrefix(System.Runtime.ExceptionServices.ExceptionDispatchInfo? ____exception)
	{
		if (____exception?.SourceException is not { } exception || exception.GetType().Name != "NetworkStoryRemoteFaultException") return true;
		GameLog.Instance.Warn("keen guards: a player's network story failed on their side; the server goes on (" + exception.Message + ")");
		return false;
	}

	// A signal for a player can name an entity that player was never sent (out of range, or created and removed between two
	// sends); serializing it threw "Replication entity has no network id" and the server died. It goes out as -1, the id
	// Keen already uses for stubs, and that player's client skips the reference.
	private static Exception? EntityIdFinalizer(Exception? __exception, ref int __result)
	{
		if (__exception is not Keen.VRage.Multiplayer.Replications.InProcess.NoMatchingLocalPairingException) return __exception;
		GameLog.Instance.WarnOnce("entity-no-network-id", "keen guards: a signal named an entity this player was never sent; the reference was dropped (" + __exception.Message + ")");
		__result = -1;
		return null;
	}

	// A client's wheel can touch ground whose material has no effect group yet; the lookup then threw ArgumentNullException
	// and the crash handler closed the game (seen joining a Classic Survival world).
	private static Exception? WheelAudioFinalizer(Exception? __exception, ref bool __result)
	{
		if (__exception is not ArgumentNullException) return __exception;
		GameLog.Instance.WarnOnce("wheel-audio-null", "keen guards: a wheel touched a surface without a sound group; rolling sound skipped");
		__result = false;
		return null;
	}
}
