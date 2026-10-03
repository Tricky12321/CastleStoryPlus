using Brix.Game.AI;
using Brix.Game.Semantique;
using CastleStoryPlus.Core;
using HarmonyLib;
using Smooth.Algebraics;

namespace CastleStoryPlus.WorkerAI;

// Stockpile bonuses are subtracted from the straight-line distance (in blocks). They were up to 40 blocks,
// so workers walked past nearby stockpiles to fuller ones. Now they only break near-ties.
[Feature(Features.StockpileChoice, Features.StockpileChoiceInfo)]
[HarmonyPatch(typeof(PickupBonuses), nameof(PickupBonuses.Init))]
internal static class StockpileWeightsPatch
{
	private static void Postfix()
	{
		PickupBonuses.Weights.Default = new PickupBonuses.Weights(1f, 0f);
		Set(new Ressource[] { Adjectif.stones, Adjectif.woodBlock }, 0f, 3f, 2f, 3f);
		Set(new Ressource[] { Adjectif.rawIron, Adjectif.plant, Adjectif.orangeCrystal, Adjectif.iron, Adjectif.stoneBlock, Adjectif.plankBlock, Adjectif.glass, Adjectif.cog, Adjectif.fabric, Adjectif.rope }, 0f, 3f, 2f, 0f);
	}

	private static void Set(Ressource[] resources, float pickupPresence, float pickupRatio, float storePresence, float storeRatio)
	{
		foreach (Ressource resource in resources)
		{
			PickupBonuses.pickupWeights[resource] = new PickupBonuses.Weights(pickupPresence, pickupRatio);
			PickupBonuses.storeWeights[resource] = new PickupBonuses.Weights(storePresence, storeRatio);
		}
	}
}

// Score a stockpile by the resource being stored, not by whatever it happens to hold first.
[Feature(Features.StockpileChoice, Features.StockpileChoiceInfo)]
[HarmonyPatch(typeof(Recepteur), nameof(Recepteur.StoreDistanceBonusFor))]
internal static class StoreBonusPatch
{
	private static bool Prefix(Recepteur __instance, Ressource item, ref float __result)
	{
		if (__instance.DistanceHackForToolRack(out float bonus, item, pickup: false))
		{
			__result = bonus;
			return false;
		}
		Ressource resource = item ?? __instance.FirstResource();
		if (resource == null)
		{
			__result = 0f;
			return false;
		}
		Tuple<bool, float> presence = __instance.ComputePresenceRatio(resource);
		__result = PickupBonuses.ForStoring(resource, presence._1, presence._2);
		return false;
	}
}
