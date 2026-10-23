using System.IO;
using Keen.Game2.Simulation.RuntimeSystems.Saves;
using Keen.VRage.DCS.Serialization;
using Keen.VRage.Library.Definitions;
using Keen.VRage.Library.Serialization;
using Keen.VRage.Library.Serialization.Binary;
using Keen.VRage.Library.Serialization.Binary.Archive;
using Keen.VRage.Library.Utils;

namespace Y51_SE2Online.Engine;

/// <summary>
/// The world session's state a joining player's game builds its own session from (identities, players, factions, time,
/// the rest of the session components' replicated state), written as the game writes a session save part: definitions
/// by id, entities as save references (the entities themselves arrive as object batches).
/// </summary>
internal static class OwnSnapshot
{
	public static byte[] Serialize(SessionComponentsSnapshot snapshot)
	{
		using var stream = new MemoryStream();
		using var context = new SerializationContext(stream, "SE2 Online world session", new EntityProxySerializationContext(),
			new DefinitionManagerDefinitionSerializationContext(Singleton<DefinitionManager>.Instance),
			new BinarySerializationParameters { MainChunkCompression = CompressionType.Brotli });
		SerializationHelper.Serialize(context, in snapshot, SerializerFormat.Binary);
		return stream.ToArray();
	}

	public static SessionComponentsSnapshot Deserialize(byte[] bytes)
	{
		using var stream = new MemoryStream(bytes, writable: false);
		using var context = new SerializationContext(stream, "SE2 Online world session", new EntityProxySerializationContext(),
			new DefinitionManagerDefinitionSerializationContext(Singleton<DefinitionManager>.Instance));
		return SerializationHelper.Deserialize<SessionComponentsSnapshot>(context, SerializerFormat.Binary);
	}
}
