using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Keen.Game2.Simulation.WorldObjects.Characters;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Mathematics;
using Y51_SE2Online.Core;
using Y51_SE2Online.Dedicated;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// Suit colour and skin (SkinComponent, the whole character customisation in SE2 2.4). Both are [SyncFromClients]
/// properties: a player's change reaches the server, and the server replays it without writing it back out, so
/// nobody else hears of it. Players' changes are checked here and relayed by HostWorld.Inject; the host's own change
/// is relayed from the server's change handlers.
/// </summary>
internal static class SuitSync
{
	public static readonly string[] Signals =
	{
		typeof(SkinComponent.SuitColorProperty).FullName!,
		typeof(SkinComponent.SuitSkinProperty).FullName!
	};

	public const byte Color = 0, Skin = 1;

	public static void Install(Harmony harmony)
	{
		harmony.Patch(AccessTools.Method(typeof(SkinComponent), "SuitColorChanged"), postfix: new HarmonyMethod(typeof(SuitSync), nameof(ColorChangedPostfix)));
		harmony.Patch(AccessTools.Method(typeof(SkinComponent), "SuitSkinChanged"), postfix: new HarmonyMethod(typeof(SuitSync), nameof(SkinChangedPostfix)));
		harmony.Patch(AccessTools.Method(typeof(SkinComponent), "Init", new[] { typeof(SkinComponentObjectBuilder) }), postfix: new HarmonyMethod(typeof(SuitSync), nameof(InitPostfix)));
	}

	private static void ColorChangedPostfix(SkinComponent __instance) => HostWorld.Current?.OnSuitChanged(__instance, Color);

	private static void SkinChangedPostfix(SkinComponent __instance) => HostWorld.Current?.OnSuitChanged(__instance, Skin);

	// Init swaps a DLC skin for the default unless this PC owns the DLC. Online that is the viewer's (or the headless
	// server's) DLC, not the wearer's; SE2's suit menu only lets owners pick it.
	private static void InitPostfix(SkinComponentObjectBuilder ob, ref RenderSkinDefinition ____suitSkin)
	{
		if (ob.SuitSkin != null && (HostWorld.Current != null || ClientWorld.Current != null || DedicatedRuntime.Headless))
		{
			____suitSkin = ob.SuitSkin;
		}
	}

	/// <summary>Host side check of a player's suit change; clamps the colour in place. Null when it may go on.</summary>
	public static string? Refuse(int kind, WireCommand cmd, uint ownCharacter)
	{
		if (cmd.Payload.Length != 8 || cmd.Refs.Count != kind + 1 || cmd.Refs[0].Kind != WireKind.Entity)
		{
			return "malformed suit change";
		}
		if (ownCharacter == 0 || cmd.Refs[0].Id != ownCharacter)
		{
			return "not the player's own character";
		}
		if (kind == Color)
		{
			SuitRate<WireCommand>.ClampColor(cmd.Payload.AsSpan(4));
			return null;
		}
		return cmd.Refs[1].Kind == WireKind.Definition && ObjectCodec.FindDefinition(cmd.Refs[1].Guid, typeof(RenderSkinDefinition)) is RenderSkinDefinition
			? null
			: "unknown suit skin";
	}
}

internal sealed partial class HostWorld
{
	private readonly SuitRate<WireCommand> _suitRate = new();

	private IEnumerable<(string Signal, WireCommand Command)> SuitGate(ClientInputBatch b)
	{
		long now = MpClock.NowMs();
		for (byte kind = 0; kind < SuitSync.Signals.Length; kind++)
		{
			if (_suitRate.Due(b.ClientId, kind, now) is WireCommand waiting)
			{
				yield return (SuitSync.Signals[kind], waiting);
			}
		}
		foreach (var (signal, cmd) in b.Commands)
		{
			int kind = Array.IndexOf(SuitSync.Signals, signal);
			if (kind < 0)
			{
				yield return (signal, cmd);
				continue;
			}
			string? why = SuitSync.Refuse(kind, cmd, _joiners.TryGetValue(b.ClientId, out var j) ? j.NetId : 0);
			if (why != null)
			{
				NoteC2SDrop(signal, why);
			}
			else if (_suitRate.Offer(b.ClientId, (byte)kind, cmd, now))
			{
				yield return (signal, cmd);
			}
		}
	}

	/// <summary>Server thread, after SkinComponent's [Server] change handler.</summary>
	internal void OnSuitChanged(SkinComponent skin, byte kind)
	{
		var host = _host;
		Entity entity = skin.Entity;
		if (host == null || !host.AnySynced || _disabled || !_ids.TryGet(entity, out var id) || _joiners.Values.Any(j => j.Character == entity))
		{
			return;
		}
		var layout = CommandLayouts.ByName(SuitSync.Signals[kind]);
		if (layout == null || !layout.Forwardable || layout.PayloadBytes != 8)
		{
			return;
		}
		var command = new WireCommand
		{
			Key = host.SignalKeys.GetOrAdd(layout.Name, layout.Def(0).PayloadBytes, layout.Signature).Key,
			Payload = new byte[8]
		};
		command.Refs.Add(WireValue.Ent(id));
		if (kind == SuitSync.Color)
		{
			BinaryPrimitives.WriteUInt32LittleEndian(command.Payload.AsSpan(4), skin.SuitColor.PackedValue);
		}
		else if (skin.SuitSkin != null)
		{
			command.Refs.Add(WireValue.Def(skin.SuitSkin.Guid));
		}
		else
		{
			return;
		}
		lock (_signalSync)
		{
			_pendingSignals.Add(command);
		}
	}
}
