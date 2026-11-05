using System.Collections.Generic;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Game.Semantique;
using Brix.Lua;
using CastleStoryPlus.Core;
using HarmonyLib;
using MoonSharp.Interpreter;
using UnityEngine;

namespace CastleStoryPlus.Metallurgy;

// Coal heats the furnace as well as orange crystal does: one piece of either for each unit of heat a recipe needs
// (iron, glass, charcoal). Coal goes first: the furnace burns the coal it holds before any crystal, and a worker
// fetching fuel for it brings coal when it can find some, else an orange crystal as before. Coal in the furnace
// stays there as fuel, as orange crystals do; only while it burns charcoal (coal is then its product) is all but a
// few pieces carried to the stockpiles. The workshop menu's ingredient row shows the fuel the furnace will burn:
// coal while it holds coal, or holds no fuel and the stockpiles have coal; else orange crystal.
[Feature(Features.Metallurgy, Features.MetallurgyInfo)]
internal static class CoalFuel
{
	// Coal a furnace burning charcoal keeps for heat; the rest of its charcoal is carried away as usual.
	internal const int Reserve = 2;

	// Inventories whose recipes make coal (charcoal): there coal is a product, not only fuel. Small: one entry per
	// furnace on a charcoal recipe, removed when it gets other recipes.
	internal static readonly HashSet<CraftingInventory> MakesCoal = new HashSet<CraftingInventory>();

	// Per station (instance id): whether the worker deciding what to fetch could find coal. Set just before the
	// station is asked what to fetch (CraftingStation.CanFetch), read when it answers (StuffToFetch).
	internal static readonly Dictionary<int, bool> CoalFound = new Dictionary<int, bool>();

	private const string InputSlot = "local GetResourceAtSlotIndex = function(i) return Data.Operable:HasSelected() and Data.Operable:HasActiveRecipe() and Data.Recipe:GetInputResourceAtSlotIndex(Data.Operable:GetActiveRecipe(), i) or Resource.None end\n";

	private const string InputSlotLook = "h.TooltipPath = ||Data.Resource:GetTooltipPath(GetResourceAtSlotIndex(i))\n"
		+ "h.Count = ||string.format(\"{0}/{1}\", Data.Operable:GetContentResourceCount(Data.Operable:GetSelected(), GetResourceAtSlotIndex(i)), Data.Recipe:GetInputResourceCount(Data.Operable:GetActiveRecipe(), GetResourceAtSlotIndex(i)))\n"
		+ "h.CountColor = ||CastleYellow\n"
		+ "h.Label = ||Data.Resource:GetName(GetResourceAtSlotIndex(i))\n"
		+ "h.LabelColor = ||CastleYellow\n"
		+ "h.Icon = ||Data.Resource:GetIcon(GetResourceAtSlotIndex(i))\n";

	private static void Enable()
	{
		LuaInjection.AddFunction("CoalInStock", (ScriptExecutionContext context, CallbackArguments args) =>
		{
			Faction faction = (User.LocalUser != null) ? User.LocalUser.faction : null;
			return DynValue.NewBoolean(faction != null && Economy.CraftLoopLimit.Stock(faction, Metallurgy.Coal, cached: true) > 0);
		});
		GameUi.GamePopupMenu.ShownInput = ShownFuel;
		// The fuel slot (the recipe's heat, listed as orange crystal) shows coal when the furnace will burn coal; the
		// count asked stays the recipe's heat.
		LuaInjection.AddPatch(Features.Metallurgy, "LUI/Menus/GameMenu.lua", InputSlot, LuaInjection.Mode.InsertAfter,
			"local GetShownResourceAtSlotIndex = function(i)\n"
			+ "local r = GetResourceAtSlotIndex(i)\n"
			+ "if r == Resource.OrangeCrystal and Resource.Coal and Data.Operable:HasSelected() then\n"
			+ "local selected = Data.Operable:GetSelected()\n"
			+ "if Data.Operable:GetContentResourceCount(selected, Resource.Coal) > 0 or (Data.Operable:GetContentResourceCount(selected, Resource.OrangeCrystal) == 0 and CastleStoryPlus.CoalInStock()) then return Resource.Coal end\n"
			+ "end\n"
			+ "return r\n"
			+ "end\n");
		LuaInjection.AddPatch(Features.Metallurgy, "LUI/Menus/GameMenu.lua", InputSlotLook, LuaInjection.Mode.Replace,
			"h.TooltipPath = ||Data.Resource:GetTooltipPath(GetShownResourceAtSlotIndex(i))\n"
			+ "h.Count = ||string.format(\"{0}/{1}\", Data.Operable:GetContentResourceCount(Data.Operable:GetSelected(), GetShownResourceAtSlotIndex(i)), Data.Recipe:GetInputResourceCount(Data.Operable:GetActiveRecipe(), GetResourceAtSlotIndex(i)))\n"
			+ "h.CountColor = ||CastleYellow\n"
			+ "h.Label = ||Data.Resource:GetName(GetShownResourceAtSlotIndex(i))\n"
			+ "h.LabelColor = ||CastleYellow\n"
			+ "h.Icon = ||Data.Resource:GetIcon(GetShownResourceAtSlotIndex(i))\n");
	}

	// The C# in-game menu's fuel slot: coal while the furnace holds coal, or holds no fuel and the stockpiles have coal.
	private static LuaCrafting.LuaResource ShownFuel(CraftingStation station, LuaCrafting.LuaResource resource)
	{
		if (resource != LuaCrafting.LuaResource.OrangeCrystal)
		{
			return resource;
		}
		Dictionary<LuaCrafting.LuaResource, int> content = GameUi.CraftingCatalog.Content(station);
		content.TryGetValue(Metallurgy.LuaCoal, out int coal);
		content.TryGetValue(LuaCrafting.LuaResource.OrangeCrystal, out int crystal);
		Faction faction = (User.LocalUser != null) ? User.LocalUser.faction : null;
		bool inStock = faction != null && Economy.CraftLoopLimit.Stock(faction, Metallurgy.Coal, cached: true) > 0;
		return (coal > 0 || (crystal == 0 && inStock)) ? Metallurgy.LuaCoal : resource;
	}

	internal static bool NeedsFuel(CraftingStation station)
	{
		Recipe recipe = station.CurrentRecipe;
		return recipe != null && recipe.NeedsHeat && !station.recepteur.Carries(Metallurgy.Coal) && !station.recepteur.Carries(Adjectif.orangeCrystal);
	}
}

[Feature(Features.Metallurgy, Features.MetallurgyInfo)]
[HarmonyPatch(typeof(CraftingStation), nameof(CraftingStation.CanFetch))]
internal static class CoalFuelFindPatch
{
	private static void Prefix(CraftingStation __instance, Labor labor)
	{
		if (CoalFuel.NeedsFuel(__instance))
		{
			CoalFuel.CoalFound[__instance.InstanceId()] = labor.Find(Metallurgy.Coal.Profile.description) != null;
		}
	}
}

// Fuel to fetch: coal when the worker found some; nothing while the furnace still holds coal.
[Feature(Features.Metallurgy, Features.MetallurgyInfo)]
[HarmonyPatch(typeof(CraftingStation), nameof(CraftingStation.StuffToFetch))]
internal static class CoalFuelFetchPatch
{
	private static void Postfix(CraftingStation __instance, ref Description __result)
	{
		if (__result == null || !ReferenceEquals(__result, Adjectif.orangeCrystal.Profile.description))
		{
			return;
		}
		if (__instance.recepteur.Carries(Metallurgy.Coal))
		{
			__result = __instance.inventory.RequiresMoreRessources() ? __instance.inventory.StuffToFecth() : null;
			return;
		}
		if (CoalFuel.CoalFound.TryGetValue(__instance.InstanceId(), out bool found) && found)
		{
			__result = Metallurgy.Coal.Profile.description;
		}
	}
}

// Each unit of heat burns a piece of coal when the furnace holds one, else an orange crystal.
[Feature(Features.Metallurgy, Features.MetallurgyInfo)]
[HarmonyPatch(typeof(Recipe), nameof(Recipe.HeatFrom))]
internal static class CoalFuelBurnPatch
{
	private static bool Prefix(Recipe __instance, Recepteur recepteur, ref bool __result)
	{
		if (!__instance.ConsumeFrom(recepteur, Metallurgy.Coal))
		{
			return true;
		}
		__instance.heat--;
		__result = true;
		return false;
	}
}

[Feature(Features.Metallurgy, Features.MetallurgyInfo)]
[HarmonyPatch(typeof(CraftingInventory), nameof(CraftingInventory.SetRecipes))]
internal static class CoalFuelRecipesPatch
{
	private static void Prefix(CraftingInventory __instance, List<RecipeInfo> recipes)
	{
		bool makesCoal = false;
		foreach (RecipeInfo recipe in recipes)
		{
			if (recipe != null && recipe.Creates == Metallurgy.CoalKey)
			{
				makesCoal = true;
			}
		}
		if (makesCoal)
		{
			CoalFuel.MakesCoal.Add(__instance);
		}
		else
		{
			CoalFuel.MakesCoal.Remove(__instance);
		}
	}
}

// A station with heat recipes keeps its coal, as it keeps its orange crystals: that coal is not carried to the
// stockpiles (a worker fetching fuel brings a whole stack, which was carried straight back out). While it makes
// charcoal it keeps CoalFuel.Reserve of it.
[Feature(Features.Metallurgy, Features.MetallurgyInfo)]
[HarmonyPatch(typeof(CraftingInventory), nameof(CraftingInventory.RefreshAmounts))]
internal static class CoalFuelKeepPatch
{
	private static void Postfix(CraftingInventory __instance)
	{
		if (!__instance.needsHeat)
		{
			return;
		}
		int stored = __instance.toStore.Value(Metallurgy.Coal);
		if (stored <= 0)
		{
			return;
		}
		int kept = CoalFuel.MakesCoal.Contains(__instance) ? System.Math.Min(stored, CoalFuel.Reserve) : stored;
		__instance.toStore.Set(Metallurgy.Coal, stored - kept);
		__instance.toStore.RemoveZeros();
		// As the game works them out from toStore.
		__instance.resources.Clear();
		__instance.resources.AddAllFrom(__instance.content);
		if (__instance.totalRequiredIngredients.Any())
		{
			__instance.resources.ExceptWith(__instance.toStore).RemoveZeros().RessourceOnly();
		}
		__instance.uniqueStorable.Clear();
		__instance.uniqueResources.Clear();
		__instance.toStore.UniqueRepresentatives(ref __instance.uniqueStorable);
		__instance.resources.UniqueRepresentatives(ref __instance.uniqueResources);
	}
}

// A worker carrying charcoal out of the furnace leaves CoalFuel.Reserve pieces behind. The game's pickup takes every
// piece of the type (upTo -1), so it emptied the furnace of its fuel too: the furnace then sent for coal from the
// stockpiles, which was carried straight back out as product, over and over.
[Feature(Features.Metallurgy, Features.MetallurgyInfo)]
[HarmonyPatch(typeof(Transaction), "TransferSpecificObjects")]
internal static class CoalFuelReservePatch
{
	private static void Prefix(Recepteur source, GameObject specificItem, ref int upTo)
	{
		if (source == null || specificItem == null || CoalFuel.MakesCoal.Count == 0)
		{
			return;
		}
		GameComponent item = specificItem.GetComponent<GameComponent>();
		if (item == null || item.AssetKey != Metallurgy.CoalKey || !Burns(source))
		{
			return;
		}
		int coal = 0;
		foreach (IDescriptor stored in source.StoredItems)
		{
			if (stored != null && stored.AssetKey == Metallurgy.CoalKey)
			{
				coal++;
			}
		}
		int free = System.Math.Max(0, coal - CoalFuel.Reserve);
		if (upTo == -1 || upTo > free)
		{
			upTo = free;
		}
	}

	// Whether the recepteur is a furnace making charcoal.
	private static bool Burns(Recepteur source)
	{
		foreach (CraftingInventory inventory in CoalFuel.MakesCoal)
		{
			if (inventory != null && inventory.recepteur == source)
			{
				return inventory.needsHeat;
			}
		}
		return false;
	}
}
