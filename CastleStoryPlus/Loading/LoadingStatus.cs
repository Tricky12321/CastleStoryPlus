using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using Brix.Engine;
using Brix.UI.Builder.Menu.Component;
using UnityEngine;
using UnityEngine.UI;
using LoadingScreen = Brix.NewUI.LoadingScreen.LoadingScreen;

namespace CastleStoryPlus.Loading;

// A line above the loading screen's spinner, which sits over the title ("Entering world"), that says what the map load is doing ("Placing trees
// and plants, 12 s"), so a slow or stuck load shows where it is. Driven by LoadTiming, which steps the loader's
// coroutines.
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

	// A copy of the loading screen's title, half its size, above the spinner over the title.
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
		rect.anchoredPosition = titleRect.anchoredPosition;
		LineAboveTitle keeper = rect.GetComponent<LineAboveTitle>() ?? rect.gameObject.AddComponent<LineAboveTitle>();
		keeper.Line = rect;
		keeper.Title = title;
		keeper.Spinner = (screen.SpinImage != null) ? screen.SpinImage.rectTransform : null;
		return _text;
	}
}

// Keeps the status line right above the spinner (or the title's letters, without one). The screen animates, so it is placed again after it every
// frame.
internal class LineAboveTitle : MonoBehaviour
{
	public RectTransform Line;

	public Text Title;

	public RectTransform Spinner;

	private readonly Vector3[] _corners = new Vector3[4];

	private Text _text;

	private void LateUpdate()
	{
		if (Line == null || Title == null)
		{
			return;
		}
		if (_text == null)
		{
			_text = Line.GetComponent<Text>();
			if (_text == null)
			{
				return;
			}
		}
		// Only the part the letters fill is measured: the boxes are much taller than their text.
		TextBounds(Title, out float below);
		if (Spinner != null && Spinner.gameObject.activeInHierarchy)
		{
			// From its middle and height, not its corners: it spins, and its corners go round with it.
			float spinnerTop = Spinner.TransformPoint(Spinner.rect.center).y + Spinner.rect.height * 0.5f * Mathf.Abs(Spinner.lossyScale.y);
			below = Mathf.Max(below, spinnerTop);
		}
		float lineBottom = TextBounds(_text, out float lineTop);
		float gap = (lineTop - lineBottom) * 0.5f;
		float shift = below + gap - lineBottom;
		// Worked out from where the line is now, so it never drifts.
		if (Mathf.Abs(shift) > 0.01f)
		{
			Line.position += new Vector3(0f, shift, 0f);
		}
	}

	// The bottom and top (world space) of the lines a text fills inside its box, from its alignment.
	private float TextBounds(Text text, out float top)
	{
		text.rectTransform.GetWorldCorners(_corners);
		float boxBottom = _corners[0].y;
		float boxTop = _corners[1].y;
		float boxHeight = text.rectTransform.rect.height;
		float scale = (boxHeight > 0.01f) ? (boxTop - boxBottom) / boxHeight : text.rectTransform.lossyScale.y;
		float height = Mathf.Min(boxTop - boxBottom, text.preferredHeight * scale);
		switch (text.alignment)
		{
		case TextAnchor.UpperLeft:
		case TextAnchor.UpperCenter:
		case TextAnchor.UpperRight:
			top = boxTop;
			break;
		case TextAnchor.LowerLeft:
		case TextAnchor.LowerCenter:
		case TextAnchor.LowerRight:
			top = boxBottom + height;
			break;
		default:
			top = (boxTop + boxBottom + height) / 2f;
			break;
		}
		return top - height;
	}
}
