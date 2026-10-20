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

	// Steam caps a game server rule value well below a lobby value; both get the same chunked text.
	private const int Chunk = 200;

	private static string? _published;

	/// <summary>The host's mod list as published text ("" for none); same-PC and direct IP players read it from the host itself.</summary>
	public static string Current { get; private set; } = "";

	/// <summary>Asks for the mod list of a game on this PC: an 8-byte all-ones account id instead of a player's.</summary>
	public const ulong LocalQuery = ulong.MaxValue;

	public static string Encode(IReadOnlyList<WorldMod> mods)
	{
		if (mods.Count == 0) return "";
		var w = new WireWriter();
		WorldMods.Write(w, mods);
		return Convert.ToBase64String(w.ToArray());
	}

	public static List<WorldMod> Decode(string text)
	{
		if (string.IsNullOrEmpty(text)) return new List<WorldMod>();
		var r = new WireReader(Convert.FromBase64String(text));
		return WorldMods.Read(ref r);
	}

	/// <summary>Main thread or server thread: keeps the published list on the hosted lobby or the game server's rules.</summary>
	public static void Publish(IReadOnlyList<WorldMod> mods, bool dedicated)
	{
		string text = Encode(mods);
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

	private static async System.Threading.Tasks.Task<List<WorldMod>?> RulesAsync(IPEndPoint endpoint, CancellationToken stop)
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
				if (finished != done.Task || !done.Task.Result) return null;
			}
		}
		finally
		{
			SteamMatchmakingServers.CancelServerQuery(query);
			GC.KeepAlive(response);
		}
		if (!rules.TryGetValue(Key, out string count) || !int.TryParse(count, out int parts)) return new List<WorldMod>();
		var text = new System.Text.StringBuilder();
		for (int i = 0; i < parts; i++)
		{
			if (!rules.TryGetValue(Key + i, out string part)) return null;
			text.Append(part);
		}
		return Decode(text.ToString());
	}
}
