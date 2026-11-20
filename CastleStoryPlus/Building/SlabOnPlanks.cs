using System.Collections.Generic;
using Brix.Engine;
using Brix.Game.Components;
using Brix.Game.Components.Volumes;
using CastleStoryPlus.Core;
using HarmonyLib;

namespace CastleStoryPlus.Building;

// A wooden slab (CustomBlocks) stands on planks and other wood floors: the slabs use the brick's validator, whose
// support is stone only (stoneSupport), so a slab laid on a plank floor or walkway was red and could not be placed.
// Now a cell under a slab also holds it when it carries a plank's support (plankSupport): placed planks for a placed
// slab and for building it, valid plank blueprints too for placing the slab's blueprint. The plank cells are counted
// together with the stone and the terrain the game's rule counts, against the same need (half the slab's footprint,
// BlockSupportRules.RequiredSupport): one plank under a corner of a 2 x 4 slab no longer holds all of it (#141). A plank
// blueprint counts only when it is valid itself (SupportState.Valid), so blueprints in the air do not hold each other.
[Feature(Features.CustomBlocks, Features.CustomBlocksInfo)]
internal static class SlabOnPlanks
{
	// Whether the slab's support cells hold it with the wood under it counted. accepted: the support the validator takes
	// from stone (a blueprint's BP or CC support, a block's or a buildable blueprint's CC only); built: only placed
	// planks count.
	internal static bool OnPlanks(AbstractValidator validator, ref ValidationData vData, Dictionary<XYZ, SupportState> supportStates, SupportState accepted, bool built)
	{
		if (vData.Volume == null || supportStates == null || supportStates.Count == 0 || !CustomBlocks.IsWoodSlab(vData.Volume))
		{
			return false;
		}
		HashSet<XYZ> under = new HashSet<XYZ>();
		foreach (XYZ cell in vData.Volume.GetCases(VPropertyMask.occupy | VPropertyMask.fantome))
		{
			under.Add(cell - XYZ.up);
		}
		SupportState wanted = built ? SupportState.CCSupport : (SupportState.CCSupport | SupportState.BPSupport);
		int supported = 0;
		foreach (KeyValuePair<XYZ, SupportState> support in supportStates)
		{
			if (((support.Value & accepted) != 0 && (support.Value & SupportState.Valid) != 0) || validator.HasGroundSupportAt(ref vData, support.Key))
			{
				supported++;
				continue;
			}
			if (!under.Contains(support.Key))
			{
				continue;
			}
			SupportState plank = VUtil.SupportStateAt(support.Key, VPropertyMask.plankSupport, vData.Faction, null);
			if ((plank & wanted) != 0 && (plank & SupportState.Valid) != 0)
			{
				supported++;
			}
		}
		return BlockSupportRules.EnoughSupport(supported, supportStates.Count, vData.MaxMissingSupport);
	}

	// A blueprint (of a block, not a slab only) under the slab: the slab waits for it, as every block does.
	internal static bool WaitsForBlueprintBelow(ref ValidationData vData, Dictionary<XYZ, SupportState> supportStates)
	{
		int lowest = int.MaxValue;
		foreach (XYZ cell in vData.Volume.GetCases(VPropertyMask.occupy | VPropertyMask.fantome))
		{
			lowest = System.Math.Min(lowest, cell.y);
		}
		foreach (KeyValuePair<XYZ, SupportState> support in supportStates)
		{
			if (support.Key.y < lowest && (support.Value & SupportState.BPSupport) != 0)
			{
				return true;
			}
		}
		return false;
	}
}

// Valid: a placed slab stays up on planks, a slab blueprint may be placed on planks or valid plank blueprints.
[Feature(Features.CustomBlocks, Features.CustomBlocksInfo)]
[HarmonyPatch(typeof(AbstractValidator), "IsValid")]
internal static class SlabOnPlanksValidPatch
{
	private static void Postfix(AbstractValidator __instance, ref ValidationData vData, Dictionary<XYZ, SupportState> supportStates, ref bool __result)
	{
		if (!__result)
		{
			bool built = !(__instance is BlueprintValidator);
			__result = SlabOnPlanks.OnPlanks(__instance, ref vData, supportStates, __instance.AcceptedSupportState, built);
		}
	}
}

// Buildable: enough of the slab's support cells hold placed stone, terrain or placed planks.
[Feature(Features.CustomBlocks, Features.CustomBlocksInfo)]
[HarmonyPatch(typeof(BlueprintValidator), "IsBuildable")]
internal static class SlabOnPlanksBuildablePatch
{
	private static void Postfix(BlueprintValidator __instance, ref ValidationData vData, Dictionary<XYZ, SupportState> supportStates, ref bool __result)
	{
		if (__result || vData.Volume == null || !CustomBlocks.IsWoodSlab(vData.Volume) || SlabOnPlanks.WaitsForBlueprintBelow(ref vData, supportStates))
		{
			return;
		}
		__result = SlabOnPlanks.OnPlanks(__instance, ref vData, supportStates, SupportState.CCSupport, built: true);
	}
}
