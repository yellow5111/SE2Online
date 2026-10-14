using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Y51_SE2Online.Core;
using Y51_SE2Online.Net;
using Y51_SE2Online.Steam;
using Y51_SE2Online.Sync;
using Y51_SE2Online.SyncEngine;
using Steamworks;

namespace Y51_SE2Online.NetHost;

internal sealed class NetSession
{
	private static NetSession? _current;

	private readonly MpOptions _o;

	private readonly IMpLog _log;

	private readonly string _pluginVersion;

	private readonly string _gameVersion;

	private readonly ManualResetEventSlim _steamReady = new(initialState: false);

	private readonly Thread _thread;

	private volatile bool _stop;

	private string _stopReason = "stopped";

	private long _lastDetailedMs;

	private bool _idleLogged;

	private HostAdvertiser? _advertiser;
	private DirectoryPublisher _directory;
	private ulong _localIdentity;
	private long _nextDirectoryMs;

	private FriendFinder? _friends;

	private ulong _joinTarget;

	private long _nextFriendScanMs;

	private long _nextPresenceMs;

	private string _lastFriendsLine = "";

	private bool _joinTargetLocked;

	private PlayerNamesPublisher? _names;

	private ClientSession? _client;

	private volatile MpTransport? _transport;

	private volatile HostSession? _host;

	private volatile string? _rejoinWhy;

	private bool _rejoinClosing;

	private long _rejoinSinceMs;

	private string _lastLoss = "connection closed";

	private const int RejoinCloseTimeoutMs = 5000;

	private const int RejoinFirstAttemptMs = 2000;

	private string _playerName = "";

	private Action<string> _rejoinHandler;

	private const int FriendScanMs = 10000;

	private const int PresenceUpdateMs = 2000;

	private long _lastNoHostLogMs;

	private string _targetSource = "";

	private bool _waitLogged;

	public static NetSession? Current => _current;

	public bool HasStopped => !_thread.IsAlive;

	public static bool ClearStopped()
	{
		if (_current != null && !_current.HasStopped) return false;
		_current = null;
		return true;
	}

	public IReadOnlyList<HostSession.PlayerRecord> Players => _host?.Players() ?? Array.Empty<HostSession.PlayerRecord>();
	public bool IsHost => _o.Host;
	public string Failure { get; private set; }
	public string DirectoryError => _directory?.Error;
	public ulong LocalIdentity => _localIdentity;
	public bool TransportReady => _transport != null && !HasStopped;
	public HostSession? Host => _host;
	public ClientSession? Client => _client;
	public bool IsDedicated => _o.Dedicated;
	public string SessionName => _o.SessionName;

	/// <summary>Kick/ban/admin action by the local host or dedicated console; joiner admins send AdminRequest instead.</summary>
	public string LocalAdmin(AdminRequest request)
	{
		HostSession host = _host;
		if (!_o.Host || host == null) return "Not hosting.";
		return host.LocalAdmin(request);
	}

	public void Kick(ulong id, string reason = "") => LocalAdmin(new AdminRequest(AdminAction.Kick, id, reason));

	/// <summary>A listen host saved settings while hosting: listing, join and world rules change without restarting hosting.</summary>
	public void ApplyHostSettings(OnlineSettings before, OnlineSettings after, IReadOnlyList<string> changes)
	{
		if (!_o.Host || _o.Dedicated) return;
		_o.MaxPlayers = after.MaxPlayers;
		_o.FriendsOnly = after.FriendsOnly;
		_o.Advertise = after.Advertise;
		_o.SessionName = after.SessionName.Trim();
		_o.Description = after.Description.Trim();
		_o.PublicAddress = after.PublicAddress ?? "";
		_o.Password = after.Password ?? "";
		_o.Pvp = after.Pvp;
		_o.OfflineRaidProtection = after.OfflineRaidProtection;
		_o.PcuLimit = after.PcuLimit;
		_o.SunRotation = after.SunRotation;
		_o.SunPeriodMinutes = after.SunPeriodMinutes;
		_o.StreamingRadiusMeters = after.StreamingRadiusMeters;
		_o.VoiceRadiusMeters = after.VoiceRadiusMeters;
		_o.WaterSync = after.WaterSync;
		_o.UsageMultipliers = after.UsageMultipliers;
		ResourceMultipliers.Current = after.UsageMultipliers;
		_host?.ApplySettings(before, after, changes);
		_log.Info("host: settings applied without restarting hosting" + (changes.Count > 0 ? ": " + string.Join(", ", changes) : ""));
	}

	internal static string AccessPath(bool dedicated) => dedicated && Y51_SE2Online.Dedicated.DedicatedRuntime.Config != null
		? System.IO.Path.Combine(Y51_SE2Online.Dedicated.DedicatedRuntime.Config.Directory, "access.json")
		: WorldAccessPath(HostWorld.Current?.WorldKey);

	// A listen host keeps admins, bans, whitelist and runtime choices per world; one shared file made every
	// player promoted in any hosted world an admin in all of them. The old shared file only seeds bans.
	private AccessList LoadAccess(string path)
	{
		_log.Info("host: admins, bans and whitelist from " + path);
		return AccessList.Load(path);
	}

	internal static string WorldAccessPath(string? worldKey)
	{
		string legacy = PluginPaths.Data("Y51_SE2Online.access.json");
		if (string.IsNullOrWhiteSpace(worldKey) || worldKey == "?") return legacy;
		string safe = string.Concat(worldKey.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
		if (safe.Length > 80) safe = safe[..80];
		string path = PluginPaths.Data("Y51_SE2Online.access." + safe + ".json");
		if (!System.IO.File.Exists(path) && System.IO.File.Exists(legacy))
		{
			try { AccessList.Load(legacy).BansOnlyCopy(path); }
			catch (Exception) { }
		}
		return path;
	}


	private NetSession(MpOptions o, IMpLog log, string pluginVersion, string gameVersion)
	{
		_o = o;
		_log = log;
		_pluginVersion = pluginVersion;
		_gameVersion = gameVersion;
		_thread = new Thread(Run)
		{
			IsBackground = true,
			Name = "Y51_SE2Online-Net"
		};
	}

	public static void Start(MpOptions o, IMpLog log, string pluginVersion, string gameVersion)
	{
		if (_current == null)
		{
			_current = new NetSession(o, log, pluginVersion, gameVersion);
			_current._thread.Start();
			log.Info("net: thread started (" + o.Describe() + ")" + (o.NeedsSteam ? ", waiting for Steam" : ""));
			if (o.Join && o.Sync)
			{
				ClientWorld.Progress.Set(o.NeedsSteam ? JoinStage.WaitingForSteam : JoinStage.Connecting, MpClock.NowMs());
			}
		}
	}

	/// <summary>Connected links with a display name: joiners when hosting, the host when joined.</summary>
	public List<(string Name, PeerStats Stats)> LinkStats()
	{
		var list = new List<(string, PeerStats)>();
		MpTransport t = _transport;
		if (t == null || _stop) return list;
		HostSession host = _host;
		foreach (PeerId peer in t.Peers)
		{
			if (!t.TryGetStats(peer, out PeerStats stats)) continue;
			string name = host?.Players().FirstOrDefault(p => p.Peer == peer)?.Name ?? stats.RemoteName;
			list.Add((name, stats));
		}
		return list;
	}

	public string DescribeStatus()
	{
		StringBuilder sb = new StringBuilder();
		sb.Append(_o.Host ? "Hosting" : (_o.Join ? "Joining" : "Transport only"));
		sb.Append(_o.Dedicated ? " (Steam dedicated server)" : (_o.UseSteamIp ? $" (Steam IP {_o.JoinIp})" : (_o.HostIp != null ? $" (local sandbox {_o.HostIp})" : (_o.JoinIp != null ? $" (local sandbox {_o.JoinIp})" : " (Steam)"))));
		if (_stop)
		{
			sb.Append(" - stopped: ").Append(_stopReason);
			return sb.ToString();
		}
		MpTransport? t = _transport;
		if (t == null)
		{
			sb.Append(_steamReady.IsSet ? " - starting" : " - waiting for Steam");
			return sb.ToString();
		}
		HostSession? host = _host;
		if (host != null)
		{
			IReadOnlyList<HostSession.PlayerRecord> players = host.Players();
			sb.Append($"\nPlayers: {players.Count}");
			foreach (HostSession.PlayerRecord r in players)
			{
				sb.Append('\n').Append(r.Name).Append(": ");
				if (!r.Peer.HasValue)
				{
					sb.Append("offline");
					continue;
				}
				sb.Append(host.StatusOf(r.Peer.Value)?.ToString() ?? "gone");
				if (t.TryGetStats(r.Peer.Value, out PeerStats st) && st.LastRttMs >= 0.0)
				{
					sb.Append($" {st.LastRttMs:0} ms");
				}
			}
			return sb.ToString();
		}
		ClientSession? client = _client;
		if (client != null)
		{
			sb.Append("\nStage: ").Append(ClientWorld.Progress.Stage).Append(" / ").Append(client.CurrentPhase);
			sb.Append($"\nPlayers: {ClientSession.DescribePlayers(client.Players)}");
			if (client.Status.Version != 0)
			{
				sb.Append($"\nPCU {client.Status.Pcu}/{client.Status.MaxPcu}");
			}
		}
		foreach (PeerId peer in t.Peers)
		{
			if (t.TryGetStats(peer, out PeerStats st))
			{
				sb.Append($"\nLink {st.RemoteName}: {(st.Ready ? "ready" : "handshake")}");
				if (st.LastRttMs >= 0.0)
				{
					sb.Append($", {st.LastRttMs:0} ms, lost {st.StateLost}");
				}
			}
		}
		return sb.ToString();
	}

	public void SignalSteamReady(string source)
	{
		if (!_steamReady.IsSet)
		{
			_log.Info("net: Steam initialised (" + source + ")");
			_steamReady.Set();
		}
	}

	public void Stop(string reason, int timeoutMs = 3000)
	{
		if (!_stop)
		{
			_stopReason = reason;
			_stop = true;
			_steamReady.Set();
			if (Thread.CurrentThread != _thread && _thread.IsAlive && !_thread.Join(timeoutMs) && timeoutMs > 0)
			{
				_log.Warn($"net: thread did not stop within {timeoutMs} ms");
			}
		}
	}

	private void Run()
	{
		bool timerRaised = false;
		try
		{
			if (OperatingSystem.IsWindows())
			{
				timerRaised = TimeBeginPeriod(1u) == 0;
				_log.Info("net: timer resolution 1 ms " + (timerRaised ? "requested" : "refused") + " for the net loop");
			}
		}
		catch (Exception ex)
		{
			_log.Warn("net: timeBeginPeriod failed (" + ex.GetType().Name + "); the net loop polls at the system timer rate");
		}
		try
		{
			RunCore();
		}
		catch (Exception ex)
		{
			Failure = ex.Message;
			_log.Error($"net: thread crashed, networking disabled (game unaffected): {ex}");
		}
		finally
		{
			if (_rejoinHandler != null) ClientWorld.RejoinRequested -= _rejoinHandler;
			_client?.Detach();
			try
			{
				if (timerRaised)
				{
					TimeEndPeriod(1u);
				}
			}
			catch (Exception)
			{
			}
		}
	}

	[DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
	private static extern uint TimeBeginPeriod(uint ms);

	[DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
	private static extern uint TimeEndPeriod(uint ms);

	private bool WaitForSteam()
	{
		long start = MpClock.NowMs();
		bool warned = false;
		while (!_steamReady.Wait(1000))
		{
			if (!warned && MpClock.NowMs() - start > 120000)
			{
				warned = true;
				_log.Warn("net: still waiting for Steam after 2 min (is Steam running and the game launched through Steam?)");
			}
		}
		return !_stop;
	}

	private (ulong Id, string Name) Identity()
	{
		try
		{
			if (_steamReady.IsSet)
			{
				return (SteamUser.GetSteamID().m_SteamID, SteamFriends.GetPersonaName());
			}
		}
		catch (Exception)
		{
		}
		return (0uL, Environment.UserName);
	}

	private void RunCore()
	{
		// Before Steam: a dedicated server whose network fails still runs its world with the configured rules.
		if (_o.Host) ResourceMultipliers.Current = _o.UsageMultipliers;
		SteamLink? steamLink = null;
		using var dedicatedSteam = _o.Dedicated ? new SteamDedicatedSession() : null;
		TcpLink? tcpLink = null;
		ulong localId;
		string name;
		IPacketLink link;
		if (_o.NeedsSteam)
		{
			if (!_o.Dedicated && !WaitForSteam())
			{
				_log.Info("net: stopped before Steam was ready (" + _stopReason + ")");
				return;
			}
			if (_o.Dedicated)
			{
				dedicatedSteam.Initialize(_o, _gameVersion, () => _stop);
				localId = dedicatedSteam.SteamId;
				name = _o.SessionName;
			}
			else
			{
				try
				{
					localId = SteamUser.GetSteamID().m_SteamID;
					name = SteamFriends.GetPersonaName();
				}
				catch (InvalidOperationException ex)
				{
					_log.Error("net: Steam API not available (" + ex.Message + "); networking disabled");
					return;
				}
			}
			_log.Info($"net: Steam user '{name}' SteamID64={localId}");
			if (_o.HostSteam && !_o.Dedicated)
			{
				_log.Info($"HOST READY: join through Multiplayer using Steam identity {localId} or the advertised listing.");
			}
			if (!_o.Dedicated) SteamNetworkingUtils.InitRelayNetworkAccess();
			_log.Info("net: relay network init requested; " + SteamLink.RelayStatus(_o.Dedicated));
			steamLink = new SteamLink(_log, _o.VirtualPort, _o.SendRateMinBytes, _o.SendRateMaxBytes,
				// Another game instance on the host's own Steam account (Steam authenticates the identity) is never a stranger.
				id => (id != 0 && id == _localIdentity) || ((_o.Allow.Count == 0 || _o.Allow.Contains(id)) && (!_o.FriendsOnly || SteamFriends.GetFriendRelationship(new CSteamID(id)) == EFriendRelationship.k_EFriendRelationshipFriend)),
				_o.SteamListenIp, _o.Dedicated);
			steamLink.Start(_o.Host);
			link = steamLink;
			SteamJoinStart(localId, name);
			if (_o.Host && _o.Advertise) _directory = new DirectoryPublisher(new DirectoryClient(_o.Dedicated ? _o.ServersDirectory : _o.SessionsDirectory), message => _log.WarnOnce("directory-publish", message));
		}
		else
		{
			(localId, name) = Identity();
			if (_o.Join)
			{
				name += " (tcp)";
			}
			tcpLink = new TcpLink("tcp", _log);
			if (_o.HostIp != null)
			{
				tcpLink.Listen(_o.HostIp);
				_log.Info("HOST READY (local sandbox): join through Multiplayer using Join local test.");
			}
			link = tcpLink;
		}
		if (_o.PlayerName != null)
		{
			name = _o.PlayerName;
			_log.Info("net: player name '" + name + "'");
		}
		_localIdentity = localId;
		MpTransport? transport = null;
		try
		{
			transport = new MpTransport(link, new TransportOptions
			{
				LocalId = localId,
				Role = !_o.Host ? (byte)1 : (byte)0,
				LocalName = name,
				PluginVersion = _pluginVersion,
				GameVersion = _gameVersion
			}, _log);
			HostSession? host = null;
			ClientSession? client = null;
			if (_o.Sync)
			{
				if (_o.Host && HostWorld.Current != null)
				{
					host = new HostSession(transport, _log, HostWorld.Current, HostWorld.Current.Keys, new HostSyncOptions
					{
						PluginVersion = _pluginVersion,
						GameVersion = _gameVersion,
						StateBudgetBytesPerSec = _o.StateBudgetBytesPerSec,
						Caps = new RateCapPolicy(_o.RateCaps),
						ResolveType = EngineCodec.ResolveType,
						IsClientInput = EngineCodec.IsClientInput,
						CheckSignal = CommandLayouts.Check,
						Settings = new HostSettings(_o.Identities, _o.Grids, _o.Research, _o.WaterSync),
						HostName = name,
						LookTotals = _o.Predict,
						MaxPlayers = _o.MaxPlayers,
						HostCountsAsPlayer = !_o.Dedicated,
						Access = LoadAccess(AccessPath(_o.Dedicated)),
						Password = _o.Password ?? "",
						SessionName = _o.SessionName,
						Motd = _o.Dedicated ? Y51_SE2Online.Dedicated.DedicatedRuntime.Config?.Motd ?? "" : "",
						MotdTitle = _o.Dedicated ? Y51_SE2Online.Dedicated.DedicatedRuntime.Config?.MotdTitle ?? "" : "",
						Dedicated = _o.Dedicated,
						HostSteamId = _o.Dedicated ? 0 : localId,
						HostIdentity = PlayerIdentities.HotfixIdentity,
						FactionTagOf = HostWorld.Current.FactionTagOf,
						SafeZones = () => HostWorld.Current?.SafeZones,
						Pvp = _o.Pvp,
						PvpChanged = TurretRelations.SetPvp,
						OfflineRaidProtection = _o.OfflineRaidProtection,
						PcuLimit = _o.PcuLimit,
						StreamingRadiusMeters = _o.StreamingRadiusMeters,
						VoiceRadiusMeters = _o.VoiceRadiusMeters,
						SunRotation = _o.SunRotation,
						SunPeriodMinutes = _o.SunPeriodMinutes,
						OfflineRaidChanged = on => PvpRules.OfflineRaid = on
					});
					TurretRelations.SetPvp(host.Pvp);
					PvpRules.OfflineRaid = host.OfflineRaidProtection;
					_log.Info("net: joiners' turning " + (_o.Predict ? "applied from their running look totals (exact, matches their look prediction)" : "applied from the newest ControlData per tick (-y51online:predict:off)"));
					HostWorld.Current.Bind(host);
				}
				else if (_o.Join && ClientWorld.Current != null)
				{
					_playerName = name;
					client = NewClientSession(transport);
					_log.Info("net: remote motion smoothing " + (_o.InterpMs > 0 ? $"on, {_o.InterpMs} ms playout delay" : "off (-y51online:interp:0)"));
					_log.Info("net: look prediction " + (_o.Predict ? "on: the own character turns locally the same frame, the host's orientation only corrects it" : "off (-y51online:predict:off): the own character turns when the host's state comes back"));
					_log.Info(_o.Rejoin ? "net: rejoin without restarting: on (lost connection: back to the main menu and join again by itself; after leaving: F3 at the main menu or 'Join Game' in Steam)" : "net: rejoin without restarting: off (-y51online:rejoin:off): restart the game to join again");
					ClientWorld.Current.Bind(client);
					_rejoinHandler = why => _rejoinWhy = why;
					ClientWorld.RejoinRequested += _rejoinHandler;
				}
			}
			if (!_o.Sync)
			{
				_log.Info("net: -y51online:nosync: transport only, no world sync");
			}
			if (host != null && _o.Nametags)
			{
				_names = new PlayerNamesPublisher(transport, _log, HostNametags.Roster, p => host.StatusOf(p) == HostSession.ClientStatus.Synced);
			}
			if (_o.Host)
			{
				HostNametags.HostName = name;
			}
			_client = client;
			_host = host;
			_transport = transport;
			Loop(steamLink, tcpLink, transport, host);
		}
		finally
		{
			try
			{
				_directory?.StopAsync().GetAwaiter().GetResult();
				_advertiser?.Clear(_stopReason);
				_friends?.Dispose();
			}
			catch (Exception)
			{
			}
			try
			{
				bool hadPeers = transport != null && transport.Peers.Count > 0;
				transport?.DisconnectAll(1004, _stopReason);
				transport?.Poll();
				if (hadPeers)
				{
					// Give the disconnect frames a moment to leave before the link is torn down.
					Thread.Sleep(500);
				}
			}
			catch (Exception ex)
			{
				_log.Warn("net: error while disconnecting: " + ex.Message);
			}
			link.Dispose();
			if (_o.Host) ResourceMultipliers.Current = ResourceMultipliers.Default;
			_log.Info("net: stopped (" + _stopReason + ")");
		}
	}

	private void UpdateDirectory(long now)
	{
		// Advertise can be switched while hosting; the publisher follows it on the net thread.
		if (_directory == null && _o.Host && _o.Advertise && _o.NeedsSteam && _transport != null)
			_directory = new DirectoryPublisher(new DirectoryClient(_o.Dedicated ? _o.ServersDirectory : _o.SessionsDirectory), message => _log.WarnOnce("directory-publish", message));
		else if (_directory != null && !_o.Advertise)
		{
			_ = _directory.StopAsync();
			_directory = null;
		}
		if (_directory == null || now < _nextDirectoryMs) return;
		_nextDirectoryMs = now + 1000;
		var world = HostWorld.Current;
		DirectoryListing listing = null;
		if (world?.WorldLoaded == true)
		{
			listing = new DirectoryListing
			{
				Kind = _o.Dedicated ? "dedicated" : "session", Name = _o.SessionName, WorldName = (world.WorldName ?? "World").Length > 100 ? world.WorldName.Substring(0, 100) : world.WorldName ?? "World",
				Description = _o.Description, SteamId = _localIdentity.ToString(System.Globalization.CultureInfo.InvariantCulture), Address = _o.PublicAddress,
				VirtualPort = _o.VirtualPort, Players = (_host?.Players().Count(p => p.Peer.HasValue) ?? 0) + (_o.Dedicated ? 0 : 1), MaxPlayers = _o.MaxPlayers,
				PluginVersion = _pluginVersion, GameVersion = _gameVersion, SyncVersion = SyncProtocol.Version, FriendsOnly = _o.FriendsOnly,
				Tags = _host != null ? ServerInfoUi.ListingTags(_host) : new[] { string.IsNullOrEmpty(_o.Password) ? null : "Password" }.Where(t => t != null).ToArray()
			};
		}
		_directory.Tick(listing, now);
	}

	private ClientSession NewClientSession(MpTransport transport)
	{
		var session = new ClientSession(transport, _log, new ClientSyncOptions
		{
			PluginVersion = _pluginVersion,
			GameVersion = _gameVersion,
			PlayerName = _playerName,
			Password = _o.JoinPassword ?? "",
			CheckSignal = CommandLayouts.Check,
			Progress = ClientWorld.Progress
		}, EngineCodec.ResolveType);
		session.Smoother.DelayMs = _o.InterpMs;
		session.Smoother.Adaptive = _o.AdaptiveInterp;
		return session;
	}

	private void Loop(SteamLink? steam, TcpLink? tcp, MpTransport transport, HostSession? host)
	{
		var connectedAt = new Dictionary<PeerId, long>();
		var detailedDone = new HashSet<PeerId>();
		transport.PeerConnected += p => connectedAt[p] = MpClock.NowMs();
		transport.PeerDisconnected += (p, info) =>
		{
			connectedAt.Remove(p);
			detailedDone.Remove(p);
			_lastLoss = DescribeLoss(info);
		};
		long lastStatusMs = MpClock.NowMs();
		long lastRelayCheckMs = 0L;
		string lastRelayStatus = "";
		uint conn = 0u;
		if (steam == null)
		{
			_joinTarget = 0uL;
		}
		long nextAttemptMs = MpClock.NowMs() + (steam != null ? 2000 : 500);
		int attempts = 0;
		int maxAttempts = steam != null ? 20 : 400;
		int retryMs = steam != null ? 15000 : 3000;
		long waitStartMs = MpClock.NowMs();
		var tickWatch = new Stopwatch();
		long slowestTickMs = 0L;
		bool wasConnected = false;
		bool lossHandled = false;
		var iteration = new Stopwatch();
		while (!_stop)
		{
			iteration.Restart();
			long now = MpClock.NowMs();
			if (steam != null && now - lastRelayCheckMs >= 2000)
			{
				lastRelayCheckMs = now;
				string relay = SteamLink.RelayStatus(_o.Dedicated);
				if (relay != lastRelayStatus)
				{
					_log.Info("net: " + relay);
					lastRelayStatus = relay;
				}
			}
			ClientSession? client = _client;
			bool hasPeers = transport.Peers.Count > 0;
			bool linkOpen = conn != 0 && (steam?.HasConnection(conn) ?? tcp.HasConnection(conn));
			string? rejoinWhy = _rejoinWhy;
			if (rejoinWhy != null && client != null)
			{
				if ((hasPeers || linkOpen) && now - _rejoinSinceMs < RejoinCloseTimeoutMs)
				{
					if (!_rejoinClosing)
					{
						_rejoinClosing = true;
						_rejoinSinceMs = now;
						foreach (PeerId peer in transport.Peers)
						{
							transport.Disconnect(peer, 1000, "joining again");
						}
						if (!hasPeers && linkOpen)
						{
							if (steam != null)
							{
								steam.Close(conn, 1000, "joining again", linger: false);
							}
							else
							{
								tcp.Close(conn, 1000, "joining again", linger: false);
							}
						}
						_log.Info("net: fresh join requested (" + rejoinWhy + "); closing the previous connection first");
					}
				}
				else
				{
					client.Detach();
					client = _client = NewClientSession(transport);
					ClientWorld.Current?.Bind(client);
					wasConnected = lossHandled = false;
					conn = 0u;
					attempts = 0;
					waitStartMs = now;
					nextAttemptMs = now + RejoinFirstAttemptMs;
					if (steam != null && !_joinTargetLocked)
					{
						SetJoinTarget(0uL, "", locked: false);
						_nextFriendScanMs = 0L;
					}
					_rejoinWhy = null;
					_rejoinClosing = false;
					_log.Info($"net: FRESH JOIN #{ClientWorld.Progress.JoinNumber} ({rejoinWhy}): new world sync session; "
						+ (steam == null
							? $"connecting to TCP host {_o.JoinIp} in {RejoinFirstAttemptMs / 1000} s"
							: _joinTarget != 0 ? $"connecting to host {_joinTarget} in {RejoinFirstAttemptMs / 1000} s" : "looking for a hosting friend"));
				}
				hasPeers = transport.Peers.Count > 0;
				linkOpen = conn != 0 && (steam?.HasConnection(conn) ?? tcp.HasConnection(conn));
			}
			if (hasPeers)
			{
				wasConnected = true;
			}
			if (_o.Join && wasConnected && !hasPeers && !linkOpen && client != null && !lossHandled && _rejoinWhy == null)
			{
				lossHandled = true;
				nextAttemptMs = long.MaxValue;
				if (client.LeftOnPurpose)
				{
					_log.Info("net: left the host (quit to the main menu); " + (_o.Rejoin ? "F3 at the main menu (or 'Join Game' in Steam) joins again" : "restart the game to join again"));
				}
				else if (client.CurrentPhase == ClientSession.Phase.Rejected)
				{
					_log.Info("net: the host refused this join and closed the connection; " + (_o.Rejoin ? "F3 at the main menu tries again" : "restart the game to try again"));
				}
				else if (_o.Rejoin)
				{
					_log.Warn("net: lost the host after joining (" + _lastLoss + "); the game joins again with a fresh world sync (back to the main menu first if a world is loaded)");
				}
				else
				{
					_log.Warn("net: lost the host after joining (" + _lastLoss + "); not reconnecting automatically (-y51online:rejoin:off). Quit to the main menu and restart the game to rejoin.");
				}
				if (!client.LeftOnPurpose)
				{
					ClientWorld.ConnectionLost(client, _lastLoss);
				}
			}
			if (steam != null && !_o.UseSteamIp)
			{
				ulong previousTarget = _joinTarget;
				SteamJoinTick(now, hasPeers || linkOpen || wasConnected, host);
				if (_joinTarget != previousTarget && !hasPeers && !linkOpen)
				{
					attempts = 0;
					nextAttemptMs = now;
				}
			}
			_names?.Tick();
			if (_o.Join && !hasPeers && !linkOpen && now >= nextAttemptMs && (client == null || client.CurrentPhase != ClientSession.Phase.Rejected) && (steam == null || _joinTarget != 0 || _o.UseSteamIp))
			{
				bool networkReady = steam == null || (_o.UseSteamIp ? SteamLink.NetworkConfigReady() : SteamLink.RelayReady());
				if (networkReady || now - waitStartMs > 20000)
				{
					if (attempts >= maxAttempts)
					{
						if (attempts == maxAttempts)
						{
							_log.Error($"net: giving up after {maxAttempts} connection attempts; " + (_o.Rejoin ? "F3 at the main menu tries again" : "restart the game to retry"));
							ClientWorld.Progress.SetAttempt(maxAttempts, maxAttempts);
							ClientWorld.Progress.Set(JoinStage.GaveUp, now);
						}
						attempts++;
						nextAttemptMs = long.MaxValue;
					}
					else
					{
						attempts++;
						ClientWorld.Progress.SetAttempt(attempts, maxAttempts);
						JoinStage stage = ClientWorld.Progress.Stage;
						if (stage <= JoinStage.LookingForHost || stage == JoinStage.GaveUp)
						{
							ClientWorld.Progress.Set(JoinStage.Connecting, now, networkReady ? null : _o.UseSteamIp ? "Steam authentication configuration loading" : "relay starting");
						}
						if (steam != null)
						{
							ulong hostId = JoinTargetRule.SteamConnectId(_joinTarget, _o);
							_log.Info($"net: connect attempt {attempts} to host {hostId}{(networkReady ? "" : " (relay network not ready yet)")}");
							// Steam refuses P2P to your own Steam ID, so a second game instance on this account joins
							// the first one through its Steam IP listener on this PC instead.
							conn = _o.UseSteamIp ? steam.Connect(_o.JoinIp)
								: hostId == _localIdentity && hostId != 0 ? steam.Connect(new IPEndPoint(IPAddress.Loopback, SyncEngine.MpMenu.Settings?.ListenPort ?? MpOptions.DefaultIpPort))
								: steam.Connect(hostId);
						}
						else
						{
							if (attempts == 1 || attempts % 10 == 0)
							{
								_log.Info($"net: connect attempt {attempts} to TCP host {_o.JoinIp}");
							}
							conn = tcp.Connect(_o.JoinIp);
						}
						nextAttemptMs = now + retryMs;
					}
				}
			}
			long beforePoll = iteration.ElapsedMilliseconds;
			tickWatch.Restart();
			transport.Poll();
			long transportMs = tickWatch.ElapsedMilliseconds;
			if (_o.Dedicated) GameServer.RunCallbacks();
			long callbacksMs = tickWatch.ElapsedMilliseconds - transportMs;
			UpdateDirectory(now);
			long pollMs = tickWatch.ElapsedMilliseconds;
			host?.Tick();
			_client?.Tick();
			ClientWorld.Current?.NetTick(now);
			long tickMs = tickWatch.ElapsedMilliseconds;
			if (tickMs > 250)
			{
				slowestTickMs = Math.Max(slowestTickMs, tickMs);
				_log.Warn($"net: loop iteration took {tickMs} ms (transport.Poll {transportMs} ms incl. the link's Pump, Steam callbacks {callbacksMs} ms, directory {pollMs - transportMs - callbacksMs} ms; {transport.LastPollDetail}); timestamps taken before it would be stale");
			}
			foreach (var entry in connectedAt)
			{
				if (steam != null && !detailedDone.Contains(entry.Key) && now - entry.Value >= 3000)
				{
					detailedDone.Add(entry.Key);
					LogDetailed(steam, entry.Key);
				}
			}
			if (now - lastStatusMs >= LogFormat.StatusIntervalMs)
			{
				lastStatusMs = now;
				LogStatus(steam, tcp, transport, now);
				if (HostWorld.Current != null && _o.Host)
				{
					_log.Info($"net status: host world sync {HostWorld.Current.Stats()}; world ready={HostWorld.Current.WorldReady}");
					if (HostWorld.Current.SignalsForwarded + HostWorld.Current.SignalsDropped + HostWorld.Current.InputCommandsApplied + HostWorld.Current.InputRejected > 0)
					{
						_log.Info("net status: " + HostWorld.Current.SignalStats());
					}
				}
			}
			if (iteration.ElapsedMilliseconds > 150)
			{
				_log.Warn($"net: whole loop iteration took {iteration.ElapsedMilliseconds} ms (before the poll {beforePoll} ms, poll and ticks {tickMs} ms, status logging and the rest {iteration.ElapsedMilliseconds - beforePoll - tickMs} ms)");
			}
			Thread.Sleep(1);
		}
	}

	internal static string DescribeLoss(DisconnectInfo info)
	{
		string reason = string.IsNullOrWhiteSpace(info.Reason) ? $"code {info.Code}" : info.Reason.Trim();
		switch (info.Code)
		{
		case 1003:
			return "no answer from the host for 30 s";
		case 1004:
			if (info.ByPeer)
			{
				return "the host closed the game (" + reason + ")";
			}
			break;
		case 1000:
			if (info.ByPeer)
			{
				return "the host ended the session: " + reason;
			}
			break;
		}
		return info.ByPeer ? "closed by the host: " + reason : reason;
	}

	private void LogStatus(SteamLink? steam, TcpLink? tcp, MpTransport transport, long now)
	{
		bool any = false;
		foreach (var (conn, remote, connected, quickStatus) in steam != null ? Steam(steam) : Tcp(tcp))
		{
			any = true;
			string app = "";
			if (transport.TryGetStats(new PeerId(remote), out PeerStats stats))
			{
				ChannelStats[] channels = stats.Channels;
				app = $" | app rtt={stats.LastRttMs:0} ms (avg {stats.AvgRttMs:0.0}, max {stats.MaxRttMs:0}, pongs {stats.PongsReceived}/{stats.PingsSent}) ctl {channels[0].FramesSent}/{channels[0].FramesReceived} fr, bulk {channels[1].BytesSent / 1024}/{channels[1].BytesReceived / 1024} KiB tx/rx, state {channels[2].FramesSent}/{channels[2].FramesReceived} fr {channels[2].BytesSent / 1024}/{channels[2].BytesReceived / 1024} KiB, queued {MpLogExtensions.Bytes(stats.BulkQueuedBytes)}, reasm {stats.ReassemblyInProgress}, seqGaps {stats.SeqGaps} protoErr {stats.ProtocolErrors} busy {stats.SendBusy}";
			}
			_log.Info($"net status {remote} conn {conn} {(connected ? "connected" : "pending")}: {quickStatus}{app}");
		}
		if (steam != null && any && now - _lastDetailedMs >= LogFormat.DetailedStatusIntervalMs)
		{
			_lastDetailedMs = now;
			foreach (var c in steam.Connections())
			{
				ulong remote = c.Remote;
				if (c.Connected)
				{
					LogDetailed(steam, new PeerId(remote));
				}
			}
		}
		if (!any && !_idleLogged)
		{
			_log.Info(_o.Host ? "net status: no clients connected (waiting)" : "net status: not connected");
		}
		_idleLogged = !any;

		static IEnumerable<(uint, ulong, bool, string)> Steam(SteamLink l)
		{
			foreach (var (conn, remote, connected) in l.Connections())
			{
				yield return (conn, remote, connected, l.QuickStatus(conn));
			}
		}

		static IEnumerable<(uint, ulong, bool, string)> Tcp(TcpLink l)
		{
			foreach (var (conn, _, connected) in l.Connections())
			{
				yield return (conn, TcpLink.SyntheticId(conn), connected, l.QuickStatus(conn));
			}
		}
	}

	private void LogDetailed(SteamLink link, PeerId peer)
	{
		foreach (var (conn, remote, connected) in link.Connections())
		{
			if (remote == peer.Value && connected)
			{
				_log.Info($"net detailed status {remote}:\n{link.DetailedStatus(conn).TrimEnd()}");
			}
		}
	}

	private void SteamJoinStart(ulong me, string name)
	{
		try
		{
			if (_o.HostSteam && !_o.Dedicated)
			{
				_advertiser = new HostAdvertiser(_log, me, _o.VirtualPort, SyncProtocol.Version, _pluginVersion);
			}
			if (!_o.Join)
			{
				return;
			}
			if (_o.UseSteamIp) { ClientWorld.Progress.SetHost(_o.JoinIp.ToString()); return; }
			_friends = new FriendFinder(_log);
			_friends.Start();
			if (_o.JoinSteamId != 0L)
			{
				SetJoinTarget(_o.JoinSteamId, "command line (-y51online:join:<id>, also what Steam's 'Join Game' passes)", locked: true);
				return;
			}
			string launch = _friends.LaunchCommandLine();
			if (launch.Length > 0)
			{
				_log.Info("steam friends: Steam launch command line: '" + launch + "'");
			}
			if (ConnectString.TryParse(launch, out var hostSteamId, out _))
			{
				SetJoinTarget(hostSteamId, "Steam launch command line", locked: true);
			}
			else if (_o.JoinFriends)
			{
				_log.Info("steam friends: looking for a Steam friend who hosts SE2 multiplayer" + (_o.JoinFriendFilter != null ? " (name contains '" + _o.JoinFriendFilter + "')" : "") + "; a 'Join Game' click in Steam works too");
				ClientWorld.Progress.Set(JoinStage.LookingForHost, MpClock.NowMs(), "Looking for a friend who is hosting...");
			}
			else if (_o.JoinWait)
			{
				ClientWorld.Progress.Set(JoinStage.LookingForHost, MpClock.NowMs(), "Click 'Join Game' on a friend in Steam (Shift+Tab).");
			}
		}
		catch (Exception ex)
		{
			_log.Error($"steam friends: setting up rich presence / friend join failed ({ex.GetType().Name}: {ex.Message}); " + (_o.JoinSteamId != 0L ? "joining the given SteamID64 still works" : "use -y51online:join:<host SteamID64> instead"));
			_friends = null;
			if (_o.JoinSteamId != 0L)
			{
				SetJoinTarget(_o.JoinSteamId, "command line", locked: true);
			}
		}
	}

	private void SetJoinTarget(ulong id, string source, bool locked, string? label = null)
	{
		_joinTarget = id;
		_joinTargetLocked = locked;
		_targetSource = source;
		if (id == 0L)
		{
			return;
		}
		_log.Info($"net: join target = host {id} (from {source})");
		if (_o.Join)
		{
			ClientWorld.Progress.SetHost(label ?? FriendName(id) ?? $"host {id}");
			JoinStage stage = ClientWorld.Progress.Stage;
			if (stage <= JoinStage.LookingForHost)
			{
				ClientWorld.Progress.Set(JoinStage.Connecting, MpClock.NowMs());
			}
		}
	}

	private string? FriendName(ulong id)
	{
		try
		{
			if (!_steamReady.IsSet)
			{
				return null;
			}
			string name = SteamFriends.GetFriendPersonaName(new CSteamID(id));
			return string.IsNullOrWhiteSpace(name) || name == "[unknown]" ? null : name;
		}
		catch (Exception)
		{
			return null;
		}
	}

	private void SteamJoinTick(long now, bool joined, HostSession? host)
	{
		try
		{
			if (_advertiser != null && now >= _nextPresenceMs)
			{
				_nextPresenceMs = now + PresenceUpdateMs;
				HostWorld world = HostWorld.Current;
				bool worldLoaded = world?.WorldLoaded ?? true;
				_advertiser.Update(worldLoaded, world?.WorldName ?? "?");
			}
			FriendFinder friends = _friends;
			if (friends == null)
			{
				return;
			}
			ulong from;
			string connect;
			while (friends.TryTakeJoinRequest(out from, out connect))
			{
				if (!ConnectString.TryParse(connect, out var hostSteamId, out var virtualPort))
				{
					_log.Warn($"steam friends: 'Join Game' from {from} carries connect string '{connect}', which has no -y51online:join:<SteamID64>; ignored " + "(the friend does not host with this plugin)");
					continue;
				}
				if (virtualPort.HasValue && virtualPort.Value != _o.VirtualPort)
				{
					_log.Warn($"steam friends: host {hostSteamId} listens on virtual port {virtualPort.Value}, this game uses {_o.VirtualPort}; restart with -y51online:vport:{virtualPort.Value} if the connection fails");
				}
				if (joined)
				{
					ClientWorld client = ClientWorld.Current;
					if (client != null && client.IdleAfterJoin)
					{
						SetJoinTarget(hostSteamId, "Steam 'Join Game' / invite after the last join ended (connect '" + connect + "')", locked: true);
						client.AskRejoin($"Steam 'Join Game' for host {hostSteamId}");
						continue;
					}
				}
				if (joined)
				{
					_log.Warn($"steam friends: 'Join Game' for host {hostSteamId} ignored: this game is already connected (or connecting) to host {_joinTarget} " + (_o.Rejoin ? "(leave to the main menu first, then click 'Join Game' again or press F3)" : "(quit the game and click 'Join Game' again to switch)"));
				}
				else
				{
					SetJoinTarget(hostSteamId, "Steam 'Join Game' / invite (connect '" + connect + "')", locked: true);
				}
			}
			if (joined || _joinTargetLocked)
			{
				return;
			}
			if (!_o.JoinFriends)
			{
				if (_o.JoinWait && !_waitLogged)
				{
					_waitLogged = true;
					_log.Info("steam friends: -y51online:join:wait: waiting for you to click 'Join Game' on a hosting friend in Steam (friends list or overlay, Shift+Tab)");
				}
				return;
			}
			// A presence change rescans early, but at most once a second.
			bool presenceChanged = friends.TakePresenceChanged();
			if (now < _nextFriendScanMs && (!presenceChanged || now < _nextFriendScanMs - FriendScanMs + 1000))
			{
				return;
			}
			_nextFriendScanMs = now + FriendScanMs;
			List<FriendHost> found = friends.Scan(now, out var friendsTotal, out var inGame);
			FriendPicker.Choice choice = FriendPicker.Pick(found, _o.JoinFriendFilter, SyncProtocol.Version);
			string line = choice.Hosting.Count == 0
				? $"no friend is hosting SE2 multiplayer right now ({friendsTotal} friends, {inGame} in Space Engineers 2{InGameNames(found)}); retrying every {FriendScanMs / 1000} s"
				: $"found {choice.Hosting.Count} friend(s) hosting: {string.Join("; ", choice.Hosting.Select(h => h.Describe()))}";
			if (line != _lastFriendsLine || (choice.Friend == null && now - _lastNoHostLogMs >= 60000))
			{
				_lastFriendsLine = line;
				_lastNoHostLogMs = now;
				_log.Info("steam friends: " + line);
			}
			bool showNoHost = choice.Friend == null;
			if (showNoHost)
			{
				showNoHost = ClientWorld.Progress.Stage is JoinStage.WaitingForSteam or JoinStage.LookingForHost or JoinStage.Connecting;
			}
			if (showNoHost)
			{
				string detail = ScreenTexts.NoFriendHosting(_o.JoinFriendFilter, FriendScanMs / 1000, choice.Hosting.Count == 0 ? null : choice.Reason);
				ClientWorld.Progress.Set(JoinStage.LookingForHost, now, detail);
			}
			if (_joinTarget != 0L)
			{
				if (choice.Hosting.Any(h => h.SteamId == _joinTarget))
				{
					return;
				}
				_log.Warn($"steam friends: host {_joinTarget} no longer advertises SE2 multiplayer; looking again");
				SetJoinTarget(0uL, "", locked: false);
				ClientWorld.Progress.Set(JoinStage.LookingForHost, now, "That host stopped; looking for another...");
			}
			if (choice.Friend != null)
			{
				_log.Info("steam friends: joining " + choice.Friend.Describe() + ": " + choice.Reason);
				if (choice.Friend.Presence.VirtualPort != _o.VirtualPort)
				{
					_log.Warn($"steam friends: that host uses virtual port {choice.Friend.Presence.VirtualPort}, this game {_o.VirtualPort}; restart with -y51online:vport:{choice.Friend.Presence.VirtualPort}");
				}
				SetJoinTarget(choice.Friend.SteamId, "friends list (" + choice.Reason + ")", locked: false, choice.Friend.Name);
			}
			else if (choice.Hosting.Count > 0)
			{
				_log.Info($"steam friends: {choice.Reason}; retrying every {FriendScanMs / 1000} s");
			}
		}
		catch (Exception ex)
		{
			_log.Error($"steam friends: friend join failed and is switched off ({ex.GetType().Name}: {ex.Message}); use -y51online:join:<host SteamID64>");
			_friends = null;
		}

		static string InGameNames(List<FriendHost> list)
		{
			if (list.Count != 0)
			{
				return ": " + string.Join(", ", list.Take(8)
					.Select(x => x.RawPresence != null ? x.Name + " [unreadable presence '" + x.RawPresence + "']" : x.Name));
			}
			return "";
		}
	}
}
