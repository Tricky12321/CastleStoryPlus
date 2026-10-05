using System;
using Brix.UI;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryPlus.UI;

// Launch option -faststartup (also --fast-startup) or [Startup] FastStartup in the config: skips the game's logo screens (SceneSplashScreen) and goes to
// the main menu as soon as the game's data is loaded. The game's splash already has a skip flag; this sets it.
// Unity's own splash, drawn by the engine before any plugin runs, cannot be skipped from here. Steam starts the
// game through its launcher, which does not pass its launch options on to the game, so there the config is needed.
[Feature(Features.FastStartup, Features.FastStartupInfo)]
[HarmonyPatch(typeof(SplashScreen), nameof(SplashScreen.Start))]
internal static class FastStartup
{
	private static readonly string[] Options = { "-faststartup", "--faststartup", "-fast-startup", "--fast-startup" };

	private static bool _requested;

	private static void Enable()
	{
		_requested = Plugin.Cfg.Bind("Startup", "FastStartup", false, "Skip the logo screens at startup (same as the -faststartup launch option).").Value || Requested();
		if (!_requested)
		{
			return;
		}
		Plugin.Log.LogInfo("FastStartup: skipping the logo screens");
		// The splash scene is already loaded (and its Start may have run) when BepInEx starts.
		foreach (SplashScreen splash in UnityEngine.Object.FindObjectsOfType<SplashScreen>())
		{
			Skip(splash);
		}
	}

	private static bool Requested()
	{
		foreach (string arg in Environment.GetCommandLineArgs())
		{
			foreach (string option in Options)
			{
				if (string.Equals(arg, option, StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}
			}
		}
		return false;
	}

	private static void Prefix(SplashScreen __instance)
	{
		if (_requested)
		{
			__instance.skip = true;
		}
	}

	// Start waits for the game's data (factories) and then reads the flag. If the logos are already showing,
	// leave for the menu now.
	private static void Skip(SplashScreen splash)
	{
		splash.skip = true;
		if (splash.firstScreen != null && splash.firstScreen.activeSelf)
		{
			splash.LoadNextScene();
		}
	}
}
