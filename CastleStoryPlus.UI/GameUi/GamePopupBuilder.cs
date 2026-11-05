using System;
using System.Collections.Generic;
using Brix.Engine.Nature;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.AI.UI;
using Brix.Game.Components;
using Brix.Game.Components.Tunnel;
using Brix.Game.Network;
using Brix.Input;
using Brix.Lua;
using Brix.UI;
using Brix.UI.Icons;
using CastleStoryPlus.Core;
using CastleStoryPlus.LuaUi;
using CastleStoryPlus.Metallurgy;
using UnityEngine;

namespace CastleStoryPlus.GameUi;

// The selected task's popup over the task list (GameMenu.lua's projectContext sections): its goals, the options of
// its type (build: blueprint selection; landscape: raise or dig all; quarry: stairs and what to keep; tunnel:
// direction and what to keep; harvest: what to harvest), the selected workshop (what it makes now, its queue and
// recipes), the task's priority and its ok/apply/cancel/delete, and for the idle group the population and the
// crystal's energy. Each section shows only while it applies.
internal static class GamePopupBuilder
{
	private static Project Sel
	{
		get { return GameUiState.Selected; }
	}

	private static bool Not(bool paused)
	{
		return !paused;
	}

	internal static UiPanel Build(GameMenuController menu)
	{
		UiPanel popup = new UiPanel { Id = "popup" };
		UiSection goals = popup.Section("goals", () => GameUiState.Text("##task_goals"));
		goals.Visible = () => GameUiState.IsSelectedNotIdle && GameUiState.Station == null;
		goals.Add(new UiButton
		{
			Id = "goalCount",
			Label = () => GameUiState.Text("##task_goals"),
			Icon = () => GameUiState.Icon(GameUiState.IsSelectedBuild ? IconKeys._UI_Brick : IconKeys.UI_Flag),
			Count = () => (Sel != null) ? Sel.GoalCounts().ToString() : "0"
		});

		UiSection build = popup.Section("build", null);
		build.Visible = () => GameUiState.IsSelectedBuild && GameUiState.Station == null;
		build.Add(new UiButton
		{
			Id = "BlueprintActive",
			Label = () => GameUiState.Text("##gamemenu_taskbar_blueprints"),
			Icon = () => GameUiState.Icon(IconKeys.SelectBlueprints),
			Highlight = () => InputModeController.IsSelectingBlueprints(),
			OnClick = (bool shift, bool ctrl) =>
			{
				if (GameUiState.IsSelectedBuild)
				{
					menu.SetSelectingBlueprints(!Picking.IsSelectingBlueprint());
				}
			}
		});
		Func<bool> selecting = () => GameUiState.IsSelectedBuild && InputModeController.IsSelectingBlueprints();
		build.Add(Action("blueprintMove", "##gamemenu_taskbar_blueprints_move", IconKeys.Blueprints_Move, selecting, () => Brix.Game.Compounders.BlueprintCompounder.Move()));
		build.Add(Action("blueprintCopy", "##gamemenu_taskbar_blueprints_copy", IconKeys.CopyBlueprints, selecting, () => Brix.Game.Compounders.BlueprintCompounder.Duplicate()));
		build.Add(Action("blueprintSplit", "##gamemenu_taskbar_blueprints_split", IconKeys.SplitBlueprints, selecting, () => UIGameActor.SplitSelectedBlueprints()));
		build.Add(Action("blueprintDelete", "##gamemenu_taskbar_blueprints_delete", IconKeys.DeleteBlueprints, selecting, () => UIGameActor.DeleteSelectedBlueprints()));
		build.Add(Action("blueprintExpand", "##gamemenu_taskbar_blueprints_expand", IconKeys.ExpandSelection, selecting, () => Brix.Game.Compounders.BlueprintCompounder.Expand()));
		build.Add(Action("blueprintClear", "##gamemenu_taskbar_blueprints_clear", IconKeys.ClearSelection, selecting, () => Brix.Game.Compounders.BlueprintCompounder.Cancel()));

		UiSection landscape = popup.Section("landscape", null);
		landscape.Visible = () => GameUiState.IsSelectedOfType(GameUiState.Landscape) && GameUiState.Station == null;
		landscape.Add(Action("landscapeRaiseAll", "##gamemenu_taskbar_raiseall", IconKeys.UI_RaiseBrush, null, () => Ui<LandscapeProjectUI>()?.SetMaxThreshold()));
		landscape.Add(Action("landscapeDigAll", "##gamemenu_taskbar_digall", IconKeys.UI_TerrainHug, null, () => Ui<LandscapeProjectUI>()?.SetMinThreshold()));

		UiSection quarry = popup.Section("quarry", null);
		quarry.Visible = () => GameUiState.IsSelectedOfType(GameUiState.Quarry) && GameUiState.Station == null;
		quarry.Add(Action("quarryStairs", "##gamemenu_taskbar_stairs", IconKeys.UI_StairsDown, null, () => Ui<QuarryProjectUI>()?.NextStairs()));
		quarry.Add(Keep("quarryKeepStone", "##gamemenu_taskbar_keepstone", IconKeys._Boulder, GroundResource.Stone, tunnel: false));
		quarry.Add(Keep("quarryKeepIron", "##gamemenu_taskbar_keepiron", IconKeys._Raw_Iron, GroundResource.Iron, tunnel: false));
		quarry.Add(Keep("quarryKeepBrimstone", "##gamemenu_taskbar_keepiron", IconKeys._Raw_Orange_Crystal, GroundResource.Brimstone, tunnel: false));

		UiSection tunnel = popup.Section("tunnel", () => GameUiState.Text("##task_tunnel_options"));
		tunnel.Visible = () => GameUiState.IsSelectedOfType(GameUiState.Tunnel) && GameUiState.Station == null;
		tunnel.Add(Direction("tunnelDirUp", "##gamemenu_taskbar_tunnel_up", IconKeys.UI_StairsUp, TunnelType.stairsUp));
		tunnel.Add(Direction("tunnelDirStraight", "##gamemenu_taskbar_tunnel_straight", IconKeys.UI_StraightRight, TunnelType.straight));
		tunnel.Add(Direction("tunnelDirDown", "##gamemenu_taskbar_tunnel_down", IconKeys.UI_StairsDown, TunnelType.stairsDown));
		tunnel.Add(Keep("tunnelKeepStone", "##gamemenu_taskbar_keepstone", IconKeys._Boulder, GroundResource.Stone, tunnel: true));
		tunnel.Add(Keep("tunnelKeepIron", "##gamemenu_taskbar_keepiron", IconKeys._Raw_Iron, GroundResource.Iron, tunnel: true));
		tunnel.Add(Keep("tunnelKeepBrimstone", "##gamemenu_taskbar_keepiron", IconKeys._Raw_Orange_Crystal, GroundResource.Brimstone, tunnel: true));

		UiSection harvest = popup.Section("harvest", () => GameUiState.Text("##task_harvest_options"));
		harvest.Visible = () => GameUiState.IsSelectedOfType(GameUiState.Harvest) && GameUiState.Station == null;
		harvest.Add(Nature("harvestBoulder", "##gamemenu_taskbar_boulders", IconKeys._Boulder, NatureType.boulder));
		harvest.Add(Nature("harvestIron", "##gamemenu_taskbar_iron", IconKeys._Raw_Iron, NatureType.iron));
		harvest.Add(Nature("harvestBrimstone", "##gamemenu_taskbar_brimstone", IconKeys._Raw_Orange_Crystal, NatureType.pyrite));
		harvest.Add(Coal());
		harvest.Add(Nature("harvestTree", "##gamemenu_taskbar_trees", IconKeys._Pine_Tree, NatureType.tree));
		harvest.Add(Nature("harvestPlant", "##gamemenu_taskbar_plants", IconKeys._Plant, NatureType.plant));

		BuildWorkshop(popup);

		UiSection priority = popup.Section("priority", () => GameUiState.Text("##task_priority"));
		priority.Visible = () => GameUiState.IsSelectedNotIdle && GameUiState.Station == null;
		menu.PriorityLower = priority.Add(new UiButton
		{
			Id = "priorityLower",
			Label = () => GameUiState.Text("##task_actions_priority_lower"),
			Icon = () => GameUiState.Icon(IconKeys.UI_Minus),
			IconColor = () => (GameUiState.HasSelected && Sel.GetPriority() != ProjectPriority.Bias.Locked) ? LuiStyle.CastleYellow : LuiStyle.Gray,
			OnClick = (bool shift, bool ctrl) =>
			{
				if (!GameUiState.Paused && GameUiState.HasSelected)
				{
					User.LocalUser.GetComponent<UNetGroupe>().CallCmdLowerPriority(Sel.gameObject);
				}
			}
		});
		menu.PriorityRaise = priority.Add(new UiButton
		{
			Id = "priorityRaise",
			Label = () => GameUiState.Text("##task_actions_priority_raise"),
			Icon = () => GameUiState.Icon(IconKeys.UI_Plus),
			IconColor = () => (GameUiState.HasSelected && Sel.GetPriority() != ProjectPriority.Bias.High) ? LuiStyle.CastleYellow : LuiStyle.Gray,
			OnClick = (bool shift, bool ctrl) =>
			{
				if (!GameUiState.Paused && GameUiState.HasSelected)
				{
					User.LocalUser.GetComponent<UNetGroupe>().CallCmdRaisePriority(Sel.gameObject);
				}
			}
		});

		UiSection actions = popup.Section("taskActions", () => GameUiState.Text("##task_actions"));
		actions.Visible = () => GameUiState.IsSelectedNotIdle && GameUiState.Station == null;
		actions.Add(Status("taskActionsOk", "##task_actions_ok", IconKeys.UI_Check, new Color(0.4f, 0.9f, 0.3f), Project.CommitStatusAction.Ok));
		actions.Add(Status("taskActionsApply", "##task_actions_apply", IconKeys.UI_Down, LuiStyle.CastleYellow, Project.CommitStatusAction.Apply));
		actions.Add(Status("taskActionsCancel", "##task_actions_cancel", IconKeys.UI_X, LuiStyle.CastleRed, Project.CommitStatusAction.Cancel));
		menu.DeleteTask = actions.Add(Status("taskActionsDelete", "##task_actions_delete", IconKeys.UI_Trash, LuiStyle.CastleRed, Project.CommitStatusAction.Delete));

		BuildIdle(popup);
		return popup;
	}

	// After the features added theirs: every button with an article and no tooltip of its own gets the article.
	internal static void AddTooltips(UiPanel popup)
	{
		foreach (UiSection section in popup.Sections)
		{
			foreach (UiItem item in section.Items)
			{
				UiButton button = item as UiButton;
				string article = (button != null && button.Tooltip == null) ? GamePopupMenu.ArticleOf(button.Id) : null;
				if (article != null)
				{
					button.Tooltip = GameArticles.Tooltip(button.Label, article);
				}
			}
		}
		UiButton goals = popup.Find("goals").Items.Find((UiItem i) => i.Id == "goalCount") as UiButton;
		if (goals != null)
		{
			Func<string> label = goals.Label;
			goals.Tooltip = () => GameArticles.Tooltip(label, GoalsArticle())();
		}
	}

	// The goals' article of the selected task's type.
	private static string GoalsArticle()
	{
		if (GameUiState.IsSelectedBuild)
		{
			return "Task_Build_Goals";
		}
		if (GameUiState.IsSelectedOfType(GameUiState.Quarry))
		{
			return "Task_Quarry_Goals";
		}
		if (GameUiState.IsSelectedOfType(GameUiState.Tunnel))
		{
			return "Task_Tunnel_Goals";
		}
		if (GameUiState.IsSelectedOfType(GameUiState.Landscape))
		{
			return "Task_Landscape_Goals";
		}
		if (Sel is HarvestProject h)
		{
			NatureType types = h.data.AcceptedTypes;
			if (types == NatureType.tree)
			{
				return "Task_Harvest_Goals_Trees";
			}
			if (types == NatureType.plant)
			{
				return "Task_Harvest_Goals_Plants";
			}
			if (types == NatureType.iron)
			{
				return "Task_Harvest_Goals_Iron";
			}
			if (types == NatureType.pyrite)
			{
				return "Task_Harvest_Goals_Brimstone";
			}
			if (types == NatureType.boulder)
			{
				return "Task_Harvest_Goals_Boulders";
			}
		}
		string type = GameUiState.SelectedType;
		if (type == GameUiState.Repair)
		{
			return "Task_Repair_Goals";
		}
		if (type == GameUiState.Cleanup)
		{
			return "Task_Cleanup_Goals";
		}
		return "Task_All_Goals";
	}

	private static T Ui<T>() where T : Component
	{
		return (Sel != null) ? Sel.GetComponentInChildren<T>(true) : null;
	}

	private static UiButton Action(string id, string label, IconKey icon, Func<bool> visible, Action run)
	{
		return new UiButton
		{
			Id = id,
			Visible = visible,
			Label = () => GameUiState.Text(label),
			Icon = () => GameUiState.Icon(icon),
			IconColor = () => LuiStyle.CastleYellow,
			OnClick = (bool shift, bool ctrl) =>
			{
				if (!GameUiState.Paused)
				{
					run();
				}
			}
		};
	}

	// A quarry's or tunnel's "keep" toggle (ToggleResourceTypeFlag).
	private static UiButton Keep(string id, string label, IconKey icon, GroundResource resource, bool tunnel)
	{
		return new UiButton
		{
			Id = id,
			Label = () => GameUiState.Text(label),
			Icon = () => GameUiState.Icon(icon),
			IconColor = () => LuiStyle.White,
			Highlight = () =>
			{
				if (tunnel)
				{
					TunnelProject t = Sel as TunnelProject;
					return t != null && t.TypesToHarvest.HasFlag(resource);
				}
				MineProject m = Sel as MineProject;
				return m != null && m.TypesToHarvest.HasFlag(resource);
			},
			OnClick = (bool shift, bool ctrl) =>
			{
				if (GameUiState.Paused || Sel == null)
				{
					return;
				}
				UNetProjectCmd cmd = User.LocalUser.GetComponent<UNetProjectCmd>();
				if (tunnel && Sel is TunnelProject)
				{
					cmd.CallCmdTunnelToggleResource(Sel.gameObject, (int)resource);
				}
				else if (!tunnel && Sel is MineProject)
				{
					cmd.CallCmdQuarryToggleResource(Sel.gameObject, (int)resource);
				}
			}
		};
	}

	private static UiButton Direction(string id, string label, IconKey icon, TunnelType type)
	{
		return new UiButton
		{
			Id = id,
			Label = () => GameUiState.Text(label),
			Icon = () => GameUiState.Icon(icon),
			IconColor = () => LuiStyle.CastleYellow,
			Highlight = () => Sel is TunnelProject t && t.tunnelVolume != null && t.tunnelVolume.GetExpansionType() == type,
			OnClick = (bool shift, bool ctrl) =>
			{
				if (!GameUiState.Paused && Sel is TunnelProject t && t.tunnelVolume != null)
				{
					t.tunnelVolume.SetExpansionType(type);
				}
			}
		};
	}

	private static UiButton Nature(string id, string label, IconKey icon, NatureType type)
	{
		return new UiButton
		{
			Id = id,
			Label = () => GameUiState.Text(label),
			Icon = () => GameUiState.Icon(icon),
			IconColor = () => LuiStyle.White,
			Highlight = () => Sel is HarvestProject h && h.data.AcceptedTypes.HasFlag(type),
			OnClick = (bool shift, bool ctrl) =>
			{
				if (!GameUiState.Paused && Sel is HarvestProject h)
				{
					User.LocalUser.GetComponent<UNetProjectCmd>().CallCmdHarvestToggleResource(h.gameObject, (int)type);
				}
			}
		};
	}

	// Coal deposits on their own (CoalHarvest), shown with Metallurgy on.
	private static UiButton Coal()
	{
		return new UiButton
		{
			Id = "harvestCoal",
			Visible = () => Plugin.IsEnabled(Features.Metallurgy) && CastleStoryPlus.Metallurgy.Metallurgy.CoalIcon != null,
			Label = () => "Coal",
			Icon = () => GameUiState.Icon(CastleStoryPlus.Metallurgy.Metallurgy.CoalIcon),
			IconColor = () => LuiStyle.White,
			Highlight = () => Sel is HarvestProject h && CoalHarvest.Accepts((int)h.data.AcceptedTypes),
			OnClick = (bool shift, bool ctrl) =>
			{
				if (!GameUiState.Paused && Sel is HarvestProject h)
				{
					User.LocalUser.GetComponent<UNetProjectCmd>().CallCmdHarvestSetResource(h.gameObject, CoalHarvest.Toggled((int)h.data.AcceptedTypes));
				}
			}
		};
	}

	// Ok, apply, cancel or delete: shown when the task offers it, coloured when it can be done now.
	private static UiButton Status(string id, string label, IconKey icon, Color color, Project.CommitStatusAction action)
	{
		return new UiButton
		{
			Id = id,
			Visible = () => GameUiState.HasSelected && Show(Sel, action),
			Label = () => GameUiState.Text(label),
			Icon = () => GameUiState.Icon(icon),
			IconColor = () => (GameUiState.HasSelected && Has(Sel, action) && !(action == Project.CommitStatusAction.Delete && Brix.Game.Compounders.BlueprintCompounder.instantiatingCompound)) ? color : LuiStyle.Gray,
			OnClick = (bool shift, bool ctrl) =>
			{
				if (GameUiState.Paused || !GameUiState.HasSelected)
				{
					return;
				}
				if (action == Project.CommitStatusAction.Delete && (GameUiState.IsSelectedIdle || Brix.Game.Compounders.BlueprintCompounder.instantiatingCompound))
				{
					return;
				}
				Set(Sel, action);
			}
		};
	}

	private static bool Show(Project project, Project.CommitStatusAction action)
	{
		switch (action)
		{
			case Project.CommitStatusAction.Ok:
				return Project.ShowOk(project);
			case Project.CommitStatusAction.Apply:
				return Project.ShowApply(project);
			case Project.CommitStatusAction.Cancel:
				return Project.ShowCancel(project);
			default:
				return Project.ShowDelete(project);
		}
	}

	private static bool Has(Project project, Project.CommitStatusAction action)
	{
		switch (action)
		{
			case Project.CommitStatusAction.Ok:
				return Project.HasOk(project);
			case Project.CommitStatusAction.Apply:
				return Project.HasApply(project);
			case Project.CommitStatusAction.Cancel:
				return Project.HasCancel(project);
			default:
				return Project.HasDelete(project);
		}
	}

	private static void Set(Project project, Project.CommitStatusAction action)
	{
		switch (action)
		{
			case Project.CommitStatusAction.Ok:
				Project.SetOk(project);
				break;
			case Project.CommitStatusAction.Apply:
				Project.SetApply(project);
				break;
			case Project.CommitStatusAction.Cancel:
				Project.SetCancel(project);
				break;
			default:
				Project.SetDelete(project);
				break;
		}
	}

	// ---- The selected workshop ----

	private const int RecipeSlots = 8;

	private const int QueueSlots = 8;

	// The workshop's popup, top to bottom: its name and worker, what it is making now (the needs it holds, a bar for
	// the work done) and the rest of the queue, the recipes as rows (their cost green while the stockpiles hold
	// enough, else red), then the workshop's own settings (WorkshopSettings: the mod's loop limit and priority).
	private static void BuildWorkshop(UiPanel popup)
	{
		UiSection title = popup.Section("operableTitle", null);
		title.Visible = () => GameUiState.Station != null;
		title.Add(new UiText
		{
			Id = "operableName",
			Text = () => GameTaskList.WorkshopName(GameUiState.Station) + "  <size=11><color=" + UiTheme.Hex(UiTheme.TextMuted) + ">" + Workers() + "</color></size>",
			Color = () => LuiStyle.CastleYellow,
			FontSize = LuiStyle.TitleFontSize,
			Title = true
		});

		UiSection now = popup.Section("operableNow", null);
		now.Visible = () => GameUiState.Station != null;
		now.Add(new UiStatus
		{
			Id = "operableNow",
			Icon = () =>
			{
				LuaCrafting.LuaRecipe active = GamePopupMenu.ActiveRecipe();
				return (active != LuaCrafting.LuaRecipe.None) ? CraftingCatalog.RecipeIcon(active) : GameUiState.Icon(GameUiState.Station.Icon);
			},
			Caption = () => (GamePopupMenu.ActiveRecipe() != LuaCrafting.LuaRecipe.None) ? "Making now" : "Waiting",
			Title = () =>
			{
				LuaCrafting.LuaRecipe active = GamePopupMenu.ActiveRecipe();
				return (active != LuaCrafting.LuaRecipe.None) ? CraftingCatalog.RecipeName(active) : "Nothing queued";
			},
			Detail = NowDetail,
			Progress = Progress,
			Tooltip = () => (GamePopupMenu.ActiveRecipe() != LuaCrafting.LuaRecipe.None) ? "Click to take it out of the queue" : "Pick a recipe below",
			OnClick = () => RemoveAt(0)
		});

		UiSection queue = popup.Section("operableQueue", () => "Next  (" + GameUiState.Station.QueueCount + " queued)");
		queue.Visible = () => GameUiState.Station != null && GameUiState.Station.QueueCount > 0;
		queue.CellSize = 40f;
		for (int i = 1; i < QueueSlots; i++)
		{
			int slot = i;
			Func<LuaCrafting.LuaRecipe?> queued = () =>
			{
				List<LuaCrafting.LuaRecipe> list = CraftingCatalog.Queue(GameUiState.Station);
				return (slot < list.Count) ? list[slot] : (LuaCrafting.LuaRecipe?)null;
			};
			queue.Add(new UiButton
			{
				Id = "operableQueue" + (slot + 1),
				Visible = () => queued() != null,
				Label = () => CraftingCatalog.RecipeName(queued().Value),
				Icon = () => CraftingCatalog.RecipeIcon(queued().Value),
				IconColor = () => LuiStyle.White,
				Tooltip = () => CraftingCatalog.RecipeName(queued().Value) + ": click to take it out of the queue",
				OnClick = (bool shift, bool ctrl) => RemoveAt(slot)
			});
		}
		queue.Add(new UiButton
		{
			Id = "toggleLoop",
			Label = () => GameUiState.Text("##operable_queue_toggle_looping"),
			Icon = () => GameUiState.Icon(IconKeys._UI_Wheel),
			IconColor = () => (GameUiState.Station != null && GameUiState.Station.Looping) ? LuiStyle.CastleYellow : LuiStyle.Gray,
			Hotkey = () => GameUiState.KeyOf("Projects", "project_ToggleLoop"),
			OnClick = (bool shift, bool ctrl) => GamePopupMenu.ToggleLoop()
		});
		queue.Add(new UiButton
		{
			Id = "clearQueue",
			Label = () => GameUiState.Text("##operable_queue_clear"),
			Icon = () => GameUiState.Icon(IconKeys._UI_X),
			IconColor = () => LuiStyle.CastleYellow,
			Hotkey = () => GameUiState.KeyOf("Projects", "project_ClearQueue"),
			OnClick = (bool shift, bool ctrl) => GamePopupMenu.ClearQueue()
		});

		UiSection recipes = popup.Section("operableRecipes", () => GameUiState.Text("##operable_craft"));
		recipes.Visible = () => GameUiState.Station != null;
		recipes.Layout = UiLayout.Rows;
		for (int i = 0; i < RecipeSlots; i++)
		{
			int slot = i;
			Func<LuaCrafting.LuaRecipe?> recipe = () =>
			{
				List<LuaCrafting.LuaRecipe> list = CraftingCatalog.RecipesOf(GameUiState.Station);
				return (list != null && slot < list.Count) ? list[slot] : (LuaCrafting.LuaRecipe?)null;
			};
			recipes.Add(new UiButton
			{
				Id = "operableRecipe" + (slot + 1),
				Visible = () => recipe() != null,
				Label = () => CraftingCatalog.RecipeName(recipe().Value),
				Detail = () => Cost(recipe().Value),
				Icon = () => CraftingCatalog.RecipeIcon(recipe().Value),
				IconColor = () => LuiStyle.White,
				Highlight = () => recipe().Value == GamePopupMenu.ActiveRecipe(),
				Hotkey = () => (slot < 5) ? GameUiState.KeyOf("Projects", "project_QueueSlot" + (slot + 1)) : string.Empty,
				OnClick = (bool shift, bool ctrl) =>
				{
					LuaCrafting.LuaRecipe? r = recipe();
					if (r != null)
					{
						GamePopupMenu.AddToQueue(r.Value);
					}
				}
			});
		}
	}

	// "1 of 1 worker".
	private static string Workers()
	{
		CraftingStation station = GameUiState.Station;
		if (station == null)
		{
			return string.Empty;
		}
		int most = station.MaximumCrewSize;
		return station.CrewCount() + " of " + most + ((most == 1) ? " worker" : " workers");
	}

	// The needs of the recipe being made as the workshop holds them: "Plant 11/10", green once there are enough.
	private static string NowDetail()
	{
		LuaCrafting.LuaRecipe active = GamePopupMenu.ActiveRecipe();
		if (active == LuaCrafting.LuaRecipe.None)
		{
			return string.Empty;
		}
		Dictionary<LuaCrafting.LuaResource, int> held = CraftingCatalog.Content(GameUiState.Station);
		List<string> parts = new List<string>();
		foreach (KeyValuePair<LuaCrafting.LuaResource, int> need in CraftingCatalog.Inputs(active))
		{
			LuaCrafting.LuaResource shown = GamePopupMenu.Shown(need.Key);
			held.TryGetValue(shown, out int have);
			Color color = (have >= need.Value) ? UiTheme.Good : UiTheme.TextMuted;
			parts.Add("<color=" + UiTheme.Hex(color) + ">" + CraftingCatalog.ResourceName(shown) + " " + have + "/" + need.Value + "</color>");
		}
		return string.Join("   ", parts.ToArray());
	}

	// How far the recipe being made is: its work and heat done of all it takes; empty while it is queued but not
	// started, -1 when nothing is queued.
	private static float Progress()
	{
		if (GamePopupMenu.ActiveRecipe() == LuaCrafting.LuaRecipe.None)
		{
			return -1f;
		}
		CraftingStation station = GameUiState.Station;
		Recipe current = (station != null && station.craftingLabor != null) ? station.CurrentRecipe : null;
		if (current == null || current.Info == null)
		{
			return 0f;
		}
		int total = current.Info.work + current.Info.heat;
		if (total <= 0)
		{
			return 0f;
		}
		int left = Mathf.Max(0, current.work) + Mathf.Max(0, current.heat);
		return 1f - (float)left / total;
	}

	// A recipe's cost: "2 Plant (71)" per input, green while the stockpiles hold enough, else red.
	private static string Cost(LuaCrafting.LuaRecipe recipe)
	{
		Dictionary<LuaCrafting.LuaResource, int> stored = StoredNow();
		List<string> parts = new List<string>();
		foreach (KeyValuePair<LuaCrafting.LuaResource, int> need in CraftingCatalog.Inputs(recipe))
		{
			LuaCrafting.LuaResource shown = GamePopupMenu.Shown(need.Key);
			stored.TryGetValue(shown, out int have);
			Color color = (have >= need.Value) ? UiTheme.Good : LuiStyle.CastleRed;
			parts.Add("<color=" + UiTheme.Hex(color) + ">" + need.Value + " " + CraftingCatalog.ResourceName(shown) + "</color> <color=" + UiTheme.Hex(UiTheme.TextDim) + ">(" + have + ")</color>");
		}
		return string.Join("   ", parts.ToArray());
	}

	private static Dictionary<LuaCrafting.LuaResource, int> _stored;

	private static float _storedAt = -1f;

	// The stockpiles' content, read at most twice a second (every recipe row asks).
	private static Dictionary<LuaCrafting.LuaResource, int> StoredNow()
	{
		if (_stored == null || Time.unscaledTime - _storedAt > 0.5f || Time.unscaledTime < _storedAt)
		{
			_stored = CraftingCatalog.Stored();
			_storedAt = Time.unscaledTime;
		}
		return _stored;
	}

	private static void RemoveAt(int slot)
	{
		CraftingStation station = GameUiState.Station;
		if (station != null && slot < station.QueueCount)
		{
			CraftingStation.localUserProject.CallCmdcraftingRemoveAt(station.gameObject, slot);
		}
	}

	// What a need slot shows in place of the resource the recipe lists (CoalFuel: coal as the furnace's fuel); the

	// ---- The idle group ----

	private static void BuildIdle(UiPanel popup)
	{
		UiSection population = popup.Section("idleTaskPopulation", () => GameUiState.Text("##task_idle_population"));
		population.Visible = () => GameUiState.IsSelectedIdle && GameUiState.Station == null;
		population.Add(new UiButton
		{
			Id = "idleTaskPopulationCount",
			Label = () => GameUiState.Text("##task_idle_population"),
			Icon = () => GameUiState.Icon(IconKeys._Bricktron),
			IconColor = () => LuiStyle.White,
			Count = () => GameActions.CountBricktrons() + "/" + Brix.Game.Rules.GeneralRules.Main.bricktronCap
		});
		population.Add(new UiButton
		{
			Id = "idleTaskActionsMortuary",
			Label = () => GameUiState.Text("##task_idle_dead"),
			Icon = () => GameUiState.Icon(IconKeys.Skull),
			IconColor = () => (FireflyInfo.AutoRespawn() > 0) ? LuiStyle.White : LuiStyle.Gray,
			Count = () => FireflyInfo.AutoRespawn().ToString()
		});
		UiSection actions = popup.Section("idleTaskActions", () => GameUiState.Text("##task_idle_crystal"));
		actions.Visible = () => GameUiState.IsSelectedIdle && GameUiState.Station == null;
		actions.Add(new UiProgress
		{
			Id = "idleTaskActionsFireflyProgress",
			Value = FireflyInfo.Progress,
			Text = () => GameUiState.Text("##task_idle_energy") + " " + FireflyInfo.ProgressText()
		});
		actions.Add(new UiButton
		{
			Id = "idleTaskActionsSpawnUnit",
			Label = () => GameUiState.Text("##task_idle_spawn"),
			Icon = () => GameUiState.Icon(IconKeys._UI_Bricktron),
			IconColor = () => (FireflyInfo.Spawnable() > 0) ? LuiStyle.CastleYellow : LuiStyle.Gray,
			Count = () => FireflyInfo.Spawnable().ToString(),
			OnClick = (bool shift, bool ctrl) =>
			{
				if (FireflyInfo.Spawnable() > 0)
				{
					FireflyInfo.SpawnUnit();
				}
			}
		});
		// A new bricktron whenever the crystal has the energy for one (Workers.AutoSpawn).
		actions.Add(new UiToggle
		{
			Id = "idleTaskActionsAutoSpawn",
			Label = () => "Auto spawn bricktrons",
			Value = () => CastleStoryPlus.Workers.AutoSpawn.On,
			Set = (bool on) => CastleStoryPlus.Workers.AutoSpawn.On = on,
			// The popup is 320 wide with 10 of padding each side.
			Width = 300f
		});
	}
}
