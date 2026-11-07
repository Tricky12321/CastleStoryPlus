using System.Collections.Generic;
using Brix.Engine;
using Brix.Game.Components;
using Brix.Game.Components.Volumes;
using CastleStoryPlus.Core;
using CastleStoryPlus.Pathfinding;
using HarmonyLib;

namespace CastleStoryPlus.Building;

// A ladder leans on wood too. The ladder's blueprint (CustomBlocks) uses the stone brick's validator, whose support is
// stone only (stoneSupport): a ladder above the ground was red beside a wood column (Wood_VerticalBeam: logSupport),
// a log wall or a plank walkway, and a row of ladders dragged up a column had nothing to stand on but the bottom one.
// Now the ladder's support cells (the block below it and the four beside it) also hold it when they carry a log's or
// a plank's support: placed wood for building it, wood blueprints too for placing the blueprint.
[Feature(Features.Ladders, Features.LaddersInfo)]
internal static class LadderSupport
{
	private const VPropertyMask Wood = VPropertyMask.logSupport | VPropertyMask.plankSupport;

	// Whether wood beside or under the ladder holds it. built: only placed wood counts.
	internal static bool OnWood(ref ValidationData vData, bool built)
	{
		IVolume volume = vData.Volume;
		if (volume == null || volume.AssetKey == null || volume.AssetKey.Name != Ladders.Name)
		{
			return false;
		}
		SupportState wanted = built ? SupportState.CCSupport : (SupportState.CCSupport | SupportState.BPSupport);
		VolumePropertyRequest cells = volume.GetCases(VProperty.mainSupportReq);
		for (int i = 0; i < cells.Count; i++)
		{
			SupportState state = VUtil.SupportStateAt(cells[i], Wood, vData.Faction, null);
			if ((state & wanted) != 0 && (state & SupportState.Valid) != 0)
			{
				return true;
			}
		}
		return false;
	}

	// A blueprint below the ladder (the ladder under it, not built yet) still makes it wait: built from the bottom up,
	// as CustomBlocksBuildablePatch keeps it.
	internal static bool WaitsForBelow(ref ValidationData vData, Dictionary<XYZ, SupportState> supportStates)
	{
		VolumePropertyRequest body = vData.Volume.GetCases(VProperty.fantome);
		if (body.Count == 0 || supportStates == null)
		{
			return false;
		}
		int lowest = int.MaxValue;
		for (int i = 0; i < body.Count; i++)
		{
			if (body[i].y < lowest)
			{
				lowest = body[i].y;
			}
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

// Valid: a ladder blueprint may be placed against wood or wood blueprints.
[Feature(Features.Ladders, Features.LaddersInfo)]
[HarmonyPatch(typeof(AbstractValidator), "IsValid")]
internal static class LadderSupportValidPatch
{
	private static void Postfix(AbstractValidator __instance, ref ValidationData vData, ref bool __result)
	{
		if (!__result && __instance is BlueprintValidator)
		{
			__result = LadderSupport.OnWood(ref vData, built: false);
		}
	}
}

// Buildable: the wood beside or under the ladder's blueprint is built.
[Feature(Features.Ladders, Features.LaddersInfo)]
[HarmonyPatch(typeof(BlueprintValidator), "IsBuildable")]
internal static class LadderSupportBuildablePatch
{
	private static void Postfix(ref ValidationData vData, Dictionary<XYZ, SupportState> supportStates, ref bool __result)
	{
		if (!__result && LadderSupport.OnWood(ref vData, built: true) && !LadderSupport.WaitsForBelow(ref vData, supportStates))
		{
			__result = true;
		}
	}
}
