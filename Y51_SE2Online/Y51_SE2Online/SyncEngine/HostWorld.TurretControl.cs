using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Keen.Game2.Simulation.StreamedUI.Terminal.ControlPanel.BlockDetails;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.Weapons;
using Keen.VRage.DCS.Components;
using Keen.VRage.Multiplayer.Replications;
using Keen.VRage.Multiplayer.Replications.FailureStrategies;
using Keen.VRage.Multiplayer.Replications.InProcess;
using Keen.VRage.Multiplayer.Sessions;
using Y51_SE2Online.Core;
using Y51_SE2Online.Dedicated;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

/// <summary>Direct turret control (gatling and rocket turrets) goes to the player who took the turret: <see cref="TurretHolders{T}"/>.</summary>
internal sealed partial class HostWorld
{
	private const string TurretControlSignal = "Keen.Game2.Simulation.WorldObjects.CubeBlocks.Weapons.AutoTargetingComponent+SyncControlSignal";

	internal static readonly TurretHolders<Entity> Turrets = new();

	private static int _turretControlStub = -1;

	[ThreadStatic]
	private static List<(int Offset, Segment Seg)>? _turretScratch;

	// The server-side turret panel model each player's "Control" toggle edits. On a listen host a player's edit reaches the
	// server later, through the host's own client, so no story or edit of that player is running when SetControl does.
	private static readonly ConditionalWeakTable<object, StrongBox<ushort>> _panelPlayers = new();

	public static void InstallTurretControl(Harmony harmony)
	{
		harmony.Patch(AccessTools.Method(typeof(AutoTargetingComponent), nameof(AutoTargetingComponent.SetControl)), new HarmonyMethod(typeof(HostWorld), nameof(TurretControlPrefix)));
		harmony.Patch(AccessTools.DeclaredMethod(typeof(AutomatedTurretBlockDetailModelServer), "OnPropertyChanged", new[] { typeof(object), typeof(PropertyChangedEventArgs) }), new HarmonyMethod(typeof(HostWorld), nameof(TurretPanelPrefix)), finalizer: new HarmonyMethod(typeof(HostWorld), nameof(TurretPanelFinalizer)));
	}

	internal static void NoteEditOrigin(object replica, ushort clientId)
	{
		if (replica is not AutomatedTurretBlockDetailModel)
		{
			return;
		}
		object? server = replica;
		if (!HeadlessReplication.Active && Current?._serverComp is InProcessReplicationSessionComponent comp)
		{
			bool skip = false;
			server = comp.ReplayContext.TranslateObject(replica, ReplicationFailureStrategy.NullOnAnyFail, ref skip);
		}
		if (server != null)
		{
			_panelPlayers.AddOrUpdate(server, new StrongBox<ushort>(clientId));
		}
	}

	private static void TurretPanelPrefix(AutomatedTurretBlockDetailModelServer __instance, out ushort __state)
	{
		__state = StoryActor;
		if (_panelPlayers.TryGetValue(__instance, out var player))
		{
			StoryActor = player.Value;
		}
	}

	private static void TurretPanelFinalizer(ushort __state) => StoryActor = __state;

	// The panel sync (AutomatedTurretBlockDetailModelServer.OnWeaponChanged) sets every other open panel of the turret,
	// which calls SetControl again with the same value: that would re-send SyncControl(true), and the holder's client
	// answers a second one by releasing the turret.
	private static bool TurretControlPrefix(AutoTargetingComponent __instance, bool control)
	{
		if (control == __instance.IsControlling)
		{
			return false;
		}
		ushort actor = StoryActor;
		TurretTag? tag = control && actor > TurretHolders<Entity>.Host && Current?._joiners.TryGetValue(actor, out var j) == true ? new TurretTag(j, j.Character) : null;
		Turrets.SetControl(__instance.Entity, control, actor, tag);
		return true;
	}

	private sealed record TurretTag(Joiner Joiner, Entity? Body);

	/// <summary>
	/// Server thread: a turret whose holder left, was kicked, died or respawned is released like the holder's own release,
	/// so it goes back to auto-targeting and anyone can take it. Nobody else could: only the holder's release is accepted.
	/// </summary>
	private void UpdateTurrets()
	{
		foreach (var (turret, client, tag) in Turrets.Held())
		{
			if (EngineCodec.IsDead(turret) || turret.TryGet<AutoTargetingComponent>() is not { } targeting)
			{
				Turrets.Forget(turret);
				continue;
			}
			if (client <= TurretHolders<Entity>.Host || tag is not TurretTag held)
			{
				continue;
			}
			bool left = !_joiners.TryGetValue(client, out var now) || now != held.Joiner;
			if (!left && held.Joiner.Character == held.Body && held.Body != null && !EngineCodec.IsDead(held.Body))
			{
				continue;
			}
			try
			{
				targeting.SetControl(false);
				TurretsReleased++;
				_log.Info($"host world sync: ClientId {client} '{held.Joiner.Name}' {(left ? "left" : "lost its character")} while controlling a turret; released it back to auto-targeting");
			}
			catch (Exception ex)
			{
				_log.WarnOnce("turret-release", $"host world sync: releasing a turret of ClientId {client} failed ({ex.GetType().Name}: {ex.Message})");
			}
			// Gone players get no release signal; a player still here does, so their game leaves the turret view.
			if (left) Turrets.Forget(turret);
		}
	}

	internal static long TurretsReleased;

	/// <summary>A turret's SyncControl signal: true, with its holder in <paramref name="to"/> (null: the host or nobody).</summary>
	private bool IsTurretControl(CommandLayout layout, ReplicationCommandBuffer cb, ReadOnlySpan<byte> payload, ref Joiner? to)
	{
		if (layout.Name != TurretControlSignal)
		{
			return false;
		}
		int handle = payload.Length >= 4 ? BinaryPrimitives.ReadInt32LittleEndian(payload) : -1;
		ushort client = handle >= 0 ? Turrets.AddresseeOf(cb.ReadObject(handle) as Entity) : TurretHolders<Entity>.Nobody;
		if (client > TurretHolders<Entity>.Host)
		{
			_joiners.TryGetValue(client, out to);
		}
		return true;
	}

	/// <summary>
	/// Listen host: the length to skip when the host's own game is about to apply the SyncControl signal of a turret a
	/// player holds (that player alone possesses it), else -1.
	/// </summary>
	internal static int HostGameTurretSkip(int stub, ReadOnlySpan<byte> payload, ReplicationCommandBuffer cb)
	{
		int known = _turretControlStub;
		if (known >= 0 ? stub != known : CommandLayouts.ByStub(stub)?.Name != TurretControlSignal)
		{
			return -1;
		}
		_turretControlStub = stub;
		int handle = payload.Length >= 4 ? BinaryPrimitives.ReadInt32LittleEndian(payload) : -1;
		if (handle < 0 || Turrets.AddresseeOf(cb.ReadObject(handle) as Entity) <= TurretHolders<Entity>.Host)
		{
			return -1;
		}
		int length = CommandLayouts.ByStub(stub)?.Frame(payload, _turretScratch ??= new List<(int, Segment)>()) ?? -1;
		return length >= 0 && length <= payload.Length ? length : -1;
	}
}
