using System.Collections.Generic;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Pathfinding.Test;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryPlus.WorkerAI;

// A worker that finishes a task of a work area goes on to the area's next task at once. The game let it stand
// "available" for 1 to 1.5 seconds after every block dug, tree felled or item cleaned up: its search for the next task
// walks through every project, waits a tick per project and per resource it checks, and shares a small time budget
// per frame with every other worker's search. Now the moment a task of a quarry, tunnel, dig, flatten, harvest, cleanup or build
// area ends well, the area's next task is chosen right there with the game's own choice (GoalSelector, so the mod's
// reservations and reach checks count too) and given to the worker as its next task. When the area has nothing the
// worker can do, the game's search goes on as before. A free worker goes by priority over every area: it stays in an
// area only while the game's own search chose that area for it in the last RecheckSeconds (that search goes through
// the areas by priority, so nothing better had work for it); then the game searches once more. A build task's next blueprint
// comes from the build task's own search (what the worker can fetch, top priority first), run in one go. A workshop's
// worker gets its next step from the workshop at once (fetch ingredients, work, take out what is made). The worker trace logs each prediction. Host only, the player's workers.
[Feature(Features.TaskPrediction, Features.TaskPredictionInfo)]
[HarmonyPatch(typeof(Task), "OnFinally")]
internal static class TaskPrediction
{
	// Most tasks one prediction looks at; larger areas leave it to the game's time-sliced search.
	private const int MaxGoals = 1500;

	// Most steps of one search (a build search's route search steps included).
	private const int MaxSteps = 5000;

	// Most CPU time one search may take; a longer one is left to the game's search spread over frames.
	private const double MaxMilliseconds = 4.0;

	private static readonly System.Diagnostics.Stopwatch Clock = new System.Diagnostics.Stopwatch();

	private static readonly List<Goal> Goals = new List<Goal>();

	// How long a free worker keeps to the area the game's search chose for it before the game searches again.
	private const float RecheckSeconds = 30f;

	// The area the game's own search last gave each worker a task of, and when.
	private static readonly Dictionary<Labor, KeyValuePair<Project, float>> SearchChose = new Dictionary<Labor, KeyValuePair<Project, float>>();

	// The area each worker last finished a task of, and when (its follow-up tasks lead back there).
	private static readonly Dictionary<Labor, KeyValuePair<Project, float>> LastArea = new Dictionary<Labor, KeyValuePair<Project, float>>();

	// A quarry's or dig area's blocks by the priority the game gives them (DigGoal.PriorityFor), highest first.
	private static readonly List<KeyValuePair<long, Goal>> Ranked = new List<KeyValuePair<long, Goal>>();

	private static readonly System.Comparison<KeyValuePair<long, Goal>> HighestFirst = (a, b) => b.Key.CompareTo(a.Key);

	private static bool _busy;

	// The task each worker was last given by a prediction (RouteView colours its route).
	private static readonly Dictionary<Labor, Task> Predicted = new Dictionary<Labor, Task>();

	// A predicted task that has not started by then never will (see PredictionWatch).
	private const float StartSeconds = 2f;

	// When each predicted task was given.
	private static readonly Dictionary<Task, float> Given = new Dictionary<Task, float>();

	private static void Enable()
	{
		GameSession.OnLeave(() =>
		{
			Predicted.Clear();
			SearchChose.Clear();
			LastArea.Clear();
			Given.Clear();
		});
		Plugin.Root.AddComponent<PredictionWatch>();
	}

	// A predicted task is given while the worker's last task is still ending (Task.OnFinally). Now and then (about 1 in
	// 20 times after a worker's idle walk in a build area) the worker's behaviour takes it on as its current task and
	// never starts it: it stands idle for good, and as the blueprint counts it as its worker, no other worker takes the
	// blueprint either (the last blueprint of a build area was left unbuilt). A predicted task still not started after
	// StartSeconds is let go as the game ends a task: the blueprint no longer counts the worker, and the worker is free
	// for the game's search.
	private sealed class PredictionWatch : MonoBehaviour
	{
		private float _next;

		private readonly List<Task> _done = new List<Task>();

		private void Update()
		{
			if (Given.Count == 0 || Time.time < _next)
			{
				return;
			}
			_next = Time.time + 0.5f;
			_done.Clear();
			foreach (KeyValuePair<Task, float> entry in Given)
			{
				Task task = entry.Key;
				Labor labor = task.Worker as Labor;
				if (labor == null || labor.IsNullOrReleased() || task.TaskStartedTimestamp > 0f || (labor.CurrentTask != task && labor.PendingTask != task))
				{
					_done.Add(task);
					continue;
				}
				if (Time.time - entry.Value < StartSeconds || labor.CurrentTask != task)
				{
					continue;
				}
				_done.Add(task);
				Goal goal = (task.Giver != null) ? task.Giver.GetComponent<Goal>() : null;
				Plugin.Log.LogInfo("TaskPrediction: " + labor.name + " never started its predicted task " + task.Description + "; let go");
				Trace(labor, "next task: the predicted task never started, let go");
				if (goal != null)
				{
					goal.UnassignWorker(task);
				}
				labor.CurrentTask = null;
			}
			foreach (Task task in _done)
			{
				Given.Remove(task);
			}
		}
	}

	internal static bool IsPredicted(Labor labor)
	{
		return labor != null && Predicted.TryGetValue(labor, out Task task) && task != null && (labor.CurrentTask == task || labor.PendingTask == task);
	}

	private static void Prefix(Task __instance)
	{
		if (_busy || __instance.result != TaskResult.Succes || !(__instance.Worker is Labor labor) || labor.IsNullOrReleased())
		{
			return;
		}
		if (labor.faction == null || labor.faction.isAI || !UnityEngine.Networking.NetworkServer.active)
		{
			return;
		}
		if (__instance.InitialPackage == null || __instance.InitialPackage.DecisionSource != Labor.DecisionSource.Project)
		{
			if (__instance.InitialPackage != null && __instance.InitialPackage.DecisionSource == Labor.DecisionSource.Unspecified)
			{
				AfterFollowUp(labor);
				return;
			}
			Trace(labor, "next task: not predicted, the task came from " + ((__instance.InitialPackage != null) ? __instance.InitialPackage.DecisionSource.ToString() : "nowhere"));
			return;
		}
		// A workshop's worker: the workshop decides at once what comes next (fetch, work, take out what is made).
		if (labor.Operable is CraftingStation station && station.Operator == labor)
		{
			if (labor.PendingTask == null)
			{
				Guarded(() => PredictWorkshop(labor, station));
			}
			return;
		}
		GameObject target = (__instance.Target != null) ? __instance.Target : __instance.Giver;
		Goal finished = (target != null) ? target.GetComponent<Goal>() : null;
		Project project = (finished != null && finished.project != null) ? finished.project : labor.Project;
		bool predicted = Predicted.TryGetValue(labor, out Task last) && last == __instance;
		if (!predicted && project != null)
		{
			SearchChose[labor] = new KeyValuePair<Project, float>(project, (__instance.TaskStartedTimestamp > 0f) ? __instance.TaskStartedTimestamp : Time.time);
		}
		if (project != null)
		{
			LastArea[labor] = new KeyValuePair<Project, float>(project, Time.time);
		}
		if (!Predictable(project))
		{
			Trace(labor, "next task: not predicted for " + ((project != null) ? project.GetType().Name : "no task area"));
			return;
		}
		if (labor.PendingTask != null)
		{
			Trace(labor, "next task: already has one (" + labor.PendingTask.Description + ")");
			return;
		}
		_busy = true;
		try
		{
			Predict(labor, project);
		}
		catch (System.Exception ex)
		{
			Plugin.Log.LogWarning("TaskPrediction: " + ex.Message);
		}
		finally
		{
			_busy = false;
		}
	}

	// A task the game gave on its own after an area's task (taking what was dug or felled to storage): the worker goes
	// back to that area's next task, as after the area's own task.
	private static void AfterFollowUp(Labor labor)
	{
		if (!LastArea.TryGetValue(labor, out KeyValuePair<Project, float> last) || Time.time - last.Value > RecheckSeconds)
		{
			Trace(labor, "next task: not predicted, the task came from Unspecified");
			return;
		}
		Project project = last.Key;
		if (!Predictable(project) || project is BuildProject)
		{
			Trace(labor, "next task: not predicted after a follow-up for " + ((project != null) ? project.GetType().Name : "no task area"));
			return;
		}
		if (labor.PendingTask != null)
		{
			Trace(labor, "next task: already has one (" + labor.PendingTask.Description + ")");
			return;
		}
		Guarded(() => Predict(labor, project));
	}

	private static void Guarded(System.Action action)
	{
		_busy = true;
		try
		{
			action();
		}
		catch (System.Exception ex)
		{
			Plugin.Log.LogWarning("TaskPrediction: " + ex.Message);
		}
		finally
		{
			_busy = false;
		}
	}

	// As the game asks a workshop for its worker's next task (CraftingStation.GetInstruction, AssignInstruction).
	private static void PredictWorkshop(Labor labor, CraftingStation station)
	{
		string name = station.GetType().Name;
		if (!station.IsStillAlive || !station.HasWorkFor(labor))
		{
			Trace(labor, "next task: nothing to do at " + name + ", the game searches");
			return;
		}
		CraftingStation.Actions action = station.DecideWhatToDo(labor);
		OrderPackage<Labor> package = station.GetPackage(action);
		if (package == null || package == OrderPackage<Labor>.Empty || package.Instruction == null)
		{
			Trace(labor, "next task: nothing to do at " + name + ", the game searches");
			return;
		}
		if (!station.Hire(labor))
		{
			Trace(labor, "next task: " + name + " would not take it on");
			return;
		}
		package.Send(labor);
		Predicted[labor] = labor.PendingTask;
		Trace(labor, "next task predicted: " + action + " at " + name);
	}

	private static bool Predictable(Project project)
	{
		return project != null && project.IsStillAlive
			&& (project is MineProject || project is TunnelProject || project is LandscapeProject || project is HarvestProject || project is CleanupProject || project is BuildProject);
	}

	private static void Predict(Labor labor, Project project)
	{
		if (!project.ReadyToAssignGoals() || !project.HasWorkFor(labor))
		{
			Trace(labor, "next task: " + project.GetType().Name + " has no work for it");
			return;
		}
		if (!KeepsToArea(labor, project))
		{
			Trace(labor, "next task: the game searches again (priorities)");
			return;
		}
		GoalSelector selector = project.goalSelector;
		if (selector == null)
		{
			return;
		}
		IEnumerator<Goal> candidates = Candidates(labor, project, out bool firstMatchIsBest);
		if (candidates == null)
		{
			return;
		}
		GoalSelector.GoalSelectionData data = GoalSelector.GoalSelectionData.Get();
		Goal best;
		int checkedGoals = 0;
		try
		{
			// A build search yields null where the game would wait a tick (its route search is spread over ticks):
			// here it just goes on, up to MaxSteps.
			Clock.Reset();
			Clock.Start();
			for (int step = 0; step < MaxSteps && candidates.MoveNext(); step++)
			{
				if (Clock.Elapsed.TotalMilliseconds > MaxMilliseconds)
				{
					Trace(labor, "next task: the search in " + project.GetType().Name + " takes too long (" + checkedGoals + " tasks checked), the game searches");
					return;
				}
				Goal goal = candidates.Current;
				if (goal == null || goal.IsNullOrReleased() || !goal.IsValid())
				{
					continue;
				}
				checkedGoals++;
				if (selector.ProcessGoalAndReturnIsBest(labor, goal, project, data))
				{
					break;
				}
				// The blocks come in the order of their priority: the first the worker can dig is the game's choice.
				if (firstMatchIsBest && data.BestGoal != null)
				{
					break;
				}
			}
			best = data.BestGoal;
			// Reserves the goal for this worker (TaskReservation), as the game's search does when it ends.
			if (best == null || selector.GoalIsRejected(labor, best, data.foundUnmapped))
			{
				Trace(labor, "next task: nothing in " + project.GetType().Name + " for it, the game searches");
				return;
			}
			selector.errorStore.Reset(labor);
		}
		finally
		{
			data.Dispose();
			candidates.Dispose();
		}
		// As the game gives a project's task (Project.GetInstruction, AssignInstruction).
		if (!project.GetPrepareFunction(best)(labor))
		{
			Trace(labor, "next task: " + project.GetType().Name + " would not take it on");
			return;
		}
		best.Package(Labor.DecisionSource.Project).Send(labor);
		Predicted[labor] = labor.PendingTask;
		if (labor.PendingTask != null)
		{
			Given[labor.PendingTask] = Time.time;
		}
		Trace(labor, "next task predicted: " + best.name + " in " + project.GetType().Name + " (" + checkedGoals + " tasks checked, " + Clock.Elapsed.TotalMilliseconds.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " ms)");
	}

	// The tasks the project's own search would look at, in its order: a build task's blueprints the worker can bring
	// something to (with the blueprints given top priority first, BuildPriority), the other areas' tasks.
	private static IEnumerator<Goal> Candidates(Labor labor, Project project, out bool firstMatchIsBest)
	{
		firstMatchIsBest = false;
		if (project is BuildProject build)
		{
			TestPathfinding pathfinding = TestPathfinding.Instance;
			if (pathfinding == null || pathfinding.DirtyVoxels.Count > 0 || pathfinding.QueuedVoxels.Count > 0)
			{
				Trace(labor, "next task: routes are being updated, the game searches");
				return null;
			}
			List<BuildGoal> first = Building.BuildPriority.Of(build);
			BuildGoalProvider.FulfillableEnumerator rest = build.goals.FulfillableBy(labor);
			return (first != null) ? new Building.PriorityFirstEnumerator(first, rest) : (IEnumerator<Goal>)rest;
		}
		Goals.Clear();
		foreach (IGoalProvider provider in project.GetGoalProviders())
		{
			provider?.GetAll(Goals);
		}
		if (Goals.Count == 0 || Goals.Count > MaxGoals)
		{
			Trace(labor, "next task: " + Goals.Count + " tasks in " + project.GetType().Name + ", left to the game");
			return null;
		}
		if (project.goalSelector.SelectorType == GoalSelector.Type.CompareAll && RankBlocks(labor))
		{
			firstMatchIsBest = true;
			var ranked = new List<Goal>(Ranked.Count);
			foreach (KeyValuePair<long, Goal> entry in Ranked)
			{
				ranked.Add(entry.Value);
			}
			Ranked.Clear();
			return ranked.GetEnumerator();
		}
		return new List<Goal>(Goals).GetEnumerator();
	}

	// A quarry's search compares every block (CompareAll): highest layer first, then the nearest one the worker can
	// reach (DigGoal.PriorityFor). Every block costs a reach check and a check for storage, the slow part of the search.
	// With the blocks in the order of that priority the first block the worker can dig is the one the whole search would
	// choose, so the search ends there. Only when every task is a plain DigGoal (whose priority is known). Blocks marked
	// unreachable (UnreachableTasks) come after all others, as the game's search only takes one when nothing else would do.
	private static bool RankBlocks(Labor labor)
	{
		Ranked.Clear();
		Vector3 position = labor.transform.position;
		foreach (Goal goal in Goals)
		{
			if (goal == null || goal.GetType() != typeof(DigGoal))
			{
				Ranked.Clear();
				return false;
			}
			var dig = (DigGoal)goal;
			Vector3 at = dig.transform.position;
			long priority = -(int)Vector3.Distance(position, at) + (dig.UseHeightAdjustment ? (int)at.y * 10000 : 0);
			if (UnreachableTasks.IsMarked(goal))
			{
				priority -= 1L << 40;
			}
			Ranked.Add(new KeyValuePair<long, Goal>(priority, goal));
		}
		Ranked.Sort(HighestFirst);
		return true;
	}

	private static void Trace(Labor labor, string text)
	{
		if (Diagnostics.WorkerTrace.Active)
		{
			Diagnostics.WorkerTrace.Log(labor.name, text);
		}
	}

	// A worker of the area's crew keeps to it. A free worker only while the game's own search chose this area for it
	// a short while ago.
	private static bool KeepsToArea(Labor labor, Project project)
	{
		if (labor.Autonomy != AutonomyStatus.FreeAgent)
		{
			return true;
		}
		return SearchChose.TryGetValue(labor, out KeyValuePair<Project, float> chose) && chose.Key == project && Time.time - chose.Value <= RecheckSeconds;
	}
}
