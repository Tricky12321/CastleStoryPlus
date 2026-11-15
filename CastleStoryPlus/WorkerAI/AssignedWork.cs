using System.Collections.Generic;
using Brix.Game.AI;
using Brix.Game.AI.InstructionType;
using Brix.Game.Components;
using Brix.Utils;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryPlus.WorkerAI;

// A worker the player put on a task (LockedToOperable) stays with it while the task has work. The game asks the idle
// task for such a worker whenever its own task has nothing for it at that moment (its goals all taken by the rest of
// the crew, a tree being walked to, a block waiting for the one under it): the idle task then sent it to the gather
// point, and the mod's idle chores sent it after loose logs (issue #81), into stockpiles or to repairs. On a big quarry
// with a full crew the workers drifted off one by one (issue #86). Now, while its task still has a goal left, the
// worker waits where it is and takes the task's next goal; only a task with nothing left to do lets it go to the idle
// task (and the idle chores are for workers on no task at all).
internal static class AssignedWork
{
	private const float CacheSeconds = 1f;

	private static readonly Dictionary<Project, KeyValuePair<float, bool>> Cache = new Dictionary<Project, KeyValuePair<float, bool>>();

	private static readonly List<Goal> Goals = new List<Goal>();

	// On a task of the player's choosing (not the idle task, not a raid).
	internal static bool IsAssigned(Labor labor)
	{
		return labor != null && labor.Autonomy == AutonomyStatus.LockedToOperable && labor.Project != null && !(labor.Project is IdleProject) && !(labor.Project is RaidProject2) && labor.Project.IsStillAlive;
	}

	// The mod's idle chores are for workers on no task (or on the idle task).
	internal static bool IdleChoresFor(Labor labor)
	{
		return labor != null && (labor.Project == null || labor.Project is IdleProject);
	}

	// Whether the task still has a goal left that is valid and not marked unreachable (looked up at most once a second).
	internal static bool HasWork(Project project)
	{
		if (project == null || project.IsNullOrReleased())
		{
			return false;
		}
		float now = Time.time;
		if (Cache.TryGetValue(project, out KeyValuePair<float, bool> cached) && now - cached.Key < CacheSeconds)
		{
			return cached.Value;
		}
		bool work = false;
		Goals.Clear();
		foreach (IGoalProvider provider in project.GetGoalProviders())
		{
			provider?.GetAll(Goals);
		}
		foreach (Goal goal in Goals)
		{
			if (goal != null && !goal.IsNullOrReleased() && goal.IsValid() && !UnreachableTasks.IsMarked(goal))
			{
				work = true;
				break;
			}
		}
		Goals.Clear();
		if (Cache.Count > 256)
		{
			Cache.Clear();
		}
		Cache[project] = new KeyValuePair<float, bool>(now, work);
		return work;
	}
}

// The idle task asked for a worker put on a task that still has work: it waits where it is.
[Feature(Features.WorkerAI, Features.WorkerAIInfo)]
[HarmonyPatch(typeof(IdleProject), nameof(IdleProject._GetInstruction))]
internal static class AssignedWorkerWaitsPatch
{
	[HarmonyPriority(Priority.VeryHigh)]
	private static bool Prefix(IdleProject __instance, Labor labor, Var<OrderPackage<Labor>> result, bool __runOriginal)
	{
		// The first seconds after a task's order the idle task gives nothing anyway (the game's own wait).
		if (!__runOriginal || !AssignedWork.IsAssigned(labor) || !labor.recepteur.IsEmpty() || !__instance.GatherAtIdlePoint(labor) || !AssignedWork.HasWork(labor.Project))
		{
			return __runOriginal;
		}
		// The idle task's own sit, where the worker stands (not at the gather point); it ends after a moment and the
		// worker looks for work in its task again. Interruptible, so the task's work always wins.
		OrderPackage<Labor> package = Order.Package.Of(Order.ForLabor.Sit, Labor.DecisionSource.Project);
		package.SetPrepare((BaseLabor worker) => __instance.Prepare(worker as Labor, false, Activity.Chilling));
		result.Set(package);
		return false;
	}
}
