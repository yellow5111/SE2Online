using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Y51_SE2Online.Core;

/// <summary>Who is signed in on one RCON connection. Owner is the password; Steam sign-ins take their role from the admin list.</summary>
public sealed class RconCaller
{
	public const string Owner = "Owner", Admin = "Admin", Player = "Player";
	public string Address { get; init; } = "?";
	public ulong SteamId { get; init; }
	public string Name { get; init; } = "";
	/// <summary>Owner, Admin or Player; a Steam sign-in's role is looked up again before every command.</summary>
	public string Role { get; set; } = Player;
	public bool IsSteam => SteamId != 0;
	public override string ToString() => IsSteam ? $"{Name} ({SteamId}, {Role})" : "password (Owner)";
}

/// <summary>The dedicated server side of RCON: running commands on the game thread, Steam ticket checks, roles.</summary>
public interface IRconHost
{
	/// <summary>Runs one console command for <paramref name="caller"/> and returns what it printed.</summary>
	Task<string> ExecuteAsync(RconCaller caller, string command);
	/// <summary>Asks the server's Steam game server to confirm <paramref name="ticket"/> belongs to <paramref name="steamId"/>; null on success, else why not.</summary>
	Task<string> CheckSteamAsync(ulong steamId, byte[] ticket, CancellationToken cancel);
	/// <summary>The server's own Steam ID (its Steam game server), 0 while it is not signed in to Steam.</summary>
	Task<ulong> ServerSteamIdAsync(CancellationToken cancel);
	/// <summary>Ends the Steam auth session a successful check started.</summary>
	void EndSteam(ulong steamId);
	/// <summary>Owner, Admin or Player for display (an owner runs every command). Null while the permissions cannot be read.</summary>
	string RoleOf(ulong steamId);
	/// <summary>The command words this Steam sign-in may run ("*" for all); empty when it may not use RCON at all
	/// (0.18: rcon.connect and command.&lt;verb&gt; permissions).</summary>
	List<string> CommandsFor(RconCaller caller);
	string NameOf(ulong steamId);
}

public sealed class RconOptions
{
	public IPAddress Bind { get; init; } = IPAddress.Any;
	public int Port { get; init; } = 27020;
	public string Password { get; init; } = "";
	public bool SteamAuth { get; init; } = true;
	public IReadOnlyDictionary<string, List<string>> Roles { get; init; } = RconAccess.DefaultRoles();
	/// <summary>Signed-in connections.</summary>
	public int MaxClients { get; init; } = 16;
	/// <summary>Connections still signing in, counted apart so a flood of them never keeps signed-in consoles out.</summary>
	public int MaxPending { get; init; } = 32;
	/// <summary>Connections from one address (an IPv6 /64 counts as one), signed in or not.</summary>
	public int MaxPerAddress { get; init; } = 4;
	public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(15);
	/// <summary>How long a new connection has to start its sign-in packet, and any started packet to finish.</summary>
	public TimeSpan SignInTimeout { get; init; } = TimeSpan.FromSeconds(4);
	/// <summary>How long one reply may take to go out; a client that stops reading is dropped.</summary>
	public TimeSpan WriteTimeout { get; init; } = TimeSpan.FromSeconds(30);
	public TimeSpan SteamTimeout { get; init; } = TimeSpan.FromSeconds(10);
	public int LockoutFailures { get; init; } = 5;
	public TimeSpan LockoutFor { get; init; } = TimeSpan.FromMinutes(5);
}

/// <summary>Refused sign-ins per address: <c>failures</c> within the window lock the address out until it ends.</summary>
public sealed class RconLockout
{
	private readonly int _failures;
	private readonly TimeSpan _window;
	private readonly Dictionary<string, (int Count, DateTime Until)> _seen = new();
	private const int MaxTracked = 10_000;

	public RconLockout(int failures, TimeSpan window) { _failures = failures; _window = window; }

	/// <summary>Time left on the address's lockout, or null when it may try.</summary>
	public TimeSpan? LockedFor(string address, DateTime now)
	{
		lock (_seen)
		{
			if (!_seen.TryGetValue(address, out var entry)) return null;
			if (entry.Until <= now) { _seen.Remove(address); return null; }
			return entry.Count >= _failures ? entry.Until - now : null;
		}
	}

	/// <summary>Counts a refusal; true when this one locks the address out.</summary>
	public bool Fail(string address, DateTime now)
	{
		lock (_seen)
		{
			if (_seen.Count >= MaxTracked)
				foreach (var stale in _seen.Where(e => e.Value.Until <= now).Select(e => e.Key).ToList()) _seen.Remove(stale);
			// ponytail: a flood from more than 10,000 addresses at once stops being counted; the per-address and
			// total connection caps still bound it.
			if (_seen.Count >= MaxTracked && !_seen.ContainsKey(address)) return false;
			int count = _seen.TryGetValue(address, out var entry) && entry.Until > now ? entry.Count + 1 : 1;
			// The window starts at the first refusal and is not stretched by later ones, so a lockout always ends.
			DateTime until = count == 1 ? now + _window : entry.Until;
			if (count == _failures) until = now + _window;
			_seen[address] = (count, until);
			return count == _failures;
		}
	}

	public void Clear(string address) { lock (_seen) _seen.Remove(address); }
}

/// <summary>Which commands a role may run (RconRoles in server.json): command words, "*" for all.</summary>
public static class RconAccess
{
	/// <summary>Every command word RCON knows, after aliases.</summary>
	public static readonly string[] Commands =
	[
		"help", "status", "players", "json", "save", "say", "kick", "ban", "unban", "bans", "admin", "deadmin", "admins",
		"whitelist", "pvp", "raid", "pcu", "sun", "usage", "mods", "motd", "restart", "stop", "group", "perms", "cleanup"
	];

	public static Dictionary<string, List<string>> DefaultRoles() => new(StringComparer.OrdinalIgnoreCase)
	{
		// What the in-game admin panel lets an admin do, plus save: no stop or restart, no admin list changes.
		[RconCaller.Admin] = ["status", "players", "json", "save", "say", "kick", "ban", "unban", "bans", "admins", "whitelist", "pvp", "raid", "pcu", "sun", "usage", "mods", "motd"],
		[RconCaller.Player] = []
	};

	/// <summary>The command word with aliases resolved (quit/exit/shutdown are stop, player is players), lower case.</summary>
	public static string Verb(string command)
	{
		string text = (command ?? "").Trim();
		int space = text.IndexOf(' ');
		string word = (space < 0 ? text : text[..space]).ToLowerInvariant();
		return word is "quit" or "exit" or "shutdown" ? "stop" : word == "player" ? "players" : word;
	}

	public static List<string> ListFor(IReadOnlyDictionary<string, List<string>> roles, string role)
	{
		if (string.Equals(role, RconCaller.Owner, StringComparison.OrdinalIgnoreCase)) return ["*"];
		List<string> list = null;
		if (roles != null && role != null)
			foreach (var (name, commands) in roles)
				if (string.Equals(name, role, StringComparison.OrdinalIgnoreCase)) { list = commands; break; }
		return (list ?? []).Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => Verb(c)).Distinct().ToList();
	}

	/// <summary>help is always allowed; "json" allows the whole json family.</summary>
	public static bool Allowed(IReadOnlyCollection<string> list, string command)
	{
		string verb = Verb(command);
		return verb is "help" or "" || list.Contains("*") || list.Contains(verb);
	}

	// Key bindings end in "Key" too; they are the only such settings that are not secrets.
	private static readonly HashSet<string> KeyBindings = new(StringComparer.OrdinalIgnoreCase) { "ChatKey", "PlayersKey", "AdminKey", "BroadcastKey", "VoiceKey", "EmoteKey", "HackKey", "PingKey", "MarkerFilterKey" };

	/// <summary>Whether a config option holds a secret: any name with password, secret, token or credential in it, or one
	/// ending in Key (an API or private key) except the key bindings.</summary>
	public static bool Secret(string name) =>
		name.Contains("password", StringComparison.OrdinalIgnoreCase) || name.Contains("secret", StringComparison.OrdinalIgnoreCase)
		|| name.Contains("token", StringComparison.OrdinalIgnoreCase) || name.Contains("credential", StringComparison.OrdinalIgnoreCase)
		|| name.EndsWith("key", StringComparison.OrdinalIgnoreCase) && !KeyBindings.Contains(name);

	/// <summary>Every secret value (<see cref="Secret"/>: the join password, RconPassword, any token or key) becomes "(set)" or "": json config never carries one.</summary>
	public static void Redact(System.Text.Json.Nodes.JsonNode node)
	{
		if (node is System.Text.Json.Nodes.JsonObject obj)
		{
			foreach (var (key, value) in obj.ToList())
			{
				if (Secret(key))
					obj[key] = value is System.Text.Json.Nodes.JsonValue v && v.TryGetValue(out string text) && text.Length == 0 ? "" : "(set)";
				else Redact(value);
			}
		}
		else if (node is System.Text.Json.Nodes.JsonArray array) foreach (var item in array) Redact(item);
	}

	/// <summary>Why a command is refused before it reaches the console, or null: one line, no control characters.</summary>
	public static string Malformed(string command)
	{
		if (command.Length > 1000) return "Commands are at most 1000 characters.";
		foreach (char c in command)
			if (char.IsControl(c)) return "Commands are one line of text (no line breaks or control characters).";
		return null;
	}
}

/// <summary>
/// Remote console over TCP in the Source RCON protocol (<see cref="RconWire"/>), so the SE2 Online RCON tool and any
/// generic RCON client can run the dedicated console's commands. Two ways in: the RconPassword (role Owner, every
/// command), or "steam:&lt;SteamID64&gt;:&lt;ticket hex&gt;", a Steam auth session ticket the server's Steam game
/// server confirms; that account's role on the admin list then picks its commands (RconRoles). Connections are served
/// one request at a time, so the empty-packet echo always follows the reply it marks.
/// </summary>
public sealed class RconServer : IDisposable
{
	private readonly RconOptions _o;
	private readonly IRconHost _host;
	private readonly IMpLog _log;
	private readonly Func<DateTime> _clock;
	private readonly byte[] _passwordHash;
	private readonly RconLockout _lockout;
	private readonly CancellationTokenSource _stop = new();
	private readonly ConcurrentDictionary<Session, byte> _sessions = new();
	private readonly Dictionary<string, int> _perAddress = new();
	private TcpListener _listener;
	private int _clients, _pending;

	private sealed class Session
	{
		public RconCaller Caller;
		/// <summary>Set once Steam confirmed this account's ticket: its auth session ends with the connection, signed in or refused.</summary>
		public ulong SteamSession;
		/// <summary>Closed once the current reply is out (the account lost its access).</summary>
		public bool Done;
		public readonly CancellationTokenSource Closed = new();
	}

	public RconServer(RconOptions options, IRconHost host, IMpLog log, Func<DateTime> clock = null)
	{
		_o = options;
		_host = host;
		_log = log;
		_clock = clock ?? (() => DateTime.UtcNow);
		_passwordHash = string.IsNullOrEmpty(options.Password) ? null : SHA256.HashData(Encoding.UTF8.GetBytes(options.Password));
		_lockout = new RconLockout(options.LockoutFailures, options.LockoutFor);
	}

	public IPEndPoint LocalEndpoint => _listener?.LocalEndpoint as IPEndPoint;
	public int Connected => Volatile.Read(ref _clients);

	/// <summary>Opens the port. Throws when it cannot listen (port in use); the caller reports it.</summary>
	public void Start()
	{
		_listener = new TcpListener(_o.Bind, _o.Port);
		_listener.Start();
		_ = AcceptLoop();
	}

	/// <summary>Closes every connection signed in as this Steam account (Steam withdrew the ticket: cancelled, banned, signed out).</summary>
	public void Revoke(ulong steamId, string why)
	{
		foreach (var s in _sessions.Keys)
			if (s.Caller?.SteamId == steamId)
			{
				_log.Info($"rcon: {s.Caller} signed out by Steam ({why})");
				s.Closed.Cancel();
			}
	}

	private async Task AcceptLoop()
	{
		while (!_stop.IsCancellationRequested)
		{
			TcpClient client;
			try { client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false); }
			catch (OperationCanceledException) { break; }
			catch (ObjectDisposedException) { break; }
			catch (SocketException ex) { _log.Warn("rcon: accept failed: " + ex.Message); continue; }
			string address = AddressKey((client.Client.RemoteEndPoint as IPEndPoint)?.Address);
			if (!Admit(address))
			{
				// Full: closed before a byte is read, so a flood of connections costs nothing but the accept.
				client.Dispose();
				continue;
			}
			_ = Task.Run(() => Serve(client, address));
		}
	}

	/// <summary>The address as lockouts and caps count it: IPv4 as is, IPv6 by its /64 (one host can hold a whole /64).</summary>
	public static string AddressKey(IPAddress address)
	{
		if (address == null) return "?";
		if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
		if (address.AddressFamily != AddressFamily.InterNetworkV6) return address.ToString();
		byte[] bytes = address.GetAddressBytes();
		Array.Clear(bytes, 8, 8);
		return new IPAddress(bytes) + "/64";
	}

	private bool Admit(string address)
	{
		lock (_perAddress)
		{
			_perAddress.TryGetValue(address, out int mine);
			if (_pending >= _o.MaxPending || mine >= _o.MaxPerAddress) return false;
			_perAddress[address] = mine + 1;
			_pending++;
			return true;
		}
	}

	/// <summary>Moves a connection from signing in to signed in; false when every signed-in slot is taken.</summary>
	private bool Promote()
	{
		lock (_perAddress)
		{
			if (_clients >= _o.MaxClients) return false;
			_pending--;
			_clients++;
			return true;
		}
	}

	private void Release(string address, bool signedIn)
	{
		lock (_perAddress)
		{
			if (signedIn) _clients--; else _pending--;
			if (_perAddress.TryGetValue(address, out int mine) && mine > 1) _perAddress[address] = mine - 1;
			else _perAddress.Remove(address);
		}
	}

	private async Task Serve(TcpClient client, string address)
	{
		var session = new Session();
		bool signedIn = false, awaitingSignIn = false;
		_sessions[session] = 0;
		using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, session.Closed.Token);
		try
		{
			client.NoDelay = true;
			// Not disposed here: that closes the socket at once; CloseGently ends the connection.
			var stream = client.GetStream();
			// Nothing but a sign-in until one succeeds, and only one try per connection.
			var auth = await RconWire.ReadAsync(stream, _o.SignInTimeout, _o.SignInTimeout, cancel.Token).ConfigureAwait(false);
			// The tool binds its Steam ticket to this server's Steam ID, so it asks for it first (once; a locked-out address is
			// refused instead). Steam then refuses the ticket on any other server. The tool has twice the sign-in timeout to
			// fetch the ticket; a query that is not followed by a sign-in counts as a refused one, so it cannot hold slots for free.
			if (auth is { Type: RconWire.Auth } ask && ask.Text == RconWire.ServerIdQuery && _lockout.LockedFor(address, _clock()) == null)
			{
				ulong serverId = _o.SteamAuth ? await ServerSteamId(cancel.Token).ConfigureAwait(false) : 0;
				await Send(stream, RconWire.Encode(ask.Id, RconWire.Response, RconWire.ServerIdReply + serverId), cancel.Token).ConfigureAwait(false);
				awaitingSignIn = true;
				auth = await RconWire.ReadAsync(stream, _o.SignInTimeout * 2, _o.SignInTimeout, cancel.Token).ConfigureAwait(false);
			}
			if (auth is not { Type: RconWire.Auth } first) return;
			awaitingSignIn = false;
			string refusal = await SignIn(session, address, first.Body, cancel.Token).ConfigureAwait(false);
			if (refusal == null && !(signedIn = Promote()))
			{
				_log.Warn($"rcon: {session.Caller} from {address} refused: all {_o.MaxClients} remote console slots are in use");
				session.Caller = null;
				refusal = $"All {_o.MaxClients} remote console slots on this server are in use; try again later.";
			}
			// The RESPONSE_VALUE before the AUTH_RESPONSE is protocol; its body carries the reason for clients that show one.
			await Send(stream, RconWire.Encode(first.Id, RconWire.Response, refusal ?? ""), cancel.Token).ConfigureAwait(false);
			if (refusal != null)
			{
				await Send(stream, RconWire.Encode(-1, RconWire.ExecOrAuthResponse, ""), cancel.Token).ConfigureAwait(false);
				return;
			}
			await Send(stream, RconWire.Encode(first.Id, RconWire.ExecOrAuthResponse, ""), cancel.Token).ConfigureAwait(false);
			while (!cancel.IsCancellationRequested)
			{
				var read = await RconWire.ReadAsync(stream, _o.IdleTimeout, _o.SignInTimeout, cancel.Token).ConfigureAwait(false);
				if (read is not { } packet) break;
				if (packet.Type == RconWire.Response)
				{
					await Send(stream, RconWire.Encode(packet.Id, RconWire.Response, ""), cancel.Token).ConfigureAwait(false);
					continue;
				}
				// A second AUTH on a signed-in connection, or an unknown type: nothing to do, no reply (as Source servers).
				if (packet.Type != RconWire.ExecOrAuthResponse) continue;
				string output = await Run(session, packet.Text).ConfigureAwait(false);
				foreach (byte[] chunk in RconWire.Chunks(packet.Id, output))
					await Send(stream, chunk, cancel.Token).ConfigureAwait(false);
				if (session.Done) break;
			}
		}
		catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException or TimeoutException or InvalidDataException) { }
		catch (Exception ex) { _log.Warn($"rcon: {address}: {ex.GetType().Name}: {ex.Message}"); }
		finally
		{
			_sessions.TryRemove(session, out _);
			if (awaitingSignIn && _lockout.Fail(address, _clock())) _log.Warn($"rcon: {address} locked out for {_o.LockoutFor.TotalMinutes:0} min after {_o.LockoutFailures} refused sign-ins");
			if (session.Caller != null) _log.Info($"rcon: {session.Caller} from {address} disconnected");
			if (session.SteamSession != 0)
			{
				try { _host.EndSteam(session.SteamSession); }
				catch (Exception ex) { _log.Warn("rcon: ending the Steam session failed: " + ex.Message); }
			}
			await CloseGently(client).ConfigureAwait(false);
			Release(address, signedIn);
		}
	}

	private async Task<ulong> ServerSteamId(CancellationToken cancel)
	{
		using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
		limit.CancelAfter(_o.SignInTimeout);
		try { return await _host.ServerSteamIdAsync(limit.Token).ConfigureAwait(false); }
		catch (OperationCanceledException) when (!cancel.IsCancellationRequested) { return 0; }
	}

	// Closing with unread bytes waiting (a client's end marker) resets the connection, and the client can lose the last reply
	// (a refusal reason). Finish sending, then read what is left for up to a second before closing.
	private static async Task CloseGently(TcpClient client)
	{
		try
		{
			client.Client.Shutdown(SocketShutdown.Send);
			using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(1));
			var sink = new byte[512];
			while (await client.Client.ReceiveAsync(sink, SocketFlags.None, limit.Token).ConfigureAwait(false) > 0) { }
		}
		catch (Exception ex) when (ex is SocketException or ObjectDisposedException or OperationCanceledException) { }
		finally { client.Dispose(); }
	}

	private async Task Send(NetworkStream stream, byte[] packet, CancellationToken cancel)
	{
		using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
		limit.CancelAfter(_o.WriteTimeout);
		await stream.WriteAsync(packet, limit.Token).ConfigureAwait(false);
	}

	/// <summary>Signs the session in and returns null, or the reason it is refused. The body (a password or ticket) is never logged.</summary>
	private async Task<string> SignIn(Session session, string address, byte[] body, CancellationToken cancel)
	{
		if (_lockout.LockedFor(address, _clock()) is TimeSpan wait)
		{
			_log.Warn($"rcon: {address} refused: locked out for another {Math.Ceiling(wait.TotalMinutes)} min");
			return $"Too many refused sign-ins from your address; try again in {Math.Ceiling(wait.TotalMinutes)} min.";
		}
		string why;
		RconCaller caller = null;
		string text = Encoding.UTF8.GetString(body);
		if (text.StartsWith("steam:", StringComparison.OrdinalIgnoreCase)) (why, caller) = await SignInSteam(session, address, text, cancel).ConfigureAwait(false);
		else if (_passwordHash == null) why = _o.SteamAuth ? "This server only takes Steam sign-in." : "RCON has no password set.";
		// Hashes compared in fixed time: neither the password nor its length leaks through the reply time.
		else if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(body), _passwordHash)) why = "Wrong password.";
		else { why = null; caller = new RconCaller { Address = address, Role = RconCaller.Owner }; }
		if (why != null)
		{
			bool locked = _lockout.Fail(address, _clock());
			_log.Warn($"rcon: {address} refused: {why}" + (locked ? $" (locked out for {_o.LockoutFor.TotalMinutes:0} min after {_o.LockoutFailures} refused sign-ins)" : ""));
			return why;
		}
		_lockout.Clear(address);
		session.Caller = caller;
		_log.Info($"rcon: {caller} signed in from {address}");
		return null;
	}

	private async Task<(string Why, RconCaller Caller)> SignInSteam(Session session, string address, string text, CancellationToken cancel)
	{
		if (!_o.SteamAuth) return ("Steam sign-in is off on this server (RconSteamAuth).", null);
		string[] parts = text.Split(':', 3);
		if (parts.Length != 3 || !ulong.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out ulong id)
			|| id == 0 || parts[2].Length is 0 or > 2048 || parts[2].Length % 2 != 0)
			return ("Malformed Steam sign-in.", null);
		byte[] ticket;
		try { ticket = Convert.FromHexString(parts[2]); }
		catch (FormatException) { return ("Malformed Steam sign-in.", null); }
		// The ticket first: whether an account is on the admin list is no one's business until Steam vouches for it.
		using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
		limit.CancelAfter(_o.SteamTimeout);
		string failed;
		try { failed = await _host.CheckSteamAsync(id, ticket, limit.Token).ConfigureAwait(false); }
		catch (OperationCanceledException) when (!cancel.IsCancellationRequested) { failed = $"Steam did not answer within {_o.SteamTimeout.TotalSeconds:0} s"; }
		if (failed != null) return ($"Steam did not confirm {id}: {failed}", null);
		session.SteamSession = id;
		string role = _host.RoleOf(id);
		string name = _host.NameOf(id);
		if (role == null) return ("The server's admin list is not loaded yet; try again in a moment.", null);
		var caller = new RconCaller { Address = address, SteamId = id, Name = name, Role = role };
		if (role != RconCaller.Owner && _host.CommandsFor(caller).Count == 0)
			return ($"{name} ({id}) has no remote console access on this server. The owner can put you in a group with rcon.connect (group set {id} admin).", null);
		return (null, caller);
	}

	private async Task<string> Run(Session session, string command)
	{
		var caller = session.Caller;
		command = command.Trim();
		if (RconAccess.Malformed(command) is { } bad) return "[WARN] " + bad;
		if (caller.IsSteam)
		{
			// Promotions and demotions take effect on the next command, not the next sign-in.
			string role = _host.RoleOf(caller.SteamId);
			if (role != null) caller.Role = role;
		}
		var allowed = caller.Role == RconCaller.Owner ? ["*"] : _host.CommandsFor(caller);
		if (allowed.Count == 0)
		{
			session.Done = true;
			return $"[WARN] {caller} has no remote console access any more.";
		}
		if (!RconAccess.Allowed(allowed, command)) return $"[WARN] '{RconAccess.Verb(command)}' is not allowed for {caller}.";
		// json is the tool's heartbeat (status, players and log every few seconds); logging it would drown the log.
		if (RconAccess.Verb(command) != "json") _log.Info($"rcon: {caller}: {command}");
		try { return await _host.ExecuteAsync(caller, command).ConfigureAwait(false); }
		catch (Exception ex) { return "[ERROR] " + ex.GetBaseException().Message; }
	}

	public void Dispose()
	{
		_stop.Cancel();
		try { _listener?.Stop(); } catch (SocketException) { }
	}
}
