using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using CastleStoryPlus.Core;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CastleStoryPlus.Diagnostics;

// BepInEx/system-report.txt, for bug reports: the mod's and the game's versions, the system (operating system, Wine or
// Proton, processor, memory, graphics card and API, screen), the game's own settings (Info/UserSettings/Current.json)
// and every setting of the mod and its plugins (BepInEx/config). Written when the game starts and again each time a
// scene loads (a game started or left), so it holds the settings in use. Nothing is sent anywhere.
[Feature]
internal static class SystemReport
{
	internal const string FileName = "system-report.txt";

	private const string GameSettings = "Info/UserSettings/Current.json";

	internal static string FilePath
	{
		get { return Path.Combine(BepInEx.Paths.BepInExRootPath, FileName); }
	}

	private static void Enable()
	{
		Write("game started");
		SceneManager.sceneLoaded += (Scene scene, LoadSceneMode mode) =>
		{
			if (mode == LoadSceneMode.Single)
			{
				Write("scene " + scene.name + " loaded");
			}
		};
	}

	internal static void Write(string reason)
	{
		try
		{
			File.WriteAllText(FilePath, Build(reason), Encoding.UTF8);
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning("SystemReport: could not write " + FilePath + ": " + ex.Message);
		}
	}

	internal static string Build(string reason)
	{
		StringBuilder text = new StringBuilder();
		text.AppendLine("Castle Story Plus system report");
		text.AppendLine("Written " + DateTime.Now.ToString("dd-MM-yyyy HH:mm:ss") + " (" + reason + ")");
		text.AppendLine();

		Section(text, "Versions");
		Line(text, "Castle Story Plus", Plugin.Version);
		Line(text, "Unity", Application.unityVersion);
		Line(text, "Platform", Application.platform.ToString());
		Line(text, "Game folder", BepInEx.Paths.GameRootPath);
		Line(text, "Working folder", Environment.CurrentDirectory);
		Line(text, "Command line", Environment.CommandLine);

		Section(text, "System");
		Line(text, "Operating system", SystemInfo.operatingSystem);
#if UNITY6
		Line(text, "OS family", SystemInfo.operatingSystemFamily.ToString());
#endif
		Line(text, "Wine / Proton", WineVersion() ?? "no");
		Line(text, "Processor", SystemInfo.processorType + " (" + SystemInfo.processorCount + " threads, " + SystemInfo.processorFrequency + " MHz)");
		Line(text, "Memory", SystemInfo.systemMemorySize + " MB");
		Line(text, "Graphics card", SystemInfo.graphicsDeviceName + " (" + SystemInfo.graphicsDeviceVendor + ", " + SystemInfo.graphicsMemorySize + " MB)");
		Line(text, "Graphics API", SystemInfo.graphicsDeviceType + ": " + SystemInfo.graphicsDeviceVersion);
		Line(text, "Shader level", SystemInfo.graphicsShaderLevel.ToString());
#if UNITY6
		Line(text, "Screen", Screen.width + " x " + Screen.height + ", " + Screen.fullScreenMode + ", " + Screen.currentResolution.refreshRateRatio.value.ToString("0") + " Hz, dpi " + Screen.dpi.ToString("0"));
#else
		Line(text, "Screen", Screen.width + " x " + Screen.height + (Screen.fullScreen ? ", full screen" : ", windowed") + ", " + Screen.currentResolution.refreshRate + " Hz, dpi " + Screen.dpi.ToString("0"));
#endif
		Line(text, "Displays", Display.displays.Length.ToString());
		Line(text, "Quality level", QualitySettings.names.Length > QualitySettings.GetQualityLevel() ? QualitySettings.names[QualitySettings.GetQualityLevel()] : QualitySettings.GetQualityLevel().ToString());
		Line(text, "VSync / target FPS", QualitySettings.vSyncCount + " / " + Application.targetFrameRate);
		Line(text, "System language", Application.systemLanguage.ToString());
		Line(text, "Mono / runtime", Environment.Version.ToString());

		Section(text, "Game settings (" + GameSettings + ")");
		text.AppendLine(ReadOrNote(GameSettings));

		Section(text, "Mod and plugin settings (BepInEx/config)");
		foreach (string file in ConfigFiles())
		{
			text.AppendLine("--- " + Path.GetFileName(file));
			text.AppendLine(Settings(file));
		}

		Section(text, "Plugins");
		foreach (KeyValuePair<string, BepInEx.PluginInfo> plugin in BepInEx.Bootstrap.Chainloader.PluginInfos)
		{
			text.AppendLine(plugin.Value.Metadata.Name + " " + plugin.Value.Metadata.Version + " (" + plugin.Key + ")");
		}
		return text.ToString();
	}

	private static void Section(StringBuilder text, string title)
	{
		text.AppendLine();
		text.AppendLine("== " + title + " ==");
	}

	private static void Line(StringBuilder text, string name, string value)
	{
		text.AppendLine(name.PadRight(20) + ": " + value);
	}

	private static string ReadOrNote(string path)
	{
		try
		{
			return File.Exists(path) ? File.ReadAllText(path) : "(no file)";
		}
		catch (Exception ex)
		{
			return "(could not read: " + ex.Message + ")";
		}
	}

	private static IEnumerable<string> ConfigFiles()
	{
		try
		{
			string[] files = Directory.GetFiles(BepInEx.Paths.ConfigPath, "*.cfg");
			Array.Sort(files, StringComparer.Ordinal);
			return files;
		}
		catch (Exception)
		{
			return new string[0];
		}
	}

	// A config file's sections and values, without its comments and blank lines.
	private static string Settings(string file)
	{
		StringBuilder text = new StringBuilder();
		try
		{
			foreach (string line in File.ReadAllLines(file))
			{
				string trimmed = line.Trim();
				if (trimmed.Length > 0 && !trimmed.StartsWith("#"))
				{
					text.AppendLine(trimmed);
				}
			}
		}
		catch (Exception ex)
		{
			text.AppendLine("(could not read: " + ex.Message + ")");
		}
		return text.ToString();
	}

	[DllImport("kernel32", CharSet = CharSet.Ansi)]
	private static extern IntPtr GetModuleHandle(string name);

	[DllImport("kernel32", CharSet = CharSet.Ansi)]
	private static extern IntPtr GetProcAddress(IntPtr module, string name);

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate IntPtr WineGetVersion();

	// Wine (and so Proton) has ntdll export wine_get_version; Windows does not. With Proton, also its version from
	// the environment Steam sets.
	private static string WineVersion()
	{
		if (Application.platform != RuntimePlatform.WindowsPlayer)
		{
			return null;
		}
		try
		{
			IntPtr ntdll = GetModuleHandle("ntdll.dll");
			IntPtr function = (ntdll != IntPtr.Zero) ? GetProcAddress(ntdll, "wine_get_version") : IntPtr.Zero;
			if (function == IntPtr.Zero)
			{
				return null;
			}
			string version = Marshal.PtrToStringAnsi(((WineGetVersion)Marshal.GetDelegateForFunctionPointer(function, typeof(WineGetVersion)))());
			string proton = Environment.GetEnvironmentVariable("STEAM_COMPAT_TOOL_PATHS") ?? Environment.GetEnvironmentVariable("PROTON_VERSION");
			return "Wine " + version + (string.IsNullOrEmpty(proton) ? string.Empty : " (Proton: " + proton + ")");
		}
		catch (Exception ex)
		{
			return "unknown (" + ex.Message + ")";
		}
	}
}
