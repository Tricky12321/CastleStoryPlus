using Brix.Engine;
using Brix.Game.AI;
using Brix.Game.AI.KnowledgeSpace;
using CastleStoryPlus.Core;
using HarmonyLib;

namespace CastleStoryPlus.Pathfinding;

// Bricktrons walk through each other. A bricktron standing in a voxel blocked every other bricktron's path through
// it, unless the path search happened to allow passing through allies, so workers waited for, or walked around, each
// other in doorways, on stairs and on narrow walls, and a path failed when another worker stood in the way. Allies
// (own and allied factions) now never block each other; enemies still do.
[Feature(Features.PassThroughAllies, Features.PassThroughAlliesInfo)]
[HarmonyPatch(typeof(VoxelPresence), "PreventsMovement")]
internal static class PassThroughAllies
{
	private static bool Prefix(VoxelPresence __instance, VoxelPresence presence, ref bool __result)
	{
		if (presence == null || presence == __instance || presence.affiliation == null || !presence.affiliation.IsAllied(__instance))
		{
			return true;
		}
		__result = false;
		return false;
	}
}

// The workers' reachability map ("can this worker reach that?") counted a voxel with any bricktron in it as a wall,
// so a worker standing in a doorway could cut a whole area off and every task there was "out of reach". It now
// ignores bricktrons, as the game's other reachability map already does.
[Feature(Features.PassThroughAllies, Features.PassThroughAlliesInfo)]
[HarmonyPatch(typeof(KnowledgeConnectedAccessFinder.BranchEvaluator), nameof(KnowledgeConnectedAccessFinder.BranchEvaluator.PresenceTest))]
internal static class PassThroughAlliesReachPatch
{
	private static bool Prefix(XYZ p, ref bool __result)
	{
		__result = KnowledgeDisjointRegionsFinder.BranchEvaluator.PresenceTest(p);
		return false;
	}
}
