using System.Collections.Generic;
using Y51_SE2Online.Net;

namespace Y51_SE2Online.Sync;

public interface IHostWorld
{
	void OnSocialRequest(PeerId peer, ushort clientId, SocialRequest request) { }
	void OnAdminRequest(PeerId? peer, ushort clientId, AdminRequest request) { }
	void OnGpsShare(ushort fromClientId, string fromName, GpsShare share) { }
	void OnPunch(ushort fromClientId, ushort targetClientId) { }
	void OnTrade(ushort clientId, byte[] data) { }
	/// <summary>Where a player's character is for proximity voice, from a server-thread snapshot; false if unknown.</summary>
	bool TryVoicePosition(ushort clientId, out Vec3D position) { position = default; return false; }
	ServerInfoRow[] ServerInfo() => System.Array.Empty<ServerInfoRow>();
	/// <summary>The mods the running world loaded; joiners load the same set.</summary>
	IReadOnlyList<WorldMod> Mods => System.Array.Empty<WorldMod>();
	bool WorldReady { get; }

	string WorldName { get; }

	void RequestCapture(PeerId client);

	void CancelCapture(PeerId client);

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
