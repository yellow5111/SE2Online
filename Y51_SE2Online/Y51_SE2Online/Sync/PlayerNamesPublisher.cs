using System;
using System.Collections.Generic;
using System.Linq;
using Y51_SE2Online.Core;
using Y51_SE2Online.Net;

namespace Y51_SE2Online.Sync;

public sealed class PlayerNamesPublisher
{
	private readonly ITransport _t;

	private readonly IMpLog _log;

	private readonly PlayerRoster _roster;

	private readonly Func<PeerId, bool> _isSynced;

	private readonly Dictionary<PeerId, uint> _sent = new Dictionary<PeerId, uint>();

	private uint _loggedVersion;

	private readonly Func<PeerId, bool>? _takesPatches;

	private readonly Dictionary<PeerId, DocState> _docs = new Dictionary<PeerId, DocState>();

	private readonly DocDiffCache _diffs = new DocDiffCache();

	public long Messages;

	public PlayerNamesPublisher(ITransport transport, IMpLog log, PlayerRoster roster, Func<PeerId, bool> isSynced, Func<PeerId, bool>? takesPatches = null)
	{
		_t = transport;
		_log = log;
		_roster = roster;
		_isSynced = isSynced;
		_takesPatches = takesPatches;
		_t.PeerDisconnected += (PeerId p, DisconnectInfo _) =>
		{
			_sent.Remove(p);
			_docs.Remove(p);
		};
		_t.MessageReceived += (PeerId p, Channel _, byte[] data) =>
		{
			// A client lost its copy of the tags; the next tick sends them whole.
			if (data.Length == 2 && data[0] == (byte)SyncMsg.RosterSync && data[1] == (byte)SyncMsg.PlayerNames && _docs.TryGetValue(p, out var doc))
			{
				doc.RequestFull();
				_sent.Remove(p);
			}
		};
	}

	public void Tick()
	{
		var (num, tags) = _roster.Snapshot();
		if (num == 0)
		{
			return;
		}
		byte[] array = null;
		JsonDoc doc = null;
		foreach (PeerId peer in _t.Peers)
		{
			if ((!_sent.TryGetValue(peer, out var value) || value != num) && _isSynced(peer))
			{
				if (_takesPatches?.Invoke(peer) == true)
				{
					if (doc == null)
					{
						_diffs.Clear();
						doc = JsonDoc.Of(new PlayerNamesDoc { Version = num, Tags = tags.Select(t => t with { Name = HostPresence.Clean(t.Name, PlayerNamesMessage.MaxNameChars) }).ToList() });
					}
					if (!_docs.TryGetValue(peer, out var state)) _docs[peer] = state = new DocState();
					byte[] data = state.Next((byte)SyncMsg.PlayerNames, doc, _diffs);
					if (data != null) _t.Send(peer, RosterSync.LaneOf(data), data);
				}
				else
				{
					if (array == null)
					{
						array = PlayerNamesMessage.Encode(num, tags);
					}
					_t.Send(peer, Channel.Control, array);
				}
				_sent[peer] = num;
				Messages++;
			}
		}
		_diffs.Clear();
		if (array != null && _loggedVersion != num)
		{
			_loggedVersion = num;
			_log.Info("nametags(host): player list " + _roster.Describe() + " sent to the joined players");
		}
	}
}
