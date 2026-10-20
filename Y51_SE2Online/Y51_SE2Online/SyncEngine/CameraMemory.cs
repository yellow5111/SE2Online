using System;
using System.IO;
using System.Text.Json;
using HarmonyLib;
using Keen.Game2.Client.GameSystems.CameraSystems.Adapters;
using Keen.Game2.Client.GameSystems.CameraSystems.Modes;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// The third-person zoom (Alt + scroll) a player last chose, on foot and in a ship. Single player keeps it in the save;
/// in a multiplayer session the camera is rebuilt on every join, respawn and cockpit change and fell back to the default.
/// </summary>
internal static class CameraMemory
{
	private sealed class Saved
	{
		public float? Character { get; set; }

		public float? Ship { get; set; }
	}

	private static Saved? _saved;

	private static string FilePath => PluginPaths.Data("Y51_SE2Online.camera.json");

	public static void Install(Harmony harmony)
	{
		var camera = typeof(ThirdPersonCameraComponent);
		harmony.Patch(AccessTools.Method(camera, "OnAddedToScene"), postfix: new HarmonyMethod(typeof(CameraMemory), nameof(AddedPostfix)));
		harmony.Patch(AccessTools.Method(camera, "OnBeforeRemovedFromScene"), prefix: new HarmonyMethod(typeof(CameraMemory), nameof(RemovedPrefix)));
	}

	private static Saved State => _saved ??= Load();

	private static bool IsShip(ThirdPersonCameraComponent camera) => camera._thirdPersonAdapter is CubeGridThirdPersonAdapterComponent;

	private static void AddedPostfix(ThirdPersonCameraComponent __instance)
	{
		if (!AdminAccess.InSession) return;
		try
		{
			if ((IsShip(__instance) ? State.Ship : State.Character) is float distance) __instance.ForceDistance(distance, instant: true);
		}
		catch (Exception ex) { GameLog.Instance.WarnOnce("camera-memory", "camera: restoring the third-person zoom failed: " + ex.Message); }
	}

	private static void RemovedPrefix(ThirdPersonCameraComponent __instance)
	{
		if (!AdminAccess.InSession) return;
		try
		{
			float distance = __instance.TargetDistance;
			if (!float.IsFinite(distance) || distance <= 0 || distance >= float.MaxValue / 2) return;
			var state = State;
			if (IsShip(__instance))
			{
				if (state.Ship == distance) return;
				state.Ship = distance;
			}
			else
			{
				if (state.Character == distance) return;
				state.Character = distance;
			}
			File.WriteAllText(FilePath, JsonSerializer.Serialize(state));
		}
		catch (Exception ex) { GameLog.Instance.WarnOnce("camera-memory-save", "camera: saving the third-person zoom failed: " + ex.Message); }
	}

	private static Saved Load()
	{
		try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<Saved>(File.ReadAllText(FilePath)) ?? new Saved() : new Saved(); }
		catch (Exception) { return new Saved(); }
	}
}
