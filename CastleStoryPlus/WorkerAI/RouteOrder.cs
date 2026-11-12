using System.Collections.Generic;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.AI.KnowledgeSpace;
using Brix.Game.Components;
using CastleStoryPlus.Core;
using HarmonyLib;
using Motus.Behavior;
using UnityEngine;

namespace CastleStoryPlus.WorkerAI;

// A harvest area's and a cleanup area's tasks by walking distance (#69). The game went through a harvest area's logs
// in no particular order and took the nearer (as the crow flies) of the first two it could pick up; a cleanup area
// took the first item it could. So a worker ran down a hill and round the slope for a log far off while logs and
// trees next to it waited. Now the tasks come nearest first by the worker's walk to them (RouteDistance), logs and
// other loose items before trees and plants unless a tree is FellExtra blocks of walking nearer, and their priority
// is the walking cost too, so the game's choice among them agrees.
[Feature(Features.WorkerAI, Features.WorkerAIInfo)]
[HarmonyPatch]
internal static class RouteOrder
{
	// Blocks of walking a tree or a plant must be nearer than a loose item for the worker to fell it first: loose
	// logs are picked up before more trees are felled, as the game meant, but not from across the map.
	private const int FellExtra = 12;

	private static readonly List<KeyValuePair<long, Goal>> Ranked = new List<KeyValuePair<long, Goal>>();

	private static readonly System.Comparison<KeyValuePair<long, Goal>> LowestFirst = (KeyValuePair<long, Goal> a, KeyValuePair<long, Goal> b) => a.Key.CompareTo(b.Key);

	private static void Enable()
	{
		GameSession.OnLeave(RouteDistance.Clear);
	}

	[HarmonyPatch(typeof(HarvestProject), "SelectBestGoal")]
	[HarmonyPrefix]
	private static bool HarvestSelect(HarvestProject __instance, Labor labor, Var<Goal> result, ref Node __result)
	{
		return Select(__instance, labor, result, ref __result);
	}

	[HarmonyPatch(typeof(CleanupProject), "SelectBestGoal")]
	[HarmonyPrefix]
	private static bool CleanupSelect(CleanupProject __instance, Labor labor, Var<Goal> result, ref Node __result)
	{
		return Select(__instance, labor, result, ref __result);
	}

	private static bool Select(Project project, Labor labor, Var<Goal> result, ref Node __result)
	{
		List<Goal> goals = new List<Goal>();
		foreach (IGoalProvider provider in project.GetGoalProviders())
		{
			provider?.GetAll(goals);
		}
		goals.RemoveAll((Goal goal) => goal.IsNullOrReleased() || !goal.IsValid());
		if (!Sort(labor, goals))
		{
			return true;
		}
		__result = project.SelectBestGoalPooledList(goals.GetEnumerator(), labor, result);
		return false;
	}

	// The goals nearest first by the worker's walk to them (loose items before trees and plants, see FellExtra), in
	// place; false (and the list as it was) when the worker stands on no place of the route graph.
	internal static bool Sort(Labor labor, List<Goal> goals)
	{
		if (goals.Count == 0)
		{
			return true;
		}
		RouteDistance.Field field = RouteDistance.For(labor);
		if (field == null)
		{
			return false;
		}
		Vector3 from = labor.transform.position;
		Ranked.Clear();
		for (int i = 0; i < goals.Count; i++)
		{
			Goal goal = goals[i];
			// The index keeps the game's order between equals.
			long key = ((long)Rank(field, from, goal) << 20) | (long)i;
			Ranked.Add(new KeyValuePair<long, Goal>(key, goal));
		}
		Ranked.Sort(LowestFirst);
		goals.Clear();
		foreach (KeyValuePair<long, Goal> entry in Ranked)
		{
			goals.Add(entry.Value);
		}
		Ranked.Clear();
		return true;
	}

	internal static bool Ordered(Project project)
	{
		return project is HarvestProject || project is CleanupProject;
	}

	private static int Rank(RouteDistance.Field field, Vector3 from, Goal goal)
	{
		int rank = RouteDistance.Rank(field, from, goal.transform.position);
		return (goal is TreeGoal || goal is NatureHarvestGoal) ? rank + FellExtra : rank;
	}

	// The priority the game compares tasks by, with the walking cost in place of the straight line (only while the
	// worker's field from this search is there).
	private static bool Walked(Labor labor, Goal goal, out int rank)
	{
		rank = 0;
		RouteDistance.Field field = RouteDistance.Current(labor);
		if (field == null || goal == null)
		{
			return false;
		}
		rank = Rank(field, labor.transform.position, goal);
		return true;
	}

	[HarmonyPatch(typeof(CleanUpGoal), nameof(CleanUpGoal.PriorityFor), new[] { typeof(Labor), typeof(float) }, new[] { ArgumentType.Normal, ArgumentType.Ref })]
	[HarmonyPostfix]
	private static void CleanUpPriority(CleanUpGoal __instance, Labor labor, ref int __result)
	{
		if (Walked(labor, __instance, out int rank))
		{
			__result = -rank - (Knowledge.Instance.IsReserved(__instance.gameObject) ? 20 : 0);
		}
	}

	[HarmonyPatch(typeof(TreeGoal), nameof(TreeGoal.PriorityFor), new[] { typeof(Labor), typeof(float) }, new[] { ArgumentType.Normal, ArgumentType.Ref })]
	[HarmonyPostfix]
	private static void TreePriority(TreeGoal __instance, Labor labor, ref int __result)
	{
		if (Walked(labor, __instance, out int rank))
		{
			__result = -rank - ((Knowledge.Instance.IsReserved(__instance.gameObject) || __instance.HasOtherWorkerAssigned(labor)) ? 100 : 0);
		}
	}

	[HarmonyPatch(typeof(NatureHarvestGoal), nameof(NatureHarvestGoal.PriorityFor), new[] { typeof(Labor), typeof(float) }, new[] { ArgumentType.Normal, ArgumentType.Ref })]
	[HarmonyPostfix]
	private static void PlantPriority(NatureHarvestGoal __instance, Labor labor, ref int __result)
	{
		if (Walked(labor, __instance, out int rank))
		{
			__result = -rank;
		}
	}
}
