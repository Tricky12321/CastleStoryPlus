using System.Collections.Generic;
using BepInEx.Configuration;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Components;
using CastleStoryPlus.Core;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.WorkerAI;

// Failsafe for a worker that hangs in a task: one that has not moved for [StuckReset] Seconds (10) in a task that is
// a walk (going to, picking up from, running or walking towards something), or for LongSeconds (60) in any other
// task, has the task cancelled and puts down what it carries where it stands, then looks for work (or follows a
// waiting order) afresh. Workshop workers (who stand at their station) and fighters are left alone. Each reset is
// written to the log. Host only.
// The watch is kept per task goal (the blueprint, dig voxel, tree... the task was made from), not per task object (#143): a
// worker that cannot get to its task fails and takes a new task for the same goal again and again, standing where it
// is, and each new task started the watch afresh, so it never fired. Only a task that failed carries the watch over
// to the next one; a task done starts it afresh (a builder storing kind after kind into one blueprint). Short gaps
// between those tasks (no task for up to RecoveryRules.TaskGapSeconds) keep the watch; a reset that ends such a run
// marks the goal unreachable (UnreachableTasks), so the worker takes other work. A player's order to the worker starts
// the watch afresh.
[Feature(Features.StuckReset, Features.StuckResetInfo)]
internal sealed class StuckReset : MonoBehaviour
{
	private const float CheckSeconds = 1f;

	// Moving less than this (blocks) counts as standing still.
	private const float MoveTolerance = 0.5f;

	private sealed class Watch
	{
		public Vector3 Anchor;

		public float Since;

		// The task's goal (or target, or the task itself when it has neither).
		public object Key;

		public Task Task;

		// More than one task on the same goal stood still in, each one before the current having failed.
		public bool SeveralTasks;

		// One of those failed tasks failed on its walk (a way that cannot be found or followed).
		public bool WalkFailed;

		// When the worker last had a task (gaps between tasks of a retry loop keep the watch).
		public float LastSeen;
	}

	private static ConfigEntry<float> _seconds;

	private static ConfigEntry<float> _longSeconds;

	private static readonly Dictionary<Labor, Watch> Watches = new Dictionary<Labor, Watch>();

	private readonly List<Labor> _labors = new List<Labor>();

	private float _next;

	private static void Enable()
	{
		_seconds = Plugin.Cfg.Bind("StuckReset", "Seconds", 10f, new ConfigDescription("A worker that has not moved this long in a walking task has the task cancelled and puts down what it carries (0: off).", new AcceptableValueRange<float>(0f, 120f)));
		_longSeconds = Plugin.Cfg.Bind("StuckReset", "LongSeconds", 60f, new ConfigDescription("The same for any other task (building, digging...), which may stand still longer (0: off).", new AcceptableValueRange<float>(0f, 600f)));
		GameSession.OnLeave(Watches.Clear);
		Plugin.Root.AddComponent<StuckReset>();
	}

	private void Update()
	{
		if (!NetworkServer.active || Time.time < _next)
		{
			return;
		}
		_next = Time.time + CheckSeconds;
		Live<Labor>.Active(_labors);
		foreach (Labor labor in _labors)
		{
			try
			{
				Check(labor);
			}
			catch (System.Exception ex)
			{
				Plugin.Log.LogWarning("StuckReset: " + ex.Message);
				Watches.Remove(labor);
			}
		}
	}

	// A player's order: the watch starts afresh.
	internal static void Forget(Labor labor)
	{
		Watches.Remove(labor);
	}

	private static void Check(Labor labor)
	{
		// A worker digging out of a pit or in LoadRelief's store sequence is left alone: both have limits of their own.
		if (labor == null || labor.IsPhasedOut || labor.Operable != null || labor.Activity == Activity.Fighting || !Watched(labor) || StuckEscape.IsEscaping(labor) || (Plugin.IsEnabled(Features.LoadRelief) && LoadRelief.IsStoring(labor.CurrentTask)))
		{
			Watches.Remove(labor);
			return;
		}
		Task task = labor.CurrentTask;
		Vector3 at = labor.transform.position;
		Watches.TryGetValue(labor, out Watch watch);
		if (task == null)
		{
			// Between two tasks: a short gap keeps the watch (a retry loop), a longer one (the worker idles) ends it.
			if (watch != null && (RecoveryRules.GapEndsWatch(watch.LastSeen, Time.time) || Moved(watch, at)))
			{
				Watches.Remove(labor);
			}
			return;
		}
		object key = KeyOf(task);
		if (watch == null || watch.Key != key || Moved(watch, at))
		{
			Watches[labor] = new Watch { Anchor = at, Since = Time.time, Key = key, Task = task, LastSeen = Time.time };
			return;
		}
		watch.LastSeen = Time.time;
		if (watch.Task != task)
		{
			if (Succeeded(watch.Task))
			{
				// The task before was done: work goes on (a builder storing kind after kind into one blueprint), so
				// the watch starts afresh with the new task.
				Watches[labor] = new Watch { Anchor = at, Since = Time.time, Key = key, Task = task, LastSeen = Time.time };
				return;
			}
			watch.WalkFailed |= WalkFailed(watch.Task);
			watch.Task = task;
			watch.SeveralTasks = true;
		}
		float limit = RecoveryRules.StuckLimit(IsWalk(task), watch.SeveralTasks, _seconds.Value, _longSeconds.Value);
		if (limit <= 0f || Time.time - watch.Since < limit)
		{
			return;
		}
		Plugin.Log.LogInfo("StuckReset: " + labor.name + " stood still " + Mathf.RoundToInt(Time.time - watch.Since) + " s at " + Mathf.FloorToInt(at.x) + "," + Mathf.FloorToInt(at.y) + "," + Mathf.FloorToInt(at.z) + " in '" + task.Description + "'" + (watch.SeveralTasks ? " (tried again and again)" : string.Empty) + (labor.recepteur.IsEmpty() ? string.Empty : (" carrying " + labor.recepteur.ContentString())) + ": task cancelled, load put down");
		Watches.Remove(labor);
		if (watch.SeveralTasks && Plugin.IsEnabled(Features.UnreachableTasks))
		{
			// The worker kept going back to a task it never got to: it is passed by for a while. A blueprint whose
			// tasks failed for another reason (no material to be found) is not drawn unreachable.
			Goal goal = (key is GameObject giver && !giver.IsNullOrReleased()) ? giver.GetComponent<Goal>() : null;
			if (goal != null && RecoveryRules.MarksStoodStillGoal(watch.WalkFailed || WalkFailed(task), goal is BuildGoal))
			{
				UnreachableTasks.MarkWalkFailed(goal, "stood still " + Mathf.RoundToInt(Time.time - watch.Since) + " s trying it again and again (" + labor.name + ")", labor);
			}
		}
		labor.AbortWorkSignal.Invoke(TaskResult.ProjectCancelledTask);
		// Never on a stair, a climb or a quarry's floor: the worker keeps it there.
		if (!labor.recepteur.IsEmpty() && LoadRelief.CanDropHere(labor))
		{
			labor.recepteur.DropAll();
		}
	}

	// A task that ended done (not failed, cancelled or still running).
	private static bool Succeeded(Task task)
	{
		return task != null && task.Status == TaskProgression.Finished && task.result == TaskResult.Succes;
	}

	private static bool WalkFailed(Task task)
	{
		return task != null && (task.result == TaskResult.PathFailed || task.result == TaskResult.CouldNotReach);
	}

	private static bool Moved(Watch watch, Vector3 at)
	{
		return (at - watch.Anchor).sqrMagnitude > MoveTolerance * MoveTolerance;
	}

	// What a task is for: the goal it was made from (a blueprint, a dig voxel, a tree...), else the task itself. Tasks
	// given by a project or the idle round (no goal: wander, store, wait) are watched one by one, as before.
	private static object KeyOf(Task task)
	{
		GameObject giver = task.Giver;
		if (!giver.IsNullOrReleased() && giver.GetComponent<Goal>() != null)
		{
			return giver;
		}
		return task;
	}

	// The player's workers only.
	private static bool Watched(Labor labor)
	{
		Faction faction = Faction.GetFaction(labor.gameObject);
		return faction != null && !faction.isAI;
	}

	private static bool IsWalk(Task task)
	{
		string name = task.Description ?? string.Empty;
		return name.StartsWith("Go") || name.StartsWith("Run") || name.StartsWith("Walk");
	}
}
