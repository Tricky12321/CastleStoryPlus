using System.Diagnostics;
using Brix.Game.AI;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryPlus.WorkerAI;

// A worker looking for its next task checks the tasks of its project one by one and paused a tick whenever the
// game's estimated cost passed 1 unit. Each dig/mine voxel, tree or boulder is its own task costing 0.1-0.2
// units, so only 5-10 were checked per tick: in a big mining area with hundreds of voxels a worker stood idle
// for seconds after every block. The checks are cheap, so the pause now follows real time instead: all
// searches together get FrameBudgetMs of CPU time per frame, then continue next frame.
[Feature(Features.FasterTaskSearch, Features.FasterTaskSearchInfo)]
[HarmonyPatch(typeof(GoalSelector), nameof(GoalSelector.CheckEnoughTimeConsumed))]
internal static class TaskSearchBudget
{
	private const double FrameBudgetMs = 3.0;

	private static readonly Stopwatch Clock = Stopwatch.StartNew();

	private static int _frame = -1;

	private static double _frameStartMs;

	private static bool Prefix(GoalSelector.GoalSelectionData selectionData, ref bool __result)
	{
		double now = Clock.Elapsed.TotalMilliseconds;
		if (Time.frameCount != _frame)
		{
			_frame = Time.frameCount;
			_frameStartMs = now;
		}
		__result = now - _frameStartMs > FrameBudgetMs;
		if (__result)
		{
			selectionData.timeSkipped = selectionData.timeConsumed;
		}
		return false;
	}
}
