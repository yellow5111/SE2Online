using System.Collections.Generic;
using Y51_SE2Online.Net;

namespace Y51_SE2Online.Sync;

public interface IHostWorld
{
	void OnSocialRequest(PeerId peer, ushort clientId, SocialRequest request) { }
	void OnAdminRequest(PeerId? peer, ushort clientId, AdminRequest request) { }
	void OnGpsShare(ushort fromClientId, string fromName, GpsShare share) { }
	void OnPunch(ushort fromClientId, ushort targetClientId) { }
	/// <summary>Network thread: a player's own character inputs (<see cref="OwnInputs"/>).</summary>
	void OnOwnMotion(ushort clientId, byte[] inputs) { }
	void OnTrade(ushort clientId, byte[] data) { }
	void OnNotSeated(ushort clientId) { }
	void OnPossessed(ushort clientId) { }
	/// <summary>Where a player's character is for proximity voice, from a server-thread snapshot; false if unknown.</summary>
	bool TryVoicePosition(ushort clientId, out Vec3D position) { position = default; return false; }
	ServerInfoRow[] ServerInfo() => System.Array.Empty<ServerInfoRow>();
	/// <summary>The mods the running world loaded; players load the same set.</summary>
	IReadOnlyList<WorldMod> Mods => System.Array.Empty<WorldMod>();
	/// <summary>The world runs missions and contracts (a player's game builds its mission parts then).</summary>
	bool HostMissions => false;
	/// <summary>Network thread: an accepted player's game builds the world and asks for its session snapshot (once per connection).</summary>
	void SnapshotRequested(PeerId peer, ushort clientId) { }
	/// <summary>Network thread: a player's connection closed (left, kicked, lost); its game's place on the server goes.</summary>
	void PeerLeft(PeerId peer) { }
	bool WorldReady { get; }

	string WorldName { get; }

	void RequestCapture(PeerId client);

	void CancelCapture(PeerId client);

	/// <summary>The join was refused; SE2 4.0 also refuses the player at the game's own handshake.</summary>
	void Rejected(PeerId peer, string why) { }

	void RequestCharacter(PeerId client, ushort clientId, string name, int choice, bool respawn)
	{
	}

	void ReleaseCharacter(PeerId client, ushort clientId)
	{
	}

	void OnClientStory(PeerId client, ushort clientId, StoryMove move)
	{
	}

	void ForgetStories(PeerId client)
	{
	}

	void RegisterPlayer(PeerId client, ushort clientId, string identityKey, string name)
	{
	}

	void OnObservableEdits(PeerId client, ushort clientId, IReadOnlyList<MemberEdit> edits)
	{
	}
}
