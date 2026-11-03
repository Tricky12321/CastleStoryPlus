using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Game.Semantique;
using Brix.Input;
using Brix.Utils;
using Brix.Lua;
using CastleStoryPlus.Core;
using CastleStoryPlus.UI;
using UnityEngine;
using UnityEngine.UI;

namespace CastleStoryPlus.Building;

// Building that stands still for want of material says so: a box on the right, under the resource list, lists every resource the player's
// build tasks still need more of than the stockpiles hold ("Planks: 42 missing, 0 in stock, logs: 0"), with what it
// is made from where that is the usual hold-up. A small icon of the resource floats over every blueprint still
// waiting for material: on red when the stockpiles do not hold enough for it, on green when they do (the stock is
// shared out over the blueprints), on blue when a worker is fetching or bringing it. A shortage shows after it has lasted ShowAfterSeconds (material on its way in a
// worker's hands is not in stock for a moment). The box's button folds it to a small column of icons with the missing
// counts ([BuildShortage] Minified remembers it). Counted every RefreshSeconds on this machine, for its own player.
// The icons over the blueprints are many while a lot is built and stay through fights, so they show only when asked
// for ([BuildShortage] Icons): Toggle (the default) shows and hides them with a press of Alt on its own, Hold shows
// them while Alt is held, Always keeps them. Alt with another key or a click is the game's own and changes nothing.
[Feature(Features.BuildShortage, Features.BuildShortageInfo)]
internal sealed class BuildShortage : MonoBehaviour
{
	private const float RefreshSeconds = 2f;

	private const float ShowAfterSeconds = 8f;

	private const int MaxRows = 6;

	private const int MaxIcons = 150;

	private const float IconSize = 26f;

	// Per resource: its name and missing count on one line, what is in stock (and what it is made from) under it.
	private const float RowHeight = 40f;

	private const float TitleHeight = 30f;

	private const float Width = 220f;

	private const float MiniWidth = 84f;

	private const float ChipHeight = 26f;

	private const float Gap = 8f;

	private static readonly Vector3[] Corners = new Vector3[4];

	private float _height;

	// The resource list on the right (the game menu's), which the box sits under.
	private RectTransform _list;

	private static ConfigEntry<bool> _minified;

	internal enum IconsMode
	{
		Toggle,
		Hold,
		Always
	}

	internal static ConfigEntry<IconsMode> Icons;

	// Toggle: whether Alt has shown the icons. Alt counts only when nothing else was pressed while it was held.
	private static bool _iconsOn;

	private static bool _altAlone;

	private bool _drawnIconsShown;

	private static readonly Color MissingColor = new Color(1f, 0.35f, 0.3f, 1f);

	private static readonly Color DetailColor = new Color(0.7f, 0.7f, 0.7f, 1f);

	// What a resource is made from, where that is what the player has to go and get.
	private static readonly Dictionary<LuaCrafting.LuaResource, LuaCrafting.LuaResource> MadeFrom = new Dictionary<LuaCrafting.LuaResource, LuaCrafting.LuaResource>
	{
		{ LuaCrafting.LuaResource.PlankBlock, LuaCrafting.LuaResource.WoodBlock },
		{ LuaCrafting.LuaResource.Iron, LuaCrafting.LuaResource.RawIron },
		{ LuaCrafting.LuaResource.Rope, LuaCrafting.LuaResource.Plant },
		{ LuaCrafting.LuaResource.Fabric, LuaCrafting.LuaResource.Plant },
		{ LuaCrafting.LuaResource.Glass, LuaCrafting.LuaResource.Stones },
		{ LuaCrafting.LuaResource.Cog, LuaCrafting.LuaResource.Iron }
	};

	private sealed class Shortage
	{
		public LuaCrafting.LuaResource Resource;

		public Type Type;

		public int Missing;

		public int Stock;

		public float Since;
	}

	private readonly Dictionary<Type, Shortage> _shortages = new Dictionary<Type, Shortage>();

	private readonly Dictionary<Type, int> _needed = new Dictionary<Type, int>();

	private enum Supply
	{
		Missing,
		InStock,
		OnTheWay
	}

	private struct Waiting
	{
		public Blueprint Blueprint;

		public LuaCrafting.LuaResource Resource;

		public Supply Supply;

		// Where its icon floats (blueprints stand still), taken when counted.
		public Vector3 Position;

		// Built or removed since it was counted: no icon until the next count.
		public bool Gone;
	}

	// The icons over the blueprints are only drawn again when what they show changed (a new count, a blueprint gone)
	// or the camera moved; the resource list and the camera are looked up again at most this often.
	private const float LookupSeconds = 0.5f;

	// Bumped with every count, which fills _waiting anew.
	private int _waitingVersion;

	private int _drawnVersion = -1;

	private Camera _camera;

	private float _nextCameraLookup;

	private Camera _drawnCamera;

	private Matrix4x4 _drawnView;

	private Matrix4x4 _drawnProjection;

	private Rect _drawnPixels;

	// The icons shown now.
	private int _usedIcons;

	private float _nextListLookup;

	private Image _listBackground;

	// The working sets of a count, kept so a count makes no garbage of its own.
	private readonly List<Blueprint> _blueprints = new List<Blueprint>();

	private readonly HashSet<Type> _lacking = new HashSet<Type>();

	private readonly List<Type> _over = new List<Type>();

	// One resource of each type, to ask the stock of (the stock only looks at its type).
	private readonly Dictionary<Type, Ressource> _resourceOf = new Dictionary<Type, Ressource>();

	private static readonly ShortageOrder MostMissingFirst = new ShortageOrder();

	private sealed class ShortageOrder : IComparer<Shortage>
	{
		public int Compare(Shortage a, Shortage b)
		{
			return b.Missing.CompareTo(a.Missing);
		}
	}

	private static readonly Color MissingBack = new Color(0.45f, 0.05f, 0.05f, 0.85f);

	private static readonly Color InStockBack = new Color(0.08f, 0.38f, 0.1f, 0.85f);

	private static readonly Color OnTheWayBack = new Color(0.08f, 0.25f, 0.55f, 0.85f);

	// The blueprints waiting for material: the resource each shows and how it stands.
	private readonly List<Waiting> _waiting = new List<Waiting>();

	// Stock not yet shared out to a blueprint, per resource (MarkBlueprints).
	private readonly Dictionary<Type, int> _left = new Dictionary<Type, int>();

	private readonly List<Shortage> _shown = new List<Shortage>();

	private float _nextRefresh;

	private RectTransform _canvas;

	private RectTransform _box;

	private Text _title;

	private readonly List<Image> _rowIcons = new List<Image>();

	private readonly List<Text[]> _rowTexts = new List<Text[]>();

	private readonly List<Image> _icons = new List<Image>();

	// Per icon: its marker (the coloured back) and what it was last drawn for (resource and supply, -1: nothing).
	private readonly List<RectTransform> _iconMarkers = new List<RectTransform>();

	private readonly List<Image> _iconBacks = new List<Image>();

	private readonly List<int> _iconKeys = new List<int>();

	// Per row of the box: what its texts were last written for, so unchanged rows build no new strings.
	private readonly int[] _rowResource = new int[MaxRows];

	private readonly int[] _rowMissing = new int[MaxRows];

	private readonly int[] _rowStock = new int[MaxRows];

	private readonly int[] _rowSourceStock = new int[MaxRows];

	private readonly int[] _chipResource = new int[MaxRows];

	private readonly int[] _chipMissing = new int[MaxRows];

	// The full table and the folded strip of chips (icon and missing count).
	private RectTransform _table;

	private RectTransform _strip;

	private readonly List<Image> _chipIcons = new List<Image>();

	private readonly List<Text> _chipTexts = new List<Text>();

	private Text _toggleLabel;

	private static void Enable()
	{
		_minified = Plugin.Cfg.Bind("BuildShortage", "Minified", false, "Whether the box of missing building materials is folded to a small strip of icons with the missing counts.");
		Icons = Plugin.Cfg.Bind("BuildShortage", "Icons", IconsMode.Toggle, "When the icons over blueprints waiting for material show: Toggle (a press of Alt on its own shows and hides them), Hold (while Alt is held) or Always.");
		Plugin.Root.AddComponent<BuildShortage>();
	}

	private void Update()
	{
		WatchAlt();
		if (Time.time < _nextRefresh)
		{
			return;
		}
		_nextRefresh = Time.time + RefreshSeconds;
		try
		{
			Count();
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning("BuildShortage: " + ex.Message);
			_shown.Clear();
			_waiting.Clear();
		}
		_waitingVersion++;
		DrawBox();
	}

	private static bool AltHeld()
	{
		return Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
	}

	private static void WatchAlt()
	{
		if (Input.GetKeyDown(KeyCode.LeftAlt) || Input.GetKeyDown(KeyCode.RightAlt))
		{
			_altAlone = true;
			return;
		}
		if (!_altAlone)
		{
			return;
		}
		if (Input.anyKeyDown)
		{
			_altAlone = false;
			return;
		}
		if (!AltHeld())
		{
			_altAlone = false;
			if (Icons.Value == IconsMode.Toggle && !InputModeController.IsInMenu())
			{
				_iconsOn = !_iconsOn;
			}
		}
	}

	private static bool IconsShown()
	{
		switch (Icons.Value)
		{
			case IconsMode.Always:
				return true;
			case IconsMode.Hold:
				return AltHeld();
			default:
				return _iconsOn;
		}
	}

	private void LateUpdate()
	{
		Place();
		DrawIcons();
	}

	// What all the player's build tasks still need, against the stockpiles.
	private void Count()
	{
		_needed.Clear();
		_waiting.Clear();
		_shown.Clear();
		Faction faction = (User.LocalUser != null) ? User.LocalUser.faction : null;
		if (faction == null || UIGameObserver.projects == null || UIGameObserver.projects.all == null)
		{
			_shortages.Clear();
			return;
		}
		GameUi.CraftingCatalog.EnsureMapped();
		List<Blueprint> blueprints = _blueprints;
		blueprints.Clear();
		foreach (Project project in UIGameObserver.projects.all)
		{
			BuildGoalProvider provider = (project != null) ? project.GetComponent<BuildGoalProvider>() : null;
			if (provider == null)
			{
				continue;
			}
			foreach (Blueprint blueprint in provider.TrackedBlueprints)
			{
				if (blueprint.IsNullOrReleased() || blueprint.IsAnti || blueprint.recepteur == null)
				{
					continue;
				}
				blueprints.Add(blueprint);
				Description needed = blueprint.recepteur.BaseCapacity;
				Description delivered = blueprint.recepteur.ContentDescription;
				foreach (KeyValuePair<Type, Adjectif> pair in needed.DicoAdjectif)
				{
					if (!(pair.Value is Ressource resource))
					{
						continue;
					}
					int missing = needed.Value(resource) - delivered.Value(resource);
					if (missing > 0)
					{
						_needed.TryGetValue(pair.Key, out int sum);
						_needed[pair.Key] = sum + missing;
					}
				}
			}
		}
		Dictionary<AdjectiveKey, LuaCrafting.LuaResource> toLua = LuaCrafting._adjectiveToLuaResource;
		HashSet<Type> lacking = _lacking;
		lacking.Clear();
		foreach (KeyValuePair<Type, int> need in _needed)
		{
			Ressource resource = ResourceOf(need.Key);
			if (resource == null || toLua == null || !toLua.TryGetValue(resource.GetKey(), out LuaCrafting.LuaResource lua))
			{
				continue;
			}
			int stock = Economy.CraftLoopLimit.Stock(faction, resource, cached: true);
			if (stock >= need.Value)
			{
				continue;
			}
			lacking.Add(need.Key);
			if (!_shortages.TryGetValue(need.Key, out Shortage shortage))
			{
				shortage = new Shortage { Type = need.Key, Resource = lua, Since = Time.time };
				_shortages[need.Key] = shortage;
			}
			shortage.Missing = need.Value - stock;
			shortage.Stock = stock;
		}
		List<Type> over = _over;
		over.Clear();
		foreach (Type type in _shortages.Keys)
		{
			if (!lacking.Contains(type))
			{
				over.Add(type);
			}
		}
		foreach (Type type in over)
		{
			_shortages.Remove(type);
		}
		foreach (Shortage shortage in _shortages.Values)
		{
			if (Time.time - shortage.Since >= ShowAfterSeconds)
			{
				_shown.Add(shortage);
			}
		}
		_shown.Sort(MostMissingFirst);
		MarkBlueprints(faction, blueprints, toLua);
		blueprints.Clear();
	}

	private Ressource ResourceOf(Type type)
	{
		if (!_resourceOf.TryGetValue(type, out Ressource resource))
		{
			resource = Adjectif.New(type, 1) as Ressource;
			_resourceOf[type] = resource;
		}
		return resource;
	}

	// Every blueprint still waiting for material gets an icon: on the way when a worker is fetching or bringing it,
	// else in stock when the stockpiles hold enough for it (the stock is shared out over the blueprints in order),
	// else missing.
	private void MarkBlueprints(Faction faction, List<Blueprint> blueprints, Dictionary<AdjectiveKey, LuaCrafting.LuaResource> toLua)
	{
		if (toLua == null)
		{
			return;
		}
		_left.Clear();
		foreach (Blueprint blueprint in blueprints)
		{
			if (_waiting.Count >= MaxIcons)
			{
				break;
			}
			Description needed = blueprint.recepteur.BaseCapacity;
			Description delivered = blueprint.recepteur.ContentDescription;
			LuaCrafting.LuaResource first = default;
			LuaCrafting.LuaResource lacking = default;
			bool any = false;
			bool enough = true;
			foreach (KeyValuePair<Type, Adjectif> pair in needed.DicoAdjectif)
			{
				if (!(pair.Value is Ressource resource))
				{
					continue;
				}
				int missing = needed.Value(resource) - delivered.Value(resource);
				if (missing <= 0 || !toLua.TryGetValue(resource.GetKey(), out LuaCrafting.LuaResource lua))
				{
					continue;
				}
				if (!any)
				{
					first = lua;
					any = true;
				}
				if (!_left.TryGetValue(pair.Key, out int left))
				{
					left = Economy.CraftLoopLimit.Stock(faction, ResourceOf(pair.Key), cached: true);
				}
				if (left < missing && enough)
				{
					enough = false;
					lacking = lua;
				}
				_left[pair.Key] = left;
			}
			if (!any)
			{
				continue;
			}
			Vector3 position = blueprint.transform.position + Vector3.up * 1.2f;
			if (OnTheWay(blueprint))
			{
				_waiting.Add(new Waiting { Blueprint = blueprint, Resource = first, Supply = Supply.OnTheWay, Position = position });
				continue;
			}
			if (enough)
			{
				// The stock this blueprint takes is no longer there for the next ones.
				foreach (KeyValuePair<Type, Adjectif> pair in needed.DicoAdjectif)
				{
					if (pair.Value is Ressource resource && _left.TryGetValue(pair.Key, out int left))
					{
						_left[pair.Key] = left - Math.Max(0, needed.Value(resource) - delivered.Value(resource));
					}
				}
			}
			_waiting.Add(new Waiting { Blueprint = blueprint, Resource = enough ? first : lacking, Supply = enough ? Supply.InStock : Supply.Missing, Position = position });
		}
	}

	// A worker is fetching or bringing material for the blueprint.
	private static bool OnTheWay(Blueprint blueprint)
	{
		BuildGoal goal = blueprint.GetComponent<BuildGoal>();
		if (goal == null || goal.workers == null)
		{
			return false;
		}
		foreach (Labor worker in goal.workers)
		{
			if (WorkerAI.GoalWorkers.IsOn(goal, worker))
			{
				return true;
			}
		}
		return false;
	}

	private void DrawBox()
	{
		if (_shown.Count == 0)
		{
			if (_box != null)
			{
				_box.gameObject.SetActive(false);
			}
			return;
		}
		EnsureBox();
		_box.gameObject.SetActive(true);
		Faction faction = (User.LocalUser != null) ? User.LocalUser.faction : null;
		int rows = Mathf.Min(_shown.Count, MaxRows);
		bool mini = _minified.Value;
		_table.gameObject.SetActive(!mini);
		_strip.gameObject.SetActive(mini);
		_toggleLabel.text = mini ? "+" : "-";
		if (mini)
		{
			for (int i = 0; i < MaxRows; i++)
			{
				bool used = i < rows;
				_chipIcons[i].transform.parent.gameObject.SetActive(used);
				if (used && ((int)_shown[i].Resource != _chipResource[i] || _shown[i].Missing != _chipMissing[i]))
				{
					_chipIcons[i].sprite = GameUi.CraftingCatalog.ResourceIcon(_shown[i].Resource);
					_chipTexts[i].text = _shown[i].Missing.ToString();
					_chipResource[i] = (int)_shown[i].Resource;
					_chipMissing[i] = _shown[i].Missing;
				}
			}
			_height = TitleHeight + rows * ChipHeight + 6f;
			Place();
			return;
		}
		for (int i = 0; i < MaxRows; i++)
		{
			bool used = i < rows;
			_rowIcons[i].transform.parent.gameObject.SetActive(used);
			if (!used)
			{
				continue;
			}
			Shortage shortage = _shown[i];
			LuaCrafting.LuaResource source = default;
			bool hasSource = faction != null && MadeFrom.TryGetValue(shortage.Resource, out source);
			int sourceStock = hasSource ? StockOf(faction, source) : -1;
			// The same as last written: no new strings.
			if ((int)shortage.Resource == _rowResource[i] && shortage.Missing == _rowMissing[i] && shortage.Stock == _rowStock[i] && sourceStock == _rowSourceStock[i])
			{
				continue;
			}
			_rowResource[i] = (int)shortage.Resource;
			_rowMissing[i] = shortage.Missing;
			_rowStock[i] = shortage.Stock;
			_rowSourceStock[i] = sourceStock;
			_rowIcons[i].sprite = GameUi.CraftingCatalog.ResourceIcon(shortage.Resource);
			Text[] cells = _rowTexts[i];
			cells[0].text = GameUi.CraftingCatalog.ResourceName(shortage.Resource);
			cells[1].text = shortage.Missing.ToString();
			string from = hasSource
				? ", from " + GameUi.CraftingCatalog.ResourceName(source) + " (" + sourceStock + ")"
				: string.Empty;
			cells[2].text = "In stock " + shortage.Stock + from;
		}
		_height = TitleHeight + rows * RowHeight + 8f;
		Place();
	}

	// Under the resource list on the right, as wide as it (it grows with the resources shown); where the list is
	// hidden, at the same place on the right.
	private void Place()
	{
		if (_box == null || !_box.gameObject.activeSelf)
		{
			return;
		}
		// Looked for by name at most every LookupSeconds while it is missing or hidden (GameObject.Find walks the scene).
		if ((_list == null || !_list.gameObject.activeInHierarchy) && Time.unscaledTime >= _nextListLookup)
		{
			_nextListLookup = Time.unscaledTime + LookupSeconds;
			GameObject list = GameObject.Find("CS resource list");
			_list = (list != null) ? list.GetComponent<RectTransform>() : null;
			_listBackground = (_list != null) ? _list.GetComponent<Image>() : null;
		}
		float scale = _canvas.localScale.x;
		bool mini = _minified.Value;
		// Only what changed is set: an unchanged box does not make its canvas build again.
		if (_list != null && _list.gameObject.activeInHierarchy && _listBackground != null && _listBackground.enabled && scale > 0f)
		{
			_list.GetWorldCorners(Corners);
			float width = (Corners[3].x - Corners[0].x) / scale;
			SetSize(new Vector2(mini ? MiniWidth : width, _height));
			Vector3 position = new Vector3(Corners[3].x, Corners[3].y - Gap * scale, 0f);
			if (_box.position != position)
			{
				_box.position = position;
			}
			return;
		}
		SetSize(new Vector2(mini ? MiniWidth : Width, _height));
		Vector2 fallback = new Vector2(-56f, -480f);
		if (_box.anchoredPosition != fallback)
		{
			_box.anchoredPosition = fallback;
		}
	}

	private void SetSize(Vector2 size)
	{
		if (_box.sizeDelta != size)
		{
			_box.sizeDelta = size;
		}
	}

	private static int StockOf(Faction faction, LuaCrafting.LuaResource lua)
	{
		try
		{
			Ressource resource = LuaCrafting.ToResource(lua);
			return (resource != null) ? Economy.CraftLoopLimit.Stock(faction, resource, cached: true) : 0;
		}
		catch (KeyNotFoundException)
		{
			return 0;
		}
	}

	private void EnsureCanvas()
	{
		if (_canvas != null)
		{
			return;
		}
		// Only the fold button takes clicks: everything else has raycastTarget off.
		_canvas = UiKit.CreateCanvas("CastleStoryPlusBuildShortage", transform, 0).GetComponent<RectTransform>();
	}

	private void EnsureBox()
	{
		if (_box != null)
		{
			return;
		}
		EnsureCanvas();
		for (int i = 0; i < MaxRows; i++)
		{
			_rowResource[i] = -1;
			_chipResource[i] = -1;
		}
		_box = UiKit.CreateRect("Box", _canvas);
		// Top right, under the resource list (Place); grows downwards.
		UiKit.SetRect(_box, new Vector2(1f, 1f), new Vector2(1f, 1f), Vector2.zero, new Vector2(Width, 100f));
		_box.pivot = new Vector2(1f, 1f);
		Image background = _box.gameObject.AddComponent<Image>();
		background.color = UiKit.Background;
		background.raycastTarget = false;
		Button toggle = UiKit.CreateButton(_box, "-", 0f, () =>
		{
			_minified.Value = !_minified.Value;
			DrawBox();
		});
		RectTransform toggleRect = toggle.GetComponent<RectTransform>();
		toggleRect.anchorMin = new Vector2(1f, 1f);
		toggleRect.anchorMax = new Vector2(1f, 1f);
		toggleRect.pivot = new Vector2(1f, 1f);
		toggleRect.anchoredPosition = new Vector2(-4f, -4f);
		toggleRect.sizeDelta = new Vector2(22f, 22f);
		_toggleLabel = toggle.GetComponentInChildren<Text>();
		_toggleLabel.fontStyle = FontStyle.Bold;
		_table = UiKit.CreateRect("Table", _box);
		UiKit.SetRect(_table, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
		_strip = UiKit.CreateRect("Strip", _box);
		UiKit.SetRect(_strip, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
		for (int i = 0; i < MaxRows; i++)
		{
			RectTransform chip = UiKit.CreateRect("Chip", _strip);
			chip.anchorMin = new Vector2(0f, 1f);
			chip.anchorMax = new Vector2(1f, 1f);
			chip.pivot = new Vector2(0f, 1f);
			chip.anchoredPosition = new Vector2(8f, -TitleHeight - i * ChipHeight);
			chip.sizeDelta = new Vector2(-16f, ChipHeight);
			RectTransform chipIcon = UiKit.CreateRect("Icon", chip);
			chipIcon.anchorMin = new Vector2(0f, 0.5f);
			chipIcon.anchorMax = new Vector2(0f, 0.5f);
			chipIcon.pivot = new Vector2(0f, 0.5f);
			chipIcon.anchoredPosition = Vector2.zero;
			chipIcon.sizeDelta = new Vector2(22f, 22f);
			Image image = chipIcon.gameObject.AddComponent<Image>();
			image.preserveAspect = true;
			image.raycastTarget = false;
			Text count = UiKit.CreateText(chip, string.Empty, 13, MissingColor, TextAnchor.MiddleRight);
			count.fontStyle = FontStyle.Bold;
			count.raycastTarget = false;
			RectTransform countRect = count.rectTransform;
			countRect.anchorMin = new Vector2(0f, 0f);
			countRect.anchorMax = new Vector2(1f, 1f);
			countRect.offsetMin = new Vector2(24f, 0f);
			countRect.offsetMax = Vector2.zero;
			_chipIcons.Add(image);
			_chipTexts.Add(count);
		}
		_title = UiKit.CreateText(_table, "Waiting for materials", 14, MissingColor, TextAnchor.MiddleLeft);
		_title.fontStyle = FontStyle.Bold;
		RectTransform title = _title.rectTransform;
		title.anchorMin = new Vector2(0f, 1f);
		title.anchorMax = new Vector2(1f, 1f);
		title.pivot = new Vector2(0f, 1f);
		title.anchoredPosition = new Vector2(8f, -4f);
		title.sizeDelta = new Vector2(-40f, 22f);
		for (int i = 0; i < MaxRows; i++)
		{
			RectTransform row = Row(-TitleHeight - i * RowHeight);
			RectTransform iconRect = UiKit.CreateRect("Icon", row);
			iconRect.anchorMin = new Vector2(0f, 1f);
			iconRect.anchorMax = new Vector2(0f, 1f);
			iconRect.pivot = new Vector2(0f, 1f);
			iconRect.anchoredPosition = Vector2.zero;
			iconRect.sizeDelta = new Vector2(22f, 22f);
			Image icon = iconRect.gameObject.AddComponent<Image>();
			icon.preserveAspect = true;
			icon.raycastTarget = false;
			Text[] cells = new Text[3];
			// Name, missing count (right-aligned), and under them the stock line.
			cells[0] = Cell(row, 26f, 0f, -26f - 50f, 22f, TextAnchor.MiddleLeft, 13, UiKit.TextColor);
			cells[1] = Cell(row, 0f, 0f, 0f, 22f, TextAnchor.MiddleRight, 13, MissingColor);
			cells[1].fontStyle = FontStyle.Bold;
			cells[2] = Cell(row, 26f, -21f, -26f, 16f, TextAnchor.MiddleLeft, 11, DetailColor);
			_rowIcons.Add(icon);
			_rowTexts.Add(cells);
		}
	}

	private RectTransform Row(float y)
	{
		RectTransform row = UiKit.CreateRect("Row", _table);
		row.anchorMin = new Vector2(0f, 1f);
		row.anchorMax = new Vector2(1f, 1f);
		row.pivot = new Vector2(0f, 1f);
		row.anchoredPosition = new Vector2(8f, y);
		row.sizeDelta = new Vector2(-16f, RowHeight);
		return row;
	}

	// A text in the row, x and y from its top left, as wide as the row plus extraWidth.
	private static Text Cell(RectTransform row, float x, float y, float extraWidth, float height, TextAnchor anchor, int size, Color color)
	{
		Text text = UiKit.CreateText(row, string.Empty, size, color, anchor);
		text.raycastTarget = false;
		text.horizontalOverflow = HorizontalWrapMode.Overflow;
		RectTransform rect = text.rectTransform;
		rect.anchorMin = new Vector2(0f, 1f);
		rect.anchorMax = new Vector2(1f, 1f);
		rect.pivot = new Vector2(0f, 1f);
		rect.anchoredPosition = new Vector2(x, y);
		rect.sizeDelta = new Vector2(extraWidth, height);
		return text;
	}

	// An icon of the resource over each waiting blueprint, the same size at any distance, on red (missing), green (in
	// stock) or blue (on the way). Drawn again only after a count, when a blueprint is gone or when the camera moved.
	private void DrawIcons()
	{
		if (_waiting.Count == 0 && _usedIcons == 0 && _drawnVersion == _waitingVersion)
		{
			return;
		}
		bool shown = IconsShown();
		Camera camera = (_waiting.Count > 0 && shown) ? MainCamera() : null;
		bool fresh = _drawnVersion != _waitingVersion;
		bool dirty = fresh || camera != _drawnCamera || shown != _drawnIconsShown;
		_drawnIconsShown = shown;
		if (camera != null)
		{
			Matrix4x4 view = camera.worldToCameraMatrix;
			Matrix4x4 projection = camera.projectionMatrix;
			Rect pixels = camera.pixelRect;
			if (view != _drawnView || projection != _drawnProjection || pixels != _drawnPixels)
			{
				dirty = true;
				_drawnView = view;
				_drawnProjection = projection;
				_drawnPixels = pixels;
			}
		}
		if (!dirty)
		{
			for (int i = 0; i < _waiting.Count; i++)
			{
				Waiting entry = _waiting[i];
				if (!entry.Gone && entry.Blueprint.IsNullOrReleased())
				{
					dirty = true;
					break;
				}
			}
			if (!dirty)
			{
				return;
			}
		}
		_drawnCamera = camera;
		_drawnVersion = _waitingVersion;
		int used = 0;
		if (camera != null)
		{
			for (int i = 0; i < _waiting.Count; i++)
			{
				Waiting entry = _waiting[i];
				if (entry.Gone)
				{
					continue;
				}
				if (entry.Blueprint.IsNullOrReleased())
				{
					entry.Gone = true;
					_waiting[i] = entry;
					continue;
				}
				Vector3 screen = camera.WorldToScreenPoint(entry.Position);
				if (screen.z <= 0f || screen.x < 0f || screen.y < 0f || screen.x > Screen.width || screen.y > Screen.height)
				{
					continue;
				}
				Image icon = IconAt(used);
				int key = (int)entry.Resource * 4 + (int)entry.Supply;
				// After a count the sprite is asked for again (as before, in case it was not there yet).
				if (fresh || _iconKeys[used] != key)
				{
					icon.sprite = GameUi.CraftingCatalog.ResourceIcon(entry.Resource);
					_iconBacks[used].color = (entry.Supply == Supply.Missing) ? MissingBack : (entry.Supply == Supply.InStock) ? InStockBack : OnTheWayBack;
					_iconKeys[used] = key;
				}
				RectTransform marker = _iconMarkers[used];
				marker.position = new Vector3(screen.x, screen.y, 0f);
				if (!marker.gameObject.activeSelf)
				{
					marker.gameObject.SetActive(true);
				}
				used++;
			}
		}
		for (int i = used; i < _usedIcons; i++)
		{
			GameObject marker = _iconMarkers[i].gameObject;
			if (marker.activeSelf)
			{
				marker.SetActive(false);
			}
		}
		_usedIcons = used;
	}

	// Camera.main, looked up again at most every LookupSeconds while the one found is still there and enabled.
	private Camera MainCamera()
	{
		if (_camera == null || !_camera.isActiveAndEnabled || Time.unscaledTime >= _nextCameraLookup)
		{
			_nextCameraLookup = Time.unscaledTime + LookupSeconds;
			_camera = Camera.main;
		}
		return _camera;
	}

	private Image IconAt(int index)
	{
		while (_icons.Count <= index)
		{
			EnsureCanvas();
			RectTransform rect = UiKit.CreateRect("WaitingIcon", _canvas);
			rect.sizeDelta = new Vector2(IconSize, IconSize);
			Image back = rect.gameObject.AddComponent<Image>();
			back.color = MissingBack;
			back.raycastTarget = false;
			RectTransform inner = UiKit.CreateRect("Icon", rect);
			inner.anchorMin = Vector2.zero;
			inner.anchorMax = Vector2.one;
			inner.offsetMin = new Vector2(3f, 3f);
			inner.offsetMax = new Vector2(-3f, -3f);
			Image icon = inner.gameObject.AddComponent<Image>();
			icon.preserveAspect = true;
			icon.raycastTarget = false;
			_icons.Add(icon);
			_iconMarkers.Add(rect);
			_iconBacks.Add(back);
			_iconKeys.Add(-1);
		}
		return _icons[index];
	}
}
