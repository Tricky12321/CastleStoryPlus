using System;
using Brix.Assets;
using Brix.Engine;
using Brix.Game;
using Brix.Network;
using Brix.UI.Builder.Menu;
using CastleStoryPlus.Core;
using CastleStoryPlus.LuaUi;
using CastleStoryPlus.Menus;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;

namespace CastleStoryPlus.DevTools.Worlds;

// Starts a new single-player world for /api/world/new the way the game's new game screen does: the map, mode and
// preset are chosen as the screen chooses them (BaseMapLibraryMenu), made the loaded ones and GameParam.StartGame
// loads them (BaseMapStarterMenu.OnOk). From inside a game it first leaves without saving, like "leave game" in
// the quit dialog (Architecte.QuitToMainMenu), and starts once the title screen is up again.
internal class WorldStarter : MonoBehaviour
{
	private const string MenuScene = "SceneMenu";

	// The title screen has to be up this long before a game starts from it. Its menus load over a few frames, and
	// right after the game's start the network manager is not ready yet: a world started 2.5 s after the title
	// screen came up failed to spawn the local player ("SpawnWithClientAuthority NetworkConnection is not ready!"),
	// hung 40 s in GameLoader.StartStep and came up without its in-game menu (the intro dialog could not be closed).
	private const float MenuSettleSeconds = 5f;

	// After the world is in: how long the in-game menu may take to hand its tables to the C# menus.
	private const float MenuAttachSeconds = 20f;

	// Gives up when the title screen is not reached in time.
	private const float LeaveTimeoutSeconds = 60f;

	// Gives up waiting for the new world to finish loading.
	private const float LoadTimeoutSeconds = 600f;

	internal static WorldStarter Instance;

	// What it is doing, shown by /api/status: null when idle.
	internal string Phase;

	internal string LastResult;

	private Asset_Map _map;

	private Asset_Gamemode _mode;

	private Asset_GamemodePreset _preset;

	private int _waveMinutes;

	// A save to load instead of a new world (its last played mode and preset).
	private bool _isSave;

	// The map opened in the world editor instead.
	private bool _isEditor;

	private float _menuSince = -1f;

	private float _giveUpAt;

	private float _loadedAt = -1f;

	private void Awake()
	{
		Instance = this;
	}

	internal bool Busy => Phase != null;

	// Main thread. The map, mode and preset come from WorldEndpoints, already checked.
	internal void BeginSave(Asset_Map save)
	{
		Begin(save, null, null, 0);
		_isSave = true;
	}

	internal void BeginEditor(Asset_Map map)
	{
		Begin(map, BaseMapLibraryMenu.WorldEditorGamemode, null, 0);
		_isEditor = true;
	}

	internal void Begin(Asset_Map map, Asset_Gamemode mode, Asset_GamemodePreset preset, int waveMinutes)
	{
		_isSave = false;
		_isEditor = false;
		_map = map;
		_mode = mode;
		_preset = preset;
		_waveMinutes = waveMinutes;
		_menuSince = -1f;
		_loadedAt = -1f;
		_giveUpAt = Time.realtimeSinceStartup + LeaveTimeoutSeconds;
		if (InGameScene())
		{
			Phase = "leaving the current game";
			try
			{
				Architecte.QuitToMainMenu();
			}
			catch (Exception ex)
			{
				// A Lua menu closing on the way out can fail (its BeforeCloseAll disconnecting a hook it never got:
				// "bad argument #1 to 'Hooks:Disconnect'"); the game is left all the same.
				DevToolsPlugin.Log.LogWarning("World starter: leaving the game: " + ex.Message);
			}
		}
		else
		{
			Phase = "waiting for the title screen";
		}
	}

	private static bool InGameScene()
	{
		return Architecte.instance != null && SceneManager.GetActiveScene().name != MenuScene;
	}

	private void Update()
	{
		if (Phase == null)
		{
			return;
		}
		if (Phase == "loading")
		{
			UpdateLoading();
			return;
		}
		bool onMenu = SceneManager.GetActiveScene().name == MenuScene && GameParam.instance != null && Architecte.instance == null && NetworkIdle();
		if (!onMenu)
		{
			_menuSince = -1f;
			if (Time.realtimeSinceStartup > _giveUpAt)
			{
				Phase = null;
				LastResult = "failed: the title screen was not reached";
			}
			return;
		}
		if (_menuSince < 0f)
		{
			_menuSince = Time.realtimeSinceStartup;
			Phase = "waiting for the title screen";
		}
		if (Time.realtimeSinceStartup - _menuSince < MenuSettleSeconds)
		{
			return;
		}
		if (_isSave)
		{
			_mode = LoadSave(_map);
			if (_mode == null)
			{
				Phase = null;
				LastResult = "failed: the save " + _map.customName + " has no mode to load it in (from another version?)";
				return;
			}
		}
		else if (_isEditor)
		{
			EditWorld(_map);
		}
		else
		{
			StartWorld(_map, _mode, _preset, _waveMinutes);
		}
		Phase = "loading";
		_giveUpAt = Time.realtimeSinceStartup + LoadTimeoutSeconds;
	}

	// No game hosted or joined, not even one still shutting down: the network manager is free to host a new one.
	private static bool NetworkIdle()
	{
		return UNetManager.instance != null && !NetworkServer.active && !NetworkClient.active;
	}

	// The world counts as started once the game runs with the local player in it (its user and faction), and the
	// in-game menu built: with the C# menus on, GameMenu hands its tables over at the end of its build.
	private void UpdateLoading()
	{
		if (Architecte.instance == null || !Architecte.commence || Architecte.termine)
		{
			if (Time.realtimeSinceStartup > _giveUpAt)
			{
				Phase = null;
				LastResult = "failed: the world did not finish loading in time";
			}
			return;
		}
		if (_loadedAt < 0f)
		{
			_loadedAt = Time.realtimeSinceStartup;
		}
		// The world editor has no bricktrons of the player's and its own menu.
		bool player = _isEditor || (User.LocalUser != null && User.LocalUser.faction != null);
		bool menu = !Plugin.IsEnabled(Features.CsMenus) || (_isEditor ? (!LuaUiConfig.CsWorldEditor || CastleStoryPlus.GameUi.CsWorldEditor.IsOpen) : LuaUiConfig.CsGameMenu ? CastleStoryPlus.GameUi.CsGameMenu.Model != null : (!LuaUiConfig.AnyGameMenu || GameMenuLink.Menu != null));
		if (player && menu)
		{
			Phase = null;
			LastResult = (_isSave ? "loaded " + _map.customName : (_isEditor ? "editing " : "started ") + _map.displayName) + " (" + _mode.codename + ")";
			return;
		}
		if (Time.realtimeSinceStartup - _loadedAt > MenuAttachSeconds)
		{
			Phase = null;
			LastResult = "failed: " + _map.displayName + " loaded but " + (player ? "its in-game menu was not built" : "the local player was not spawned") + "; the game is probably unusable, see /api/log?problems=true and start again";
			DevToolsPlugin.Log.LogWarning("DevTools: " + LastResult);
		}
	}

	// As the title screen's Continue loads a save (ContainerGenericPopulator.ContinueLatestSave): in its last played
	// mode and preset. The mode, or null when the save has none.
	private static Asset_Gamemode LoadSave(Asset_Map save)
	{
		BaseMapLibraryMenu.SetSelectedMap(save, AssetPlayableContext.Singleplayer, BaseAssetMenu.MenuGameContext.Normal, BaseAssetMenu.MenuTypeContext.Existing);
		Asset_Gamemode mode = GameParam.SelectedGamemode;
		if (mode == null)
		{
			BaseMapLibraryMenu.EmptySelected();
			return null;
		}
		BaseMapLibraryMenu.SetSelectedAsLoaded();
		GameParam.StartGame(() =>
		{
			BaseMapLibraryMenu.EmptySelected();
		});
		DevToolsPlugin.Log.LogInfo("DevTools: loading the save " + save.customName + " (" + mode.codename + ")");
		return mode;
	}

	// The world editor library's: the map in the world editor context (its only mode is world_editor, with its
	// default preset), then BaseMapStarterMenu.OnOk.
	private static void EditWorld(Asset_Map map)
	{
		BaseMapLibraryMenu.SetSelectedMap(map, AssetPlayableContext.Singleplayer, BaseAssetMenu.MenuGameContext.WorldEditor, BaseAssetMenu.MenuTypeContext.New);
		BaseMapLibraryMenu.SetSelectedAsLoaded();
		GameParam.StartGame(() =>
		{
			BaseMapLibraryMenu.EmptySelected();
		});
		DevToolsPlugin.Log.LogInfo("DevTools: opening " + map.displayName + " in the world editor");
	}

	// The new game screen's own calls: SetSelectedMap picks the map's first mode and its default preset, then the
	// chosen mode and preset replace them, and OnOk makes them the loaded ones and starts.
	private static void StartWorld(Asset_Map map, Asset_Gamemode mode, Asset_GamemodePreset preset, int waveMinutes)
	{
		if (waveMinutes > 0)
		{
			WaveInterval.Chosen = waveMinutes;
		}
		BaseMapLibraryMenu.SetSelectedMap(map, AssetPlayableContext.Singleplayer, BaseAssetMenu.MenuGameContext.Normal, BaseAssetMenu.MenuTypeContext.New);
		BaseMapLibraryMenu.SetSelectedGamemode(mode);
		BaseMapLibraryMenu.SetSelectedPreset(preset);
		BaseMapLibraryMenu.SetSelectedAsLoaded();
		GameParam.StartGame(() =>
		{
			BaseMapLibraryMenu.EmptySelected();
		});
		DevToolsPlugin.Log.LogInfo("DevTools: starting a new world: " + map.displayName + ", " + mode.codename + ", " + ((preset != null) ? preset.codename : "default preset"));
	}
}
