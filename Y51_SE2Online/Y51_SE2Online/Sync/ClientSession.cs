using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Y51_SE2Online.Core;
using Y51_SE2Online.Net;

namespace Y51_SE2Online.Sync;

public sealed class ClientSession
{
	public enum Phase
	{
		Connecting,
		JoinSent,
		WaitingForHostWorld,
		Accepted,
		Rejected,
		Disconnected
	}

	private readonly ITransport _t;

	private readonly IMpLog _log;

	private readonly ClientSyncOptions _o;

	private readonly Func<string, (int TypeId, int Size)?> _resolveType;

	private readonly Func<long> _clock;

	private readonly ConcurrentQueue<(ClientPhase Phase, string Text)> _reports = new();

	private byte[]? _snapshot;

	private long _snapshotTaken;

	private volatile Phase _phase;

	public long LifecycleReceived;

	public long StateMessages;

	public long StateEntries;

	public long BlobMessages;

	private long _blobLogBytes;

	private readonly HashSet<ushort> _transformKeys = new();

	public long SmoothedEntries;

	public long SmoothedRefused;

	private long _assignVersion;

	private uint _upSeq;

	public long InputSent;

	public long CommandsSent;

	public long LookSent;

	private uint _lookSeq;

	public long StoryMovesIn;

	public long StoryFinishesIn;

	public long StoryMovesOut;

	public long ObservableOpsIn;

	public long ObservableEditsOut;

	private bool _detached;

	private uint _editSeq;

	public PeerId? Host { get; private set; }

	public Phase CurrentPhase => _phase;

	public JoinAccept? Accept { get; private set; }

	public string? RejectReason { get; private set; }

	public byte RejectCode { get; private set; }

	/// <summary>The refusing host's plugin version, from the connection handshake.</summary>
	public string RejectHostVersion { get; private set; } = "";

	public long RejectVersion { get; private set; }

	public AdminState Admin { get; private set; } = new();

	private long _adminMessageMs = long.MinValue / 2;

	private bool _adminSeen;

	public void SendChat(string text)
	{
		if (Host.HasValue && CurrentPhase == Phase.Accepted)
			_t.Send(Host.Value, Channel.Control, SocialMessage.Encode(SyncMsg.ChatSend, new ChatSend(text)));
	}

	/// <summary>The host's last Server info answer; Version counts answers so a screen can wait for the next one.</summary>
	public (ServerInfoRow[] Rows, int Version) ServerInfo { get; private set; } = (System.Array.Empty<ServerInfoRow>(), 0);

	public void RequestServerInfo()
	{
		if (Host.HasValue && CurrentPhase == Phase.Accepted)
			_t.Send(Host.Value, Channel.Control, SocialMessage.Encode(SyncMsg.ServerInfo, new ServerInfoRequest()));
	}

	private uint _voiceSeq;

	public void SendVoice(byte[] frame)
	{
		if (Host.HasValue && CurrentPhase == Phase.Accepted && frame.Length <= VoiceMessage.MaxFrameBytes)
			_t.Send(Host.Value, Channel.State, VoiceMessage.EncodeUp(++_voiceSeq, frame));
	}

	public void SendBuildGhost(BuildGhost ghost)
	{
		if (Host.HasValue && CurrentPhase == Phase.Accepted)
			_t.Send(Host.Value, Channel.Control, BuildGhostMessage.EncodeUp(ghost));
	}

	public void SendTrade(byte[] data)
	{
		if (Host.HasValue && CurrentPhase == Phase.Accepted)
			_t.Send(Host.Value, Channel.Control, data);
	}

	public void SendPunch(ushort target)
	{
		if (Host.HasValue && CurrentPhase == Phase.Accepted)
			_t.Send(Host.Value, Channel.Control, SocialMessage.Encode(SyncMsg.Punch, new PunchRequest(target)));
	}

	public void SendGpsShare(GpsShare share)
	{
		if (Host.HasValue && CurrentPhase == Phase.Accepted)
			_t.Send(Host.Value, Channel.Control, SocialMessage.Encode(SyncMsg.GpsShare, share));
	}

	public void SendDesyncReport(uint checkId, IReadOnlyList<DesyncMismatch> mismatches)
	{
		if (Host.HasValue && CurrentPhase == Phase.Accepted && mismatches.Count > 0)
		{
			byte[] msg = DesyncReportMessage.Encode(checkId, mismatches);
			_t.Send(Host.Value, msg.Length > 8192 ? Channel.Bulk : Channel.Control, msg);
		}
	}

	public void SendAdmin(AdminRequest request)
	{
		if (Host.HasValue && CurrentPhase == Phase.Accepted)
			_t.Send(Host.Value, Channel.Control, SocialMessage.Encode(SyncMsg.AdminRequest, request));
	}

	public RemoteTypeTable Types { get; } = new();

	public ClientStateTable State { get; } = new();

	public LifecycleSequencer<LifecycleItem> Lifecycle { get; } = new();

	public ClientBlobStore Blobs { get; } = new();

	public IReadOnlyList<Guid> SnapshotBlobs { get; private set; } = Array.Empty<Guid>();

	public TransformSmoother Smoother { get; } = new();

	/// <summary>Newest host tick seen on the wire and when it arrived (net thread writes, game thread reads).</summary>
	public HostTickMark? NewestHostTick => Volatile.Read(ref _newestHostTick);

	private HostTickMark? _newestHostTick;

	public ushort ClientId => Accept?.ClientId ?? 0;

	public (byte Status, uint NetId, string Text, long Version) Assignment { get; private set; }

	public PlayerNamesInbox Names { get; } = new();

	public RemoteSignalTable RemoteSignals { get; } = new();

	public ConcurrentQueue<object> StoryInbox { get; } = new();

	public IReadOnlyList<PlayerInfo> Players { get; private set; } = Array.Empty<PlayerInfo>();

	public long PlayersVersion { get; private set; }
	public SocialSnapshot Social { get; private set; } = new();
	public void SendSocial(SocialRequest request)
	{
		if (Host.HasValue && CurrentPhase == Phase.Accepted && IdentityId != 0)
			_t.Send(Host.Value, Channel.Control, SocialMessage.Encode(SyncMsg.SocialRequest, request));
	}

	public long IdentityId => Accept?.IdentityId ?? 0;

	public (int Pcu, int MaxPcu, long Version) Status { get; private set; }

	public bool IsDetached => _detached;

	public bool HasSnapshot => Volatile.Read(in _snapshot) != null;

	public bool LeftOnPurpose { get; private set; }

	public event Action? SnapshotReady;

	public ClientSession(ITransport transport, IMpLog log, ClientSyncOptions options, Func<string, (int TypeId, int Size)?> resolveType, Func<long>? clock = null)
	{
		_t = transport;
		_log = log;
		_o = options;
		_resolveType = resolveType;
		_clock = clock ?? new Func<long>(MpClock.NowMs);
		_t.PeerConnected += OnConnected;
		_t.PeerDisconnected += OnDisconnected;
		_t.MessageReceived += OnMessage;
	}

	public void Detach()
	{
		if (!_detached)
		{
			_detached = true;
			_t.PeerConnected -= OnConnected;
			_t.PeerDisconnected -= OnDisconnected;
			_t.MessageReceived -= OnMessage;
		}
	}

	public byte[]? TryTakeSnapshot()
	{
		byte[] snapshot = Volatile.Read(in _snapshot);
		if (snapshot == null || Interlocked.Exchange(ref _snapshotTaken, 1L) != 0L)
		{
			return null;
		}
		return snapshot;
	}

	public void Report(ClientPhase phase, string text)
	{
		string message = $"client: {phase}: {text}";
		if (phase == ClientPhase.Error)
		{
			_log.Error(message);
		}
		else if (phase >= ClientPhase.Warning)
		{
			_log.Warn(message);
		}
		else
		{
			_log.Info(message);
		}
		_reports.Enqueue((phase, text));
	}

	private void OnConnected(PeerId peer)
	{
		if (Host.HasValue)
		{
			_log.Warn($"sync(client): second host connection {peer} ignored");
			return;
		}
		Host = peer;
		_phase = Phase.JoinSent;
		if (_o.Progress is { } progress && _t.TryGetStats(peer, out PeerStats stats) && !string.IsNullOrWhiteSpace(stats.RemoteName))
		{
			progress.SetHost(stats.RemoteName);
		}
		_o.Progress?.Set(JoinStage.Connecting, _clock(), "Connected, asking to join...");
		_docs.Clear();
		var request = new JoinRequest
		{
			PluginVersion = _o.PluginVersion,
			GameVersion = _o.GameVersion,
			PlayerName = _o.PlayerName,
			Password = _o.Password ?? "",
			Features = JoinRequest.RosterPatches | JoinRequest.TradeView | JoinRequest.BuildGhosts
		};
		_t.Send(peer, Channel.Control, request.Encode());
		_log.Info($"sync(client): join request sent to {peer}");
		_reports.Enqueue((ClientPhase.JoinSent, "plugin " + _o.PluginVersion + ", game " + _o.GameVersion));
	}

	private void OnDisconnected(PeerId peer, DisconnectInfo info)
	{
		if (Host == peer)
		{
			// NetSession tells a refused join from a lost connection by this phase.
			if (_phase != Phase.Rejected)
			{
				_phase = Phase.Disconnected;
			}
			_log.Warn($"sync(client): disconnected from host ({info})");
		}
	}

	private void OnMessage(PeerId peer, Channel ch, byte[] data)
	{
		if (!SyncProtocol.IsSyncMessage(data))
		{
			return;
		}
		PeerId? host = Host;
		if (!host.HasValue || peer != host.GetValueOrDefault())
		{
			return;
		}
		try
		{
			Handle(ch, data);
		}
		catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException or ArgumentException)
		{
			_log.Warn($"sync(client): malformed {(SyncMsg)data[0]} ({data.Length} B on {ch}): {ex.Message}; disconnecting");
			_t.Disconnect(peer, DisconnectCodes.ProtocolError, $"malformed {(SyncMsg)data[0]}");
		}
	}

	// Roster documents the host keeps up to date with patches, by message kind (RosterDocs.cs).
	private readonly Dictionary<byte, DocInbox> _docs = new();

	private static T Typed<T>(System.Text.Json.Nodes.JsonObject doc)
	{
		try { return System.Text.Json.JsonSerializer.Deserialize<T>(doc) ?? throw new ArgumentException("Empty roster document."); }
		catch (System.Text.Json.JsonException e) { throw new ArgumentException("Invalid roster document.", e); }
	}

	private void OnPlayerTable(List<PlayerInfo> players)
	{
		Players = players;
		PlayersVersion++;
		_log.Info("sync(client): player table: " + DescribePlayers(Players));
	}

	private void OnSocial(SocialSnapshot snapshot)
	{
		if (snapshot.Actor != IdentityId || snapshot.Players == null || snapshot.Factions == null || snapshot.Grids == null) return;
		try { new SocialState { Players = snapshot.Players.ToList(), Factions = snapshot.Factions.ToList() }.Validate(); }
		catch (InvalidOperationException error) { throw new ArgumentException("Invalid social snapshot.", error); }
		Social = snapshot;
	}

	private void OnAdmin(AdminState state, long now)
	{
		// Roster updates carry no message; keep the last action's feedback readable for a while.
		if (!string.IsNullOrEmpty(state.Message)) _adminMessageMs = now;
		else if (now - _adminMessageMs < 10000) state.Message = Admin.Message;
		if (!_adminSeen || state.Role != Admin.Role) _log.Info($"admin(client): this player's role on the server is {state.Role}");
		_adminSeen = true;
		Admin = state;
	}

	private void OnNames(uint version, List<PlayerTag> tags)
	{
		if (Names.Accept(version, tags))
		{
			_log.Info($"nametags(client): player list v{version} from the host: {PlayerNamesMessage.Describe(tags)}");
		}
	}

	private void Handle(Channel ch, byte[] data)
	{
		long now = _clock();
		switch ((SyncMsg)data[0])
		{
		case SyncMsg.JoinAccept:
		{
			JoinAccept accept = JoinAccept.Decode(data);
			Accept = accept;
			Players = Accept.Players;
			PlayersVersion++;
			_phase = Phase.Accepted;
			// Bulk bytes received before the accept are not world data; join progress counts from here.
			long? bulkBase = Host is PeerId host && _t.TryGetStats(host, out PeerStats stats) ? stats.Channels[1].BytesReceived : null;
			_o.Progress?.Accepted(Accept.WorldName, Accept.SnapshotBytes + Accept.WorldBytes + Accept.WorldBlobBytes, Accept.WorldParts, now, bulkBase);
			_o.Progress?.SetWorldEntities(Accept.WorldEntities);
			_log.Info($"sync(client): JOIN ACCEPTED: world '{Accept.WorldName}', snapshot {MpLogExtensions.Bytes(Accept.SnapshotBytes)}, world {MpLogExtensions.Bytes(Accept.WorldBytes)} in {Accept.WorldParts} part(s) ({Accept.WorldEntities} entities), {Accept.WorldBlobs} offloaded blobs ({MpLogExtensions.Bytes(Accept.WorldBlobBytes)}) on the way; this player is ClientId {Accept.ClientId}");
			_log.Info($"sync(client): host settings: {Accept.Settings}; " + (Accept.IdentityId != 0L ? $"this player is IdentityId {Accept.IdentityId} (PlayerIdentity {Accept.PlayerId}); players: {DescribePlayers(Accept.Players)}" : "this player plays as the host's identity (identities off on the host)"));
			break;
		}
		case SyncMsg.JoinReject:
			var (code, reason) = TextMessage.Decode(data);
			RejectReason = reason;
			RejectCode = code;
			RejectHostVersion = Host is PeerId rejectedBy && _t.TryGetStats(rejectedBy, out PeerStats rejectStats) ? rejectStats.RemotePluginVersion : "";
			RejectVersion++;
			if (code == JoinRejectCode.Removed)
			{
				reason = System.Text.Json.JsonSerializer.Deserialize<RemovalNotice>(reason)?.Describe(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) ?? "Removed by an admin.";
			}
			_phase = Phase.Rejected;
			_o.Progress?.Set(JoinStage.Rejected, now, reason);
			_log.Error("sync(client): JOIN REJECTED by host: " + reason);
			break;
		case SyncMsg.JoinWait:
		{
			string text = TextMessage.Decode(data).Text;
			_phase = Phase.WaitingForHostWorld;
			_o.Progress?.Set(JoinStage.WaitingForHostWorld, now, text);
			_log.Info("sync(client): host says wait: " + text);
			break;
		}
		case SyncMsg.TypeKeys:
		{
			List<TypeKeyDef> defs = TypeKeysMessage.Decode(data);
			List<string> unusable = Types.Add(defs, _resolveType);
			foreach (TypeKeyDef def in defs)
			{
				if (def.ShortName == "WorldTransform")
				{
					lock (_transformKeys)
					{
						_transformKeys.Add(def.Key);
					}
				}
			}
			_log.Info($"sync(client): {defs.Count} type key(s) received ({Types.Count} total), {unusable.Count} unusable");
			foreach (string problem in unusable)
			{
				_log.Warn("sync(client): " + problem);
			}
			break;
		}
		case SyncMsg.Snapshot:
		{
			byte[] snapshot = SnapshotMessage.Decode(data, out List<Guid> requiredBlobs);
			SnapshotBlobs = requiredBlobs;
			Volatile.Write(ref _snapshot, snapshot);
			Blobs.Check(requiredBlobs, now, out var absent);
			_log.Info($"sync(client): session snapshot received ({MpLogExtensions.Bytes(snapshot.Length)}; references {requiredBlobs.Count} offloaded blobs, {absent} not here; blobs so far: {Blobs.Describe()})");
			_reports.Enqueue((ClientPhase.SnapshotReceived, MpLogExtensions.Bytes(snapshot.Length)));
			try
			{
				SnapshotReady?.Invoke();
			}
			catch (Exception ex)
			{
				_log.Error($"sync(client): SnapshotReady handler threw: {ex}");
			}
			break;
		}
		case SyncMsg.EntityBundle:
		{
			var bundle = EntityBundleMessage.Decode(data);
			Offer(bundle.LifeSeq, new LifecycleItem
			{
				Type = SyncMsg.EntityBundle,
				Bundle = bundle,
				WireBytes = data.Length
			});
			if (bundle.Kind == 0)
			{
				_o.Progress?.PartReceived();
			}
			if (bundle.Kind == 0)
			{
				_log.Info($"sync(client): world bundle received: {bundle.Entities.Count} entities, {MpLogExtensions.Bytes(bundle.Bundle.Length)} engine bundle, {bundle.Data.Count} entities with data ({MpLogExtensions.Bytes(bundle.DataBytes)}), lifeSeq {bundle.LifeSeq}");
			}
			break;
		}
		case SyncMsg.Despawn:
			var (despawnSeq, despawnMode, netIds) = DespawnMessage.Decode(data);
			Offer(despawnSeq, new LifecycleItem
			{
				Type = SyncMsg.Despawn,
				NetIds = netIds,
				DespawnMode = despawnMode,
				WireBytes = data.Length
			});
			break;
		case SyncMsg.DataRemove:
		case SyncMsg.DataTag:
			var (removeSeq, removes) = DataRemoveMessage.Decode(data);
			Offer(removeSeq, new LifecycleItem
			{
				Type = (SyncMsg)data[0],
				Removes = removes,
				WireBytes = data.Length
			});
			break;
		case SyncMsg.SignalKeys:
		{
			List<SignalKeyDef> defs = SignalKeysMessage.Decode(data);
			List<string> unusable = RemoteSignals.Add(defs, _o.CheckSignal);
			_log.Info($"sync(client): {defs.Count} host signal key(s) ({RemoteSignals.Count} total), {unusable.Count} unusable");
			foreach (string problem in unusable)
			{
				_log.WarnOnce("client-sigkey-" + problem, "sync(client): " + problem + "; ignored");
			}
			break;
		}
		case SyncMsg.SignalBatch:
			var (signalSeq, hostTick, signals) = SignalBatchMessage.Decode(data);
			if (Volatile.Read(ref _newestHostTick) is not { } mark || hostTick > mark.Tick)
			{
				Volatile.Write(ref _newestHostTick, new HostTickMark(hostTick, _clock()));
			}
			Offer(signalSeq, new LifecycleItem
			{
				Type = SyncMsg.SignalBatch,
				Signals = signals,
				HostTick = hostTick,
				WireBytes = data.Length
			});
			break;
		case SyncMsg.StringTable:
			var (stringSeq, strings) = StringTableMessage.Decode(data);
			Offer(stringSeq, new LifecycleItem
			{
				Type = SyncMsg.StringTable,
				Strings = strings,
				WireBytes = data.Length
			});
			break;
		case SyncMsg.ObservableBatch:
			var (observableSeq, ops) = ObservableBatchMessage.Decode(data);
			ObservableOpsIn += ops.Count;
			Offer(observableSeq, new LifecycleItem
			{
				Type = SyncMsg.ObservableBatch,
				Observables = ops,
				WireBytes = data.Length
			});
			break;
		case SyncMsg.PlayerTable:
			// A host that sends patches never sends this; one that overtook them would be older.
			if (!_docs.ContainsKey(data[0])) OnPlayerTable(PlayerTableMessage.Decode(data));
			break;
		case SyncMsg.SocialSnapshot:
			if (!_docs.ContainsKey(data[0])) OnSocial(SocialMessage.Decode<SocialSnapshot>(data));
			break;
		case SyncMsg.RosterSync:
		{
			if (data.Length < 12 || !RosterSync.IsKind(data[1])) break;
			byte kind = data[1];
			if (!_docs.TryGetValue(kind, out var inbox)) _docs[kind] = inbox = new DocInbox();
			var doc = inbox.Receive(data);
			if (inbox.TakeFullRequest())
			{
				_log.Warn($"sync(client): lost the host's {(SyncMsg)kind}; asking for it whole");
				_t.Send(Host.Value, Channel.Control, RosterSync.Request(kind));
			}
			if (doc == null) break;
			switch ((SyncMsg)kind)
			{
			case SyncMsg.PlayerTable: OnPlayerTable(Typed<PlayerTableDoc>(doc).Players ?? new List<PlayerInfo>()); break;
			case SyncMsg.SocialSnapshot: OnSocial(Typed<SocialSnapshot>(doc)); break;
			case SyncMsg.AdminState: OnAdmin(Typed<AdminState>(doc), now); break;
			case SyncMsg.PlayerNames:
				var names = Typed<PlayerNamesDoc>(doc);
				OnNames(names.Version, names.Tags ?? new List<PlayerTag>());
				break;
			}
			break;
		}
		case SyncMsg.ChatLine:
		{
			var line = SocialMessage.Decode<ChatLine>(data);
			ChatLog.Add(line with { From = SocialState.Clean(line.From, 80), Text = SocialState.Clean(line.Text, ChatLog.MaxText) }, now);
			break;
		}
		case SyncMsg.BuildGhost:
			if (BuildGhostMessage.TryDecodeDown(data, out ushort builder, out BuildGhost ghost)) BuildGhostMessage.Received?.Invoke(builder, ghost);
			break;
		case SyncMsg.VoiceDown:
			if (VoiceMessage.TryDecodeDown(data, out ushort speaker, out Vec3D at, out byte[] frame)) VoiceMessage.Received?.Invoke(speaker, at, frame);
			break;
		case SyncMsg.InputAck:
		{
			var (ackSeq, appliedHostMs) = InputAckMessage.Decode(data);
			var sent = _inputSent[ackSeq % (uint)_inputSent.Length];
			if (sent.Seq == ackSeq && ackSeq != InputAck.Seq && (InputAck.Seq == 0 || Seq.Newer(ackSeq, InputAck.Seq)))
			{
				long rtt = now - sent.LocalMs;
				InputAck = (ackSeq, sent.LocalMs, appliedHostMs, rtt);
				if (rtt >= 0 && rtt < 2000)
				{
					_rttWindow[_rttCount++ % _rttWindow.Length] = rtt;
					InputRttMs = MedianRtt();
				}
				if (now - _ackWorstSinceMs > 5000) { _ackWorstSinceMs = now; InputAckWorstMs = 0; }
				if (rtt > InputAckWorstMs) InputAckWorstMs = rtt;
			}
			break;
		}
		case SyncMsg.TradeInvite:
		case SyncMsg.TradeState:
			if (data.Length <= 65536) TradeInbox.Raise((SyncMsg)data[0], data);
			break;
		case SyncMsg.ServerInfo:
		{
			var info = SocialMessage.Decode<ServerInfoMessage>(data);
			ServerInfo = ((info.Rows ?? System.Array.Empty<ServerInfoRow>()).Where(r => r != null).Take(Sync.ServerInfo.MaxRows).ToArray(), ServerInfo.Version + 1);
			break;
		}
		case SyncMsg.Motd:
		{
			var motd = SocialMessage.Decode<MotdMessage>(data);
			Motd = new MotdMessage(SocialState.Clean(motd.Title ?? "", Core.Motd.MaxTitle), JoinProgress.Sanitize(motd.Text, Core.Motd.MaxText));
			break;
		}
		case SyncMsg.WaterGrids:
			WaterGridsMessage.Receive(data);
			break;
		case SyncMsg.FightLock:
			if (data.Length <= 64) FightLockState.Set(SocialMessage.Decode<FightLockMessage>(data).Locked);
			break;
		case SyncMsg.SpawnMenu:
		{
			var menu = SocialMessage.Decode<SpawnMenu>(data);
			if (menu.Options == null || menu.Options.Length is 0 or > 64) break;
			SpawnMenu = menu with { Message = SocialState.Clean(menu.Message ?? "", 200), Options = menu.Options.Select(o => o with { Name = SocialState.Clean(o.Name ?? "", 100), Detail = SocialState.Clean(o.Detail ?? "", 120) }).ToArray() };
			break;
		}
		case SyncMsg.AdminState:
			if (!_docs.ContainsKey(data[0])) OnAdmin(SocialMessage.Decode<AdminState>(data), now);
			break;
		case SyncMsg.PlayerStatus:
		{
			var (_, pcu, maxPcu) = PlayerStatusMessage.Decode(data);
			Status = (pcu, maxPcu, Status.Version + 1);
			break;
		}
		case SyncMsg.StoryMove:
			StoryMovesIn++;
			StoryInbox.Enqueue(StoryMove.Decode(data));
			break;
		case SyncMsg.StoryFinish:
			StoryFinishesIn++;
			StoryInbox.Enqueue(StoryFinish.Decode(data));
			break;
		case SyncMsg.BlobData:
		{
			List<BlobPiece> pieces = BlobDataMessage.Decode(data);
			BlobMessages++;
			int rejected = 0;
			foreach (BlobPiece piece in pieces)
			{
				if (!Blobs.Accept(piece, now))
				{
					rejected++;
				}
			}
			if (rejected > 0)
			{
				_log.Warn($"sync(client): {rejected} inconsistent blob piece(s) ignored");
			}
			if (Blobs.BytesReceived - _blobLogBytes >= 16777216 || BlobMessages == 1)
			{
				_blobLogBytes = Blobs.BytesReceived;
				_log.Info($"sync(client): offloaded blobs: {Blobs.Describe()}, {MpLogExtensions.Bytes(Blobs.BytesReceived)} received");
			}
			break;
		}
		case SyncMsg.StateBatch:
		{
			List<StateEntry> entries = StateBatchMessage.Decode(data);
			StateMessages++;
			StateEntries += entries.Count;
			foreach (StateEntry e in entries)
			{
				if (e.NetId == StateCoalescer.WatchNetId && StateCoalescer.WatchLog.Count < 20000)
				{
					StateCoalescer.WatchLog.Enqueue($"arrived,{now},{e.Key},{e.HostMs}");
				}
				if (Smoother.Enabled && IsTransformKey(e.Key) && !State.IsTombstoned(e.NetId) && !Smoother.IsRaw(e.NetId))
				{
					if (Smoother.Add(e.NetId, e.HostMs, e.Bytes, now))
					{
						SmoothedEntries++;
					}
					else
					{
						SmoothedRefused++;
					}
				}
				else
				{
					if (Smoother.Enabled && IsTransformKey(e.Key))
					{
						Smoother.NoteClock(e.HostMs, now);
					}
					State.Put(in e, now);
				}
			}
			break;
		}
		case SyncMsg.PlayerAssign:
		{
			var (clientId, status, netId, text) = PlayerAssignMessage.Decode(data);
			Assignment = (status, netId, text, ++_assignVersion);
			SpawnMenu = null;
			string what = status switch
			{
				0 => $"CHARACTER ASSIGNED: NetworkId {netId}",
				2 => $"character NetworkId {netId} lost",
				_ => "no character",
			};
			_log.Info($"sync(client): ClientId {clientId}: {what} ({text})");
			break;
		}
		case SyncMsg.PlayerNames:
			if (!_docs.ContainsKey(data[0]))
			{
				var (namesVersion, tags) = PlayerNamesMessage.Decode(data);
				OnNames(namesVersion, tags);
			}
			break;
		case SyncMsg.DesyncCheck:
		{
			var check = DesyncCheckMessage.Decode(data);
			Offer(check.LifeSeq, new LifecycleItem { Type = SyncMsg.DesyncCheck, Check = check, WireBytes = data.Length });
			break;
		}
		default:
			_log.WarnOnce($"client-unexpected-{data[0]}", $"sync(client): unexpected {(SyncMsg)data[0]} on {ch}");
			break;
		}
	}

	public static string DescribePlayers(IReadOnlyList<PlayerInfo> players)
	{
		if (players.Count != 0)
		{
			return string.Join(", ", players.Select(p => $"ClientId {p.ClientId} '{p.Name}' = IdentityId {p.Identity}{(p.Connected ? "" : " (away)")}"));
		}
		return "none";
	}

	public void Leave(string reason)
	{
		LeftOnPurpose = true;
		PeerId? host = Host;
		if (host.HasValue)
		{
			PeerId hostPeer = host.GetValueOrDefault();
			_t.Disconnect(hostPeer, 1000, reason);
		}
	}

	private void Offer(uint seq, LifecycleItem item)
	{
		LifecycleReceived++;
		if (!Lifecycle.Offer(seq, item))
		{
			if (Lifecycle.Overflow > 0 && Host.HasValue)
			{
				_log.Error($"sync(client): lifecycle buffer overflow waiting for #{Lifecycle.NextExpected}; disconnecting");
				_t.Disconnect(Host.Value, 1001, "lifecycle buffer overflow");
			}
			else
			{
				_log.Warn($"sync(client): duplicate lifecycle #{seq} ignored");
			}
		}
		else if (seq != Lifecycle.NextExpected - 1 && Lifecycle.Buffered > 0 && Lifecycle.Buffered % 100 == 1)
		{
			_log.Info($"sync(client): lifecycle #{seq} arrived early, waiting for #{Lifecycle.NextExpected} ({Lifecycle.Buffered} buffered)");
		}
	}

	public bool TryGetTransformType(out int typeId, out int size)
	{
		lock (_transformKeys)
		{
			foreach (ushort transformKey in _transformKeys)
			{
				if (Types.TryResolve(transformKey, out typeId, out size) && size == 40)
				{
					return true;
				}
			}
		}
		typeId = -1;
		size = 0;
		return false;
	}

	public bool IsTransformKey(ushort key)
	{
		lock (_transformKeys)
		{
			if (!_transformKeys.Contains(key))
			{
				return false;
			}
		}
		if (Types.TryResolve(key, out var _, out var size))
		{
			return size == 40;
		}
		return false;
	}

	public void RequestCharacter(byte reason, int choice = -1)
	{
		if (Host.HasValue)
		{
			_t.Send(Host.Value, Channel.Control, CharacterRequestMessage.Encode(reason, choice));
		}
	}

	/// <summary>The host's spawn choices while we wait for a character; null once chosen.</summary>
	public SpawnMenu? SpawnMenu { get; private set; }

	/// <summary>The host's message of the day, until it has been shown.</summary>
	public MotdMessage? Motd { get; set; }

	public void ChooseSpawn(int index)
	{
		var menu = SpawnMenu;
		if (menu == null || index < 0 || index >= menu.Options.Length || !menu.Options[index].Available) return;
		SpawnMenu = null;
		RequestCharacter(menu.Respawn ? CharacterRequestMessage.Respawn : CharacterRequestMessage.Initial, index);
	}

	public void AnnounceInputTypes(IReadOnlyList<TypeKeyDef> defs)
	{
		if (Host.HasValue && defs.Count > 0)
		{
			_t.Send(Host.Value, Channel.Control, TypeKeysMessage.Encode(defs));
		}
	}

	public void AnnounceSignals(IReadOnlyList<SignalKeyDef> defs)
	{
		if (Host.HasValue && defs.Count > 0)
		{
			_t.Send(Host.Value, Channel.Control, SignalKeysMessage.Encode(defs));
		}
	}

	public void SendObservableEdits(IReadOnlyList<MemberEdit> edits)
	{
		if (Host.HasValue && edits.Count != 0)
		{
			for (int i = 0; i < edits.Count; i += 256)
			{
				List<MemberEdit> chunk = edits.Skip(i).Take(256).ToList();
				byte[] msg = ObservableEditsMessage.Encode(++_editSeq, chunk);
				_t.Send(Host.Value, msg.Length > 8192 ? Channel.Bulk : Channel.Control, msg);
			}
			ObservableEditsOut += edits.Count;
		}
	}

	public void SendStoryMove(StoryMove move)
	{
		if (Host.HasValue)
		{
			byte[] msg = move.Encode();
			_t.Send(Host.Value, msg.Length > 8192 ? Channel.Bulk : Channel.Control, msg);
			StoryMovesOut++;
		}
	}

	public void SendLook(uint netId, ushort epoch, double x, double y, double z)
	{
		if (Host.HasValue)
		{
			_t.Send(Host.Value, Channel.State, ClientLookMessage.Encode(++_lookSeq, netId, epoch, x, y, z));
			LookSent++;
		}
	}

	private readonly (uint Seq, long LocalMs)[] _inputSent = new (uint, long)[512];

	/// <summary>Diagnostics: the newest input the host applied, when it was sent, and how long until its ack came back.</summary>
	public (uint Seq, long SentLocalMs, uint AppliedHostMs, long RoundTripMs) InputAck { get; private set; }

	public long InputAckWorstMs { get; private set; }

	/// <summary>Median time from sending an input to the host's ack of it over the last acks; -1 until the first ack.</summary>
	public double InputRttMs { get; private set; } = -1;

	// Inputs go out only when they change, so acks are sparse: a 0.1 moving average took ~20 acks (often many
	// seconds) to follow a ping change, and a lag estimate that short dragged the predicted look and lead back.
	private readonly long[] _rttWindow = new long[9];

	private int _rttCount;

	private long _pongsSeen;

	private long _rttCheckMs;

	private double MedianRtt()
	{
		int n = Math.Min(_rttCount, _rttWindow.Length);
		var sorted = _rttWindow.AsSpan(0, n).ToArray();
		Array.Sort(sorted);
		return n % 2 == 1 ? sorted[n / 2] : (sorted[n / 2 - 1] + sorted[n / 2]) / 2.0;
	}

	private long _ackWorstSinceMs;

	public void SendUpstream(IReadOnlyList<InputEntry> input, IReadOnlyList<WireCommand> commands)
	{
		if (!Host.HasValue)
		{
			return;
		}
		uint seq = ++_upSeq;
		if (input.Count > 0)
		{
			_inputSent[seq % (uint)_inputSent.Length] = (seq, _clock());
			// Split so each message stays under ~1100 B; every part shares the same seq.
			var batch = new List<InputEntry>();
			int batchBytes = 7;
			foreach (InputEntry entry in input)
			{
				if (batch.Count > 0 && batchBytes + 8 + entry.Bytes.Length > 1100)
				{
					_t.Send(Host.Value, Channel.State, ClientInputMessage.Encode(seq, batch));
					batch = new List<InputEntry>();
					batchBytes = 7;
				}
				batch.Add(entry);
				batchBytes += 8 + entry.Bytes.Length;
			}
			_t.Send(Host.Value, Channel.State, ClientInputMessage.Encode(seq, batch));
			InputSent += input.Count;
		}
		if (commands.Count > 0)
		{
			byte[] msg = ClientSignalsMessage.Encode(seq, commands);
			_t.Send(Host.Value, msg.Length > 8192 ? Channel.Bulk : Channel.Control, msg);
			CommandsSent += commands.Count;
		}
	}

	public void Tick()
	{
		if (!Host.HasValue || _detached)
		{
			return;
		}
		bool progress = _o.Progress != null && Accept != null && _phase == Phase.Accepted;
		long nowMs = _clock();
		if ((progress || nowMs - _rttCheckMs >= 250) && _t.TryGetStats(Host.Value, out PeerStats stats))
		{
			_rttCheckMs = nowMs;
			if (progress) _o.Progress.BulkBytes(stats.Channels[1].BytesReceived);
			// The transport's 1 s pings keep the round trip current while no input changes (and so nothing is acked).
			if (stats.PongsReceived != _pongsSeen && stats.LastRttMs >= 0 && stats.LastRttMs < 2000 && InputRttMs >= 0)
			{
				_pongsSeen = stats.PongsReceived;
				_rttWindow[_rttCount++ % _rttWindow.Length] = (long)Math.Round(stats.LastRttMs);
				InputRttMs = MedianRtt();
			}
		}
		(ClientPhase Phase, string Text) report;
		while (_reports.TryDequeue(out report))
		{
			_t.Send(Host.Value, Channel.Control, TextMessage.Encode(SyncMsg.ClientStatus, (byte)report.Phase, report.Text));
		}
	}
}

public sealed record HostTickMark(uint Tick, long AtMs);
