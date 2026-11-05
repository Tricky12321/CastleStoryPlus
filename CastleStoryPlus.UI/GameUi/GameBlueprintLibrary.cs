using System;
using System.Collections.Generic;
using Brix.Engine;
using Brix.Game.Compounders;
using Brix.Lua;
using Brix.Steam;
using Brix.UI.Icons;
using CastleStoryPlus.LuaUi;
using UnityEngine;

namespace CastleStoryPlus.GameUi;

// The blueprint library (GameMenu.lua's blueprintsMenu): a sidebar beside the left bar with the name of the
// blueprints selected in the world and a save button (a thumbnail is taken first), the saved blueprints' load,
// publish to the Workshop and delete, and the list of saved blueprints (up to MaxCompounds) with their thumbnails. All of it
// goes straight to the game's BlueprintCompounder, as the Lua's BlueprintSelection calls did.
internal static class GameBlueprintLibrary
{
	// The game itself has no limit; its Lua menu had 16 slots. The sidebar scrolls.
	private const int MaxCompounds = 100;

	private static readonly List<string> Compounds = new List<string>();

	// A thumbnail is being taken for a save.
	private static bool _saving;

	private static bool _clearConnected;

	private static readonly Color DeleteRed = new Color(1f, 0.1f, 0.1f, 1f);

	// GameMenu.lua's first line: the saved blueprints read from disk, cleared again when the game is left.
	internal static void Initialize()
	{
		BlueprintCompounder.InitializeCompoundDictionary();
		if (!_clearConnected)
		{
			_clearConnected = true;
			GameSignals.Connect(GameSignals.LeaveGame, BlueprintCompounder.ClearStaticLists);
		}
		UpdateList();
		_saving = false;
	}

	private static void UpdateList()
	{
		Compounds.Clear();
		Compounds.AddRange(BlueprintCompounder.blueprintsDictionary.Keys);
	}

	// UIGame.UpdateBlueprintThumbnail: a thumbnail of the selected blueprints (menus hidden), then the save.
	private static void Save()
	{
		if (_saving)
		{
			return;
		}
		_saving = true;
		try
		{
			ScreenShotTaker.CaptureScreenshot(hideMenus: true, createThumbnail: true, compoundMode: true, 1f, (Texture2D texture, Texture2D thumbnail) =>
			{
				_saving = false;
				Brix.Utils.TextureUtils.CopyTo(texture, ref UIGameObserver.TmpBlueprintThumbnail);
				OnScreenshotTaken();
			});
		}
		catch (Exception ex)
		{
			// The screenshot taker is busy (another screenshot).
			_saving = false;
			LuaBridge.LogOnce("CsGameMenu blueprint thumbnail", ex.Message);
		}
	}

	// The thumbnail is taken: the selected blueprints are saved under the typed name (hk_OnBlueprintScreenshotTaken).
	private static void OnScreenshotTaken()
	{
		try
		{
			BlueprintCompounder.Duplicate();
			BlueprintCompounder.SaveSelected();
			UpdateList();
			GameUiState.Announce();
		}
		catch (Exception ex)
		{
			Plugin.Log.LogError("CsGameMenu: saving a blueprint: " + ex);
		}
	}

	private static bool HasSelectedAny
	{
		get { return BlueprintCompounder.currentSelected != string.Empty; }
	}

	private static bool SelectionIsFromWorkshop
	{
		get
		{
			return BlueprintCompounder.blueprintsDictionary.TryGetValue(BlueprintCompounder.currentSelected, out Brix.Assets.Asset_Blueprints asset) && asset != null && asset.authorType == Brix.Assets.AssetAuthorType.Workshop;
		}
	}

	private static bool HasBlueprintSelection
	{
		get { return Brix.UI.GameSelector.UIGameSelector.HasSelectionInTrack(Brix.UI.GameSelector.SelectionTrackID.Blueprints) || Brix.UI.GameSelector.UIGameSelector.HasSelectionInTrack(Brix.UI.GameSelector.SelectionTrackID.Blocks); }
	}

	private static bool CanSave
	{
		get { return HasBlueprintSelection && Compounds.Count < MaxCompounds && BlueprintCompounder.currentName != string.Empty; }
	}

	internal static UiPanel Build()
	{
		UiPanel panel = new UiPanel { Id = "blueprintsMenu", Title = () => GameUiState.Text("##gamemenu_taskbar_blueprints") };
		UiSection actions = panel.Section("blueprintsMenuActions", () => GameUiState.Text("##gamemenu_taskbar_blueprintactions"));
		actions.Add(new UiInput
		{
			Id = "blueprintName",
			Text = () => BlueprintCompounder.currentName,
			Set = (string text) => BlueprintCompounder.currentName = text,
			Button = new UiButton
			{
				Id = "blueprintSave",
				Label = () => GameUiState.Text("##gamemenu_taskbar_saveblueprint"),
				Icon = () => GameUiState.Icon(IconKeys.SaveBlueprints),
				IconColor = () => CanSave ? LuiStyle.CastleYellow : LuiStyle.Gray,
				OnClick = (bool shift, bool ctrl) =>
				{
					if (CanSave)
					{
						Save();
					}
				}
			}
		});
		UiSection info = panel.Section("blueprintsMenuInfo", () => GameUiState.Text("##gamemenu_taskbar_blueprintssaved") + " (" + Compounds.Count + "/" + MaxCompounds + ")");
		info.CellSize = 48f;
		info.Add(new UiButton
		{
			Id = "blueprintLoad",
			Label = () => GameUiState.Text("##gamemenu_taskbar_loadblueprint"),
			Icon = () => GameUiState.Icon(IconKeys.LoadBlueprints),
			IconColor = () => HasSelectedAny ? LuiStyle.CastleYellow : LuiStyle.Gray,
			OnClick = (bool shift, bool ctrl) =>
			{
				if (HasSelectedAny && !GameUiState.Paused)
				{
					BlueprintCompounder.LoadCompoundBlueprints(BlueprintCompounder.blueprintsDictionary[BlueprintCompounder.currentSelected], fromFile: true);
				}
			}
		});
		info.Add(new UiButton
		{
			Id = "blueprintPublish",
			Label = () => GameUiState.Text("##gamemenu_taskbar_publishblueprint"),
			Icon = () => GameUiState.Icon(IconKeys._UI_Steam),
			IconColor = () => (HasSelectedAny && SteamWorkshopAgent.IsConnected && !SelectionIsFromWorkshop) ? LuiStyle.CastleYellow : LuiStyle.Gray,
			OnClick = (bool shift, bool ctrl) =>
			{
				if (HasSelectedAny && SteamWorkshopAgent.IsConnected && !SelectionIsFromWorkshop)
				{
					BlueprintCompounder.SendToWorkshop();
					UpdateList();
					GameSignals.Invoke(GameSignals.OnAutoFadeDialog, GameUiState.Text("##gamemenu_taskbar_blueprintpublished"), 3f);
				}
			}
		});
		info.Add(new UiButton
		{
			Id = "blueprintDelete",
			Label = () => GameUiState.Text("##gamemenu_taskbar_blueprintdelete"),
			Icon = () => GameUiState.Icon(IconKeys.UI_Trash),
			IconColor = () => (HasSelectedAny && !SelectionIsFromWorkshop) ? DeleteRed : LuiStyle.Gray,
			OnClick = (bool shift, bool ctrl) =>
			{
				if (HasSelectedAny && !SelectionIsFromWorkshop)
				{
					Delete();
				}
			}
		});
		UiSection list = panel.Section("blueprintsMenuList", null);
		for (int i = 0; i < MaxCompounds; i++)
		{
			int slot = i;
			list.Add(new UiRow
			{
				Id = "blueprintCompound" + (slot + 1),
				Visible = () => slot < Compounds.Count,
				Icon = () => Thumbnail(Compounds[slot]),
				IconColor = () => BlueprintCompounder.instantiatingCompound ? LuiStyle.Gray : LuiStyle.White,
				Text = () => Compounds[slot],
				Highlight = () => BlueprintCompounder.currentSelected == Compounds[slot],
				OnClick = () =>
				{
					if (!BlueprintCompounder.instantiatingCompound && slot < Compounds.Count)
					{
						BlueprintCompounder.currentSelected = Compounds[slot];
						BlueprintCompounder.lua_CompoundSelectionChanged._signal.Invoke();
					}
				}
			});
		}
		return panel;
	}

	// BlueprintSelection.DeleteSelected.
	private static void Delete()
	{
		string selected = BlueprintCompounder.currentSelected;
		Brix.Assets.Asset_Blueprints asset = BlueprintCompounder.blueprintsDictionary[selected];
		BlueprintCompounder.blueprintsDictionary.Remove(selected);
		BlueprintCompounder.thumbnailDictionary.Remove(selected);
		BlueprintCompounder.currentSelected = string.Empty;
		asset.Disk_Delete();
		LuaHookBase.InvokeFromCLR(BlueprintCompounder.lua_OnCompoundDeleted);
		UpdateList();
	}

	// A saved blueprint's thumbnail, made once (GetThumbnailFromFile).
	private static Sprite Thumbnail(string key)
	{
		if (!BlueprintCompounder.blueprintsDictionary.TryGetValue(key, out Brix.Assets.Asset_Blueprints asset) || asset == null || asset.Thumbnail == null)
		{
			return null;
		}
		Dictionary<string, Sprite> sprites = BlueprintCompounder.thumbnailDictionary;
		if (!sprites.TryGetValue(key, out Sprite sprite) || sprite == null || sprite.texture == null)
		{
			Texture2D texture = asset.Thumbnail;
			sprite = Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height), new Vector2(0.5f, 0.5f));
			sprites[key] = sprite;
		}
		return sprite;
	}
}
