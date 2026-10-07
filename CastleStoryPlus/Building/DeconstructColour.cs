using System.Collections.Generic;
using Brix.External.Factories;
using Brix.Game.Components;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryPlus.Building;

// A build task's blueprints share materials per block and state, kept by the task's BuildGoalMaterialProvider. While
// the task is not selected (after loading, after clicking elsewhere) the game sets all of them to white, so its
// deconstruction ghosts lost their dark orange and looked like pale blueprints; only selecting the task brought the
// orange back. Now the deconstruction materials always keep their orange; the other blueprints still go white.
[Feature(Features.DeconstructColour, Features.DeconstructColourInfo)]
[HarmonyPatch(typeof(BuildGoalMaterialProvider), nameof(BuildGoalMaterialProvider.GetSharedMaterialForAsset))]
internal static class DeconstructColourMaterialPatch
{
	private static void Postfix(PlacementFlags mode, BlueprintMaterialData __result)
	{
		DeconstructColour.Restore(mode, __result);
	}
}

[Feature(Features.DeconstructColour, Features.DeconstructColourInfo)]
[HarmonyPatch(typeof(BuildGoalMaterialProvider), nameof(BuildGoalMaterialProvider.ToggleSharedMaterialFocus))]
internal static class DeconstructColourFocusPatch
{
	private static void Postfix(BuildGoalMaterialProvider __instance)
	{
		foreach (KeyValuePair<Factory.AssetKey, Dictionary<PlacementFlags, BlueprintMaterialData>> asset in __instance.materialForAsset)
		{
			foreach (KeyValuePair<PlacementFlags, BlueprintMaterialData> state in asset.Value)
			{
				DeconstructColour.Restore(state.Key, state.Value);
			}
		}
	}
}

internal static class DeconstructColour
{
	// The orange of a deconstruction state back on its materials. The remover's hover (mayRemove, yellow) is left alone.
	internal static void Restore(PlacementFlags mode, BlueprintMaterialData data)
	{
		if ((mode & PlacementFlags.anti) == 0 || (mode & PlacementFlags.mayRemove) != 0 || data == null || data.materials == null)
		{
			return;
		}
		foreach (Material material in data.materials)
		{
			if (material != null)
			{
				material.color = data.OriginalColor;
			}
		}
	}
}
