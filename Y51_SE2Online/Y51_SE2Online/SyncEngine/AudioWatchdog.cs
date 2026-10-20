using System;
using System.Linq;
using HarmonyLib;
using Keen.Game2.Client.GameSystems.PlayerControl;
using Keen.Game2.Client.WorldObjects.CubeBlocks.Cockpit;
using Keen.VRage.DCS.Components;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// Every two seconds online: is anything holding this player's own 3D sounds silent while music plays (no audio
/// listener, a listener left in a cockpit, a paused or snapshotted-out SFX bus)? A state that holds for
/// <see cref="AudioHealth.StuckMs"/> is logged with its cause and repaired.
/// </summary>
internal static class AudioWatchdog
{
	private static readonly AudioHealth Health = new();
	private static Type? _type;
	private static Func<object?>? _instance;
	private static AccessTools.FieldRef<object, bool>? _listenerValid;
	private static AccessTools.FieldRef<object, FMOD.Studio.System>? _studio;
	private static System.Reflection.FieldInfo? _origin;
	private static FMOD.Studio.Bus _sfx;
	private static long _nextMs;
	private static bool _failed;
	public static long Repairs;

	internal static void Tick()
	{
		long now = MpClock.NowMs();
		if (_failed || now < _nextMs || !AdminAccess.InSession) return;
		_nextMs = now + 2000;
		try
		{
			if (_type == null)
			{
				_type = AccessTools.TypeByName("Keen.VRage.Audio.AudioManager");
				var instance = AccessTools.Property(_type.BaseType, "Instance");
				_instance = () => instance.GetValue(null);
				_listenerValid = AccessTools.FieldRefAccess<bool>(_type, "_hasValidListenerTransform");
				_studio = AccessTools.FieldRefAccess<FMOD.Studio.System>(_type, "_system");
				_origin = AccessTools.Field(_type, "_listenerOrigin");
			}
			object? manager = _instance!();
			var controller = MpPlugin.App?.ClientSession?.SessionComponents.TryGet<ClientPlayersSessionComponent>()?.LocalPlayerController;
			var top = controller?.GetTopControllable();
			// Dead, in the spawn menu or loading: SE2 has no listener and nothing of this player's to hear.
			if (manager == null || top == null) { Health.Observe(AudioFault.None, now); return; }
			var studio = _studio!(manager);
			if (!_sfx.isValid() && studio.getBus("bus:/SFX", out _sfx) == FMOD.RESULT.OK) _sfx.lockChannelGroup();
			bool paused = false, muted = false;
			float volume = 0, final = 1;
			if (_sfx.isValid()) { _sfx.getPaused(out paused); _sfx.getMute(out muted); _sfx.getVolume(out volume, out final); }
			object? originRef = _origin!.GetValue(manager);
			var origin = originRef?.GetType().GetProperty("Entity")?.GetValue(originRef) as Entity;
			bool fadeOut = studio.getEvent("snapshot:/FadeOut3DScene", out var fade) == FMOD.RESULT.OK && fade.getInstanceCount(out int fades) == FMOD.RESULT.OK && fades > 0;
			var fault = AudioHealth.Diagnose(_listenerValid!(manager), origin != null, controller!.GetTopControllable<CockpitControllableComponent>() != null, paused, volume, final, fadeOut);
			var stuck = Health.Observe(fault, now);
			if (stuck == AudioFault.None) return;
			Repair(stuck, manager, studio, top, origin);
			Health.Repaired(now);
			if (++Repairs <= 10) GameLog.Instance.Warn(FormattableString.Invariant($"audio watchdog: this player's sounds were silent ({stuck}; SFX bus volume {volume:0.00}, after snapshots {final:0.00}, paused {paused}, muted {muted}, listener origin {origin?.ToString() ?? "none"}, controlling {top.GetType().Name}); repaired"));
		}
		catch (Exception ex)
		{
			_failed = true;
			GameLog.Instance.Warn($"audio watchdog: switched off ({ex.GetType().Name}: {ex.Message})");
		}
	}

	private static void Repair(AudioFault fault, object manager, FMOD.Studio.System studio, IControllable top, Entity? origin)
	{
		switch (fault)
		{
			case AudioFault.NoListener:
				AccessTools.Method(_type, "UpdateListenerTransform").Invoke(manager, new object?[] { top.Entity });
				break;
			case AudioFault.StaleCockpitListener:
				AccessTools.Method(_type, "OverrideListenerOrigin").Invoke(manager, new object?[] { null });
				break;
			case AudioFault.SfxBusStopped:
				_sfx.setPaused(false);
				break;
			case AudioFault.SfxBusSilent:
				// SE2 ships snapshot:/FadeOut3DScene (SFX to zero) but nothing in the game stops it once started.
				if (studio.getEvent("snapshot:/FadeOut3DScene", out var fade) == FMOD.RESULT.OK && fade.getInstanceList(out var list) == FMOD.RESULT.OK)
					foreach (var instance in list) instance.stop(FMOD.Studio.STOP_MODE.ALLOWFADEOUT);
				break;
		}
	}

	public static string Describe() => $"audio watchdog repairs {Repairs}";
}
