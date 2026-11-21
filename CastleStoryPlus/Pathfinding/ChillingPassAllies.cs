#if UNITY6
using Brix.Game.AI;
using Brix.Game.Components;
using CastleStoryPlus.Core;
using HarmonyLib;

namespace CastleStoryPlus.Pathfinding;

// A bricktron on an idle task did not walk through allies. The game lets a bricktron step, climb or step down into a
// voxel where an ally stands only while it is not fighting and not "chilling" (Builder.CanPassThrough), and chilling is
// the activity of the idle task's walk and sit, of stockpile consolidation (until the worker picks something up) and
// of the mod's idle clean-up, repair and stockpile emptying. With the ally's voxel the only way (a stair one voxel wide between two terraces in a maintainer's
// save) three or four such workers stood in one stair voxel for 15 s and more, gave their walk up
// (PathExecutionFailed) or walked back down and up again, while workers at work walked through each other there.
// A chilling bricktron now passes allies as one at work does; the rest of the game's rule stays (not while fighting;
// a soldier not while it digs). Bricktrons that end up in the same voxel standing still are still moved apart by the
// game (Labor.Unstack, which also covers chilling bricktrons). Nothing is sent to other players.
[Feature(Features.PassThroughAllies, Features.PassThroughAlliesInfo)]
[HarmonyPatch(typeof(Builder), nameof(Builder.CanPassThrough))]
internal static class ChillingPassAllies
{
	private static void Postfix(Builder __instance, Labor ___labor, ref bool __result)
	{
		if (__result || ___labor == null || ___labor.Activity != Activity.Chilling)
		{
			return;
		}
		__result = __instance.IsWorker() || ___labor.hackedDigGoal.IsNullOrReleased();
	}
}
#endif
