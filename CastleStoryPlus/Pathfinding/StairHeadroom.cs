using System.Collections.Generic;
using System.Reflection;
using Brix.Components;
using Brix.Engine;
using Brix.Engine.Blocks;
using Brix.Pathfinding.Test;
using CastleStoryPlus.Core;
using HarmonyLib;

namespace CastleStoryPlus.Pathfinding;

// A bricktron needs 2 empty voxels above a full surface and 1 above a slope voxel: any block above counts, whatever
// its shape. Stairs under a walkway (a wall walk over a flight of steps) lost their nodes and branches under it, so
// the flight was cut and bricktrons never used it. On stair blocks (the slope and its full step) 1 empty voxel above
// the step's lowest point is now enough; flat ground keeps the game's rule. The head may go a little into the walkway.
internal static class StairHeadroom
{
	// StairBlockInfoMeta.StairType (16, 503-506) and SimpleStairBlockInfoMeta.StairType (507-509).
	internal static bool IsStair(XYZ position)
	{
		BlockEngine engine = BrixSingleton<BlockEngine>.Instance;
		if (!engine)
		{
			return false;
		}
		int id = engine.GetBlockID(position);
		return id == 16 || (id >= 503 && id <= 509);
	}

	// The game's "the voxel above is empty" test of the initial node build, given the voxel above the node.
	internal static bool HasHeadroom(XYZ above)
	{
		if (Voxel.IsEmpty(above))
		{
			return true;
		}
		XYZ node = above - XYZ.up;
		if (IsStair(node))
		{
			return true;
		}
		return Voxel.IsEmpty(node) && IsStair(node - XYZ.up);
	}
}

// Node and branch test (also used when blocks change and while walking).
[Feature(Features.Pathfinding, Features.PathfindingInfo)]
[HarmonyPatch(typeof(Voxel), nameof(Voxel.IsOpenAndWalkable))]
internal static class StairHeadroomWalkablePatch
{
	private static void Postfix(XYZ position, ref bool __result)
	{
		if (__result || !Voxel.IsNonEmpty(position + XYZ.up))
		{
			return;
		}
		if (Voxel.IsNonEmpty(position))
		{
			__result = !Voxel.IsFull(position) && StairHeadroom.IsStair(position);
		}
		else
		{
			__result = Voxel.IsFull(position - XYZ.up) && StairHeadroom.IsStair(position - XYZ.up);
		}
	}
}

// The node build when a map loads tests the voxel above itself instead of using IsOpenAndWalkable.
[Feature(Features.Pathfinding, Features.PathfindingInfo)]
[HarmonyPatch]
internal static class StairHeadroomLoadPatch
{
	private static MethodBase TargetMethod()
	{
		return AccessTools.EnumeratorMoveNext(AccessTools.Method(typeof(TestPathfinding), "ReadAllPathNodes"));
	}

	private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
	{
		MethodInfo isEmpty = AccessTools.Method(typeof(Voxel), nameof(Voxel.IsEmpty));
		MethodInfo headroom = AccessTools.Method(typeof(StairHeadroom), nameof(StairHeadroom.HasHeadroom));
		List<CodeInstruction> code = new List<CodeInstruction>(instructions);
		int replaced = 0;
		foreach (CodeInstruction instruction in code)
		{
			if (instruction.Calls(isEmpty))
			{
				instruction.operand = headroom;
				replaced++;
			}
		}
		if (replaced != 2)
		{
			Plugin.Log.LogWarning("StairHeadroom: expected 2 headroom tests in ReadAllPathNodes, found " + replaced);
		}
		return code;
	}
}
