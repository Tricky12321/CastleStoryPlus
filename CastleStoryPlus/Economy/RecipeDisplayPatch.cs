using Brix.External.Factories;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryPlus.Economy;

// A workshop (forge) shows a model of what its current recipe makes, looked up in a fixed table of the game's own
// products. A recipe of the mod (steel ingot, upgrades, trades) is not in it, and the lookup threw every time the
// queue changed. Such a product now shows no model.
[Feature]
[HarmonyPatch(typeof(RecipeDisplay), "DisplayOf")]
internal static class RecipeDisplayPatch
{
	private static bool Prefix(Factory.AssetKey key, ref GameObject __result)
	{
		if (key != null && RecipeDisplay.displays.ContainsKey(key))
		{
			return true;
		}
		__result = null;
		return false;
	}
}
