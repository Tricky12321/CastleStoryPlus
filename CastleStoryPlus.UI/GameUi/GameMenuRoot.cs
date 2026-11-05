using System;
using CastleStoryPlus.Core;
using CastleStoryPlus.LuaUi;
using CastleStoryPlus.UI;
using UnityEngine;

namespace CastleStoryPlus.GameUi;

// The way in from the mod (UiAssembly): opening and closing the in-game menu, its model and its views' refresh. Each
// load of this assembly (the first, and every UI reload) sets these on UiAssembly.
internal static class UiEntry
{
	private static GameMenuRoot _root;

	public static void Load()
	{
		UiTheme.Install();
		UiAssembly.Open = Open;
		UiAssembly.Close = Close;
		UiAssembly.Model = () => (_root != null) ? _root.Model : null;
		UiAssembly.MarkAll = CsItemViews.MarkAll;
		UiAssembly.OpenWorldEditor = CastleStoryPlus.WorldEditor.WorldEditorRoot.Open;
		UiAssembly.CloseWorldEditor = CastleStoryPlus.WorldEditor.WorldEditorRoot.Close;
		UiAssembly.WorldEditorOpen = () => CastleStoryPlus.WorldEditor.WorldEditorRoot.IsOpen;
	}

	// reload: the menu is built again in a game already going (no help at the start, as at the game's start).
	private static void Open(bool reload)
	{
		Close();
		try
		{
			GameObject go = new GameObject("CS game menu", typeof(RectTransform));
			go.transform.SetParent(LuiCanvas.Instance.Menus, false);
			RectTransform rect = (RectTransform)go.transform;
			rect.anchorMin = Vector2.zero;
			rect.anchorMax = Vector2.one;
			rect.offsetMin = Vector2.zero;
			rect.offsetMax = Vector2.zero;
			_root = go.AddComponent<GameMenuRoot>();
			_root.Build();
			// GameMenu opened the help at the start of a single-player sandbox game while "show help at game start"
			// is on (TutorialConditionsAreTrue).
			if (!reload && GameHelp.Available && Brix.Engine.Neo.showHelpAtGameStart)
			{
				_root.Model.Help.Open();
			}
			Plugin.Log.LogInfo("CsGameMenu: the in-game menu is drawn");
		}
		catch (Exception ex)
		{
			Plugin.Log.LogError("CsGameMenu: could not build the in-game menu: " + ex);
		}
	}

	private static void Close()
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
			Plugin.Log.LogError("CsGameMenu: closing: " + ex);
		}
		UnityEngine.Object.DestroyImmediate(_root.gameObject);
		_root = null;
		GameHelp.Reset();
	}
}

// The menu of one game: its model, its views (bars, sidebars) and its once-a-frame work.
internal class GameMenuRoot : MonoBehaviour
{
	public GameMenuController Model { get; private set; }

	private Brix.External.Factories.Factory.AssetKey _held;

	private Brix.Game.Components.CraftingStation _station;

	internal void Build()
	{
		GameUiState.Reset();
		GameUiState.Poll();
		GameBlueprintLibrary.Initialize();
		Model = new GameMenuController();
		GameMenuItems.Apply(Model);
		GamePopupBuilder.AddTooltips(Model.Popup);
		Transform parent = transform;
		CsTopBarView.Create(parent);
		if (ResourceList.Enabled)
		{
			ResourceListView.Create(parent, Model);
		}
		CsBarView.Create(parent, Model.LeftBar, rightSide: false, LuiStyle.BgDefault);
		CsBarView.Create(parent, Model.RightBar, rightSide: true, LuiStyle.BgDefault);
		foreach (UiPanel panel in new UiPanel[] { Model.NewTask, Model.Structures, Model.Blocks, Model.Blueprints })
		{
			CsPanelView.Create(parent, panel, rightSide: false);
		}
		CsPanelView.Create(parent, Model.Settings, rightSide: true);
		CsPanelView.Create(parent, Model.Help, rightSide: true);
		CsPanelView.Create(parent, Model.Debug, rightSide: true);
		GameTaskList.Create(parent);
		CsPopupView.Create(parent, Model.Popup);
		CsUpgradeWindow.Create(parent, Upgrades.UpgradeHall.Research);
		CsUpgradeWindow.Create(parent, Upgrades.UpgradeHall.Smithy);
		CsUpgradeWindow.Create(parent, Upgrades.UpgradeHall.Armoury);
		CsMarketWindow.Create(parent);
		CsPausedView.Create(parent);
		foreach (UiPanel panel in Model.ExtraPanels)
		{
			CsPanelView.Create(parent, panel, panel.Group == Model.Right);
		}
		// GameMenu's OnOpen: the menu starts in its Projects mode.
		Model.ActivateProjects();
		Model.OnStateChanged();
		_held = GameUiState.HeldBlueprint;
		_station = GameUiState.Station;
	}

	internal void Teardown()
	{
		GameHotkeys.UnregisterAll();
		GameHotkeys.ResetCategories();
		GameActions.SetScoreboardVisible(true);
		GameActions.Forget();
		GameUiState.Reset();
		CsWindowDrag.Reset();
	}

	private void Update()
	{
		if (Model == null)
		{
			return;
		}
		GameUiState.Poll();
		Model.OnStateChanged();
		if (!Equals(GameUiState.HeldBlueprint, _held))
		{
			_held = GameUiState.HeldBlueprint;
			if (_held != null)
			{
				Model.OnHeldChanged();
			}
		}
		if (GameUiState.Station != _station)
		{
			_station = GameUiState.Station;
			Model.OnStationChanged();
		}
		CsItemViews.Tick();
	}
}

