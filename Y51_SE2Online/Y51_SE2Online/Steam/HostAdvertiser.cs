using System;
using Y51_SE2Online.Core;
using Y51_SE2Online.Net;
using Steamworks;

namespace Y51_SE2Online.Steam;

internal sealed class HostAdvertiser
{
	private readonly IMpLog _log;

	private readonly ulong _me;

	private readonly int _vport;

	private readonly ushort _syncVersion;

	private readonly string _pluginVersion;

	private string? _current;

	public const string PingKey = "y51online_ping";

	private string _ping = "";

	private long _nextPingMs;

	private bool _failed;

	public HostAdvertiser(IMpLog log, ulong me, int virtualPort, ushort syncVersion, string pluginVersion)
	{
		_log = log;
		_me = me;
		_vport = virtualPort;
		_syncVersion = syncVersion;
		_pluginVersion = pluginVersion;
	}

	public void Update(bool worldLoaded, string worldName)
	{
		if (_failed)
		{
			return;
		}
		try
		{
			string text = (worldLoaded ? new HostPresence(_syncVersion, _pluginVersion, _vport, worldName).Encode() : null);
			if (!(text == _current))
			{
				if (text == null)
				{
					Set("y51online_host", null);
					Set("connect", null);
					Set("status", null);
					Set(PingKey, null);
					_ping = "";
					_log.Info("steam presence: no world loaded: 'Join Game' removed from your Steam profile");
				}
				else
				{
					string value = ConnectString.Build(_me, _vport);
					bool flag = Set("y51online_host", text) & Set("connect", value) & Set("status", HostPresence.Clean("Hosting SE2 multiplayer (Y51_SE2Online): " + worldName, 120));
					_log.Info($"steam presence: advertising world '{worldName}' to Steam friends{(flag ? "" : " (Steam refused a key, see above)")}: {"y51online_host"}='{text}', connect='{value}'. Friends join with Join-Friend.bat, -y51online:join:friends, " + "or 'Join Game' in the Steam friends list (needs -plugins:... in their SE2 Steam launch options)");
				}
				_current = text;
			}
			// Steam measures the location a few seconds after start and refines it later; friends estimate their ping from it.
			long now = MpClock.NowMs();
			if (text != null && now >= _nextPingMs)
			{
				_nextPingMs = now + 30000;
				string ping = SteamPing.ForPresence(SteamPing.Local(server: false));
				if (ping.Length > 0 && ping != _ping && Set(PingKey, ping)) _ping = ping;
			}
		}
		catch (Exception ex)
		{
			_failed = true;
			_log.Warn($"steam presence: SetRichPresence failed ({ex.GetType().Name}: {ex.Message}); friends must join with your SteamID64 {_me}");
		}
	}

	public void Clear(string why)
	{
		if (_current == null || _failed)
		{
			return;
		}
		try
		{
			Set("y51online_host", null);
			Set("connect", null);
			Set("status", null);
			Set(PingKey, null);
			_ping = "";
			_current = null;
			_log.Info("steam presence: removed (" + why + ")");
		}
		catch (Exception ex)
		{
			_log.Warn($"steam presence: removing the keys failed ({ex.GetType().Name}: {ex.Message})");
		}
	}

	private bool Set(string key, string? value)
	{
		bool flag = SteamFriends.SetRichPresence(key, value ?? "");
		if (!flag)
		{
			_log.WarnOnce("rp-set-" + key, "steam presence: SetRichPresence('" + key + "') returned false");
		}
		return flag;
	}
}
