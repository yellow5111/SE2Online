using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Y51_SE2Online.Core;
using Y51_SE2Online.Net;

namespace Y51_SE2Online.Sync;

public sealed partial class HostSession : IStorySink
{
	public enum ClientStatus
	{
		Connected,
		WaitingForWorld,
		CaptureRequested,
		Synced
	}

	private static readonly HashSet<uint> NothingStreamedOut = new();

	private sealed class RemoteClient
	{
		public PeerId Peer;

		/// <summary>Root NetIds this joiner does not have (out of its streaming radius); replaced whole, never mutated.</summary>
		public volatile HashSet<uint> StreamedOut = NothingStreamedOut;

		public bool Wants(uint root) => root == 0 || !StreamedOut.Contains(root);

		public string Name = "?";

		public ClientStatus Status;

		public bool AdminStateSent;

		public bool Removed;

		public uint NextLifeSeq = 1u;

		public StateCoalescer State;

		public long JoinedMs;

		public long SyncedMs;

		public long LifecycleMessages;

		public long LifecycleBytes;

		public long StateBytes;

		public long BlobsSent;

		public long BlobBytes;

		public readonly HashSet<Guid> SentBlobs = new();

		public PlayerRecord? Player;

		public (int Pcu, int Max)? LastStatus;

		public readonly Dictionary<ushort, long> SentByKeyAtLastStatus = new();

		public readonly object Input = new();

		public readonly RemoteTypeTable InputTypes = new();

		public readonly Dictionary<ushort, (SignalKeyDef Def, string? Problem)> Signals = new();

		public readonly Dictionary<(uint, ushort), (uint Seq, byte[] Bytes, bool Dirty)> InputData = new();

		public long InputOverflow;

		public readonly List<(string Signal, WireCommand Command)> InputCommands = new();

		public LookAccumulator? Look;

		public long LookIgnored;

		public long LookControlWrites;

		public readonly Phase2Stats P2 = new();

		public uint LastCommandSeq;

		public long InputMessages;

		public long InputEntries;

		public long InputRejected;

		public int InputRefusedLogged;

		public long InputRemoved;

		public long CommandsIn;

		public long CommandsRejected;

		public long InputApplied;

		public long CommandsApplied;

		public long InputGaps;

		public long InputGapMaxMs;

		public long InputLastArrivalMs = -1L;

		public long TicksWithoutInput;

		public long TicksWithInput;

		public long InputMessagesAtTake;

		public uint AckSeq;

		public uint AckHostMs;

		public uint AckSentSeq;

		public long AckSentMs;

		public long InputGapLogMs = -4611686018427387904L;

		public long LastStatusMs;

		public long StateBytesAtLastStatus;

		public long SentAtLastStatus;

		public string LastPhase = "";
	}

	public sealed class PlayerRecord
	{
		public string Key;

		public string Name;

		public ushort ClientId;

		public long IdentityId;

		public long PlayerId;

		public PeerId? Peer;

		public uint CharacterNetId;

		public bool CharacterRequested;

		public int Joins;
	}

	private const int MaxInputEntries = 256;

	private const int MaxInputBytes = 1024;

	private readonly ITransport _t;

	private readonly IMpLog _log;

	private readonly IHostWorld _world;

	private readonly HostSyncOptions _o;

	private readonly Func<long> _clock;

	private readonly object _sync = new();

	private readonly Dictionary<PeerId, RemoteClient> _clients = new();

	private readonly ConcurrentQueue<StateEntry> _stateIn = new();

	private readonly ConcurrentQueue<uint> _despawnedIn = new();

	private readonly Dictionary<string, PlayerRecord> _players = new();

	private ushort _nextClientId = 2;

	private readonly List<byte[]> _out = new List<byte[]>();

	private readonly Dictionary<ushort, int> _intervalByKey = new();

	private volatile int _syncedCount;

	private long _lastWaitCheckMs;

	private long _lastEmitMs = -4611686018427387904L;

	public long StateIn;

	public long StateDroppedNoClient;

	public long PlayerTablesSent;

	private readonly List<string> _hostStrings = new();

	public TypeKeyTable Keys { get; }

	public bool AnySynced => _syncedCount > 0;

	public SignalKeyTable SignalKeys { get; } = new();

	public int HostStringCount
	{
		get
		{
			lock (_sync)
			{
				return _hostStrings.Count;
			}
		}
	}

	public HostSession(ITransport transport, IMpLog log, IHostWorld world, TypeKeyTable keys, HostSyncOptions options, Func<long>? clock = null)
	{
		_t = transport;
		_log = log;
		_world = world;
		Keys = keys;
		_o = options;
		_clock = clock ?? new Func<long>(MpClock.NowMs);
		_t.PeerConnected += OnConnected;
		_t.PeerDisconnected += OnDisconnected;
		_t.MessageReceived += OnMessage;
		_t.BulkGate = IsAdmitted;
		_log.Info($"sync(host): ready; state budget {_o.StateBudgetBytesPerSec / 1024} KiB/s per client, rate caps {_o.Caps.Describe()}");
	}

	private int IntervalFor(ushort key)
	{
		lock (_intervalByKey)
		{
			if (_intervalByKey.TryGetValue(key, out var interval))
			{
				return interval;
			}
			string shortName = $"key#{key}";
			foreach (TypeKeyDef def in Keys.All())
			{
				if (def.Key == key)
				{
					shortName = def.ShortName;
					break;
				}
			}
			interval = _o.Caps.IntervalMsFor(shortName);
			_intervalByKey[key] = interval;
			return interval;
		}
	}

	private bool IsAdmitted(PeerId peer)
	{
		lock (_sync)
		{
			return _clients.TryGetValue(peer, out RemoteClient client) && client.Player != null;
		}
	}

	private void OnConnected(PeerId peer)
	{
		lock (_sync)
		{
			_clients[peer] = new RemoteClient
			{
				Peer = peer,
				Status = ClientStatus.Connected,
				State = new StateCoalescer(IntervalFor, _o.StateBudgetBytesPerSec),
				JoinedMs = _clock()
			};
		}
	}

	private void OnDisconnected(PeerId peer, DisconnectInfo info)
	{
		RemoteClient client;
		lock (_sync)
		{
			if (_clients.Remove(peer, out client) && client.Status == ClientStatus.Synced)
			{
				_syncedCount--;
			}
		}
		if (client == null)
		{
			return;
		}
		if (client.Status == ClientStatus.Synced && !client.Removed)
		{
			Announce(info.ByPeer ? $"{client.Name} left the server." : $"{client.Name} lost connection.");
		}
		if (client.Status == ClientStatus.CaptureRequested)
		{
			_world.CancelCapture(peer);
		}
		PlayerRecord player = client.Player;
		_world.ForgetStories(peer);
		if (player != null)
		{
			lock (_sync)
			{
				player.Peer = null;
			}
			if (player.IdentityId != 0L)
			{
				PushPlayerTable();
			}
			if (player.CharacterNetId != 0 || player.CharacterRequested)
			{
				_log.Info($"sync(host): ClientId {player.ClientId} '{player.Name}' left; releasing its character NetworkId {player.CharacterNetId}");
				player.CharacterNetId = 0;
				player.CharacterRequested = false;
				_world.ReleaseCharacter(peer, player.ClientId);
			}
		}
		_log.Info($"sync(host): client {peer} '{client.Name}' left ({info}); was {client.Status}, {client.LifecycleMessages} lifecycle msgs / {MpLogExtensions.Bytes(client.LifecycleBytes)}, state {MpLogExtensions.Bytes(client.StateBytes)}, {client.BlobsSent} blobs / {MpLogExtensions.Bytes(client.BlobBytes)}");
	}

	private void OnMessage(PeerId peer, Channel ch, byte[] data)
	{
		if (!SyncProtocol.IsSyncMessage(data))
		{
			return;
		}
		try
		{
			switch ((SyncMsg)data[0])
			{
			case SyncMsg.JoinRequest:
				HandleJoin(peer, JoinRequest.Decode(data));
				break;
			case SyncMsg.TypeKeys:
			case SyncMsg.CharacterRequest:
			case SyncMsg.SignalKeys:
			case SyncMsg.StoryMove:
			case SyncMsg.ClientCommands:
			case SyncMsg.ObservableEdits:
			case SyncMsg.ClientInput:
			case SyncMsg.ClientLook:
			case SyncMsg.SocialRequest:
			case SyncMsg.ChatSend:
			case SyncMsg.AdminRequest:
			case SyncMsg.GpsShare:
			case SyncMsg.Punch:
			case SyncMsg.ServerInfo:
			case SyncMsg.VoiceUp:
			case SyncMsg.TradeRequest:
			case SyncMsg.TradeAction:
				HandleUpstream(peer, ch, data);
				break;
			case SyncMsg.ClientStatus:
			{
				var (phase, text) = TextMessage.Decode(data);
				string name;
				ushort clientId = 0;
				lock (_sync)
				{
					name = _clients.TryGetValue(peer, out RemoteClient client) ? client.Name : "?";
					if (client != null)
					{
						client.LastPhase = ((ClientPhase)phase).ToString();
						clientId = client.Player?.ClientId ?? 0;
					}
				}
				if (phase == (byte)ClientPhase.NotSeated && clientId != 0)
				{
					_world.OnNotSeated(clientId);
				}
				string message = $"client {peer} '{name}': {(ClientPhase)phase}: {text}";
				if (phase == (byte)ClientPhase.Error)
				{
					_log.Error(message);
				}
				else if (phase >= (byte)ClientPhase.Warning)
				{
					_log.Warn(message);
				}
				else
				{
					_log.Info(message);
				}
				break;
			}
			default:
				_log.WarnOnce($"host-unexpected-{data[0]}", $"sync(host): unexpected {(SyncMsg)data[0]} from {peer}");
				break;
			}
		}
		catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException)
		{
			_log.Warn($"sync(host): malformed {(SyncMsg)data[0]} from {peer} ({data.Length} B); disconnecting");
			_t.Disconnect(peer, DisconnectCodes.ProtocolError, $"malformed {(SyncMsg)data[0]}");
		}
	}

	private void HandleJoin(PeerId peer, JoinRequest req)
	{
		RemoteClient client;
		lock (_sync)
		{
			_clients.TryGetValue(peer, out client);
		}
		if (client == null)
		{
			return;
		}
		client.Name = req.PlayerName;
		_log.Info($"sync(host): join request from {peer} '{req.PlayerName}' (plugin {req.PluginVersion}, game {req.GameVersion}, sync v{req.SyncVersion})");
		if (req.SyncVersion != SyncProtocol.Version)
		{
			Reject(peer, VersionMismatch.Refusal(_o.PluginVersion, req.PluginVersion), JoinRejectCode.Version);
			return;
		}
		if (_o.PluginVersion.Length > 0 && req.PluginVersion != _o.PluginVersion)
		{
			Reject(peer, VersionMismatch.Refusal(_o.PluginVersion, req.PluginVersion), JoinRejectCode.Version);
			return;
		}
		if (req.GameVersion != _o.GameVersion)
		{
			_log.Warn($"sync(host): {peer} runs game {req.GameVersion}, host {_o.GameVersion}; continuing, but entity data may not match");
		}
		if (client.Status == ClientStatus.Connected && client.Player == null && !CheckAccess(peer, req))
		{
			return;
		}
		if (client.Status != ClientStatus.Connected)
		{
			_log.Warn($"sync(host): duplicate join request from {peer} ignored (status {client.Status})");
			return;
		}
		lock (_sync)
		{
			if (client.Player == null && _clients.Values.Count(c => c.Player != null && c.Peer != peer) >= _o.MaxPlayers - (_o.HostCountsAsPlayer ? 1 : 0))
			{
				Reject(peer, "The session is full.");
				return;
			}
		}
		if (req.SyncVersion == SyncProtocol.Version && client.Player == null)
		{
			client.Player = Register(peer, req.PlayerName);
			_log.Info($"sync(host): {peer} '{req.PlayerName}' is ClientId {client.Player.ClientId}" + (client.Player.Joins > 1 ? $" again (join #{client.Player.Joins})" : " (new player)") + ", identity key " + client.Player.Key);
		}
		ContinueJoin(client);
	}

	private void ContinueJoin(RemoteClient c)
	{
		PeerId peer = c.Peer;
		if (!_world.WorldReady)
		{
			c.Status = ClientStatus.WaitingForWorld;
			_t.Send(peer, Channel.Control, TextMessage.Encode(SyncMsg.JoinWait, 0, "the host has no world loaded yet; it will be sent as soon as the host loads one"));
			_log.Info($"sync(host): {peer} waits until a world is loaded on this PC");
		}
		else
		{
			RequestCapture(c);
		}
	}

	private PlayerRecord Register(PeerId peer, string name)
	{
		lock (_sync)
		{
			string baseKey = PlayerIdentities.KeyFor(peer, string.IsNullOrWhiteSpace(name) ? "?" : name.Trim());
			int n = 1;
			string key;
			PlayerRecord record;
			while (true)
			{
				key = n == 1 ? baseKey : $"{baseKey}#{n}";
				if (!_players.TryGetValue(key, out record))
				{
					break;
				}
				// Reuse the record when it is offline or this is the same PC rejoining; otherwise try the next suffix.
				if (!record.Peer.HasValue || !(record.Peer != peer))
				{
					record.Name = name;
					record.Peer = peer;
					record.Joins++;
					record.CharacterNetId = 0;
					record.CharacterRequested = false;
					return record;
				}
				n++;
			}
			if (_nextClientId == ushort.MaxValue)
			{
				throw new InvalidOperationException("ClientId space exhausted");
			}
			record = new PlayerRecord
			{
				Key = key,
				Name = name,
				ClientId = _nextClientId++,
				Peer = peer,
				Joins = 1
			};
			_players[key] = record;
			return record;
		}
	}

	public IReadOnlyList<PlayerRecord> Players()
	{
		lock (_sync)
		{
			return new List<PlayerRecord>(_players.Values);
		}
	}

	public void PlayerIdentified(ushort clientId, long identity, long player)
	{
		lock (_sync)
		{
			PlayerRecord record = null;
			foreach (PlayerRecord p in _players.Values)
			{
				if (p.ClientId == clientId)
				{
					record = p;
				}
			}
			if (record == null)
			{
				return;
			}
			record.IdentityId = identity;
			record.PlayerId = player;
		}
		if (identity != 0L)
		{
			PushPlayerTable();
		}
	}

	public List<PlayerInfo> PlayerTable()
	{
		var table = new List<PlayerInfo>();
		if (_o.HostCountsAsPlayer) table.Add(new PlayerInfo(1, 1559595546L, -1559595548L, _o.HostName, Connected: true));
		lock (_sync)
		{
			foreach (PlayerRecord p in _players.Values)
			{
				if (p.IdentityId != 0L)
				{
					table.Add(new PlayerInfo(p.ClientId, p.PlayerId, p.IdentityId, p.Name, p.Peer.HasValue));
				}
			}
		}
		table.Sort((a, b) => a.ClientId.CompareTo(b.ClientId));
		return table;
	}

	public void SendSocial(PeerId peer, SocialSnapshot snapshot)
	{
		lock (_sync)
			if (!_clients.TryGetValue(peer, out var client) || client.Status != ClientStatus.Synced) return;
		byte[] data = SocialMessage.Encode(SyncMsg.SocialSnapshot, snapshot);
		_t.Send(peer, data.Length > 8192 ? Channel.Bulk : Channel.Control, data);
	}

	private void PushPlayerTable()
	{
		byte[] msg = PlayerTableMessage.Encode(PlayerTable());
		lock (_sync)
		{
			foreach (RemoteClient client in _clients.Values)
			{
				if (client.Status == ClientStatus.Synced)
				{
					_t.Send(client.Peer, Channel.Control, msg);
					PlayerTablesSent++;
				}
			}
		}
	}

	public bool PublishPlayerStatus(ushort clientId, int pcu, int maxPcu)
	{
		lock (_sync)
		{
			foreach (RemoteClient client in _clients.Values)
			{
				if (client.Status != ClientStatus.Synced || client.Player?.ClientId != clientId)
				{
					continue;
				}
				if (client.LastStatus == (pcu, maxPcu))
				{
					return false;
				}
				client.LastStatus = (pcu, maxPcu);
				_t.Send(client.Peer, Channel.Control, PlayerStatusMessage.Encode(clientId, pcu, maxPcu));
				return true;
			}
		}
		return false;
	}

	private void HandleUpstream(PeerId peer, Channel ch, byte[] data)
	{
		try
		{
			HandleUpstreamCore(peer, data);
		}
		catch (Exception ex) when (ex is not ArgumentOutOfRangeException and not IndexOutOfRangeException)
		{
			_log.WarnOnce($"host-upstream-{data[0]}", $"sync(host): handling {(SyncMsg)data[0]} from {peer} failed: {ex.GetType().Name}: {ex.Message}");
		}
	}

	private void HandleUpstreamCore(PeerId peer, byte[] data)
	{
		RemoteClient client;
		lock (_sync)
		{
			_clients.TryGetValue(peer, out client);
		}
		if (client == null || client.Player == null)
		{
			return;
		}
		switch ((SyncMsg)data[0])
		{
		case SyncMsg.ChatSend:
			HandleChat(client, data);
			break;
		case SyncMsg.AdminRequest:
			HandleAdmin(client, data);
			break;
		case SyncMsg.GpsShare:
			HandleGpsShare(client, data);
			break;
		case SyncMsg.Punch:
			if (client.Status == ClientStatus.Synced && data.Length <= 64) _world.OnPunch(client.Player.ClientId, SocialMessage.Decode<PunchRequest>(data).Target);
			break;
		case SyncMsg.VoiceUp:
			HandleVoice(client, data);
			break;
		case SyncMsg.TradeRequest:
		case SyncMsg.TradeAction:
			HandleTrade(client, data);
			break;
		case SyncMsg.ServerInfo:
			HandleServerInfo(client);
			break;
		case SyncMsg.SocialRequest:
			if (client.Status == ClientStatus.Synced && client.Player.IdentityId != 0)
				_world.OnSocialRequest(peer, client.Player.ClientId, SocialMessage.Decode<SocialRequest>(data));
			break;
		case SyncMsg.TypeKeys:
		{
			List<TypeKeyDef> defs = TypeKeysMessage.Decode(data);
			if (_o.ResolveType == null)
			{
				break;
			}
			Func<string, bool> isInput = _o.IsClientInput ?? (_ => false);
			List<TypeKeyDef> refused = defs.Where(d => !isInput(d.Name)).ToList();
			List<string> unusable;
			lock (client.Input)
			{
				unusable = client.InputTypes.Add(defs.Where(d => isInput(d.Name)), _o.ResolveType);
				// Registered as unusable so a key re-announced under a refused name stops resolving to its old type.
				client.InputTypes.Add(refused, _ => null);
			}
			foreach (TypeKeyDef def in refused)
			{
				if (client.InputRefusedLogged++ < 16)
				{
					_log.Warn($"input: refused data {def.Name} from ClientId {client.Player.ClientId}: not a client input type, its updates are ignored");
				}
			}
			_log.Info($"sync(host): ClientId {client.Player.ClientId}: {defs.Count} input type key(s) ({string.Join(", ", defs.Select(d => d.ShortName))}), {unusable.Count} unusable");
			foreach (string problem in unusable)
			{
				_log.Warn($"sync(host): ClientId {client.Player.ClientId}: {problem}");
			}
			break;
		}
		case SyncMsg.SignalKeys:
		{
			List<SignalKeyDef> defs = SignalKeysMessage.Decode(data);
			int unusable = 0;
			lock (client.Input)
			{
				foreach (SignalKeyDef def in defs)
				{
					string problem = _o.CheckSignal == null ? "client signals disabled" : _o.CheckSignal(def);
					client.Signals[def.Key] = (def, problem);
					if (problem != null)
					{
						unusable++;
						_log.Warn($"sync(host): ClientId {client.Player.ClientId}: signal {def.Name}: {problem}; ignored");
					}
				}
			}
			_log.Info($"sync(host): ClientId {client.Player.ClientId}: {defs.Count} signal key(s), {unusable} unusable");
			break;
		}
		case SyncMsg.ClientInput:
			var (inputSeq, entries) = ClientInputMessage.Decode(data);
			lock (client.Input)
			{
				long now = _clock();
				if (client.InputLastArrivalMs >= 0)
				{
					long gap = now - client.InputLastArrivalMs;
					if (gap > 50)
					{
						client.InputGaps++;
						if (gap > client.InputGapMaxMs)
						{
							client.InputGapMaxMs = gap;
						}
						if (gap > 150 && now - client.InputGapLogMs > 5000)
						{
							client.InputGapLogMs = now;
							_log.Info($"sync(host): ClientId {client.Player?.ClientId}: no input from this joiner for {gap} ms (diagnostic; {client.InputGaps} gaps > 50 ms so far)");
						}
					}
				}
				client.InputLastArrivalMs = now;
				client.InputMessages++;
				foreach (InputEntry entry in entries)
				{
					client.InputEntries++;
					(uint, ushort) key = (entry.NetId, entry.Key);
					bool known = client.InputData.TryGetValue(key, out var prev);
					// Honest input is a few small structs on the character, seat and piloted grid; anything past this is hostile.
					if (entry.Bytes.Length > MaxInputBytes || (!known && client.InputData.Count >= MaxInputEntries))
					{
						if (client.InputOverflow++ == 0) _log.Warn($"sync(host): ClientId {client.Player?.ClientId} sent more input data than any honest client ({client.InputData.Count} entries, {entry.Bytes.Length} B); extra entries dropped");
						continue;
					}
					if (!known || Seq.Newer(inputSeq, prev.Seq))
					{
						// Stays dirty until TakeInputs applies it, even if a newer copy brings the same bytes.
						bool changed = prev.Bytes == null || !prev.Bytes.AsSpan().SequenceEqual(entry.Bytes);
						client.InputData[key] = (inputSeq, entry.Bytes, changed || prev.Dirty);
					}
				}
				break;
			}
		case SyncMsg.ClientLook:
		{
			var (seq, netId, epoch, x, y, z) = ClientLookMessage.Decode(data);
			if (!_o.LookTotals)
			{
				if (client.LookIgnored++ == 0L)
				{
					_log.Info($"sync(host): ClientId {client.Player.ClientId}: ignoring the joiner's look totals (-y51online:predict:off on the host): " + "its turning is applied from the newest ControlData per tick as in 0.7.0, its prediction will correct more often");
				}
				break;
			}
			bool newEpoch;
			lock (client.Input)
			{
				client.Look ??= new LookAccumulator();
				newEpoch = client.Look.Offer(seq, netId, epoch, x, y, z);
			}
			if (newEpoch)
			{
				_log.Info($"sync(host): ClientId {client.Player.ClientId}: look totals for NetworkId {netId} (epoch {epoch}): the joiner's turning is applied " + "exactly as it predicted it (total minus applied, once per tick)" + (netId != client.Player.CharacterNetId ? $"; its character here is NetworkId {client.Player.CharacterNetId}, waiting" : ""));
			}
			break;
		}
		case SyncMsg.ClientCommands:
			var (lastCommandSeq, commands) = ClientSignalsMessage.Decode(data);
			lock (client.Input)
			{
				client.LastCommandSeq = lastCommandSeq;
				foreach (WireCommand command in commands)
				{
					client.CommandsIn++;
					if (client.Signals.TryGetValue(command.Key, out var signal) && signal.Problem == null && (signal.Def.PayloadBytes == ushort.MaxValue || command.Payload.Length == signal.Def.PayloadBytes))
					{
						if (client.InputCommands.Count < 4096)
						{
							client.InputCommands.Add((signal.Def.Name, command));
						}
					}
					else
					{
						client.CommandsRejected++;
					}
				}
				break;
			}
		case SyncMsg.StoryMove:
		{
			StoryMove move = StoryMove.Decode(data);
			client.P2.StoriesIn++;
			if (client.Status != ClientStatus.Synced)
			{
				SendStoryFinish(peer, new StoryFinish
				{
					StoryId = move.StoryId,
					Status = 2,
					Text = "not in the world yet"
				});
			}
			else
			{
				_world.OnClientStory(peer, client.Player.ClientId, move);
			}
			break;
		}
		case SyncMsg.ObservableEdits:
		{
			List<MemberEdit> edits = ObservableEditsMessage.Decode(data).Edits;
			client.P2.ObservableEditsIn += edits.Count;
			if (client.Status == ClientStatus.Synced && edits.Count != 0)
			{
				_world.OnObservableEdits(peer, client.Player.ClientId, edits);
			}
			break;
		}
		case SyncMsg.CharacterRequest:
		{
			var (reason, choice) = CharacterRequestMessage.Decode(data);
			PlayerRecord player = client.Player;
			if (client.Status != ClientStatus.Synced)
			{
				_log.Warn($"sync(host): ClientId {player.ClientId} asked for a character before its world was captured; ignored");
				break;
			}
			if (player.CharacterNetId != 0)
			{
				_t.Send(peer, Channel.Control, PlayerAssignMessage.Encode(player.ClientId, 0, player.CharacterNetId, "already assigned"));
				break;
			}
			player.CharacterRequested = true;
			_log.Info($"sync(host): ClientId {player.ClientId} '{player.Name}' requests a character ({(reason == CharacterRequestMessage.Respawn ? "respawn" : "first spawn")})");
			_world.RequestCharacter(peer, player.ClientId, player.Name, choice, reason == CharacterRequestMessage.Respawn);
			break;
		}
		}
	}

	public void CharacterAssigned(PeerId peer, uint netId, string detail)
	{
		RemoteClient client;
		lock (_sync)
		{
			_clients.TryGetValue(peer, out client);
		}
		if (client?.Player != null)
		{
			client.Player.CharacterNetId = netId;
			client.Player.CharacterRequested = false;
			lock (client.Input)
			{
				client.InputData.Clear();
				client.Look = null;
			}
			_t.Send(peer, Channel.Control, PlayerAssignMessage.Encode(client.Player.ClientId, 0, netId, detail));
			_log.Info($"sync(host): CHARACTER ASSIGNED: ClientId {client.Player.ClientId} '{client.Player.Name}' controls NetworkId {netId} ({detail})");
		}
	}

	public void SendSpawnMenu(PeerId peer, SpawnMenu menu) => _t.Send(peer, Channel.Control, SocialMessage.Encode(SyncMsg.SpawnMenu, menu));

	public void CharacterFailed(PeerId peer, string why)
	{
		RemoteClient client;
		lock (_sync)
		{
			_clients.TryGetValue(peer, out client);
		}
		if (client?.Player != null)
		{
			client.Player.CharacterRequested = false;
			_t.Send(peer, Channel.Control, PlayerAssignMessage.Encode(client.Player.ClientId, 1, 0u, why));
			_log.Error($"sync(host): no character for ClientId {client.Player.ClientId} '{client.Player.Name}': {why}");
		}
	}

	public void CharacterLost(PeerId peer, string why)
	{
		RemoteClient client;
		lock (_sync)
		{
			_clients.TryGetValue(peer, out client);
		}
		if (client?.Player != null && client.Player.CharacterNetId != 0)
		{
			uint characterNetId = client.Player.CharacterNetId;
			client.Player.CharacterNetId = 0;
			lock (client.Input)
			{
				client.InputData.Clear();
				client.Look = null;
			}
			_t.Send(peer, Channel.Control, PlayerAssignMessage.Encode(client.Player.ClientId, 2, characterNetId, why));
			_log.Info($"sync(host): ClientId {client.Player.ClientId} '{client.Player.Name}' lost its character NetworkId {characterNetId}: {why}");
		}
	}

	private static bool IsControlKey(RemoteClient c, LookAccumulator look, ushort key)
	{
		ushort? known = look.ControlKey;
		if (known.HasValue)
		{
			ushort controlKey = known.GetValueOrDefault();
			return controlKey == key;
		}
		if (c.InputTypes.Name(key) != "ControlData" || !c.InputTypes.TryResolve(key, out var _, out var size) || size != 24)
		{
			return false;
		}
		look.ControlKey = key;
		return true;
	}

	public List<ClientInputBatch> TakeInputs()
	{
		var batches = new List<ClientInputBatch>();
		List<RemoteClient> clients;
		lock (_sync)
		{
			clients = new List<RemoteClient>(_clients.Values);
		}
		foreach (RemoteClient client in clients)
		{
			PlayerRecord player = client.Player;
			if (player == null || client.Status != ClientStatus.Synced || player.CharacterNetId == 0)
			{
				continue;
			}
			ClientInputBatch batch = null;
			lock (client.Input)
			{
				if (client.InputMessages == client.InputMessagesAtTake)
				{
					client.TicksWithoutInput++;
				}
				else
				{
					client.TicksWithInput++;
				}
				client.InputMessagesAtTake = client.InputMessages;
				if (client.InputCommands.Count == 0 && client.InputData.Count == 0)
				{
					continue;
				}
				// Look totals only count while they are for the character this client controls now.
				LookAccumulator look = _o.LookTotals && client.Look is { } current && current.NetId == player.CharacterNetId ? current : null;
				bool controlChanged = false;
				foreach (var key in new List<(uint NetId, ushort Key)>(client.InputData.Keys))
				{
					var input = client.InputData[key];
					if (look != null && key.NetId == look.NetId && IsControlKey(client, look, key.Key))
					{
						if (input.Dirty || look.Control == null)
						{
							client.InputData[key] = (input.Seq, input.Bytes, false);
							look.Control = input.Bytes;
							controlChanged |= input.Dirty;
							NoteApplied(client, input.Seq);
						}
					}
					else
					{
						if (!input.Dirty)
						{
							continue;
						}
						client.InputData[key] = (input.Seq, input.Bytes, false);
						NoteApplied(client, input.Seq);
						if (!client.InputTypes.TryResolve(key.Key, out var localTypeId, out var size) || (input.Bytes.Length != size && input.Bytes.Length != 0))
						{
							client.InputRejected++;
							continue;
						}
						batch ??= new ClientInputBatch
						{
							Peer = client.Peer,
							ClientId = player.ClientId,
							CharacterNetId = player.CharacterNetId
						};
						if (input.Bytes.Length == 0)
						{
							batch.Removes.Add((key.NetId, localTypeId));
							client.InputRemoved++;
						}
						else
						{
							batch.Data.Add((key.NetId, localTypeId, input.Bytes));
						}
					}
				}
				if (look != null)
				{
					ushort? known = look.ControlKey;
					if (known.HasValue)
					{
						ushort controlKey = known.GetValueOrDefault();
						byte[] control = look.Control;
						// Non-short-circuit: Take must run every tick, it consumes the turn and yields x, y, z.
						if (control != null && control.Length == 24 && client.InputTypes.TryResolve(controlKey, out var controlTypeId, out var controlSize) && controlSize == 24 && (look.Take(out var x, out var y, out var z) | controlChanged))
						{
							batch ??= new ClientInputBatch
							{
								Peer = client.Peer,
								ClientId = player.ClientId,
								CharacterNetId = player.CharacterNetId
							};
							batch.Data.Add((look.NetId, controlTypeId, LookAccumulator.WithRotation(control, x, y, z)));
							client.LookControlWrites++;
						}
					}
				}
				if (client.InputCommands.Count > 0)
				{
					batch ??= new ClientInputBatch
					{
						Peer = client.Peer,
						ClientId = player.ClientId,
						CharacterNetId = player.CharacterNetId
					};
					Core.LatencyTrace.Mark(Core.LatencyTrace.HostApplied, (byte)SyncMsg.ClientCommands, client.LastCommandSeq, client.InputCommands.Count);
					batch.Commands.AddRange(client.InputCommands);
					client.InputCommands.Clear();
				}
			}
			if (batch != null)
			{
				batches.Add(batch);
			}
		}
		SendInputAcks(clients);
		return batches;
	}

	private void NoteApplied(RemoteClient c, uint seq)
	{
		if (c.AckSeq == 0 || Seq.Newer(seq, c.AckSeq))
		{
			c.AckSeq = seq;
			c.AckHostMs = (uint)_clock();
		}
	}

	// The joiner's prediction starts where this input left off (see MovePredictor.SetAck). Sent before this tick's
	// state, on the state channel, and repeated so a lost ack heals.
	private void SendInputAcks(List<RemoteClient> clients)
	{
		long now = _clock();
		foreach (RemoteClient c in clients)
		{
			if (c.AckSeq == 0 || (c.AckSentSeq == c.AckSeq && now - c.AckSentMs < 250)) continue;
			c.AckSentSeq = c.AckSeq;
			c.AckSentMs = now;
			_t.Send(c.Peer, Channel.State, InputAckMessage.Encode(c.AckSeq, c.AckHostMs));
		}
	}

	public void AddHostStrings(IReadOnlyCollection<string> names)
	{
		if (names.Count == 0)
		{
			return;
		}
		lock (_sync)
		{
			_hostStrings.AddRange(names);
		}
	}

	public void PublishSignals(uint hostTick, IReadOnlyList<WireCommand> commands) => PublishSignals(hostTick, commands, null);

	/// <summary><paramref name="roots"/>, when given, holds per command the root NetIds it refers to; a joiner that has streamed one of them out skips the command.</summary>
	public void PublishSignals(uint hostTick, IReadOnlyList<WireCommand> commands, IReadOnlyList<uint[]>? roots)
	{
		if (commands.Count == 0 || _syncedCount == 0)
		{
			return;
		}
		AnnounceSignalKeys();
		byte[] batch = SignalBatchMessage.Encode(0u, hostTick, commands);
		lock (_sync)
		{
			foreach (RemoteClient client in _clients.Values)
			{
				// Signals can carry StringHash values; the joiner needs their names before the batch that uses them.
			if (client.Status == ClientStatus.Synced && client.P2.StringsSent < _hostStrings.Count)
				{
					int count = _hostStrings.Count - client.P2.StringsSent;
					byte[] names = StringTableMessage.Encode(client.NextLifeSeq++, _hostStrings, client.P2.StringsSent, count);
					_t.Send(client.Peer, names.Length > 8192 ? Channel.Bulk : Channel.Control, names);
					if (client.P2.StringsSent == 0)
					{
						_log.Info($"sync(host): {client.Peer}: sent {count} StringHash name(s) ({MpLogExtensions.Bytes(names.Length)}) before its first signals");
					}
					client.P2.StringsSent += count;
					client.P2.StringsOut += count;
					client.LifecycleMessages++;
					client.LifecycleBytes += names.Length;
				}
			}
		}
		lock (_sync)
		{
			foreach (RemoteClient client in _clients.Values)
			{
				if (client.Status != ClientStatus.Synced)
				{
					continue;
				}
				byte[] mine = batch;
				int count = commands.Count;
				var streamedOut = client.StreamedOut;
				if (roots != null && streamedOut.Count > 0)
				{
					var kept = new List<WireCommand>(commands.Count);
					for (int i = 0; i < commands.Count; i++)
					{
						if (!Array.Exists(roots[i], streamedOut.Contains)) kept.Add(commands[i]);
					}
					if (kept.Count == 0)
					{
						continue;
					}
					if (kept.Count != commands.Count)
					{
						mine = SignalBatchMessage.Encode(0u, hostTick, kept);
						count = kept.Count;
					}
				}
				byte[] copy = mine == batch ? (byte[])batch.Clone() : mine;
				EntityBundleMessage.SetLifeSeq(copy, client.NextLifeSeq++);
				_t.Send(client.Peer, copy.Length > 8192 ? Channel.Bulk : Channel.Control, copy);
				client.LifecycleMessages++;
				client.LifecycleBytes += copy.Length;
				client.P2.SignalBatches++;
				client.P2.SignalsOut += count;
				client.P2.SignalBytesOut += copy.Length;
			}
		}
	}

	private void AnnounceSignalKeys()
	{
		List<SignalKeyDef> defs = SignalKeys.TakeUnannounced();
		if (defs.Count == 0)
		{
			return;
		}
		byte[] msg = SignalKeysMessage.Encode(defs);
		lock (_sync)
		{
			foreach (RemoteClient client in _clients.Values)
			{
				if (client.Status == ClientStatus.Synced)
				{
					_t.Send(client.Peer, Channel.Control, msg);
				}
			}
		}
	}

	public void SendStoryMove(PeerId peer, StoryMove move)
	{
		byte[] msg = move.Encode();
		_t.Send(peer, msg.Length > 8192 ? Channel.Bulk : Channel.Control, msg);
		lock (_sync)
		{
			if (_clients.TryGetValue(peer, out RemoteClient client))
			{
				client.P2.StoryMovesOut++;
			}
		}
	}

	public void SendStoryFinish(PeerId peer, StoryFinish finish)
	{
		byte[] msg = finish.Encode();
		_t.Send(peer, msg.Length > 8192 ? Channel.Bulk : Channel.Control, msg);
		lock (_sync)
		{
			if (_clients.TryGetValue(peer, out RemoteClient client))
			{
				client.P2.StoryFinishesOut++;
			}
		}
	}

	public void InputApplied(PeerId peer, int data, int commands, int rejected)
	{
		RemoteClient client;
		lock (_sync)
		{
			_clients.TryGetValue(peer, out client);
		}
		if (client == null)
		{
			return;
		}
		lock (client.Input)
		{
			client.InputApplied += data;
			client.CommandsApplied += commands;
			client.InputRejected += rejected;
		}
	}

	private void RequestCapture(RemoteClient c)
	{
		c.Status = ClientStatus.CaptureRequested;
		PlayerRecord player = c.Player;
		if (player != null && _o.Settings.Identities)
		{
			player.IdentityId = 0L;
			player.PlayerId = 0L;
			_log.Info($"sync(host): registering ClientId {player.ClientId} '{player.Name}' (key {player.Key}) as its own player, then capturing world '{_world.WorldName}' for {c.Peer}");
			_world.RegisterPlayer(c.Peer, player.ClientId, player.Key, player.Name);
		}
		else
		{
			_log.Info($"sync(host): capturing world '{_world.WorldName}' for {c.Peer} at the next server tick");
		}
		_world.RequestCapture(c.Peer);
	}

	private void Reject(PeerId peer, string why, byte code = JoinRejectCode.Generic)
	{
		_log.Error($"sync(host): rejecting {peer}: {why}");
		_t.Send(peer, Channel.Control, TextMessage.Encode(SyncMsg.JoinReject, code, why));
	}

	public bool BeginCapture(PeerId peer, out uint worldLifeSeq) => BeginCapture(peer, 1, out worldLifeSeq);

	public bool BeginCapture(PeerId peer, int parts, out uint firstLifeSeq)
	{
		firstLifeSeq = 0;
		string name;
		lock (_sync)
		{
			if (!_clients.TryGetValue(peer, out RemoteClient client) || client.Status != ClientStatus.CaptureRequested)
			{
				return false;
			}
			client.Status = ClientStatus.Synced;
			client.SyncedMs = _clock();
			firstLifeSeq = client.NextLifeSeq;
			client.NextLifeSeq += (uint)Math.Max(1, parts);
			_syncedCount++;
			name = client.Name;
		}
		Announce($"{name} joined the server.");
		SendMotd(peer, name);
		return true;
	}

	public bool WantsCapture(PeerId peer)
	{
		lock (_sync)
		{
			return _clients.TryGetValue(peer, out RemoteClient client) && client.Status == ClientStatus.CaptureRequested;
		}
	}

	public void CompleteCapture(PeerId peer, byte[] snapshot, EntityBundleMessage world, string summary, bool hostMissions = false, CaptureBlobs? blobs = null)
		=> CompleteCapture(peer, snapshot, new List<EntityBundleMessage> { world }, summary, hostMissions, blobs);

	public void CompleteCapture(PeerId peer, byte[] snapshot, IReadOnlyList<EntityBundleMessage> parts, string summary, bool hostMissions = false, CaptureBlobs? blobs = null)
	{
		RemoteClient client;
		lock (_sync)
		{
			_clients.TryGetValue(peer, out client);
		}
		if (client == null || client.Status != ClientStatus.Synced)
		{
			_log.Warn($"sync(host): capture for {peer} finished but the client is gone");
			return;
		}
		var encoded = new List<byte[]>(parts.Count);
		int entities = 0;
		int withData = 0;
		long worldBytes = 0L;
		foreach (EntityBundleMessage part in parts)
		{
			byte[] msg = part.Encode();
			encoded.Add(msg);
			worldBytes += msg.Length;
			entities += part.Entities.Count;
			withData += part.Data.Count;
		}
		var accept = new JoinAccept
		{
			WorldName = _world.WorldName,
			HostTick = parts.Count > 0 ? parts[0].HostTick : 0u,
			SnapshotBytes = snapshot.Length,
			WorldBytes = (int)Math.Min(2147483647L, worldBytes),
			WorldEntities = entities,
			WorldParts = parts.Count,
			HostMissions = hostMissions,
			WorldBlobs = blobs?.Data.Count ?? 0,
			WorldBlobBytes = blobs?.Bytes ?? 0,
			ClientId = client.Player?.ClientId ?? 0,
			Settings = _o.Settings,
			IdentityId = client.Player?.IdentityId ?? 0,
			PlayerId = client.Player?.PlayerId ?? 0,
			Players = PlayerTable(),
			Mods = _world.Mods.ToList()
		};
		_t.Send(peer, Channel.Control, accept.Encode());
		List<TypeKeyDef> typeKeys = Keys.All();
		if (typeKeys.Count > 0)
		{
			_t.Send(peer, Channel.Control, TypeKeysMessage.Encode(typeKeys));
		}
		List<SignalKeyDef> signalKeys = SignalKeys.All();
		if (signalKeys.Count > 0)
		{
			_t.Send(peer, Channel.Control, SignalKeysMessage.Encode(signalKeys));
		}
		int blobCount;
		long blobBytes;
		if (blobs != null)
		{
			(blobCount, blobBytes) = SendBlobs(client, blobs.Snapshot, blobs.Data);
		}
		else
		{
			blobCount = 0;
			blobBytes = 0L;
		}
		_t.Send(peer, Channel.Bulk, SnapshotMessage.Encode(snapshot, blobs?.Snapshot));
		for (int i = 0; i < encoded.Count; i++)
		{
			if (blobs != null)
			{
				var (count, bytes) = SendBlobs(client, parts[i].RequiredBlobs, blobs.Data);
				blobCount += count;
				blobBytes += bytes;
			}
			_t.Send(peer, Channel.Bulk, encoded[i]);
		}
		lock (_sync)
		{
			client.LifecycleMessages += encoded.Count;
			client.LifecycleBytes += worldBytes + snapshot.Length;
		}
		int missing = 0;
		if (blobs != null)
		{
			foreach (byte[] blob in blobs.Data.Values)
			{
				missing += blob == null ? 1 : 0;
			}
		}
		_log.Info($"sync(host): world for {peer} '{client.Name}' queued: snapshot {MpLogExtensions.Bytes(snapshot.Length)}, world {parts.Count} part(s) {MpLogExtensions.Bytes(worldBytes)} ({entities} entities, {withData} with captured data), {typeKeys.Count} type keys, {blobCount} offloaded blobs {MpLogExtensions.Bytes(blobBytes)} ({blobs?.Snapshot.Count ?? 0} for the snapshot, {missing} missing on this PC); {summary}");
	}

	private (int Count, long Bytes) SendBlobs(RemoteClient c, IReadOnlyList<Guid> ids, IReadOnlyDictionary<Guid, byte[]?> data)
	{
		if (ids.Count == 0)
		{
			return (Count: 0, Bytes: 0L);
		}
		var toSend = new List<KeyValuePair<Guid, byte[]>>();
		long bytes = 0L;
		lock (_sync)
		{
			foreach (Guid id in ids)
			{
				if (data.TryGetValue(id, out byte[] blob) && c.SentBlobs.Add(id))
				{
					toSend.Add(new KeyValuePair<Guid, byte[]>(id, blob));
					bytes += blob?.Length ?? 0;
				}
			}
			c.BlobsSent += toSend.Count;
			c.BlobBytes += bytes;
		}
		if (toSend.Count == 0)
		{
			return (Count: 0, Bytes: 0L);
		}
		var messages = new List<byte[]>();
		BlobDataMessage.Pack(toSend, 1048576, messages);
		foreach (byte[] msg in messages)
		{
			_t.Send(c.Peer, Channel.Bulk, msg);
		}
		return (Count: toSend.Count, Bytes: bytes);
	}

	public int PublishBlobs(IReadOnlyDictionary<Guid, byte[]?> data)
	{
		if (data.Count == 0)
		{
			return 0;
		}
		List<RemoteClient> synced;
		lock (_sync)
		{
			synced = new List<RemoteClient>();
			foreach (RemoteClient client in _clients.Values)
			{
				if (client.Status == ClientStatus.Synced)
				{
					synced.Add(client);
				}
			}
		}
		var ids = new List<Guid>(data.Keys);
		int sent = 0;
		foreach (RemoteClient client in synced)
		{
			sent += SendBlobs(client, ids, data).Count;
		}
		return sent;
	}

	public void FailCapture(PeerId peer, string reason)
	{
		lock (_sync)
		{
			if (_clients.TryGetValue(peer, out RemoteClient client))
			{
				if (client.Status == ClientStatus.Synced)
				{
					_syncedCount--;
				}
				client.Status = ClientStatus.Connected;
			}
		}
		Reject(peer, "the host could not capture its world: " + reason);
	}

	public int PublishLifecycle(byte[] encoded) => PublishLifecycle(encoded, null);

	public int PublishLifecycle(byte[] encoded, Func<PeerId, bool>? include)
	{
		int sent = 0;
		lock (_sync)
		{
			foreach (RemoteClient client in _clients.Values)
			{
				if (client.Status == ClientStatus.Synced && (include == null || include(client.Peer)))
				{
					byte[] copy = (byte[])encoded.Clone();
					EntityBundleMessage.SetLifeSeq(copy, client.NextLifeSeq++);
					Channel channel = copy.Length > 8192 ? Channel.Bulk : Channel.Control;
					_t.Send(client.Peer, channel, copy);
					client.LifecycleMessages++;
					client.LifecycleBytes += copy.Length;
					sent++;
				}
			}
			return sent;
		}
	}

	public bool PublishLifecycleTo(PeerId peer, byte[] encoded)
	{
		lock (_sync)
		{
			if (!_clients.TryGetValue(peer, out RemoteClient client) || client.Status != ClientStatus.Synced)
			{
				return false;
			}
			EntityBundleMessage.SetLifeSeq(encoded, client.NextLifeSeq++);
			Channel channel = encoded.Length > 8192 ? Channel.Bulk : Channel.Control;
			_t.Send(client.Peer, channel, encoded);
			client.LifecycleMessages++;
			client.LifecycleBytes += encoded.Length;
			client.P2.ObservableBatches++;
			client.P2.ObservableBytesOut += encoded.Length;
			return true;
		}
	}

	public void PublishDespawn(IReadOnlyList<uint> netIds, byte mode = 0) => PublishDespawn(netIds, mode, null);

	/// <summary><paramref name="roots"/>, when given, holds each id's root NetId; joiners that streamed that root out never had the entity.</summary>
	public void PublishDespawn(IReadOnlyList<uint> netIds, byte mode, IReadOnlyList<uint>? roots)
	{
		if (netIds.Count == 0 || _syncedCount == 0)
		{
			return;
		}
		byte[] all = DespawnMessage.Encode(0u, mode, netIds);
		lock (_sync)
		{
			foreach (RemoteClient client in _clients.Values)
			{
				if (client.Status != ClientStatus.Synced)
				{
					continue;
				}
				var streamedOut = client.StreamedOut;
				byte[] msg = all;
				if (roots != null && streamedOut.Count > 0)
				{
					var kept = new List<uint>(netIds.Count);
					for (int i = 0; i < netIds.Count; i++)
					{
						if (!streamedOut.Contains(roots[i])) kept.Add(netIds[i]);
					}
					if (kept.Count == 0)
					{
						continue;
					}
					if (kept.Count != netIds.Count)
					{
						msg = DespawnMessage.Encode(0u, mode, kept);
					}
				}
				SendLifecycle(client, msg == all ? (byte[])all.Clone() : msg);
			}
		}
		foreach (uint netId in netIds)
		{
			_despawnedIn.Enqueue(netId);
		}
	}

	/// <summary>Removes entities from one joiner only (streamed out of its radius): closed, then forgotten, like a destroyed entity.</summary>
	public void PublishStreamOut(PeerId peer, IReadOnlyList<uint> netIds)
	{
		if (netIds.Count == 0)
		{
			return;
		}
		lock (_sync)
		{
			if (!_clients.TryGetValue(peer, out RemoteClient client) || client.Status != ClientStatus.Synced)
			{
				return;
			}
			SendLifecycle(client, DespawnMessage.Encode(0u, 0, netIds));
			SendLifecycle(client, DespawnMessage.Encode(0u, 1, netIds));
			foreach (uint netId in netIds)
			{
				client.State.Remove(netId);
			}
		}
	}

	/// <summary>A stream-in bundle (or anything else) for one joiner; false if it is not synced.</summary>
	public bool PublishLifecycleOnlyTo(PeerId peer, byte[] encoded)
	{
		lock (_sync)
		{
			if (!_clients.TryGetValue(peer, out RemoteClient client) || client.Status != ClientStatus.Synced)
			{
				return false;
			}
			SendLifecycle(client, encoded);
			return true;
		}
	}

	private void SendLifecycle(RemoteClient client, byte[] msg)
	{
		EntityBundleMessage.SetLifeSeq(msg, client.NextLifeSeq++);
		_t.Send(client.Peer, msg.Length > 8192 ? Channel.Bulk : Channel.Control, msg);
		client.LifecycleMessages++;
		client.LifecycleBytes += msg.Length;
	}

	public void SetStreamedOut(PeerId peer, HashSet<uint> roots)
	{
		lock (_sync)
		{
			if (_clients.TryGetValue(peer, out RemoteClient client)) client.StreamedOut = roots.Count == 0 ? NothingStreamedOut : roots;
		}
	}

	public double StreamingRadiusMeters { get => _o.StreamingRadiusMeters; set => _o.StreamingRadiusMeters = value; }

	public int PublishBlobsTo(PeerId peer, IReadOnlyDictionary<Guid, byte[]?> data)
	{
		RemoteClient client;
		lock (_sync)
		{
			if (!_clients.TryGetValue(peer, out client) || client.Status != ClientStatus.Synced) return 0;
		}
		return SendBlobs(client, data.Keys.ToList(), data).Count;
	}

	public void PublishDataRemove(IReadOnlyList<(uint NetId, ushort Key)> items)
	{
		if (items.Count != 0 && _syncedCount != 0)
		{
			PublishLifecycle(DataRemoveMessage.Encode(0u, items));
		}
	}

	public void WorldChanged(string reason)
	{
		var peers = new List<PeerId>();
		lock (_sync)
		{
			foreach (RemoteClient client in _clients.Values)
			{
				if (client.Status is ClientStatus.CaptureRequested or ClientStatus.Synced)
				{
					peers.Add(client.Peer);
				}
			}
		}
		foreach (PeerId peer in peers)
		{
			_log.Warn($"sync(host): disconnecting {peer}: {reason}");
			_t.Disconnect(peer, 1000, reason);
		}
	}

	public void PublishState(in StateEntry e)
	{
		if (_syncedCount == 0)
		{
			Interlocked.Increment(ref StateDroppedNoClient);
			return;
		}
		Interlocked.Increment(ref StateIn);
		_stateIn.Enqueue(e);
	}

	public void Tick()
	{
		long now = _clock();
		var sw = System.Diagnostics.Stopwatch.StartNew();
		TickAdmin(now);
		long admin = sw.ElapsedMilliseconds;
		List<RemoteClient> synced;
		lock (_sync)
		{
			synced = new List<RemoteClient>(_clients.Count);
			foreach (RemoteClient client in _clients.Values)
			{
				if (client.Status == ClientStatus.Synced)
				{
					synced.Add(client);
				}
			}
		}
		uint netId;
		while (_despawnedIn.TryDequeue(out netId))
		{
			foreach (RemoteClient client in synced)
			{
				client.State.Remove(netId);
			}
		}
		long locked = sw.ElapsedMilliseconds;
		StateEntry entry;
		while (_stateIn.TryDequeue(out entry))
		{
			foreach (RemoteClient client in synced)
			{
				if (client.Wants(entry.Root)) client.State.Put(in entry, now);
			}
		}
		List<TypeKeyDef> newKeys = Keys.TakeUnannounced();
		if (newKeys.Count > 0)
		{
			byte[] msg = TypeKeysMessage.Encode(newKeys);
			foreach (RemoteClient client in synced)
			{
				_t.Send(client.Peer, Channel.Control, msg);
			}
		}
		bool emit = now - _lastEmitMs >= _o.StateEmitIntervalMs;
		if (emit)
		{
			_lastEmitMs = now;
		}
		foreach (RemoteClient client in synced)
		{
			if (emit)
			{
				_out.Clear();
				client.State.Emit(now, 1100, _out);
				foreach (byte[] msg in _out)
				{
					_t.Send(client.Peer, Channel.State, msg);
					client.StateBytes += msg.Length;
				}
			}
			if (now - client.LastStatusMs >= _o.StatusIntervalMs)
			{
				LogStatus(client, now);
			}
		}
		if (sw.ElapsedMilliseconds > 100)
		{
			_log.Warn($"sync(host): tick took {sw.ElapsedMilliseconds} ms (admin {admin}, clients lock {locked - admin}, state drain + send {sw.ElapsedMilliseconds - locked})");
		}
		if (now - _lastWaitCheckMs < 1000)
		{
			return;
		}
		_lastWaitCheckMs = now;
		if (!_world.WorldReady)
		{
			return;
		}
		var waiting = new List<RemoteClient>();
		lock (_sync)
		{
			foreach (RemoteClient client in _clients.Values)
			{
				if (client.Status == ClientStatus.WaitingForWorld)
				{
					waiting.Add(client);
				}
			}
		}
		foreach (RemoteClient client in waiting)
		{
			RequestCapture(client);
		}
	}

	private void LogStatus(RemoteClient c, long now)
	{
		double seconds = c.LastStatusMs == 0L ? Math.Max(1.0, (now - c.SyncedMs) / 1000.0) : (now - c.LastStatusMs) / 1000.0;
		var rates = new List<(string Name, double PerSec)>();
		foreach (var (key, sent) in c.State.SentByKey)
		{
			c.SentByKeyAtLastStatus.TryGetValue(key, out var before);
			if (sent > before)
			{
				rates.Add((KeyName(key), (sent - before) / seconds));
			}
			c.SentByKeyAtLastStatus[key] = sent;
		}
		rates.Sort((a, b) => b.PerSec.CompareTo(a.PerSec));
		string topRates = rates.Count == 0 ? "none" : string.Join(", ", rates.Take(8).Select(r => $"{r.Name} {r.PerSec:0}/s"));
		long stateBytes = c.StateBytes - c.StateBytesAtLastStatus;
		long entries = c.State.Sent - c.SentAtLastStatus;
		c.LastStatusMs = now;
		c.StateBytesAtLastStatus = c.StateBytes;
		c.SentAtLastStatus = c.State.Sent;
		StateCoalescer state = c.State;
		_log.Info($"sync(host) {c.Peer} '{c.Name}': state {stateBytes / 1024.0 / seconds:0.0} KiB/s ({entries / seconds:0}/s entries, {state.Messages} msgs total), in {state.Puts} coalesced {state.Coalesced} capped {state.DeferredByCap} budget-deferred {state.DeferredByBudget} dropped(cap 0) {state.DroppedByCap}, dirty {state.DirtyCount}/{state.EntryCount}; lifecycle {c.LifecycleMessages} msgs {MpLogExtensions.Bytes(c.LifecycleBytes)}; blobs {c.BlobsSent} ({MpLogExtensions.Bytes(c.BlobBytes)}); type keys {Keys.Count}; client phase {c.LastPhase}");
		_log.Info($"sync(host) {c.Peer} send rates: {topRates}");
		PlayerRecord player = c.Player;
		if (player == null)
		{
			return;
		}
		lock (c.Input)
		{
			_log.Info($"sync(host) {c.Peer} ClientId {player.ClientId} '{player.Name}' ({(player.IdentityId != 0L ? $"IdentityId {player.IdentityId}" : "host's identity")}{(c.LastStatus is { } status ? $", PCU {status.Pcu}/{status.Max}" : "")}): character {(player.CharacterNetId != 0 ? $"NetworkId {player.CharacterNetId}" : player.CharacterRequested ? "requested" : "none")}; "
				+ $"input {c.InputMessages} msgs / {c.InputEntries} values in, {c.InputApplied} applied, {c.InputRejected} rejected, {c.InputRemoved} removals, "
				+ $"arrival gaps > 50 ms {c.InputGaps} (largest {c.InputGapMaxMs} ms), host ticks with / without a fresh input message {c.TicksWithInput} / {c.TicksWithoutInput}; "
				+ $"commands {c.CommandsIn} in, {c.CommandsApplied} applied, {c.CommandsRejected} rejected; "
				+ $"signals out {c.P2.SignalsOut} in {c.P2.SignalBatches} batches ({MpLogExtensions.Bytes(c.P2.SignalBytesOut)}), names {c.P2.StringsOut}; "
				+ $"stories in {c.P2.StoriesIn}, moves out {c.P2.StoryMovesOut}, finished {c.P2.StoryFinishesOut}; "
				+ $"observables out {c.P2.ObservableBatches} batches ({MpLogExtensions.Bytes(c.P2.ObservableBytesOut)}), edits in {c.P2.ObservableEditsIn}; "
				+ (c.Look != null ? $"{c.Look.Describe()}, {c.LookControlWrites} ControlData written" : _o.LookTotals ? "look totals: none yet" : $"look totals ignored ({c.LookIgnored}, -y51online:predict:off)"));
		}
	}

	private string KeyName(ushort key)
	{
		foreach (TypeKeyDef def in Keys.All())
		{
			if (def.Key == key)
			{
				return def.ShortName;
			}
		}
		return $"key#{key}";
	}

	public ClientStatus? StatusOf(PeerId peer)
	{
		lock (_sync)
		{
			return _clients.TryGetValue(peer, out RemoteClient client) ? client.Status : null;
		}
	}
}
