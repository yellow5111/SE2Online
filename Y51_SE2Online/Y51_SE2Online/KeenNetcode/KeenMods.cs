using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using Keen.Game2.Game.RuntimeSystems.GameSessions;
using Keen.VRage.Core.Services.GameSessions;
using Keen.VRage.Steam.GameSessions;
using Steamworks;
using Y51_SE2Online.Core;
using Y51_SE2Online.Net;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>
/// SE2 4.0's own join loads the world with whatever mods this game already has: a network client never asks the host.
/// The host therefore publishes its Workshop mods before anyone connects (Steam lobby data for a hosted world, Steam game
/// server rules for a dedicated server), and a player reads them, downloads what is missing and switches projects first.
/// </summary>
internal static class KeenMods
{
	private const string Key = "y51mods";

	// Steam cuts game server rule values short (about 127 characters): 200-character chunks arrived truncated, the
	// joined text was no longer Base64 and joins hung on "Connecting".
	private const int Chunk = 100;

	private static string? _published;

	/// <summary>The host's mod list as published text ("" for none); same-PC and direct IP players read it from the host itself.</summary>
	public static string Current { get; private set; } = "";

	/// <summary>Asks for the mod list of a game on this PC: an 8-byte all-ones account id instead of a player's.</summary>
	public const ulong LocalQuery = ulong.MaxValue;

	// After the mod list: the host's world runs SE2's missions and contracts. Older players stop reading at the list.
	private const byte MissionsMark = 0x4D;

	/// <summary>Whether the text read last (the server a player is about to join) runs missions.</summary>
	public static bool LastMissions { get; private set; }

	public static string Encode(IReadOnlyList<WorldMod> mods, bool missions = false)
	{
		if (mods.Count == 0 && !missions) return "";
		var w = new WireWriter();
		WorldMods.Write(w, mods);
		if (missions) w.U8(MissionsMark);
		return Convert.ToBase64String(w.ToArray());
	}

	public static List<WorldMod> Decode(string text)
	{
		LastMissions = false;
		if (string.IsNullOrEmpty(text)) return new List<WorldMod>();
		var r = new WireReader(Convert.FromBase64String(text));
		var mods = WorldMods.Read(ref r);
		LastMissions = r.Remaining >= 1 && r.U8() == MissionsMark;
		return mods;
	}

	/// <summary>Main thread or server thread: keeps the published list on the hosted lobby or the game server's rules.</summary>
	public static void Publish(IReadOnlyList<WorldMod> mods, bool dedicated, bool missions)
	{
		string text = Encode(mods, missions);
		Current = text;
		if (text == _published) return;
		try
		{
			if (dedicated)
			{
				int parts = (text.Length + Chunk - 1) / Chunk;
				SteamGameServer.SetKeyValue(Key, parts.ToString());
				for (int i = 0; i < parts; i++) SteamGameServer.SetKeyValue(Key + i, text.Substring(i * Chunk, Math.Min(Chunk, text.Length - i * Chunk)));
			}
			else if (KeenNet.LobbyId is CSteamID lobby)
			{
				SteamMatchmaking.SetLobbyData(lobby, Key, text);
			}
			else return;
			_published = text;
		}
		catch (InvalidOperationException) { }
	}

	public static void ResetPublished()
	{
		_published = null;
		Current = "";
	}

	/// <summary>The server's name in the player's mod consent file, as SE2 Online 2.4 wrote it: address, or the host's SteamID.</summary>
	public static string Label(GameSessionID session) => session switch
	{
		GameSessionIPEndpoint ip => ip.IPEndpoint.ToString(),
		SteamLobbyID lobby when SteamMatchmaking.GetLobbyOwner(lobby.LobbySteamID) is { } owner && owner.IsValid() => owner.m_SteamID.ToString(),
		_ => session.ToString()
	};

	/// <summary>The mods a Keen session publishes; null when it does not answer (no SE2 Online host, or offline).</summary>
	public static async System.Threading.Tasks.Task<List<WorldMod>?> QueryAsync(GameSessionID session, CancellationToken stop)
	{
		if (session is SteamLobbyID lobby)
		{
			SteamMatchmaking.RequestLobbyData(lobby.LobbySteamID);
			for (int i = 0; i < 50 && !stop.IsCancellationRequested; i++)
			{
				string text = SteamMatchmaking.GetLobbyData(lobby.LobbySteamID, Key);
				// The lobby's join code is set as soon as it exists; once it reads, our key is final too (empty: no mods).
				if (text.Length > 0 || SteamMatchmaking.GetLobbyData(lobby.LobbySteamID, "code").Length > 0) return Decode(text);
				await System.Threading.Tasks.Task.Delay(100, stop);
			}
			return null;
		}
		if (session is GameSessionIPEndpoint ip) return await RulesAsync(ip.IPEndpoint, stop);
		// A game on this PC answers on its loopback port (a co-op host has no server rules).
		if (session is LocalSessionID local) return await LocalAsync(local.Endpoint, stop) ?? await RulesAsync(local.Endpoint, stop);
		if (session is SteamIpSessionID direct) return await DirectIpSessions.ModsAsync(direct.Endpoint, stop);
		return null;
	}

	private static async System.Threading.Tasks.Task<List<WorldMod>?> LocalAsync(IPEndPoint endpoint, CancellationToken stop)
	{
		try
		{
			using var client = new System.Net.Sockets.TcpClient();
			using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
			timeout.CancelAfter(5000);
			await client.ConnectAsync(endpoint, timeout.Token);
			var stream = client.GetStream();
			await stream.WriteAsync(BitConverter.GetBytes(LocalQuery), timeout.Token);
			var length = new byte[4];
			await stream.ReadExactlyAsync(length, timeout.Token);
			var text = new byte[BitConverter.ToInt32(length)];
			await stream.ReadExactlyAsync(text, timeout.Token);
			return Decode(System.Text.Encoding.UTF8.GetString(text));
		}
		catch (Exception ex) when (ex is System.Net.Sockets.SocketException or OperationCanceledException or System.IO.IOException or System.IO.EndOfStreamException) { return null; }
	}

	// Steam's server query can fail within seconds while the server answers a plain A2S_RULES query fine; the player then
	// joined without the server's mods and the world failed to load on their content. The server is asked directly first,
	// Steam's query is the fallback.
	private static async System.Threading.Tasks.Task<List<WorldMod>?> RulesAsync(IPEndPoint endpoint, CancellationToken stop)
	{
		if (endpoint.Address.IsIPv4MappedToIPv6) endpoint = new IPEndPoint(endpoint.Address.MapToIPv4(), endpoint.Port);
		var failed = new List<string>();
		foreach (bool direct in new[] { true, false })
		{
			if (stop.IsCancellationRequested) break;
			string path = direct ? "a direct query" : "Steam's server query";
			// Steam's query takes an IPv4 address only.
			if (!direct && endpoint.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) { failed.Add(path + " skipped (not IPv4)"); break; }
			var (rules, why) = direct ? await DirectRulesAsync(endpoint, stop) : await SteamRulesAsync(endpoint, stop);
			List<WorldMod>? mods = null;
			if (rules != null && (mods = FromRules(rules, out string unread)) == null) why = unread;
			if (mods != null)
			{
				GameLog.Instance.Info($"client: read the mod list of {endpoint} through {path}" + (why.Length > 0 ? " (" + why + ")" : "") + (failed.Count > 0 ? " after " + string.Join("; ", failed) : ""));
				return mods;
			}
			failed.Add(path + " " + why);
		}
		GameLog.Instance.Info($"client: could not read the mod list of {endpoint}: " + string.Join("; ", failed));
		return null;
	}

	private static async System.Threading.Tasks.Task<(Dictionary<string, string>? Rules, string Why)> SteamRulesAsync(IPEndPoint endpoint, CancellationToken stop)
	{
		var rules = new Dictionary<string, string>();
		var done = new System.Threading.Tasks.TaskCompletionSource<bool>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
		var response = new ISteamMatchmakingRulesResponse((k, v) => rules[k] = v, () => done.TrySetResult(false), () => done.TrySetResult(true));
		byte[] b = endpoint.Address.GetAddressBytes();
		uint host = (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
		HServerQuery query = SteamMatchmakingServers.ServerRules(host, (ushort)endpoint.Port, response);
		try
		{
			using (stop.Register(() => done.TrySetResult(false)))
			{
				var finished = await System.Threading.Tasks.Task.WhenAny(done.Task, System.Threading.Tasks.Task.Delay(5000, stop));
				if (finished != done.Task) return (null, "timed out");
				if (!done.Task.Result) return (null, "failed");
			}
		}
		finally
		{
			SteamMatchmakingServers.CancelServerQuery(query);
			GC.KeepAlive(response);
		}
		return (rules, "");
	}

	// Asked again every second within the 3 s budget, as ServerPinger does: one lost datagram must not cost the list.
	private static async System.Threading.Tasks.Task<(Dictionary<string, string>? Rules, string Why)> DirectRulesAsync(IPEndPoint endpoint, CancellationToken stop)
	{
		var split = new List<byte[]>();
		try
		{
			using var udp = new System.Net.Sockets.UdpClient(endpoint.AddressFamily);
			if (OperatingSystem.IsWindows()) udp.Client.IOControl(-1744830452, new byte[4], null);
			udp.Connect(endpoint);
			using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
			timeout.CancelAfter(3000);
			byte[] ask = { 0xFF, 0xFF, 0xFF, 0xFF, (byte)'V', 0xFF, 0xFF, 0xFF, 0xFF };
			while (true)
			{
				await udp.SendAsync(ask, timeout.Token);
				using var round = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
				round.CancelAfter(1000);
				try
				{
					while (true)
					{
						byte[] reply = (await udp.ReceiveAsync(round.Token)).Buffer;
						bool joined = false;
						if (reply.Length >= 4 && BitConverter.ToInt32(reply, 0) == -2)
						{
							split.Add(reply);
							if (split.Count > 4 * WorldMods.MaxSplitPackets) return (null, "sent a split reply that never completed");
							if (WorldMods.JoinSplit(split) is not { } whole) continue;
							reply = whole;
							joined = true;
							split.Clear();
						}
						if (WorldMods.ParseA2sRules(reply, out byte[]? challenge) is { } rules) return (rules, joined ? "split reply" : "");
						if (challenge == null) return (null, joined ? "gave an unreadable split reply" : "gave an unreadable reply");
						challenge.CopyTo(ask, 5);
						break;
					}
				}
				catch (OperationCanceledException) when (!timeout.IsCancellationRequested) { }
			}
		}
		catch (OperationCanceledException) { return (null, split.Count > 0 ? "timed out on a split reply" : "timed out"); }
		catch (System.Net.Sockets.SocketException ex) { return (null, "failed (" + ex.SocketErrorCode + ")"); }
	}

	private static List<WorldMod>? FromRules(Dictionary<string, string> rules, out string why)
	{
		why = "";
		if (!rules.TryGetValue(Key, out string? count) || !int.TryParse(count, out int parts)) return new List<WorldMod>();
		var text = new System.Text.StringBuilder();
		for (int i = 0; i < parts; i++)
		{
			if (!rules.TryGetValue(Key + i, out string? part)) { why = $"lacked part {i + 1} of {parts}"; return null; }
			text.Append(part);
		}
		// A server from before the shorter chunks publishes a list that cannot be read: join with this game's mods.
		try { return Decode(text.ToString()); }
		catch (Exception ex) when (ex is FormatException or ArgumentException or IndexOutOfRangeException) { why = "gave a list that cannot be read"; return null; }
	}
}
