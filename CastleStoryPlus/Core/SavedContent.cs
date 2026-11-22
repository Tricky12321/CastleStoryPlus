#if !ESSENTIALS
using System;
#endif
using Brix.External.Factories;
using Brix.Game.Components;
using Brix.Game.Utils;
using HarmonyLib;

namespace CastleStoryPlus.Core;

// The mod's buildings, blocks and items that a save can hold are added to the game's factories whether or not their
// feature is switched on (#185). A save (and the network) knows an object only by its factory key: with the market,
// the warehouse, the custom blocks or coal switched off, their keys were missing, every saved object of them failed to
// load and was gone from the world at the next save, and a still-enabled feature that used them (roads and ladders are
// custom blocks) broke. A switched-off feature now only leaves the build menu and its own behaviour; what it put in the
// world still loads. Each part runs on its own, so one that fails does not keep the others out.
// The Essentials edition compiles none of this content (docs/Editions.md), so it has nothing to add here: a Full save's
// objects of it are skipped by the loader there (UnknownObjects, UnknownBlocks, FullSaveGuards).
#if !ESSENTIALS
[Feature]
[HarmonyPatch(typeof(Factory), nameof(Factory.Awake))]
internal static class SavedContent
{
	// Before the features' own Factory.Awake postfixes, which change these templates (looks, room for resources).
	[HarmonyPriority(Priority.First)]
	private static void Postfix()
	{
		Run("Metallurgy items", CastleStoryPlus.Metallurgy.Metallurgy.SetUpItems);
		Run("DarkCrystals item", CastleStoryPlus.Economy.DarkCrystals.SetUpFactories);
		Run("CustomBlocks", CastleStoryPlus.Building.CustomBlocks.AddToFactories);
		Run("LargeStockpile", CastleStoryPlus.Building.LargeStockpile.AddToFactories);
		Run("Warehouse", CastleStoryPlus.Building.Warehouse.AddToFactories);
		Run("Market", CastleStoryPlus.Market.MarketBuilding.AddToFactories);
		Run("Upgrades buildings", CastleStoryPlus.Upgrades.UpgradeBuildings.AddToFactories);
		Run("Saplings", CastleStoryPlus.Nature.Saplings.AddToFactories);
		Run("SaplingArea", CastleStoryPlus.Nature.SaplingArea.AddToFactories);
	}

	private static void Run(string what, Action setUp)
	{
		try
		{
			setUp();
		}
		catch (Exception ex)
		{
			Plugin.Log.LogError("SavedContent: " + what + " could not be added to the factories: " + ex);
		}
	}
}
#endif

// The mod's buildings are mapped to their blueprints as soon as they are made (CustomBuilding, LargeStockpile); when
// the game maps all blueprints afterwards it must skip them, or the duplicate key stops the game's factory loading.
// This guard came with the market, the large stockpile and the upgrades, so with those three switched off and another
// building on (the warehouse, saplings) nothing skipped them. Always on now, as the buildings are (SavedContent).
[Feature]
[HarmonyPatch(typeof(BlueprintMapper), nameof(BlueprintMapper.AddMappings))]
internal static class BlueprintMappingGuard
{
	private static bool Prefix(BlueprintMapper __instance, Blueprint bp)
	{
		return !__instance.blueprint2Concrete.ContainsKey(bp.AssetKey);
	}
}
