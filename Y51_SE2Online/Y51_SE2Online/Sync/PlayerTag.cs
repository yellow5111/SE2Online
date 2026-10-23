using System.Collections.Generic;
using System.Linq;

namespace Y51_SE2Online.Sync;

public readonly record struct PlayerTag(ushort ClientId, uint NetId, string Name)
{
	/// <summary>Parked characters of offline players have no ClientId; their tags are keyed from here up, a range HostSession never hands out.</summary>
	public const ushort OfflineBase = 0xF000;

	public const string OfflineSuffix = " (offline)";

	public static bool IsOffline(ushort clientId) => clientId >= OfflineBase;

	/// <summary>
	/// The host's name tag list: online players with their suit broadcast on, then every alive character owned by a known
	/// player who is not online (their parked body), unless that player's last broadcast state was off.
	/// </summary>
	public static List<PlayerTag> Build(bool nametags, IEnumerable<(ushort ClientId, uint NetId, long Identity, string Name)> online,
		IEnumerable<(uint NetId, long Owner)> bodies, IReadOnlyDictionary<long, string> names, ISet<long> silent, long hostIdentity = 0)
	{
		var tags = new List<PlayerTag>();
		if (!nametags) return tags;
		// A co-op host is always online, also before its own entry reaches the list.
		var onlineIds = new HashSet<long>();
		if (hostIdentity != 0) onlineIds.Add(hostIdentity);
		foreach (var p in online)
		{
			onlineIds.Add(p.Identity);
			if (p.NetId != 0 && !silent.Contains(p.Identity)) tags.Add(new PlayerTag(p.ClientId, p.NetId, p.Name));
		}
		ushort key = OfflineBase;
		foreach (var (netId, owner) in bodies.OrderBy(b => b.Owner).ThenBy(b => b.NetId))
		{
			if (key == ushort.MaxValue) break;
			if (netId == 0 || !PlayerIdentities.IsHuman(owner) || onlineIds.Contains(owner) || silent.Contains(owner)) continue;
			if (!names.TryGetValue(owner, out string name)) continue;
			tags.Add(new PlayerTag(key++, netId, name + OfflineSuffix));
		}
		return tags;
	}
}
