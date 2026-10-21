using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// A low-level keyboard hook beside the game's DirectInput keyboard. Overlays (NVIDIA takes Alt+F1/F9/F10, Alt+Z, ...)
/// swallow their hotkeys before DirectInput sees them, but low-level hooks run newest first, so ours sees every key.
/// Our own Ctrl/Alt bindings are eaten while the game is in front, so the overlay does not act on them too.
/// Hooked only while online or while a binding is being captured: it sees every key typed system-wide.
/// </summary>
internal static class KeyHook
{
	private const int WhKeyboardLl = 13, WmQuit = 0x12, WmKeyDown = 0x100, WmKeyUp = 0x101, WmSysKeyDown = 0x104, WmSysKeyUp = 0x105;
	private const int VkShift = 0x10, VkCtrl = 0x11, VkAlt = 0x12;

	private static readonly bool[] Down = new bool[256];
	private static readonly bool[] Eaten = new bool[256];
	private static volatile KeyBinding[] _claimed = Array.Empty<KeyBinding>();
	private static OnlineSettings _claimedFrom;
	private static readonly LowLevelProc Proc = Hook;
	private static Run? _run;
	private static readonly int OwnPid = Environment.ProcessId;

	public static bool IsDown(int vk) => vk is >= 0 and < 256 && Down[vk];

	private sealed class Run { public uint ThreadId; public bool Stopped; }

	/// <summary>Main thread, every frame: hooks or unhooks when <paramref name="wanted"/> changes.</summary>
	public static void Want(bool wanted)
	{
		if (wanted == (_run != null) || !OperatingSystem.IsWindows()) return;
		if (wanted)
		{
			var run = _run = new Run();
			new Thread(() => Loop(run)) { IsBackground = true, Name = "Y51_SE2Online-Keys" }.Start();
			return;
		}
		var old = _run;
		_run = null;
		lock (old)
		{
			old.Stopped = true;
			if (old.ThreadId != 0) PostThreadMessage(old.ThreadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
		}
		Array.Clear(Down);
		Array.Clear(Eaten);
	}

	/// <summary>Called every frame; only re-reads the bindings when the settings object changes.</summary>
	public static void Claim(OnlineSettings settings)
	{
		if (ReferenceEquals(settings, _claimedFrom)) return;
		_claimedFrom = settings;
		var list = new List<KeyBinding>();
		foreach (var text in new[] { settings.ChatKey, settings.PlayersKey, settings.AdminKey, settings.BroadcastKey, settings.VoiceKey, settings.EmoteKey, settings.HackKey })
			if (KeyBinding.TryParse(text, out var key) && (key.Ctrl || key.Alt)) list.Add(key);
		_claimed = list.ToArray();
	}

	private static void Loop(Run run)
	{
		try
		{
			IntPtr hook = SetWindowsHookEx(WhKeyboardLl, Proc, GetModuleHandle(null), 0);
			if (hook == IntPtr.Zero) { GameLog.Instance.Warn("[keys] low-level keyboard hook not installed (error " + Marshal.GetLastWin32Error() + "); hotkeys an overlay owns will not work"); return; }
			lock (run)
			{
				if (run.Stopped) { UnhookWindowsHookEx(hook); return; }
				run.ThreadId = GetCurrentThreadId();
			}
			GameLog.Instance.Info("[keys] low-level keyboard hook installed");
			while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }
			UnhookWindowsHookEx(hook);
			GameLog.Instance.Info("[keys] low-level keyboard hook removed");
		}
		catch (Exception ex) { GameLog.Instance.Warn("[keys] keyboard hook failed: " + ex.Message); }
	}

	private static IntPtr Hook(int code, IntPtr wParam, IntPtr lParam)
	{
		if (code >= 0)
		{
			int msg = (int)wParam, vk = Marshal.ReadInt32(lParam) & 0xFF;
			bool down = msg is WmKeyDown or WmSysKeyDown;
			if (down || msg is WmKeyUp or WmSysKeyUp)
			{
				Set(vk, down);
				if (down ? ShouldEat(vk) : Eaten[vk])
				{
					Eaten[vk] = down;
					return (IntPtr)1;
				}
			}
		}
		return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
	}

	private static void Set(int vk, bool down)
	{
		Down[vk] = down;
		// Left/right modifier codes also count as the generic ones the bindings use.
		switch (vk)
		{
		case 0xA0: case 0xA1: Down[VkShift] = Down[0xA0] || Down[0xA1]; break;
		case 0xA2: case 0xA3: Down[VkCtrl] = Down[0xA2] || Down[0xA3]; break;
		case 0xA4: case 0xA5: Down[VkAlt] = Down[0xA4] || Down[0xA5]; break;
		}
	}

	private static bool ShouldEat(int vk)
	{
		if (KeyBindButton.Listening) return false;
		foreach (var key in _claimed)
			if (key.Vk == vk && key.IsDown(IsDown)) return OwnWindowInFront();
		return false;
	}

	private static bool OwnWindowInFront()
	{
		IntPtr window = GetForegroundWindow();
		return window != IntPtr.Zero && GetWindowThreadProcessId(window, out uint pid) != 0 && pid == OwnPid;
	}

	private delegate IntPtr LowLevelProc(int code, IntPtr wParam, IntPtr lParam);

	[DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, LowLevelProc proc, IntPtr module, uint thread);
	[DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
	[DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
	[DllImport("user32.dll")] private static extern int GetMessage(out Msg msg, IntPtr window, uint min, uint max);
	[DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
	[DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
	[DllImport("user32.dll")] private static extern bool PostThreadMessage(uint thread, int msg, IntPtr wParam, IntPtr lParam);
	[DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);
	[DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

	[StructLayout(LayoutKind.Sequential)]
	private struct Msg { public IntPtr Window; public uint Message; public IntPtr WParam, LParam; public uint Time; public int X, Y; }
}
