using System.Collections.Generic;
using CastleStoryPlus.Core;
using CastleStoryPlus.UI;
using MoonSharp.Interpreter;
using UnityEngine;
using UnityEngine.UI;

namespace CastleStoryPlus.LuaUi;

// [LuaUi] CsBuildMenus: the new-task sidebar and the structure and block catalogues (GameMenu's newTask,
// buildStructures and buildStoneBlocks handles) drawn in C# beside the left bar. GameMenu loads each into a
// SidebarFloatingMenu with one button panel per entry (the catalogues have one per building and block) while the
// map loads; their sockets are taken out of the left content overlay, so those menus are never made. The
// blueprint library in the same overlay stays the game's (CsBlueprintLibrary).
[Feature(Features.CsMenus, Features.CsMenusInfo)]
internal static class CsBuildMenus
{
	private static readonly string[] Handles = new string[] { "newTask", "buildStructures", "buildStoneBlocks" };

	private static readonly List<GameObject> Views = new List<GameObject>();

	private static void Enable()
	{
		if (!LuaUiConfig.CsBuildMenus)
		{
			return;
		}
		LuaMenuRecorder.Register();
		foreach (string handle in Handles)
		{
			// The call goes, the chain around it (_m.menu_leftContent .AddMenuHandleSocket(...) ...) stays whole.
			LuaInjection.AddPatch(Features.CsMenus, "LUI/Menus/GameMenu.lua", ".AddMenuHandleSocket(_m.mh." + handle + ")", LuaInjection.Mode.Replace, string.Empty);
		}
		GameMenuLink.Attached += Show;
		GameMenuLink.Detached += Hide;
	}

	private static void Show()
	{
		Hide();
		foreach (string name in Handles)
		{
			Table handle = GameMenuLink.Handle("mh", name);
			if (handle == null)
			{
				Plugin.Log.LogWarning("CsMenus: no " + name + " handle in GameMenu");
				continue;
			}
			Views.Add(LuaSidebarView.Create(LuiCanvas.Instance.Menus, handle, rightSide: false).gameObject);
		}
		Plugin.Log.LogInfo("CsMenus: the new-task sidebar and the building and block catalogues are drawn in C#");
	}

	private static void Hide()
	{
		foreach (GameObject view in Views)
		{
			if (view != null)
			{
				Object.Destroy(view);
			}
		}
		Views.Clear();
	}
}

// [LuaUi] CsSettings: the in-game settings sidebar (pause, quicksave, screenshot, mute, x-ray..., and the mod's
// settings button) drawn in C# beside the right bar, its socket taken out of the right content overlay; and the
// "Paused" notice at the top of the screen (IsPausedMenu.lua), whose block in GameMenu is cut out.
[Feature(Features.CsMenus, Features.CsMenusInfo)]
internal static class CsSettings
{
	private static readonly List<GameObject> Views = new List<GameObject>();

	private static void Enable()
	{
		if (!LuaUiConfig.CsSettings)
		{
			return;
		}
		LuaInjection.AddPatch(Features.CsMenus, "LUI/Menus/GameMenu.lua", ".AddMenuHandleSocket(_m.mh.settings)", LuaInjection.Mode.Replace, string.Empty);
		GameMenuLink.ReplaceMenuBlock(Features.CsMenus, "---is paused\ndo\n", "menu_isPaused");
		GameMenuLink.Attached += Show;
		GameMenuLink.Detached += Hide;
	}

	private static void Show()
	{
		Hide();
		Table settings = GameMenuLink.Handle("mh", "settings");
		if (settings != null)
		{
			Views.Add(LuaSidebarView.Create(LuiCanvas.Instance.Menus, settings, rightSide: true).gameObject);
		}
		else
		{
			Plugin.Log.LogWarning("CsMenus: no settings handle in GameMenu");
		}
		Views.Add(CsPausedView.Create(LuiCanvas.Instance.Menus).gameObject);
		Plugin.Log.LogInfo("CsMenus: the settings sidebar and the paused notice are drawn in C#");
	}

	private static void Hide()
	{
		foreach (GameObject view in Views)
		{
			if (view != null)
			{
				Object.Destroy(view);
			}
		}
		Views.Clear();
	}
}

// "Paused" and its hint in a dark box at the top centre while the game is paused, as IsPausedMenu.lua draws it.
// Not a raycast target: the world under it can still be clicked, as with the game's.
internal class CsPausedView : MonoBehaviour
{
	private GameObject _box;

	public static CsPausedView Create(Transform parent)
	{
		RectTransform rect = UiKit.CreateRect("CS paused", parent);
		UiKit.SetRect(rect, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
		CsPausedView view = rect.gameObject.AddComponent<CsPausedView>();
		RectTransform box = UiKit.CreateRect("Box", rect);
		box.anchorMin = box.anchorMax = new Vector2(0.5f, 1f);
		box.pivot = new Vector2(0.5f, 0.5f);
		box.anchoredPosition = new Vector2(0f, -100f);
		box.sizeDelta = new Vector2(256f, 60f);
		Image background = box.gameObject.AddComponent<Image>();
		background.color = LuiStyle.BgPanel;
		background.raycastTarget = false;
		VerticalLayoutGroup layout = box.gameObject.AddComponent<VerticalLayoutGroup>();
		layout.childAlignment = TextAnchor.MiddleCenter;
		layout.childControlWidth = true;
		layout.childControlHeight = true;
		layout.childForceExpandWidth = true;
		layout.childForceExpandHeight = false;
		Text title = LuiWidgets.Label(box, LuaBridge.Localized("##gamemenu_paused"), 24, LuiStyle.CastleYellow, TextAnchor.MiddleCenter, LuiStyle.Title);
		title.gameObject.AddComponent<LayoutElement>().preferredHeight = 28f;
		Text hint = LuiWidgets.Label(box, LuaBridge.Localized("##gamemenu_paused_text"), 16, LuiStyle.CastleYellow, TextAnchor.MiddleCenter, LuiStyle.Title);
		hint.gameObject.AddComponent<LayoutElement>().preferredHeight = 18f;
		view._box = box.gameObject;
		view._box.SetActive(ClockConfig.Paused);
		return view;
	}

	private void Update()
	{
		bool paused = ClockConfig.Paused;
		if (_box.activeSelf != paused)
		{
			_box.SetActive(paused);
		}
	}
}
