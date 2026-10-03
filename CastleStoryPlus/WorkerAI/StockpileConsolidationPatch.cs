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
	private static bool Prefix(IdleProject __instance, Labor labor, Var<OrderPackage<Labor>> result)
	{
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
