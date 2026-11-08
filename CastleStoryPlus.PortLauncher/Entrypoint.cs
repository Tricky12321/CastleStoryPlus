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
//
// On Linux the port's "Update" is done here: the port leaves an update request (CSP_LAUNCHER_UPDATE tells it this
// launcher takes one) and quits, and this runs the installer once the port is gone and starts the new port. An
// installer started by the port itself did not outlive it under Steam (the game's container ends what the game leaves
// behind), so the update never happened; this process is the one Steam waits for, so the installer runs to the end.
public static class Entrypoint
{
	internal const string PortFolder = "CastleStoryPlus-Unity6";

	// Written by the port: the release tag on the first line, the installer (in the game's BepInEx folder) on the second.
	private const string UpdateRequest = "CastleStoryPlus.Update.request";

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
			// A request left from before (the port crashed after writing it) is not acted on.
			TakeUpdateRequest(gameDir);
			bool first = true;
			while (true)
			{
				ProcessStartInfo start = PortStart(gameDir, portDir);
				if (start == null)
				{
					if (first)
					{
						Log(gameDir, "no Unity 6 port in " + portDir + "; the original game starts");
						return;
					}
					Log(gameDir, "no Unity 6 port in " + portDir + " after the update");
					Environment.Exit(1);
				}
				first = false;
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
				string[] request = TakeUpdateRequest(gameDir);
				if (request == null || !RunInstaller(gameDir, request[0], request[1]))
				{
					Environment.Exit(code);
				}
				// Installed: the new port starts, as "Update and restart" promises.
			}
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
		if (!IsWindows)
		{
			start.EnvironmentVariables["CSP_LAUNCHER_UPDATE"] = "1";
		}
		return start;
	}

	// The update request the port left, removed so it is acted on once; null when there is none or it is not valid.
	private static string[] TakeUpdateRequest(string gameDir)
	{
		if (IsWindows)
		{
			return null;
		}
		string file = Path.Combine(Path.Combine(gameDir, "BepInEx"), UpdateRequest);
		try
		{
			if (!File.Exists(file))
			{
				return null;
			}
			string[] lines = File.ReadAllText(file).Replace("\r", string.Empty).Split('\n');
			File.Delete(file);
			if (lines.Length < 2 || !IsTag(lines[0].Trim()) || !File.Exists(lines[1].Trim()))
			{
				Log(gameDir, "ignored an update request that is not valid");
				return null;
			}
			return new string[] { lines[0].Trim(), lines[1].Trim() };
		}
		catch (Exception ex)
		{
			Log(gameDir, "could not read the update request: " + ex.Message);
			return null;
		}
	}

	// A release tag: v and digits and dots, nothing a shell would read.
	private static bool IsTag(string tag)
	{
		if (tag.Length < 2 || tag.Length > 32 || tag[0] != 'v')
		{
			return false;
		}
		for (int i = 1; i < tag.Length; i++)
		{
			if (!char.IsDigit(tag[i]) && tag[i] != '.')
			{
				return false;
			}
		}
		return true;
	}

	// Runs the installer for the tag and waits for it; its output goes to the game's BepInEx/CastleStoryPlus.Update.log
	// (not the port's folder, which the installer replaces).
	private static bool RunInstaller(string gameDir, string tag, string installer)
	{
		string folder = Path.Combine(gameDir, "BepInEx");
		string log = Path.Combine(folder, "CastleStoryPlus.Update.log");
		// The command goes in a script of its own, started as "/bin/bash '<script>'" (as the port is): the game's Mono 2.6
		// splits a "-c '...'" argument with quotes nested in it ('\'') at the first closing quote, so bash got only
		// "bash " and the installer never ran.
		string script = Path.Combine(folder, "CastleStoryPlus.Update.run.sh");
		string command = "exec bash " + Quote(installer) + " --tag " + Quote(tag) + " --game-dir " + Quote(gameDir) + " > " + Quote(log) + " 2>&1 < /dev/null\n";
		Log(gameDir, "updating to " + tag + " with " + installer);
		try
		{
			File.WriteAllText(script, command);
			ProcessStartInfo start = new ProcessStartInfo("/bin/bash", Quote(script));
			start.UseShellExecute = false;
			start.WorkingDirectory = gameDir;
			Process process = Process.Start(start);
			process.WaitForExit();
			Log(gameDir, "the installer ended (" + process.ExitCode + "), see " + log);
			return process.ExitCode == 0;
		}
		catch (Exception ex)
		{
			Log(gameDir, "could not run the installer: " + ex.Message);
			return false;
		}
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
