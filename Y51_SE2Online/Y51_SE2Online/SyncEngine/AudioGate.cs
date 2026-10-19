using System;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using Keen.Game2.Client.WorldObjects.CubeBlocks.Cockpit;
using Keen.Game2.Client.WorldObjects.CubeBlocks.Effects;
using Keen.VRage.Audio;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.Core.Game.GameSystems.Audio;
using Keen.VRage.Library.Mathematics;
using System.Reflection;
using Keen.Game2.Client.GameSystems.BlockPlacement;
using Keen.Game2.Client.GameSystems.PlayerControl;
using Keen.Game2.Client.WorldObjects.Character;
using Keen.VRage.DCS.Components;
using Keen.VRage.Multiplayer.Sessions;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

internal static class AudioGate
{
	private const byte KindUnknown = 0;

	private const byte KindNone = 1;

	private const byte KindCharacter = 3;

	[ThreadStatic]
	private static int t_muted;

	private static byte[] _kinds = Array.Empty<byte>();

	public static volatile bool Enabled;

	public static long Muted2D;

	private static int _logged;

	private static int _failed;

	internal static Action? OnPlayed2D;

	public static bool IsMuted => t_muted > 0;

	public static void Enter() => t_muted++;

	public static void Exit() => t_muted--;

	public static bool Play2DPrefix()
	{
		if (t_muted <= 0)
		{
			OnPlayed2D?.Invoke();
			return true;
		}
		Interlocked.Increment(ref Muted2D);
		if (Interlocked.Exchange(ref _logged, 1) == 0)
		{
			GameLog.Instance.Info("audio: another player's action played as this player's own (2D) sound; muted here (0.7.6, further ones counted in the status line)");
		}
		return false;
	}

	// Crouch sounds come from a CharacterCrouchTag OnAdded/OnRemoved job (CheckCrouchAfterPhysics), not a
	// replicated signal, so Mute() never sees them. A character nobody here controls is someone else's.
	public static void CrouchPrefix(Component __instance, out bool __state)
	{
		__state = false;
		if (!Enabled)
		{
			return;
		}
		try
		{
			IControllable? controllable = __instance.Entity.TryGet<CharacterControllableComponent>();
			__state = controllable != null && controllable.Controller == null;
		}
		catch (Exception)
		{
			return;
		}
		if (__state)
		{
			t_muted++;
			Interlocked.Increment(ref AudioRouting.Instance.CharacterMuted);
		}
	}

	public static void CrouchFinalizer(bool __state)
	{
		if (__state)
		{
			t_muted--;
		}
	}

	private static byte Kind(int stub)
	{
		byte[] kinds = _kinds;
		if ((uint)stub < (uint)kinds.Length && kinds[stub] != KindUnknown)
		{
			return kinds[stub];
		}
		byte kind = KindNone;
		CommandLayout layout = CommandLayouts.ByStub(stub);
		if (layout != null)
		{
			string shortName = ClientUpstream.Short(layout.Name);
			kind = AudioRouting.IsCharacterSound(shortName) ? KindCharacter : KindNone;
		}
		if ((uint)stub >= (uint)kinds.Length)
		{
			byte[] grown = new byte[Math.Max(stub + 1, Math.Max(1024, kinds.Length * 2))];
			kinds.CopyTo(grown, 0);
			kinds = _kinds = grown;
		}
		kinds[stub] = kind;
		return kind;
	}

	public unsafe static bool Mute(int stub, ReplicationCommandBuffer cb, byte* payload, nint available, bool injected, bool s2c)
	{
		if (!Enabled)
		{
			return false;
		}
		try
		{
			byte kind = Kind(stub);
			if (kind == KindNone)
			{
				return false;
			}
			long nowMs = MpClock.NowMs();
			if (injected)
			{
				ClientWorld client = ClientWorld.Current;
				if (client == null)
				{
					return false;
				}
				if (available < 4)
				{
					return false;
				}
				bool own = client.IsOwnCharacterProxy(cb.ReadObject(Unsafe.ReadUnaligned<int>(payload)));
				if (!own)
				{
					Interlocked.Increment(ref AudioRouting.Instance.CharacterMuted);
				}
				return !own;
			}
			HostWorld host = HostWorld.Current;
			if (!s2c || host == null)
			{
				return false;
			}
			if (available < 4)
			{
				return false;
			}
			bool joiners = host.IsJoinerCharacter(cb.ReadObject(Unsafe.ReadUnaligned<int>(payload)) as Entity);
			if (joiners)
			{
				Interlocked.Increment(ref AudioRouting.Instance.CharacterMuted);
			}
			return joiners;
		}
		catch (Exception ex)
		{
			if (Interlocked.Exchange(ref _failed, 1) == 0)
			{
				GameLog.Instance.Warn($"audio: deciding whose sound a signal is failed ({ex.GetType().Name}: {ex.Message}); sounds play as in single player");
			}
			Enabled = false;
			return false;
		}
	}

	internal static void Install(Harmony harmony)
	{
		harmony.Patch(AccessTools.Method(typeof(CockpitAudioComponent), "PossessPilot"), prefix: new HarmonyMethod(typeof(AudioGate), nameof(CockpitPilotPrefix)));
		harmony.Patch(AccessTools.Method(typeof(MagneticBlockEffectsComponent), "PlayLockAudio"), prefix: new HarmonyMethod(typeof(AudioGate), nameof(MagneticLockPrefix)));
		Type placement = typeof(BlockPlacementClientSessionComponent);
		foreach (string name in new[] { "OnBlocksAddedAudio", "OnBlocksRemovedAudio" })
			harmony.Patch(AccessTools.Method(placement, name), prefix: new HarmonyMethod(typeof(AudioGate), nameof(BuildAudioPrefix)));
		foreach (string name in new[] { "OnBlockAdded", "OnBlockRemoved", "OnMultiBlocksAdded", "OnMultiBlocksAddedAABB" })
			harmony.Patch(AccessTools.Method(placement, name), postfix: new HarmonyMethod(typeof(AudioGate), nameof(BuildPlacePostfix)));
		harmony.Patch(AccessTools.Method(AccessTools.TypeByName("Keen.VRage.Audio.AudioManager"), "Update"), prefix: new HarmonyMethod(typeof(AudioGate), nameof(FlushBuildAudio)));
	}

	// The place/remove sounds are 2D UI events with no block attached (OnBlocksAddedAudio/OnBlocksRemovedAudio), and
	// the host sends them to everyone; telling them apart by who sent a build story recently let another player's
	// placements through while this player built, and every grinder removal by a client reached the listen host from
	// anywhere. The block signal right behind the audio signal says where it happened: the sound waits for it and
	// plays with distance falloff, or not at all from beyond range; this player's own builds keep full volume.
	private static (IAudioManager Audio, AudioEvent Event, bool Own, double OwnReach)? _pendingBuild;

	public static bool BuildAudioPrefix(BlockPlacementClientSessionComponent __instance, MethodBase __originalMethod)
	{
		bool client = ClientWorld.Current != null;
		if (!client && HostWorld.Current == null) return true;
		FlushBuildAudio();
		var config = __instance._effectsConfig;
		bool creative = __instance._gameMode.IsCreative(__instance._players.LocalPlayerIdentity);
		AudioEvent? audioEvent = __originalMethod.Name == "OnBlocksAddedAudio"
			? creative ? config.OnPlaceBlockCreativeAudioEvent : config.OnPlaceBlockSurvivalAudioEvent
			: creative ? config.OnDeleteBlockCreativeAudioEvent : config.OnDeleteBlockSurvivalAudioEvent;
		if (audioEvent == null) return false;
		long nowMs = MpClock.NowMs();
		bool own = client ? AudioRouting.Instance.TakeOwnBuild(nowMs) : AudioRouting.Instance.TakeJoinerBuild(nowMs) == 0;
		_pendingBuild = (__instance._audio, audioEvent.Value, own, client ? AudioRouting.OwnBuildReachMeters : AudioRouting.HostOwnBuildReachMeters);
		return false;
	}

	public static void BuildPlacePostfix(object __0, object[] __args)
	{
		if (_pendingBuild is not { } pending) return;
		if (__0 is not Entity entity || !entity.Scene.IsEntityAlive(entity.DEntity)) return;
		// A group placement (paste, symmetry, multi-place) reports the placed blocks' bounds, in the grid's frame or the
		// world's: measured to their nearest point, not to the grid's origin, which on a station can be far away.
		Vector3D listener = SoundRange.ListenerPosition;
		WorldTransform where = entity.Data.GetWorldTransform();
		double distance = __args.Length < 2 ? (where.Position - listener).Length()
			: __args[1] is BoundingBoxD world ? AudioRouting.DistanceToBox(V(listener), V(world.Min), V(world.Max))
			: __args[1] is BoundingBox local ? AudioRouting.DistanceToBox(V(WorldTransform.GetRelativeTransform(new WorldTransform(listener), where).Position), V(local.Min), V(local.Max))
			: (where.Position - listener).Length();
		_pendingBuild = null;
		PlayBuild(pending, distance);
	}

	private static Vec3D V(Vector3D v) => new(v.X, v.Y, v.Z);

	private static Vec3D V(Vector3 v) => new(v.X, v.Y, v.Z);

	// Removed blocks are often gone before their signal arrives: no position, so only this player's own removal plays.
	public static void FlushBuildAudio()
	{
		if (_pendingBuild is not { } pending) return;
		_pendingBuild = null;
		PlayBuild(pending, -1);
	}

	private static void PlayBuild((IAudioManager Audio, AudioEvent Event, bool Own, double OwnReach) pending, double distance)
	{
		float gain = AudioRouting.BuildSoundGain(pending.Own, distance, pending.OwnReach);
		if (gain > 0) pending.Audio.Play(pending.Event, gain);
		else Interlocked.Increment(ref BuildMuted);
	}

	public static long BuildMuted;

	// Every cockpit applies its "inside cockpit" snapshot (a mix filter on the whole game) when anyone sits in it;
	// another player taking a seat anywhere muffled this player's world.
	public static bool CockpitPilotPrefix(Entity pilot) => !IsOtherPlayer(pilot);

	internal static bool IsOtherPlayer(Entity? character) => ControlGuard.IsJoinerCharacter(character) || character != null && (ControlGuard.IsForeignCharacterHere?.Invoke(character) ?? false);

	// PlayLockAudio plays the 3D landing gear event through a 2D instance, which FMOD puts on the listener: every
	// landing gear locking, unlocking or coming ready anywhere was heard at full volume. Played at the block instead.
	public static bool MagneticLockPrefix(MagneticBlockEffectsComponent __instance, string state)
	{
		if (__instance._definition.OnLockStateChangedAudioEvent is not { } audioEvent) return false;
		var sound = __instance._audio.CreateAudioEventInstance3D(audioEvent);
		if (sound.TryGetParameterFromString(__instance._definition.LockStateToggleParameter, out var parameter)) sound.SetParameter(parameter, state);
		sound.UpdatePosition(__instance.Entity.Data.GetWorldTransform().Position);
		sound.Play();
		sound.Dispose();
		return false;
	}

	public static string Describe() => $"{AudioRouting.Instance.Describe()}; out-of-range build sounds {Interlocked.Read(in BuildMuted)}; 2D plays muted {Interlocked.Read(in Muted2D)}; {SoundRange.Describe()}";
}
