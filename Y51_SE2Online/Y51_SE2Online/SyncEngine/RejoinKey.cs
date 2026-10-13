using System;
using Keen.VRage.Core;
using Keen.VRage.Core.Input;
using Keen.VRage.Core.Platform;
using Keen.VRage.DCS.Components;
using Keen.VRage.Input;
using Keen.VRage.Library.Utils;

namespace Y51_SE2Online.SyncEngine;

internal static class RejoinKey
{
	private const int VkRejoin = 114;

	private static bool _down;

	internal static Func<bool>? Override;

	public static bool Pressed()
	{
		bool flag;
		try
		{
			flag = Override?.Invoke() ?? (IsDown(114) && OwnWindowInFront());
		}
		catch (Exception)
		{
			flag = false;
		}
		bool result = flag && !_down;
		_down = flag;
		return result;
	}

	internal static bool Edge(int vk, ref bool down)
	{
		bool flag;
		try
		{
			flag = IsDown(vk) && OwnWindowInFront();
		}
		catch (Exception)
		{
			flag = false;
		}
		bool result = flag && !down;
		down = flag;
		return result;
	}

	// The game's keyboard devices use Windows virtual-key codes on every platform. Checks all of them, not just
	// the default one: a plugin can register its own keyboard as the default (Remote does).
	internal static bool IsDown(int vk)
	{
		if (!ManualSingleton<InputDeviceManager>.HasValue) return false;
		var key = new DigitalInput(vk, GenericDeviceClass.Keyboard);
		foreach (var keyboard in InputDeviceManager.Instance.GetAllDevices(GenericDeviceClass.Keyboard))
		{
			if (keyboard.GetDigitalState(key)) return true;
		}
		return false;
	}

	internal static bool OwnWindowInFront()
	{
		return Singleton<VRageCore>.Instance.Engine.Single<IPlatformWindows>().Window?.IsActive ?? false;
	}
}
