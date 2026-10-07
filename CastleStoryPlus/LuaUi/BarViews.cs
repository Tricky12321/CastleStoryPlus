using System;
using System.Collections.Generic;
using CastleStoryPlus.Core;
using CastleStoryPlus.UI;
using MoonSharp.Interpreter;
using UnityEngine;
using UnityEngine.UI;

namespace CastleStoryPlus.LuaUi;

// [LuaUi] CsBars: GameMenu's top bar (the selected task's name), left bar (score, blueprints, new task,
// structures, blocks, task count) and right bar (home, settings, help, back to work, call to arms...) drawn in C#.
// Their blocks in GameMenu (TopBarMenu.lua, and LeftBarMenu.lua twice) are cut out and each menu field gets a
// recorder instead, so GameMenu's build coroutine, and the mods' additions to it (call to arms settings, the giant
// bricktron...), still add their handles to it in order; the C# bars draw what was added. The handles, their
// groups and the hotkeys stay the game's.
[Feature(Features.CsMenus, Features.CsMenusInfo)]
internal static class CsBars
{
	private static readonly List<GameObject> Views = new List<GameObject>();

	private static void Enable()
	{
		if (!LuaUiConfig.CsBars)
		{
			return;
		}
		LuaMenuRecorder.Register();
		GameMenuLink.ReplaceMenuBlock(Features.CsMenus, "LUI/Menus/GameMenu.lua", "---lyt: top bar\ndo\n", "menu_top", "CastleStoryPlus.MenuRecorder(\"menu_top\")");
		GameMenuLink.ReplaceMenuBlock(Features.CsMenus, "LUI/Menus/GameMenu.lua", "---lyt: left bar\ndo\n", "menu_leftBar", "CastleStoryPlus.MenuRecorder(\"menu_leftBar\")");
		GameMenuLink.ReplaceMenuBlock(Features.CsMenus, "LUI/Menus/GameMenu.lua", "---menu: right bar\ndo\n", "menu_rightBar", "CastleStoryPlus.MenuRecorder(\"menu_rightBar\")");
		GameMenuLink.Attached += Show;
		GameMenuLink.Detached += Hide;
	}

	private static void Show()
	{
		Hide();
		LuaMenuRecorder top = LuaMenuRecorder.Find("menu_top");
		LuaMenuRecorder left = LuaMenuRecorder.Find("menu_leftBar");
		LuaMenuRecorder right = LuaMenuRecorder.Find("menu_rightBar");
		if (top == null || left == null || right == null)
		{
			Plugin.Log.LogWarning("CsMenus: the bars' menus were not replaced (top " + (top != null) + ", left " + (left != null) + ", right " + (right != null) + "); is another mod changing GameMenu.lua?");
			return;
		}
		Views.Add(CsTopBarView.Create(LuiCanvas.Instance.Menus, top).gameObject);
		Views.Add(CsSideBarView.Create(LuiCanvas.Instance.Menus, left, rightSide: false).gameObject);
		Views.Add(CsSideBarView.Create(LuiCanvas.Instance.Menus, right, rightSide: true).gameObject);
		Plugin.Log.LogInfo("CsMenus: the top, left and right bars are drawn in C#");
	}

	private static void Hide()
	{
		foreach (GameObject view in Views)
		{
			if (view != null)
			{
				UnityEngine.Object.Destroy(view);
			}
		}
		Views.Clear();
	}
}

// The left or right bar: a column of 48 pixel buttons over the screen's full height, as LeftBarMenu.lua lays it
// out. Drawn again when more was added to its menu (GameMenu adds them over several frames).
internal class CsSideBarView : MonoBehaviour
{
	private const float Width = 48f;

	private LuaMenuRecorder _recorder;

	private bool _rightSide;

	private int _built = -1;

	public static CsSideBarView Create(Transform parent, LuaMenuRecorder recorder, bool rightSide)
	{
		RectTransform rect = UiKit.CreateRect(rightSide ? "CS right bar" : "CS left bar", parent);
		float x = rightSide ? 1f : 0f;
		rect.anchorMin = new Vector2(x, 0f);
		rect.anchorMax = new Vector2(x, 1f);
		rect.pivot = new Vector2(x, 0.5f);
		rect.anchoredPosition = Vector2.zero;
		rect.sizeDelta = new Vector2(Width, 0f);
		VerticalLayoutGroup layout = rect.gameObject.AddComponent<VerticalLayoutGroup>();
		layout.spacing = 0f;
		layout.childControlWidth = true;
		layout.childControlHeight = true;
		layout.childForceExpandWidth = true;
		layout.childForceExpandHeight = false;
		CsSideBarView view = rect.gameObject.AddComponent<CsSideBarView>();
		view._recorder = recorder;
		view._rightSide = rightSide;
		view.Rebuild();
		return view;
	}

	private void Update()
	{
		if (_recorder != null && _recorder.Version != _built)
		{
			Rebuild();
		}
	}

	private void Rebuild()
	{
		_built = _recorder.Version;
		for (int i = transform.childCount - 1; i >= 0; i--)
		{
			Destroy(transform.GetChild(i).gameObject);
		}
		LuiIconButton.Side side = _rightSide ? LuiIconButton.Side.Left : LuiIconButton.Side.Right;
		foreach (LuaMenuRecorder.Call call in _recorder.Calls)
		{
			Table handle = LuaMenuRecorder.TableArg(call, 0);
			if (handle == null)
			{
				continue;
			}
			switch (call.Method)
			{
			case "AddMenuHandleToggle":
				LuaHandleView.Create(transform, handle, Width, side);
				break;
			case "AddMenuHandleSpacer":
				LuaSpacerView.Create(transform, handle, Width, horizontal: false);
				break;
			case "AddDisplayHandle":
				LuaTextView.Create(transform, handle, "Label", 12, Width, Width).gameObject.AddComponent<Image>().color = LuiStyle.BgDefault;
				break;
			}
		}
	}
}

// The top bar: the selected task's name in a dark strip at the top centre, while a task other than the idle
// group is selected (TopBarMenu.lua with GameMenu's topSpacer and topLabel).
internal class CsTopBarView : MonoBehaviour
{
	private const float PollSeconds = 0.25f;

	private LuaMenuRecorder _recorder;

	private RectTransform _strip;

	private int _built = -1;

	private float _readAt;

	public static CsTopBarView Create(Transform parent, LuaMenuRecorder recorder)
	{
		RectTransform rect = UiKit.CreateRect("CS top bar", parent);
		UiKit.SetRect(rect, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
		CsTopBarView view = rect.gameObject.AddComponent<CsTopBarView>();
		view._recorder = recorder;
		RectTransform strip = UiKit.CreateRect("Strip", rect);
		strip.anchorMin = strip.anchorMax = new Vector2(0.5f, 1f);
		strip.pivot = new Vector2(0.5f, 1f);
		strip.anchoredPosition = Vector2.zero;
		strip.sizeDelta = new Vector2(320f, 64f);
		Image background = strip.gameObject.AddComponent<Image>();
		LuiStyle.Paint(background, LuiStyle.FrameSprite, LuiStyle.BgPanel);
		view._strip = strip;
		LuiCanvas.Instance.Tick += view.Tick;
		return view;
	}

	private void Tick()
	{
		if (_recorder == null)
		{
			return;
		}
		if (_recorder.Version != _built)
		{
			Rebuild();
		}
		if (Time.unscaledTime - _readAt >= PollSeconds)
		{
			_readAt = Time.unscaledTime;
			bool shown = LuaUiGame.SelectedTaskNotIdle();
			if (_strip.gameObject.activeSelf != shown)
			{
				_strip.gameObject.SetActive(shown);
			}
		}
	}

	private void Rebuild()
	{
		_built = _recorder.Version;
		for (int i = _strip.childCount - 1; i >= 0; i--)
		{
			Destroy(_strip.GetChild(i).gameObject);
		}
		foreach (LuaMenuRecorder.Call call in _recorder.Calls)
		{
			Table handle = LuaMenuRecorder.TableArg(call, 0);
			if (handle != null && call.Method == "AddMenuHandleLabel")
			{
				LuaTextView label = LuaTextView.Create(_strip, handle, "LabelText", LuiStyle.TitleFontSize, 320f, 64f);
				UiKit.SetRect((RectTransform)label.transform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
			}
		}
	}

	private void OnDestroy()
	{
		if (LuiCanvas.Exists)
		{
			LuiCanvas.Instance.Tick -= Tick;
		}
	}
}
