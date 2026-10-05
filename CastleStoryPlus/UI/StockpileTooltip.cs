using System.Collections.Generic;
using Brix.External.Factories;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Game.Semantique;
using Brix.Utils;
using CastleStoryPlus.Building;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using static CastleStoryPlus.UI.UiKit;

namespace CastleStoryPlus.UI;

// Hovering a stockpile (or a tool rack or any other storage with the game's icon-and-count display) shows a small
// panel next to the mouse that lists what it holds, one line per resource: icon, name and count, with the capacity
// when it holds a single resource. The game's own icons and counts above it stay. Runs on every peer from the synced
// stockpile content. A warehouse also shows how full it is, in percent of its room.
[Feature(Features.StockpileTooltip, Features.StockpileTooltipInfo)]
internal class StockpileTooltip : MonoBehaviour
{
	private const float RefreshSeconds = 0.25f;

	private const int MaxLines = 12;

	private static readonly Vector2 MouseOffset = new Vector2(22f, -22f);

	internal static Recepteur Hovered;

	private GameObject _canvas;

	private RectTransform _panel;

	private Text _title;

	private readonly List<GameObject> _rows = new List<GameObject>();

	private readonly List<Image> _icons = new List<Image>();

	private readonly List<Text> _names = new List<Text>();

	private readonly List<Text> _counts = new List<Text>();

	private float _nextRefresh;

	private static void Enable()
	{
		Plugin.Root.AddComponent<StockpileTooltip>();
	}

	private void Update()
	{
		bool show = Hovered != null && !Hovered.IsNullOrReleased() && Hovered.gameObject.activeInHierarchy;
		if (!show)
		{
			Hovered = null;
			if (_canvas != null && _canvas.activeSelf)
			{
				_canvas.SetActive(false);
			}
			return;
		}
		if (_canvas == null)
		{
			Build();
		}
		if (!_canvas.activeSelf)
		{
			_canvas.SetActive(true);
			_nextRefresh = 0f;
		}
		if (Time.unscaledTime >= _nextRefresh)
		{
			_nextRefresh = Time.unscaledTime + RefreshSeconds;
			Fill(Hovered);
		}
		_panel.position = (Vector2)Input.mousePosition + MouseOffset * _canvas.transform.localScale.x;
	}

	private void Build()
	{
		_canvas = CreateCanvas("StockpileTooltipCanvas", transform, 940);
		// Only shows: never catches the mouse.
		Destroy(_canvas.GetComponent<GraphicRaycaster>());
		GameObject window = CreateWindow(_canvas.transform, 0f);
		_panel = window.GetComponent<RectTransform>();
		_panel.pivot = new Vector2(0f, 1f);
		_panel.anchorMin = Vector2.zero;
		_panel.anchorMax = Vector2.zero;
		window.GetComponent<Image>().raycastTarget = false;
		VerticalLayoutGroup layout = window.GetComponent<VerticalLayoutGroup>();
		layout.padding = new RectOffset(10, 12, 6, 8);
		layout.spacing = 2f;
		ContentSizeFitter fitter = window.GetComponent<ContentSizeFitter>();
		fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
		_title = CreateText(CreateRow(window.transform, 20f), string.Empty, 14, Yellow, TextAnchor.MiddleLeft);
		for (int i = 0; i < MaxLines; i++)
		{
			Transform row = CreateRow(window.transform, 22f);
			Image icon = CreateRect("Icon", row).gameObject.AddComponent<Image>();
			icon.preserveAspect = true;
			icon.raycastTarget = false;
			Fixed(icon.gameObject, 20f, 20f);
			Text name = CreateText(row, string.Empty, 13, TextColor, TextAnchor.MiddleLeft);
			Fixed(name.gameObject, 120f, 20f);
			Text count = CreateText(row, string.Empty, 13, TextColor, TextAnchor.MiddleRight);
			Fixed(count.gameObject, 70f, 20f);
			_rows.Add(row.gameObject);
			_icons.Add(icon);
			_names.Add(name);
			_counts.Add(count);
		}
		_canvas.SetActive(false);
	}

	private void Fill(Recepteur recepteur)
	{
		_title.text = Title(recepteur) + FullText(recepteur);
		List<KeyValuePair<Ressource, int>> held = new List<KeyValuePair<Ressource, int>>();
		foreach (KeyValuePair<System.Type, Adjectif> pair in recepteur.ContentDescription.DicoAdjectif)
		{
			Ressource resource = pair.Value as Ressource;
			if (resource == null || resource.quantifiable == null || resource.quantifiable.valeur <= 0 || pair.Key == typeof(Ressource) || pair.Key == Adjectif.outil.GetType())
			{
				continue;
			}
			held.Add(new KeyValuePair<Ressource, int>(resource, resource.quantifiable.valeur));
		}
		held.Sort((KeyValuePair<Ressource, int> a, KeyValuePair<Ressource, int> b) => b.Value.CompareTo(a.Value));
		for (int i = 0; i < _rows.Count; i++)
		{
			bool used = i < held.Count || (i == 0 && held.Count == 0);
			_rows[i].SetActive(used);
			if (!used)
			{
				continue;
			}
			if (held.Count == 0)
			{
				_icons[i].enabled = false;
				_names[i].text = "Empty";
				_names[i].color = Grey;
				_counts[i].text = string.Empty;
				continue;
			}
			Ressource resource = held[i].Key;
			AdjectiveInfo info = resource.GetKey().GetInfo();
			Sprite sprite = (info != null) ? info.icon : null;
			_icons[i].sprite = sprite;
			_icons[i].enabled = sprite != null;
			_names[i].text = NameOf(resource, info);
			_names[i].color = TextColor;
			int capacity = recepteur.BaseCapacity.Value(resource);
			_counts[i].text = (held.Count == 1 && capacity > 0) ? (held[i].Value + " / " + capacity) : held[i].Value.ToString();
		}
	}

	private static string Title(Recepteur recepteur)
	{
		Factory.AssetKey key = recepteur.AssetKey;
		if (LargeStockpile.IsStockpileKey(key))
		{
			return LargeStockpile.IsLarge(recepteur) ? "Large stockpile" : "Stockpile";
		}
		if (Warehouse.IsWarehouse(recepteur))
		{
			return "Warehouse";
		}
		if (key == ObjetsDynamiques.Toolrack || key == ObjetsDynamiques.SingleToolrack)
		{
			return "Weapon stand";
		}
		return recepteur.gameObject.name.Replace("(Clone)", string.Empty).Trim();
	}

	// A warehouse holds any mix of resources, so how full it is shows as a share of its room ("Warehouse 82% full").
	private static string FullText(Recepteur recepteur)
	{
		if (!Warehouse.IsWarehouse(recepteur))
		{
			return string.Empty;
		}
		int capacity = recepteur.BaseCapacity.Value(Adjectif.encombrement);
		if (capacity <= 0)
		{
			return string.Empty;
		}
		int used = recepteur.ContentDescription.Value(Adjectif.encombrement);
		int percent = Mathf.Clamp(Mathf.RoundToInt(100f * used / capacity), 0, 100);
		return "  " + percent + "% full";
	}

	private static string NameOf(Ressource resource, AdjectiveInfo info)
	{
		string name = (info != null) ? info.descriptiveName : null;
		if (string.IsNullOrEmpty(name))
		{
			return resource.GetType().Name;
		}
		return name.StartsWith("##") ? I2Helper.TryGet(name) : name;
	}
}

// The storage under the mouse, from the game's hover of its icon-and-count display.
[Feature(Features.StockpileTooltip, Features.StockpileTooltipInfo)]
[HarmonyPatch(typeof(OverheadDisplayDriver), "OnHover")]
internal static class StockpileTooltipHoverPatch
{
	private static void Postfix(OverheadDisplayDriver __instance)
	{
		if (__instance is RecepteurOverheadDisplayDriver && __instance.recepteur != null)
		{
			StockpileTooltip.Hovered = __instance.recepteur;
		}
	}
}

[Feature(Features.StockpileTooltip, Features.StockpileTooltipInfo)]
[HarmonyPatch(typeof(OverheadDisplayDriver), "OnUnhover")]
internal static class StockpileTooltipUnhoverPatch
{
	private static void Postfix(OverheadDisplayDriver __instance)
	{
		if (__instance is RecepteurOverheadDisplayDriver && StockpileTooltip.Hovered == __instance.recepteur)
		{
			StockpileTooltip.Hovered = null;
		}
	}
}
