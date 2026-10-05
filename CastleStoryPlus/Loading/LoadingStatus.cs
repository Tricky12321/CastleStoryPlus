using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using Brix.Engine;
using Brix.UI.Builder.Menu.Component;
using UnityEngine;
using UnityEngine.UI;
using LoadingScreen = Brix.NewUI.LoadingScreen.LoadingScreen;

namespace CastleStoryPlus.Loading;

// A line under the loading screen's title that says what the map load is doing ("Placing trees and plants, 12 s"),
// so a slow or stuck load shows where it is. Driven by LoadTiming, which steps the loader's coroutines.
internal static class LoadingStatus
{
	private const string ObjectName = "CastleStoryPlusLoadingStatus";

	private const float FontScale = 0.5f;

	private const long RefreshMilliseconds = 250;

	// Loader steps by iterator method name, innermost known step wins.
	private static readonly Dictionary<string, string> Steps = new Dictionary<string, string>
	{
		{ "BuildStep", "Preparing the map" },
		{ "LoadBasicTerrain", "Loading the terrain" },
		{ "WaitForVoxelEngine", "Waiting for the terrain engine" },
		{ "InitTerrainGravity", "Checking terrain stability" },
		{ "LoadNature", "Placing trees and plants" },
		{ "BuildTerrainMeshes", "Building terrain meshes" },
		{ "LoadGameObjects", "Loading buildings, units and items" },
		{ "LoadState_Coroutine", "Loading buildings, units and items" },
		{ "LoadGridGuide", "Loading the grid guide" },
		{ "AwakeStep", "Preparing pathfinding" },
		{ "InitPathfinding", "Preparing pathfinding" },
		{ "Read_Coroutine", "Preparing pathfinding" },
		{ "StartStep", "Starting the game" }
	};

	private static readonly Stopwatch Total = new Stopwatch();

	private static readonly Stopwatch SinceRefresh = new Stopwatch();

	private static Text _text;

	private static string _shown;

	// The Lua menu being built, set by LuaMenuBatchPatch.
	public static string CurrentMenu;

	public static void Started()
	{
		Total.Reset();
		Total.Start();
		SinceRefresh.Reset();
		SinceRefresh.Start();
		CurrentMenu = null;
		_shown = null;
	}

	public static void Finished()
	{
		Total.Stop();
		CurrentMenu = null;
		if (_text != null)
		{
			_text.text = string.Empty;
		}
		_shown = null;
	}

	// The load stopped with an error: leave it on the screen, the game does not show it.
	public static void Failed(System.Exception error)
	{
		Total.Stop();
		Text text = FindText();
		if (text != null)
		{
			text.text = "Loading failed: " + error.GetType().Name + ": " + error.Message + " (see BepInEx/LogOutput.log)";
		}
	}

	// Called by LoadTiming each time the load yields; refreshed a few times per second.
	public static void Update(Stack<IEnumerator> stack)
	{
		if (SinceRefresh.ElapsedMilliseconds < RefreshMilliseconds)
		{
			return;
		}
		SinceRefresh.Reset();
		SinceRefresh.Start();
		Text text = FindText();
		if (text == null)
		{
			return;
		}
		string line = Describe(stack) + ", " + Total.Elapsed.TotalSeconds.ToString("0") + " s";
		if (line != _shown)
		{
			_shown = line;
			text.text = line;
		}
	}

	private static string Describe(Stack<IEnumerator> stack)
	{
		// Stack enumerates from the innermost step.
		foreach (IEnumerator step in stack)
		{
			if (!Steps.TryGetValue(MethodName(step), out string name))
			{
				continue;
			}
			if (name == "Starting the game" && LuaMenuComponent.CoroutineCount > 0)
			{
				return "Building the interface (" + LuaMenuComponent.CoroutineCount + " menus left" + (string.IsNullOrEmpty(CurrentMenu) ? string.Empty : ": " + MenuName(CurrentMenu)) + ")";
			}
			return name;
		}
		return "Loading";
	}

	// "<LoadNature>c__Iterator5" -> "LoadNature".
	private static string MethodName(IEnumerator step)
	{
		string name = step.GetType().Name;
		int open = name.IndexOf('<');
		int close = name.IndexOf('>');
		return (open >= 0 && close > open) ? name.Substring(open + 1, close - open - 1) : name;
	}

	// "Info/Lua/UI/GameMenu.lua" -> "GameMenu".
	private static string MenuName(string path)
	{
		string name = path.Replace('\\', '/');
		int slash = name.LastIndexOf('/');
		if (slash >= 0)
		{
			name = name.Substring(slash + 1);
		}
		int dot = name.LastIndexOf('.');
		return (dot > 0) ? name.Substring(0, dot) : name;
	}

	// A copy of the loading screen's title, half its size, right under it.
	private static Text FindText()
	{
		if (_text != null)
		{
			return _text;
		}
		GameObject screenObject = GameObject.Find("LoadingScreen");
		LoadingScreen screen = (screenObject != null) ? screenObject.GetComponent<LoadingScreen>() : null;
		Text title = (screen != null) ? screen.Title : null;
		if (title == null)
		{
			return null;
		}
		Transform existing = title.transform.parent.Find(ObjectName);
		GameObject copy = (existing != null) ? existing.gameObject : Object.Instantiate(title.gameObject, title.transform.parent, false);
		copy.name = ObjectName;
		// Only the text and its visual effects (outline, shadow); not the title's localisation or animation.
		foreach (MonoBehaviour behaviour in copy.GetComponents<MonoBehaviour>())
		{
			if (!(behaviour is Text) && !(behaviour is BaseMeshEffect))
			{
				Object.Destroy(behaviour);
			}
		}
		foreach (Transform child in copy.transform)
		{
			Object.Destroy(child.gameObject);
		}
		_text = copy.GetComponent<Text>();
		_text.fontSize = Mathf.Max(10, Mathf.RoundToInt(title.fontSize * FontScale));
		_text.resizeTextForBestFit = false;
		_text.horizontalOverflow = HorizontalWrapMode.Overflow;
		_text.raycastTarget = false;
		_text.text = string.Empty;
		RectTransform titleRect = title.rectTransform;
		RectTransform rect = _text.rectTransform;
		float height = titleRect.rect.height;
		rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, Mathf.Max(titleRect.rect.width, 800f));
		rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height * FontScale);
		// The copy's top edge on the title's bottom edge.
		rect.anchoredPosition = titleRect.anchoredPosition - new Vector2(0f, height * titleRect.pivot.y + height * FontScale * (1f - rect.pivot.y));
		return _text;
	}
}
