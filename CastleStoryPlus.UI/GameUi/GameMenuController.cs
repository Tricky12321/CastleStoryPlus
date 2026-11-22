using System;
using System.Collections.Generic;
using Brix.Engine;
using Brix.Engine.Nature;
using Brix.Game;
using Brix.Game.AI;
using Brix.Input;
using Brix.Legacy;
using Brix.NewUI.CornerMenus;
using Brix.Audio;
using Brix.UI.Icons;
using Brix.Utils.UI;
using CastleStoryPlus.LuaUi;
using UnityEngine;

namespace CastleStoryPlus.GameUi;

// The in-game menu (GameMenu.lua) as C#: the left bar (score, blueprints, new task, structures, blocks, task count),
// the right bar (home, settings, back to work, call to arms), their sidebars and the hotkeys of each input mode.
// Built once per game (and again on a UI reload) by GameMenuRoot; features add their own items to the model
// through GameMenuItems.Extend.
internal sealed class GameMenuController : GameMenuModel
{
	// Whether a build task was selected at the last selection change (leaving it resets the input modes).
	private bool _wasBuild;

	private Project _lastSelected;

	private bool _scoreHidden;

	public GameMenuController()
	{
		NewTask = BuildNewTask();
		Blocks = BuildCatalogue("blocks", "##gamemenu_taskbar_blocks", BuildCatalog.Blocks, BuildCatalog.BlockGroups);
		Structures = BuildCatalogue("structures", "##gamemenu_taskbar_structures", BuildCatalog.Structures, BuildCatalog.StructureGroups);
		Settings = BuildSettings();
		Blueprints = GameBlueprintLibrary.Build();
		Blueprints.Group = Left;
		Help = GameHelp.Build(this);
		Debug = GameDebugSidebar.Build(this);
		Popup = GamePopupBuilder.Build(this);
		NewTask.Group = Left;
		Blocks.Group = Left;
		Structures.Group = Left;
		Settings.Group = Right;
		BuildLeftBar();
		BuildRightBar();
		Blocks.OnOpen = () =>
		{
			DeactivateProjects();
			ActivateBricks();
		};
		Structures.OnOpen = () =>
		{
			DeactivateProjects();
			ActivateStructures();
		};
		NewTask.OnOpen = () =>
		{
			Brix.UI.GameSelector.UIGameSelector.UnselectAll(Brix.UI.GameSelector.SelectionType.Crafting);
		};
		NewTask.OnClose = () =>
		{
			DeactivateTasks();
			ActivateProjects();
		};
		Left.Changed += OnLeftChanged;
	}

	// ---- Bars ----

	private void BuildLeftBar()
	{
		// Spacers above and below: the buttons sit in the middle of the screen's height, the task count at the bottom.
		LeftBar.Add(null);
		LeftBar.Add(new UiButton
		{
			Id = "score",
			Label = () => GameUiState.Text("##gamemenu_score"),
			Icon = () => GameUiState.Icon(IconKeys.UI_Score),
			Selected = () => !_scoreHidden,
			OnClick = (bool shift, bool ctrl) =>
			{
				_scoreHidden = !_scoreHidden;
				ShowScoreboard();
			}
		});
		LeftBar.Add(new UiButton
		{
			Id = "blueprints",
			Label = () => GameUiState.Text("##gamemenu_taskbar_blueprints"),
			Icon = () => GameUiState.Icon(IconKeys.SelectBlueprints),
			IconColor = () => GameUiState.IsSelectedBuild ? LuiStyle.CastleYellow : LuiStyle.Gray,
			Selected = () => Blueprints.IsOpen,
			OnClick = (bool shift, bool ctrl) =>
			{
				if (!GameUiState.Paused && GameUiState.IsSelectedBuild)
				{
					Blueprints.Open();
					SetSelectingBlueprints(true);
				}
			}
		});
		NewTaskButton = new UiButton
		{
			Id = "newTask",
			Label = () => GameUiState.Text("##gamemenu_taskbar_newtask"),
			Icon = () => GameUiState.Icon(IconKeys._UI_Pick),
			Hotkey = () => GameUiState.KeyOf("Camera", "command_CreateProject"),
			Selected = () => NewTask.IsOpen,
			OnClick = (bool shift, bool ctrl) => OpenNewTask()
		};
		LeftBar.Add(NewTaskButton);
		StructuresButton = new UiButton
		{
			Id = "structures",
			Label = () => GameUiState.Text("##gamemenu_taskbar_structures"),
			Icon = () => GameUiState.Icon(IconKeys.UI_Stockpile),
			Hotkey = () => GameUiState.KeyOf("Camera", "command_StructuresMenu"),
			Selected = () => Structures.IsOpen,
			OnClick = (bool shift, bool ctrl) => OpenCatalogue(Structures, Brix.External.Factories.Blueprints.Pallet, structures: true)
		};
		LeftBar.Add(StructuresButton);
		BlocksButton = new UiButton
		{
			Id = "blocks",
			Label = () => GameUiState.Text("##gamemenu_taskbar_blocks"),
			Icon = () => GameUiState.Icon(IconKeys.UI_Brick),
			Hotkey = () => GameUiState.KeyOf("Camera", "command_BlocksMenu"),
			Selected = () => Blocks.IsOpen,
			OnClick = (bool shift, bool ctrl) => OpenCatalogue(Blocks, Brix.External.Factories.Blueprints.Stone_DoubleBrick, structures: false)
		};
		LeftBar.Add(BlocksButton);
		LeftBar.Add(null);
		LeftBar.Add(new UiButton
		{
			Id = "taskLimit",
			Label = () => (GameUiState.TaskCount - 1) + " / " + (Project.projectCap - 1),
			Count = () => (GameUiState.TaskCount - 1) + "/" + (Project.projectCap - 1),
			CountColor = () => GameUiState.UnderTaskCap ? LuiStyle.CastleYellow : LuiStyle.CastleRed,
			Hotkey = () => GameUiState.Text("##gamemenu_taskbar_tasknumber")
		});
	}

	private void BuildRightBar()
	{
		RightBar.Add(new UiButton
		{
			Id = "home",
			Label = () => GameUiState.Text("##gamemenu_home"),
			Icon = () => GameUiState.Icon(IconKeys.UI_Home),
			Hotkey = () => "F10",
			OnClick = (bool shift, bool ctrl) =>
			{
				if (GameUiState.Paused)
				{
					TogglePaused();
				}
				InputModeController.ToggleMainMenu();
			}
		});
		RightBar.Add(new UiButton
		{
			Id = "settings",
			Label = () => GameUiState.Text("##gamemenu_topmenu_settings"),
			Icon = () => GameUiState.Icon(IconKeys.UI_Settings),
			Selected = () => Settings.IsOpen,
			OnClick = (bool shift, bool ctrl) => Settings.Toggle()
		});
		RightBar.Add(GameHelp.Button(Help));
		RightBar.Add(GameDebugSidebar.Button(Debug));
		RightBar.Add(null);
		BackToWork = new UiButton
		{
			Id = "backtowork",
			Label = () => GameUiState.Text("##gamemenu_crystalwheel_backtowork"),
			Icon = () => GameUiState.Icon(IconKeys._UI_Hammer),
			Hotkey = () => GameUiState.KeyOf("Camera", "command_BackToWork"),
			OnClick = (bool shift, bool ctrl) =>
			{
				if (!GameUiState.Paused)
				{
					GameActions.BackToWork();
				}
			}
		};
		RightBar.Add(BackToWork);
		CallToArms = new UiButton
		{
			Id = "calltoarms",
			Label = () => GameUiState.Text("##gamemenu_crystalwheel_calltoarms"),
			Icon = () => GameUiState.Icon(IconKeys._UI_CallToArms),
			Hotkey = () => GameUiState.KeyOf("Camera", "command_CallToArms"),
			OnClick = (bool shift, bool ctrl) =>
			{
				if (!GameUiState.Paused)
				{
					GameActions.CallToArms();
				}
			}
		};
		RightBar.Add(CallToArms);
	}

	// ---- Left bar actions ----

	// Blocks or structures: pick the catalogue's first blueprint and open it in a build task, or go to the idle group
	// and let the game start a build task in that mode.
	private void OpenCatalogue(UiPanel catalogue, Brix.External.Factories.Factory.AssetKey first, bool structures)
	{
		if (GameUiState.Paused)
		{
			return;
		}
		Brix.Input.InputContextComponent.defaultHeldObject = first;
		if (GameUiState.IsSelectedBuild)
		{
			Picking.SetBlueprintInPicker(first);
			catalogue.Open();
		}
		else if (TeamBuildTask() is Brix.Game.AI.BuildProject existing)
		{
			// The game would select its last selected build task, and with none (merged away by GlobalBuildJob) make a
			// new, empty one each time that was never selected: the team's build task is selected here instead.
			GameUiState.Select(existing, true);
			Picking.SetBlueprintInPicker(first);
			ActivateBlueprints();
			catalogue.Open();
		}
		else if (GameUiState.HasSelected)
		{
			GameUiState.Select(GameUiState.IdleProject_, true);
			if (structures)
			{
				InputModeController.structuresPanelButtonPressed = true;
			}
			else
			{
				InputModeController.bricksPanelButtonPressed = true;
			}
		}
	}

	// The local team's build task with the most workers, then the most blueprints; null when there is none.
	private static Brix.Game.AI.BuildProject TeamBuildTask()
	{
		User user = User.LocalUser;
		if (user == null || user.faction == null)
		{
			return null;
		}
		Brix.Game.AI.BuildProject best = null;
		int bestBlueprints = -1;
		foreach (GameObject go in Brix.Components.BrixSingleton<Brix.Lifecycle.Pooling.AutoList>.Instance.GetInstances(Brix.External.Factories.Groupes.Construction))
		{
			Brix.Game.AI.BuildProject project = (go != null) ? go.GetComponent<Brix.Game.AI.BuildProject>() : null;
			if (project == null || !project.IsStillAlive || project.faction != user.faction)
			{
				continue;
			}
			Brix.Game.AI.BuildGoalProvider provider = project.GetComponent<Brix.Game.AI.BuildGoalProvider>();
			int blueprints = (provider != null) ? provider.TrackedBlueprints.Count : 0;
			if (best == null || project.CrewCount() > best.CrewCount() || (project.CrewCount() == best.CrewCount() && blueprints > bestBlueprints))
			{
				best = project;
				bestBlueprints = blueprints;
			}
		}
		return best;
	}

	private void OpenNewTask()
	{
		if (GameUiState.Paused)
		{
			return;
		}
		DeactivateProjects();
		ActivateTasks();
		if (GameUiState.IsSelectedNotIdle)
		{
			GameUiState.Select(GameUiState.Selected, false);
			NewTask.Open();
		}
		else
		{
			NewTask.Toggle();
		}
	}

	internal void SetSelectingBlueprints(bool on)
	{
		Project selected = UIGameObserver.projects.CurrentSelected;
		if (on && !Picking.IsSelectingBlueprint())
		{
			if (selected != null)
			{
				selected.GetComponent<Brix.Input.InputContextComponent>().SaveCurrentContext();
			}
			Picking.SetModeToBlueprintSelection();
		}
		else if (!on && Picking.IsSelectingBlueprint())
		{
			if (selected != null)
			{
				selected.GetComponent<Brix.Input.InputContextComponent>().LoadContext();
			}
		}
		if (Picking.IsSelectingBlueprint())
		{
			ActivateBlueprints();
		}
		else
		{
			DeactivateBlueprints();
		}
	}

	internal void TogglePaused()
	{
		InputModeController.TogglePause();
		GameUiState.Sound(InputModeController.IsPaused() ? "Pause_In" : "Pause_Out");
	}

	// ---- New task ----

	private UiPanel BuildNewTask()
	{
		UiPanel panel = new UiPanel { Id = "newTask", Title = () => GameUiState.Text("##gamemenu_taskbar_newtask") };
		UiSection alert = panel.Section("alert", null);
		alert.Visible = () => !GameUiState.UnderTaskCap;
		alert.Add(new UiText
		{
			Id = "tooManyTasks",
			Text = () => GameUiState.Text("##gamemenu_help_popup_toomanytasks"),
			Color = () => LuiStyle.CastleRed
		});
		UiSection repair = panel.Section("newRepairTask", () => GameUiState.Text("##gamemenu_taskbar_maintenancetasks"));
		repair.Add(TaskButton("newRepairTask", "##gamemenu_taskbar_repair", IconKeys._Repair_Brick, "project_CreateRepairTask", "Tool_Repair", () => Placing(Brix.External.Factories.Groupes.Repair)));
		UiSection harvest = panel.Section("newHarvestTask", () => GameUiState.Text("##gamemenu_taskbar_harvesttasks"));
		harvest.Add(TaskButton("newHarvestWoodTask", "##gamemenu_taskbar_trees", IconKeys._Cut_Tree, "project_CreateWoodTask", "Tool_HarvestTrees", () => Placing(NatureType.tree)));
		harvest.Add(TaskButton("newHarvestPlantsTask", "##gamemenu_taskbar_plants", IconKeys._Harvest_Plant, "project_CreatePlantsTask", "Tool_HarvestPlants", () => Placing(NatureType.plant)));
		harvest.Add(TaskButton("newCleanupTask", "##gamemenu_taskbar_cleaning", IconKeys._Cleanup, "project_CreateCleaningTask", "Tool_Clean", () => Placing(Brix.External.Factories.Groupes.Cleanup)));
		harvest.Add(TaskButton("newHarvestIronTask", "##gamemenu_taskbar_iron", IconKeys._Harvest_Iron, "project_CreateIronTask", "Tool_HarvestIron", () => Placing(NatureType.iron)));
		harvest.Add(TaskButton("newHarvestBrimstoneTask", "##gamemenu_taskbar_brimstone", IconKeys._Harvest_Orange, "project_CreateBrimstoneTask", "Tool_HarvestBrimstone", () => Placing(NatureType.pyrite)));
		harvest.Add(TaskButton("newHarvestBoulderTask", "##gamemenu_taskbar_boulders", IconKeys._Harvest_Stone, "project_CreateBoulderTask", "Tool_HarvestBoulders", () => Placing(NatureType.boulder)));
		harvest.Add(TaskButton("newHarvestTask", "##gamemenu_taskbar_harvest", IconKeys._Harvest, "project_CreateHarvestTask", "Tool_HarvestEverything", () => Placing(NatureType.everything)));
		UiSection mining = panel.Section("newMiningTask", () => GameUiState.Text("##gamemenu_taskbar_miningtasks"));
		mining.Add(TaskButton("newTunnelTask", "##gamemenu_taskbar_tunnel", IconKeys._Tool_Pickaxe, "project_CreateTunnelTask", "Tool_Tunnel", () => Placing(Brix.External.Factories.Groupes.Tunnel)));
		mining.Add(TaskButton("newLandscapeTask", "##gamemenu_taskbar_landscape", IconKeys._Break_Voxel, "project_CreateLandscapeTask", "Tool_Landscape", () => Placing(Brix.External.Factories.Groupes.Landscape)));
		mining.Add(TaskButton("newQuarryTask", "##gamemenu_taskbar_quarry", IconKeys._Tool_Shovel, "project_CreateQuarryTask", "Tool_Quarry", () => Placing(Brix.External.Factories.Groupes.Mine)));
		UiSection construction = panel.Section("newConstructionTask", () => GameUiState.Text("##gamemenu_taskbar_constructiontasks"));
		construction.Add(TaskButton("newTaskBuild", "##gamemenu_taskbar_build", IconKeys._Build_Brick, "project_CreateBuildTask", "Tool_Construction", null));
		return panel;
	}

	// The hotkey actions of the new-task buttons (Tasks mode), by button id.
	private readonly Dictionary<string, string> _taskHotkeys = new Dictionary<string, string>();

	private UiButton TaskButton(string id, string label, IconKey icon, string hotkey, string tool, Func<bool> placing)
	{
		_taskHotkeys[id] = hotkey;
		return new UiButton
		{
			Id = id,
			Label = () => GameUiState.Text(label),
			Icon = () => GameUiState.Icon(icon),
			IconColor = () => GameUiState.UnderTaskCap ? LuiStyle.White : LuiStyle.Gray,
			Hotkey = () => GameUiState.KeyOf("Projects", hotkey),
			Highlight = placing,
			OnClick = (bool shift, bool ctrl) =>
			{
				if (!GameUiState.Paused && GameUiState.UnderTaskCap)
				{
					Instructions.Execute(tool);
				}
			}
		};
	}

	private static bool Placing(Brix.External.Factories.Factory.AssetKey key)
	{
		return InputModeController.ProjectBeingPlaced() == key;
	}

	private static bool Placing(NatureType type)
	{
		return InputModeController.HarvestBeingPlaced() == type;
	}

	// ---- Catalogues ----

	private UiPanel BuildCatalogue(string id, string title, List<BuildCatalog.Entry> entries, BuildCatalog.Group[] groups)
	{
		UiPanel panel = new UiPanel { Id = id, Title = () => GameUiState.Text(title) };
		foreach (BuildCatalog.Group group in groups)
		{
			string groupTitle = group.Title;
			UiSection section = panel.Section(id + group.Id, () => GameUiState.Text(groupTitle));
			foreach (BuildCatalog.Entry entry in entries)
			{
				if (entry.Group != group.Id)
				{
					continue;
				}
				BuildCatalog.Entry e = entry;
				section.Add(new UiButton
				{
					Id = e.Key.Name,
					Visible = e.Shown,
					Label = e.Name,
					Icon = e.Icon,
					IconColor = () => LuiStyle.White,
					Hotkey = () => string.IsNullOrEmpty(e.Hotkey) ? string.Empty : GameUiState.KeyOf("Projects", e.Hotkey),
					Highlight = () => GameUiState.HeldBlueprint == e.Key,
					Tooltip = e.Tooltip,
					OnClick = (bool shift, bool ctrl) =>
					{
						if (!GameUiState.Paused)
						{
							Picking.SetBlueprintInPicker(e.Key);
						}
					}
				});
			}
		}
		return panel;
	}

	// ---- Settings ----

	private UiPanel BuildSettings()
	{
		UiPanel panel = new UiPanel { Id = "settings", Title = () => GameUiState.Text("##gamemenu_topmenu_settings") };
		UiSection section = panel.Section("settings", null);
		bool sandboxLike = !Neo.IsMultiplayer && InputModeController.DefaultMode != InputMode.worldEditor;
		bool editor = InputModeController.DefaultMode == InputMode.worldEditor;
		if (sandboxLike)
		{
			section.Add(Setting("pause", "##gamemenu_topmenu_pause", () => GameUiState.Icon(IconKeys.UI_Pause), TogglePaused));
			section.Add(Setting("quicksave", "##gamemenu_topmenu_quicksave", () => GameUiState.Icon(IconKeys.UI_Quicksave), () => MenuCalls.QuickSave(default(MoonSharp.Interpreter.ScriptExecutionContext), default(MoonSharp.Interpreter.CallbackArguments))));
		}
		section.Add(Setting("screenshot", "##gamemenu_topmenu_screenshot", () => GameUiState.Icon(IconKeys.UI_Screenshot), () => MenuCalls.TakeScreenShot(default(MoonSharp.Interpreter.ScriptExecutionContext), default(MoonSharp.Interpreter.CallbackArguments))));
		section.Add(Setting("mute", "##gamemenu_topmenu_mute", () => GameUiState.Icon(SoundEngine.mute ? IconKeys.UI_Mute : IconKeys.UI_VolumeHigh), () => SoundEngine.mute = !SoundEngine.mute));
		if (!editor)
		{
			section.Add(Setting("xray", "##gamemenu_topmenu_xray", () => GameUiState.Icon(IconKeys.UI_XRay), () => XRayImpl.instance.enabled = !XRayImpl.instance.enabled));
			section.Add(Setting("toggledebug", "##gamemenu_topmenu_toggledebug", () => GameUiState.Icon(IconKeys.UI_Console), () => Architecte.allowDebugMenu = !Architecte.allowDebugMenu));
		}
		section.Add(Setting("editorcamera", "##gamemenu_topmenu_editorcamera", () => GameUiState.Icon(IconKeys.UI_TerrainHug), GameActions.ToggleEditorCamera));
		return panel;
	}

	private static UiButton Setting(string id, string label, Func<Sprite> icon, Action action)
	{
		return new UiButton
		{
			Id = id,
			Label = () => GameUiState.Text(label),
			Icon = icon,
			OnClick = (bool shift, bool ctrl) => action()
		};
	}

	// ---- Input modes (GameMenu.lua's Activate/DeactivateInputMode*) ----

	internal void ActivateProjects()
	{
		GameHotkeys.Register("command_CallToArms", () => CallToArms.Click(false, false));
		GameHotkeys.Register("command_BackToWork", () => BackToWork.Click(false, false));
		GameHotkeys.Register("command_BlocksMenu", () => BlocksButton.Click(false, false));
		GameHotkeys.Register("command_StructuresMenu", () => StructuresButton.Click(false, false));
		GameHotkeys.Register("command_CreateProject", () => NewTaskButton.Click(false, false));
		GameHotkeys.Register("project_ToggleXRay", () => XRayImpl.instance.enabled = !XRayImpl.instance.enabled);
		for (int i = 1; i <= 5; i++)
		{
			string slot = "operableRecipe" + i;
			GameHotkeys.Register("project_QueueSlot" + i, () => PopupButton(slot)?.Click(false, false));
		}
		GameHotkeys.Register("project_ToggleLoop", () => PopupButton("toggleLoop")?.Click(false, false));
		GameHotkeys.Register("project_ClearQueue", () => PopupButton("clearQueue")?.Click(false, false));
		for (int i = 1; i <= 6; i++)
		{
			int slot = i;
			GameHotkeys.Register("project_TaskActionSlot" + i, () => TaskAction(slot));
		}
		RegisterTaskKeys();
		GameHotkeys.Register("project_OpenBlueprintManagement", () => PopupButton("BlueprintActive")?.Click(false, false));
		foreach (KeyValuePair<string, Action> key in GameMenuItems.ProjectHotkeys)
		{
			GameHotkeys.Register(key.Key, key.Value);
		}
		GameHotkeys.SetCombatHotkeys(true);
		GameHotkeys.SetCategory(KeyBindingsUtility.Category.General, false);
		GameHotkeys.SetCategory(KeyBindingsUtility.Category.Projects, true);
	}

	internal void DeactivateProjects()
	{
		foreach (string action in new string[] { "command_CallToArms", "command_BackToWork", "command_BlocksMenu", "command_StructuresMenu", "command_CreateProject", "project_ToggleXRay", "project_QueueSlot1", "project_QueueSlot2", "project_QueueSlot3", "project_QueueSlot4", "project_QueueSlot5", "project_ToggleLoop", "project_ClearQueue", "project_TaskActionSlot1", "project_TaskActionSlot2", "project_TaskActionSlot3", "project_TaskActionSlot4", "project_TaskActionSlot5", "project_TaskActionSlot6", "project_RaisePriority", "project_LowerPriority", "project_DeleteTask" })
		{
			GameHotkeys.Unregister(action);
		}
		foreach (KeyValuePair<string, Action> key in GameMenuItems.ProjectHotkeys)
		{
			GameHotkeys.Unregister(key.Key);
		}
		GameHotkeys.SetCombatHotkeys(false);
		GameHotkeys.SetCategory(KeyBindingsUtility.Category.General, true);
		GameHotkeys.SetCategory(KeyBindingsUtility.Category.Projects, false);
	}

	private void ActivateTasks()
	{
		foreach (UiSection section in NewTask.Sections)
		{
			foreach (UiItem item in section.Items)
			{
				if (item is UiButton button && _taskHotkeys.TryGetValue(button.Id, out string action))
				{
					GameHotkeys.Register(action, () => button.Click(false, false));
				}
			}
		}
		GameHotkeys.Register("command_CallToArms", () => CallToArms.Click(false, false));
		GameHotkeys.Register("command_BlocksMenu", () => BlocksButton.Click(false, false));
		GameHotkeys.Register("command_StructuresMenu", () => StructuresButton.Click(false, false));
	}

	private void DeactivateTasks()
	{
		foreach (string action in _taskHotkeys.Values)
		{
			GameHotkeys.Unregister(action);
		}
		GameHotkeys.Unregister("command_CallToArms");
		GameHotkeys.Unregister("command_BlocksMenu");
		GameHotkeys.Unregister("command_StructuresMenu");
	}

	private void ActivateBricks()
	{
		GameHotkeys.Register("command_CallToArms", () => CallToArms.Click(false, false));
		GameHotkeys.Register("command_StructuresMenu", () => StructuresButton.Click(false, false));
		GameHotkeys.Register("command_CreateProject", () => NewTaskButton.Click(false, false));
		RegisterCatalogueKeys(BuildCatalog.Blocks);
		RegisterTaskKeys();
	}

	private void ActivateStructures()
	{
		GameHotkeys.Register("command_CallToArms", () => CallToArms.Click(false, false));
		GameHotkeys.Register("command_BlocksMenu", () => BlocksButton.Click(false, false));
		GameHotkeys.Register("command_CreateProject", () => NewTaskButton.Click(false, false));
		RegisterCatalogueKeys(BuildCatalog.Structures);
		RegisterTaskKeys();
	}

	// Raise and lower priority and delete the task: also while a catalogue is open.
	private void RegisterTaskKeys()
	{
		GameHotkeys.Register("project_RaisePriority", () => PriorityRaise.Click(false, false));
		GameHotkeys.Register("project_LowerPriority", () => PriorityLower.Click(false, false));
		GameHotkeys.Register("project_DeleteTask", () => DeleteTask.Click(false, false));
		foreach (KeyValuePair<string, Action> key in GameMenuItems.TaskHotkeys)
		{
			GameHotkeys.Register(key.Key, key.Value);
		}
	}

	internal UiButton PopupButton(string id)
	{
		foreach (UiSection section in Popup.Sections)
		{
			foreach (UiItem item in section.Items)
			{
				if (item.Id == id && item is UiButton button)
				{
					return button.IsVisible() ? button : null;
				}
			}
		}
		return null;
	}

	// The n-th option of the selected task's type (OnActionSelectedTask): harvest, tunnel, landscape or quarry.
	private void TaskAction(int slot)
	{
		string[][] ids = new string[][]
		{
			new string[] { "harvestBoulder", "tunnelDirUp", "landscapeRaiseAll", "quarryStairs" },
			new string[] { "harvestIron", "tunnelDirStraight", "landscapeDigAll", "quarryKeepStone" },
			new string[] { "harvestBrimstone", "tunnelDirDown", "quarryKeepIron" },
			new string[] { "harvestTree", "tunnelKeepStone", "quarryKeepBrimstone" },
			new string[] { "harvestPlant", "tunnelKeepIron" },
			new string[] { "harvestPlant", "tunnelKeepBrimstone" }
		};
		foreach (string id in ids[slot - 1])
		{
			UiButton button = PopupButton(id);
			if (button != null && SectionShown(id))
			{
				button.Click(false, false);
			}
		}
	}

	private bool SectionShown(string itemId)
	{
		foreach (UiSection section in Popup.Sections)
		{
			if (section.Items.Exists((UiItem i) => i.Id == itemId))
			{
				return section.IsVisible();
			}
		}
		return false;
	}

	private static void RegisterCatalogueKeys(List<BuildCatalog.Entry> entries)
	{
		foreach (BuildCatalog.Entry entry in entries)
		{
			if (!string.IsNullOrEmpty(entry.Hotkey) && entry.Shown())
			{
				Brix.External.Factories.Factory.AssetKey key = entry.Key;
				GameHotkeys.Register(entry.Hotkey, () => Picking.SetBlueprintInPicker(key));
			}
		}
	}

	private void ActivateBlueprints()
	{
		GameHotkeys.Register("tb_constr_blueprints_duplicate", () => Brix.Game.Compounders.BlueprintCompounder.Duplicate());
		GameHotkeys.Register("tb_constr_blueprints_delete", () => Brix.UI.UIGameActor.DeleteSelectedBlueprints());
		GameHotkeys.Register("tb_constr_blueprints_expand", () => Brix.Game.Compounders.BlueprintCompounder.Expand());
		GameHotkeys.Register("tb_constr_blueprints_clear", () => Brix.Game.Compounders.BlueprintCompounder.Cancel());
		GameHotkeys.SetCategory(KeyBindingsUtility.Category.General, false);
		GameHotkeys.SetCategory(KeyBindingsUtility.Category.Blueprints, true);
	}

	private void DeactivateBlueprints()
	{
		GameHotkeys.SetCategory(KeyBindingsUtility.Category.Blueprints, false);
		GameHotkeys.SetCategory(KeyBindingsUtility.Category.General, true);
	}

	// ---- Reactions (GameMenu's listeners) ----

	// Once a frame from GameMenuRoot.
	internal void OnStateChanged()
	{
		Project selected = GameUiState.Selected;
		if (selected != _lastSelected)
		{
			_lastSelected = selected;
			OnSelectionChanged();
		}
	}

	private void OnSelectionChanged()
	{
		bool build = GameUiState.IsSelectedBuild;
		if (_wasBuild && !build)
		{
			// OnDeselectBuildProject.
			DeactivateBlueprints();
			ActivateProjects();
			Left.CloseAll();
		}
		bool enteredBuild = build && !_wasBuild;
		_wasBuild = build;
		if (enteredBuild)
		{
			// The structures or blocks button made a build task with the blueprint already held from the last time: the
			// held blueprint does not change, so its catalogue is opened here.
			OnHeldChanged();
		}
		// The scoreboard moves aside while a task is selected (OnToggleSidebarSpacerHack).
		bool notIdle = GameUiState.IsSelectedNotIdle;
		GameActions.SetSidebarSpacer(notIdle);
		GameUiState.Sound(notIdle ? "Edit_Mode_In" : "Edit_Mode_Out");
		if (notIdle && NewTask.IsOpen)
		{
			NewTask.Close();
		}
	}

	// The held blueprint changed in a build task: open its catalogue (OnReopenBuild).
	internal void OnHeldChanged()
	{
		if (!GameUiState.IsSelectedBuild)
		{
			return;
		}
		ActivateBlueprints();
		foreach (Func<bool> keepClosed in GameMenuItems.KeepCatalogueClosed)
		{
			if (keepClosed())
			{
				return;
			}
		}
		Brix.External.Factories.Factory.AssetKey held = GameUiState.HeldBlueprint;
		if (BuildCatalog.IsBlock(held))
		{
			Blocks.Open();
		}
		else if (BuildCatalog.IsStructure(held))
		{
			Structures.Open();
		}
	}

	internal void OnStationChanged()
	{
		if (GameUiState.Station != null && NewTask.IsOpen)
		{
			NewTask.Close();
		}
	}

	private void OnLeftChanged()
	{
		if (Left.Open == null)
		{
			GameHotkeys.ResetCategories();
		}
		ShowScoreboard();
	}

	// The scoreboard hides while a left sidebar is open, or when the score button hid it.
	private void ShowScoreboard()
	{
		GameActions.SetScoreboardVisible(!_scoreHidden && Left.Open == null);
	}
}
