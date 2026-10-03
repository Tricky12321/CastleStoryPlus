using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Doorstop;

// Doorstop calls this instead of BepInEx.Preloader.dll (doorstop 3 calls Main, doorstop 4 calls Start).
// Castle Story runs Unity's Mono 2.6, where MonoMod cannot find a working way to change memory
// protection (its libc platform needs Environment.SystemPageSize, which Mono 2.6 lacks), so every
// Harmony patch fails. This installs a native platform that calls mprotect directly, then hands over
// to the real BepInEx preloader.
public static class Entrypoint
{
	private const string PreloaderFile = "BepInEx.Preloader.dll";

	private static string _coreDir;

	public static void Main()
	{
		Start();
	}

	public static void Start()
	{
		_coreDir = Path.GetDirectoryName(Path.GetFullPath(Environment.GetEnvironmentVariable("DOORSTOP_INVOKE_DLL_PATH") ?? "."));
		AppDomain.CurrentDomain.AssemblyResolve += ResolveFromCore;
		try
		{
			// The libc platform is only needed on Linux/macOS; Windows keeps MonoMod's VirtualProtect platform.
			if (Environment.OSVersion.Platform == PlatformID.Unix || Environment.OSVersion.Platform == PlatformID.MacOSX || (int)Environment.OSVersion.Platform == 128)
			{
				InstallNativePlatform();
			}
		}
		catch (Exception ex)
		{
			WriteError("Could not install the native detour platform", ex);
		}
		string preloader = Path.Combine(_coreDir, PreloaderFile);
		Environment.SetEnvironmentVariable("DOORSTOP_INVOKE_DLL_PATH", preloader);
		// The resolver stays registered: BepInEx must get the same MonoMod instance that holds the platform.
		RunPreloader(preloader);
	}

	// Kept out of Start so MonoMod is only loaded after the resolver is registered.
	[MethodImpl(MethodImplOptions.NoInlining)]
	private static void InstallNativePlatform()
	{
		CastleStoryPlus.Bootstrap.MprotectPlatform.Install();
	}

	private static void RunPreloader(string path)
	{
		Assembly assembly = ResolveFromCore(null, new ResolveEventArgs(Path.GetFileNameWithoutExtension(path)));
		// BepInEx 5.4.22 and older (doorstop 3) / 5.4.23 and newer (doorstop 4).
		MethodInfo entry = assembly.GetType("BepInEx.Preloader.Entrypoint")?.GetMethod("Main", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null)
			?? assembly.GetType("Doorstop.Entrypoint")?.GetMethod("Start", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
		if (entry == null)
		{
			WriteError("No BepInEx preloader entry point found in " + path, null);
			return;
		}
		entry.Invoke(null, null);
	}

	// Mono 2.6 loads a second copy when LoadFile is called again for the same file, so loaded
	// assemblies are reused by name.
	private static Assembly ResolveFromCore(object sender, ResolveEventArgs args)
	{
		string name = new AssemblyName(args.Name).Name;
		foreach (Assembly loaded in AppDomain.CurrentDomain.GetAssemblies())
		{
			if (loaded.GetName().Name == name)
			{
				return loaded;
			}
		}
		string file = Path.Combine(_coreDir, name + ".dll");
		return File.Exists(file) ? Assembly.LoadFile(file) : null;
	}

	private static void WriteError(string message, Exception ex)
	{
		try
		{
			string root = Path.GetDirectoryName(Path.GetDirectoryName(_coreDir));
			File.AppendAllText(Path.Combine(root, "CastleStoryPlus.Bootstrap.log"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + message + Environment.NewLine + ex + Environment.NewLine);
		}
		catch
		{
		}
	}
}
