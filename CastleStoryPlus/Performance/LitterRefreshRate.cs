#if UNITY6
using System.Collections.Generic;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryPlus.Performance;

// Every litter goal provider (one per beacon whose workers pick up loose items) asks the tracking registry for every
// loose item in its radius on every AI tick (its tree runs Refresh with a 30 ms delay; 3-4 refreshes a frame over
// the providers of a base): 1.1 % of the main thread, with frames of 8 ms where the radius holds many items. Items
// appear and vanish far more slowly than that: a provider now refreshes at most every RefreshSeconds. The goals it
// has stay as they are in between; a gone item's goal is dropped at the next refresh, as before.
[Feature(Features.FrameSavers, Features.FrameSaversInfo)]
[HarmonyPatch(typeof(LitterGoalProvider), "Refresh")]
internal static class LitterRefreshRate
{
	private const float RefreshSeconds = 0.5f;

	private static readonly Dictionary<LitterGoalProvider, float> Last = new Dictionary<LitterGoalProvider, float>();

	private static void Enable()
	{
		GameSession.OnLeave(Last.Clear);
	}

	private static bool Prefix(LitterGoalProvider __instance)
	{
		float now = Time.time;
		if (Last.TryGetValue(__instance, out float last) && now - last < RefreshSeconds)
		{
			return false;
		}
		if (Last.Count > 512)
		{
			Prune();
		}
		Last[__instance] = now;
		return true;
	}

	private static void Prune()
	{
		List<LitterGoalProvider> gone = new List<LitterGoalProvider>();
		foreach (LitterGoalProvider provider in Last.Keys)
		{
			if (provider == null)
			{
				gone.Add(provider);
			}
		}
		foreach (LitterGoalProvider provider in gone)
		{
			Last.Remove(provider);
		}
	}
}
#endif
