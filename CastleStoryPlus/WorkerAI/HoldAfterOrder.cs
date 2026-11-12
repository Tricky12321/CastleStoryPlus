using System.Collections.Generic;
using Brix.Game.AI;
using Brix.Game.Components;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.WorkerAI;

// A bricktron the player gave an order that leaves it carrying something (pick up a pylon, take from a stockpile)
// waits a moment for the player's next order before it goes back to work. The game made a bricktron InstinctOnly
// once a player's order ended (a builder turned FreeAgent again after 3 s, other jobs never), which gave the player
// time to say where to put it down. The mod (FasterWorkers) makes it FreeAgent at once, and idle workers look for
// work every 2 ticks: the bricktron joined a task with the item in its hands (or stored it) before the player could
// give the drop order. Now it does not look for project work for HoldSeconds after such an order, or until its hands
// are empty or the player gives it another order. A bricktron told to dig a voxel waits DigHoldSeconds after the dig,
// carrying something or not, so the player can point it at the next voxel before it goes back to a task (issue #73).
// The instinct (fighting) is not held back. Host only (the tasks run on the host), the players' bricktrons.
[Feature(Features.WorkerAI, Features.WorkerAIInfo)]
internal static class HoldAfterOrder
{
	private const float HoldSeconds = 15f;

	private const float DigHoldSeconds = 10f;

	// When each held bricktron may look for work again.
	private static readonly Dictionary<Labor, float> Until = new Dictionary<Labor, float>();

	// The held bricktrons that wait after a dig order: their hold does not end with empty hands.
	private static readonly HashSet<Labor> AfterDig = new HashSet<Labor>();

	private static void Enable()
	{
		GameSession.OnLeave(() =>
		{
			Until.Clear();
			AfterDig.Clear();
		});
	}

	internal static void TaskEnded(Labor labor, Task task)
	{
		if (labor == null)
		{
			return;
		}
		bool players = labor.faction != null && !labor.faction.isAI;
		bool userOrder = task != null && task.InitialPackage != null && task.InitialPackage.DecisionSource == Labor.DecisionSource.User;
		AfterDig.Remove(labor);
		// A next order already waiting (a queued drop) takes over by itself; nothing to hold for.
		bool held = NetworkServer.active && players && userOrder && labor.PendingTask == null;
		if (held && IsDigOrder(task))
		{
			Until[labor] = Time.time + DigHoldSeconds;
			AfterDig.Add(labor);
		}
		else if (held && !labor.recepteur.IsEmpty())
		{
			Until[labor] = Time.time + HoldSeconds;
		}
		else
		{
			Until.Remove(labor);
		}
	}

	internal static bool IsHeld(Labor labor)
	{
		if (Until.Count == 0 || !Until.TryGetValue(labor, out float until))
		{
			return false;
		}
		// Time is up or the hands are empty (it put the item down). A later order ends the hold or renews it when that
		// order's task ends (TaskEnded).
		if (labor.IsNullOrReleased() || Time.time > until || (labor.recepteur.IsEmpty() && !AfterDig.Contains(labor)))
		{
			Until.Remove(labor);
			AfterDig.Remove(labor);
			return false;
		}
		return true;
	}

	// The player's "dig here" (the secondary right-click on the ground, or the dig command).
	private static bool IsDigOrder(Task task)
	{
		return task.InitialPackage is OrderPackage<Labor, Location> package && package.InstructionValue == Order.ForLabor.GoDig;
	}
}

// A player's order has ended: hold the bricktron if it carries something.
[Feature(Features.WorkerAI, Features.WorkerAIInfo)]
[HarmonyPatch(typeof(Labor), nameof(Labor.OnTaskEnded))]
internal static class HoldAfterOrderEndPatch
{
	private static void Postfix(Labor __instance, Task t)
	{
		HoldAfterOrder.TaskEnded(__instance, t);
	}
}

// A held bricktron does not look for project work (the idle task's storing included).
[Feature(Features.WorkerAI, Features.WorkerAIInfo)]
[HarmonyPatch(typeof(Labor), nameof(Labor.LookingForWork))]
internal static class HoldAfterOrderLookPatch
{
	private static void Postfix(Labor __instance, ref bool __result)
	{
		if (__result && HoldAfterOrder.IsHeld(__instance))
		{
			__result = false;
		}
	}
}
