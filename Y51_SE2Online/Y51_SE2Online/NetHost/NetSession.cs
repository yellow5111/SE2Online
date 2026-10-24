using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Y51_SE2Online.Core;
using Y51_SE2Online.KeenNetcode;
using Y51_SE2Online.Net;
using Y51_SE2Online.Steam;
using Y51_SE2Online.Sync;
using Y51_SE2Online.SyncEngine;
using Steamworks;

namespace Y51_SE2Online.NetHost;

internal sealed partial class NetSession
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

	private HostAdvertiser? _advertiser;
	private DirectoryPublisher _directory;
	private ulong _localIdentity;
	private long _nextDirectoryMs;
	private string _listedAddress = "";

	private FriendFinder? _friends;

	private ulong _joinTarget;

	private long _nextFriendScanMs;

	private long _nextPresenceMs;

	private string _lastFriendsLine = "";

	private bool _joinTargetLocked;

	private PlayerNamesPublisher? _names;

	private readonly NetProfile _profile = new();

	private ClientSession? _client;

	private volatile MpTransport? _transport;

	private volatile HostSession? _host;

	private volatile string? _rejoinWhy;

	private string _lastLoss = "connection closed";

	private string _playerName = "";

	private Action<string> _rejoinHandler;

	private const int FriendScanMs = 10000;

	private const int PresenceUpdateMs = 2000;

	private long _lastNoHostLogMs;

	private string _targetSource = "";

	private bool _waitLogged;

	public static NetSession? Current => _current;

	public MpOptions Options => _o;

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
		_o.PublicAddress = after.PublishedAddress;
		_o.Country = Countries.Normalize(after.Country) ?? "";
		_o.Password = after.Password ?? "";
		_o.Pvp = after.Pvp;
		_o.OfflineRaidProtection = after.OfflineRaidProtection;
		_o.PcuLimit = after.PcuLimit;
		_o.SunRotation = after.SunRotation;
		_o.SunPeriodMinutes = after.SunPeriodMinutes;
		_o.StreamingRadiusMeters = after.StreamingRadiusMeters;
		_o.VoiceRadiusMeters = after.VoiceRadiusMeters;
		_o.Pings = after.PingSettingsOf();
		_o.MarkerDefaults = after.MarkerDefaults ?? new();
		_o.WaterSync = after.WaterSync;
		_o.UsageMultipliers = after.UsageMultipliers;
		ResourceMultipliers.Current = after.UsageMultipliers;
		_o.InventorySizes = after.InventorySizes;
		InventoryMultipliers.Current = after.InventorySizes;
		_host?.ApplySettings(before, after, changes);
		_log.Info("host: settings applied without restarting hosting" + (changes.Count > 0 ? ": " + string.Join(", ", changes) : ""));
	}

	internal static string AccessPath(bool dedicated) => dedicated && Y51_SE2Online.Dedicated.DedicatedRuntime.Config != null
		? System.IO.Path.Combine(Y51_SE2Online.Dedicated.DedicatedRuntime.Config.Directory, "access.json")
		: WorldAccessPath(HostWorld.Current?.WorldKey, HostWorld.Current?.WorldName);

	// A listen host keeps admins, bans, whitelist and runtime choices per world; one shared file made every
	// player promoted in any hosted world an admin in all of them. The old shared file seeds bans and the whitelist.
	private AccessList LoadAccess(string path)
	{
		_log.Info("host: admins, bans and whitelist from " + path);
		return AccessList.Load(path);
	}

	/// <summary>
	/// The permission files: dedicated, Permissions/ next to server.json; co-op, the host's own group files shared by all
	/// worlds (plugin data Permissions/) with one players file per world. Missing files are written from the access list
	/// and RconRoles (0.17's rights); unreadable ones fall back to 0.17's rights in memory, and the log says why.
	/// </summary>
	private (PermissionSet Set, string? Folder, string? PlayersFile) LoadPermissions(AccessList access)
	{
		string folder, players;
		System.Collections.Generic.IReadOnlyDictionary<string, System.Collections.Generic.List<string>>? roles = null;
		if (_o.Dedicated && Y51_SE2Online.Dedicated.DedicatedRuntime.Config is { } config)
		{
			folder = System.IO.Path.Combine(config.Directory, "Permissions");
			players = System.IO.Path.Combine(folder, "players.json");
			roles = config.RconRoles;
			AuditLog.Path = System.IO.Path.Combine(config.Directory, "audit.jsonl");
		}
		else
		{
			folder = PluginPaths.Data("Permissions");
			players = WorldFile("Y51_SE2Online.permissions-players.", HostWorld.Current?.WorldKey) ?? System.IO.Path.Combine(folder, "players.json");
			AuditLog.Path = PluginPaths.Data("Y51_SE2Online.audit.jsonl");
		}
		try
		{
			foreach (string note in PermissionFiles.Prepare(folder, players, access, roles)) _log.Info("permissions: " + note);
			var set = PermissionSet.Load(folder, players, out var warnings);
			foreach (string warning in warnings) _log.Warn("permissions: " + warning);
			foreach (string note in set.ReconcileAdmins(access.Copy(0).Admins.Select(a => (a.SteamId, a.Name)))) _log.Info("permissions: " + note);
			access.MirrorAdmins(set.AdminMembers());
			_log.Info($"permissions: {set.Groups.Count} groups ({string.Join(", ", set.Groups.OrderBy(g => g.Rank).Select(g => g.Name + " " + g.Rank))}), {set.Assignments().Length} assigned players, from {folder}");
			return (set, folder, players);
		}
		catch (Exception ex) when (ex is System.IO.InvalidDataException or System.IO.IOException or UnauthorizedAccessException)
		{
			_log.Error("permissions: could not read the permission files (" + ex.Message + "); running with 0.17's rights from the access list until they are fixed (perms reload)");
			return (PermissionSet.FromAccess(access), System.IO.Directory.Exists(folder) ? folder : null, players);
		}
	}

	private static string? WorldFile(string prefix, string? worldKey)
	{
		if (string.IsNullOrWhiteSpace(worldKey) || worldKey == "?") return null;
		string safe = string.Concat(worldKey.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
		return PluginPaths.Data(prefix + (safe.Length > 80 ? safe[..80] : safe) + ".json");
	}

	internal static string WorldAccessPath(string? worldKey, string? worldName)
	{
		string legacy = PluginPaths.Data("Y51_SE2Online.access.json");
		if (string.IsNullOrWhiteSpace(worldKey) || worldKey == "?") return legacy;
		static string Keyed(string key)
		{
			string safe = string.Concat(key.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
			return PluginPaths.Data("Y51_SE2Online.access." + (safe.Length > 80 ? safe[..80] : safe) + ".json");
		}
		string path = Keyed(worldKey);
		try { AccessList.PrepareWorldFile(path, string.IsNullOrWhiteSpace(worldName) || worldName == "?" ? null : Keyed(worldName), legacy); }
		catch (Exception) { }
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

	private void RunCore() => RunOwn();

	private (HostSession? Host, ClientSession? Client) CreateSessions(MpTransport transport, string name, ulong localId)
	{
		HostSession? host = null;
		ClientSession? client = null;
		if (_o.Sync)
		{
			if (_o.Host && HostWorld.Current != null)
			{
				var access = LoadAccess(AccessPath(_o.Dedicated));
				var (permissions, permissionsFolder, playersFile) = LoadPermissions(access);
				host = new HostSession(transport, _log, HostWorld.Current, HostWorld.Current.Keys, new HostSyncOptions
				{
					PluginVersion = _pluginVersion,
					GameVersion = _gameVersion,
					StateBudgetBytesPerSec = _o.StateBudgetBytesPerSec,
					Caps = new RateCapPolicy(_o.RateCaps),
					ResolveType = EngineCodec.ResolveType,
					IsClientInput = EngineCodec.IsClientInput,
					CheckSignal = CommandLayouts.Check,
					Settings = new HostSettings(_o.Identities, _o.Grids, _o.Research, _o.WaterSync, _o.ClientMovement, true),
					HostName = name,
					LookTotals = _o.Predict,
					MaxPlayers = _o.MaxPlayers,
					HostCountsAsPlayer = !_o.Dedicated,
					Access = access,
					Permissions = permissions,
					PermissionsFolder = permissionsFolder,
					PlayersFile = playersFile,
					OwnerSteamIds = _o.Dedicated ? Y51_SE2Online.Dedicated.DedicatedRuntime.Config?.OwnerSteamIds : null,
					ModeOf = HostWorld.Current.ModeOf,
					CleanupStatus = HostWorld.Current.CleanupStatus,
					CleanupConfig = HostWorld.Current.CleanupConfigJson,
					RunWorldAdmin = HostWorld.Current.RunAdminNow,
					Password = _o.Password ?? "",
					SessionName = _o.SessionName,
					Motd = _o.Motd,
					MotdTitle = _o.MotdTitle,
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
					Pings = _o.Pings,
					MarkerDefaults = _o.MarkerDefaults,
					SunRotation = _o.SunRotation,
					SunPeriodMinutes = _o.SunPeriodMinutes,
					OfflineRaidChanged = on => PvpRules.OfflineRaid = on
				});
				TurretRelations.SetPvp(host.Pvp);
				PvpRules.OfflineRaid = host.OfflineRaidProtection;
				HostWorld.Current.Bind(host);
			}
			else if (_o.Join && ClientWorld.Current != null)
			{
				_playerName = name;
				client = NewClientSession(transport);
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
			_names = new PlayerNamesPublisher(transport, _log, HostNametags.Roster, p => host.StatusOf(p) == HostSession.ClientStatus.Synced, host.TakesPatches);
		}
		if (_o.Host)
		{
			HostNametags.HostName = name;
		}
		return (host, client);
	}

	private void UpdateDirectory(long now)
	{
		// Advertise can be switched while hosting; the publisher follows it on the net thread.
		if (_directory == null && _o.Host && _o.Advertise && _o.NeedsSteam && _transport != null)
			_directory = NewDirectory();
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
				Description = _o.Description, SteamId = _localIdentity.ToString(System.Globalization.CultureInfo.InvariantCulture), Address = ListingAddress(out int autoPort), AutoAddressPort = autoPort,
				VirtualPort = _o.VirtualPort, Players = (_host?.Players().Count(p => p.Peer.HasValue) ?? 0) + (_o.Dedicated ? 0 : 1), MaxPlayers = _o.MaxPlayers,
				PluginVersion = _pluginVersion, GameVersion = _gameVersion, SyncVersion = SyncProtocol.Version, FriendsOnly = _o.FriendsOnly, Country = _o.Country,
				PingLocation = SteamPing.Local(_o.Dedicated), Mods = world.ListingMods(),
				Tags = _host != null ? ServerInfoUi.ListingTags(_host) : new[] { string.IsNullOrEmpty(_o.Password) ? null : "Password" }.Where(t => t != null).ToArray()
			};
		}
		_directory.Tick(listing, now);
		if (_o.Dedicated && _directory.ObservedAddress is { Length: > 0 } seen && seen != _observedLogged)
		{
			_observedLogged = seen;
			_log.Info("server browser: public address " + seen + " (seen by the directory)");
		}
		if (listing != null && _o.Dedicated && listing.Address != _listedAddress)
		{
			_listedAddress = listing.Address;
			_log.Info(_listedAddress.Length > 0 ? "server browser: players connect to " + _listedAddress + (string.IsNullOrWhiteSpace(_o.PublicAddress) ? " (found automatically)" : " (Network.PublicAddress)")
				: "server browser: the directory lists the IP it sees this server announce from");
		}
	}

	private string _observedLogged = "";

	private DirectoryPublisher NewDirectory()
	{
		string service = _o.Dedicated ? _o.ServersDirectory : _o.SessionsDirectory;
		// The directory lists a dedicated server at the IP it sees, so that must be the family its game port listens on.
		var client = _o.Dedicated && _o.SteamListenIp?.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6 ? DirectoryClient.Ipv4(service) : new DirectoryClient(service);
		return new DirectoryPublisher(client, message => _log.WarnOnce("directory-publish", message));
	}

	/// <summary>A typed Network.PublicAddress as is; for a dedicated server without one, its best guess plus the port the directory lists with the IP it sees.</summary>
	private string ListingAddress(out int autoPort)
	{
		autoPort = 0;
		if (!_o.Dedicated || !string.IsNullOrWhiteSpace(_o.PublicAddress)) return _o.PublicAddress;
		autoPort = _o.SteamListenIp?.Port ?? MpOptions.DefaultIpPort;
		return ListedAddress.Choose("", _directory?.ObservedAddress, Y51_SE2Online.Steam.SteamDedicatedSession.PublicIp(), ListedAddress.Local(), autoPort);
	}

	private Func<PeerId, string> PeerNames()
	{
		var names = new Dictionary<PeerId, string>();
		foreach (var p in _host?.Players() ?? Array.Empty<HostSession.PlayerRecord>())
		{
			if (p.Peer is PeerId peer) names[peer] = p.Name;
		}
		return peer => names.TryGetValue(peer, out string name) ? name : peer.ToString();
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

	internal static string DescribeLoss(DisconnectInfo info)
	{
		string reason = string.IsNullOrWhiteSpace(info.Reason) ? $"code {info.Code}" : info.Reason.Trim();
		switch (info.Code)
		{
		case 1003:
			return "no answer from the host (" + reason + ")";
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
