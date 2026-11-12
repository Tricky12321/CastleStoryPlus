using Brix.Engine;
using Brix.External.Factories;
using CastleStoryPlus.Core;
using HarmonyLib;

namespace CastleStoryPlus.WorkerAI;

// A quarry keeps its stair. The quarry's goal list knows the stair's steps only from its own list of them, made
// again when the quarry's settings change; a committed quarry whose list is empty (as when it was not made again
// after the quarry was set up or loaded) treated the steps as ground and dug them away, leaving holes no bricktron
// can climb. The list is now made again whenever it is empty in a committed quarry with steps to cut (not an ore
// mine, which has none). And a step's task was thrown away and made anew every 15 seconds while it waited (the
// game always replaces a step's task), so a worker on its way there lost it and turned back; it is now kept while
// its step and direction stay the same.
[Feature(Features.WorkerAI, Features.WorkerAIInfo)]
[HarmonyPatch(typeof(QuarryGoalProvider2), "VoxelNeedsGoal")]
internal static class QuarryStairs
{
	[HarmonyPriority(Priority.High)]
	private static void Prefix(QuarryGoalProvider2 __instance)
	{
		if (__instance.stairs.Count == 0 && __instance.data != null && __instance.data.Commited && !Metallurgy.OreMine.IsOreMine(__instance.data))
		{
			__instance.RefreshStairs();
		}
	}

	private static void Postfix(QuarryGoalProvider2 __instance, XYZ pos, ref SpawnOrder __result)
	{
		if (!__result.alwaysReplace || __result.key != Test.BuildStairsGoal)
		{
			return;
		}
		if (__instance.spawnPerVoxel.TryGetValue(pos, out SpawnOrder current) && current.key == __result.key && current.direction == __result.direction && current.instance != null && current.IsValid())
		{
			__result.alwaysReplace = false;
		}
	}
}
