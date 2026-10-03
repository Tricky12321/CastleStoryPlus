using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx.Configuration;
using Brix.Assets;
using Brix.Engine;
using Brix.Game;
using Brix.Input;
using Brix.Network;
using Brix.Utils;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;

namespace CastleStoryPlus.Saving;

// Saves the game every [Saving] AutosaveMinutes minutes of play (paused time and menus do not count).
// Each map gets its own autosave ("<map> (Autosave)"), overwritten every time, so an autosave never
// replaces a manual save or the autosave of another map. Any save made by the player restarts the countdown.
[Feature(Features.AutoSave, Features.AutoSaveInfo)]
internal class AutoSave : MonoBehaviour
{
	// The intervals offered in the mod settings window; 0 = off.
	public static readonly int[] Choices = new int[8] { 0, 1, 2, 3, 5, 10, 15, 30 };

	private const string Suffix = " (Autosave)";

	internal static ConfigEntry<int> Minutes;

	private static float _elapsed;

	private static bool _saving;

	private static void Enable()
	{
		Minutes = Plugin.Cfg.Bind("Saving", "AutosaveMinutes", 2, "Minutes of play between autosaves (0 = off). Can also be changed in the game: Settings > Castle Story Plus settings.");
		Plugin.Root.AddComponent<AutoSave>();
	}

	internal static void ResetTimer()
	{
		if (!_saving)
		{
			_elapsed = 0f;
		}
	}

	public static string Describe(int minutes)
	{
		return (minutes <= 0) ? "Off" : (minutes + " min");
	}

	private void Update()
	{
		if (Minutes.Value <= 0 || !CanSave())
		{
			_elapsed = 0f;
			return;
		}
		if (InputModeController.GamePlayPaused())
		{
			return;
		}
		_elapsed += Time.unscaledDeltaTime;
		if (_elapsed >= Minutes.Value * 60f)
		{
			_elapsed = 0f;
			Save();
		}
	}

	// In a running game (not the world editor); in multiplayer only the host saves.
	private static bool CanSave()
	{
		if (GameParam.Map == null || !Architecte.commence || Architecte.termine || SceneManager.GetActiveScene().name == "SceneMenu")
		{
			return false;
		}
		if (InputModeController.DefaultMode == InputMode.worldEditor)
		{
			return false;
		}
		return !Neo.IsMultiplayer || NetworkServer.active;
	}

	private static void Save()
	{
		_saving = true;
		try
		{
			Asset_Map loadedMap = GameParam.Map;
			StatsTracker.SaveStats();
			Asset_Map autosave = FindAutosave(loadedMap);
			if (autosave != null)
			{
				// Refresh the meta (game mode, preset, thumbnail...) from the running game before overwriting.
				string customName = autosave.customName;
				autosave.FillFrom(loadedMap, includeMapType: false);
				autosave.customName = customName;
				autosave.saveType = Asset_Map.SaveType.Autosave;
				autosave.Disk_Overwrite();
			}
			else
			{
				autosave = Asset_Map.CreateShellFrom(loadedMap, Asset_Map.MapType.Save);
				autosave.customName = AutosaveName(loadedMap);
				autosave.saveType = Asset_Map.SaveType.Autosave;
				autosave.Disk_WriteAsNew(AssetAuthorType.Official);
			}
			Plugin.Log.LogInfo("Autosaved " + autosave.customName);
		}
		catch (Exception ex)
		{
			Plugin.Log.LogError("Autosave failed: " + ex);
		}
		finally
		{
			_saving = false;
		}
	}

	// The autosave of the running map: the loaded save itself when it is one, else the autosave with the same map codename.
	private static Asset_Map FindAutosave(Asset_Map loadedMap)
	{
		if (loadedMap.mapType == Asset_Map.MapType.Save && loadedMap.saveType == Asset_Map.SaveType.Autosave && !loadedMap.IsShell())
		{
			return loadedMap;
		}
		string name = AutosaveName(loadedMap);
		List<Asset_Map> autosaves = AssetDispensary.saves.SendSaveQuery(Asset_Map.SaveType.Autosave);
		foreach (Asset_Map save in autosaves)
		{
			if (save != null && !save.IsShell() && save.codename == loadedMap.codename && save.customName == name)
			{
				return save;
			}
		}
		return null;
	}

	private static string AutosaveName(Asset_Map loadedMap)
	{
		string baseName = !string.IsNullOrEmpty(loadedMap.customName) ? loadedMap.customName : loadedMap.rawDisplayName;
		if (string.IsNullOrEmpty(baseName))
		{
			baseName = I2Helper.TryGet("##gamemenu_savegame_untitled_filename");
		}
		if (baseName.EndsWith(Suffix, StringComparison.Ordinal))
		{
			baseName = baseName.Substring(0, baseName.Length - Suffix.Length);
		}
		foreach (char invalid in Path.GetInvalidFileNameChars())
		{
			baseName = baseName.Replace(invalid, '_');
		}
		return baseName.Trim('-', ' ') + Suffix;
	}
}

// A save made by the player (quicksave, save menu, save & leave) restarts the autosave countdown.
[Feature(Features.AutoSave, Features.AutoSaveInfo)]
[HarmonyPatch]
internal static class AutoSaveResetPatch
{
	private static IEnumerable<MethodBase> TargetMethods()
	{
		yield return AccessTools.Method(typeof(Asset_Map), nameof(Asset_Map.Disk_Overwrite));
		yield return AccessTools.Method(typeof(Asset_Map), nameof(Asset_Map.Disk_WriteAsNew));
	}

	private static void Postfix(Asset_Map __instance)
	{
		if (__instance.mapType == Asset_Map.MapType.Save)
		{
			AutoSave.ResetTimer();
		}
	}
}
