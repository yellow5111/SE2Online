using System;
using System.Runtime.InteropServices;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// SE2's AI library (VRage.KytheraV2.Native.dll) registers a vectored exception handler in KytV2_CreateCore that
/// symbolizes the stack and appends it to Game2\kytv2_crash.log for EVERY access violation, including the ones .NET turns
/// into a caught NullReferenceException. That costs 90-115 ms per exception on a desktop and ~250 ms on a 2-vCPU server,
/// a frame freeze each time (the VM's periodic "desync 257 ms" frames). The handler only logs and passes the exception on,
/// so removing it changes nothing else. Checked every frame because each new AI core registers it again.
/// </summary>
internal static class KytheraVeh
{
	private const string Module = "VRage.KytheraV2.Native.dll";

	private static IntPtr _base;
	private static int _global;
	private static bool _off;
	public static int Removed;

	public static void Tick()
	{
		if (_off) return;
		try
		{
			if (_base == IntPtr.Zero)
			{
				if (!OperatingSystem.IsWindows()) { _off = true; return; }
				_base = GetModuleHandle(Module);
				if (_base == IntPtr.Zero) return;
				if (FindHandlerGlobal(_base) is not int global)
				{
					_off = true;
					GameLog.Instance.Warn("kythera: no access-violation logger found in VRage.KytheraV2.Native.dll; it stays (every NullReferenceException may cost ~100 ms)");
					return;
				}
				_global = global;
			}
			IntPtr slot = _base + _global;
			IntPtr handle = Marshal.ReadIntPtr(slot);
			if (handle == IntPtr.Zero) return;
			Marshal.WriteIntPtr(slot, IntPtr.Zero);
			bool ok = RemoveVectoredExceptionHandler(handle) != 0;
			Removed++;
			if (Removed == 1 || !ok) GameLog.Instance.Info($"kythera: removed the AI library's access-violation logger ({(ok ? "ok" : "it was already gone")})");
		}
		catch (Exception ex)
		{
			_off = true;
			GameLog.Instance.Warn("kythera: could not remove the access-violation logger: " + ex.Message);
		}
	}

	/// <summary>
	/// The global KytV2_CreateCore keeps the handler in, found by shape so a new game build needs no new offsets:
	/// `cmp qword [g], 0` ... `call [AddVectoredExceptionHandler]` ... `mov [g], rax`. SE2 2.4.0.95: 0x368a8; 4.0.0.3151: 0x3cbf0.
	/// </summary>
	internal static int? FindHandlerGlobal(IntPtr image)
	{
		IntPtr add = NativeLibrary.GetExport(NativeLibrary.Load("kernel32.dll"), "AddVectoredExceptionHandler");
		int pe = Marshal.ReadInt32(image + 0x3C);
		int sections = Marshal.ReadInt16(image + pe + 6);
		IntPtr table = image + pe + 24 + Marshal.ReadInt16(image + pe + 20);
		for (int s = 0; s < sections; s++)
		{
			IntPtr header = table + s * 40;
			if (Marshal.PtrToStringAnsi(header, 5) != ".text") continue;
			int size = Marshal.ReadInt32(header + 8), start = Marshal.ReadInt32(header + 12);
			var code = new byte[size];
			Marshal.Copy(image + start, code, 0, size);
			for (int i = 0; i + 6 <= size; i++)
			{
				if (code[i] != 0xFF || code[i + 1] != 0x15 || Marshal.ReadIntPtr(image + start + i + 6 + BitConverter.ToInt32(code, i + 2)) != add) continue;
				int? check = null;
				for (int j = Math.Max(0, i - 0x60); j + 8 <= i; j++)
					if (code[j] == 0x48 && code[j + 1] == 0x83 && code[j + 2] == 0x3D && code[j + 7] == 0x00) check = start + j + 8 + BitConverter.ToInt32(code, j + 3);
				for (int j = i + 6; j + 7 <= Math.Min(size, i + 0x60); j++)
					if (code[j] == 0x48 && code[j + 1] == 0x89 && code[j + 2] == 0x05 && start + j + 7 + BitConverter.ToInt32(code, j + 3) == check) return check;
			}
		}
		return null;
	}

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);
	[DllImport("kernel32.dll")] private static extern uint RemoveVectoredExceptionHandler(IntPtr handle);
}
