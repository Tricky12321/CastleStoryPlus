using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace Doorstop;

// Castle Story Plus runs on the Unity 6 port, a player of its own in the game's folder (CastleStoryPlus-Unity6). Steam
// still starts the original game, and BepInEx's doorstop (run_bepinex.sh on Linux, winhttp.dll on Windows) hands the
// game's Mono this entry point before anything of the game runs. It starts the port's player, waits for it and ends
// the original game with its exit code, so Steam sees the game running for as long as the port plays (play time, the
// overlay's "playing" state, Stop in the library). The installers point doorstop here (target_assembly).
//
// The port gets the environment the game was started with, less doorstop's own variables (the port's own doorstop
// would take them for its own and skip BepInEx), and is told where the game's data is (CSP_GAME_DATA), so its first
// start installs from this game without asking (CSP_AUTO_INSTALL). Without the port the game starts as it is.
public static class Entrypoint
{
	internal const string PortFolder = "CastleStoryPlus-Unity6";

	// Doorstop 3 calls Main, doorstop 4 calls Start.
	public static void Main()
	{
		Start();
	}

	public static void Start()
	{
		string gameDir = GameDir();
		string portDir = Path.Combine(gameDir, PortFolder);
		try
		{
			ProcessStartInfo start = PortStart(gameDir, portDir);
			if (start == null)
			{
				Log(gameDir, "no Unity 6 port in " + portDir + "; the original game starts");
				return;
			}
			DateTime started = DateTime.Now;
			Process port = Process.Start(start);
			Log(gameDir, "started the Unity 6 port (" + start.FileName + ", process " + port.Id + ")");
			port.WaitForExit();
			int code = port.ExitCode;
			Log(gameDir, "the Unity 6 port ended (" + code + ")");
			// The first start after an install converts the game, then starts the player again: that one is waited for too.
			int restarted = RestartedPid(portDir, started);
			if (restarted > 0)
			{
				Log(gameDir, "the Unity 6 port started again after its install (process " + restarted + ")");
				WaitForProcess(restarted);
				Log(gameDir, "the Unity 6 port ended");
			}
			Environment.Exit(code);
		}
		catch (Exception ex)
		{
			Log(gameDir, "could not start the Unity 6 port: " + ex);
		}
	}

	// The game's folder: doorstop's own path is <game>/BepInEx/core/CastleStoryPlus.PortLauncher.dll.
	private static string GameDir()
	{
		string dll = Environment.GetEnvironmentVariable("DOORSTOP_INVOKE_DLL_PATH");
		if (!string.IsNullOrEmpty(dll))
		{
			DirectoryInfo core = new FileInfo(Path.GetFullPath(dll)).Directory;
			if (core != null && core.Parent != null && core.Parent.Parent != null)
			{
				return core.Parent.Parent.FullName;
			}
		}
		return Directory.GetCurrentDirectory();
	}

	private static ProcessStartInfo PortStart(string gameDir, string portDir)
	{
		ProcessStartInfo start;
		if (IsWindows)
		{
			string exe = Path.Combine(portDir, "CastleStoryPlus.exe");
			if (!File.Exists(exe))
			{
				return null;
			}
			start = new ProcessStartInfo(exe);
		}
		else
		{
			string script = Path.Combine(portDir, "run_bepinex.sh");
			if (!File.Exists(script))
			{
				return null;
			}
			start = new ProcessStartInfo("/bin/bash", Quote(script));
		}
		start.WorkingDirectory = portDir;
		start.UseShellExecute = false;
		List<string> doorstop = new List<string>();
		foreach (string name in start.EnvironmentVariables.Keys)
		{
			if (name.StartsWith("DOORSTOP_", StringComparison.OrdinalIgnoreCase))
			{
				doorstop.Add(name);
			}
		}
		foreach (string name in doorstop)
		{
			start.EnvironmentVariables.Remove(name);
		}
		if (!IsWindows)
		{
			// The game's doorstop library; the port's run_bepinex.sh preloads its own.
			start.EnvironmentVariables.Remove("LD_PRELOAD");
		}
		start.EnvironmentVariables["CSP_GAME_DATA"] = Path.Combine(gameDir, "Castle Story_Data");
		start.EnvironmentVariables["CSP_AUTO_INSTALL"] = "1";
		return start;
	}

	private static int RestartedPid(string portDir, DateTime since)
	{
		string file = Path.Combine(portDir, "restarted.pid");
		try
		{
			if (File.Exists(file) && File.GetLastWriteTime(file) >= since.AddSeconds(-1))
			{
				return int.Parse(File.ReadAllText(file).Trim());
			}
		}
		catch (Exception)
		{
		}
		return 0;
	}

	// Not a child of this process: Mono 2.6 cannot wait for it on Linux, so its /proc entry is watched there.
	private static void WaitForProcess(int pid)
	{
		while (true)
		{
			bool running;
			if (IsWindows)
			{
				try
				{
					running = !Process.GetProcessById(pid).HasExited;
				}
				catch (Exception)
				{
					running = false;
				}
			}
			else
			{
				running = Directory.Exists("/proc/" + pid);
			}
			if (!running)
			{
				return;
			}
			System.Threading.Thread.Sleep(1000);
		}
	}

	private static bool IsWindows => Environment.OSVersion.Platform != PlatformID.Unix && Environment.OSVersion.Platform != PlatformID.MacOSX && (int)Environment.OSVersion.Platform != 128;

	private static string Quote(string value)
	{
		return "'" + value.Replace("'", "'\\''") + "'";
	}

	private static void Log(string gameDir, string message)
	{
		try
		{
			File.AppendAllText(Path.Combine(Path.Combine(gameDir, "BepInEx"), "CastleStoryPlus.PortLauncher.log"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + message + Environment.NewLine);
		}
		catch (Exception)
		{
		}
	}
}
