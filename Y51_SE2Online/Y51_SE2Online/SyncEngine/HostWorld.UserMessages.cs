using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using HarmonyLib;
using Keen.Game2.Simulation.GameSystems.UserMessages;
using Keen.VRage.DCS.Components;
using Keen.VRage.Multiplayer.Sessions;
using Y51_SE2Online.Net;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// The server's user message popups (contract completed, undo/redo results, respawn notices) are one session-wide
/// signal without an addressee, so the host saw every player's popups and the players saw none. A popup raised while
/// a player's story runs on the server is that player's and goes to them alone; any other stays with the host.
/// </summary>
internal sealed partial class HostWorld
{
	/// <summary>The player whose story the server is running on this thread right now (0: none, 1: the host).</summary>
	// ponytail: only the story's synchronous part counts; code after an await that resumes later runs without an actor,
	// and its popups stay with the host as before. Carry the actor through Keen's task continuations if that matters.
	[ThreadStatic]
	internal static ushort StoryActor;

	private const string DisplayMessageSignal = "Keen.Game2.Simulation.GameSystems.UserMessages.UserMessagesSystemServerSessionComponent+RequestDisplayMessageSignal";

	private const string RemoveMessageSignal = "Keen.Game2.Simulation.GameSystems.UserMessages.UserMessagesSystemServerSessionComponent+RequestRemoveMessageSignal";

	private const string ItemTransferSignal = "Keen.Game2.Simulation.WorldObjects.Items.InventoryComponent+ItemTransferSignal";

	private const int MessageAddresseeCap = 4096;

	private readonly ConcurrentDictionary<int, ushort> _messageTo = new();

	private List<(PeerId Peer, WireCommand Command)> _addressedSignals = new();

	public static void InstallUserMessages(Harmony harmony)
	{
		// DisplayMessage, not the signal trigger it calls: the JIT inlines that one-liner, so a patch on it never runs.
		harmony.Patch(AccessTools.Method(typeof(UserMessagesSystemServerSessionComponent), nameof(UserMessagesSystemServerSessionComponent.DisplayMessage)), new HarmonyMethod(typeof(HostWorld), nameof(DisplayMessagePrefix)));
	}

	private static void DisplayMessagePrefix(UserMessagesSystemServerSessionComponent __instance)
	{
		int id = __instance._id;
		ushort actor = StoryActor;
		if (actor <= 1 || Current is not { } world)
		{
			return;
		}
		if (world._messageTo.Count >= MessageAddresseeCap)
		{
			world._messageTo.Clear();
		}
		world._messageTo[id] = actor;
	}

	/// <summary>
	/// For a user message, item transfer or turret control signal: true, with the player it is for in <paramref name="to"/> (null: nobody
	/// but the host). False for every other signal.
	/// </summary>
	private bool IsAddressedSignal(CommandLayout layout, ReplicationCommandBuffer cb, ReadOnlySpan<byte> payload, out Joiner? to)
	{
		to = null;
		if (IsTurretControl(layout, cb, payload, ref to))
		{
			return true;
		}
		if (layout.Name == ItemTransferSignal)
		{
			// Raised on the inventory's entity; only a player's character shows it (CharacterControllableComponent).
			int handle = payload.Length >= 4 ? BinaryPrimitives.ReadInt32LittleEndian(payload) : -1;
			if (handle >= 0 && cb.ReadObject(handle) is Entity target)
			{
				foreach (Joiner joiner in _joiners.Values)
				{
					if (ReferenceEquals(joiner.Character, target))
					{
						to = joiner;
						break;
					}
				}
			}
			return true;
		}
		// Payload: the session entity's reference, then (display) the message's reference, then the message id.
		int at = layout.Name == DisplayMessageSignal ? 8 : layout.Name == RemoveMessageSignal ? 4 : -1;
		if (at < 0)
		{
			return false;
		}
		if (payload.Length >= at + 4)
		{
			int id = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(at));
			ushort client;
			bool known = at == 4 ? _messageTo.TryRemove(id, out client) : _messageTo.TryGetValue(id, out client);
			if (known)
			{
				_joiners.TryGetValue(client, out to);
			}
		}
		return true;
	}

	private void FlushAddressedSignals(HostSession host)
	{
		List<(PeerId Peer, WireCommand Command)> pending;
		lock (_signalSync)
		{
			if (_addressedSignals.Count == 0)
			{
				return;
			}
			pending = _addressedSignals;
			_addressedSignals = new();
		}
		var byPeer = new Dictionary<PeerId, List<WireCommand>>();
		foreach (var (peer, command) in pending)
		{
			if (!byPeer.TryGetValue(peer, out var list))
			{
				byPeer[peer] = list = new List<WireCommand>();
			}
			list.Add(command);
		}
		foreach (var (peer, commands) in byPeer)
		{
			host.PublishSignals(_hostTick, commands, null, peer);
		}
	}
}
