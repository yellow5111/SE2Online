using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Keen.Game2.Simulation.WorldObjects.Characters;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.Pilotable;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.DCS.Components;
using Keen.VRage.Multiplayer.Annotations;
using Keen.VRage.Multiplayer.Transport.OutOfProcess.Server;
using Y51_SE2Online.Core;
using Y51_SE2Online.SyncEngine;
using Transport = Keen.VRage.Multiplayer.Transport.OutOfProcess.OutOfProcessPeerTransport;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>
/// SE2 4.0's server writes every replicated entity data a client sends straight onto the entity it names; [SyncFromClients]
/// is only checked on the sending side. A modified client could fly any unpiloted ship it could see and write or remove
/// transforms, stats and the rest on any character or grid. The server now keeps a client's entry only when its type is
/// [SyncFromClients] (the flight and look inputs) and it lands on that player's own character or the ship they sit in
/// (2.4's "not a client input type" and seated-pilot checks). Everything else is dropped before it is applied.
/// </summary>
internal static class ClientDataGuard
{
	// After standing up, the client's last inputs (keys let go, target lock removed) may arrive after the seat is empty.
	private const long LeftSeatGraceMs = 3000;

	public static long Dropped;

	private static readonly ConcurrentDictionary<Type, bool> FromClients = new();
	private static readonly ConcurrentDictionary<Type, Action<object, Sender>> PendingFilters = new();
	private static readonly ConcurrentDictionary<Type, Action<object, Sender>> SampledFilters = new();
	private static readonly ConditionalWeakTable<Transport, Sender> Senders = new();

	public static void Install(Harmony harmony)
	{
		harmony.Patch(AccessTools.Method(typeof(Transport), "ProcessIncomingData"), prefix: new HarmonyMethod(typeof(ClientDataGuard), nameof(DataPrefix)));
		harmony.Patch(AccessTools.Method(typeof(Transport), "ProcessIncomingSampledData"), prefix: new HarmonyMethod(typeof(ClientDataGuard), nameof(SampledPrefix)));
	}

	private static bool IsFromClients(Type type) => FromClients.GetOrAdd(type, t => t.IsDefined(typeof(SyncFromClients), inherit: false));

	private static void DataPrefix(Transport __instance, Transport.OutOfProcessDataBatch dataBatch)
	{
		if (SenderFor(__instance) is not { } sender) return;
		try
		{
			var lists = dataBatch.DataLists;
			for (int i = lists.Count - 1; i >= 0; i--)
			{
				var list = lists[i];
				Type data = list.GetType().GetGenericArguments()[0];
				if (!IsFromClients(data))
				{
					sender.Drop(data.Name, ((System.Collections.ICollection)list.GetType().GetField("Data")!.GetValue(list)!).Count);
					lists.RemoveAt(i);
					continue;
				}
				PendingFilters.GetOrAdd(data, t => Filter(nameof(FilterPending), t))(list, sender);
			}
			sender.Drop("removal", dataBatch.Removals.RemoveAll(r => !IsFromClients(r.DataType) || !sender.Allows(r.Target)));
		}
		catch (Exception ex)
		{
			// Failing open here would hand a modified client the whole world again; dropping the batch only costs one tick of input.
			GameLog.Instance.WarnOnce("client-data-guard", $"client data: could not check ClientId {__instance.PeerClientId.Id}'s data ({ex.GetType().Name}: {ex.Message}); dropped");
			dataBatch.DataLists.Clear();
			dataBatch.Removals.Clear();
		}
	}

	private static void SampledPrefix(Transport __instance, Transport.SampledDataBatch batch)
	{
		if (SenderFor(__instance) is not { } sender) return;
		try
		{
			var lists = batch.Lists;
			for (int i = lists.Count - 1; i >= 0; i--)
			{
				var list = lists[i];
				Type data = list.GetType().GetGenericArguments()[0];
				if (!IsFromClients(data))
				{
					sender.Drop(data.Name, ((System.Collections.ICollection)list.GetType().GetField("Entries")!.GetValue(list)!).Count);
					lists.RemoveAt(i);
					continue;
				}
				SampledFilters.GetOrAdd(data, t => Filter(nameof(FilterSampled), t))(list, sender);
			}
			sender.Drop("removal", batch.Removals.RemoveAll(r => !IsFromClients(r.DataType) || !sender.Allows(sender.Resolve(r.Id))));
		}
		catch (Exception ex)
		{
			GameLog.Instance.WarnOnce("client-sampled-guard", $"client data: could not check ClientId {__instance.PeerClientId.Id}'s sampled data ({ex.GetType().Name}: {ex.Message}); dropped");
			batch.Lists.Clear();
			batch.Removals.Clear();
		}
	}

	private static Action<object, Sender> Filter(string method, Type data)
		=> AccessTools.Method(typeof(ClientDataGuard), method).MakeGenericMethod(data).CreateDelegate<Action<object, Sender>>();

	private static void FilterPending<T>(object list, Sender sender) where T : unmanaged
		=> sender.Drop(typeof(T).Name, ((Transport.PendingList<T>)list).Data.RemoveAll(d => !sender.Allows(d.Target)));

	private static void FilterSampled<T>(object list, Sender sender) where T : unmanaged
		=> sender.Drop(typeof(T).Name, ((Transport.SampledList<T>)list).Entries.RemoveAll(e => !sender.Allows(sender.Resolve(e.Id))));

	private static Sender? SenderFor(Transport transport)
	{
		if (!AdminAccess.Hosting || transport is not OutOfProcessServerPeerTransport) return null;
		var sender = Senders.GetValue(transport, t => new Sender(t));
		sender.Refresh();
		return sender;
	}

	/// <summary>One client's own character and the ship it sits in, kept between batches.</summary>
	private sealed class Sender(Transport transport)
	{
		private long _identity, _now, _scannedMs, _leftMs;
		private Entity? _character, _seatGrid, _leftGrid;

		public void Refresh()
		{
			_now = MpClock.NowMs();
			long identity = (long)ServerAccess.IdentityOf(transport.PeerClientId);
			if (identity != _identity)
			{
				_identity = identity;
				_character = null;
				_scannedMs = 0;
			}
			if (_character != null && !Owned(_character)) _character = null;
			if (_character == null && identity != 0 && _now - _scannedMs >= 1000)
			{
				_scannedMs = _now;
				if (MpPlugin.App?.ServerSession is { } session)
					foreach (Entity character in session.GetEntitiesOfType<CharacterLifeComponent>())
						if (Owned(character)) { _character = character; break; }
			}
			var grid = _character?.FirstOrDefault<ISeatable>()?.CurrentSeat?.Entity.GetTopLevelParent();
			if (grid == _seatGrid) return;
			if (_seatGrid != null)
			{
				_leftGrid = _seatGrid;
				_leftMs = _now;
			}
			_seatGrid = grid;
		}

		private bool Owned(Entity character)
			=> character.Scene.IsEntityAlive(character.DEntity) && character.TryGet<CharacterLifeComponent>() is { IsAlive: true } && TeamPermissions.OwnerOf(character) == _identity;

		public Entity? Resolve(Keen.VRage.Multiplayer.Transport.NetworkId id) => transport.ResolveSampledTarget(id, transport.SessionComponent.Entity.Scene);

		public bool Allows(Entity? target)
		{
			if (target == null || _identity == 0) return false;
			if (target == _character) return true;
			// Their own character, also a body or one this sender has not been matched to yet (just respawned).
			if (target.TryGet<CharacterComponent>() != null) return TeamPermissions.OwnerOf(target) == _identity;
			var root = target.GetTopLevelParent();
			return root == _seatGrid || (root == _leftGrid && _now - _leftMs <= LeftSeatGraceMs);
		}

		public void Drop(string what, int count)
		{
			if (count <= 0) return;
			Dropped += count;
			if (ServerAccess.LogDue(transport.PeerClientId, "data"))
				GameLog.Instance.Info($"client data: dropped {count} {what} entries from ClientId {transport.PeerClientId.Id} (identity {_identity}): not that player's character or ship (further ones within 5 s not logged)");
		}
	}
}
