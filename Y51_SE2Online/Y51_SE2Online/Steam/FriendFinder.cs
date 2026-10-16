using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Y51_SE2Online.Core;
using Y51_SE2Online.Net;
using Steamworks;

namespace Y51_SE2Online.Steam;

internal sealed class FriendFinder : IDisposable
{
	private readonly IMpLog _log;

	private readonly ConcurrentQueue<(ulong From, string Connect)> _joinRequests = new ConcurrentQueue<(ulong, string)>();

	private readonly ConcurrentQueue<ulong> _presenceUpdates = new ConcurrentQueue<ulong>();

	private Callback<GameRichPresenceJoinRequested_t>? _joinCallback;

	private Callback<FriendRichPresenceUpdate_t>? _presenceCallback;

	private readonly Dictionary<ulong, long> _requestedAt = new Dictionary<ulong, long>();

	private uint _appId;

	public long PresenceUpdates;

	public long JoinRequests;

	public FriendFinder(IMpLog log)
	{
		_log = log;
	}

	public void Start()
	{
		_joinCallback = Callback<GameRichPresenceJoinRequested_t>.Create((GameRichPresenceJoinRequested_t r) =>
		{
			_joinRequests.Enqueue((r.m_steamIDFriend.m_SteamID, r.m_rgchConnect ?? ""));
		});
		_presenceCallback = Callback<FriendRichPresenceUpdate_t>.Create((FriendRichPresenceUpdate_t u) =>
		{
			if (u.m_nAppID.m_AppId == _appId || _appId == 0)
			{
				_presenceUpdates.Enqueue(u.m_steamIDFriend.m_SteamID);
			}
		});
		try
		{
			_appId = SteamUtils.GetAppID().m_AppId;
		}
		catch (Exception)
		{
			_appId = 0u;
		}
		_log.Info($"steam friends: listening for 'Join Game' clicks and friends' Y51_SE2Online host presence (app {_appId})");
	}

	public bool TryTakeJoinRequest(out ulong from, out string connect)
	{
		if (_joinRequests.TryDequeue(out (ulong, string) result))
		{
			JoinRequests++;
			(from, connect) = result;
			return true;
		}
		from = 0uL;
		connect = "";
		return false;
	}

	public bool TakePresenceChanged()
	{
		bool changed = false;
		while (_presenceUpdates.TryDequeue(out _))
		{
			changed = true;
			PresenceUpdates++;
		}
		return changed;
	}

	public List<FriendHost> Scan(long nowMs, out int friendsTotal, out int inGame)
	{
		List<FriendHost> list = new List<FriendHost>();
		friendsTotal = SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate);
		inGame = 0;
		for (int i = 0; i < friendsTotal; i++)
		{
			CSteamID friendByIndex = SteamFriends.GetFriendByIndex(i, EFriendFlags.k_EFriendFlagImmediate);
			if (SteamFriends.GetFriendGamePlayed(friendByIndex, out var pFriendGameInfo) && (_appId == 0 || pFriendGameInfo.m_gameID.AppID().m_AppId == _appId))
			{
				inGame++;
				if (!_requestedAt.TryGetValue(friendByIndex.m_SteamID, out var value) || nowMs - value > 60000)
				{
					_requestedAt[friendByIndex.m_SteamID] = nowMs;
					SteamFriends.RequestFriendRichPresence(friendByIndex);
				}
				string friendRichPresence = SteamFriends.GetFriendRichPresence(friendByIndex, "y51online_host");
				HostPresence.TryDecode(friendRichPresence, out HostPresence presence);
				string ping = presence == null ? "" : SteamFriends.GetFriendRichPresence(friendByIndex, HostAdvertiser.PingKey) ?? "";
				list.Add(new FriendHost(friendByIndex.m_SteamID, SteamFriends.GetFriendPersonaName(friendByIndex) ?? "?", presence, string.IsNullOrEmpty(friendRichPresence) ? null : friendRichPresence, ping.Length <= DirectoryListing.MaxPingLocation ? ping : ""));
			}
		}
		return list;
	}

	public string LaunchCommandLine()
	{
		try
		{
			string pszCommandLine;
			return (SteamApps.GetLaunchCommandLine(out pszCommandLine, 1024) > 0) ? (pszCommandLine ?? "") : "";
		}
		catch (Exception)
		{
			return "";
		}
	}

	public void Dispose()
	{
		_joinCallback?.Dispose();
		_presenceCallback?.Dispose();
	}
}
