using Brix.Engine;
using Brix.Game.Components;
using Brix.Game.Components.Volumes;
using CastleStoryPlus.Core;
using HarmonyLib;

namespace CastleStoryPlus.Building;

// A wooden slab (CustomBlocks) stands on planks and other wood floors: the slabs use the brick's validator, whose
// support is stone only (stoneSupport), so a slab laid on a plank floor or walkway was red and could not be placed.
// Now the cells under a slab also hold it when they carry a plank's support (plankSupport): placed planks for a placed
// slab and for building it, plank blueprints too for placing the slab's blueprint.
[Feature(Features.CustomBlocks, Features.CustomBlocksInfo)]
internal static class SlabOnPlanks
{
	// Whether wood under the slab holds it. built: only placed planks count.
	internal static bool OnPlanks(ref ValidationData vData, bool built)
	{
		if (vData.Volume == null || !CustomBlocks.IsWoodSlab(vData.Volume))
		{
			return false;
		}
		SupportState wanted = built ? SupportState.CCSupport : (SupportState.CCSupport | SupportState.BPSupport);
		foreach (XYZ cell in vData.Volume.GetCases(VPropertyMask.occupy | VPropertyMask.fantome))
		{
			XYZ below = cell - XYZ.up;
			SupportState state = VUtil.SupportStateAt(below, VPropertyMask.plankSupport, vData.Faction, null);
			if ((state & wanted) != 0 && (state & SupportState.Valid) != 0)
			{
				return true;
			}
		}
		return false;
	}
}

// Valid: a placed slab stays up on planks, a slab blueprint may be placed on planks or plank blueprints.
[Feature(Features.CustomBlocks, Features.CustomBlocksInfo)]
[HarmonyPatch(typeof(AbstractValidator), "IsValid")]
internal static class SlabOnPlanksValidPatch
{
	private static void Postfix(AbstractValidator __instance, ref ValidationData vData, ref bool __result)
	{
		if (!__result)
		{
			__result = SlabOnPlanks.OnPlanks(ref vData, built: !(__instance is BlueprintValidator));
		}
	}
}

// Buildable: the planks under the slab's blueprint are built.
[Feature(Features.CustomBlocks, Features.CustomBlocksInfo)]
[HarmonyPatch(typeof(BlueprintValidator), "IsBuildable")]
internal static class SlabOnPlanksBuildablePatch
{
	private static void Postfix(ref ValidationData vData, ref bool __result)
	{
		if (!__result)
		{
			__result = SlabOnPlanks.OnPlanks(ref vData, built: true);
		}
	}
}
