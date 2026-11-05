using System.Collections.Generic;
using System.Reflection;
using Brix.UI.Builder.Menu;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace CastleStoryPlus.UI;

// A "Castle Story Plus" tab in the game's own Options > Settings (title screen and in game), after Gameplay: its page
// holds every setting of the mod (AllSettingsPanel, built into the page). The tab is a copy of the Gameplay tab (look,
// sounds), opening a copy of the Gameplay page whose option list is replaced by the mod's settings; the page is one of
// the settings pages (same group), so opening it closes the others and theirs close it. A value is saved to the mod's
// config at once, so the options' Apply and Revert do not concern it.
[Feature]
[HarmonyPatch(typeof(Container), "OnEnable")]
internal static class ModOptionsTab
{
	internal const LegacyBaseMenu.Operation Operation = (LegacyBaseMenu.Operation)7303;

	private const string Title = "Castle Story Plus";

	// The description's width under the lists (the game's own option lists are about 770 wide).
	private const float PanelWidth = 760f;

	private static readonly HashSet<Container> Logged = new HashSet<Container>();

	// Before the tab container builds its tabs (each time it is shown): the tab and its page are added.
	private static void Prefix(Container __instance)
	{
		if (__instance == null || __instance.GetItem(Operation) != null)
		{
			return;
		}
		Container.Item gameplay = __instance.GetItem(LegacyBaseMenu.Operation.OptionsGameplay);
		Containee page = (gameplay != null) ? OpenTarget(gameplay) : null;
		if (page == null || page.container == null)
		{
			return;
		}
		try
		{
			Containee ours = Page(page);
			if (ours == null)
			{
				return;
			}
			Container.Item tab = Copy(gameplay);
			tab.containerPopulatorOperation = Operation;
			tab.titleText = "&&" + Title;
			tab.localizeTitle = tab.titleText;
			tab.subtitleText = "&&Every setting of the mod";
			tab.localizeSubtitle = tab.subtitleText;
			tab.instance = null;
			tab.isDefaultInGroup = false;
			tab.actionItems = new List<Container.Item.ActionItem>
			{
				new Container.Item.ActionItem { action = Container.Item.ActionItem.Action.Open, target = ours }
			};
			__instance.items.Insert(__instance.items.IndexOf(gameplay) + 1, tab);
			IncludeOperation();
			if (Logged.Add(__instance))
			{
				Plugin.Log.LogInfo("ModOptionsTab: \"" + Title + "\" added to " + __instance.name);
			}
		}
		catch (System.Exception ex)
		{
			Plugin.Log.LogError("ModOptionsTab: could not add the tab: " + ex);
		}
	}

	// The page a tab opens.
	private static Containee OpenTarget(Container.Item tab)
	{
		if (tab.actionItems == null)
		{
			return null;
		}
		foreach (Container.Item.ActionItem action in tab.actionItems)
		{
			if (action != null && action.action == Container.Item.ActionItem.Action.Open && action.target != null)
			{
				return action.target;
			}
		}
		return null;
	}

	// The mod's page beside the Gameplay page, made once per pages container.
	private static Containee Page(Containee gameplay)
	{
		Container pages = gameplay.container;
		Container.Item existing = pages.GetItem(Operation);
		if (existing != null && existing.instance != null)
		{
			return existing.instance;
		}
		// Copied while the Gameplay page is off, so nothing of the copy (its option menus) starts before it is cleared.
		bool wasActive = gameplay.gameObject.activeSelf;
		gameplay.gameObject.SetActive(false);
		GameObject go;
		try
		{
			go = Object.Instantiate(gameplay.gameObject, gameplay.transform.parent, false);
		}
		finally
		{
			gameplay.gameObject.SetActive(wasActive);
		}
		go.name = "Middle: Options: Settings: Castle Story Plus";
		go.hideFlags = HideFlags.DontSave;
		go.transform.SetSiblingIndex(gameplay.transform.GetSiblingIndex() + 1);
		Transform area = go.transform.Find("Scrollable/Content Area");
		if (area == null)
		{
			Object.DestroyImmediate(go);
			return null;
		}
		for (int i = area.childCount - 1; i >= 0; i--)
		{
			Object.DestroyImmediate(area.GetChild(i).gameObject);
		}
		SetText(go.transform.Find("Title"), Title.ToUpper());
		Transform bread = go.transform.Find("Bread");
		Text breadText = (bread != null) ? bread.GetComponent<Text>() : null;
		if (breadText != null)
		{
			string path = breadText.text;
			int last = path.LastIndexOf('➭');
			SetText(bread, ((last >= 0) ? path.Substring(0, last + 1) + "  " : string.Empty) + Title.ToUpper());
		}
		Containee containee = go.GetComponent<Containee>();
		containee.container = pages;
		// The page's own container listed the Gameplay page's parts; it has none of its own now.
		Container own = go.GetComponent<Container>();
		if (own != null)
		{
			own.items.Clear();
		}
		AllSettingsPanel.Embed((RectTransform)area, PanelWidth);
		Container.Item gameplayItem = null;
		foreach (Container.Item item in pages.items)
		{
			if (item.instance == gameplay)
			{
				gameplayItem = item;
			}
		}
		Container.Item pageItem = (gameplayItem != null) ? Copy(gameplayItem) : new Container.Item();
		pageItem.containerPopulatorOperation = Operation;
		pageItem.instance = containee;
		pageItem.isDefaultInGroup = false;
		pageItem.actionItems = new List<Container.Item.ActionItem>();
		pages.items.Add(pageItem);
		return containee;
	}

	// The text, kept: the copied localizer would put the Gameplay page's back.
	private static void SetText(Transform t, string text)
	{
		if (t == null)
		{
			return;
		}
		foreach (MonoBehaviour behaviour in t.GetComponents<MonoBehaviour>())
		{
			if (behaviour != null && behaviour.GetType().Name.Contains("Localizer"))
			{
				Object.DestroyImmediate(behaviour);
			}
		}
		Text label = t.GetComponent<Text>();
		if (label != null)
		{
			label.text = text;
		}
	}

	private static Container.Item Copy(Container.Item source)
	{
		Container.Item item = new Container.Item();
		foreach (FieldInfo field in typeof(Container.Item).GetFields(BindingFlags.Public | BindingFlags.Instance))
		{
			field.SetValue(item, field.GetValue(source));
		}
		return item;
	}

	// The tabs highlight the selected operation, and only the game's own settings operations can be selected.
	private static void IncludeOperation()
	{
		if (!LegacyOptionCategoriesMenu.IncludedOperations.Contains(Operation))
		{
			LegacyOptionCategoriesMenu.IncludedOperations.Add(Operation);
		}
	}
}
