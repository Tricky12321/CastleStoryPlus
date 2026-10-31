using System.Collections.Generic;
using System.Reflection;
using Brix.Engine;
using Brix.Engine.Nature;
using Brix.Game.AI;
using CastleStoryPlus.Core;
using HarmonyLib;
using Motus.Behavior;

namespace CastleStoryPlus.Performance;

// A task's check before a worker is given its work (Project.VerifyWorkFor) asks, for every kind of thing the task
// yields, whether the worker has a stockpile to store it in (BestRecepteurToStore, about 0.75 ms each), and for every
// kind of thing it needs, whether the worker can find some (Labor.Find, a search of the known stockpiles and loose
// items, about 0.5 ms each). The check passes when one of them is found, so all answers go into one flag that is only
// ever set (true for the task's "any storage, any resource" check, false for a quarry's or tunnel's "storage for
// everything" check). The game asked every kind also after the flag was set, and since FasterTaskSearch all of them
// in one tick: a build task needing five materials cost some 2.5 ms in one frame per worker asking, a harvest task's
// yields as many times 0.75 ms. A check whose answer can no longer change the flag is now left out; the flag, and
// with it whether the task passes, is the same. What the left-out searches would have remembered for the worker's
// decision round (its storage and resource caches) is searched when the goal search asks for it, as for any kind not
// asked here. With FasterTaskSearch off, a left-out kind also no longer waits its tick.
[Feature(Features.FrameSavers, Features.FrameSaversInfo)]
[HarmonyPatch]
internal static class VerifyWorkShortCut
{
	private static IEnumerable<MethodBase> TargetMethods()
	{
		yield return AccessTools.Method(typeof(Project), "VerifyOneStorageFor");
		yield return AccessTools.Method(typeof(Project), "VerifyResourceAccessFor");
	}

	// These only ever set the flag to true.
	private static bool Prefix(Var<bool> result, ref Node __result)
	{
		if (result == null || !result.Get())
		{
			return true;
		}
		__result = Node.Empty;
		return false;
	}
}

// The quarry's and tunnel's "storage for everything" check only ever sets its flag to false.
[Feature(Features.FrameSavers, Features.FrameSaversInfo)]
[HarmonyPatch(typeof(Project), "VerifyStorageForAll")]
internal static class VerifyAllStorageShortCut
{
	private static bool Prefix(Var<bool> result, ref Node __result)
	{
		if (result == null || result.Get())
		{
			return true;
		}
		__result = Node.Empty;
		return false;
	}
}

// A harvest task's number of goals (HarvestGoalProvider.GoalCount: the plants, boulders and ore in its circle of the
// kinds it harvests) walked all its goals through a Slinq pipeline with two delegate calls per goal (the filter and
// the game's compiled HasFlag). It is asked over and over: by every idle worker for every task each time it sorts the
// tasks by priority, by the job board for every task each tick a worker looks for work, and by HasWorkFor. It is now
// counted with a plain loop and the same flag test ((accepted & kind) == kind), so the count is the same.
[Feature(Features.FrameSavers, Features.FrameSaversInfo)]
[HarmonyPatch(typeof(HarvestGoalProvider), nameof(HarvestGoalProvider.GoalCountOfType))]
internal static class HarvestGoalCount
{
	private static bool Prefix(HarvestGoalProvider __instance, NatureType type, ref int __result)
	{
		Dictionary<XYZ, NatureHarvestGoal> goals = __instance._goals;
		if (goals == null)
		{
			return true;
		}
		long accepted = (long)type;
		int count = 0;
		foreach (KeyValuePair<XYZ, NatureHarvestGoal> pair in goals)
		{
			long kind = (long)pair.Value.NatureType;
			if ((accepted & kind) == kind)
			{
				count++;
			}
		}
		__result = count;
		return false;
	}
}
