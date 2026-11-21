using System.Collections.Generic;
using Brix.Engine;
using Brix.External.Factories;
using Brix.Game.AI;
using Brix.Game.Components;
using CastleStoryPlus.Core;
using HarmonyLib;
using Motus.Behavior;
using UnityEngine;

namespace CastleStoryPlus.WorkerAI;

// A quarry keeps its stair. The quarry's goal list knows the stair's steps only from its own list of them, made
// again when the quarry's settings change; a committed quarry whose list is empty (as when it was not made again
// after the quarry was set up or loaded) treated the steps as ground and dug them away, leaving holes no bricktron
// can climb. The list is now made again whenever it is empty in a committed quarry with steps to cut (not an ore
// mine, which has none). And a step's task was thrown away and made anew every 15 seconds while it waited (the
// game always replaces a step's task), so a worker on its way there lost it and turned back; it is now kept while
// its step and direction stay the same.
//
// Only the quarry's own goal list kept the steps: it gives no dig task for a step or for the ground right under one
// (which holds the step up; the game's own "make way" digging leaves both alone too). Digging the mod added did not:
// a worker digging its way out of a pit (StuckEscape: terrain only, so not a cut step itself) took the ground under a
// step, and so the step, or the ground of a step not cut yet (it can then never be cut: the quarry cuts a step only
// out of ground, and nothing fills the hole); an ore mine took ore there (in quarry walls, where it looks for it);
// and another quarry or tunnel over the same ground, or a dig task left from before the list was made again (after a
// load), dug whatever stood at its voxel, a cut step included. Now, as long as the quarry stands, its steps (cut, or
// still to be cut by its list) and the ground under each are kept from all of them (Keeps): no dig task is made
// there, no worker takes or goes on with one there, and a worker digging out of a pit goes round them.
//
// And a quarry's floor is not dug lower than its stair goes (TooDeep): a stair with a step dug away ends above it, and
// the floor under that end was dug on down, two and three blocks under the lowest step left, so its diggers stood in a
// pit with no way back out. No dig task is made there now, and none left from before is taken, until the stair goes
// down that far again.
[Feature(Features.WorkerAI, Features.WorkerAIInfo)]
[HarmonyPatch(typeof(QuarryGoalProvider2), "VoxelNeedsGoal")]
internal static class QuarryStairs
{
	private static readonly List<QuarryGoalProvider2> Quarries = new List<QuarryGoalProvider2>();

	private static int _quarriesFrame = -1;

	// The list of steps Keeps looks at, and its test (made once: no closure for every cell asked).
	private static Dictionary<XYZ, SpawnOrder> _steps;

	private static readonly System.Func<int, int, int, bool> IsStepAt = (int x, int y, int z) => IsStep(_steps, new XYZ(x, y, z));

	// Whether a cell is a step of a standing quarry (cut, or still to be cut) or the ground right under one.
	internal static bool Keeps(XYZ pos)
	{
		if (!Plugin.IsEnabled(Features.WorkerAI))
		{
			return false;
		}
		List<QuarryGoalProvider2> quarries = StandingQuarries();
		for (int i = 0; i < quarries.Count; i++)
		{
			QuarryGoalProvider2 quarry = quarries[i];
			if (quarry.IsNullOrReleased())
			{
				continue;
			}
			VoxelBounds box = quarry.Bounds;
			if (!QuarryStairRules.Near(pos.x, pos.y, pos.z, box.min.x, box.min.y, box.min.z, box.max.x, box.max.y, box.max.z))
			{
				continue;
			}
			MakeStepList(quarry);
			_steps = quarry.stairs;
			bool kept = QuarryStairRules.Keeps(pos.x, pos.y, pos.z, IsStepAt);
			_steps = null;
			if (kept)
			{
				return true;
			}
		}
		return false;
	}

	// Each quarry's lowest step a worker can still walk down to (QuarryStairRules.LowestStep), worked out once a frame.
	private static readonly Dictionary<QuarryGoalProvider2, int> LowestSteps = new Dictionary<QuarryGoalProvider2, int>();

	private static int _lowestFrame = -1;

	// Whether a dig at the cell would take a quarry's floor lower than its stair goes down (QuarryStairRules.TooDeep).
	// Only a quarry whose box holds the cell's column is asked; null: any standing quarry.
	internal static bool TooDeep(XYZ pos, QuarryGoalProvider2 only = null)
	{
		if (!Plugin.IsEnabled(Features.WorkerAI))
		{
			return false;
		}
		List<QuarryGoalProvider2> quarries = StandingQuarries();
		for (int i = 0; i < quarries.Count; i++)
		{
			QuarryGoalProvider2 quarry = quarries[i];
			if (quarry.IsNullOrReleased() || (only != null && quarry != only))
			{
				continue;
			}
			VoxelBounds box = quarry.Bounds;
			if (pos.x < box.min.x || pos.x > box.max.x || pos.z < box.min.z || pos.z > box.max.z || pos.y > box.max.y)
			{
				continue;
			}
			if (QuarryStairRules.TooDeep(pos.y, LowestStep(quarry)))
			{
				return true;
			}
		}
		return false;
	}

	// Whether a worker with its feet at the cell stands in a standing quarry (in its box: on its floor or its stair).
	internal static bool InQuarry(XYZ feet)
	{
		List<QuarryGoalProvider2> quarries = StandingQuarries();
		for (int i = 0; i < quarries.Count; i++)
		{
			QuarryGoalProvider2 quarry = quarries[i];
			if (quarry.IsNullOrReleased())
			{
				continue;
			}
			VoxelBounds box = quarry.Bounds;
			if (feet.x >= box.min.x && feet.x <= box.max.x && feet.z >= box.min.z && feet.z <= box.max.z && feet.y >= box.min.y && feet.y <= box.max.y)
			{
				return true;
			}
		}
		return false;
	}

	private static int LowestStep(QuarryGoalProvider2 quarry)
	{
		if (_lowestFrame != Time.frameCount)
		{
			_lowestFrame = Time.frameCount;
			LowestSteps.Clear();
		}
		if (LowestSteps.TryGetValue(quarry, out int lowest))
		{
			return lowest;
		}
		MakeStepList(quarry);
		List<XYZ> steps = new List<XYZ>(quarry.stairs.Keys);
		steps.Sort((XYZ a, XYZ b) => b.y.CompareTo(a.y));
		int[] ys = new int[steps.Count];
		bool[] gone = new bool[steps.Count];
		for (int i = 0; i < steps.Count; i++)
		{
			ys[i] = steps[i].y;
			// Dug away: an empty cell with ground under it (a step planned over open air, as on a hillside, is no gap).
			gone[i] = Voxel.IsEmpty(steps[i]) && !Voxel.IsEmpty(steps[i] - XYZ.up);
		}
		lowest = QuarryStairRules.LowestStep(ys, gone);
		LowestSteps[quarry] = lowest;
		return lowest;
	}

	// The quarries that keep a stair: committed, not ore mines (which have none). Gathered once a frame.
	private static List<QuarryGoalProvider2> StandingQuarries()
	{
		if (_quarriesFrame == Time.frameCount)
		{
			return Quarries;
		}
		_quarriesFrame = Time.frameCount;
		Live<QuarryGoalProvider2>.Active(Quarries);
		Quarries.RemoveAll((QuarryGoalProvider2 quarry) => quarry.IsNullOrReleased() || quarry.data == null || !quarry.data.Commited || Metallurgy.OreMine.IsOreMine(quarry.data));
		return Quarries;
	}

	private static bool IsStep(Dictionary<XYZ, SpawnOrder> steps, XYZ pos)
	{
		return steps.ContainsKey(pos) || QuarryGoalProvider2.IsStair(pos);
	}

	// A committed quarry's list of steps, made again when it is empty (after a load, before the quarry's first look).
	private static void MakeStepList(QuarryGoalProvider2 quarry)
	{
		if (quarry.stairs.Count == 0 && quarry.data != null && quarry.data.Commited && !Metallurgy.OreMine.IsOreMine(quarry.data))
		{
			quarry.RefreshStairs();
		}
	}

	[HarmonyPriority(Priority.High)]
	private static void Prefix(QuarryGoalProvider2 __instance)
	{
		MakeStepList(__instance);
	}

	private static void Postfix(QuarryGoalProvider2 __instance, XYZ pos, ref SpawnOrder __result)
	{
		// Another quarry's step, or the ground under it (where quarries overlap): not dug by this one either.
		if (__result.key == Test.DigGoal && (Keeps(pos) || TooDeep(pos, __instance)))
		{
			__result = SpawnOrder.None;
			return;
		}
		if (!__result.alwaysReplace || __result.key != Test.BuildStairsGoal)
		{
			return;
		}
		if (__instance.spawnPerVoxel.TryGetValue(pos, out SpawnOrder current) && current.key == __result.key && current.direction == __result.direction && current.instance != null && current.IsValid())
		{
			__result.alwaysReplace = false;
		}
	}
}

// The quarries, for QuarryStairs.Keeps.
[Feature(Features.WorkerAI, Features.WorkerAIInfo)]
[HarmonyPatch(typeof(QuarryGoalProvider2), nameof(QuarryGoalProvider2.Initialize))]
internal static class QuarryStairsLivePatch
{
	private static void Postfix(QuarryGoalProvider2 __instance)
	{
		Live<QuarryGoalProvider2>.Add(__instance);
	}
}

// No worker takes a dig task on a quarry's step or the ground under it (one of another quarry, a tunnel, a task
// left from before the quarry's list of steps was made again), nor one of a quarry's floor lower than its stair goes.
[Feature(Features.WorkerAI, Features.WorkerAIInfo)]
[HarmonyPatch(typeof(DigGoal), nameof(DigGoal.IsMatchFor))]
internal static class QuarryStairsDigMatchPatch
{
	private static void Postfix(DigGoal __instance, Labor labor, AIErrorsStore errorDestination, ref bool __result)
	{
		if (!__result || __instance.IsNullOrReleased() || (!QuarryStairs.Keeps(__instance.voxel.position) && (StuckEscape.IsEscapeDig(__instance) || !QuarryStairs.TooDeep(__instance.voxel.position))))
		{
			return;
		}
		__result = false;
		if (errorDestination != null && labor != null)
		{
			errorDestination.AddDecisionError(SharedWorkException<Exceptions.GoalUnavailable>.Get.Problem, labor);
		}
	}
}

// A dig task already under way (or sent straight to a worker) on a quarry's step or the ground under it ends there,
// as if it were dug: no swing of the pick reaches the step.
[Feature(Features.WorkerAI, Features.WorkerAIInfo)]
[HarmonyPatch(typeof(DigGoal), "VoxelWorkIsDone")]
internal static class QuarryStairsDigDonePatch
{
	private static void Postfix(DigGoal goal, ref bool __result)
	{
		if (!__result && goal != null && !goal.IsNullOrReleased() && QuarryStairs.Keeps(goal.voxel.position))
		{
			__result = true;
		}
	}
}
