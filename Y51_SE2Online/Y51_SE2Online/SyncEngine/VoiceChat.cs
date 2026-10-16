using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using FMOD;
using HarmonyLib;
using Keen.VRage.Core;
using Keen.VRage.Library.Mathematics;
using Steamworks;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// Proximity voice: Steam captures and Opus-compresses the microphone (the same on Windows and Linux), the host relays
/// each frame to players in range with the speaker's position, and every listener plays each speaker as an FMOD 3D
/// user stream on the game's own FMOD core system, placed relative to the game's audio listener exactly like the
/// game's own 3D sounds (AudioEventInstance3D / FmodHelper.Get3DAttributes).
/// </summary>
internal static class VoiceChat
{
	private sealed class Speaker
	{
		public readonly short[] Ring;
		public int Read, Count;
		public Sound Sound;
		public Channel Channel;
		public bool Started;
		public Vec3D At;
		public long LastMs;
		public string Name = "";

		public Speaker(int samples) => Ring = new short[samples];
	}

	private const int PrebufferMs = 80, IdleReleaseMs = 4000, TalkingShownMs = 400;

	private static readonly ConcurrentQueue<(ushort Speaker, Vec3D At, byte[] Frame)> Inbox = new();
	private static readonly Dictionary<ushort, Speaker> Speakers = new();
	private static readonly Dictionary<nint, Speaker> BySound = new();
	// Kept in a static so the delegate FMOD calls from its stream thread is never collected.
	private static readonly SOUND_PCMREAD_CALLBACK ReadCallback = Read;
	private static readonly byte[] CaptureBuffer = new byte[8192];
	private static byte[] _pcm = new byte[48000];
	private static bool _installed, _recording, _failed;
	private static uint _rate;
	private static long _selfTalkingMs;
	private static object _audio;
	private static System.Reflection.FieldInfo _coreField;
	private static System.Reflection.PropertyInfo _listenerProperty;

	/// <summary>Names of the players heard (or this player) in the last moment, for the HUD.</summary>
	public static IReadOnlyList<string> Talking { get; private set; } = Array.Empty<string>();

	public static void Tick(bool inSession, bool free)
	{
		if (_failed) return;
		try
		{
			if (!_installed)
			{
				_installed = true;
				VoiceMessage.Received += (speaker, at, frame) => { if (Inbox.Count < 512) Inbox.Enqueue((speaker, at, frame)); };
			}
			var settings = MpMenu.Settings;
			bool active = inSession && settings.VoiceEnabled && MpPlugin.SteamAvailable && RadiusOn;
			Capture(active && (settings.VoiceOpenMic ? free && !ChatUi.InputOpen : PushToTalkHeld(settings.VoiceKey)));
			Play(active, settings);
		}
		catch (Exception ex)
		{
			_failed = true;
			GameLog.Instance.Error("voice: switched off for this game after an error (the game is unaffected): " + ex);
			try { Release(); } catch (Exception) { }
		}
	}

	/// <summary>Mutes or unmutes a player by name; returns the new state, or null if nobody of that name is online.</summary>
	public static bool? ToggleMute(string name)
	{
		// Names from the name tags carry the faction tag ("[TAG] name"); the player table does not.
		string bare = FactionTag.Strip(name?.Trim());
		var player = Players().FirstOrDefault(p => string.Equals(FactionTag.Strip(p.Name), bare, StringComparison.OrdinalIgnoreCase));
		if (player.Name == null) return null;
		var muted = MpMenu.Settings.MutedVoices ??= new List<string>();
		bool now = !muted.Contains(player.Name, StringComparer.OrdinalIgnoreCase);
		muted.RemoveAll(m => string.Equals(m, player.Name, StringComparison.OrdinalIgnoreCase));
		if (now) muted.Add(player.Name);
		MpMenu.SaveSettings(MpMenu.Settings);
		return now;
	}

	private static bool RadiusOn => AdminAccess.Hosting ? AdminAccess.Host.VoiceRadiusMeters > 0 : AdminAccess.Client?.Admin.VoiceRadius > 0;

	private static double Radius => AdminAccess.Hosting ? AdminAccess.Host.VoiceRadiusMeters : AdminAccess.Client?.Admin.VoiceRadius ?? 50;

	private static bool PushToTalkHeld(string binding) =>
		KeyBinding.TryParse(binding, out var key) && GameKeys.IsDown(key.Vk) && GameKeys.OwnWindowInFront();

	private static IEnumerable<(ushort ClientId, string Name)> Players() =>
		(AdminAccess.Hosting ? AdminAccess.Host.PlayerTable() : AdminAccess.Client?.Players.ToList() ?? new List<PlayerInfo>()).Select(p => (p.ClientId, p.Name));

	private static void Capture(bool talk)
	{
		if (talk != _recording)
		{
			_recording = talk;
			if (talk) SteamUser.StartVoiceRecording();
			else SteamUser.StopVoiceRecording();
		}
		// Steam keeps delivering the tail of a sentence after StopVoiceRecording, so the buffer is read either way.
		while (SteamUser.GetAvailableVoice(out uint size) == EVoiceResult.k_EVoiceResultOK && size > 0)
		{
			if (SteamUser.GetVoice(true, CaptureBuffer, (uint)CaptureBuffer.Length, out uint written) != EVoiceResult.k_EVoiceResultOK || written == 0) break;
			var frame = CaptureBuffer.AsSpan(0, (int)Math.Min(written, VoiceMessage.MaxFrameBytes)).ToArray();
			_selfTalkingMs = MpClock.NowMs();
			if (AdminAccess.Hosting) AdminAccess.Host.LocalVoice(frame);
			else AdminAccess.Client?.SendVoice(frame);
		}
	}

	private static void Play(bool active, OnlineSettings settings)
	{
		long now = MpClock.NowMs();
		if (!active)
		{
			while (Inbox.TryDequeue(out _)) { }
			if (Speakers.Count > 0) Release();
			Talking = Array.Empty<string>();
			return;
		}
		if (_rate == 0) _rate = Math.Clamp(SteamUser.GetVoiceOptimalSampleRate(), 11025u, 48000u);
		var names = Players().ToDictionary(p => p.ClientId, p => p.Name);
		while (Inbox.TryDequeue(out var item))
		{
			// Each speaker costs an FMOD stream and a ring buffer, so only ids in the player table get one.
			if (!names.TryGetValue(item.Speaker, out string name) || name == null) continue;
			if (settings.MutedVoices?.Contains(name, StringComparer.OrdinalIgnoreCase) == true) continue;
			var speaker = SpeakerFor(item.Speaker);
			if (speaker == null) return;
			speaker.At = item.At;
			speaker.LastMs = now;
			speaker.Name = name;
			Decode(speaker, item.Frame, VoiceLevel.Gain(settings.VoiceVolume));
		}
		if (!TryListener(out WorldTransform listener)) return;
		float max = (float)Math.Max(Radius, VoiceRelay.MinRadius);
		foreach (var (id, speaker) in Speakers.ToList())
		{
			if (now - speaker.LastMs > IdleReleaseMs) { Remove(id); continue; }
			lock (speaker)
			{
				if (!speaker.Started && speaker.Count >= _rate * PrebufferMs / 1000)
				{
					speaker.Started = true;
					speaker.Channel.setPaused(false);
				}
			}
			var relative = WorldTransform.GetRelativeTransform(new WorldTransform(new Vector3D(speaker.At.X, speaker.At.Y, speaker.At.Z), Quaternion.Identity), listener).Position;
			var position = new VECTOR { x = (float)relative.X, y = (float)relative.Y, z = (float)relative.Z };
			var velocity = new VECTOR();
			speaker.Channel.set3DAttributes(ref position, ref velocity);
			speaker.Channel.set3DMinMaxDistance(VoiceLevel.FullVolumeMeters, max);
		}
		var talking = Speakers.Values.Where(s => now - s.LastMs < TalkingShownMs).Select(s => s.Name);
		if (now - _selfTalkingMs < TalkingShownMs) talking = talking.Prepend("You");
		Talking = talking.ToArray();
	}

	private static void Decode(Speaker speaker, byte[] frame, float gain)
	{
		EVoiceResult result;
		uint written;
		while ((result = SteamUser.DecompressVoice(frame, (uint)frame.Length, _pcm, (uint)_pcm.Length, out written, _rate)) == EVoiceResult.k_EVoiceResultBufferTooSmall && _pcm.Length < 1 << 20)
			_pcm = new byte[_pcm.Length * 2];
		if (result != EVoiceResult.k_EVoiceResultOK) return;
		int samples = (int)written / 2;
		lock (speaker)
		{
			var ring = speaker.Ring;
			for (int i = 0; i < samples; i++)
			{
				// Too far behind (a stall or a burst): drop the oldest audio rather than grow the delay.
				if (speaker.Count == ring.Length) { speaker.Read = (speaker.Read + 1) % ring.Length; speaker.Count--; }
				ring[(speaker.Read + speaker.Count) % ring.Length] = VoiceLevel.Apply((short)(_pcm[2 * i] | _pcm[2 * i + 1] << 8), gain);
				speaker.Count++;
			}
		}
	}

	private static Speaker SpeakerFor(ushort id)
	{
		if (Speakers.TryGetValue(id, out var speaker)) return speaker;
		if (!TryCore(out FMOD.System core)) return null;
		speaker = new Speaker((int)_rate);
		var info = new CREATESOUNDEXINFO
		{
			cbsize = Marshal.SizeOf<CREATESOUNDEXINFO>(),
			numchannels = 1,
			defaultfrequency = (int)_rate,
			format = SOUND_FORMAT.PCM16,
			length = _rate * 2 * 10,
			decodebuffersize = _rate / 50
		};
		info.pcmreadcallback = ReadCallback;
		// FMOD's default inverse rolloff halves the level at twice the min distance (-14 dB at 10 m); linear-square
		// stays at full volume within FullVolumeMeters and fades to silence at the voice radius.
		RESULT result = core.createSound(IntPtr.Zero, MODE.OPENUSER | MODE.LOOP_NORMAL | MODE.CREATESTREAM | MODE._3D | MODE._3D_LINEARSQUAREROLLOFF, ref info, out Sound sound);
		if (result != RESULT.OK) throw new InvalidOperationException("FMOD createSound for voice: " + result);
		lock (BySound) BySound[sound.handle] = speaker;
		speaker.Sound = sound;
		result = core.playSound(sound, default, true, out Channel channel);
		if (result != RESULT.OK) throw new InvalidOperationException("FMOD playSound for voice: " + result);
		speaker.Channel = channel;
		Speakers[id] = speaker;
		return speaker;
	}

	// FMOD's stream thread: hand over what has arrived, silence for the rest.
	private static RESULT Read(nint sound, nint data, uint length)
	{
		Speaker speaker;
		lock (BySound) BySound.TryGetValue(sound, out speaker);
		int wanted = (int)length / 2, copied = 0;
		if (speaker != null)
		{
			lock (speaker)
			{
				if (speaker.Started)
				{
					while (copied < wanted && speaker.Count > 0)
					{
						int run = Math.Min(Math.Min(wanted - copied, speaker.Count), speaker.Ring.Length - speaker.Read);
						Marshal.Copy(speaker.Ring, speaker.Read, data + copied * 2, run);
						speaker.Read = (speaker.Read + run) % speaker.Ring.Length;
						speaker.Count -= run;
						copied += run;
					}
					// Ran dry: wait for a fresh cushion so one late packet does not chop every word.
					if (speaker.Count == 0) speaker.Started = false;
				}
			}
		}
		for (int i = copied * 2; i < wanted * 2; i++) Marshal.WriteByte(data, i, 0);
		return RESULT.OK;
	}

	private static void Remove(ushort id)
	{
		if (!Speakers.Remove(id, out var speaker)) return;
		speaker.Channel.stop();
		lock (BySound) BySound.Remove(speaker.Sound.handle);
		speaker.Sound.release();
	}

	private static void Release()
	{
		foreach (ushort id in Speakers.Keys.ToList()) Remove(id);
		if (_recording) { _recording = false; SteamUser.StopVoiceRecording(); }
	}

	private static bool TryAudio()
	{
		if (_audio != null) return true;
		var type = AccessTools.TypeByName("Keen.VRage.Audio.AudioManager");
		var singleton = type == null ? null : AccessTools.Property(type.BaseType, "Instance");
		_audio = singleton?.GetValue(null);
		_coreField ??= type == null ? null : AccessTools.Field(type, "_coreSystem");
		_listenerProperty ??= type == null ? null : AccessTools.Property(type, "ListenerTransform");
		return _audio != null && _coreField != null && _listenerProperty != null;
	}

	private static bool TryCore(out FMOD.System core)
	{
		core = default;
		if (!TryAudio()) return false;
		core = (FMOD.System)_coreField.GetValue(_audio);
		return core.hasHandle();
	}

	private static bool TryListener(out WorldTransform listener)
	{
		listener = WorldTransform.Identity;
		if (!TryAudio()) return false;
		listener = (WorldTransform)_listenerProperty.GetValue(_audio);
		return true;
	}
}
