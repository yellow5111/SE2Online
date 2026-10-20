using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Keen.Game2.Simulation.GameSystems.Identities;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.Pilotable;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.Game2.Simulation.WorldObjects.Movement;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.DCS.Components;
using Keen.VRage.Multiplayer.Transport.OutOfProcess;
using Y51_SE2Online.Core;
using Y51_SE2Online.SyncEngine;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>
/// One pilot per ship on the server (2.4's PassengerGate). SE2 4.0 takes a ship's flight input (control, angular,
/// target, orientation and look data) from any client without asking who sends it, so a second player in another
/// cockpit of the same ship steered it too. With the host's one-pilot setting on, the player who sat down first in one
/// of the ship's control seats flies it; flight input any other client sends for that ship is dropped before it is
/// applied. Passenger seats (beds, toilets, seats) are never control seats.
/// </summary>
internal static class OnePilot
{
	private static readonly HashSet<Type> ControlTypes = new()
	{
		typeof(ControlData), typeof(AngularControlData), typeof(TargetControlData), typeof(ControlReferenceOrientation), typeof(LookOffsetData)
	};

	// When each seat's current pilot was first seen in it: the earliest still seated flies the ship.
	private static readonly ConditionalWeakTable<SeatComponent, StrongBox<(Entity? Pilot, long Since)>> Seated = new();

	public static long Dropped;

	public static void Install(Harmony harmony)
	{
		harmony.Patch(AccessTools.Method(typeof(OutOfProcessPeerTransport), "ProcessIncomingData"), prefix: new HarmonyMethod(typeof(OnePilot), nameof(IncomingPrefix)));
	}

	private static void IncomingPrefix(OutOfProcessPeerTransport __instance, object dataBatch)
	{
		if (!AdminAccess.Hosting || !(HostWorld.Current?.OnePilot ?? true) || __instance is not Keen.VRage.Multiplayer.Transport.OutOfProcess.Server.OutOfProcessServerPeerTransport) return;
		if (dataBatch.GetType().GetField("DataLists")?.GetValue(dataBatch) is not IList lists) return;
		long sender = 0;
		Dictionary<Entity, long>? pilots = null;
		foreach (object? list in lists)
		{
			var type = list?.GetType();
			if (type is not { IsGenericType: true } || !ControlTypes.Contains(type.GetGenericArguments()[0]) || type.GetField("Data")?.GetValue(list) is not IList entries) continue;
			for (int i = entries.Count - 1; i >= 0; i--)
			{
				if (entries[i]?.GetType().GetField("Target")?.GetValue(entries[i]) is not Entity target) continue;
				var root = target.GetTopLevelParent();
				if (root.TryGet<CubeGridComponent>() == null) continue;
				if (sender == 0)
				{
					var identities = MpPlugin.App?.ServerSession?.SessionComponents.TryGet<IdentitiesSessionComponent>();
					if (identities == null) return;
					sender = (long)identities.GetIdentityFromClient(__instance.PeerClientId);
				}
				pilots ??= new Dictionary<Entity, long>(ReferenceEqualityComparer.Instance);
				if (!pilots.TryGetValue(root, out long pilot)) pilots[root] = pilot = PilotOf(root);
				if (pilot == 0 || pilot == sender) continue;
				entries.RemoveAt(i);
				if (Dropped++ == 0) GameLog.Instance.Info($"one pilot: identity {sender}'s flight input for a ship identity {pilot} flies is ignored (further ones counted)");
			}
		}
	}

	/// <summary>Identity of the player who has sat longest in one of the ship's control seats, or 0.</summary>
	private static long PilotOf(Entity root)
	{
		long now = MpClock.NowMs(), best = long.MaxValue, owner = 0;
		root.Get<CubeGridComponent>().VisitAllBlocksWithComponent((SeatComponent seat) =>
		{
			var pilot = seat.Pilot;
			if (pilot == null || seat.Entity.TryGet<CockpitComponent>() == null || PassengerSeats.IsPassengerSeat(seat.Entity)) return;
			var box = Seated.GetValue(seat, _ => new StrongBox<(Entity?, long)>((null, 0)));
			if (box.Value.Pilot != pilot) box.Value = (pilot, now);
			if (box.Value.Since < best) { best = box.Value.Since; owner = TeamPermissions.OwnerOf(pilot); }
		});
		return owner;
	}
}
