using System.Collections.Generic;
using Brix.Game.Components;
using Brix.Game.Utils;
using Brix.Transactions;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryPlus.Market;

// Server: a finished trade produces what was paid for at the current prices (its lot, at least one).
[Feature(Features.Market, Features.MarketInfo)]
[HarmonyPatch(typeof(Recipe), nameof(Recipe.SpawnAll))]
internal static class MarketTradePatch
{
	private static bool Prefix(Recipe __instance, ref List<GameObject> gos, Vector3 pos)
	{
		MarketPrices.Trade trade = MarketPrices.TradeFor(__instance.Info);
		if (trade == null)
		{
			return true;
		}
		int given = __instance.ingredients.Value(trade.Give.Resource);
		if (given <= 0)
		{
			given = trade.GiveAmount;
		}
		int output = MarketPrices.OutputFor(trade, given);
		MarketPrices.Traded(trade, given, output);
		Spawn(gos, trade.Get, output, pos);
		return false;
	}

	private static void Spawn(List<GameObject> gos, MarketPrices.Good good, int count, Vector3 pos)
	{
		for (int i = 0; i < count; i++)
		{
			gos.Add(Transactor.SpawnUNet(good.Asset, null, pos, Quaternion.identity));
		}
	}
}

// The game's crafting menu (Lua) only knows its own stations; for a market it shows nothing and the market
// window (MarketPanel) is used instead.
[Feature(Features.Market, Features.MarketInfo)]
[HarmonyPatch(typeof(CraftingStation), nameof(CraftingStation.SelectedStation))]
internal static class MarketHideFromCraftingMenuPatch
{
	private static void Postfix(ref CraftingStation __result)
	{
		if (__result is MarketStation)
		{
			__result = null;
		}
	}
}

// The market is mapped to its building as soon as it is created (MarketBuilding); when the game maps all
// blueprints afterwards it must skip it, or the duplicate key stops the game's factory loading.
[Feature(Features.Market, Features.MarketInfo)]
[HarmonyPatch(typeof(BlueprintMapper), nameof(BlueprintMapper.AddMappings))]
internal static class MarketBlueprintMappingPatch
{
	private static bool Prefix(BlueprintMapper __instance, Blueprint bp)
	{
		return !__instance.blueprint2Concrete.ContainsKey(bp.AssetKey);
	}
}

// The trades with coal and steel, after the metallurgy recipes and the research station's (made first here if
// their own postfixes have not run yet: the order of postfixes is not guaranteed).
[Feature(Features.Market, Features.MarketInfo)]
[HarmonyPatch(typeof(Brix.Lua.LuaCrafting), nameof(Brix.Lua.LuaCrafting.Register))]
[HarmonyPriority(Priority.VeryLow)]
internal static class MarketLateTradesPatch
{
	private static void Postfix()
	{
		Metallurgy.MetallurgyLuaPatch.Run();
		Upgrades.UpgradeLines.RegisterRecipes(Upgrades.UpgradeHall.Research);
		MarketPrices.RegisterLateTrades();
	}
}
