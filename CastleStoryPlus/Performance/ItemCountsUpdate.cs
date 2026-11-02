#if UNITY6
using System.Collections.Generic;
using Brix.NewUI.OverheadDisplay;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryPlus.Performance;

// Every stockpile's overhead icon has an ItemCounts line whose Update only sets a counter back to zero (what was
// removed this frame, read by SetOrAddResource); Unity still called it on every one of them every frame (some 300 in a
// big base, 0.1 ms a frame of call overhead alone). The Update is switched off on its first call; the counter is put
// back to zero in SetOrAddResource instead when a new frame has begun since the last call.
[Feature(Features.FrameSavers, Features.FrameSaversInfo)]
[HarmonyPatch]
internal static class ItemCountsUpdate
{
	private static readonly AccessTools.FieldRef<ItemCounts, int> RemovedThisFrame = AccessTools.FieldRefAccess<ItemCounts, int>("removedThisFrame");

	// The frame in which each line's counter was last put back to zero.
	private static readonly Dictionary<ItemCounts, int> Frame = new Dictionary<ItemCounts, int>();

	private static void Enable()
	{
		GameSession.OnLeave(Frame.Clear);
	}

	[HarmonyPatch(typeof(ItemCounts), "Update")]
	[HarmonyPrefix]
	private static bool Update(ItemCounts __instance)
	{
		__instance.enabled = false;
		RemovedThisFrame(__instance) = 0;
		Frame[__instance] = Time.frameCount;
		return false;
	}

	[HarmonyPatch(typeof(ItemCounts), nameof(ItemCounts.SetOrAddResource))]
	[HarmonyPrefix]
	private static void SetOrAddResource(ItemCounts __instance)
	{
		if (__instance.enabled)
		{
			// Its own Update still runs and resets the counter.
			return;
		}
		int frame = Time.frameCount;
		if (!Frame.TryGetValue(__instance, out int last) || last != frame)
		{
			Frame[__instance] = frame;
			RemovedThisFrame(__instance) = 0;
		}
	}
}
#endif
