using System;
using Keen.VRage.Core;
using Keen.VRage.Core.Input;
using Keen.VRage.Core.Platform;
using Keen.VRage.DCS.Components;
using Keen.VRage.Input;
using Keen.VRage.Library.Utils;

namespace Y51_SE2Online.SyncEngine;

/// <summary>Hotkeys read from the game's own keyboard devices, so they work wherever the game's input does (Windows, Proton and native Linux) and only while the game has focus.</summary>
internal static class GameKeys
{
	private const int VkF3 = 114;

	private static bool _menuJoinDown;

	/// <summary>F3 at the main menu joins the last host again.</summary>
	public static bool MenuJoinPressed()
	{
		bool down = IsDown(VkF3) && OwnWindowInFront();
		bool edge = down && !_menuJoinDown;
		_menuJoinDown = down;
		return edge;
	}

	/// <summary>Key binding codes are Windows virtual-key codes, which is also how the game numbers its keyboard inputs on every platform.</summary>
	internal static bool IsDown(int vk)
	{
		try
		{
			if (!InputDeviceManager.HasValue) return false;
			var key = new DigitalInput(vk, GenericDeviceClass.Keyboard);
			// Every keyboard, not just the default one: a plugin can register its own keyboard as the default (Remote does).
			foreach (var keyboard in Singleton<InputDeviceManager>.Instance.GetAllDevices(GenericDeviceClass.Keyboard))
			{
				if (keyboard.GetDigitalState(key)) return true;
			}
			return false;
		}
		catch (Exception)
		{
			return false;
		}
	}

	internal static bool OwnWindowInFront()
	{
		try { return Singleton<VRageCore>.Instance.Engine.Single<IPlatformWindows>().Window?.IsActive ?? false; }
		catch (Exception) { return false; }
	}
}
