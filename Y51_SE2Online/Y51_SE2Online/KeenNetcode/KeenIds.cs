using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.Components;
using Keen.VRage.Multiplayer.Transport;
using Keen.VRage.Multiplayer.Transport.OutOfProcess;
using Keen.VRage.Multiplayer.Transport.OutOfProcess.Client;
using Keen.VRage.Multiplayer.Transport.OutOfProcess.Server;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>
/// Entity ids SE2 Online's messages share between host and players: Keen's own network ids. The server keeps one id per
/// replicated entity for every connection, and each client resolves it to its copy, so no id table travels on our side.
/// Read on the session thread that replicates (server tick / client session update).
/// </summary>
internal sealed class KeenIds(Session session, bool server)
{
	private NetworkIdMapper? Mapper
	{
		get
		{
			OutOfProcessPeerTransportSessionComponent? transport = server
				? session.SessionComponents.TryGet<OutOfProcessServerPeerTransportSessionComponent>()
				: session.SessionComponents.TryGet<OutOfProcessClientPeerTransportSessionComponent>();
			return transport?.NetworkIdMapper;
		}
	}

	/// <summary>The entity's id, or 0 when Keen has not replicated it to anyone (no player can refer to it then).</summary>
	public uint GetOrAssign(Entity entity) => TryGet(entity, out uint id) ? id : 0;

	public bool TryGet(Entity entity, out uint id)
	{
		id = Mapper?.TryGetId(entity) is NetworkId nid && nid.Value <= uint.MaxValue ? (uint)nid.Value : 0;
		return id != 0;
	}

	public bool TryGet(uint id, out Entity entity)
	{
		entity = id == 0 ? null : Mapper?.TryGetObject(new NetworkId(id)) as Entity;
		return entity != null;
	}
}
