using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using BepInEx.Configuration;
using Brix.Game.AI;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryPlus.Pathfinding;

// Route searches share a time budget per frame. Every bricktron and enemy with a search running takes a slice of it
// each update (100 nodes for a worker, 50 for an enemy), all on the main thread, with nothing limiting how many do so
// in the same frame: a wave of enemies or a crowd of workers starting searches together made one frame take as long
// as all their slices. Once the frame's searches have taken the budget, the others wait for the next frame (their
// search goes on where it was, the route is the same, only found a frame later). A search that had to wait a few
// frames in a row runs anyway, so none is left waiting while others take the budget every frame.
// The searches cannot move to a worker thread: they call into the game's objects (whether a voxel holds a blocking
// object, whether the target is reached) and read the navigation graph the main thread changes when terrain does.
// A single slice that takes far longer than its nodes can explain (a garbage collection or a graph rebuild inside it)
// is logged, with the garbage collections during it, to find where a hitch came from.
[Feature(Features.Pathfinding, Features.PathfindingInfo)]
[HarmonyPatch]
internal static class SearchFrameBudget
{
	private const int DefaultBudgetMs = 6;

	// Frames in a row a search may be held back before it runs regardless of the budget.
	private const int MostFramesWaiting = 3;

	private const double SlowSliceMs = 100;

	internal static ConfigEntry<int> BudgetMs;

	// Set while DevTools runs a search to the end in one frame (PathProbe): no budget then.
	internal static bool Unlimited;

	private static readonly Dictionary<SearchPathRequest, int> Waiting = new Dictionary<SearchPathRequest, int>();

	private static int _frame = -1;

	private static long _spent;

	// Garbage collections when the running slice started (slices do not nest).
	private static int _gcsAtStart;

	private static void Enable()
	{
		BudgetMs = Plugin.Cfg.Bind("Pathfinding", "SearchFrameBudgetMs", DefaultBudgetMs, new ConfigDescription("Milliseconds per frame all route searches together may take; the rest wait for the next frame. Lower keeps the frame rate even when many search at once, higher finds their routes a few frames sooner.", new AcceptableValueRange<int>(2, 50)));
	}

	private static IEnumerable<MethodBase> TargetMethods()
	{
		yield return AccessTools.Method(typeof(SearchPathRequest), "Work");
		yield return AccessTools.Method(typeof(SearchPathRequest), "LoopImprovePath");
	}

	private static long BudgetTicks()
	{
		int ms = (BudgetMs != null) ? BudgetMs.Value : DefaultBudgetMs;
		return ms * Stopwatch.Frequency / 1000;
	}

	// __state: when the slice started, 0 when it is held back (or not measured).
	[HarmonyPriority(Priority.First)]
	private static bool Prefix(SearchPathRequest __instance, ref bool __result, out long __state)
	{
		if (Unlimited)
		{
			__state = 0;
			return true;
		}
		int frame = Time.frameCount;
		if (frame != _frame)
		{
			_frame = frame;
			_spent = 0;
			if (Waiting.Count > 64)
			{
				Waiting.Clear();
			}
		}
		if (_spent >= BudgetTicks())
		{
			Waiting.TryGetValue(__instance, out int waited);
			if (waited < MostFramesWaiting)
			{
				Waiting[__instance] = waited + 1;
				__result = false;
				__state = 0;
				return false;
			}
		}
		Waiting.Remove(__instance);
		_gcsAtStart = System.GC.CollectionCount(0);
		__state = Stopwatch.GetTimestamp();
		return true;
	}

	private static void Postfix(SearchPathRequest __instance, MethodBase __originalMethod, long __state)
	{
		if (__state == 0)
		{
			return;
		}
		long ticks = Stopwatch.GetTimestamp() - __state;
		_spent += ticks;
		double ms = ticks * 1000.0 / Stopwatch.Frequency;
		if (ms >= SlowSliceMs)
		{
			Navigation navigation = __instance.Navigation;
			Location location = __instance.Location;
			Plugin.Log.LogWarning("SearchFrameBudget: one route search slice (" + __originalMethod.Name + ") took " + ms.ToString("0") + " ms, " + (System.GC.CollectionCount(0) - _gcsAtStart) + " garbage collections during it; unit " + ((navigation != null) ? navigation.gameObject.name : "?") + ", target " + ((location != null && location.Object != null) ? location.Object.name : ((location != null) ? location.GetType().Name : "?")) + ", " + __instance.Searched + " coarse and " + __instance.ImproveSearched + " fine nodes so far");
		}
	}
}
