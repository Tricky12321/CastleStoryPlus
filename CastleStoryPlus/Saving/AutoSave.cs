using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx.Configuration;
using Brix.Assets;
using Brix.Engine;
using Brix.Game;
using Brix.Input;
using Brix.IO.Serialization;
using Brix.Network;
using Brix.Utils;
using CastleStoryPlus.Core;
using CastleStoryPlus.Loading;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;

namespace CastleStoryPlus.Saving;

// Saves the game every [Saving] AutosaveMinutes minutes of play (paused time and menus do not count).
// Each map gets its own autosave ("<map> (Autosave)"), overwritten every time, so an autosave never
// replaces a manual save or the autosave of another map. Any save made by the player restarts the countdown.
// The slow part of a save is writing every game object as JSON, which the game does in one frame (a visible
// hitch). An autosave does it a slice per frame instead, with the simulation paused meanwhile so all objects
// are saved from the same moment, and hands the result to the game's normal save.
[Feature(Features.AutoSave, Features.AutoSaveInfo)]
internal class AutoSave : MonoBehaviour
{
	// The intervals offered in the mod settings window; 0 = off.
	public static readonly int[] Choices = new int[8] { 0, 1, 2, 3, 5, 10, 15, 30 };

	private const string Suffix = " (Autosave)";

	internal static ConfigEntry<int> Minutes;

	private static float _elapsed;

	private static bool _saving;

	// An autosave is being written (over several frames).
	internal static bool IsSaving => _saving;

	// Time per frame for writing game objects; the game keeps drawing in between.
	private const long SliceMilliseconds = 8;

	// Game objects written ahead by the autosave, used by the next GameObjectSerializer.GetJsonGameState call.
	internal static string PreparedGameObjects;

	private static int _lastGameObjectsLength;

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
		if (_elapsed >= Minutes.Value * 60f && !_saving)
		{
			_elapsed = 0f;
			StartCoroutine(Save());
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

	private static IEnumerator Save()
	{
		_saving = true;
		float timeScale = Time.timeScale;
		Time.timeScale = 0f;
		Stopwatch total = Stopwatch.StartNew();
		int frames = 0;
		string objects = null;
		IEnumerator writing = WriteGameObjects(json => objects = json);
		while (true)
		{
			bool more;
			try
			{
				more = writing.MoveNext();
			}
			catch (Exception ex)
			{
				Plugin.Log.LogError("Autosave: writing game objects failed, using the game's own save: " + ex);
				objects = null;
				break;
			}
			if (!more)
			{
				break;
			}
			frames++;
			yield return null;
		}
		long sliced = total.ElapsedMilliseconds;
		Stopwatch write = Stopwatch.StartNew();
		PreparedGameObjects = objects;
		try
		{
			if (CanSave())
			{
				WriteSave();
			}
		}
		finally
		{
			PreparedGameObjects = null;
			if (Time.timeScale == 0f)
			{
				Time.timeScale = timeScale;
			}
			_saving = false;
		}
		Plugin.Log.LogInfo("Autosave timing: game objects " + sliced + " ms over " + (frames + 1) + " frames, rest of the save " + write.ElapsedMilliseconds + " ms in one frame");
	}

	// Same output as GameObjectSerializer.GetJsonGameState("save"), a slice per frame. One serializer and writer
	// for all objects, so references between objects are written exactly as in one go.
	private static IEnumerator WriteGameObjects(Action<string> done)
	{
		GameObjectSerializer gameObjects = UnityEngine.Object.FindObjectOfType<GameObjectSerializer>();
		if (gameObjects == null)
		{
			yield break;
		}
		List<SerializationComponent> queue = new List<SerializationComponent>(gameObjects._saveQueue);
		// Sized from the last save: growing a ~3 MB builder by doubling leaves ~10 MB of dead buffers per save.
		StringBuilder text = new StringBuilder(Mathf.Max(1024, _lastGameObjectsLength + _lastGameObjectsLength / 8));
		text.Append("[");
		JsonSerializer serializer = JsonSerializer.Create(Io.GetSaveSerializerSettings("save"));
		StringWriter writer = new StringWriter(text);
		FrameBudget budget = new FrameBudget(SliceMilliseconds);
		foreach (SerializationComponent item in queue)
		{
			if (item != null)
			{
				item.Save(writer, serializer);
			}
			if (budget.ExceededNow)
			{
				yield return null;
				budget.Reset();
			}
		}
		text.Remove(text.Length - 1, 1);
		text.Append("]");
		_lastGameObjectsLength = text.Length;
		done(text.ToString());
	}

	private static void WriteSave()
	{
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

// Hands the game objects written ahead by the autosave to the game's save.
[Feature(Features.AutoSave, Features.AutoSaveInfo)]
[HarmonyPatch(typeof(GameObjectSerializer), nameof(GameObjectSerializer.GetJsonGameState))]
internal static class AutoSavePreparedObjectsPatch
{
	private static bool Prefix(string target, ref string __result)
	{
		if (AutoSave.PreparedGameObjects == null || target != "save")
		{
			return true;
		}
		__result = AutoSave.PreparedGameObjects;
		AutoSave.PreparedGameObjects = null;
		return false;
	}
}
