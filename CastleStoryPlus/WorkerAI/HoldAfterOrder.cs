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
// are empty or the player gives it another order. The instinct (fighting) is not held back. Host only (the tasks run
// on the host), the players' bricktrons.
[Feature(Features.WorkerAI, Features.WorkerAIInfo)]
internal static class HoldAfterOrder
{
	private const float HoldSeconds = 15f;

	// When each held bricktron may look for work again.
	private static readonly Dictionary<Labor, float> Until = new Dictionary<Labor, float>();

	private static void Enable()
	{
		GameSession.OnLeave(Until.Clear);
	}

	internal static void TaskEnded(Labor labor, Task task)
	{
		if (labor == null)
		{
			return;
		}
		bool players = labor.faction != null && !labor.faction.isAI;
		bool userOrder = task != null && task.InitialPackage != null && task.InitialPackage.DecisionSource == Labor.DecisionSource.User;
		// A next order already waiting (a queued drop) takes over by itself; nothing to hold for.
		if (NetworkServer.active && players && userOrder && labor.PendingTask == null && !labor.recepteur.IsEmpty())
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
		if (labor.IsNullOrReleased() || Time.time > until || labor.recepteur.IsEmpty())
		{
			Until.Remove(labor);
			return false;
		}
		return true;
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
