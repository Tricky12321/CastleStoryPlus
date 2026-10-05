using System.Collections.Generic;
using Brix.External.Factories;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Game.Semantique;
using Brix.Game.Utils;
using CastleStoryPlus.Building;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.Upgrades;

// Adds the smithy (weapon upgrades) and the armoury (armour upgrades) to the game's factories when they load (see
// CustomBuilding), both 4 x 3 blocks and 5 high, in the build menu's crafting group. The game already has a
// building called Forge, so the weapon building is the smithy.
[Feature(Features.Upgrades, Features.UpgradesInfo)]
[HarmonyPatch(typeof(Factory), nameof(Factory.Awake))]
internal static class UpgradeBuildings
{
	internal const string SmithyName = "Smithy";

	internal const string ArmouryName = "Armoury";

	private static readonly CustomBuilding Smithy = new CustomBuilding(SmithyName, 4, 3, 5)
	{
		BuildModel = SmithyModel.Build,
		BuildCost = (Description cost) =>
		{
			cost.Add(Adjectif.New(Adjectif.stoneBlock.GetType(), 10));
			cost.Add(Adjectif.New(Adjectif.plankBlock.GetType(), 6));
			cost.Add(Adjectif.New(Adjectif.iron.GetType(), 2));
		},
		SetupBuilding = (GameObject go) => Setup<SmithyStation>(go, UpgradeHall.Smithy, SmithyName),
		// Back wall; hearth, the smith's place, anvil; the open front with the quench barrel on the right.
		Layout = new string[3] { "####", "#O#A", "AAA#" }
	};

	private static readonly CustomBuilding Armoury = new CustomBuilding(ArmouryName, 4, 3, 4)
	{
		BuildModel = ArmouryModel.Build,
		BuildCost = (Description cost) =>
		{
			cost.Add(Adjectif.New(Adjectif.plankBlock.GetType(), 10));
			cost.Add(Adjectif.New(Adjectif.stoneBlock.GetType(), 4));
			cost.Add(Adjectif.New(Adjectif.fabric.GetType(), 2));
		},
		SetupBuilding = (GameObject go) => Setup<ArmouryStation>(go, UpgradeHall.Armoury, ArmouryName),
		// Armour stands at the back, the weapon rack on the left, the quiver in the front right corner.
		Layout = new string[3] { "####", "#OAA", "#AA#" }
	};

	private static void Enable()
	{
		UI.BuildIcons.Register();
		LuaInjection.AddPatch(Features.Upgrades, "LUI/Meta/Meta_Structure.lua", "Hotkey = \"project_MachineShop\",\tgroupId = 3 })\n", LuaInjection.Mode.InsertAfter,
			"_t.Add(AssetKey.New(\"Blueprints\", \"" + SmithyName + "\"),\t\t\t\t{ Name = ||\"" + SmithyName + "\",\t\t\tIcon = " + UI.BuildIcons.Lua("smithy", "_Forge") + ",\t\tHotkey = \"\",\tgroupId = 3 })\n"
			+ "_t.Add(AssetKey.New(\"Blueprints\", \"" + ArmouryName + "\"),\t\t\t\t{ Name = ||\"" + ArmouryName + "\",\t\t\tIcon = " + UI.BuildIcons.Lua("armoury", "_Shield") + ",\t\tHotkey = \"\",\tgroupId = 3 })\n");
	}

	private static void Postfix()
	{
		Smithy.AddToFactories();
		Armoury.AddToFactories();
	}

	// The station, and room for the most of each material one research of the building needs.
	private static void Setup<T>(GameObject go, UpgradeHall hall, string name) where T : UpgradeStation
	{
		CustomBuilding.ReplaceStation<T>(go);
		CustomBuilding.SetStorage(go, name, UpgradeLines.Storage(hall));
	}
}

// Server: a finished research unlocks its tier for the building's faction instead of producing an item. The
// materials are used up as for any recipe.
[Feature(Features.Upgrades, Features.UpgradesInfo)]
[HarmonyPatch(typeof(Recipe), nameof(Recipe.Finish))]
internal static class UpgradeResearchPatch
{
	private static bool Prefix(Recipe __instance, Recepteur recepteur)
	{
		if (!UpgradeLines.TryGetResearch(__instance.Info, out UpgradeLine line, out int tier))
		{
			return true;
		}
		if (NetworkServer.active && recepteur != null)
		{
			__instance.ConsumeAllFrom(recepteur);
			__instance.finished = true;
			TeamUpgrades.Unlock(Faction.GetFaction(recepteur.gameObject), line.Index, tier);
		}
		return false;
	}
}

// The game's crafting menu (Lua) only knows its own stations; for the smithy and the armoury it shows nothing and
// the upgrade window (UpgradePanel) is used instead.
[Feature(Features.Upgrades, Features.UpgradesInfo)]
[HarmonyPatch(typeof(CraftingStation), nameof(CraftingStation.SelectedStation))]
internal static class UpgradeHideFromCraftingMenuPatch
{
	private static void Postfix(ref CraftingStation __result)
	{
		if (__result is UpgradeStation)
		{
			__result = null;
		}
	}
}

// The buildings are mapped to their blueprints as soon as they are created (CustomBuilding); when the game maps
// all blueprints afterwards it must skip them, or the duplicate key stops the game's factory loading.
[Feature(Features.Upgrades, Features.UpgradesInfo)]
[HarmonyPatch(typeof(BlueprintMapper), nameof(BlueprintMapper.AddMappings))]
internal static class UpgradeBlueprintMappingPatch
{
	private static bool Prefix(BlueprintMapper __instance, Blueprint bp)
	{
		return !__instance.blueprint2Concrete.ContainsKey(bp.AssetKey);
	}
}
