using System;
using System.Collections.Concurrent;
using Steamworks;
using Y51_SE2Online.Core;
using Y51_SE2Online.Net;
using Y51_SE2Online.SyncEngine;

namespace Y51_SE2Online.Steam;

/// <summary>
/// Steam lobby invites and Steam launches from a friend's game on SE2 Online's own netcode. The game still makes a
/// private lobby for every hosted world, so Steam can deliver an invite as a lobby join; with the game's multiplayer off
/// the game ignores it. The invite is turned into the inviting friend's SE2 Online connect line (their rich presence).
/// "Join Game" clicks on a friend's rich presence are MpMenu's FriendFinder's.
/// </summary>
internal static class SteamJoins
{
	private static readonly ConcurrentQueue<ulong> Invites = new();

	private static Callback<GameLobbyJoinRequested_t>? _lobby;

	/// <summary>Main thread, once Steam is initialised.</summary>
	public static void Start()
	{
		_lobby ??= Callback<GameLobbyJoinRequested_t>.Create(r => Invites.Enqueue(r.m_steamIDFriend.m_SteamID));
	}

	/// <summary>Main thread (MpPlugin.Tick).</summary>
	public static void Tick()
	{
		if (Invites.IsEmpty || MpPlugin.Controller is not { Busy: false } || !Invites.TryDequeue(out ulong friend)) return;
		string connect;
		try { connect = SteamFriends.GetFriendRichPresence(new CSteamID(friend), "connect") ?? ""; }
		catch (Exception) { connect = ""; }
		if (Options(connect) is not { } options)
		{
			GameLog.Instance.Info($"steam friends: an invite from {friend} ignored: their game shows no SE2 Online session to join");
			MpMenu.Report("That invite is not to an SE2 Online session (the friend is not hosting or playing online).");
			return;
		}
		GameLog.Instance.Info($"steam friends: joining from an invite ({connect})");
		MpMenu.Activate(options);
	}

	/// <summary>The SE2 Online join a Steam launch carries (a friend's connect line as launch options), if any.</summary>
	public static MpOptions? LaunchJoin(string[] args) => Options(string.Join(" ", args));

	private static MpOptions? Options(string line)
	{
		try
		{
			if (ConnectString.TryParseAddress(line, out string address)) return MpMenu.Settings.JoinAddressOptions(address);
			if (ConnectString.TryParse(line, out ulong host, out int? port)) return MpMenu.Settings.JoinOptions(host, false, port ?? MpOptions.DefaultVirtualPort);
		}
		catch (Exception ex) when (ex is FormatException or ArgumentException)
		{
			GameLog.Instance.Warn($"steam friends: the join line '{line}' cannot be used ({ex.Message})");
		}
		return null;
	}
}
