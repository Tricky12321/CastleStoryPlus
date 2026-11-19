using System;
using System.Collections.Generic;
using System.Reflection;
using Brix.Game.AI;
using Brix.Game.AI.KnowledgeSpace;
using Brix.Game.AI.Nodes;
using Brix.Game.Components;
using Brix.Game.Semantique;
using Brix.Engine;
using Brix.Game;
using Brix.External.Factories;
using CastleStoryPlus.Core;
using CastleStoryPlus.Economy;
using Smooth.Slinq;
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
		// A workshop's ingredients at different resource priorities: the lowest cost first (ResourcePriority).
		if (Plugin.IsEnabled(Features.ResourcePriority) && ResourcePriority.TryChoose(labor, wanted, cached: false, out GameObject chosen, out _))
		{
			return chosen;
		}
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

	// Ends a dig, tree or harvest task with storing the bag when it is full and a storage has room. After digging,
	// also when the bag is not full but none of the area's other blocks fits in it any more (the game digs only
	// what fits): the worker would only be turned down by every block and stand idle until it stored by itself.
	internal static Node StoreIfFull(Labor labor, Goal goal)
	{
		if (labor == null || labor.recepteur == null || (!labor.recepteur.IsFull() && !NoDigFits(labor, goal as DigGoal)))
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

	private static readonly List<Goal> DigGoals = new List<Goal>();

	// A block the worker would not dig anyway (its resource is at the quarry's limit or at 0 in the resource
	// priorities): it does not count as a block that fits. A bag with room only for such blocks is stored at once, instead
	// of the worker standing about with a bag that no block can use (issue #124).
	private static bool LeftAlone(Labor labor, DigGoal dig)
	{
		if (Plugin.IsEnabled(Features.QuarryLimit) && dig.ResourceSetting is QuarryData data && QuarryLimit.All(data) != null)
		{
			int flag = QuarryLimit.FlagOf(Voxel.GetBlockInfo(dig.voxel.position).ResourceKey);
			if (flag != 0 && QuarryLimit.Reached(data, flag))
			{
				return true;
			}
		}
		return Plugin.IsEnabled(Features.ResourcePriority) && ResourcePriority.Customised(labor.faction) && ResourcePriority.Applies(dig) && ResourcePriority.Get(labor.faction, dig) == ResourcePriority.Never;
	}

	// How many pieces in the bag are not what a digger takes up (stone, crystals, ore, coal): glass from a furnace, planks...
	internal static int ForeignCarried(Labor labor)
	{
		if (labor == null || labor.recepteur == null || labor.recepteur.IsEmpty())
		{
			return 0;
		}
		Description content = labor.recepteur.ContentDescription;
		int total = 0;
		foreach (Ressource resource in content.GetRessources().ToList())
		{
			total += resource.quantifiable.valeur;
		}
		int dug = content.Value<Stones>() + content.Value<BlueCrystal>() + content.Value<OrangeCrystal>() + content.Value<RawIron>() + content.Value<Clay>();
		return RecoveryRules.ForeignCount(total, dug);
	}

	// Something the bag carries that a storage takes.
	internal static bool CanStoreForeign(Labor labor)
	{
		foreach (IDescriptor item in labor.recepteur.StoredItems)
		{
			if (item != null && item.GameObject != null && labor.BestRecepteurToStore(item.GameObject) != null)
			{
				return true;
			}
		}
		return false;
	}

	// Starts a dig task: what the bag holds that is not a digger's load is put into a storage first. The worker took
	// the glass from a furnace, joined a quarry with it in its hands and dug a few blocks before it went to store it,
	// and the dig's count of what still fits did not see the glass in the bag (issues #129 and #124).
	internal static Node StoreForeignFirst(Labor labor, Goal goal)
	{
		if (ForeignCarried(labor) == 0 || !CanStoreForeign(labor))
		{
			return Node.Empty;
		}
		Plugin.Log.LogInfo("TaskFlow: " + labor.name + " carries " + labor.recepteur.ContentString() + ": stores it before it digs");
		return labor.GoStoreInBestStorage();
	}

	private static bool NoDigFits(Labor labor, DigGoal finished)
	{
		Project project = (finished != null && finished.DigOnlyWhatFitsInBag) ? finished.project : null;
		if (project == null || project.IsNullOrReleased())
		{
			return false;
		}
		int space = finished.SpaceRemainingInInventory(labor);
		DigGoals.Clear();
		foreach (IGoalProvider provider in project.GetGoalProviders())
		{
			provider?.GetAll(DigGoals);
		}
		bool anyLeft = false;
		foreach (Goal goal in DigGoals)
		{
			if (goal == finished || !(goal is DigGoal dig) || dig.IsNullOrReleased() || !dig.IsValid() || dig.HasOtherWorkerAssigned(labor))
			{
				continue;
			}
			anyLeft = true;
			if (dig.AmountGarnotteInGround(dig.voxel.position) <= space && !LeftAlone(labor, dig))
			{
				DigGoals.Clear();
				return false;
			}
		}
		DigGoals.Clear();
		return anyLeft;
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

	private static void Postfix(Goal __instance, Labor Worker, ref Node __result)
	{
		if (__result == null || Worker == null)
		{
			return;
		}
		__result = (StaticNode)Node.Sequence.Label("Gather, then store a full bag")
			.Do((StaticNode)__result)
			.Do((Func<Labor, Goal, Node>)TaskFlow.StoreIfFull, Worker, __instance)
			.Label("Store if full");
	}
}

// A dig task starts with storing what the bag holds that is no digger's load.
[Feature(Features.TaskFlow, Features.TaskFlowInfo)]
[HarmonyPatch(typeof(DigGoal), nameof(DigGoal.GetWorkNode))]
internal static class TaskFlowStoreForeignFirstPatch
{
	private static void Postfix(Goal __instance, Labor Worker, ref Node __result)
	{
		if (__result == null || Worker == null)
		{
			return;
		}
		__result = (StaticNode)Node.Sequence.Label("Store what is not a digger's load, then dig")
			.Do((Func<Labor, Goal, Node>)TaskFlow.StoreForeignFirst, Worker, __instance)
			.Label("Store carried things first")
			.Do((StaticNode)__result);
	}
}

// A worker carrying something a storage does not take does not join a quarry: with it in its hands it could not
// put the digs' stone into the bag.
[Feature(Features.TaskFlow, Features.TaskFlowInfo)]
[HarmonyPatch(typeof(DigGoal), nameof(DigGoal.IsMatchFor))]
internal static class TaskFlowForeignBagPatch
{
	private static void Postfix(Labor labor, AIErrorsStore errorDestination, ref bool __result)
	{
		if (!__result || labor == null || labor.recepteur == null || labor.recepteur.IsEmpty() || TaskFlow.ForeignCarried(labor) == 0)
		{
			return;
		}
		if (!TaskFlow.CanStoreForeign(labor))
		{
			__result = false;
			errorDestination?.AddDecisionError(SharedWorkException<Exceptions.BagTooFullToDig>.Get.Problem, labor);
		}
	}
}
