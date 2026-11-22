using System.Collections.Generic;
using Brix.Components;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Game.Semantique;
using Brix.External.Factories;
using Brix.Lifecycle.Pooling;
using Brix.Network;
using Brix.UI.Icons;
using CastleStoryPlus.Army;
using CastleStoryPlus.Core;
using CastleStoryPlus.Upgrades;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.WorkerAI;

// Gets a bricktron out of work it cannot do (issue #121). Three ways in:
// - "Reset bricktrons" (the in-game settings sidebar, #142, and the Castle Story Plus settings page): every bricktron
//   of the player's team lets go of its
//   task, its queued task, its reservations and its wait for an order, and looks for work afresh. What it carries stays
//   in its hands, a bricktron put on a task stays on it, unless that task has no work for it (then it is free to help
//   elsewhere, as a worker that has none always was). The host does it; a client's button sends a command (message
//   214) and the host resets the sender's own team only. Asked for while the game is paused (the Escape menu pauses a
//   single player game), the reset waits until the game runs again: the workers' behaviour does not run while paused,
//   so a task let go of then was only half let go of (#142). A worker marked as fighting is reset too, and legs that
//   cannot move are put back on their feet (LocomotionRecovery, #156).
// - A loop of failed tasks: a worker whose tasks fail again and again (a block no one can get to, a build that has run
//   out of planks, an item that fell off the map) lets go of what it holds, and when the loop comes back within two
//   minutes it waits longer before it looks again. A worker locked to a task goes free (it stays a member of the task, so
//   it comes back when the task has work again, as a worker helping other tasks always did): the game only skipped the
//   failed task for a moment, and a locked worker tried it again for ever. A locked worker that stands without any task
//   (the failure came before a task was made, so nothing is counted) is freed by the reset button.
// - A task whose target is gone: a log that rolled off the map (below the world) is taken out of the game, a destroyed
//   target ends the task, so the worker does not walk to it for ever.
// A player's order to a worker ends its run of failed tasks (and a loop found but not yet handled), so the loop's
// handling never throws out the order (#143).
// Host only; the player's workers (builders and the like), never soldiers or members of a raid.
[Feature(Features.StuckReset, Features.StuckResetInfo)]
internal sealed class BricktronRecovery : MonoBehaviour
{
	private const short ResetMessage = 214;

	private const float CheckSeconds = 1f;

	private const float VoidSeconds = 3f;

	private sealed class ResetNetMessage : MessageBase
	{
	}

	// The failed tasks in a row of one worker on one task area or goal.
	private sealed class Run
	{
		public object Key;

		public readonly List<float> Times = new List<float>();
	}

	private static readonly Dictionary<Labor, Run> Failures = new Dictionary<Labor, Run>();


	// A worker whose task target looked destroyed at the last check (it must look so twice).
	private static readonly Dictionary<Labor, Task> GoneSeen = new Dictionary<Labor, Task>();

	private static readonly Dictionary<Labor, float> LastLoop = new Dictionary<Labor, float>();

	// Workers a loop was found for, handled in Update (not while their task is ending).
	private static readonly List<KeyValuePair<Labor, bool>> Loops = new List<KeyValuePair<Labor, bool>>();

	// When each team asked for a reset last (host, the clients' commands).
	private static readonly Dictionary<Faction, float> LastReset = new Dictionary<Faction, float>();

	// Teams whose reset was asked for while the game was paused (host): reset once it runs again.
	private static readonly HashSet<Faction> PendingResets = new HashSet<Faction>();

	// What the last reset asked for from this game did (the settings sidebar button's tooltip).
	internal static string LastResult;

	// How many teams wait for the game to run again to be reset (DevTools tests).
	internal static int PendingResetCount => PendingResets.Count;

	private readonly List<Labor> _labors = new List<Labor>();

	private float _next;

	private static void Enable()
	{
		GameSession.OnLeave(() =>
		{
			Failures.Clear();
			LastLoop.Clear();
			GoneSeen.Clear();
			Loops.Clear();
			LastReset.Clear();
			PendingResets.Clear();
			LastResult = null;
		});
		// The in-game settings sidebar (#142): the Escape menu, where the button was first, pauses a single player game.
		GameUi.GameMenuItems.Extend((GameUi.GameMenuModel menu) =>
		{
			GameUi.UiSection section = menu.Settings.SectionAfter("buildtasks", "bricktrons", () => "Bricktrons");
			section.Add(new GameUi.UiButton
			{
				Id = "resetBricktrons",
				Label = () => "Reset bricktrons",
				Icon = () => GameUi.GameUiState.Icon(IconKeys.UI_Reset),
				Tooltip = () => "Every bricktron of your team lets go of its task, queued task and reservations and looks for work afresh (what it carries stays in its hands). Use it when bricktrons stand frozen." + (string.IsNullOrEmpty(LastResult) ? string.Empty : "\n" + LastResult),
				OnClick = (bool shift, bool ctrl) => LastResult = Request()
			});
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
			if (!RecoveryRules.CanResetNow(Time.timeScale))
			{
				PendingResets.Add(team);
				Plugin.Log.LogInfo("BricktronRecovery: reset of " + team.name + " asked for while paused: done when the game runs again");
				return "The game is paused: your bricktrons are reset as soon as it runs again.";
			}
			return "Reset " + ResetTeam(team) + " bricktrons.";
		}
		NetworkClient client = (UNetManager.instance != null) ? UNetManager.instance.LocalNetworkClient : null;
		if (client == null)
		{
			return "Not connected to a host.";
		}
		client.Send(ResetMessage, new ResetNetMessage());
		return "Asked the host to reset your bricktrons (the host needs the StuckReset feature on).";
	}

	// Host: a client's button. Only the sender's own team is reset.
	private static void ResetReceived(NetworkMessage netMsg)
	{
		if (!NetworkServer.active)
		{
			return;
		}
		Faction team = Multiplayer.ConnectionTeams.TeamOf(netMsg.conn);
		float last = (team != null && LastReset.TryGetValue(team, out float at)) ? at : -1f;
		if (!RecoveryRules.AcceptResetCommand(team != null, last, Time.time))
		{
			return;
		}
		LastReset[team] = Time.time;
		if (!RecoveryRules.CanResetNow(Time.timeScale))
		{
			PendingResets.Add(team);
			return;
		}
		ResetTeam(team);
	}

	// Host: the resets asked for while the game was paused, on the first frame it runs again.
	private static void RunPendingResets()
	{
		if (PendingResets.Count == 0 || !RecoveryRules.CanResetNow(Time.timeScale))
		{
			return;
		}
		List<Faction> teams = new List<Faction>(PendingResets);
		PendingResets.Clear();
		foreach (Faction team in teams)
		{
			if (team == null)
			{
				continue;
			}
			int count = ResetTeam(team);
			if (User.LocalUser != null && User.LocalUser.faction == team)
			{
				LastResult = "Reset " + count + " bricktrons.";
			}
		}
	}

	// A player's order to the worker: its run of failed tasks and a loop found but not handled yet are dropped, so the
	// loop's handling does not let go of the order, and the stuck watch starts afresh (#143).
	internal static void OrderGiven(Labor labor)
	{
		if (!NetworkServer.active || labor == null)
		{
			return;
		}
		Failures.Remove(labor);
		Loops.RemoveAll((KeyValuePair<Labor, bool> loop) => loop.Key == labor);
		StuckReset.Forget(labor);
	}

	// Host: how many bricktrons of the team were reset.
	internal static int ResetTeam(Faction team)
	{
		List<Labor> labors = new List<Labor>();
		Live<Labor>.Active(labors);
		int count = 0;
		foreach (Labor labor in labors)
		{
			if (labor == null || labor.faction != team)
			{
				continue;
			}
			Forget(labor);
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

	private static void Forget(Labor labor)
	{
		Failures.Remove(labor);
		LastLoop.Remove(labor);
		GoneSeen.Remove(labor);
	}

	// A worker of the player's: not a soldier (their tasks fail when the enemy dies or runs off, and they belong to a raid
	// task), not a member of a raid.
	internal static bool Eligible(Labor labor)
	{
		if (labor.IsNullOrReleased() || labor.faction == null || labor.faction.isAI || labor.Project is RaidProject2)
		{
			return false;
		}
		Profession profession = (labor.Occupation != null) ? labor.Occupation.GetProfession() : null;
		return profession != null && profession.IsWorker();
	}

	// The reset of one worker. A worker marked as fighting is reset too (#156): a builder only fights on the player's
	// order, and one left marked as fighting without a fight neither looks for work nor was touched by the reset. Its
	// legs are put back on their feet when they cannot move (LocomotionRecovery), so the next task does not wait for
	// them for good.
	private static bool Unstick(Labor labor)
	{
		if (!Eligible(labor) || labor.IsPhasedOut)
		{
			return false;
		}
		LetGo(labor);
		Free(labor);
		if (labor.Activity == Activity.Fighting)
		{
			labor.Activity = Activity.Idle;
		}
		LocomotionRecovery.ResetLegs(labor, "reset");
		return true;
	}

	// A locked worker goes free but stays a member of its task (the task's work brings it back, as with the game's
	// helping of other tasks); a worker holding its position keeps to it.
	private static void Free(Labor labor)
	{
		if (labor.Autonomy == AutonomyStatus.HoldingPosition || labor.Operable != null)
		{
			return;
		}
		labor.Autonomy = AutonomyStatus.FreeAgent;
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
#if !ESSENTIALS
		AutoCleanup.Release(labor);
		AutoRepair.Release(labor);
		StockpileConsolidation.Release(labor);
#endif
		KitReservation.Release(labor);
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
		if (!NetworkServer.active || labor == null || task == null || !Eligible(labor))
		{
			return;
		}
		Labor.DecisionSource source = (task.InitialPackage != null) ? task.InitialPackage.DecisionSource : Labor.DecisionSource.Unspecified;
		if (source != Labor.DecisionSource.Project && source != Labor.DecisionSource.User)
		{
			return;
		}
		// The failures count for one task area or goal: a worker that fails on different tasks is only unlucky.
		object key = KeyOf(task);
		if (!Failures.TryGetValue(labor, out Run run))
		{
			run = new Run();
			Failures[labor] = run;
		}
		if (run.Key != key)
		{
			run.Key = key;
			run.Times.Clear();
		}
		if (!RecoveryRules.RecordOutcome(run.Times, Time.time, IsFailure(task.result)))
		{
			return;
		}
		bool hard = LastLoop.TryGetValue(labor, out float last) && RecoveryRules.Escalates(last, Time.time);
		LastLoop[labor] = Time.time;
		Loops.Add(new KeyValuePair<Labor, bool>(labor, hard));
	}

	private static object KeyOf(Task task)
	{
		GameObject giver = (task.Giver != null) ? task.Giver : task.Target;
		if (giver == null)
		{
			return null;
		}
		Project project = giver.GetComponent<Project>();
		if (project == null)
		{
			Goal goal = giver.GetComponent<Goal>();
			project = (goal != null) ? goal.project : null;
		}
		return (object)project ?? giver;
	}

	private static bool IsFailure(TaskResult result)
	{
		return result == TaskResult.WorkerFailed || result == TaskResult.PathFailed || result == TaskResult.CouldNotReach || result == TaskResult.InvalidSearch || result == TaskResult.BlockedByTransferable;
	}

	private static void HandleLoop(Labor labor, bool hard)
	{
		if (!Eligible(labor) || labor.Activity == Activity.Fighting)
		{
			return;
		}
		string where = (labor.CurrentTask != null) ? labor.CurrentTask.Description : "no task";
		LetGo(labor);
		// The first loop only lets go of the tasks (a worker locked to its task stays on it, the failures may be passing);
		// a loop that comes back within two minutes frees it, still a member of its task.
		if (hard || labor.Autonomy != AutonomyStatus.LockedToOperable)
		{
			Free(labor);
		}
		// A repeated loop waits longer before the next look, so other tasks get their turn.
		labor.DelayBeforeAvailable = hard ? 10f : 3f;
		Plugin.Log.LogInfo("BricktronRecovery: " + labor.name + " failed " + RecoveryRules.FailureLimit + " tasks in a row (" + where + "): tasks let go" + (hard ? ", free to take other work" : string.Empty));
	}

	// ---- a task whose target is gone

	private void Update()
	{
		if (!NetworkServer.active)
		{
			return;
		}
		RunPendingResets();
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
		Prune();
		CheckVoidItems();
	}

	private static void CheckTarget(Labor labor)
	{
		Task task = (labor != null) ? labor.CurrentTask : null;
		if (task == null || !Eligible(labor) || labor.IsPhasedOut || labor.Operable != null || labor.Activity == Activity.Fighting)
		{
			GoneSeen.Remove(labor);
			return;
		}
		if (task.InitialPackage == null || task.InitialPackage.DecisionSource == Labor.DecisionSource.Character || task.Status != TaskProgression.Running)
		{
			GoneSeen.Remove(labor);
			return;
		}
		GameObject target = (task.Target != null) ? task.Target : task.Giver;
		if ((object)target != null && target != null && RecoveryRules.InVoid(target.transform.position.y))
		{
			// A loose item that fell off the map stays a valid goal for ever: it is taken out of the game.
			Plugin.Log.LogInfo("BricktronRecovery: the target of " + labor.name + "'s '" + task.Description + "' is off the map: taken out, task ended");
			ReleaseVoidItem(target);
			labor.AbortWorkSignal.Invoke(TaskResult.GoalWasRemoved);
			GoneSeen.Remove(labor);
			return;
		}
		// A destroyed target must look so at two checks in a row (a task that ends well destroys its target too).
		if (Destroyed(task.Target) || Destroyed(task.Giver))
		{
			if (GoneSeen.TryGetValue(labor, out Task seen) && seen == task)
			{
				Plugin.Log.LogInfo("BricktronRecovery: " + labor.name + "'s target of '" + task.Description + "' is destroyed: task ended");
				labor.AbortWorkSignal.Invoke(TaskResult.GoalWasRemoved);
				GoneSeen.Remove(labor);
			}
			else
			{
				GoneSeen[labor] = task;
			}
			return;
		}
		GoneSeen.Remove(labor);
	}

	private static bool Destroyed(GameObject target)
	{
		return (object)target != null && target == null;
	}

	// Entries of workers that are gone (the pool reuses the objects).
	private static void Prune()
	{
		if (Failures.Count + LastLoop.Count + GoneSeen.Count == 0)
		{
			return;
		}
		Drop(Failures);
		Drop(LastLoop);
		Drop(GoneSeen);
	}

	private static void Drop<T>(Dictionary<Labor, T> map)
	{
		List<Labor> gone = null;
		foreach (Labor labor in map.Keys)
		{
			if (labor == null || !labor.gameObject.activeInHierarchy)
			{
				(gone ?? (gone = new List<Labor>())).Add(labor);
			}
		}
		if (gone != null)
		{
			foreach (Labor labor in gone)
			{
				map.Remove(labor);
			}
		}
	}

	// ---- items off the map

	private readonly List<Goal> _goals = new List<Goal>();

	private float _nextVoid;

	// The loose items of the clean-up areas that lie below the map are taken out of the game, so every worker and every
	// area drops them (nothing destroyed them: they kept falling and stayed valid tasks).
	private void CheckVoidItems()
	{
		if (Time.time < _nextVoid)
		{
			return;
		}
		_nextVoid = Time.time + VoidSeconds;
		foreach (GameObject go in BrixSingleton<AutoList>.Instance.GetInstances(Groupes.Cleanup))
		{
			Project project = (go != null) ? go.GetComponent<Project>() : null;
			if (project == null || !project.IsStillAlive)
			{
				continue;
			}
			_goals.Clear();
			foreach (IGoalProvider provider in project.GetGoalProviders())
			{
				provider?.GetAll(_goals);
			}
			foreach (Goal goal in _goals)
			{
				if (goal is CleanUpGoal && !goal.IsNullOrReleased() && RecoveryRules.InVoid(goal.transform.position.y))
				{
					Plugin.Log.LogInfo("BricktronRecovery: " + goal.gameObject.name + " lies off the map: taken out of the game");
					ReleaseVoidItem(goal.gameObject);
				}
			}
		}
		_goals.Clear();
	}

	// Takes a loose thing below the map out of the game as the game does with its own: off the network, back to its pool.
	internal static void ReleaseVoidItem(GameObject item)
	{
		if (item == null || item.IsNullOrReleased() || !RecoveryRules.InVoid(item.transform.position.y) || item.GetComponentInParent<Labor>() != null || item.GetComponent<Apparence>() == null)
		{
			return;
		}
		if (NetworkServer.active && item.GetComponent<NetworkIdentity>() != null)
		{
			NetworkServer.UnSpawn(item);
		}
		ObjectPoolSingleton.Release(item);
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

// A player's order to a worker ends its loop of failed tasks (#143).
[Feature(Features.StuckReset, Features.StuckResetInfo)]
[HarmonyPatch(typeof(Labor), nameof(Labor.SendOrder))]
internal static class BricktronRecoveryOrderPatch
{
	private static void Postfix(Labor __instance, Labor.DecisionSource source)
	{
		if (source != Labor.DecisionSource.User)
		{
			return;
		}
		try
		{
			BricktronRecovery.OrderGiven(__instance);
		}
		catch (System.Exception ex)
		{
			Plugin.Log.LogWarning("BricktronRecovery: " + ex.Message);
		}
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
