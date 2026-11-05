using System;
using Brix.Audio;
using Brix.External.Factories;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Input;
using Brix.Lua;
using Brix.UI;
using Brix.UI.GameSelector;
using Brix.UI.Icons;
using Brix.Utils;
using Brix.Utils.UI;
using CastleStoryPlus.LuaUi;
using UnityEngine;

namespace CastleStoryPlus.GameUi;

// What the in-game menu shows, read straight from the game's C# (what LUI/Data/Data_Project.lua and its siblings
// wrapped for the Lua menu): the selected task and its type, the held blueprint, the selected workshop, pause.
// Changes are looked for once a frame (Poll) and announced through Changed, so views refresh when something they
// show may have changed instead of reading everything a few times a second.
internal static class GameUiState
{
	// The game's names for its task types (the task's LuaDriver handle), as GameMenu compared them.
	internal const string Build = "fy_BuildBlocksProject";

	internal const string Quarry = "fy_QuarryProject";

	internal const string Tunnel = "fy_TunnelProject";

	internal const string Harvest = "fy_HarvestProject";

	internal const string Landscape = "fy_LandscapeProject";

	internal const string Repair = "fy_RepairProject";

	internal const string Cleanup = "fy_CleanupProject";

	internal const string Idle = "fy_idleProject";

	// Something the menus show may have changed: the selection, the held blueprint, the workshop, pause.
	internal static event Action Changed;

	private static Project _selected;

	private static string _selectedType;

	private static Factory.AssetKey _held;

	private static CraftingStation _station;

	private static bool _paused;

	private static int _taskCount = -1;

	// The selected task (the idle group when nothing else is), or null.
	internal static Project Selected
	{
		get { return _selected; }
	}

	internal static string SelectedType
	{
		get { return _selectedType; }
	}

	internal static Factory.AssetKey HeldBlueprint
	{
		get { return _held; }
	}

	// The selected workshop (crafting station), or null.
	internal static CraftingStation Station
	{
		get { return _station; }
	}

	internal static bool Paused
	{
		get { return _paused; }
	}

	internal static void Reset()
	{
		_selected = null;
		_selectedType = null;
		_held = null;
		_station = null;
		_paused = false;
		_taskCount = -1;
	}

	// Once a frame: what changed since the last frame is announced once.
	internal static void Poll()
	{
		bool changed = false;
		Project selected = ReadSelected();
		if (selected != _selected)
		{
			_selected = selected;
			_selectedType = TypeOf(selected);
			changed = true;
		}
		Factory.AssetKey held = Picking.BlueprintBeingPlaced();
		if (!Equals(held, _held))
		{
			_held = held;
			changed = true;
		}
		CraftingStation station = CraftingStation.SelectedStation();
		if (station != _station)
		{
			_station = station;
			changed = true;
		}
		bool paused = InputModeController.IsPaused();
		if (paused != _paused)
		{
			_paused = paused;
			changed = true;
		}
		int tasks = (UIGameObserver.projects != null && UIGameObserver.projects.all != null) ? UIGameObserver.projects.all.Count : 0;
		if (tasks != _taskCount)
		{
			_taskCount = tasks;
			changed = true;
		}
		if (changed)
		{
			Announce();
		}
	}

	// Something changed that Poll does not look at (a click on a menu button): views refresh at once.
	internal static void Announce()
	{
		if (Changed == null)
		{
			return;
		}
		try
		{
			Changed();
		}
		catch (Exception ex)
		{
			Plugin.Log.LogError("GameUi: refresh: " + ex);
		}
	}

	private static Project ReadSelected()
	{
		if (UIGameObserver.projects == null || UIGameObserver.projects.all == null)
		{
			return null;
		}
		foreach (Project project in UIGameObserver.projects.all)
		{
			if (project != null && project.selection)
			{
				return project;
			}
		}
		return IdleProject.GetAlliedProject();
	}

	// The task's or workshop's type name (its LuaDriver handle: fy_BuildBlocksProject, fy_Furnace...), or null.
	internal static string TypeOf(Component thing)
	{
		if (thing == null)
		{
			return null;
		}
		LuaDriver driver = thing.GetComponent<LuaDriver>();
		return (driver != null) ? driver.Handle : null;
	}

	internal static bool Exists(Project project)
	{
		return project != null && !project.IsNullOrReleased() && !project.IsBeingPlaced;
	}

	internal static bool HasSelected
	{
		get { return Exists(_selected); }
	}

	internal static bool IsSelectedOfType(string type)
	{
		return HasSelected && _selectedType == type;
	}

	internal static bool IsSelectedIdle
	{
		get { return IsSelectedOfType(Idle); }
	}

	internal static bool IsSelectedBuild
	{
		get { return IsSelectedOfType(Build); }
	}

	// A task other than the idle group is selected.
	internal static bool IsSelectedNotIdle
	{
		get { return HasSelected && _selectedType != Idle; }
	}

	internal static Project IdleProject_
	{
		get { return IdleProject.GetAlliedProject(); }
	}

	// Data.Project:SetSelection: selects the task, or with select false goes back to the idle group.
	internal static void Select(Project project, bool select)
	{
		if (select && project != null)
		{
			UIGameSelector.Select(project.gameObject);
		}
		else
		{
			UIGameActor.SelectIdleProject();
		}
	}

	internal static bool UnderTaskCap
	{
		get { return UIGameObserver.projects != null && UIGameObserver.projects.all != null && UIGameObserver.projects.all.Count < Project.projectCap; }
	}

	internal static int TaskCount
	{
		get { return (UIGameObserver.projects != null && UIGameObserver.projects.all != null) ? UIGameObserver.projects.all.Count : 0; }
	}

	// English for the keys the game's own menus use but its localization lacks (it showed the key itself).
	private static readonly System.Collections.Generic.Dictionary<string, string> Missing = new System.Collections.Generic.Dictionary<string, string>
	{
		{ "##task_actions_priority_raise", "Raise priority" },
		{ "##task_actions_priority_lower", "Lower priority" }
	};

	// The game's localized text for a ##key (GetLocalized). A key the localization lacks gets the English above, or
	// its last words ("##task_actions_priority_raise": "Priority raise") instead of the key itself.
	internal static string Text(string key)
	{
		string text = I2Helper.TryGet(key);
		if (string.IsNullOrEmpty(key) || !key.StartsWith("##") || (!string.IsNullOrEmpty(text) && !text.StartsWith("##")))
		{
			return text;
		}
		if (Missing.TryGetValue(key, out string english))
		{
			return english;
		}
		string[] words = key.Substring(2).Split('_');
		string tail = (words.Length >= 2) ? words[words.Length - 2] + " " + words[words.Length - 1] : words[words.Length - 1];
		return (tail.Length > 0) ? char.ToUpper(tail[0]) + tail.Substring(1) : text;
	}

	internal static Sprite Icon(IconKey key)
	{
		return (key != null) ? key.Get64() : null;
	}

	// An IconKeys field by its name ("PickUp", "_Bricktron"), as the Lua menus wrote IconKeys.PickUp.
	internal static IconKey IconNamed(string name)
	{
		System.Reflection.FieldInfo field = string.IsNullOrEmpty(name) ? null : typeof(IconKeys).GetField(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
		return (field != null) ? field.GetValue(null) as IconKey : null;
	}

	// The key bound to an action, as the game's buttons show it (UIGame.GetKeyBindingCodename).
	internal static string KeyOf(string category, string action)
	{
		try
		{
			return KeyBindingsUtility.GetKeyBindingCodename(category, action);
		}
		catch (Exception)
		{
			return string.Empty;
		}
	}

	// A menu sound (Meta_Sound.lua's names: Pause_In, Edit_Mode_In...).
	internal static void Sound(string name)
	{
		try
		{
			SoundEngine.Play(name);
		}
		catch (Exception ex)
		{
			LuaBridge.LogOnce("GameUi sound " + name, ex.Message);
		}
	}

	internal static Color Yellow
	{
		get { return LuiStyle.CastleYellow; }
	}
}
