using System;
using System.Collections.Generic;
using System.Reflection;
using Brix.Game.AI;
using Brix.Game.AI.KnowledgeSpace;
using Brix.Game.AI.Nodes;
using Brix.Game.Components;
using Brix.Game.Semantique;
using CastleStoryPlus.Core;
using HarmonyLib;
using Motus.Behavior;
using UnityEngine;

namespace CastleStoryPlus.WorkerAI;

// Less dead time between tasks, and transport thought into them:
// - a finished work task waits 1 tick instead of 10, and the worker is available again at once instead of after
//   0.05 s, so the next search starts right away;
// - materials are fetched from the stockpile that makes the shortest trip worker -> stockpile -> destination
//   (blueprint, workshop), instead of the stockpile nearest the worker;
// - a worker whose bag is full after digging, felling or harvesting stores it as the end of that task, instead of
//   waiting for a separate idle round that only comes once every project has turned it down.
[Feature(Features.TaskFlow, Features.TaskFlowInfo)]
internal static class TaskFlow
{
	private const int DefaultDelayOnFinished = 10;

	private const int DelayOnFinished = 1;

	// Where the materials being fetched are going; set while a fetch picks its source.
	[ThreadStatic]
	private static bool _hasDestination;

	[ThreadStatic]
	private static Vector3 _destination;

	internal static void BeginFetch(Vector3 destination)
	{
		_hasDestination = true;
		_destination = destination;
	}

	internal static void EndFetch()
	{
		_hasDestination = false;
	}

	internal static bool TryGetDestination(out Vector3 destination)
	{
		destination = _destination;
		return _hasDestination;
	}

	internal static void ShortenFinish(Task task)
	{
		if (task.Giver != null && task.Giver.GetComponent<Goal>() != null && task.DelayOnFinished == DefaultDelayOnFinished)
		{
			task.DelayOnFinished = DelayOnFinished;
		}
	}

	// The materials' source, chosen with the destination in mind; the game's per-decision cache is skipped, it is
	// keyed by resource only and would return the source picked for another destination.
	internal static GameObject FindForDestination(Labor labor, Description wanted)
	{
		foreach (Ressource resource in wanted.GetRessources().ToList())
		{
			if (resource.quantifiable.valeur == 0)
			{
				continue;
			}
			GameObject item = labor.FindOneResource(resource);
			if (!item.IsNullOrReleased() && labor.recepteur.HasRoomFor(item))
			{
				return item;
			}
		}
		return null;
	}

	// Ends a dig, tree or harvest task with storing the bag when it is full and a storage has room.
	internal static Node StoreIfFull(Labor labor)
	{
		if (labor == null || labor.recepteur == null || !labor.recepteur.IsFull())
		{
			return Node.Empty;
		}
		foreach (IDescriptor item in labor.recepteur.StoredItems)
		{
			if (item != null && item.GameObject != null && labor.BestRecepteurToStore(item.GameObject) != null)
			{
				return labor.GoStoreInBestStorage();
			}
			break;
		}
		return Node.Empty;
	}
}

// The 10-tick tail after a work task.
[Feature(Features.TaskFlow, Features.TaskFlowInfo)]
[HarmonyPatch(typeof(Task), nameof(Task.WrapNode))]
internal static class TaskFlowFinishDelayPatch
{
	private static void Prefix(Task __instance)
	{
		TaskFlow.ShortenFinish(__instance);
	}
}

// The 0.05 s before a worker that finished a task may look for work again.
[Feature(Features.TaskFlow, Features.TaskFlowInfo)]
[HarmonyPatch(typeof(Goal), nameof(Goal.UnassignWorker))]
internal static class TaskFlowAvailablePatch
{
	private static void Prefix(Task task, out BaseLabor __state)
	{
		__state = task?.Worker;
	}

	private static void Postfix(BaseLabor __state)
	{
		if (__state != null)
		{
			__state.DelayBeforeAvailable = 0f;
		}
	}
}

// Fetching for a blueprint (and anything else filling a recepteur): the destination is the recepteur.
[Feature(Features.TaskFlow, Features.TaskFlowInfo)]
[HarmonyPatch(typeof(PickUpNode), nameof(PickUpNode.FindAndPickUp), new Type[] { typeof(Labor), typeof(Recepteur), typeof(int) })]
internal static class TaskFlowFetchForRecepteurPatch
{
	private static void Prefix(Recepteur toFill)
	{
		if (toFill != null)
		{
			TaskFlow.BeginFetch(toFill.transform.position);
		}
	}

	private static void Finalizer()
	{
		TaskFlow.EndFetch();
	}
}

// Fetching ingredients for a workshop: the destination is the task's giver.
[Feature(Features.TaskFlow, Features.TaskFlowInfo)]
[HarmonyPatch(typeof(PickUpNode), nameof(PickUpNode.FindAndPickUp), new Type[] { typeof(Labor), typeof(Description), typeof(int) })]
internal static class TaskFlowFetchForDescriptionPatch
{
	private static void Prefix(Labor labor)
	{
		GameObject giver = labor?.CurrentTask?.Giver;
		if (giver != null)
		{
			TaskFlow.BeginFetch(giver.transform.position);
		}
	}

	private static void Finalizer()
	{
		TaskFlow.EndFetch();
	}
}

// With a destination, the source is searched for it instead of taken from the worker-only cache.
[Feature(Features.TaskFlow, Features.TaskFlowInfo)]
[HarmonyPatch(typeof(Labor), "ClosestResource", new Type[] { typeof(GameObject), typeof(int), typeof(Description) }, new ArgumentType[] { ArgumentType.Out, ArgumentType.Out, ArgumentType.Normal })]
internal static class TaskFlowClosestResourcePatch
{
	private static bool Prefix(Labor __instance, ref GameObject closest, ref int qty, Description ingredients, ref bool __result)
	{
		if (!TaskFlow.TryGetDestination(out _))
		{
			return true;
		}
		closest = TaskFlow.FindForDestination(__instance, ingredients);
		qty = 0;
		__result = closest != null;
		return false;
	}
}

// Stockpile score for a fetch: the walk to the stockpile plus the walk from it to the destination.
[Feature(Features.TaskFlow, Features.TaskFlowInfo)]
[HarmonyPatch(typeof(KnowledgeUtility), "PickupDistance")]
internal static class TaskFlowPickupDistancePatch
{
	private static void Postfix(Recepteur storage, ref float __result)
	{
		if (__result == float.MaxValue || storage == null || !TaskFlow.TryGetDestination(out Vector3 destination))
		{
			return;
		}
		__result += Vector3.Distance(storage.transform.position, destination);
	}
}

// Dig, tree and harvest tasks store a full bag as their last step.
[Feature(Features.TaskFlow, Features.TaskFlowInfo)]
[HarmonyPatch]
internal static class TaskFlowStoreAfterGatherPatch
{
	private static IEnumerable<MethodBase> TargetMethods()
	{
		yield return AccessTools.Method(typeof(DigGoal), nameof(DigGoal.GetWorkNode));
		yield return AccessTools.Method(typeof(TreeGoal), nameof(TreeGoal.GetWorkNode));
		yield return AccessTools.Method(typeof(NatureHarvestGoal), nameof(NatureHarvestGoal.GetWorkNode));
	}

	private static void Postfix(Labor Worker, ref Node __result)
	{
		if (__result == null || Worker == null)
		{
			return;
		}
		__result = (StaticNode)Node.Sequence.Label("Gather, then store a full bag")
			.Do((StaticNode)__result)
			.Do((Func<Labor, Node>)TaskFlow.StoreIfFull, Worker)
			.Label("Store if full");
	}
}
