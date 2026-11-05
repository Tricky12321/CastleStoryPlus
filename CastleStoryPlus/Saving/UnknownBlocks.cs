using System.Collections.Generic;
using Brix.Engine;
using Brix.Engine.Blocks;
using CastleStoryPlus.Core;
using HarmonyLib;

namespace CastleStoryPlus.Saving;

// A save with a block type this game does not know (a mod's block, with the mod or that feature off, or a newer
// version's) stopped loading at that block: the game looked the type up, got nothing and threw while making it, and
// the whole load ended there. Such blocks are left out now (they could not exist anyway), each type written to the
// log once with how many were skipped, and the rest of the world loads.
[Feature]
[HarmonyPatch(typeof(BlockEngine), "RegisterBlockDataStruct")]
internal static class UnknownBlocks
{
	private static readonly Dictionary<int, int> Skipped = new Dictionary<int, int>();

	private static bool Prefix(BlockDataStruct bds)
	{
		if (bds.type.BlockInfoIDToBlockInfo() != null)
		{
			return true;
		}
		Skipped.TryGetValue(bds.type, out int count);
		Skipped[bds.type] = count + 1;
		if (count == 0)
		{
			Plugin.Log.LogWarning("UnknownBlocks: block type " + bds.type + " is unknown (first at " + bds.P + "): its blocks are left out of the world");
		}
		return false;
	}
}
