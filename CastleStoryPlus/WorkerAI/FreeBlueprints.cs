using Brix.Game.AI;
using Brix.Game.AI.Nodes;
using Brix.Game.Components;
using Brix.Game.Components.Volumes;
using Brix.Game.Locomotion;
using CastleStoryPlus.Core;
using HarmonyLib;
using Motus.Behavior;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.WorkerAI;

// A blueprint that costs nothing (the mod's saplings and sapling area markers) was never built: a blueprint turns
// into its building when the last item it needs is delivered, and the builders only look for blueprints that take
// something they can fetch. Now a builder also takes on a free blueprint: it goes there, works on it a moment with
// its hammer and the blueprint becomes its building. One builder per free blueprint.
[Feature(Features.BuildJobs, Features.BuildJobsInfo)]
internal static class FreeBlueprints
{
	internal static bool IsFree(BuildGoal goal)
	{
		return goal != null && !goal.IsAnti && goal.recepteur != null && goal.recepteur.BaseCapacity.NoneOrZeros();
	}

	internal static Node WorkNode(BuildGoal goal, Labor labor)
	{
		return (StaticNode)Node.Sequence.Label("Build free blueprint")
			.Do((Labor l, BuildGoal g) => l.RunToExactly(Location.Of(g)), labor, goal)
			.Do((Labor l, BuildGoal g) => AnimationNodes.Once(Node.Empty, Working.WorkStates.Hammer, g.transform, l), labor, goal)
			.Do((BuildGoal g) => Finish(g), goal);
	}

	private static void Finish(BuildGoal goal)
	{
		if (!NetworkServer.active || goal.IsNullOrReleased() || goal.Blueprint == null || goal.Blueprint.Released)
		{
			return;
		}
		goal.Blueprint.MorphToObject();
	}
}

// The builders' search finds free blueprints too.
[Feature(Features.BuildJobs, Features.BuildJobsInfo)]
[HarmonyPatch(typeof(BuildGoalFinder), "Visit", new System.Type[] { typeof(IVolume) })]
internal static class FreeBlueprintsFinderPatch
{
	private static void Postfix(BuildGoalFinder __instance, IVolume v)
	{
		GameObject go = (v != null) ? v.GameObject : null;
		if (go.IsNullOrReleased())
		{
			return;
		}
		BuildGoal goal = go.GetComponent<BuildGoal>();
		if (!FreeBlueprints.IsFree(goal) || __instance.Results.Contains(goal) || !goal.Commited || goal.transform.parent != __instance.ProjecTransform)
		{
			return;
		}
		PlacementFlags state = goal.Blueprint.State;
		if (state.HasFlag(PlacementFlags.valid) && state.HasFlag(PlacementFlags.buildable))
		{
			__instance.Results.Add(goal);
		}
	}
}

// A free blueprint is never "full" of what it waits for: it stays a goal until it is built.
[Feature(Features.BuildJobs, Features.BuildJobsInfo)]
[HarmonyPatch(typeof(BuildGoal), nameof(BuildGoal.IsValid))]
internal static class FreeBlueprintsValidPatch
{
	private static void Postfix(BuildGoal __instance, ref bool __result)
	{
		if (!__result && FreeBlueprints.IsFree(__instance) && !__instance.IsNullOrReleased())
		{
			__result = true;
		}
	}
}

// A builder matches a free blueprint it can reach that no other builder is on (it has nothing to pick up for it).
[Feature(Features.BuildJobs, Features.BuildJobsInfo)]
[HarmonyPatch(typeof(BuildGoal), nameof(BuildGoal.IsMatchFor))]
internal static class FreeBlueprintsMatchPatch
{
	[HarmonyPriority(Priority.High)]
	private static bool Prefix(BuildGoal __instance, Labor labor, AIErrorsStore errorDestination, ref bool __result)
	{
		if (!FreeBlueprints.IsFree(__instance) || labor.IsNullOrReleased())
		{
			return true;
		}
		__result = false;
		if (!__instance.Blueprint.State.HasFlag(PlacementFlags.valid) || !__instance.Blueprint.State.HasFlag(PlacementFlags.buildable))
		{
			errorDestination.AddDecisionError(SharedWorkException<Exceptions.GoalUnavailable>.Get.Problem, labor);
		}
		else if (__instance.HasOtherWorkerAssigned(labor))
		{
			errorDestination.AddDecisionError(SharedWorkException<Exceptions.GoalTooCrowded>.Get.Problem, labor);
		}
		else if (!__instance.CanReachWithKnowledge(labor))
		{
			errorDestination.AddDecisionError(SharedWorkException<Exceptions.CantReach>.Get.Problem, labor);
		}
		else
		{
			__result = true;
		}
		return false;
	}
}

// A builder only joins a build task when it can find one of the resources the task's blueprints need, so a task
// with free blueprints and blueprints waiting for something not to be had (a ward's crystal) was never taken up.
// It is when one of its free blueprints is ready and no other builder is on it.
[Feature(Features.BuildJobs, Features.BuildJobsInfo)]
[HarmonyPatch(typeof(Project), nameof(Project.VerifyWorkFor))]
internal static class FreeBlueprintsVerifyPatch
{
	private static bool Prefix(Project __instance, Labor labor, ref Node __result, bool __runOriginal)
	{
		// Another prefix (DemolishVerifyPatch) may have passed the worker already.
		if (!__runOriginal)
		{
			return false;
		}
		if (!(__instance is BuildProject) || labor.IsNullOrReleased())
		{
			return true;
		}
		BuildGoalProvider provider = __instance.GetComponent<BuildGoalProvider>();
		if (provider == null)
		{
			return true;
		}
		foreach (Blueprint blueprint in provider.TrackedBlueprints)
		{
			if (blueprint == null || blueprint.Released || !blueprint.State.HasFlag(PlacementFlags.valid) || !blueprint.State.HasFlag(PlacementFlags.buildable))
			{
				continue;
			}
			BuildGoal goal = blueprint.GetComponent<BuildGoal>();
			if (FreeBlueprints.IsFree(goal) && goal.Commited && !goal.HasOtherWorkerAssigned(labor))
			{
				__result = Node.Empty;
				return false;
			}
		}
		return true;
	}
}

// The work on a free blueprint: go there, hammer, build it.
[Feature(Features.BuildJobs, Features.BuildJobsInfo)]
[HarmonyPatch(typeof(BuildGoal), nameof(BuildGoal.GetWorkNode))]
internal static class FreeBlueprintsWorkPatch
{
	[HarmonyPriority(Priority.High)]
	private static bool Prefix(BuildGoal __instance, Labor labor, ref Node __result)
	{
		if (__instance.IsNullOrReleased() || labor.IsNullOrReleased() || !FreeBlueprints.IsFree(__instance))
		{
			return true;
		}
		__result = FreeBlueprints.WorkNode(__instance, labor);
		return false;
	}
}
