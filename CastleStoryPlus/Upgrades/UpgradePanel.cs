using System.Collections.Generic;
using System.IO;
using Path = System.IO.Path;
using System.Text;
using Brix.Components;
using Brix.External.Factories;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Game.Network;
using Brix.Game.Semantique;
using Brix.Lifecycle.Pooling;
using Brix.UI.GameSelector;
using CastleStoryPlus.Core;
using UnityEngine;
using UnityEngine.UI;
using static CastleStoryPlus.UI.UiKit;

namespace CastleStoryPlus.Upgrades;

// Upgrade window, open while a smithy or an armoury is selected: one row per upgrade line of the building with its
// icon, researched tier, what the next tier does and costs (with what the stockpiles hold), and a button to queue
// the research; then the building's queue as a list. Uses the game's own crafting commands.
[Feature(Features.Upgrades, Features.UpgradesInfo)]
internal class UpgradePanel : MonoBehaviour
{
	private class LineRow
	{
		public UpgradeLine Line;

		public Image Icon;

		public Text Title;

		public Text Effect;

		public Text Cost;

		public Button Research;

		public Text ResearchLabel;
	}

	private class QueueRow
	{
		public GameObject Row;

		public Text Text;
	}

	private const float RefreshSeconds = 0.3f;

	private const int MaxQueue = 5;

	private const float Width = 560f;

	private static readonly Color Bad = new Color(0.9f, 0.3f, 0.22f, 1f);

	private static readonly Color Done = new Color(0.45f, 0.85f, 0.4f, 1f);

	private static readonly Dictionary<string, Sprite> Icons = new Dictionary<string, Sprite>();

	private UpgradeStation _station;

	private UpgradeHall _hall;

	private GameObject _window;

	private readonly Dictionary<UpgradeHall, GameObject> _windows = new Dictionary<UpgradeHall, GameObject>();

	private readonly Dictionary<UpgradeHall, List<LineRow>> _rows = new Dictionary<UpgradeHall, List<LineRow>>();

	private readonly Dictionary<UpgradeHall, Text> _queueTitles = new Dictionary<UpgradeHall, Text>();

	private readonly Dictionary<UpgradeHall, Text> _queueEmpties = new Dictionary<UpgradeHall, Text>();

	// Research station: how many of the crystal its research is paid in the stockpiles hold, in the header.
	private readonly Dictionary<UpgradeHall, Text> _crystalTexts = new Dictionary<UpgradeHall, Text>();

	private readonly Dictionary<UpgradeHall, Transform> _queueLists = new Dictionary<UpgradeHall, Transform>();

	private readonly List<QueueRow> _queueRows = new List<QueueRow>();

	private string _queueShown;

	private float _nextRefresh;

	private readonly Dictionary<System.Type, int> _stock = new Dictionary<System.Type, int>();

	private static void Enable()
	{
		Plugin.Root.AddComponent<UpgradePanel>();
	}

	private void Update()
	{
		CraftingStation selected = (UIGameObserver.crafting != null) ? UIGameObserver.crafting.CurrentSelected : null;
		UpgradeStation station = (!selected.IsNullOrReleased()) ? (selected as UpgradeStation) : null;
		// By reference: a destroyed station equals null for Unity, which would keep it and leave the window open.
		if (!ReferenceEquals(station, _station))
		{
			_station = station;
			if (_window != null)
			{
				_window.SetActive(false);
				_window = null;
			}
			if (station != null)
			{
				_hall = station.Hall;
				_window = WindowFor(_hall);
				_window.SetActive(true);
			}
			ClearQueueRows();
			_nextRefresh = 0f;
		}
		if (_station != null && Time.unscaledTime >= _nextRefresh)
		{
			Refresh();
		}
	}

	private Faction StationFaction => (_station != null) ? Faction.GetFaction(_station.gameObject) : null;

	private void Refresh()
	{
		_nextRefresh = Time.unscaledTime + RefreshSeconds;
		CountStock();
		Faction faction = StationFaction;
		List<RecipeInfo> queue = _station.QueuedRecipes;
		int queued = (queue != null) ? queue.Count : 0;
		foreach (LineRow row in _rows[_hall])
		{
			RefreshRow(row, faction, queue, queued);
		}
		RefreshQueue(queue, queued);
		if (_crystalTexts.TryGetValue(_hall, out Text crystals))
		{
			_stock.TryGetValue(ResearchCrystal.GetType(), out int have);
			crystals.text = (Economy.DarkCrystals.IsOn() ? "Dark crystals: " : "Blue crystals: ") + have;
		}
	}

	// The crystal the research station's studies are paid in.
	private static Ressource ResearchCrystal => Economy.DarkCrystals.IsOn() ? Economy.DarkCrystals.Resource : Adjectif.blueCrystal;

	private void RefreshRow(LineRow row, Faction faction, List<RecipeInfo> queue, int queued)
	{
		UpgradeLine line = row.Line;
		int tier = TeamUpgrades.Tier(faction, line.Index);
		int queuedTier = QueuedTier(queue, line);
		int next = tier + 1;
		row.Icon.sprite = IconFor(line, Mathf.Clamp(Mathf.Max(next, queuedTier), 1, line.Tiers));
		row.Title.text = "<b>" + line.Name + "</b>  <color=#999999>" + line.Unit + "</color>  " + TierText(line, tier);
		if (next > line.Tiers)
		{
			row.Effect.text = line.Effect[tier];
			row.Effect.color = Done;
			row.Cost.text = "Fully researched.";
			row.Research.interactable = false;
			row.ResearchLabel.text = "Done";
			return;
		}
		row.Effect.text = ((tier > 0) ? (line.Effect[tier] + "  ->  ") : "Next: ") + line.Effect[next];
		row.Effect.color = TextColor;
		row.Cost.text = "Tier " + next + " (" + UpgradeLines.TierName(line, next) + "): " + CostText(line.Cost[next]);
		if (queuedTier > 0)
		{
			row.Research.interactable = false;
			row.ResearchLabel.text = "Queued";
			return;
		}
		row.Research.interactable = queued < MaxQueue && line.Recipe[next] != null;
		row.ResearchLabel.text = "Research";
	}

	// The tier of the line waiting in the queue, 0 if none.
	private static int QueuedTier(List<RecipeInfo> queue, UpgradeLine line)
	{
		if (queue == null)
		{
			return 0;
		}
		foreach (RecipeInfo recipe in queue)
		{
			if (UpgradeLines.TryGetResearch(recipe, out UpgradeLine queuedLine, out int tier) && queuedLine == line)
			{
				return tier;
			}
		}
		return 0;
	}

	private static string TierText(UpgradeLine line, int tier)
	{
		if (tier == 0)
		{
			return "<color=#999999>not researched</color>";
		}
		return "<color=#ffcc40>Tier " + tier + " " + UpgradeLines.TierName(line, tier) + "</color>";
	}

	// "6 iron, 4 planks", each count red when the stockpiles hold less.
	private string CostText(Ingredient[] cost)
	{
		StringBuilder text = new StringBuilder();
		foreach (Ingredient ingredient in cost)
		{
			if (text.Length > 0)
			{
				text.Append(", ");
			}
			_stock.TryGetValue(ingredient.Resource.GetType(), out int have);
			bool enough = have >= ingredient.Count;
			text.Append(enough ? "<color=#e0e0e0>" : "<color=#e64d38>").Append(ingredient.Count).Append(' ').Append(ingredient.Name).Append("</color>");
			text.Append(" <color=#999999>(").Append(have).Append(")</color>");
		}
		return text.ToString();
	}

	// The queue as a list, one row per queued research; rebuilt only when the queue changes.
	private void RefreshQueue(List<RecipeInfo> queue, int count)
	{
		StringBuilder key = new StringBuilder();
		for (int i = 0; i < count; i++)
		{
			key.Append(queue[i].id).Append(',');
		}
		_queueTitles[_hall].text = "QUEUE (" + count + "/" + MaxQueue + ")";
		_queueEmpties[_hall].gameObject.SetActive(count == 0);
		if (key.ToString() == _queueShown)
		{
			return;
		}
		_queueShown = key.ToString();
		ClearQueueRows();
		_queueShown = key.ToString();
		for (int i = 0; i < count; i++)
		{
			QueueRow row = CreateQueueRow(_queueLists[_hall], i);
			string name = UpgradeLines.TryGetResearch(queue[i], out UpgradeLine line, out int tier) ? (line.Name + " tier " + tier + " (" + UpgradeLines.TierNames[tier] + ")") : "?";
			row.Text.text = name + ((i == 0) ? "  <color=#999999>now</color>" : string.Empty);
			_queueRows.Add(row);
		}
	}

	private void ClearQueueRows()
	{
		foreach (QueueRow row in _queueRows)
		{
			Destroy(row.Row);
		}
		_queueRows.Clear();
		_queueShown = null;
	}

	// What the local player's stockpiles hold of every material the building's researches use.
	private void CountStock()
	{
		_stock.Clear();
		List<Ressource> resources = new List<Ressource>(UpgradeLines.Storage(_hall).Keys);
		if (_hall == UpgradeHall.Research && !resources.Exists((Ressource resource) => resource.GetType() == ResearchCrystal.GetType()))
		{
			resources.Add(ResearchCrystal);
		}
		foreach (Ressource resource in resources)
		{
			_stock[resource.GetType()] = 0;
		}
		var stockpiles = BrixSingleton<AutoList>.Instance?.GetInstances(ObjetsDynamiques.Palette);
		if (stockpiles == null)
		{
			return;
		}
		foreach (GameObject go in stockpiles)
		{
			if (go == null || !go.activeInHierarchy || !Affiliation.BelongsToMe(go))
			{
				continue;
			}
			Recepteur recepteur = go.GetComponent<Recepteur>();
			if (recepteur == null)
			{
				continue;
			}
			foreach (Ressource resource in resources)
			{
				_stock[resource.GetType()] += recepteur.ContentDescription.Value(resource);
			}
		}
	}

	private static UNetProjectCmd Commands => (User.LocalUser != null) ? User.LocalUser.GetComponent<UNetProjectCmd>() : null;

	private void Research(UpgradeLine line)
	{
		if (_station == null || Commands == null)
		{
			return;
		}
		int next = TeamUpgrades.Tier(StationFaction, line.Index) + 1;
		if (next > line.Tiers || line.Recipe[next] == null || QueuedTier(_station.QueuedRecipes, line) > 0)
		{
			return;
		}
		Commands.CallCmdcraftingQueueRecipe(_station.gameObject, line.Recipe[next].id, 1);
		_nextRefresh = Time.unscaledTime + 0.1f;
	}

	private void RemoveQueued(int index)
	{
		if (_station != null && Commands != null)
		{
			Commands.CallCmdcraftingRemoveAt(_station.gameObject, index);
			_nextRefresh = Time.unscaledTime + 0.1f;
		}
	}

	// The window follows the selection, so closing it deselects the building.
	private void Close()
	{
		if (_station != null)
		{
			UIGameSelector.Unselect(_station.gameObject);
		}
	}

	// ---- window

	private Transform _canvas;

	private static string HallTitle(UpgradeHall hall)
	{
		switch (hall)
		{
		case UpgradeHall.Smithy:
			return "SMITHY";
		case UpgradeHall.Armoury:
			return "ARMOURY";
		default:
			return "RESEARCH STATION";
		}
	}

	private static string HallIntro(UpgradeHall hall)
	{
		switch (hall)
		{
		case UpgradeHall.Smithy:
			return "Weapon upgrades for the whole team. Each tier is researched once and works for every bricktron at once.";
		case UpgradeHall.Armoury:
			return "Armour upgrades for the whole team. Each tier is researched once and works for every soldier at once.";
		default:
			return "Studies that improve the whole colony, paid in " + (Economy.DarkCrystals.IsOn() ? "dark crystals" : "blue crystal") + ". Each tier is researched once and works at once.";
		}
	}

	private GameObject WindowFor(UpgradeHall hall)
	{
		if (_windows.TryGetValue(hall, out GameObject window) && window != null)
		{
			return window;
		}
		if (_canvas == null)
		{
			_canvas = CreateCanvas("UpgradeCanvas", transform, 900).transform;
		}
		window = CreateWindow(_canvas, Width);
		RectTransform windowRect = window.GetComponent<RectTransform>();
		windowRect.anchorMin = new Vector2(1f, 0.5f);
		windowRect.anchorMax = new Vector2(1f, 0.5f);
		windowRect.pivot = new Vector2(1f, 0.5f);
		windowRect.anchoredPosition = new Vector2(-64f, 0f);

		Transform header = CreateRow(window.transform, 30f);
		Text title = CreateText(header, HallTitle(hall), 18, Yellow, TextAnchor.MiddleLeft);
		title.fontStyle = FontStyle.Bold;
		Flexible(title.gameObject);
		if (hall == UpgradeHall.Research)
		{
			Text crystals = CreateText(header, string.Empty, 14, Yellow, TextAnchor.MiddleRight);
			_crystalTexts[hall] = crystals;
		}
		CreateButton(header, "X", 28f, Close);
		Text intro = AddNote(window.transform, HallIntro(hall));
		intro.color = Grey;

		List<LineRow> rows = new List<LineRow>();
		foreach (UpgradeLine line in UpgradeLines.In(hall))
		{
			rows.Add(CreateLineRow(window.transform, line));
		}
		_rows[hall] = rows;

		Transform queueHeader = CreateRow(window.transform, 24f);
		Text queueTitle = CreateText(queueHeader, "QUEUE", 12, Grey, TextAnchor.LowerLeft);
		Flexible(queueTitle.gameObject);
		_queueTitles[hall] = queueTitle;
		RectTransform queueList = CreateRect("Queue", window.transform);
		VerticalLayoutGroup queueLayout = queueList.gameObject.AddComponent<VerticalLayoutGroup>();
		queueLayout.spacing = 2f;
		queueLayout.childControlWidth = true;
		queueLayout.childControlHeight = true;
		queueLayout.childForceExpandWidth = true;
		queueLayout.childForceExpandHeight = false;
		_queueLists[hall] = queueList;
		_queueEmpties[hall] = AddNote(window.transform, "Nothing is being researched. Pick an upgrade above.");
		Text note = AddNote(window.transform, "A worker brings the materials here and works on the research, one at a time. Higher tiers need the tier before. Upgrades are kept in the save.");
		note.color = Grey;
		_windows[hall] = window;
		return window;
	}

	private LineRow CreateLineRow(Transform parent, UpgradeLine line)
	{
		Transform row = CreateRow(parent, 66f);
		Image background = row.gameObject.AddComponent<Image>();
		background.color = new Color(0.16f, 0.16f, 0.19f, 1f);
		HorizontalLayoutGroup layout = row.GetComponent<HorizontalLayoutGroup>();
		layout.padding = new RectOffset(6, 6, 4, 4);

		Image icon = CreateRect("Icon", row).gameObject.AddComponent<Image>();
		icon.preserveAspect = true;
		icon.raycastTarget = false;
		Fixed(icon.gameObject, 54f, 54f);

		RectTransform texts = CreateRect("Texts", row);
		VerticalLayoutGroup textLayout = texts.gameObject.AddComponent<VerticalLayoutGroup>();
		textLayout.spacing = 1f;
		textLayout.childControlWidth = true;
		textLayout.childControlHeight = true;
		textLayout.childForceExpandWidth = true;
		textLayout.childForceExpandHeight = false;
		Flexible(texts.gameObject);
		Text title = CreateText(texts, line.Name, 14, TextColor, TextAnchor.MiddleLeft);
		title.supportRichText = true;
		Text effect = CreateText(texts, string.Empty, 12, TextColor, TextAnchor.MiddleLeft);
		effect.horizontalOverflow = HorizontalWrapMode.Wrap;
		Text cost = CreateText(texts, string.Empty, 12, Grey, TextAnchor.MiddleLeft);
		cost.supportRichText = true;
		cost.horizontalOverflow = HorizontalWrapMode.Wrap;
		foreach (Text text in new[] { title, effect, cost })
		{
			LayoutElement element = text.gameObject.AddComponent<LayoutElement>();
			element.minHeight = 17f;
			element.preferredHeight = 17f;
		}

		Button research = CreateButton(row, "Research", 96f, () => Research(line));
		return new LineRow
		{
			Line = line,
			Icon = icon,
			Title = title,
			Effect = effect,
			Cost = cost,
			Research = research,
			ResearchLabel = research.GetComponentInChildren<Text>()
		};
	}

	private QueueRow CreateQueueRow(Transform list, int index)
	{
		Transform row = CreateRow(list, 26f);
		Text number = CreateText(row, (index + 1) + ".", 12, Grey, TextAnchor.MiddleRight);
		Fixed(number.gameObject, 20f, 26f);
		Text text = CreateText(row, string.Empty, 13, TextColor, TextAnchor.MiddleLeft);
		text.supportRichText = true;
		Flexible(text.gameObject);
		CreateButton(row, "X", 24f, () => RemoveQueued(index));
		return new QueueRow
		{
			Row = row.gameObject,
			Text = text
		};
	}

	private static Text AddNote(Transform parent, string text)
	{
		Text note = CreateText(parent, text, 12, Grey, TextAnchor.UpperLeft);
		note.horizontalOverflow = HorizontalWrapMode.Wrap;
		return note;
	}

	// The tier's icon from Assets/Upgrades (made by tools/make_upgrade_icons.py), or none if it is missing.
	private static Sprite IconFor(UpgradeLine line, int tier)
	{
		string name = line.Key + "_t" + tier;
		if (Icons.TryGetValue(name, out Sprite sprite))
		{
			return sprite;
		}
		string path = Path.Combine(Path.Combine(Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location), "Assets"), "Upgrades"), name + ".png");
		Texture2D texture = new Texture2D(2, 2, TextureFormat.ARGB32, mipmap: false);
		if (File.Exists(path) && texture.LoadImage(File.ReadAllBytes(path)))
		{
			texture.name = "Upgrade " + name;
			texture.filterMode = FilterMode.Bilinear;
			sprite = Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height), new Vector2(0.5f, 0.5f));
		}
		else
		{
			Plugin.Log.LogWarning("Upgrades: icon " + path + " not found");
			Destroy(texture);
			sprite = null;
		}
		Icons[name] = sprite;
		return sprite;
	}
}
