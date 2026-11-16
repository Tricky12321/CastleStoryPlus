using System.Collections.Generic;
using Brix.Game.AI;
using Brix.Game.Components;
using UnityEngine;

namespace CastleStoryPlus.WorkerAI;

// A worker the player put on a task (LockedToOperable) stays with it while the task has work. The game asks the idle
// task for such a worker whenever its own task has nothing for it at that moment (its goals all taken by the rest of
// the crew, a tree being walked to, a block waiting for storage), and the mod's idle chores then sent it after loose
// logs (issue #81), into stockpiles or to repairs; the mod's help for other task groups took it into another group
// while its own still had work. On a big quarry the crew drifted off one by one (issue #86). The idle chores are now
// for workers on no task only (IdleChoresFor), and a worker helps another group only when its own has no goal left
// (HasWork, HelpOtherGroupsPatch).
internal static class AssignedWork
{
	private const float CacheSeconds = 1f;

	private static readonly Dictionary<Project, KeyValuePair<float, bool>> Cache = new Dictionary<Project, KeyValuePair<float, bool>>();

	private static readonly List<Goal> Goals = new List<Goal>();

	// The mod's idle chores are for workers on no task (or on the idle task).
	internal static bool IdleChoresFor(Labor labor)
	{
		return labor != null && (labor.Project == null || labor.Project is IdleProject);
	}

	// Whether the task still has a valid goal left (looked up at most once a second). Goals marked unreachable count too:
	// the marks come from a route map a few seconds old, and while a quarry is dug its last blocks are often marked for
	// a moment.
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
			if (goal != null && !goal.IsNullOrReleased() && goal.IsValid())
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
