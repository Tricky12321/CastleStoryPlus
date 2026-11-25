using System.Collections.Generic;
using Brix.Engine;
using Brix.Game.AI;
using Brix.Game.Components;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.WorkerAI;

// A bricktron put on a task the moment that task ends froze for good (issue #199, a co-op Invasion game). A task that
// ends (built, emptied, deleted, or deleted by its player's client when its last worker left) goes to the game's pool:
// its game objects are switched off and parked there, and its crew is sent to the free workers first. Project.Hire does
// not ask whether the task is still there, and joining a crew moves the bricktron's game object into the task's crew
// holder. A bricktron that joined a task already gone (the game's own search hires a worker into the task it chose
// ticks before, when it starts on the chosen goal; mod code holding on to a task does the same), or one that joined
// while the task was sending its crew away, went into the pool with the task: switched off, without a task, taking no
// orders; the other players kept seeing it frozen where it stood. Now:
// - a task that is gone takes no one into its crew (Hire answers false, as for a full task), and the bricktron goes to
//   the free workers when it has no task left;
// - a bricktron still in the crew holder of a task that has just been sent to the pool is sent to the free workers
//   there and then, where it stands (the game only sent those it had counted in the crew; the players' bricktrons);
// - every few seconds the host looks for a bricktron left in the crew holder of a task that is gone, and for a
//   bricktron whose current task, never started, came from a task that is gone, and frees it (the players' bricktrons).
// Always on (no switch): a task that is gone can never keep a bricktron. Host only, except the Hire check, which also
// keeps a client's copy of a bricktron out of a task that is gone.
[Feature]
internal sealed class VanishedTask : MonoBehaviour
{
	private static readonly AccessTools.FieldRef<Project, WorkForceProvider> ProviderField = AccessTools.FieldRefAccess<Project, WorkForceProvider>("workforceProvider");

	private static readonly AccessTools.FieldRef<WorkForceProvider, Transform> HolderField = AccessTools.FieldRefAccess<WorkForceProvider, Transform>("CharactersHolder");

	private static readonly List<Labor> Labors = new List<Labor>();

	// Bricktrons whose current task, never started, came from a task that is gone at the last check.
	private static readonly Dictionary<Labor, Task> UnstartedSeen = new Dictionary<Labor, Task>();

	private static readonly Dictionary<Labor, Task> UnstartedNow = new Dictionary<Labor, Task>();

	private float _next;

	private static void Enable()
	{
		GameSession.OnLeave(UnstartedSeen.Clear);
		Plugin.Root.AddComponent<VanishedTask>();
	}

	// Whether the task is still there to take a bricktron into its crew.
	internal static bool MayJoin(Project project)
	{
		if (project == null)
		{
			return false;
		}
		WorkForceProvider provider = ProviderField(project);
		return VanishedTaskRules.MayJoin(!project.IsStillAlive, provider == null || provider.IsNullOrReleased());
	}

	// Project.Hire on a task that is gone: refused. The bricktron goes to the free workers when it has no live task.
	internal static void Refused(Project project, Labor labor)
	{
		if (labor == null || labor.IsNullOrReleased())
		{
			return;
		}
		Plugin.Log.LogInfo("VanishedTask: " + labor.name + " was not put on " + ((project != null) ? project.name : "a task") + ", the task is gone");
		if (!NetworkServer.active)
		{
			return;
		}
		Project current = labor.Project;
		if (current == null || !MayJoin(current))
		{
			labor.Autonomy = labor.Autonomy.OnFiredFromOperable;
			labor.AssignToIdle();
		}
	}

	// Server: the task went to the pool (WorkForceProvider.ResetComponent); the game sent the crew it counted to the
	// free workers. One still in the crew holder (it joined while the crew was sent away) goes now, where it stands.
	internal static void CrewHolderReset(WorkForceProvider provider)
	{
		if (!NetworkServer.active || Architecte.termine || provider == null)
		{
			return;
		}
		Transform holder = HolderField(provider);
		if (holder == null)
		{
			return;
		}
		List<Labor> left = null;
		for (int i = holder.childCount - 1; i >= 0; i--)
		{
			Labor labor = holder.GetChild(i).GetComponent<Labor>();
			if (labor != null && !labor.IsNullOrReleased() && labor.faction != null && !labor.faction.isAI)
			{
				(left ?? (left = new List<Labor>())).Add(labor);
			}
		}
		if (left == null)
		{
			return;
		}
		foreach (Labor labor in left)
		{
			Free(labor, "was still in the crew of " + provider.name + " as the task ended");
		}
	}

	private void Update()
	{
		if (!NetworkServer.active || Time.time < _next)
		{
			return;
		}
		_next = Time.time + VanishedTaskRules.CheckSeconds;
		try
		{
			Check();
		}
		catch (System.Exception ex)
		{
			Plugin.Log.LogWarning("VanishedTask: " + ex.Message);
		}
	}

	private static void Check()
	{
		Live<Labor>.All(Labors);
		UnstartedNow.Clear();
		foreach (Labor labor in Labors)
		{
			if (labor.IsNullOrReleased() || labor.faction == null || labor.faction.isAI)
			{
				continue;
			}
			Project holding = HoldingTask(labor.transform);
			bool inGoneTask = holding != null && !MayJoin(holding);
			if (VanishedTaskRules.Stranded(true, labor.IsPhasedOut, inGoneTask))
			{
				Free(labor, "was left in the crew of " + holding.name + ", a task that is gone");
				continue;
			}
			if (!labor.gameObject.activeInHierarchy)
			{
				continue;
			}
			CheckUnstarted(labor);
		}
		UnstartedSeen.Clear();
		foreach (KeyValuePair<Labor, Task> entry in UnstartedNow)
		{
			UnstartedSeen[entry.Key] = entry.Value;
		}
		Labors.Clear();
	}

	// A current task the bricktron never started, from a task that is gone: let go of at the second check in a row
	// that finds it so (the game's search hires and gives the task in one go, so a task area gone in between leaves a
	// task that never starts and is never cancelled).
	private static void CheckUnstarted(Labor labor)
	{
		Task task = labor.CurrentTask;
		if (task == null)
		{
			return;
		}
		bool started = task.TaskStartedTimestamp > 0f;
		bool areaGone = !started && AreaGone(task);
		if (!areaGone)
		{
			return;
		}
		bool seenBefore = UnstartedSeen.TryGetValue(labor, out Task seen) && seen == task;
		if (!VanishedTaskRules.LetGoUnstarted(started, areaGone, seenBefore))
		{
			UnstartedNow[labor] = task;
			return;
		}
		TaskPrediction.LetGoUnstarted(labor, "its task is gone (#199)");
		Project current = labor.Project;
		if (current == null || !MayJoin(current))
		{
			labor.Autonomy = labor.Autonomy.OnFiredFromOperable;
			labor.AssignToIdle();
		}
	}

	// Whether the task the bricktron's task came from is gone: the goal itself or the task area holding it.
	private static bool AreaGone(Task task)
	{
		GameObject giver = task.Giver;
		if ((object)giver == null)
		{
			return false;
		}
		if (giver == null || giver.IsNullOrReleased())
		{
			return true;
		}
		Project project = giver.GetComponent<Project>();
		if (project == null)
		{
			Goal goal = giver.GetComponent<Goal>();
			project = (goal != null) ? goal.project : null;
		}
		return (object)project != null && !MayJoin(project);
	}

	// The task whose crew holder the bricktron's game object sits in: the first task above it, null when none.
	private static Project HoldingTask(Transform transform)
	{
		Transform parent = transform.parent;
		while (parent != null)
		{
			Project project = parent.GetComponent<Project>();
			if (project != null)
			{
				return project;
			}
			parent = parent.parent;
		}
		return null;
	}

	// The bricktron lets go of what it was given and joins the free workers (the game's own move out of a crew when a
	// task ends: WorkForceProvider.ResetComponent).
	private static void Free(Labor labor, string why)
	{
		Plugin.Log.LogWarning("VanishedTask: " + labor.name + " " + why + ": sent to the free workers (#199)");
		TaskPrediction.LetGoUnstarted(labor, "its task is gone (#199)");
		labor.Autonomy = labor.Autonomy.OnFiredFromOperable;
		labor.AssignToIdle();
		if (labor.CurrentTask != null && labor.CurrentTask.CanInterrupt)
		{
			labor.AbortWorkSignal.Invoke(TaskResult.ProjectCancelledTask);
		}
	}
}

// A task that is gone takes no one into its crew.
[Feature]
[HarmonyPatch(typeof(Project), nameof(Project.Hire))]
internal static class VanishedTaskHirePatch
{
	private static bool Prefix(Project __instance, Labor labor, ref bool __result)
	{
		if (VanishedTask.MayJoin(__instance))
		{
			return true;
		}
		__result = false;
		VanishedTask.Refused(__instance, labor);
		return false;
	}
}

// The task went to the pool: a bricktron still in its crew holder goes to the free workers.
[Feature]
[HarmonyPatch(typeof(WorkForceProvider), nameof(WorkForceProvider.ResetComponent))]
internal static class VanishedTaskCrewResetPatch
{
	private static void Postfix(WorkForceProvider __instance)
	{
		VanishedTask.CrewHolderReset(__instance);
	}
}
