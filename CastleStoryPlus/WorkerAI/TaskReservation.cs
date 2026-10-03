using System.Collections.Generic;
using Brix.Game.AI;
using Brix.Game.Components;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryPlus.WorkerAI;

// A worker reserves the task it chooses, so no other worker is sent to the same task.
// The game only knows a task's workers once the work has started (Goal.WrapTask), while a worker's search for
// a task can take several ticks: idle workers searching at the same time all picked the same free task and
// walked to it together. Now the chosen task is reserved the moment the search ends; other searches skip it
// until the worker is registered on the task, gives up, or 15 s have passed.
// Exclusive tasks: every task the game already gives to one worker (digging, trees, harvesting, landscaping,
// stairs, repairs, demolishing...). A build blueprint that needs several items still takes several workers,
// but reserved workers count as workers on it (BuildJobs). Attacks are not reserved.
[Feature(Features.TaskReservation, Features.TaskReservationInfo)]
internal static class TaskReservation
{
	private class Reservation
	{
		public Goal Goal;

		public Labor Labor;

		public float Until;
	}

	private const float TimeoutSeconds = 15f;

	private static readonly Dictionary<Goal, List<Reservation>> ByGoal = new Dictionary<Goal, List<Reservation>>();

	private static readonly Dictionary<Labor, Reservation> ByLabor = new Dictionary<Labor, Reservation>();

	public static bool IsExclusive(Goal goal)
	{
		if (goal is AttackCharacterGoal)
		{
			return false;
		}
		return !(goal is BuildGoal build) || build.IsAnti;
	}

	public static void Reserve(Goal goal, Labor labor)
	{
		Release(labor);
		Reservation reservation = new Reservation
		{
			Goal = goal,
			Labor = labor,
			Until = Time.time + TimeoutSeconds
		};
		ByLabor[labor] = reservation;
		if (!ByGoal.TryGetValue(goal, out List<Reservation> list))
		{
			list = new List<Reservation>();
			ByGoal[goal] = list;
		}
		list.Add(reservation);
	}

	public static void Release(Labor labor)
	{
		if (labor == null || !ByLabor.TryGetValue(labor, out Reservation reservation))
		{
			return;
		}
		ByLabor.Remove(labor);
		if (ByGoal.TryGetValue(reservation.Goal, out List<Reservation> list))
		{
			list.Remove(reservation);
			if (list.Count == 0)
			{
				ByGoal.Remove(reservation.Goal);
			}
		}
	}

	// Workers other than this one that reserved the goal and are not registered on it yet.
	public static int OtherReservations(Goal goal, Labor labor)
	{
		if (goal == null || !ByGoal.TryGetValue(goal, out List<Reservation> list))
		{
			return 0;
		}
		int count = 0;
		for (int i = list.Count - 1; i >= 0; i--)
		{
			Reservation reservation = list[i];
			if (!IsValid(reservation))
			{
				Release(reservation.Labor);
				continue;
			}
			if (reservation.Labor != labor)
			{
				count++;
			}
		}
		return count;
	}

	// A reservation ends when it times out, when either side is gone, or once the game has the worker on the
	// goal (from then on the game's own worker list keeps others away).
	private static bool IsValid(Reservation reservation)
	{
		if (Time.time > reservation.Until || reservation.Labor.IsNullOrReleased() || reservation.Goal.IsNullOrReleased())
		{
			return false;
		}
		return !reservation.Goal.workers.Contains(reservation.Labor);
	}

	public static bool IsReservedByOther(Goal goal, Labor labor)
	{
		return IsExclusive(goal) && OtherReservations(goal, labor) > 0;
	}
}

// While searching: skip tasks another worker has reserved.
[Feature(Features.TaskReservation, Features.TaskReservationInfo)]
[HarmonyPatch(typeof(GoalSelector), nameof(GoalSelector.ProcessGoalAndReturnIsBest))]
internal static class TaskReservationSkipPatch
{
	private static bool Prefix(Labor labor, Goal goal, ref bool __result)
	{
		if (goal.IsNullOrReleased() || !TaskReservation.IsReservedByOther(goal, labor))
		{
			return true;
		}
		__result = false;
		return false;
	}
}

// When the search ends: reserve the chosen task, or drop it if another worker reserved it in the meantime
// (the worker then searches again).
[Feature(Features.TaskReservation, Features.TaskReservationInfo)]
[HarmonyPatch(typeof(GoalSelector), nameof(GoalSelector.GoalIsRejected))]
internal static class TaskReservationReservePatch
{
	private static void Postfix(Labor labor, Goal bestGoal, ref bool __result)
	{
		if (__result || labor.IsNullOrReleased() || bestGoal.IsNullOrReleased())
		{
			return;
		}
		if (TaskReservation.IsReservedByOther(bestGoal, labor))
		{
			__result = true;
			return;
		}
		TaskReservation.Reserve(bestGoal, labor);
	}
}

// A pooled worker that is reused has no reservation.
[Feature(Features.TaskReservation, Features.TaskReservationInfo)]
[HarmonyPatch(typeof(Labor), nameof(Labor.OnRequested))]
internal static class TaskReservationResetPatch
{
	private static void Prefix(Labor __instance)
	{
		TaskReservation.Release(__instance);
	}
}
