using System.Collections.Generic;
using Brix.Game.Components;
using Brix.Game.Semantique;
using Brix.Lua;
using Brix.UI.Icons;
using CastleStoryPlus.Core;
using CastleStoryPlus.GameUi;
using CastleStoryPlus.LuaUi;

namespace CastleStoryPlus.Economy;

// Copying a workshop's setup to another: "Copy setup" in a workshop's menu takes its queue, whether it loops and its
// loop-until-stock limits (CraftLoopLimit) and its fetch range (WorkshopFetchRange); "Paste setup" in another workshop's menu replaces that one's queue with
// them. Only the recipes the other workshop can make are pasted, so a forge's setup goes to another forge (and a
// kind of workshop with none of the recipes cannot take it). Everything goes through the game's own queue commands
// and the limits' own command, so it works on multiplayer clients too.
[Feature(Features.WorkshopSetup, Features.WorkshopSetupInfo)]
internal static class WorkshopSetup
{
	private sealed class Setup
	{
		public readonly List<int> Queue = new List<int>();

		public bool Looping;

		public Dictionary<int, int> Limits;

		public CraftingStation From;

		public string Name;

		// The workshop's own fetch range (WorkshopFetchRange), -1: the default; null when that feature is off.
		public int? FetchRange;
	}

	private static Setup _copied;

	private static void Enable()
	{
		GameMenuItems.Extend(AddToMenu);
	}

	private static void AddToMenu(GameMenuModel menu)
	{
		UiSection section = menu.Popup.SectionAfter("operableQueue", "operableSetup", () => "Setup");
		section.Visible = () => GameUiState.Station != null;
		section.CellSize = 40f;
		section.Add(new UiButton
		{
			Id = "copySetup",
			Label = () => "Copy setup",
			Icon = () => GameUiState.Icon(IconKeys._UI_CopyBlueprints),
			IconColor = () => LuiStyle.CastleYellow,
			Tooltip = () => "Copy setup: remember this workshop's queue, looping, stock limits and fetch range, to paste them into another workshop",
			OnClick = (bool shift, bool ctrl) => Copy(GameUiState.Station)
		});
		section.Add(new UiButton
		{
			Id = "pasteSetup",
			Label = () => "Paste setup",
			Icon = () => GameUiState.Icon(IconKeys._UI_PasteBlueprints),
			IconColor = () => CanPaste(GameUiState.Station) ? LuiStyle.CastleYellow : LuiStyle.Gray,
			Enabled = () => CanPaste(GameUiState.Station),
			Tooltip = () => PasteTooltip(GameUiState.Station),
			OnClick = (bool shift, bool ctrl) => Paste(GameUiState.Station)
		});
	}

	private static void Copy(CraftingStation station)
	{
		if (station == null || station.craftingLabor == null)
		{
			return;
		}
		Setup setup = new Setup { Looping = station.Looping, From = station, Name = station.name };
		foreach (RecipeInfo info in station.QueuedRecipes)
		{
			setup.Queue.Add(info.id);
		}
		Dictionary<int, int> limits = Plugin.IsEnabled(Features.CraftLoopLimit) ? CraftLoopLimit.All(station.state) : null;
		setup.Limits = (limits != null) ? new Dictionary<int, int>(limits) : null;
		setup.FetchRange = Plugin.IsEnabled(Features.WorkshopFetchRange) ? WorkshopFetchRange.Raw(station.state) : (int?)null;
		_copied = setup;
	}

	// The game's recipe ids a workshop can make.
	private static HashSet<int> RecipeIds(CraftingStation station)
	{
		HashSet<int> ids = new HashSet<int>();
		List<LuaCrafting.LuaRecipe> recipes = CraftingCatalog.RecipesOf(station);
		if (recipes == null)
		{
			return ids;
		}
		CraftingCatalog.EnsureMapped();
		foreach (LuaCrafting.LuaRecipe recipe in recipes)
		{
			List<RecipeInfo> infos = LuaCrafting.ToRecipeInfo(recipe);
			if (infos == null)
			{
				continue;
			}
			foreach (RecipeInfo info in infos)
			{
				ids.Add(info.id);
			}
		}
		return ids;
	}

	// How many of the copied queue's recipes this workshop can make.
	private static int Fitting(CraftingStation station)
	{
		if (_copied == null || station == null || station.craftingLabor == null)
		{
			return 0;
		}
		HashSet<int> ids = RecipeIds(station);
		int count = 0;
		foreach (int id in _copied.Queue)
		{
			if (ids.Contains(id))
			{
				count++;
			}
		}
		return count;
	}

	private static bool CanPaste(CraftingStation station)
	{
		return _copied != null && station != null && station != _copied.From && Fitting(station) > 0;
	}

	private static string PasteTooltip(CraftingStation station)
	{
		if (_copied == null)
		{
			return "Paste setup: copy a workshop's setup first";
		}
		if (station == _copied.From)
		{
			return "Paste setup: this is the workshop the setup was copied from";
		}
		int fitting = Fitting(station);
		if (fitting == 0)
		{
			return "Paste setup: this workshop makes none of the copied recipes";
		}
		return "Paste setup: replace this workshop's queue with the copied one (" + fitting + " of " + _copied.Queue.Count + " queued recipes fit"
			+ (_copied.Looping ? ", looping" : string.Empty) + ")";
	}

	private static void Paste(CraftingStation station)
	{
		if (!CanPaste(station) || GameUiState.Paused)
		{
			return;
		}
		HashSet<int> ids = RecipeIds(station);
		// Clearing the queue also clears its limits; the commands are carried out in the order they are sent.
		CraftingStation.localUserProject.CallCmdcraftingDoNothing(station.gameObject);
		foreach (int id in _copied.Queue)
		{
			if (ids.Contains(id))
			{
				CraftingStation.localUserProject.CallCmdcraftingQueueRecipe(station.gameObject, id, 1);
			}
		}
		if (station.Looping != _copied.Looping)
		{
			CraftingStation.localUserProject.CallCmdcraftingLoop(station.gameObject, _copied.Looping);
		}
		if (_copied.FetchRange.HasValue && Plugin.IsEnabled(Features.WorkshopFetchRange))
		{
			WorkshopFetchRange.Change(station.state, _copied.FetchRange.Value);
		}
		if (_copied.Limits == null || station.state == null)
		{
			return;
		}
		foreach (KeyValuePair<int, int> limit in _copied.Limits)
		{
			if (limit.Key == CraftLoopLimit.AllRecipes || ids.Contains(limit.Key))
			{
				CraftLoopLimit.Change(station.state, limit.Key, limit.Value);
			}
		}
	}
}
