using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Y51_SE2Online.Net;

namespace Y51_SE2Online.Sync;

public sealed partial class HostSession
{
	private readonly long _startedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
	private readonly Dictionary<PeerId, long> _infoAsked = new();

	public bool HasPassword => !string.IsNullOrEmpty(_o.Password);

	private void HandleServerInfo(RemoteClient c)
	{
		if (c.Status != ClientStatus.Synced) return;
		long now = _clock();
		lock (_infoAsked)
		{
			if (_infoAsked.TryGetValue(c.Peer, out long last) && now - last < 2000) return;
			_infoAsked[c.Peer] = now;
		}
		_t.Send(c.Peer, Channel.Control, SocialMessage.Encode(SyncMsg.ServerInfo, new ServerInfoMessage(ServerInfoRows())));
	}

	/// <summary>Every setting players may want to know before or while playing, for the Server info screen.</summary>
	public ServerInfoRow[] ServerInfoRows()
	{
		var rows = new List<ServerInfoRow>();
		void Add(string section, string name, string value) => rows.Add(new ServerInfoRow(section, name, value));
		int online = PlayerTable().Count(p => p.Connected);
		Add("Server", "Name", _o.SessionName);
		Add("Server", "Type", _o.Dedicated ? "Dedicated server" : "Hosted by " + _o.HostName);
		Add("Server", "Players", online + " / " + _o.MaxPlayers);
		Add("Server", "Password", string.IsNullOrEmpty(_o.Password) ? "No" : "Yes");
		Add("Server", "Whitelist only", _o.Access.WhitelistOnly ? "Yes" : "No");
		Add("Server", "Uptime", ServerInfo.Duration(TimeSpan.FromMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _startedUnixMs)));
		Add("Server", "Version", $"Y51 SE2 Online {_o.PluginVersion}, protocol {SyncProtocol.Version}, SE2 {_o.GameVersion}");
		Add("Rules", "PvP", ServerInfo.OnOff(Pvp));
		Add("Rules", "Offline raid protection", ServerInfo.OnOff(OfflineRaidProtection));
		Add("Rules", "PCU per player", _o.PcuInForce == 0 ? "Unlimited" : _o.PcuInForce.ToString("N0", CultureInfo.InvariantCulture));
		if (_o.Access.WorldPcu is int world) Add("Rules", "World PCU setting", world.ToString("N0", CultureInfo.InvariantCulture));
		var usage = ResourceMultipliers.Current;
		if (!usage.IsDefault) Add("Rules", "Resource usage", $"Energy {ResourceMultipliers.Format(usage.Energy)}, hydrogen {ResourceMultipliers.Format(usage.Hydrogen)}, oxygen {ResourceMultipliers.Format(usage.Oxygen)}");
		var sun = _o.SunInForce;
		Add("World", "Sun", sun.Rotation ? $"Moving, one day = {sun.PeriodMinutes:0.#} min" : "Fixed");
		Add("World", "Streaming radius", _o.StreamingRadiusMeters > 0 ? $"{_o.StreamingRadiusMeters / 1000:0.#} km" : "Off (whole world)");
		Add("World", "Water sync", WaterSync ? "On (experimental)" : "Off");
		rows.AddRange(_world.ServerInfo());
		string motd = MotdTemplate;
		Add("Message of the day", "Text", string.IsNullOrWhiteSpace(motd) ? "None" : motd.Replace('\n', ' '));
		return rows.Take(ServerInfo.MaxRows).ToArray();
	}
}
