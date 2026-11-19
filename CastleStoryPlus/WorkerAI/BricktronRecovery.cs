using System.Collections.Generic;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Lifecycle.Pooling;
using Brix.Network;
using CastleStoryPlus.Core;
using CastleStoryPlus.Upgrades;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.WorkerAI;

// Gets a bricktron out of work it cannot do (issue #121). Three ways in:
// - "Reset all bricktrons" (the Castle Story Plus settings page): every bricktron of the player's team lets go of its
//   task, its queued task, its reservations and its wait for an order, and looks for work afresh. What it carries stays
//   in its hands, a bricktron put on a task stays on it, unless that task has no work for it (then it is free to help
//   elsewhere, as a worker that has none always was). The host does it; a client's button sends a command (message
//   214) and the host resets the sender's own team only.
// - A loop of failed tasks: a worker whose tasks fail again and again (a block no one can get to, a build that has run
//   out of planks, an item that fell off the map) lets go of what it holds, and when the loop comes back within two
//   minutes it is also taken off the task it was put on and goes free (the game's "the worker can do nothing here" only
//   ever skipped the task for a moment, and a locked worker tried it again for ever).
// - A task whose target is gone: a log that rolled off the map (below the world, or destroyed) leaves a worker walking
//   to it or standing at nothing; its task is ended.
// Host only; the player's workers, fighters are left alone.
[Feature(Features.StuckReset, Features.StuckResetInfo)]
internal sealed class BricktronRecovery : MonoBehaviour
{
	private const short ResetMessage = 214;

	private const float CheckSeconds = 1f;

	private sealed class ResetNetMessage : MessageBase
	{
	}

	private static readonly Dictionary<Labor, List<float>> Failures = new Dictionary<Labor, List<float>>();

	private static readonly Dictionary<Labor, float> LastLoop = new Dictionary<Labor, float>();

	// Workers a loop was found for, handled in Update (not while their task is ending).
	private static readonly List<KeyValuePair<Labor, bool>> Loops = new List<KeyValuePair<Labor, bool>>();

	// When each team asked for a reset last (host, the clients' commands).
	private static readonly Dictionary<Faction, float> LastReset = new Dictionary<Faction, float>();

	private readonly List<Labor> _labors = new List<Labor>();

	private float _next;

	private static void Enable()
	{
		GameSession.OnLeave(() =>
		{
			Failures.Clear();
			LastLoop.Clear();
			Loops.Clear();
			LastReset.Clear();
		});
		GameSession.OnLevelReady(LevelReady);
		Plugin.Root.AddComponent<BricktronRecovery>();
	}

	private static void LevelReady()
	{
		if (NetworkServer.active)
		{
			NetworkServer.UnregisterHandler(ResetMessage);
			NetworkServer.RegisterHandler(ResetMessage, ResetReceived);
		}
	}

	// ---- the reset

	// The settings page's button. Answers what happened, for the page's text.
	internal static string Request()
	{
		User user = User.LocalUser;
		Faction team = (user != null) ? user.faction : null;
		if (team == null)
		{
			return "No game is running.";
		}
		if (NetworkServer.active)
		{
			return "Reset " + ResetTeam(team) + " bricktrons.";
		}
		NetworkClient client = (UNetManager.instance != null) ? UNetManager.instance.LocalNetworkClient : null;
		if (client == null)
		{
			return "Not connected to a host.";
		}
		client.Send(ResetMessage, new ResetNetMessage());
		return "Asked the host to reset your bricktrons.";
	}

	// Host: a client's button. Only the sender's own team is reset.
	private static void ResetReceived(NetworkMessage netMsg)
	{
		if (!NetworkServer.active)
		{
			return;
		}
		Faction team = Sprint.TeamOf(netMsg.conn);
		float last = (team != null && LastReset.TryGetValue(team, out float at)) ? at : -1f;
		if (!RecoveryRules.AcceptResetCommand(team != null, last, Time.time))
		{
			return;
		}
		LastReset[team] = Time.time;
		ResetTeam(team);
	}

	// Host: how many bricktrons of the team were reset.
	internal static int ResetTeam(Faction team)
	{
		List<Labor> labors = new List<Labor>();
		Live<Labor>.Active(labors);
		int count = 0;
		UnreachableTasks.ForgetAll();
		foreach (Labor labor in labors)
		{
			if (labor == null || labor.faction != team)
			{
				continue;
			}
			try
			{
				if (Unstick(labor))
				{
					count++;
				}
			}
			catch (System.Exception ex)
			{
				Plugin.Log.LogWarning("BricktronRecovery: " + labor.name + ": " + ex.Message);
			}
		}
		Plugin.Log.LogInfo("BricktronRecovery: reset of " + count + " bricktrons of " + team.name);
		return count;
	}

	private static bool Unstick(Labor labor)
	{
		if (labor.IsNullOrReleased() || labor.IsPhasedOut || labor.Activity == Activity.Fighting)
		{
			return false;
		}
		LetGo(labor);
		if (labor.Autonomy == AutonomyStatus.LockedToOperable)
		{
			// Put on a task with nothing to do for it: free to help elsewhere, still a member of the task.
			Project project = labor.Project;
			if (labor.Operable == null && project != null && !(project is IdleProject) && !(project is RaidProject2) && !project.HasWorkFor(labor))
			{
				labor.Autonomy = AutonomyStatus.FreeAgent;
			}
		}
		else if (labor.Autonomy != AutonomyStatus.HoldingPosition)
		{
			labor.Autonomy = AutonomyStatus.FreeAgent;
		}
		return true;
	}

	// The worker's task, queued task, reservations and wait for an order are let go; it looks for work afresh.
	private static void LetGo(Labor labor)
	{
		TaskPrediction.LetGoUnstarted(labor, "reset");
		Task pending = labor.PendingTask;
		if (pending != null)
		{
			Goal goal = (pending.Giver != null) ? pending.Giver.GetComponent<Goal>() : null;
			if (goal != null)
			{
				goal.UnassignWorker(pending);
			}
			labor.PendingTask = null;
		}
		TaskReservation.Release(labor);
		ResourceReservation.Release(labor);
		AutoCleanup.Release(labor);
		HoldAfterOrder.Release(labor);
		if (labor.CurrentTask != null && labor.Operable == null)
		{
			labor.AbortWorkSignal.Invoke(TaskResult.ProjectCancelledTask);
		}
		labor.DelayBeforeAvailable = 0f;
	}

	// ---- a loop of failed tasks

	internal static void TaskEnded(Labor labor, Task task)
	{
		if (!NetworkServer.active || labor == null || task == null || labor.faction == null || labor.faction.isAI)
		{
			return;
		}
		Labor.DecisionSource source = (task.InitialPackage != null) ? task.InitialPackage.DecisionSource : Labor.DecisionSource.Unspecified;
		if (source != Labor.DecisionSource.Project && source != Labor.DecisionSource.User)
		{
			return;
		}
		if (!Failures.TryGetValue(labor, out List<float> failures))
		{
			failures = new List<float>();
			Failures[labor] = failures;
		}
		if (!RecoveryRules.RecordOutcome(failures, Time.time, IsFailure(task.result)))
		{
			return;
		}
		bool hard = LastLoop.TryGetValue(labor, out float last) && RecoveryRules.Escalates(last, Time.time);
		LastLoop[labor] = Time.time;
		Loops.Add(new KeyValuePair<Labor, bool>(labor, hard));
	}

	private static bool IsFailure(TaskResult result)
	{
		return result == TaskResult.WorkerFailed || result == TaskResult.PathFailed || result == TaskResult.CouldNotReach || result == TaskResult.InvalidSearch || result == TaskResult.BlockedByTransferable;
	}

	private static void HandleLoop(Labor labor, bool hard)
	{
		if (labor.IsNullOrReleased() || labor.Activity == Activity.Fighting)
		{
			return;
		}
		string where = (labor.CurrentTask != null) ? labor.CurrentTask.Description : "no task";
		LetGo(labor);
		if (hard)
		{
			labor.AssignToIdle();
			labor.Autonomy = AutonomyStatus.FreeAgent;
		}
		else if (labor.Autonomy != AutonomyStatus.LockedToOperable && labor.Autonomy != AutonomyStatus.HoldingPosition)
		{
			labor.Autonomy = AutonomyStatus.FreeAgent;
		}
		Plugin.Log.LogInfo("BricktronRecovery: " + labor.name + " failed " + RecoveryRules.FailureLimit + " tasks in a row (" + where + "): tasks let go" + (hard ? ", taken off its task" : string.Empty));
	}

	// ---- a task whose target is gone

	private void Update()
	{
		if (!NetworkServer.active)
		{
			return;
		}
		if (Loops.Count > 0)
		{
			List<KeyValuePair<Labor, bool>> loops = new List<KeyValuePair<Labor, bool>>(Loops);
			Loops.Clear();
			foreach (KeyValuePair<Labor, bool> loop in loops)
			{
				try
				{
					HandleLoop(loop.Key, loop.Value);
				}
				catch (System.Exception ex)
				{
					Plugin.Log.LogWarning("BricktronRecovery: " + ex.Message);
				}
			}
		}
		if (Time.time < _next)
		{
			return;
		}
		_next = Time.time + CheckSeconds;
		Live<Labor>.Active(_labors);
		foreach (Labor labor in _labors)
		{
			try
			{
				CheckTarget(labor);
			}
			catch (System.Exception ex)
			{
				Plugin.Log.LogWarning("BricktronRecovery: " + ex.Message);
			}
		}
	}

	private static void CheckTarget(Labor labor)
	{
		Task task = (labor != null) ? labor.CurrentTask : null;
		if (task == null || labor.IsPhasedOut || labor.Operable != null || labor.Activity == Activity.Fighting || labor.faction == null || labor.faction.isAI)
		{
			return;
		}
		if (task.InitialPackage == null || task.InitialPackage.DecisionSource == Labor.DecisionSource.Character)
		{
			return;
		}
		if (!Gone(task.Target) && !Gone(task.Giver))
		{
			return;
		}
		Plugin.Log.LogInfo("BricktronRecovery: " + labor.name + "'s target of '" + task.Description + "' is gone (destroyed or off the map): task ended");
		labor.AbortWorkSignal.Invoke(TaskResult.GoalWasRemoved);
	}

	// A thing that was destroyed, or lies below the map.
	private static bool Gone(GameObject target)
	{
		if ((object)target == null)
		{
			return false;
		}
		return target == null || RecoveryRules.InVoid(target.transform.position.y);
	}
}

// A task ended: a run of failed ones is a loop.
[Feature(Features.StuckReset, Features.StuckResetInfo)]
[HarmonyPatch(typeof(Labor), nameof(Labor.OnTaskEnded))]
internal static class BricktronRecoveryEndPatch
{
	private static void Postfix(Labor __instance, Task t)
	{
		BricktronRecovery.TaskEnded(__instance, t);
	}
}

// An item that lies below the map is no job for a clean-up area's worker.
[Feature(Features.StuckReset, Features.StuckResetInfo)]
[HarmonyPatch(typeof(CleanUpGoal), nameof(CleanUpGoal.IsMatchFor))]
internal static class BricktronRecoveryVoidItemPatch
{
	private static void Postfix(CleanUpGoal __instance, ref bool __result)
	{
		if (__result && RecoveryRules.InVoid(__instance.transform.position.y))
		{
			__result = false;
		}
	}
}
