using System.Collections.Generic;

namespace CastleStoryPlus.LuaUi;

// The [LuaUi] switches: one per part of the game's Lua UI that has a C# replacement. All are off by default, and
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
		get { return Switch("CsScoreboard", "Draw the gamemode's top-right panel (wave timer, kills, crystals, players) in C#."); }
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

	// A small C# copy of GameMenu's right bar, drawn next to the game's own, to check the C# menu kit in a game.
	public static bool CsSmokeTest
	{
		get { return Switch("CsSmokeTest", "Debug: show a C# copy of the in-game right bar below the game's own, to check the C# menus."); }
	}

	// Whether any part of GameMenu is drawn in C#: GameMenu then hands its menu table to C# when it is built.
	public static bool AnyGameMenu
	{
		get { return CsBars || CsBuildMenus || CsSettings || CsTooltips || CsTaskPopup || CsTaskList || CsBlueprintLibrary || CsSmokeTest; }
	}

	// Whether the C# menu kit is needed at all.
	public static bool Any
	{
		get { return CsScoreboard || AnyGameMenu; }
	}

	// Switched off as a whole with the CsMenus feature.
	private static bool Switch(string key, string description)
	{
		if (!Read.TryGetValue(key, out bool value))
		{
			value = Plugin.IsEnabled(Core.Features.CsMenus) && Plugin.Cfg.Bind(Section, key, false, description + " Restart to apply.").Value;
			Read[key] = value;
		}
		return value;
	}
}
