namespace Y51_SE2Online.Sync;

/// <summary>Why this player's own 3D sounds would be silent while music plays.</summary>
public enum AudioFault
{
	None,
	/// <summary>The audio manager has no listener: SE2 drops every spatial one-shot and queues every loop.</summary>
	NoListener,
	/// <summary>The listener is still pinned to a cockpit the player no longer sits in.</summary>
	StaleCockpitListener,
	/// <summary>The SFX bus is paused. (Muted is SE2's own doing, AudioManager.ToggleMute or a 0 % volume; the rig's muted profile showed it.)</summary>
	SfxBusStopped,
	/// <summary>snapshot:/FadeOut3DScene holds the SFX bus at zero while its own volume is up.</summary>
	SfxBusSilent
}

/// <summary>The audio watchdog's decision; pure so it can be unit tested. A fault is acted on only once it has held
/// for <see cref="StuckMs"/>, so respawn blackouts, seat changes and menus pass untouched.</summary>
public sealed class AudioHealth
{
	public const long StuckMs = 8000;

	private AudioFault _fault;
	private long _sinceMs;

	public static AudioFault Diagnose(bool listenerValid, bool cockpitListener, bool inCockpit, bool sfxPaused, float sfxVolume, float sfxFinalVolume, bool fadeOutSnapshot)
	{
		if (!listenerValid) return AudioFault.NoListener;
		if (cockpitListener && !inCockpit) return AudioFault.StaleCockpitListener;
		if (sfxPaused) return AudioFault.SfxBusStopped;
		if (fadeOutSnapshot && sfxVolume > 0.05f && sfxFinalVolume < sfxVolume * 0.02f) return AudioFault.SfxBusSilent;
		return AudioFault.None;
	}

	/// <summary>The fault to repair now, or None while it is new (or there is none).</summary>
	public AudioFault Observe(AudioFault fault, long nowMs)
	{
		if (fault != _fault) { _fault = fault; _sinceMs = nowMs; }
		return fault != AudioFault.None && nowMs - _sinceMs >= StuckMs ? fault : AudioFault.None;
	}

	/// <summary>After a repair the same fault has to persist a full period again before the next one.</summary>
	public void Repaired(long nowMs) => _sinceMs = nowMs;
}
