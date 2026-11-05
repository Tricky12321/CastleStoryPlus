using System.Collections.Generic;
using Brix.Engine;
using Brix.External.Factories;
using Brix.Game.Components;
using Brix.Game.Components.Volumes;
using CastleStoryPlus.Core;
using HarmonyLib;

namespace CastleStoryPlus.Building;

// A wooden slab (plank) is also held from the side: by a plank right next to it on its level that the game's own rule
// holds (its ends rest on a log, a block or a beam, or on planks in line with it). The game held a plank only at its
// ends along its length, so a slab laid beside a finished walkway stayed red. One step out only: the plank beside it
// is judged by the game's rule alone, so planks do not hold each other in an endless chain. For a blueprint to be
// valid the plank beside it may be a valid blueprint too; to be built, and for a built plank to stay up, it must be a
// finished one. Rope bridge planks keep their own rule.
[Feature(Features.SidePlanks, Features.SidePlanksInfo)]
[HarmonyPatch]
internal static class SidePlanks
{
	private static readonly XYZ[] Sides = new XYZ[4]
	{
		new XYZ(1, 0, 0),
		new XYZ(-1, 0, 0),
		new XYZ(0, 0, 1),
		new XYZ(0, 0, -1)
	};

	// While a neighbour is judged: by the game's rule only.
	private static bool _inner;

	[HarmonyPatch(typeof(PlankBPValidator), "IsValid")]
	[HarmonyPostfix]
	private static void BlueprintValid(ref ValidationData vData, ref bool __result)
	{
		if (!__result && !_inner && HeldFromSide(vData, finishedOnly: false))
		{
			__result = true;
		}
	}

	[HarmonyPatch(typeof(PlankBPValidator), "IsBuildable")]
	[HarmonyPostfix]
	private static void BlueprintBuildable(ref ValidationData vData, ref bool __result)
	{
		if (!__result && !_inner && HeldFromSide(vData, finishedOnly: true))
		{
			__result = true;
		}
	}

	[HarmonyPatch(typeof(PlankBlockValidator), nameof(PlankBlockValidator.Validate))]
	[HarmonyPostfix]
	private static void BlockStays(ref ValidationData vData, ref ValidationResult __result)
	{
		if (__result == ValidationResult.Invalid && !_inner && HeldFromSide(vData, finishedOnly: true))
		{
			__result = ValidationResult.Valid;
		}
	}

	private static bool HeldFromSide(ValidationData vData, bool finishedOnly)
	{
		if (!vData.IsNotDefault || vData.Volume == null || IsRopeBridge(vData.Volume))
		{
			return false;
		}
		List<XYZ> own = new List<XYZ>();
		foreach (XYZ cell in vData.Volume.GetCases(VPropertyMask.occupy | VPropertyMask.fantome))
		{
			own.Add(cell);
		}
		HashSet<UnityEngine.GameObject> judged = new HashSet<UnityEngine.GameObject>();
		foreach (XYZ cell in own)
		{
			foreach (XYZ side in Sides)
			{
				XYZ next = cell + side;
				if (own.Contains(next))
				{
					continue;
				}
				ValidationData neighbour = BaseRecursiveValidation.VDataWithPropertyAt(VPropertyMask.plankSupport, next);
				if (!neighbour.IsNotDefault || neighbour.Target == vData.Target || neighbour.Target == null || !judged.Add(neighbour.Target))
				{
					continue;
				}
				if ((finishedOnly && neighbour.IsBlueprint) || neighbour.Volume == null || IsRopeBridge(neighbour.Volume))
				{
					continue;
				}
				if (HeldByGame(neighbour))
				{
					return true;
				}
			}
		}
		return false;
	}

	// The neighbour by the game's rule alone. Asked of the rule itself (the component's validator), not of the
	// neighbour's ValidatorComponent: that one also sets the neighbour's state (a blueprint turned invalid, a block
	// made unstable), and each change revalidated the planks round it, which asked their neighbours again: on a large
	// plank floor the chain ran hundreds of planks deep and the game crashed (stack overflow) loading such a save.
	private static bool HeldByGame(ValidationData neighbour)
	{
		IValidator validator = neighbour.Validator;
		if (validator is ValidatorComponent component)
		{
			validator = component.Validator;
		}
		if (validator == null)
		{
			return false;
		}
		_inner = true;
		try
		{
			return validator.Validate(ref neighbour, null) != ValidationResult.Invalid;
		}
		finally
		{
			_inner = false;
		}
	}

	private static bool IsRopeBridge(IVolume volume)
	{
		return volume.AssetKey == Blocks.RopeBridgePlank || volume.AssetKey == Blueprints.RopeBridgePlank;
	}
}
