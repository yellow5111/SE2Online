using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using Y51_SE2Online.Net;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.Core;

public sealed class MpOptions
{
	public int MaxPlayers { get; internal set; } = 8;

	public bool FriendsOnly { get; internal set; } = true;

	public bool UseSteamIp { get; private set; }

	public IPEndPoint SteamListenIp { get; private set; }

	public bool Dedicated { get; internal set; }

	/// <summary>SE2 4.0: the game's own join (invite, join code) already started the client; SE2 Online only rides along.</summary>
	public bool KeenAttached { get; internal set; }

	/// <summary>SE2 4.0: the game session to join (Steam lobby or server address) when the game itself picked it.</summary>
	public Keen.VRage.Core.Services.GameSessions.GameSessionID? KeenSession { get; internal set; }

	/// <summary>The game's join code of the co-op session to find and join (Steam lobby data "code").</summary>
	public Keen.VRage.Core.Services.GameSessions.SessionJoinCode? JoinCode { get; internal set; }

	public bool Advertise { get; internal set; }

	public string SessionName { get; internal set; } = "My SE2 session";

	public string Description { get; internal set; } = "";
	/// <summary>The country to publish, "" to let the directory locate the server by IP.</summary>
	public string Country { get; internal set; } = "";

	public string PublicAddress { get; internal set; } = "";

	public string Password { get; internal set; } = "";

	public string Motd { get; internal set; } = "";

	public string MotdTitle { get; internal set; } = "";

	public bool Pvp { get; internal set; } = true;

	public bool OfflineRaidProtection { get; internal set; } = true;

	public int? PcuLimit { get; internal set; }

	public double StreamingRadiusMeters { get; internal set; }
	public bool WaterSync { get; internal set; }
	public bool ClientMovement { get; internal set; }
	public bool OwnReplication { get; internal set; } = true;
	public double VoiceRadiusMeters { get; internal set; }
	public Y51_SE2Online.Sync.ResourceMultipliers UsageMultipliers { get; internal set; } = Y51_SE2Online.Sync.ResourceMultipliers.Default;
	public Y51_SE2Online.Sync.InventoryMultipliers InventorySizes { get; internal set; } = Y51_SE2Online.Sync.InventoryMultipliers.Default;

	public bool? SunRotation { get; internal set; }

	public double? SunPeriodMinutes { get; internal set; }

	public string JoinPassword { get; internal set; } = "";

	public string ServersDirectory { get; internal set; } = "https://se2-online-servers.yellow51.dev";

	public string SessionsDirectory { get; internal set; } = "https://se2-online-sessions.yellow51.dev";

	public const int DefaultVirtualPort = 7742;

	public const int DefaultIpPort = 7742;

	public const string Prefix = "-y51online:";

	// Floor of the adaptive playout delay: two 60 Hz host samples plus margin. Live sessions sat pinned at the old 60 ms
	// floor (p97 lateness + 12 ms margin stayed below it, extrapolation 0.3-0.4%), so the floor alone delayed every remote body.
	public const int DefaultInterpMs = 35;

	public const double DefaultNametagRange = 200.0;

	internal static bool TestAllowNonLoopbackTcp;

	public bool HostSteam { get; private set; }

	public IPEndPoint? HostIp { get; private set; }

	public ulong JoinSteamId { get; private set; }

	public IPEndPoint? JoinIp { get; private set; }

	public int VirtualPort { get; private set; } = 7742;

	public int SendRateMaxBytes { get; private set; } = 8388608;

	public int SendRateMinBytes { get; private set; } = 1048576;

	public bool Sync { get; private set; } = true;

	public bool ClientGame { get; private set; } = true;

	public bool Spectator { get; private set; } = true;

	public bool? ClientMissions { get; private set; }

	public int StateBudgetBytesPerSec { get; private set; } = 1048576;

	public string? PlayerName { get; private set; }

	public int InterpMs { get; private set; } = DefaultInterpMs;

	public bool Character { get; private set; } = true;

	public bool Predict { get; private set; } = true;

	public bool SpawnNearHost { get; private set; }

	public bool Identities { get; private set; } = true;

	public GridPermissionMode Grids { get; private set; }

	public ResearchMode Research { get; private set; }

	public bool Rejoin { get; private set; } = true;

	public bool ExitFix { get; private set; } = true;

	public bool AudioFix { get; private set; } = true;

	public bool OnePilot { get; private set; } = true;

	public bool BuildFix { get; private set; } = true;

	public bool ControlFix { get; private set; } = true;

	public bool FastSmooth { get; private set; }

	public bool BodyFilter { get; private set; } = true;

	public bool DeckFrame { get; private set; } = true;

	public bool AdaptiveInterp { get; private set; } = true;

	public Dictionary<string, double> RateCaps { get; } = new(StringComparer.OrdinalIgnoreCase);

	public HashSet<ulong> Allow { get; } = new();

	public bool JoinFriends { get; private set; }

	public bool JoinWait { get; private set; }

	public string? JoinFriendFilter { get; private set; }

	public bool Nametags { get; private set; } = true;

	public double NametagRange { get; private set; } = 200.0;

	public bool JoinSteamDynamic => JoinSteamId == 0 && JoinIp is null && (JoinFriends || JoinWait);

	public List<string> Recognised { get; } = new();

	public List<string> Errors { get; } = new();

	public bool Host => HostSteam || HostIp != null;

	public bool Join => JoinSteamId != 0 || JoinIp is not null || JoinFriends || JoinWait;

	public bool Networking => (Host || Join) && Errors.Count == 0;

	public bool Any => Host || Join;

	public bool NeedsSteam
	{
		get
		{
			if (UseSteamIp || Dedicated)
			{
				return true;
			}
			return HostSteam || JoinSteamId != 0 || (JoinIp is null && (JoinFriends || JoinWait));
		}
	}

	public static MpOptions Parse(IEnumerable<string>? args)
	{
		var options = new MpOptions();
		if (args is null)
		{
			return options;
		}
		foreach (string arg in args)
		{
			if (arg is null)
			{
				continue;
			}
			string text = arg.Trim().Trim('"');
			if (!text.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			string body = text.Substring(Prefix.Length);
			int colon = body.IndexOf(':');
			string key = (colon < 0 ? body : body.Substring(0, colon)).ToLowerInvariant();
			string value = colon < 0 ? null : body.Substring(colon + 1);
			try
			{
				if (!options.Apply(key, value))
				{
					options.Errors.Add($"unknown argument '{text}'");
				}
				else
				{
					options.Recognised.Add(text);
				}
			}
			catch (FormatException ex)
			{
				options.Errors.Add($"bad argument '{text}': {ex.Message}");
			}
		}
		if (options.Host && options.Join)
		{
			options.Errors.Add("host and join arguments are mutually exclusive");
		}
		if (options.JoinSteamId != 0 && options.JoinIp is not null)
		{
			options.Errors.Add("-y51online:join and -y51online:join-ip are mutually exclusive");
		}
		if (options.JoinIp is not null && (options.JoinFriends || options.JoinWait))
		{
			options.Errors.Add("-y51online:join:friends / join:wait and -y51online:join-ip are mutually exclusive");
		}
		if (options.HostIp is not null && !TestAllowNonLoopbackTcp && !IsLoopback(options.HostIp.Address))
		{
			options.Errors.Add($"-y51online:host-ip is for same-PC tests and only listens on 127.0.0.1 / ::1 (got {options.HostIp.Address}); to play over the internet or a LAN, host with -y51online:host (Steam)");
		}
		if (options.JoinIp is not null && !options.UseSteamIp && !TestAllowNonLoopbackTcp && !IsLoopback(options.JoinIp.Address))
		{
			options.Errors.Add($"-y51online:join-ip is for same-PC tests and only connects to 127.0.0.1 / ::1 (got {options.JoinIp.Address}); join a friend over Steam with -y51online:join");
		}
		if (options.HostSteam && options.HostIp is not null)
		{
			options.Errors.Add("-y51online:host and -y51online:host-ip are mutually exclusive (one link per session in this build)");
		}
		return options;
	}

	private static bool IsLoopback(IPAddress address) =>
		IPAddress.IsLoopback(address) || (address.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(address.MapToIPv4()));

	private bool Apply(string key, string? value)
	{
		switch (key)
		{
		case "listen-address":
			SteamListenIp = ServerEndpoint.Parse(value, listen: true);
			return true;
		case "join-address":
			JoinIp = ServerEndpoint.Parse(value);
			UseSteamIp = true;
			return true;
		case "host":
			if (value is not null)
			{
				throw new FormatException("-y51online:host takes no value (use -y51online:host-ip:<port> for TCP)");
			}
			HostSteam = true;
			return true;
		case "host-ip":
			HostIp = value is null ? new IPEndPoint(IPAddress.Loopback, 7742) : TcpLink.ParseEndpoint(value, IPAddress.Loopback);
			return true;
		case "join":
			if (value is not null && value.Equals("friends", StringComparison.OrdinalIgnoreCase))
			{
				JoinFriends = true;
				return true;
			}
			if (value is not null && value.Equals("wait", StringComparison.OrdinalIgnoreCase))
			{
				JoinWait = true;
				return true;
			}
			if (value is not null && value.StartsWith("friend:", StringComparison.OrdinalIgnoreCase))
			{
				string filter = value.Substring("friend:".Length).Trim().Trim('"');
				if (filter.Length == 0 || filter.Length > 64)
				{
					throw new FormatException("expected -y51online:join:friend:<part of the friend's Steam name, or its SteamID64>");
				}
				JoinFriends = true;
				JoinFriendFilter = filter;
				return true;
			}
			JoinSteamId = ParseSteamId(value);
			return true;
		case "join-ip":
			JoinIp = value is null ? new IPEndPoint(IPAddress.Loopback, 7742) : TcpLink.ParseEndpoint(value, IPAddress.Loopback);
			if (JoinIp.Address.Equals(IPAddress.Any))
			{
				throw new FormatException("join address 0.0.0.0 is not connectable; use 127.0.0.1 or the host's LAN address");
			}
			return true;
		case "vport":
			VirtualPort = ParseInt(value, 0, 65535);
			return true;
		case "sendrate":
			SendRateMaxBytes = checked(ParseInt(value, 64, 1048576) * 1024);
			return true;
		case "sendratemin":
			SendRateMinBytes = checked(ParseInt(value, 16, 1048576) * 1024);
			return true;
		case "nosync":
			Sync = false;
			return true;
		case "noclientgame":
			ClientGame = false;
			return true;
		case "nospectator":
			Spectator = false;
			return true;
		case "missions":
			ClientMissions = value?.ToLowerInvariant() switch
			{
				"auto" => null,
				"on" => true,
				"off" => false,
				_ => throw new FormatException("expected -y51online:missions:auto, :on or :off"),
			};
			return true;
		case "statebudget":
			StateBudgetBytesPerSec = checked(ParseInt(value, 8, 1048576) * 1024);
			return true;
		case "cap":
		{
			if (string.IsNullOrEmpty(value) || value.IndexOf('=') <= 0)
			{
				throw new FormatException("expected -y51online:cap:<DataTypeShortName>=<Hz>");
			}
			string typeName = value.Substring(0, value.IndexOf('='));
			string rateText = value.Substring(value.IndexOf('=') + 1);
			if (!double.TryParse(rateText, NumberStyles.Float, CultureInfo.InvariantCulture, out var hz) || hz < 0.0 || hz > 1000.0)
			{
				throw new FormatException($"rate '{rateText}' must be 0..1000 Hz (0 = never send)");
			}
			RateCaps[typeName] = hz;
			return true;
		}
		case "name":
		{
			string name = (value ?? "").Trim();
			if (name.Length == 0 || name.Length > 32)
			{
				throw new FormatException("expected a player name of 1-32 characters");
			}
			foreach (char c in name)
			{
				if (char.IsControl(c))
				{
					throw new FormatException("player name contains a control character");
				}
			}
			PlayerName = name;
			return true;
		}
		case "interp":
			InterpMs = ParseInt(value, 0, 500);
			return true;
		case "nocharacter":
			Character = false;
			return true;
		case "predict":
			Predict = value?.ToLowerInvariant() switch
			{
				"on" => true,
				"off" => false,
				_ => throw new FormatException("expected -y51online:predict:on or :off"),
			};
			return true;
		case "spawn":
			SpawnNearHost = value?.ToLowerInvariant() switch
			{
				"respawn" => false,
				"host" => true,
				_ => throw new FormatException("expected -y51online:spawn:respawn or :host"),
			};
			return true;
		case "rejoin":
			Rejoin = OnOff(value, "rejoin");
			return true;
		case "exitfix":
			ExitFix = OnOff(value, "exitfix");
			return true;
		case "audiofix":
			AudioFix = OnOff(value, "audiofix");
			return true;
		case "onepilot":
			OnePilot = OnOff(value, "onepilot");
			return true;
		case "buildfix":
			BuildFix = OnOff(value, "buildfix");
			return true;
		case "controlfix":
			ControlFix = OnOff(value, "controlfix");
			return true;
		case "fastsmooth":
			FastSmooth = OnOff(value, "fastsmooth");
			return true;
		case "bodyfilter":
			BodyFilter = OnOff(value, "bodyfilter");
			return true;
		case "deckframe":
			DeckFrame = OnOff(value, "deckframe");
			return true;
		case "adaptive":
			AdaptiveInterp = OnOff(value, "adaptive");
			return true;
		case "identities":
			Identities = value?.ToLowerInvariant() switch
			{
				"on" => true,
				"off" => false,
				_ => throw new FormatException("expected -y51online:identities:on or :off"),
			};
			return true;
		case "grids":
			Grids = value?.ToLowerInvariant() switch
			{
				"team" => GridPermissionMode.Team,
				"strict" => GridPermissionMode.Strict,
				_ => throw new FormatException("expected -y51online:grids:team or :strict"),
			};
			return true;
		case "research":
			Research = value?.ToLowerInvariant() switch
			{
				"shared" => ResearchMode.Shared,
				"own" => ResearchMode.Own,
				_ => throw new FormatException("expected -y51online:research:shared or :own"),
			};
			return true;
		case "nametags":
		{
			string setting = (value ?? "on").Trim().ToLowerInvariant();
			if (setting == "on")
			{
				Nametags = true;
			}
			else if (setting == "off")
			{
				Nametags = false;
			}
			else
			{
				// Negated comparisons so NaN is rejected as well.
				if (!double.TryParse(setting, NumberStyles.Float, CultureInfo.InvariantCulture, out var range) || !(range >= 1.0) || !(range <= 100000.0))
				{
					throw new FormatException("expected -y51online:nametags:on, :off or a range in metres (1-100000)");
				}
				Nametags = true;
				NametagRange = range;
			}
			return true;
		}
		case "allow":
			if (string.IsNullOrEmpty(value))
			{
				throw new FormatException("expected a comma-separated SteamID64 list");
			}
			foreach (string id in value.Split(','))
			{
				Allow.Add(ParseSteamId(id));
			}
			return true;
		default:
			return false;
		}
	}

	public static ulong ParseSteamId(string? s)
	{
		if (string.IsNullOrWhiteSpace(s) || !ulong.TryParse(s.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var result))
		{
			throw new FormatException($"'{s}' is not a SteamID64 for a player or game server");
		}
		ulong accountType = (result >> 52) & 15;
		if ((result >> 56) != 1 || (uint)result == 0 || accountType is not (1 or 3 or 4)
			|| (accountType == 1 && ((result >> 32) & 0xFFFFF) != 1))
		{
			throw new FormatException($"'{s}' is not a public player or game-server SteamID64");
		}
		return result;
	}

	private static bool OnOff(string? value, string name) => value?.ToLowerInvariant() switch
	{
		"on" => true,
		"off" => false,
		_ => throw new FormatException($"expected -y51online:{name}:on or :off"),
	};

	private static int ParseInt(string? s, int min, int max)
	{
		if (s is null || !int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) || result < min || result > max)
		{
			throw new FormatException($"expected an integer in [{min}, {max}]");
		}
		return result;
	}

	public string Describe()
	{
		var parts = new List<string>();
		if (HostSteam)
		{
			parts.Add("host(steam)");
		}
		if (HostIp is not null)
		{
			parts.Add($"host(tcp {HostIp})");
		}
		if (JoinSteamId != 0)
		{
			parts.Add($"join(steam {JoinSteamId})");
		}
		else if (JoinFriends && JoinIp is null)
		{
			parts.Add(JoinFriendFilter is not null ? $"join(steam friends matching '{JoinFriendFilter}')" : "join(steam friends)");
		}
		else if (JoinWait && JoinIp is null)
		{
			parts.Add("join(steam: wait for a Join Game click)");
		}
		if (JoinIp is not null)
		{
			parts.Add($"join(tcp {JoinIp})");
		}
		if (Host || Join)
		{
			if (NeedsSteam)
			{
				parts.Add($"vport={VirtualPort}");
				parts.Add($"sendrate={SendRateMinBytes / 1024}..{SendRateMaxBytes / 1024}KiB/s");
			}
			parts.Add(Sync ? "sync=on" : "sync=off");
			if (Join && !ClientGame)
			{
				parts.Add("noclientgame");
			}
			if (Join && !Spectator)
			{
				parts.Add("nospectator");
			}
			if (Join && ClientMissions.HasValue)
			{
				parts.Add("missions=" + (ClientMissions.Value ? "on" : "off"));
			}
			if (Join && PlayerName is not null)
			{
				parts.Add($"name='{PlayerName}'");
			}
			if (Join && InterpMs != DefaultInterpMs)
			{
				parts.Add($"interp={InterpMs}ms");
			}
			if (Join && !Character)
			{
				parts.Add("nocharacter");
			}
			if (!Predict)
			{
				parts.Add("predict=off");
			}
			if (Join && !Rejoin)
			{
				parts.Add("rejoin=off");
			}
			if (!ExitFix)
			{
				parts.Add("exitfix=off");
			}
			if (!AudioFix)
			{
				parts.Add("audiofix=off");
			}
			if (Host && !OnePilot)
			{
				parts.Add("onepilot=off");
			}
			if (!BuildFix)
			{
				parts.Add("buildfix=off");
			}
			if (!ControlFix)
			{
				parts.Add("controlfix=off");
			}
			if (Join && FastSmooth)
			{
				parts.Add("fastsmooth=on");
			}
			if (Join && !BodyFilter)
			{
				parts.Add("bodyfilter=off");
			}
			if (Join && !DeckFrame)
			{
				parts.Add("deckframe=off");
			}
			if (Join && !AdaptiveInterp)
			{
				parts.Add("adaptive=off");
			}
			if (Host && SpawnNearHost)
			{
				parts.Add("spawn=host");
			}
			parts.Add(Nametags ? $"nametags<={NametagRange.ToString(CultureInfo.InvariantCulture)}m" : "nametags=off");
			if (Host)
			{
				parts.Add(Identities
					? $"identities=on grids={(Grids == GridPermissionMode.Team ? "team" : "strict")} research={(Research == ResearchMode.Shared ? "shared" : "own")}"
					: "identities=off");
			}
			if (Host)
			{
				parts.Add($"statebudget={StateBudgetBytesPerSec / 1024}KiB/s");
			}
			foreach (var cap in RateCaps)
			{
				parts.Add($"cap {cap.Key}={cap.Value.ToString(CultureInfo.InvariantCulture)}Hz");
			}
			if (Allow.Count > 0)
			{
				parts.Add("allow=" + string.Join(",", Allow));
			}
		}
		if (parts.Count != 0)
		{
			return string.Join(" ", parts);
		}
		return "none";
	}
}
