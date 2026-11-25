using System.Collections.Generic;
using Brix.Game;
using Brix.Game.AI;
using CastleStoryPlus.Core;
using HarmonyLib;
using Motus.Behavior;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.Treasure;

// The players' bricktrons take no walk of their own into the ruin of a chest its guardians still guard (#197). The
// keep-out of #58 (TreasureChests.InGuardedRuin) was put on the searches known to send a worker there (material, the
// idle cleanup and repair) and #181 kept those searches to the base, but a bricktron still ran out to a guarded ruin
// now and then, paused and ran back: every other way a worker picks a place (a store, the idle walk, a task's next
// goal, the mod's own task shortcuts) could still pick one there. The keep-out is now kept where every walk starts:
// the game's Navigation.NavigateTo. A walk of a player's bricktron into a guarded ruin from outside it fails at once
// (as a walk with no way there: the task fails and the worker looks for other work), unless
// - it belongs to a player's order (a direct order, or the task it started), which still sends a bricktron there;
// - the bricktron already stands in the ruin (the player sent it there), so it can move about and come out;
// - it is a fight: a soldier's walk, a walk of a member of the player's raid (sent to kill the guardians), a walk
//   after a unit (a guardian, a unit followed): no errand to the ruin.
// The first time a bricktron is turned away for an errand, the errand (task, target, task group, autonomy) is written
// to the BepInEx log and BepInEx/idle.log, so a report shows what sent it. Host only: the workers' walks are made
// there; in multiplayer the host's Treasure switch decides.
internal static class TreasureKeepOut
{
	// How long after a player's order a walk still counts as that order's (before its task is the bricktron's own).
	private const float OrderGraceSeconds = 2f;

	// The player's last order to each bricktron: its task and when.
	private static readonly Dictionary<Labor, Task> PlayerTasks = new Dictionary<Labor, Task>();

	private static readonly Dictionary<Labor, float> PlayerOrderAt = new Dictionary<Labor, float>();

	// The errand each bricktron was last turned away for (logged once until it changes).
	private static readonly Dictionary<Labor, string> Logged = new Dictionary<Labor, string>();

	// For the DevTools' tests: how many walks were turned away in this game.
	internal static int TurnedAway { get; private set; }

	internal static void PlayerOrder(Labor labor, Task task)
	{
		if (labor == null)
		{
			return;
		}
		PlayerTasks[labor] = task;
		PlayerOrderAt[labor] = Time.time;
	}

	internal static void Clear()
	{
		PlayerTasks.Clear();
		PlayerOrderAt.Clear();
		Logged.Clear();
		TurnedAway = 0;
	}

	// Whether the walk of the bricktron to the place is turned away.
	internal static bool TurnsAway(Labor labor, Location location)
	{
		if (labor == null || location == null || location.IsEmpty || !TreasureChests.Active)
		{
			return false;
		}
		Faction faction = labor.faction;
		if (faction == null || faction.isAI || faction.IsSpectator || Beasts.KindOf(labor) != null)
		{
			return false;
		}
		Vector3 to;
		try
		{
			to = location.Position;
		}
		catch (System.Exception)
		{
			return false;
		}
		if (!TreasureChests.InGuardedRuin(to))
		{
			return false;
		}
		return TreasureRules.TurnsWalkAway(true, TreasureChests.InGuardedRuin(labor.transform.position), UnderPlayerOrder(labor), Fighting(labor, location));
	}

	// A soldier (any job but the workers'), a member of a raid (the player's raid sent to kill the guardians), a unit in
	// a fight, or a walk after a unit (a guardian, a unit followed).
	private static bool Fighting(Labor labor, Location location)
	{
		if (labor.Activity == Activity.Fighting || labor.Project is RaidProject2)
		{
			return true;
		}
		if (labor.Occupation != null && labor.Occupation.CurrentJob != Occupation.Job.Builder)
		{
			return true;
		}
		GameObject target = location.Object;
		return target != null && target != labor.gameObject && target.GetComponent<Labor>() != null;
	}

	private static bool UnderPlayerOrder(Labor labor)
	{
		if (PlayerOrderAt.TryGetValue(labor, out float at) && Time.time - at < OrderGraceSeconds)
		{
			return true;
		}
		if (!PlayerTasks.TryGetValue(labor, out Task task) || task == null)
		{
			return false;
		}
		if (labor.CurrentTask == task || labor.PendingTask == task)
		{
			return true;
		}
		PlayerTasks.Remove(labor);
		PlayerOrderAt.Remove(labor);
		return false;
	}

	// The errand turned away, once per bricktron and errand.
	internal static void Note(Labor labor, Location location)
	{
		TurnedAway++;
		Task task = labor.CurrentTask ?? labor.PendingTask;
		GameObject goal = (task != null) ? (task.Target ?? task.Giver) : null;
		GameObject target = location.Object;
		string errand = ((task != null) ? task.Description : "-") + "|" + ((goal != null) ? goal.name : "-") + "|" + ((target != null) ? target.name : "-");
		if (Logged.TryGetValue(labor, out string was) && was == errand)
		{
			return;
		}
		Logged[labor] = errand;
		Vector3 to = location.Position;
		string line = "Treasure: " + labor.name + " was not let walk into the ruin of a guarded chest (" + Place(to) + ") of its own accord"
			+ " | at " + Place(labor.transform.position)
			+ " | task " + ((task != null && !string.IsNullOrEmpty(task.Description)) ? task.Description : (task != null ? "(task)" : "none"))
			+ ((goal != null) ? " | goal " + goal.name + " at " + Place(goal.transform.position) : string.Empty)
			+ ((target != null) ? " | walking to " + target.name : string.Empty)
			+ " | job " + ((labor.Project != null) ? labor.Project.GetType().Name + " " + labor.Project.name : "none")
			+ " | activity " + labor.Activity
			+ " | autonomy " + ((labor.Autonomy != null) ? labor.Autonomy.Description : "-")
			+ " | carrying " + (labor.recepteur.IsEmpty() ? "nothing" : "a load");
		Plugin.Log.LogInfo(line);
		Diagnostics.IdleLog.Line("[treasure] " + line);
	}

	private static string Place(Vector3 at)
	{
		return Mathf.FloorToInt(at.x) + "," + Mathf.FloorToInt(at.y) + "," + Mathf.FloorToInt(at.z);
	}
}

// Every walk of a unit starts here (NavigationNodes' walks and runs, a task's way to its goal, the idle walk, a
// store): a walk of a player's bricktron into a guarded ruin of its own accord fails as a walk with no way there.
[Feature(Features.Treasure, Features.TreasureInfo)]
[HarmonyPatch(typeof(Navigation), nameof(Navigation.NavigateTo))]
internal static class TreasureKeepOutWalkPatch
{
	private static bool Prefix(Navigation __instance, Location location, ref Node __result)
	{
		if (!NetworkServer.active || __instance == null)
		{
			return true;
		}
		Labor labor = __instance.Labor;
		if (!TreasureKeepOut.TurnsAway(labor, location))
		{
			return true;
		}
		TreasureKeepOut.Note(labor, location);
		__result = Node.FailPathTo<Exceptions.PathInvalid>(location);
		return false;
	}
}

// The player's orders, so a bricktron the player sends into a guarded ruin still goes.
[Feature(Features.Treasure, Features.TreasureInfo)]
[HarmonyPatch(typeof(Labor), nameof(Labor.SendOrder), new System.Type[] { typeof(Task), typeof(Labor.DecisionSource) })]
internal static class TreasureKeepOutOrderPatch
{
	private static void Prefix(Labor __instance, Task task, Labor.DecisionSource source)
	{
		if (source == Labor.DecisionSource.User && NetworkServer.active)
		{
			TreasureKeepOut.PlayerOrder(__instance, task);
		}
	}
}
