using System;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using MonoMod.RuntimeDetour;
using MonoMod.RuntimeDetour.Platforms;

namespace CastleStoryPlus.Bootstrap;

// MonoMod native platform for Linux/macOS on old Mono: x86 detours plus mprotect for memory permissions.
internal class MprotectPlatform : IDetourNativePlatform
{
	private const int ProtReadWriteExec = 1 | 2 | 4;

	private const int ScPageSizeLinux = 30;

	private const int ScPageSizeMac = 29;

	private readonly IDetourNativePlatform _inner;

	private readonly long _pageSize;

	private MprotectPlatform(IDetourNativePlatform inner, long pageSize)
	{
		_inner = inner;
		_pageSize = pageSize;
	}

	public static void Install()
	{
		int platform = (int)Environment.OSVersion.Platform;
		// Unix = 4, MacOSX = 6, and 128 on very old Mono.
		if (platform != 4 && platform != 6 && platform != 128)
		{
			return;
		}
		_instance = new MprotectPlatform(new DetourNativeX86Platform(), PageSize());
		DetourHelper.Native = _instance;
		// BepInEx's XTermFix swaps the platform and then sets it to null. On Mono 2.6 none of MonoMod's
		// own platforms work, so the getter would then return null and every later detour fails.
		// Keep this platform whatever is assigned.
		_setterHook = new Hook(typeof(DetourHelper).GetProperty("Native").GetSetMethod(), typeof(MprotectPlatform).GetMethod(nameof(KeepPlatform), BindingFlags.Static | BindingFlags.NonPublic));
	}

	private static MprotectPlatform _instance;

	private static Hook _setterHook;

	private static void KeepPlatform(Action<IDetourNativePlatform> orig, IDetourNativePlatform value)
	{
		orig(_instance);
	}

	private static long PageSize()
	{
		try
		{
			long size = sysconf(ScPageSizeLinux);
			if (size <= 0)
			{
				size = sysconf(ScPageSizeMac);
			}
			return (size > 0) ? size : 4096;
		}
		catch
		{
			return 4096;
		}
	}

	private void SetReadWriteExecute(IntPtr start, ulong length)
	{
		long first = (long)start & ~(_pageSize - 1);
		long end = ((long)start + (long)length + _pageSize - 1) & ~(_pageSize - 1);
		if (mprotect((IntPtr)first, (IntPtr)(end - first), ProtReadWriteExec) != 0)
		{
			throw new Win32Exception(Marshal.GetLastWin32Error());
		}
	}

	public NativeDetourData Create(IntPtr from, IntPtr to, byte? type)
	{
		return _inner.Create(from, to, type);
	}

	public void Free(NativeDetourData detour)
	{
		_inner.Free(detour);
	}

	public void Apply(NativeDetourData detour)
	{
		_inner.Apply(detour);
	}

	public void Copy(IntPtr src, IntPtr dst, byte type)
	{
		_inner.Copy(src, dst, type);
	}

	public void MakeWritable(IntPtr src, uint size)
	{
		SetReadWriteExecute(src, size);
	}

	public void MakeExecutable(IntPtr src, uint size)
	{
		SetReadWriteExecute(src, size);
	}

	public void MakeReadWriteExecutable(IntPtr src, uint size)
	{
		SetReadWriteExecute(src, size);
	}

	public void FlushICache(IntPtr src, uint size)
	{
		_inner.FlushICache(src, size);
	}

	public IntPtr MemAlloc(uint size)
	{
		return _inner.MemAlloc(size);
	}

	public void MemFree(IntPtr ptr)
	{
		_inner.MemFree(ptr);
	}

	[DllImport("libc", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
	private static extern int mprotect(IntPtr start, IntPtr len, int prot);

	[DllImport("libc", CallingConvention = CallingConvention.Cdecl)]
	private static extern long sysconf(int name);
}
