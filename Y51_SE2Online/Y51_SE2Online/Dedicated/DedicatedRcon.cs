using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Keen.Game2;
using Steamworks;
using Y51_SE2Online.Core;
using Y51_SE2Online.Net;
using Y51_SE2Online.NetHost;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.Dedicated;

/// <summary>
/// The dedicated server's remote console (server.json Rcon*): <see cref="RconServer"/> on its own threads, every command
/// and Steam call handed to the game thread (<see cref="Pump"/>, called from the server tick) and run through the same
/// console code as the command file, with the lines it logs captured as the reply.
/// </summary>
internal static class DedicatedRcon
{
	private const int CommandTimeoutMs = 30_000;
	private static RconServer _server;
	private static DedicatedConfig _config;
	private sealed record Request(RconCaller Caller, string Command, TaskCompletionSource<string> Done)
	{
		/// <summary>0 queued, 1 running, 2 given up by the caller (never runs late).</summary>
		public int State;
	}
	private static readonly ConcurrentQueue<Request> Requests = new();
	private static readonly ConcurrentQueue<Action> SteamCalls = new();
	private static readonly ConcurrentDictionary<ulong, TaskCompletionSource<string>> Verdicts = new();
	// Accounts with an auth session this server began for RCON; the Steam verdict callback only acts on these.
	private static readonly HashSet<ulong> SteamSessions = new();
	private static Callback<ValidateAuthTicketResponse_t> _verdicts;
	private static ulong _verdictsFor;

	/// <summary>Game thread. The server's Steam ID once multiplayer runs on its own Steam game-server session, else 0
	/// (the Workshop download's short session before it is not one: its ID dies at log-off).</summary>
	private static ulong ServerSteamId()
	{
		try
		{
			return NetSession.Current is { Host: not null } network && network.LocalIdentity != 0 && SteamGameServer.BLoggedOn()
				&& SteamGameServer.GetSteamID().m_SteamID == network.LocalIdentity ? network.LocalIdentity : 0;
		}
		catch (Exception ex) when (ex is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException) { return 0; }
	}

	/// <summary>Game thread. Steam drops its callbacks when a game-server session shuts down, so the verdict callback is
	/// made again for each session (its Steam ID changes with every anonymous logon).</summary>
	private static void EnsureVerdicts(ulong serverId)
	{
		if (_verdicts != null && _verdictsFor == serverId) return;
		try { _verdicts?.Dispose(); }
		catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }
		_verdicts = Callback<ValidateAuthTicketResponse_t>.CreateGameServer(OnVerdict);
		_verdictsFor = serverId;
	}

	public static void Start(DedicatedConfig config)
	{
		_config = config;
		if (!config.RconEnabled) return;
		try
		{
			bool password = config.RconPassword.Length > 0;
			if (!password && !config.RconSteamAuth)
			{
				GameLog.Instance.Warn("rcon: RconEnabled is on but there is no way in: set RconPassword, or turn RconSteamAuth on. Remote console stays off.");
				return;
			}
			if (password && config.RconPassword.Length < 8) GameLog.Instance.Warn("rcon: RconPassword is shorter than 8 characters; anyone who can reach the port can guess it. Use a long random one.");
			string bind = config.RconBindAddress.Length > 0 ? config.RconBindAddress : config.Network.BindAddress;
			var options = new RconOptions
			{
				Bind = IPAddress.TryParse(bind?.Trim(), out var address) ? address : IPAddress.Any, Port = config.RconPort,
				Password = config.RconPassword, SteamAuth = config.RconSteamAuth, Roles = config.RconRoles
			};
			_server = new RconServer(options, new Host(), GameLog.Instance);
			_server.Start();
			AppDomain.CurrentDomain.ProcessExit += (_, _) => _server?.Dispose();
			GameLog.Instance.Info($"rcon: listening on TCP {_server.LocalEndpoint} ({(config.RconSteamAuth ? "Steam sign-in for admins" : "no Steam sign-in")}, {(password ? "password sign-in" : "no password")})");
		}
		catch (Exception ex)
		{
			_server?.Dispose();
			_server = null;
			GameLog.Instance.Error($"rcon: could not listen on TCP port {config.RconPort} ({ex.GetType().Name}: {ex.Message}); the server runs without a remote console");
		}
	}

	/// <summary>Game thread, every server tick: Steam calls, then the waiting commands (a few per tick).</summary>
	public static void Pump(GameAppComponent app)
	{
		if (_server == null) return;
		while (SteamCalls.TryDequeue(out var call))
		{
			try { call(); }
			catch (Exception ex) { GameLog.Instance.Warn("rcon: Steam call failed: " + ex.Message); }
		}
		for (int i = 0; i < 8 && Requests.TryDequeue(out var request); i++)
		{
			if (Interlocked.CompareExchange(ref request.State, 1, 0) != 0) { i--; continue; }
			var captured = new StringBuilder();
			GameLog.Capture = captured;
			string reply;
			try { reply = Execute(app, request.Caller, request.Command); }
			catch (Exception ex) { reply = "[ERROR] " + ex.GetBaseException().Message; }
			finally { GameLog.Capture = null; }
			if (reply != null) captured.Append(reply);
			string text = captured.ToString().TrimEnd('\n');
			request.Done.TrySetResult(text.Length > 0 ? text : "OK.");
		}
	}

	// null: the command logged its own result (captured).
	private static string Execute(GameAppComponent app, RconCaller caller, string command)
	{
		string verb = RconAccess.Verb(command);
		string[] words = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
		var host = NetSession.Current?.Host;
		switch (verb)
		{
			case "": return "";
			case "help": return Help(caller);
			case "status": return StatusText();
			case "players": return PlayersText(host);
			case "json": return Json(caller, words.Skip(1).ToArray(), host);
		}
		// The same router as chat: the verb's key, and sub-commands with their own key (mods check).
		if (caller.IsSteam && caller.Role != RconCaller.Owner && Permissions() is { } perms && CommandRouter.Refusal(perms, caller.SteamId, perms.IsOwner(caller.SteamId), command, CommandChannel.Rcon) is { } refused)
			return "[WARN] " + refused;
		if (DedicatedRuntime.Exiting) return "[WARN] The server is shutting down.";
		// Reads are fine during a save or the load; changes wait their turn in the command file, here they are refused.
		if (DedicatedRuntime.Busy && verb is not ("bans" or "admins" or "mods")) return "[WARN] The server is " + (DedicatedRuntime.State().Saving ? "saving" : "loading the world") + "; try again in a moment.";
		// quit/exit/shutdown are stop; "restart update" is the runner's own message after it staged an update.
		// The dispatcher matches the prefix, so does this ("restart updates", "restart update9.9 ...").
		if (verb == "restart" && words.Length > 1 && words[1].StartsWith("update", StringComparison.OrdinalIgnoreCase)) return "[WARN] restart update is sent by the server runner only.";
		string line = verb + (words.Length > 1 ? " " + string.Join(' ', words.Skip(1)) : "");
		HostSession.LocalActor = (caller.IsSteam ? caller.Name : "Server") + " (RCON)";
		// A Steam sign-in acts as its player: every admin action it starts is checked against that player's permissions and rank.
		HostSession.LocalActorId = caller.IsSteam && caller.Role != RconCaller.Owner ? caller.SteamId : 0;
		AuditLog.Write("rcon", caller.ToString(), line, "run");
		try { DedicatedRuntime.RunCommand(app, line); }
		finally { HostSession.LocalActor = null; HostSession.LocalActorId = 0; }
		return verb switch
		{
			"save" => "Saving the world (the log shows when it completes).",
			"stop" => DedicatedRuntime.State().Exiting ? "Stopping: saving the world." : "Stop requested.",
			_ => null
		};
	}

	private static string Help(RconCaller caller)
	{
		var allowed = caller.Role == RconCaller.Owner ? ["*"] : Commands(caller);
		string[] lines =
		{
			"status | players | save | say <text>", "kick <SteamID64> [reason] | ban <SteamID64> [minutes|perm] [reason] | unban <SteamID64> | bans",
			"admin <player> | deadmin <player> | admins | group list|show <player>|set <player> <group>|remove <player> [group] | perms reload|check <player> [key]",
			"whitelist on|off|add <id>|remove <id>|list | cleanup status|preview [category]|run [category] [now]|cancel",
			"pvp on|off | raid on|off | pcu <number>|off|default | sun on|off|day <min>|move <deg>|set <0-360>",
			"usage energy|hydrogen|oxygen <0-10> | mods | motd <text>|clear | restart [now|cancel] | stop [now]",
			"json status|players|bans|admins|whitelist|config|log [after] [max]|commands"
		};
		return $"Signed in as {caller}. Commands you may run: {(allowed.Contains("*") ? "all" : string.Join(", ", RconAccess.Commands.Where(c => RconAccess.Allowed(allowed, c))))}.\n" + string.Join("\n", lines);
	}

	private static string StatusText()
	{
		var state = DedicatedRuntime.State();
		var network = NetSession.Current;
		var sb = new StringBuilder();
		sb.Append(state.Status).Append(" | World: ").Append(state.SaveId ?? (state.WorldLoaded ? _config.WorldName : "loading"))
			.Append(" | Players: ").Append(network?.Host?.PlayerTable().Count(p => p.Connected) ?? 0).Append('/').Append(_config.Network.MaxPlayers)
			.Append(" | Uptime: ").Append(Uptime(DateTime.UtcNow - state.StartedUtc)).Append(" | SE2 Online ").Append(MpPlugin.Version);
		if (network?.DescribeStatus() is { Length: > 0 } net) sb.Append('\n').Append(net.Split('\n')[0]);
		if (state.LastSaveUtc is DateTime saved) sb.Append("\nLast save: ").Append(saved.ToLocalTime().ToString("G")).Append(state.LastSaveFailed ? " (the last attempt failed)" : "");
		sb.Append('\n').Append(state.NextRestartUtc is DateTime next ? "Next scheduled restart: " + next.ToLocalTime().ToString("G") : "No scheduled restart.");
		if (state.Error != null) sb.Append("\nERROR: ").Append(state.Error);
		return sb.ToString();
	}

	private static string Uptime(TimeSpan t) => t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h" : t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : $"{(int)t.TotalMinutes}m";

	private static string PlayersText(HostSession host)
	{
		if (host == null) return "Multiplayer is not running yet.";
		var players = host.AccessPlayers().Where(p => p.Connected).ToArray();
		return "Players: " + players.Length + string.Concat(players.Select(p => $"\n{p.SteamId}  {p.Name}  {p.Role}{(p.PingMs >= 0 ? $"  {p.PingMs} ms" : "")}"));
	}

	private static readonly JsonSerializerOptions JsonOut = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

	private static string Json(RconCaller caller, string[] args, HostSession host)
	{
		string what = args.Length > 0 ? args[0].ToLowerInvariant() : "";
		long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		object result;
		switch (what)
		{
			case "status":
				var state = DedicatedRuntime.State();
				result = new
				{
					state.Status, state.Error, state.WorldLoaded, state.Saving, state.Exiting, World = state.SaveId ?? _config.WorldName,
					ServerName = _config.Network.SessionName, Players = host?.PlayerTable().Count(p => p.Connected) ?? 0, _config.Network.MaxPlayers,
					state.StartedUtc, UptimeSeconds = (long)(DateTime.UtcNow - state.StartedUtc).TotalSeconds, state.LastSaveUtc, state.LastSaveFailed,
					state.NextRestartUtc, state.RestartRequested, Version = MpPlugin.Version, Game = MpPlugin.CurrentGameVersion,
					Network = NetSession.Current?.DescribeStatus()?.Split('\n')[0], Multiplayer = host != null,
					Pvp = host?.Pvp, WhitelistOnly = host?.Access.WhitelistOnly, You = caller.ToString(), YourRole = caller.Role
				};
				break;
			case "players":
				result = (host?.AccessPlayers() ?? Array.Empty<AccessPlayer>()).Where(p => p.Connected)
					.Select(p => new { SteamId = p.SteamId.ToString(), p.Name, Role = p.Role.ToString(), p.PingMs }).ToArray();
				break;
			case "bans":
				result = (host?.Access.Copy(now).Bans ?? Array.Empty<BanEntry>())
					.Select(b => new { SteamId = b.SteamId.ToString(), b.Name, b.Reason, b.By, b.CreatedUnixMs, b.ExpiresUnixMs }).ToArray();
				break;
			case "admins":
				result = (host?.Access.Copy(now).Admins ?? Array.Empty<AccessEntry>()).Select(a => new { SteamId = a.SteamId.ToString(), a.Name }).ToArray();
				break;
			case "whitelist":
				result = new
				{
					Enforced = host?.Access.WhitelistOnly ?? false,
					Entries = (host?.Access.Copy(now).Whitelist ?? Array.Empty<AccessEntry>()).Select(a => new { SteamId = a.SteamId.ToString(), a.Name }).ToArray()
				};
				break;
			case "config":
				var file = JsonNode.Parse(File.ReadAllText(_config.ConfigPath));
				RconAccess.Redact(file);
				return file?.ToJsonString() ?? "{}";
			case "log":
				long after = args.Length > 1 && long.TryParse(args[1], out long a) ? a : 0;
				int max = args.Length > 2 && int.TryParse(args[2], out int m) ? Math.Clamp(m, 1, 500) : 200;
				result = new { Last = LogRing.Server.Last, Lines = LogRing.Server.Since(after, max).Select(l => new { l.Seq, l.Utc, l.Level, l.Text }).ToArray() };
				break;
			case "commands":
				var allowed = caller.Role == RconCaller.Owner ? ["*"] : Commands(caller);
				result = new { Role = caller.Role, Commands = RconAccess.Commands.Where(c => RconAccess.Allowed(allowed, c)).ToArray() };
				break;
			default:
				return "[WARN] Use json status|players|bans|admins|whitelist|config|log [after] [max]|commands.";
		}
		return JsonSerializer.Serialize(result, JsonOut);
	}

	private static void OnVerdict(ValidateAuthTicketResponse_t verdict)
	{
		ulong id = verdict.m_SteamID.m_SteamID;
		bool ok = verdict.m_eAuthSessionResponse == EAuthSessionResponse.k_EAuthSessionResponseOK;
		bool ours;
		lock (SteamSessions) ours = SteamSessions.Contains(id);
		// Only a verdict after our own BeginAuthSession for this account answers a waiting sign-in.
		if (ours && Verdicts.TryGetValue(id, out var waiting)) { waiting.TrySetResult(ok ? null : Describe(verdict.m_eAuthSessionResponse)); return; }
		// Later verdicts for a live session: Steam withdrew it (ticket cancelled, account signed in elsewhere, VAC).
		if (ours && !ok) _server?.Revoke(id, Describe(verdict.m_eAuthSessionResponse));
	}

	private static string Describe(EAuthSessionResponse response) => response switch
	{
		EAuthSessionResponse.k_EAuthSessionResponseUserNotConnectedToSteam => "that account is not signed in to Steam",
		EAuthSessionResponse.k_EAuthSessionResponseAuthTicketCanceled => "the ticket was cancelled",
		EAuthSessionResponse.k_EAuthSessionResponseAuthTicketInvalidAlreadyUsed => "the ticket was already used",
		EAuthSessionResponse.k_EAuthSessionResponseAuthTicketInvalid => "the ticket is not valid",
		EAuthSessionResponse.k_EAuthSessionResponseLoggedInElseWhere => "that account signed in somewhere else",
		EAuthSessionResponse.k_EAuthSessionResponseAuthTicketNetworkIdentityFailure => "the ticket was made for another server",
		EAuthSessionResponse.k_EAuthSessionResponseVACBanned or EAuthSessionResponse.k_EAuthSessionResponsePublisherIssuedBan => "that account is banned",
		_ => response.ToString()
	};

	private static string Describe(EBeginAuthSessionResult result) => result switch
	{
		EBeginAuthSessionResult.k_EBeginAuthSessionResultInvalidTicket => "the ticket is not valid for that account",
		EBeginAuthSessionResult.k_EBeginAuthSessionResultDuplicateRequest => "that account already has a Steam session on this server",
		EBeginAuthSessionResult.k_EBeginAuthSessionResultInvalidVersion => "the ticket is from another Steam version",
		EBeginAuthSessionResult.k_EBeginAuthSessionResultGameMismatch => "the ticket is for another game (the RCON tool signs in as Space Engineers 2)",
		EBeginAuthSessionResult.k_EBeginAuthSessionResultExpiredTicket => "the ticket has expired",
		_ => result.ToString()
	};

	/// <summary>The permissions in force; before multiplayer is up (the world still loading), the files on disk.</summary>
	private static PermissionSet Permissions()
	{
		if (NetSession.Current?.Host is { } host) return host.Perms;
		try
		{
			string folder = Path.Combine(_config.Directory, "Permissions");
			var set = Directory.Exists(folder) ? PermissionSet.Load(folder, Path.Combine(folder, "players.json"), out _) : PermissionSet.FromAccess(AccessList.Load(Path.Combine(_config.Directory, "access.json")));
			set.SetOwners(_config.OwnerSteamIds);
			return set;
		}
		catch (InvalidDataException) { return null; }
		catch (IOException) { return null; }
	}

	private static List<string> Commands(RconCaller caller)
	{
		if (!caller.IsSteam) return caller.Role == RconCaller.Owner ? ["*"] : [];
		var perms = Permissions();
		return perms == null ? [] : CommandRouter.AllowedVerbs(perms, caller.SteamId, perms.IsOwner(caller.SteamId));
	}

	private sealed class Host : IRconHost
	{
		public async Task<string> ExecuteAsync(RconCaller caller, string command)
		{
			var request = new Request(caller, command, new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously));
			Requests.Enqueue(request);
			if (await Task.WhenAny(request.Done.Task, Task.Delay(CommandTimeoutMs)).ConfigureAwait(false) == request.Done.Task) return request.Done.Task.Result;
			// Still queued: withdrawn, so it never runs later with a role checked long ago. Already running: its reply is coming.
			if (Interlocked.CompareExchange(ref request.State, 2, 0) == 0)
				return $"[WARN] The server did not get to the command within {CommandTimeoutMs / 1000} s (busy or stalled); it was not run.";
			return await request.Done.Task.ConfigureAwait(false);
		}

		public async Task<string> CheckSteamAsync(ulong steamId, byte[] ticket, CancellationToken cancel)
		{
			lock (SteamSessions)
				if (SteamSessions.Contains(steamId)) return "that account is already signed in over RCON; close the other session first";
			var verdict = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
			if (!Verdicts.TryAdd(steamId, verdict)) return "that account is already signing in";
			try
			{
				SteamCalls.Enqueue(() =>
				{
					if (verdict.Task.IsCompleted) return;
					try
					{
						ulong serverId = ServerSteamId();
						if (serverId == 0)
						{
							verdict.TrySetResult("the server is not signed in to Steam yet (multiplayer starts once the world is loaded); try again in a moment");
							return;
						}
						EnsureVerdicts(serverId);
						// Listed before the call: Steam may answer on its own thread before BeginAuthSession returns here.
						lock (SteamSessions) SteamSessions.Add(steamId);
						var result = SteamGameServer.BeginAuthSession(ticket, ticket.Length, new CSteamID(steamId));
						if (result != EBeginAuthSessionResult.k_EBeginAuthSessionResultOK)
						{
							lock (SteamSessions) SteamSessions.Remove(steamId);
							verdict.TrySetResult(Describe(result));
							return;
						}
					}
					catch (Exception ex) when (ex is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
					{
						lock (SteamSessions) SteamSessions.Remove(steamId);
						verdict.TrySetResult("Steam is not running on the server yet (" + ex.Message + ")");
					}
				});
				using (cancel.Register(() => verdict.TrySetCanceled(cancel)))
				{
					string failed = await verdict.Task.ConfigureAwait(false);
					// Ends the session if BeginAuthSession started one (a no-op when it never did).
					if (failed != null) EndSteam(steamId);
					return failed;
				}
			}
			catch (OperationCanceledException)
			{
				// The Steam call may still be queued or done: end whatever it began, on the game thread after it.
				SteamCalls.Enqueue(() => EndSteam(steamId));
				throw;
			}
			finally { Verdicts.TryRemove(new KeyValuePair<ulong, TaskCompletionSource<string>>(steamId, verdict)); }
		}

		public Task<ulong> ServerSteamIdAsync(CancellationToken cancel)
		{
			var id = new TaskCompletionSource<ulong>(TaskCreationOptions.RunContinuationsAsynchronously);
			cancel.Register(() => id.TrySetCanceled(cancel));
			SteamCalls.Enqueue(() =>
			{
				id.TrySetResult(ServerSteamId());
			});
			return id.Task;
		}

		public void EndSteam(ulong steamId)
		{
			lock (SteamSessions) if (!SteamSessions.Remove(steamId)) return;
			SteamCalls.Enqueue(() => SteamGameServer.EndAuthSession(new CSteamID(steamId)));
		}

		public string RoleOf(ulong steamId)
		{
			var perms = Permissions();
			if (perms == null) return null;
			return perms.IsOwner(steamId) ? RconCaller.Owner : perms.Has(steamId, "admin.panel") ? RconCaller.Admin : RconCaller.Player;
		}

		public List<string> CommandsFor(RconCaller caller) => Commands(caller);

		public string NameOf(ulong steamId)
		{
			var host = NetSession.Current?.Host;
			string name = host?.AccessPlayers().FirstOrDefault(p => p.SteamId == steamId)?.Name
				?? host?.Access.Copy(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).Admins.FirstOrDefault(a => a.SteamId == steamId)?.Name;
			return string.IsNullOrWhiteSpace(name) ? "Steam user" : name;
		}
	}
}
