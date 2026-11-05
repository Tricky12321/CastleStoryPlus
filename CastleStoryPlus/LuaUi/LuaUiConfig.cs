using System.Collections.Generic;

namespace CastleStoryPlus.LuaUi;

// The [LuaUi] switches: one per part of the game's UI that has a C# replacement. CsScoreboard, CsModals, CsMainMenu and CsGameMenu are on by default, the rest off, and
// a part that is off is not touched at all (no Lua patch, no Harmony patch), so the game's own menu is built as
// before. Bound on first use rather than in an Enable(): features are enabled in no particular order, and a later
// part's patch classes read these from their Prepare() while the plugin starts. Each is read once, so it applies
// after a restart (like the [Features] switches) and the Lua patches made at start always match the C# views.
internal static class LuaUiConfig
{
	private const string Section = "LuaUi";

	private static readonly Dictionary<string, bool> Read = new Dictionary<string, bool>();

	// The gamemode's top-right panel (GS_Scoreboard: wave timer, kills, crystals, players).
	public static bool CsScoreboard
	{
		get { return Switch("CsScoreboard", "Draw the gamemode's top-right panel (wave timer, kills, crystals, players) in C#.", true); }
	}

	// GameMenu's top bar, left bar and right bar.
	public static bool CsBars
	{
		get { return Switch("CsBars", "Draw the in-game top, left and right bars in C#."); }
	}

	// GameMenu's new-task sidebar and the structure and block catalogues.
	public static bool CsBuildMenus
	{
		get { return Switch("CsBuildMenus", "Draw the new-task sidebar and the building and block catalogues in C#."); }
	}

	// GameMenu's settings sidebar and the paused overlay.
	public static bool CsSettings
	{
		get { return Switch("CsSettings", "Draw the in-game settings sidebar and the paused overlay in C#."); }
	}

	// GameMenu's tooltips and help articles.
	public static bool CsTooltips
	{
		get { return Switch("CsTooltips", "Draw the in-game tooltips and help articles in C#."); }
	}

	// GameMenu's task popup (the selected task's options, recipes and queue).
	public static bool CsTaskPopup
	{
		get { return Switch("CsTaskPopup", "Draw the selected task's popup (options, recipes, queue) in C#."); }
	}

	// GameMenu's task list.
	public static bool CsTaskList
	{
		get { return Switch("CsTaskList", "Draw the task list in C#."); }
	}

	// GameMenu's blueprint library.
	public static bool CsBlueprintLibrary
	{
		get { return Switch("CsBlueprintLibrary", "Draw the blueprint library in C#."); }
	}

	// The gamemodes' dialogs (LUI/Menus/Game/Menu_Modal.lua in GS_Modal: intro, victory, defeat). On by default.
	public static bool CsModals
	{
		get { return Switch("CsModals", "Draw the gamemodes' dialogs (intro, victory, defeat) in C#.", true); }
	}

	// The title screen's button columns (SceneMenu's legacy "Left: ..." containers). On by default.
	public static bool CsMainMenu
	{
		get { return Switch("CsMainMenu", "Draw the title screen's menu buttons (main menu, play, multiplayer, options) in C#.", true); }
	}

	// The whole in-game menu in C# (GameUi): GameMenu.lua is not loaded at all, the C# menus read the game's state
	// directly. On by default; the other GameMenu parts below only apply with this off.
	public static bool CsGameMenu
	{
		get { return Switch("CsGameMenu", "Draw the whole in-game menu (bars, build menus, task popup, task list, settings) in C# and do not load the game's Lua GameMenu at all.", true); }
	}

	// The world editor's menu (Gamemodes/world_editor/Menu/WorldEditorMenu.lua, GS_WorldEditor) in C#: the Lua file is
	// not loaded. Off by default while the C# one does not have every tab yet.
	public static bool CsWorldEditor
	{
		get { return Switch("CsWorldEditor", "Draw the world editor's menu in C# and do not load the game's Lua world editor menu (not every tab is done yet)."); }
	}

	// A small C# copy of GameMenu's right bar, drawn next to the game's own, to check the C# menu kit in a game.
	public static bool CsSmokeTest
	{
		get { return Switch("CsSmokeTest", "Debug: show a C# copy of the in-game right bar below the game's own, to check the C# menus."); }
	}

	// Whether any part of the Lua GameMenu is drawn in C#: GameMenu then hands its menu table to C# when it is built
	// (only with CsGameMenu off, else GameMenu is not loaded).
	public static bool AnyGameMenu
	{
		get { return !CsGameMenu && (CsBars || CsBuildMenus || CsSettings || CsTooltips || CsTaskPopup || CsTaskList || CsBlueprintLibrary || CsSmokeTest); }
	}

	// Whether the C# menu kit is needed at all.
	public static bool Any
	{
		get { return CsScoreboard || CsModals || AnyGameMenu; }
	}

	// The parts once off by default and since moved to C# for good: a config written before keeps false, so it is
	// switched on once (DefaultsVersion 1); a player can still switch one off again after that.
	private static readonly HashSet<string> NowOn = new HashSet<string> { "CsScoreboard", "CsModals" };

	private const int DefaultsVersion = 1;

	private static BepInEx.Configuration.ConfigEntry<int> _defaults;

	// Switched off as a whole with the CsMenus feature.
	private static bool Switch(string key, string description, bool byDefault = false)
	{
		if (!Read.TryGetValue(key, out bool value))
		{
			if (!Plugin.IsEnabled(Core.Features.CsMenus))
			{
				value = false;
			}
			else
			{
				BepInEx.Configuration.ConfigEntry<bool> entry = Plugin.Cfg.Bind(Section, key, byDefault, description + " Restart to apply.");
				if (NowOn.Contains(key) && Migrating() && !entry.Value)
				{
					entry.Value = true;
					Plugin.Log.LogInfo("LuaUi: " + key + " switched on (now the C# menu by default)");
				}
				value = entry.Value;
			}
			Read[key] = value;
		}
		return value;
	}

	// Whether this start applies the default changes: decided (and recorded) on the first read, so every part read
	// later in the same start is switched on too.
	private static bool _migrating;

	private static bool Migrating()
	{
		if (_defaults == null)
		{
			_defaults = Plugin.Cfg.Bind(Section, "DefaultsVersion", 0, "Which of the C# menus' default changes this config has had (set by the mod; do not change).");
			_migrating = _defaults.Value < DefaultsVersion;
			if (_migrating)
			{
				_defaults.Value = DefaultsVersion;
			}
		}
		return _migrating;
	}
}
