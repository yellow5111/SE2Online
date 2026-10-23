using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.Components;
using Keen.VRage.Multiplayer.Transport;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>
/// Entity ids SE2 Online's messages share between host and players: the network ids SE2 Online's netcode gives the
/// session's replicated objects (Engine/OwnIds). The server keeps one id per replicated entity for every player, and
/// each player's game resolves it to its copy, so no id table travels for these messages.
/// Read on the session thread that replicates (server tick / client session update).
/// </summary>
internal sealed class SessionIds(Session session, bool server)
{
	private Engine.OwnIds? Ids => Engine.OwnHost.Existing(session)?.Ids;

	public bool IsServer => server;

	/// <summary>The entity's id, or 0 when it has not been replicated to anyone (no player can refer to it then).</summary>
	public uint GetOrAssign(Entity entity) => TryGet(entity, out uint id) ? id : 0;

	public bool TryGet(Entity entity, out uint id)
	{
		id = Ids?.TryGetId(entity) is NetworkId nid && nid.Value <= uint.MaxValue ? (uint)nid.Value : 0;
		return id != 0;
	}

	public bool TryGet(uint id, out Entity entity)
	{
		entity = id == 0 ? null! : Ids?.TryGetObject(new NetworkId(id)) as Entity ?? null!;
		return entity != null;
	}
}
