#nullable enable
using System;
using HarmonyLib;
using Keen.Game2.Simulation.GameSystems.Ownership;
using Keen.Game2.Simulation.GameSystems.PCU;
using Keen.Game2.Simulation.GameSystems.Player;
using Keen.Game2.Simulation.WorldObjects.Characters.Stats;
using Keen.Game2.Simulation.WorldObjects.Tools;
using Keen.Game2.Simulation.WorldObjects.Tools.Voxels;
using Y51_SE2Online.Core;
using Y51_SE2Online.KeenNetcode;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// Permission checks on the server for what SE2 runs without a story SE2 Online can see: hand and ship welders and
/// grinders (build.weld, build.grind), the paint tool (build.paint), drills (voxel.drill), fast travel (fasttravel.use) and
/// a group's PCU limit (limits.pcu). Also the stat rule that splits creative tools from god mode, and the client-side F12
/// debug menu gate (debugmenu.open; the server checks every debug action as game.admin.* anyway). All default to allowed:
/// only a group file that denies a key changes anything.
/// </summary>
internal static class PermissionHooks
{
	public static long Refused;

	public static void Install(Harmony harmony)
	{
		Patch(harmony, "welders and grinders", AccessTools.Method(typeof(IntegrityToolComponent), nameof(IntegrityToolComponent.TrySetActivation)), nameof(IntegrityPrefix));
		Patch(harmony, "paint block", AccessTools.Method(typeof(PaintToolComponent), "PaintBlock"), nameof(PaintPrefix));
		Patch(harmony, "paint ship", AccessTools.Method(typeof(PaintToolComponent), "PaintShip"), nameof(PaintPrefix));
		Patch(harmony, "drills", AccessTools.Method(typeof(DrillComponent), "TrySetActionInternal"), nameof(DrillPrefix));
		Patch(harmony, "PCU limit per group", AccessTools.Method(typeof(PCUSessionComponent), nameof(PCUSessionComponent.HasPCU)), nameof(HasPcuPrefix));
		Patch(harmony, "creative tools without god mode", AccessTools.Method(typeof(StatCollectionComponent), nameof(StatCollectionComponent.TrySetValue)), nameof(StatPrefix));
		var fastTravel = typeof(Keen.Game2.Simulation.GameSystems.FastTravel.FastTravelServerSessionComponent);
		try
		{
			harmony.Patch(AccessTools.Method(fastTravel, "ValidateOperation"), postfix: new HarmonyMethod(typeof(PermissionHooks), nameof(FastTravelPostfix)));
		}
		catch (Exception ex) { GameLog.Instance.Warn($"permissions: fast travel check not installed ({ex.GetType().Name}: {ex.Message})"); }
		if (!Y51_SE2Online.Dedicated.DedicatedRuntime.Headless)
			Patch(harmony, "debug menu", AccessTools.Method(AccessTools.TypeByName("Keen.VRage.Client.DebugMenu.DebugMenuController"), "ToggleView"), nameof(DebugMenuPrefix));
	}

	private static void Patch(Harmony harmony, string what, System.Reflection.MethodBase? target, string prefix)
	{
		try
		{
			if (target == null) throw new MissingMethodException(what);
			harmony.Patch(target, prefix: new HarmonyMethod(typeof(PermissionHooks), prefix));
		}
		catch (Exception ex) { GameLog.Instance.Warn($"permissions: {what} check not installed ({ex.GetType().Name}: {ex.Message})"); }
	}

	private static bool Server => AdminAccess.Hosting;

	private static bool Denied(long identity, string key)
	{
		if (!Server || identity == 0 || ServerAccess.HasIdentity(identity, key, unknownAllowed: true)) return false;
		if (System.Threading.Interlocked.Increment(ref Refused) % 20 == 1) GameLog.Instance.Info($"permissions: identity {identity} may not use this ({key}; logged every 20th)");
		return true;
	}

	// A hand tool's owner is its character; a ship tool's is its grid's owner (the pilot is not known to the tool).
	private static void IntegrityPrefix(IntegrityToolComponent __instance, ref bool enabled)
	{
		if (!enabled || !Server) return;
		try
		{
			string key = __instance.Definition.IntegrityChange >= 0 ? "build.weld" : "build.grind";
			if (Denied((long)__instance._owner.Owner, key)) enabled = false;
		}
		catch (Exception) { }
	}

	private static bool PaintPrefix(PaintToolComponent __instance)
	{
		try { return !Denied((long)__instance._owner.Owner, "build.paint"); }
		catch (Exception) { return true; }
	}

	private static void DrillPrefix(DrillComponent __instance, ref bool enabled)
	{
		if (!enabled || !Server) return;
		try
		{
			var owner = MpPlugin.App?.ServerSession?.SessionComponents.TryGet<OwnershipSessionComponent>()?.TryGetOwnerOf(__instance.Entity);
			if (owner is { } id && Denied((long)id, "voxel.drill")) enabled = false;
		}
		catch (Exception) { }
	}

	// The game's limit applies to everyone; a group's limits.pcu (0 or more) lowers it for that group's players.
	private static bool HasPcuPrefix(PCUSessionComponent __instance, IdentityId owner, int requiredPCU, ref bool __result)
	{
		if (!Server || !PlayerIdentities.IsHuman((long)owner) || AdminAccess.Host is not { } host) return true;
		ulong steam = host.SteamOfIdentity((long)owner);
		if (steam == 0) return true;
		double limit = host.Perms.Value(steam, "limits.pcu");
		if (limit < 0 || limit >= __instance.MaxPerPlayerPCU) return true;
		__result = __instance.GetPCU(owner) + requiredPCU <= limit;
		return false;
	}

	// Personal creative tools build without resources but keep survival stats (damage, oxygen, energy); god mode keeps
	// stats as the game's creative mode does. A world in creative mode for everyone is the game's own business.
	private static bool StatPrefix(StatCollectionComponent __instance, StatDefinition stat, ref bool ignoreCreative, ref bool __result)
	{
		if (ignoreCreative || stat.ModifiableInCreative || !MpPlugin.Online) return true;
		try
		{
			if (__instance._gameMode is not GameModeSessionComponent mode || mode.CreativeGlobal) return true;
			bool god = mode.IsInvulnerable(__instance.Entity);
			bool creative = mode.IsCreative(__instance.Entity);
			if (god)
			{
				if (creative) return true;
				__result = false;
				return false;
			}
			if (creative) ignoreCreative = true;
		}
		catch (Exception) { }
		return true;
	}

	private static void FastTravelPostfix(object __instance, Keen.VRage.Multiplayer.ClientId client, object operation)
	{
		if (!Server) return;
		try
		{
			var result = AccessTools.Property(operation.GetType(), "Result");
			if (result == null || !Equals(result.GetValue(operation), Keen.Game2.Simulation.GameSystems.FastTravel.FastTravelResult.Validated)) return;
			long identity = (long)ServerAccess.IdentityOf(client);
			if (Denied(identity, "fasttravel.use")) result.SetValue(operation, Keen.Game2.Simulation.GameSystems.FastTravel.FastTravelResult.InvalidDestination);
		}
		catch (Exception ex) { GameLog.Instance.WarnOnce("fasttravel-permission", "permissions: fast travel check failed: " + ex.Message); }
	}

	private static bool DebugMenuPrefix()
	{
		if (!AdminAccess.Joined || AdminAccess.State.Can("debugmenu.open")) return true;
		MpMenu.Report("The game's debug menu (F12) is not available for your group on " + AdminAccess.ServerName + ".");
		return false;
	}
}
