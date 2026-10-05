using System.Collections.Generic;
using Brix.External.Factories;
using Brix.Game.AI;
using CastleStoryPlus.Building;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryPlus.Market;

// Adds the market to the game's factories when they load (see CustomBuilding): 4 x 3 blocks and 4 high, with its
// own model, a market station instead of the workbench station, and a build cost. The blueprint is in the build
// menu's crafting group. Saved markets load through the same factory keys.
[Feature(Features.Market, Features.MarketInfo)]
[HarmonyPatch(typeof(Factory), nameof(Factory.Awake))]
internal static class MarketBuilding
{
	internal const string Name = "Market";

	private static readonly CustomBuilding Building = new CustomBuilding(Name, 4, 3, 4)
	{
		BuildModel = MarketModel.Build,
		BuildCost = SetBuildCost,
		SetupBuilding = Setup,
		// Shelves at the back, the counter at the front; the trader walks in from the sides and stands between them.
		Layout = new string[3] { "####", "AOOA", "####" }
	};

	private static void Enable()
	{
		UI.BuildIcons.Register();
		LuaInjection.AddPatch(Features.Market, "LUI/Meta/Meta_Structure.lua", "Hotkey = \"project_MachineShop\",\tgroupId = 3 })\n", LuaInjection.Mode.InsertAfter,
			"_t.Add(AssetKey.New(\"Blueprints\", \"Market\"),\t\t\t\t{ Name = ||\"Market\",\t\t\tIcon = " + UI.BuildIcons.Lua("market", "_Machine_Shop") + ",\t\tHotkey = \"\",\tgroupId = 3 })\n");
	}

	private static void Postfix()
	{
		Building.AddToFactories();
	}

	// Planks, bricks and fabric, brought by the workers like for any blueprint.
	private static void SetBuildCost(Description capacity)
	{
		capacity.Add(Adjectif.New(Adjectif.plankBlock.GetType(), 8));
		capacity.Add(Adjectif.New(Adjectif.stoneBlock.GetType(), 6));
		capacity.Add(Adjectif.New(Adjectif.fabric.GetType(), 2));
	}

	// A market station, holding up to MarketPrices.Capacity of every traded resource: what is given and what
	// comes out.
	private static void Setup(GameObject go)
	{
		CustomBuilding.ReplaceStation<MarketStation>(go);
		List<KeyValuePair<Ressource, int>> storage = new List<KeyValuePair<Ressource, int>>();
		foreach (MarketPrices.Good good in MarketPrices.Goods)
		{
			storage.Add(new KeyValuePair<Ressource, int>(good.Resource, MarketPrices.Capacity * 2));
		}
		CustomBuilding.SetStorage(go, Name, storage);
	}
}
