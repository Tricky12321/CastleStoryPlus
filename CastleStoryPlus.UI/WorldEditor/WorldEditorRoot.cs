using System;
using System.Collections.Generic;
using Brix.Input;
using Brix.Input.WorldEditor;
using Brix.Steam;
using Brix.UI.Builder.Menu;
using Brix.UI.Icons;
using Brix.Utils.UI;
using CastleStoryPlus.GameUi;
using CastleStoryPlus.LuaUi;
using CastleStoryPlus.UI;
using UnityEngine;
using UnityEngine.UI;

namespace CastleStoryPlus.WorldEditor;

// The world editor's menu (WorldEditorMenu.lua): a column of tabs on the left (terrain, markers, time, map, grid,
// modes, save, workshop), each opening its panel beside it, and a home button in the top right corner. Its keys are
// the game's "we_menu" bindings (WorldEditorMenu category), as the Lua menu registered them while open.
internal class WorldEditorRoot : MonoBehaviour
{
	internal const float TabWidth = 64f;

	internal const float PanelWidth = 320f;

	private const string KeyCategory = "we_menu";

	// The tabs' background (WorldEditorMenu.lua: black, 0.9).
	internal static readonly Color Background = new Color(0f, 0f, 0f, 0.9f);

	private static WorldEditorRoot _root;

	private readonly UiGroup _group = new UiGroup();

	private readonly List<string> _actions = new List<string>();

	internal static bool IsOpen
	{
		get { return _root != null; }
	}

	internal static void Open()
	{
		Close();
		try
		{
			GameObject go = new GameObject("CS world editor menu", typeof(RectTransform));
			go.transform.SetParent(LuiCanvas.Instance.Menus, false);
			RectTransform rect = (RectTransform)go.transform;
			UiKit.SetRect(rect, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
			_root = go.AddComponent<WorldEditorRoot>();
			_root.Build();
			Plugin.Log.LogInfo("CsWorldEditor: the world editor's menu is drawn");
		}
		catch (Exception ex)
		{
			Plugin.Log.LogError("CsWorldEditor: could not build the world editor's menu: " + ex);
		}
	}

	internal static void Close()
	{
		if (_root == null)
		{
			return;
		}
		try
		{
			_root.Teardown();
		}
		catch (Exception ex)
		{
			Plugin.Log.LogError("CsWorldEditor: closing: " + ex);
		}
		UnityEngine.Object.DestroyImmediate(_root.gameObject);
		_root = null;
	}

	private void Build()
	{
		RectTransform column = UiKit.CreateRect("CS world editor tabs", transform);
		column.anchorMin = new Vector2(0f, 0f);
		column.anchorMax = new Vector2(0f, 1f);
		column.pivot = new Vector2(0f, 0.5f);
		column.anchoredPosition = Vector2.zero;
		column.sizeDelta = new Vector2(TabWidth, 0f);
		column.gameObject.AddComponent<Image>().color = Background;
		VerticalLayoutGroup layout = column.gameObject.AddComponent<VerticalLayoutGroup>();
		layout.childAlignment = TextAnchor.UpperCenter;
		layout.childControlWidth = true;
		layout.childControlHeight = true;
		layout.childForceExpandWidth = false;
		layout.childForceExpandHeight = false;
		Tab(column, "terrain", "##mode_worldeditor_terrain", IconKeys.UI_EditWorld, "we_terrain_menu_toggle", WorldEditorPanels.Terrain());
		Tab(column, "markers", "##mode_worldeditor_markers", IconKeys.UI_Flag, "we_markers_menu_toggle", WorldEditorPanels.Markers());
		Divider(column);
		Tab(column, "time", "##mode_worldeditor_time", IconKeys.UI_Moon, "we_time_menu_toggle", WorldEditorPanels.Time());
		Tab(column, "map", "##mode_worldeditor_map", IconKeys.UI_Map, "we_map_menu_toggle", WorldEditorPanels.Map());
		Tab(column, "grid", "##mode_worldeditor_grid", IconKeys.Grid, "we_grid_menu_toggle", WorldEditorPanels.Grid());
		Spacer(column);
		Tab(column, "modes", "##mode_worldeditor_modes", IconKeys.UI_Sword, "we_modes_menu_toggle", WorldEditorPanels.Modes());
		Tab(column, "save", "##mode_worldeditor_save", IconKeys.UI_Save, "we_save_menu_toggle", WorldEditorPanels.Save());
		UiButton workshop = Tab(column, "workshop", "##mode_worldeditor_workshop", IconKeys.UI_Workshop, "we_workshop_menu_toggle", WorldEditorPanels.Workshop());
		workshop.Enabled = () => SteamWorkshopAgent.IsConnected;
		HomeButton();
		Hotkey("we_grid_toggle", () =>
		{
			WorldGridController grid = WorldEditorGame.Grid;
			if (grid != null)
			{
				grid.ActiveByUser = !grid.ActiveByUser;
			}
		});
		GameHotkeys.SetCategory(KeyBindingsUtility.Category.General, false);
		GameHotkeys.SetCategory(KeyBindingsUtility.Category.WorldEditorMenu, true);
	}

	// A tab: its button in the column (lit while its panel is open) and its panel beside the column.
	private UiButton Tab(RectTransform column, string id, string title, IconKey icon, string action, UiPanel panel)
	{
		panel.Id = "we_" + id;
		panel.Title = () => GameUiState.Text(title);
		panel.Group = _group;
		Action onClose = panel.OnClose;
		panel.OnClose = () =>
		{
			WorldEditorGame.DropTools();
			onClose?.Invoke();
		};
		UiButton button = new UiButton
		{
			Id = "we_tab_" + id,
			Label = () => GameUiState.Text(title),
			Icon = () => GameUiState.Icon(icon),
			Hotkey = () => GameUiState.KeyOf(KeyCategory, action),
			Highlight = () => panel.IsOpen,
			Selected = () => panel.IsOpen
		};
		button.OnClick = (bool shift, bool ctrl) => Select(panel, button);
		CsItemFactory.Create(column, button, TabWidth, LuiIconButton.Side.Right, catalogue: false);
		CsPanelView.Create(transform, panel, rightSide: false, TabWidth, PanelWidth, 0f);
		Hotkey(action, () => Select(panel, button));
		return button;
	}

	// WorldEditorMenu's tab action: the terrain brush and marker tool put away, then the tab's panel opened, or
	// closed when it was the open one.
	private void Select(UiPanel panel, UiButton button)
	{
		if (button.Enabled != null && !button.Enabled())
		{
			return;
		}
		GameUiState.Sound("WorldEditor_UI_Menu_Select");
		WorldEditorGame.DropTools();
		panel.Toggle();
		CsItemViews.MarkAll();
	}

	private void Hotkey(string action, Action run)
	{
		GameHotkeys.Register(action, run);
		_actions.Add(action);
	}

	private static void Divider(RectTransform column)
	{
		Image divider = LuiWidgets.Divider(column, vertical: false);
		LayoutElement element = divider.GetComponent<LayoutElement>() ?? divider.gameObject.AddComponent<LayoutElement>();
		element.minWidth = TabWidth;
	}

	private static void Spacer(RectTransform column)
	{
		RectTransform spacer = UiKit.CreateRect("Spacer", column);
		LayoutElement element = spacer.gameObject.AddComponent<LayoutElement>();
		element.flexibleHeight = 1f;
		element.minWidth = TabWidth;
	}

	// The home button in the top right corner: the game's main menu (pause menu).
	private void HomeButton()
	{
		RectTransform corner = UiKit.CreateRect("CS world editor home", transform);
		corner.anchorMin = corner.anchorMax = Vector2.one;
		corner.pivot = Vector2.one;
		corner.anchoredPosition = Vector2.zero;
		corner.sizeDelta = new Vector2(TabWidth, TabWidth);
		corner.gameObject.AddComponent<Image>().color = Background;
		UiButton home = new UiButton
		{
			Id = "we_home",
			Label = () => GameUiState.Text("##mainmenu_home"),
			Icon = () => GameUiState.Icon(IconKeys.UI_Home),
			OnClick = (bool shift, bool ctrl) => InputModeController.ToggleMainMenu()
		};
		CsItemView view = CsItemFactory.Create(corner, home, TabWidth, LuiIconButton.Side.Left, catalogue: false);
		UiKit.SetRect((RectTransform)view.transform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
	}

	private void Teardown()
	{
		_group.CloseAll();
		foreach (string action in _actions)
		{
			GameHotkeys.Unregister(action);
		}
		_actions.Clear();
		GameHotkeys.ResetCategories();
		WorldEditorPanels.Forget();
	}

	private void Update()
	{
		CsItemViews.Tick();
	}
}
