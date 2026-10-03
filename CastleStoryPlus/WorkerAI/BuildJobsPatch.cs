using Brix.Game.AI;
using Brix.Game.AI.Nodes;
using Brix.Game.Semantique;
using Brix.Game.Components;
using CastleStoryPlus.Core;
using HarmonyLib;
using Motus.Behavior;
using UnityEngine;

namespace CastleStoryPlus.WorkerAI;

internal static class BuildNeeds
{
	public static int OtherWorkerCount(Goal goal, Labor labor)
	{
		// Workers that chose the goal but have not started on it yet count too (TaskReservation).
		return goal.workers.Count - (goal.workers.Contains(labor) ? 1 : 0) + TaskReservation.OtherReservations(goal, labor);
	}

	public static int RemainingNeedFor(BuildGoal goal, GameObject item)
	{
		Ressource ressource = (item == null) ? null : Apparence.PrincipaleOf(item);
		if (ressource == null)
		{
			return -1;
		}
		return goal.recepteur.CurrentCapacity.Value(ressource);
	}

	public static int PickUpAmountFor(BuildGoal goal, Labor labor)
	{
		int remaining = RemainingNeedFor(goal, labor.Find(goal.recepteur));
		if (remaining < 0)
		{
			return 20;
		}
		return Mathf.Clamp(remaining - OtherWorkerCount(goal, labor), 1, 20);
	}
}

// A build goal counts as covered once the other workers on it already bring what it still needs,
// so several workers no longer take the same small job.
[Feature(Features.BuildJobs, Features.BuildJobsInfo)]
[HarmonyPatch(typeof(BuildGoal), nameof(BuildGoal.IsMatchFor))]
internal static class BuildGoalCoveredPatch
{
	private static void Postfix(BuildGoal __instance, Labor labor, AIErrorsStore errorDestination, ref bool __result)
	{
		if (!__result || __instance.IsAnti)
		{
			return;
		}
		int others = BuildNeeds.OtherWorkerCount(__instance, labor);
		if (others == 0)
		{
			return;
		}
		int remaining = BuildNeeds.RemainingNeedFor(__instance, labor.Find(__instance.recepteur));
		if (remaining >= 0 && remaining <= others)
		{
			errorDestination.AddDecisionError(SharedWorkException<Exceptions.GoalTooCrowded>.Get.Problem, labor);
			__result = false;
		}
	}
}

// Pick up only what the blueprint still needs, minus what the other workers are bringing (1-20; was always 20).
[Feature(Features.BuildJobs, Features.BuildJobsInfo)]
[HarmonyPatch(typeof(BuildGoal), nameof(BuildGoal.GetWorkNode))]
internal static class BuildPickUpAmountPatch
{
	private static bool Prefix(BuildGoal __instance, Labor labor, ref Node __result)
	{
		if (__instance.IsNullOrReleased() || labor.IsNullOrReleased() || __instance.IsAnti || labor.recepteur.FirstThatFitsIn(__instance.recepteur) != null)
		{
			return true;
		}
		int upTo = BuildNeeds.PickUpAmountFor(__instance, labor);
		__result = (StaticNode)Node.Sequence.Label("BuildGoal.GetWorkNode Sequence").Do((BuildGoal aBuildGoal, Labor aLabor, int amount) => aLabor.FindAndPickUp(aBuildGoal.recepteur, amount), __instance, labor, upTo).Label("FindAndPickUp", labor)
			.Do((BuildGoal aBuildGoal, Labor aLabor) => aLabor.GoStoreIn(aBuildGoal), __instance, labor);
		return false;
	}
}
