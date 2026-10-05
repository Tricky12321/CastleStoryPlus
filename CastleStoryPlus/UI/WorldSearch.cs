using System;
using System.Collections.Generic;
using Brix.Engine;
using Brix.External.Factories;
using Brix.Game.AI;
using Brix.Game.Bricktron;
using Brix.Game.Components;
using Brix.Input;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using static CastleStoryPlus.UI.UiKit;
using Object = UnityEngine.Object;

namespace CastleStoryPlus.UI;

// Ctrl+F opens a search bar at the top of the screen: everything in the world whose name matches what is typed gets
// a yellow ring on the ground under it (bricktrons by name, job or kind, enemies too; buildings, blueprints and
// resources by type), also through walls. Several searches can be given at once, separated by commas. Upper and
// lower case do not matter. The rings follow moving units and are kept up to date (twice a second) while the bar is
// open; Escape or the close button clears them. While typing, the game's keys (camera, hotkeys) stand by.
[Feature(Features.WorldSearch, Features.WorldSearchInfo)]
internal class WorldSearch : MonoBehaviour
{
	private const float RefreshInterval = 0.5f;

	private const int MaxRings = 400;

	// The game's input categories turned off while typing, as its chat does, under a link of our own.
	private const string InputLink = "CastleStoryPlusSearch";

	private static readonly Color RingColor = new Color(1f, 0.85f, 0.2f, 1f);

	private static WorldSearch _instance;

	private GameObject _bar;

	private InputField _field;

	private Text _count;

	private int _ringId = -1;

	private CircleHighlighter _ringOwner;

	private readonly Dictionary<GameObject, CircleHighlight> _rings = new Dictionary<GameObject, CircleHighlight>();

	private float _nextRefresh;

	private string _lastQuery = string.Empty;

	private bool _inputBlocked;

	private static void Enable()
	{
		_instance = Plugin.Root.AddComponent<WorldSearch>();
		SceneManager.activeSceneChanged += (Scene from, Scene to) =>
		{
			if (_instance != null)
			{
				_instance.Close();
			}
		};
	}

	// Ctrl+F: the game's own key actions on F stand by.
	internal static bool IsShortcutHeld()
	{
		return (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) && Input.GetKey(KeyCode.F);
	}

	private void Awake()
	{
		GameObject canvas = CreateCanvas("WorldSearchCanvas", transform, 940);
		_bar = CreatePanel("WorldSearchBar", canvas.transform);
		RectTransform rect = _bar.GetComponent<RectTransform>();
		rect.pivot = new Vector2(0.5f, 1f);
		SetRect(rect, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -70f), new Vector2(560f, 40f));
		HorizontalLayoutGroup layout = _bar.AddComponent<HorizontalLayoutGroup>();
		layout.padding = new RectOffset(10, 10, 7, 7);
		layout.spacing = 8f;
		layout.childAlignment = TextAnchor.MiddleLeft;
		layout.childControlWidth = true;
		layout.childControlHeight = true;
		layout.childForceExpandWidth = false;
		layout.childForceExpandHeight = false;
		Fixed(CreateText(_bar.transform, "Search", 15, Yellow, TextAnchor.MiddleLeft).gameObject, 56f, 26f);
		_field = CreateInputField(_bar.transform, "bricktron, job, building, resource ...", 300f);
		_field.onValueChanged.AddListener((string text) => _nextRefresh = 0f);
		_count = CreateText(_bar.transform, string.Empty, 13, Grey, TextAnchor.MiddleRight);
		Fixed(_count.gameObject, 90f, 26f);
		CreateButton(_bar.transform, "X", 26f, Close);
		_bar.SetActive(false);
	}

	private void Update()
	{
		if (IsShortcutHeld() && Input.GetKeyDown(KeyCode.F) && InGame())
		{
			Open();
		}
		if (!_bar.activeSelf)
		{
			return;
		}
		if (!InGame())
		{
			Close();
			return;
		}
		if (Input.GetKeyDown(KeyCode.Escape))
		{
			Close();
			return;
		}
		SetInputBlocked(_field.isFocused);
		if (Time.unscaledTime >= _nextRefresh)
		{
			Refresh();
		}
	}

	private static bool InGame()
	{
		return Architecte.commence && !Architecte.termine && InputModeController.Instance != null;
	}

	private void Open()
	{
		_bar.SetActive(true);
		_field.Select();
		_field.ActivateInputField();
		_nextRefresh = 0f;
	}

	private void Close()
	{
		if (_bar == null)
		{
			return;
		}
		ClearRings();
		SetInputBlocked(false);
		_field.text = string.Empty;
		_lastQuery = string.Empty;
		_bar.SetActive(false);
	}

	private void SetInputBlocked(bool blocked)
	{
		if (blocked == _inputBlocked)
		{
			return;
		}
		ControlContext context = ControlContext.instance;
		if (context == null)
		{
			_inputBlocked = false;
			return;
		}
		if (!context._links.ContainsKey(InputLink) && context._links.TryGetValue("Chat", out List<string> categories))
		{
			foreach (string category in categories.ToArray())
			{
				ControlContext.CreateLink(InputLink, category);
			}
		}
		if (!context._links.ContainsKey(InputLink))
		{
			_inputBlocked = false;
			return;
		}
		ControlContext.SetContext(InputLink, blocked);
		_inputBlocked = blocked;
	}

	private void Refresh()
	{
		_nextRefresh = Time.unscaledTime + RefreshInterval;
		string[] terms = WorldSearchQuery.Terms(_field.text);
		string query = string.Join(",", terms);
		if (query != _lastQuery)
		{
			ClearRings();
			_lastQuery = query;
		}
		if (terms.Length == 0)
		{
			ClearRings();
			_count.text = string.Empty;
			return;
		}
		if (!RingsReady())
		{
			return;
		}
		HashSet<GameObject> found = new HashSet<GameObject>();
		int matches = 0;
		foreach (FactoryImprint imprint in Object.FindObjectsOfType<FactoryImprint>())
		{
			if (imprint == null || imprint.AssetKey == null || imprint.AssetKey.Factory == "UI" || !WorldSearchQuery.Matches(SearchText(imprint), terms))
			{
				continue;
			}
			matches++;
			if (found.Count < MaxRings)
			{
				found.Add(imprint.gameObject);
			}
		}
		// Rings on things that no longer match, are gone or went back to the pool.
		List<GameObject> stale = new List<GameObject>();
		foreach (KeyValuePair<GameObject, CircleHighlight> pair in _rings)
		{
			if (pair.Key == null || !pair.Key.activeInHierarchy || pair.Value == null || !found.Contains(pair.Key))
			{
				stale.Add(pair.Key);
			}
		}
		foreach (GameObject go in stale)
		{
			RemoveRing(go);
		}
		foreach (GameObject go in found)
		{
			if (!_rings.ContainsKey(go))
			{
				AddRing(go);
			}
		}
		_count.text = (matches == 1) ? "1 match" : (matches + " matches");
	}

	// What a thing can be found by: its type, and for a bricktron its name, job and kind; for a blueprint what it
	// becomes. Underscores count as spaces ("Wood_Log" is found by "wood log").
	private static string SearchText(FactoryImprint imprint)
	{
		string text = imprint.AssetKey.Name;
		Labor labor = imprint.GetComponent<Labor>();
		if (labor != null)
		{
			Nom nom = labor.GetComponent<Nom>();
			if (nom != null)
			{
				text += " " + nom.GetNom();
			}
			Occupation occupation = labor.Occupation;
			if (occupation != null)
			{
				text += " " + occupation.CurrentJob + " " + occupation.CurrentOccupation;
			}
			text += " bricktron";
		}
		Blueprint blueprint = imprint.GetComponent<Blueprint>();
		if (blueprint != null && blueprint.BlueprintResult != null)
		{
			text += " blueprint " + blueprint.BlueprintResult.Name;
		}
		return text.Replace('_', ' ').ToLowerInvariant();
	}

	// The game's ring maker lives in the game scene; a new game has a new one.
	private bool RingsReady()
	{
		CircleHighlighter owner = CircleHighlighter._circleHighlighter;
		if (owner == null)
		{
			return false;
		}
		if (owner != _ringOwner)
		{
			_rings.Clear();
			_ringOwner = owner;
			_ringId = CircleHighlighter.GetId();
		}
		return true;
	}

	private void AddRing(GameObject go)
	{
		try
		{
			CircleHighlight ring = CircleHighlighter.HighlightGameObject(_ringId, go, RingColor, Radius(go));
			ring.OccludedOpacity = 1f;
			_rings[go] = ring;
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning("WorldSearch: ring on " + go.name + ": " + ex.Message);
		}
	}

	private void RemoveRing(GameObject go)
	{
		_rings.Remove(go);
		try
		{
			CircleHighlighter.Remove(_ringId, go);
		}
		catch (Exception)
		{
			// The thing (and its ring with it) was already destroyed.
		}
	}

	private void ClearRings()
	{
		_rings.Clear();
		if (_ringOwner == null || _ringOwner != CircleHighlighter._circleHighlighter)
		{
			return;
		}
		try
		{
			CircleHighlighter.Clear(_ringId);
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning("WorldSearch: clearing rings: " + ex.Message);
		}
	}

	// Around the thing's footprint: half its widest side, from its renderers (not the rings on it).
	private static float Radius(GameObject go)
	{
		bool any = false;
		Bounds bounds = new Bounds(go.transform.position, Vector3.zero);
		foreach (Renderer renderer in go.GetComponentsInChildren<Renderer>())
		{
			if (renderer.GetComponentInParent<CircleHighlight>() != null)
			{
				continue;
			}
			if (any)
			{
				bounds.Encapsulate(renderer.bounds);
			}
			else
			{
				bounds = renderer.bounds;
				any = true;
			}
		}
		float half = Mathf.Max(bounds.extents.x, bounds.extents.z);
		return Mathf.Clamp(half + 0.3f, 0.6f, 8f);
	}
}

// The search text: what is typed, split at commas, and whether a thing's text matches any part of it.
internal static class WorldSearchQuery
{
	// Lower case, trimmed, empty parts left out.
	internal static string[] Terms(string text)
	{
		List<string> terms = new List<string>();
		foreach (string part in (text ?? string.Empty).Split(','))
		{
			string term = part.Trim().ToLowerInvariant();
			if (term.Length > 0)
			{
				terms.Add(term);
			}
		}
		return terms.ToArray();
	}

	// text: lower case, as SearchText makes it.
	internal static bool Matches(string text, string[] terms)
	{
		foreach (string term in terms)
		{
			if (text.Contains(term))
			{
				return true;
			}
		}
		return false;
	}
}

// The game's key actions on F do not fire for Ctrl+F.
[Feature(Features.WorldSearch, Features.WorldSearchInfo)]
[HarmonyPatch(typeof(Brix.Utils.UI.KeyBindingsUtility), nameof(Brix.Utils.UI.KeyBindingsUtility.RegisterAction))]
internal static class WorldSearchKeyActionPatch
{
	private static void Prefix(ref Action<Rewired.InputActionEventData> action)
	{
		Action<Rewired.InputActionEventData> inner = action;
		action = (Rewired.InputActionEventData data) =>
		{
			if (!WorldSearch.IsShortcutHeld())
			{
				inner(data);
			}
		};
	}
}
