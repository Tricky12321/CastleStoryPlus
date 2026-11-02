#if UNITY6
using System.Collections.Generic;
using Brix.Game.AI;
using Brix.Legacy;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryPlus.Performance;

// Beacon.project walks up the beacon's parents with a GetComponent at each step to find its project, and is read
// some 70 times a frame (each beacon's overhead icon asks two or three times every frame, goal providers ask in their
// ticks). A beacon stays under its project: the answer is kept for RecheckSeconds and looked up again after that, or
// at once when the project it knew is gone.
[Feature(Features.FrameSavers, Features.FrameSaversInfo)]
[HarmonyPatch(typeof(Beacon), nameof(Beacon.project), MethodType.Getter)]
internal static class BeaconProjectCache
{
	private const float RecheckSeconds = 2f;

	private struct Known
	{
		public Project Project;

		public float Until;
	}

	private static readonly Dictionary<Beacon, Known> Cache = new Dictionary<Beacon, Known>();

	private static void Enable()
	{
		GameSession.OnLeave(Cache.Clear);
	}

	private static bool Prefix(Beacon __instance, ref Project __result)
	{
		float now = Time.time;
		// A beacon with no project keeps its null; a project destroyed since (a dead reference) is looked up again.
		if (Cache.TryGetValue(__instance, out Known known) && now < known.Until && (known.Project is null || known.Project != null))
		{
			__result = known.Project;
			return false;
		}
		Project project = __instance.GetComponentInParent<Project>(includeInactive: true);
		if (Cache.Count > 1024)
		{
			Cache.Clear();
		}
		Cache[__instance] = new Known { Project = project, Until = now + RecheckSeconds };
		__result = project;
		return false;
	}
}
#endif
