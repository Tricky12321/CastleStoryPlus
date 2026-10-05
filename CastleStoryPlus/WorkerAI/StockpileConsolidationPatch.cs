using Brix.Game.AI;
using Brix.Game.AI.InstructionType;
using CastleStoryPlus.Core;
using HarmonyLib;
using Motus.Behavior;
using UnityEngine;

namespace CastleStoryPlus.WorkerAI;

// When an idle worker has nothing else to do, it tidies up stockpiles before going to sit at the gather point.
[Feature(Features.StockpileConsolidation, Features.StockpileConsolidationInfo)]
[HarmonyPatch(typeof(IdleProject), nameof(IdleProject._GetInstruction))]
internal static class StockpileConsolidationPatch
{
	private static bool Prefix(IdleProject __instance, Labor labor, Var<OrderPackage<Labor>> result, bool __runOriginal)
	{
		// Another idle chore (auto cleanup) has already given an order.
		if (!__runOriginal)
		{
			return false;
		}
		if (!labor.recepteur.IsEmpty() || !__instance.GatherAtIdlePoint(labor))
		{
			return true;
		}
		GameObject item = StockpileConsolidation.TryPlan(labor);
		if (item == null)
		{
			return true;
		}
		OrderPackage<Labor> package = Order.Package.OfGameObject(StockpileConsolidation.Consolidate, item, Labor.DecisionSource.Project);
		// Interruptible until the worker picks something up, so real work always wins.
		package.SetPrepare((BaseLabor worker) => __instance.Prepare(worker as Labor, false, Activity.Chilling));
		result.Set(package);
		return false;
	}
}

// A worker carrying a load of an idle chore (stockpile consolidation, auto cleanup) to its storage keeps doing it.
// The idle project gives every worker that carries something a "store or drop" order, which replaced the chore on
// the way: the load went back into the nearest stockpile (the one it was taken from), and the next idle round
// planned the same move again, so workers picked things up and put them back over and over.
// Shared by both chores (each only plans moves while its feature is on).
[Feature]
[HarmonyPatch(typeof(IdleProject), nameof(IdleProject._GetInstruction))]
internal static class IdleChoreCarryPatch
{
	[HarmonyPriority(Priority.First)]
	private static bool Prefix(Labor labor, Var<OrderPackage<Labor>> result)
	{
		if (labor == null || labor.recepteur.IsEmpty())
		{
			return true;
		}
		if (!StockpileConsolidation.IsMoving(labor) && !AutoCleanup.IsCarrying(labor))
		{
			return true;
		}
		result.Set(OrderPackage<Labor>.Empty);
		return false;
	}
}
