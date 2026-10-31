using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.Rendering;

namespace CastleStoryPlus.Core;

// [Graphics] Renderer: which graphics API the Unity 6 port starts with (OpenGL Core or Vulkan, Linux). Unity picks the
// API once, when the player starts, from its command line (-force-glcore, -force-vulkan; boot.config does not take
// them), so a choice that differs from the running one starts the game again: a small shell waits until this process
// has ended and starts the same executable with the same arguments and environment (BepInEx's doorstop with it), the
// API's switch added. At startup that happens before the menu shows; from the settings window the game is saved first.
// Offered only when the install converted the shaders for more than one API (Content/graphics.txt, written by the
// installer). A start that asked for an API and did not get it (Unity fell back) sets the choice back to the default,
// so a driver without Vulkan cannot keep the game from starting.
[Feature]
internal static class RendererChoice
{
	internal const string Default = "Default";

	internal const string OpenGL = "OpenGL";

	internal const string Vulkan = "Vulkan";

	// Marks a start made by this class, so a fallback is seen and not started again.
	private const string RelaunchArgument = "-csp-renderer-relaunch";

	private static readonly string[] ApiArguments = { "-force-glcore", "-force-vulkan", "-force-opengl", "-force-d3d11", RelaunchArgument };

	private static ConfigEntry<string> _entry;

	private static List<string> _apis;

	internal static ConfigEntry<string> Entry
	{
		get
		{
			if (_entry == null)
			{
				_entry = Plugin.Cfg.Bind("Graphics", "Renderer", Default, new ConfigDescription("Graphics API of the Unity 6 port (Linux): Default (the player's first, OpenGL Core), OpenGL or Vulkan. Applies when the game starts; Settings > Castle Story Plus settings can restart it at once.", new AcceptableValueList<string>(Default, OpenGL, Vulkan)));
			}
			return _entry;
		}
	}

	// The install has shaders for more than one API (and the game runs where a restart can be made).
	internal static bool Available => Application.platform == RuntimePlatform.LinuxPlayer && InstalledApis().Count > 1;

	internal static string Current
	{
		get
		{
			switch (SystemInfo.graphicsDeviceType)
			{
				case GraphicsDeviceType.OpenGLCore:
					return OpenGL;
				case GraphicsDeviceType.Vulkan:
					return Vulkan;
				default:
					return SystemInfo.graphicsDeviceType.ToString();
			}
		}
	}

	// The choice differs from the API the game runs with.
	internal static bool NeedsRestart => Entry.Value != Default && Entry.Value != Current;

	private static void Enable()
	{
		_ = Entry;
		if (!Available || !NeedsRestart)
		{
			return;
		}
		if (Array.IndexOf(Environment.GetCommandLineArgs(), RelaunchArgument) >= 0)
		{
			// Started with the API's switch and still not on it: the driver or the install cannot run it.
			Plugin.Log.LogWarning("RendererChoice: asked for " + Entry.Value + " but the game runs on " + Current + "; the choice goes back to " + Default);
			Entry.Value = Default;
			return;
		}
		Plugin.Log.LogInfo("RendererChoice: the game runs on " + Current + ", " + Entry.Value + " is chosen; starting again");
		Restart(false);
	}

	// The next value of the choice, for the settings window's button.
	internal static void Cycle()
	{
		Entry.Value = Entry.Value == Default ? OpenGL : Entry.Value == OpenGL ? Vulkan : Default;
	}

	// Starts the game again with the chosen API; with save the running game is saved first (as save and quit). False
	// when nothing was started (the save failed, or the new process could not be started).
	internal static bool Restart(bool save)
	{
		if (save && Brix.Engine.Architecte.instance != null && GameParam.Map != null && !Menus.QuitDialogPatch.SaveBeforeLeaving())
		{
			Plugin.Log.LogWarning("RendererChoice: the game could not be saved, so it is not started again");
			return false;
		}
		string[] args = Environment.GetCommandLineArgs();
		string executable = Executable(args);
		// The game is started the way it was first, through BepInEx's script next to the executable when there is one
		// (doorstop's environment is left out of the new process, below).
		var command = new StringBuilder("rm -f \"$0\"; while kill -0 " + Process.GetCurrentProcess().Id + " 2>/dev/null; do sleep 0.2; done; exec");
		string script = Path.Combine(Path.GetDirectoryName(executable), "run_bepinex.sh");
		if (File.Exists(script))
		{
			command.Append(" /bin/sh ").Append(Quote(script));
		}
		command.Append(' ').Append(Quote(executable));
		for (int i = 1; i < args.Length; i++)
		{
			if (Array.IndexOf(ApiArguments, args[i]) < 0)
			{
				command.Append(' ').Append(Quote(args[i]));
			}
		}
		string chosen = Entry.Value == Default ? (InstalledApis()[0] == "Vulkan" ? Vulkan : OpenGL) : Entry.Value;
		command.Append(chosen == Vulkan ? " -force-vulkan" : " -force-glcore");
		command.Append(' ').Append(RelaunchArgument);
		try
		{
			// A script file, not sh -c: Mono splits a process's argument string by its own rules.
			string file = Path.Combine(Path.GetTempPath(), "castlestoryplus-restart-" + Process.GetCurrentProcess().Id + ".sh");
			File.WriteAllText(file, command.ToString() + "\n");
			Plugin.Log.LogInfo("RendererChoice: " + command);
			var start = new ProcessStartInfo("/bin/sh", file)
			{
				UseShellExecute = false,
				// The executable's folder: doorstop finds BepInEx relative to it, and the game has moved its own
				// current folder to its root (Info/...) by now.
				WorkingDirectory = Path.GetDirectoryName(executable),
			};
			// Doorstop's environment, inherited (it marks itself as done in it), makes the new process start without
			// BepInEx and the mod; the script sets it again, and the library paths it adds to.
			var names = new List<string>();
			foreach (string name in start.EnvironmentVariables.Keys)
			{
				if (name.StartsWith("DOORSTOP_", StringComparison.Ordinal) || name == "LD_PRELOAD" || name == "DYLD_INSERT_LIBRARIES")
				{
					names.Add(name);
				}
			}
			foreach (string name in names)
			{
				start.EnvironmentVariables.Remove(name);
			}
			Process.Start(start);
		}
		catch (Exception ex)
		{
			Plugin.Log.LogError("RendererChoice: could not start the game again: " + ex.Message);
			return false;
		}
		Plugin.Log.LogInfo("RendererChoice: closing to start again on " + chosen);
		Brix.Utils.ApplicationQuitter.Quit();
		return true;
	}

	private static string Executable(string[] args)
	{
		try
		{
			string path = Process.GetCurrentProcess().MainModule.FileName;
			if (!string.IsNullOrEmpty(path) && File.Exists(path))
			{
				return path;
			}
		}
		catch (Exception)
		{
			// Falls back to the command line's first argument.
		}
		return Path.GetFullPath(args[0]);
	}

	// One shell word: single quotes, a single quote inside closed, escaped and opened again.
	private static string Quote(string value)
	{
		return "'" + value.Replace("'", "'\\''") + "'";
	}

	// The APIs the install converted the shaders for, the player's default first (Content/graphics.txt).
	private static List<string> InstalledApis()
	{
		if (_apis != null)
		{
			return _apis;
		}
		_apis = new List<string>();
		try
		{
			string content = Environment.GetEnvironmentVariable("CSP_CONTENT");
			if (string.IsNullOrEmpty(content))
			{
				content = Path.Combine(Path.Combine(Application.dataPath, ".."), "Content");
			}
			string file = Path.Combine(content, "graphics.txt");
			if (File.Exists(file))
			{
				foreach (string line in File.ReadAllLines(file))
				{
					string api = line.Trim();
					if (api.Length > 0)
					{
						_apis.Add(api);
					}
				}
			}
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning("RendererChoice: could not read the installed graphics APIs: " + ex.Message);
		}
		return _apis;
	}
}
