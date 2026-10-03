using System.Collections.Generic;
using System.Reflection;
using Brix.Game.AI;
using Brix.Pathfinding;
using CastleStoryPlus.Core;
using HarmonyLib;

namespace CastleStoryPlus.Pathfinding;

// The A* distance weight was 45 against ~30 real cost per flat step (weighted A*, eps ~1.5), which
// accepted noticeably longer routes. 33 keeps the estimate close to the real cost.
[Feature(Features.Pathfinding, Features.PathfindingInfo)]
[HarmonyPatch]
internal static class HeuristicPatch
{
	private static IEnumerable<MethodBase> TargetMethods()
	{
		yield return AccessTools.Method(typeof(PathAStarQueue), nameof(PathAStarQueue.Evaluate));
		yield return AccessTools.Method(typeof(SearchPathRequest.AStarQueue), nameof(SearchPathRequest.AStarQueue.Evaluate));
		yield return AccessTools.Method(typeof(SearchPathRequest.UnrollQueue), nameof(SearchPathRequest.UnrollQueue.Evaluate));
	}

	private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
	{
		return IL.ReplaceInt(instructions, 45, 33);
	}
}
