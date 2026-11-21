#if UNITY6
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Brix.Engine;
using Brix.Game.AI;
using Brix.Game.Components.Volumes;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryPlus.Pathfinding;

// With PassThroughAllies allies never block a route search, but the walk itself still lets only a working builder
// into a voxel where an ally stands (Profession.CanPassThrough): a climb up or a step down into it halts for soldiers,
// corruptrons and biftrons, and so does a step towards an ally standing still when the short repath fails. Such a unit
// found its walk blocked, searched its route again, got the same route through the ally, was blocked again and gave
// its order up, standing behind the ally (#102: corruptrons below a ledge with a biftron standing still on its edge
// never climbed the free ledge beside it). When a unit that cannot pass allies finds its walk blocked by one, the
// ally's voxel is now closed to that unit's own route searches (its next route, the short repath round a blocker) for
// a few seconds, so it walks round the ally.
// Where the ally's voxel is the only way (a stair or a passage one voxel wide between two levels) the closed search
// found no route: the unit stood on the stair until its walk gave up (PathExecutionFailed), or walked to the nearest
// place it could reach, back down the stair, and up again (workers on their idle walk, stockpile consolidation or
// store-or-drop between two terraces in a maintainer's save, 15 s and more on one stair voxel). A search that finds no
// route while it was refused a closed voxel now opens the unit's closed voxels again and searches once more; those
// voxels count as the only way for ChokeSeconds and are not closed again meanwhile, so the unit waits for the ally
// there (ClimbPastAllies) instead of turning back.
// It reads only what the game's own searches read, with the game time, and nothing is sent to other players.
[Feature(Features.CrowdPathing, Features.CrowdPathingInfo)]
[HarmonyPatch(typeof(Locomotion4), nameof(Locomotion4.PathBlockedAt))]
internal static class CrowdPathing
{
	// How long a blocking ally's voxel stays closed to the unit it blocked, in game seconds.
	internal const float ClosedSeconds = 5f;

	// How long a closed voxel that turned out to be the only way stays open to the unit, in game seconds.
	internal const float ChokeSeconds = 20f;

	// A unit's closed voxels and the wrappers round its presence tests.
	internal sealed class Closure
	{
		public readonly AllyClosures Voxels = new AllyClosures();

		public readonly List<Func<XYZ, bool>> Inner = new List<Func<XYZ, bool>>(3);

		public readonly List<Func<XYZ, bool>> Wrapped = new List<Func<XYZ, bool>>(3);

		public Func<XYZ, bool> Wrap(Func<XYZ, bool> inner)
		{
			for (int i = 0; i < Inner.Count; i++)
			{
				if (Inner[i] == inner)
				{
					return Wrapped[i];
				}
			}
			AllyClosures voxels = Voxels;
			Func<XYZ, bool> wrapped = (XYZ p) => (voxels.Count == 0 || !voxels.Refuses(Key(p), Time.time)) && inner(p);
			Inner.Add(inner);
			Wrapped.Add(wrapped);
			return wrapped;
		}
	}

	internal static readonly ConditionalWeakTable<VoxelPresence, Closure> Closures = new ConditionalWeakTable<VoxelPresence, Closure>();

	private static readonly Func<IVolume, VoxelPresence, bool> BlocksFunc = Blocks;

	// For the DevTools: voxels closed so far, voxels left open because they were the only way a moment ago, and
	// searches that found no route with closed voxels and searched again with them open.
	internal static int Closed;

	internal static int KeptOpen;

	internal static int Reopened;

	internal static long Key(XYZ p)
	{
		return AllyClosures.Key(p.x, p.y, p.z);
	}

	// Whether the voxel was the only way for the unit a moment ago (a search found no route with it closed).
	internal static bool IsChoke(VoxelPresence presence, XYZ voxel)
	{
		return presence != null && Closures.TryGetValue(presence, out Closure closure) && closure.Voxels.IsChoke(Key(voxel), Time.time);
	}

	private static void Prefix(Locomotion4 __instance, Vector3 position)
	{
		VoxelPresence presence = __instance.presence;
		if (presence == null || __instance.Labor == null || __instance.Labor.Profession == null || __instance.Labor.Profession.CanPassThrough())
		{
			return;
		}
		XYZ occupied = __instance.PositionOccupied;
		XYZ voxel = __instance.PositionEntering;
		if (!IsBlocking(voxel, occupied, presence))
		{
			voxel = XYZ.FromVector3(position);
			if (!IsBlocking(voxel, occupied, presence))
			{
				return;
			}
		}
		bool watched = StraightPaths.Watched != null && __instance.transform == StraightPaths.Watched;
		if (!Closures.GetOrCreateValue(presence).Voxels.Close(Key(voxel), Time.time, ClosedSeconds))
		{
			KeptOpen++;
			if (watched)
			{
				StraightPaths.WatchLog.Add(Time.frameCount + ": kept " + voxel + " open (the only way)");
			}
			return;
		}
		Closed++;
		if (watched)
		{
			StraightPaths.WatchLog.Add(Time.frameCount + ": closed " + voxel);
		}
	}

	private static bool IsBlocking(XYZ voxel, XYZ occupied, VoxelPresence self)
	{
		return voxel != XYZ.zero && voxel != occupied && VolumeRegistry.HasVolumesWithPropertyAt(voxel, VProperty.character, BlocksFunc, self);
	}

	// An ally other than the unit itself (an enemy blocks the search already).
	private static bool Blocks(IVolume volume, VoxelPresence self)
	{
		VoxelPresence other = (volume != null) ? volume.GetComponent<VoxelPresence>() : null;
		return other != null && other != self && other.affiliation != null && other.affiliation.IsAllied(self);
	}
}

// Every presence test the game builds for a unit (its route searches, its repaths) goes through its closure, which
// passes everything through while it has no closed voxel.
[Feature(Features.CrowdPathing, Features.CrowdPathingInfo)]
[HarmonyPatch(typeof(VoxelPresence), nameof(VoxelPresence.BuildPresenceTest))]
internal static class CrowdPathingPresencePatch
{
	private static void Postfix(VoxelPresence __instance, ref Func<XYZ, bool> __result)
	{
		if (__result != null)
		{
			__result = CrowdPathing.Closures.GetOrCreateValue(__instance).Wrap(__result);
		}
	}
}

// A route search starts counting the closed voxels it was refused afresh.
[Feature(Features.CrowdPathing, Features.CrowdPathingInfo)]
[HarmonyPatch(typeof(SearchPathRequest), "InitSearch")]
internal static class CrowdPathingSearchStart
{
	private static void Postfix(SearchPathRequest __instance)
	{
		if (__instance.Presence != null && CrowdPathing.Closures.TryGetValue(__instance.Presence, out CrowdPathing.Closure closure))
		{
			closure.Voxels.ResetRefused();
		}
	}
}

// A route search to a place (not away from one) that found no route after it was refused a closed ally voxel: the
// closed voxels were the only way. They are opened (and kept open for CrowdPathing.ChokeSeconds) and the search starts
// again from the beginning in the same request, its next slice in the next update, so the unit's walk goes on through
// the ally's voxel instead of failing or ending at the nearest place it could reach.
[Feature(Features.CrowdPathing, Features.CrowdPathingInfo)]
[HarmonyPatch(typeof(SearchPathRequest), "Work")]
internal static class CrowdPathingSearchAgain
{
	private static readonly Action<SearchPathRequest> InitSearch = AccessTools.MethodDelegate<Action<SearchPathRequest>>(AccessTools.Method(typeof(SearchPathRequest), "InitSearch"));

	private static void Postfix(SearchPathRequest __instance, ref bool __result)
	{
		if (!__result || !__instance.Done || __instance.SearchAway || __instance.Presence == null || __instance.Search == null)
		{
			return;
		}
		if (!(__instance.Accepter is SearchPathRequest.ResultAccepter accepter) || accepter.Result != null)
		{
			return;
		}
		if (!CrowdPathing.Closures.TryGetValue(__instance.Presence, out CrowdPathing.Closure closure) || closure.Voxels.Refused == 0)
		{
			return;
		}
		if (closure.Voxels.ReopenAsChokes(Time.time, CrowdPathing.ChokeSeconds) == 0)
		{
			return;
		}
		CrowdPathing.Reopened++;
		if (StraightPaths.Watched != null && __instance.Navigation != null && __instance.Navigation.transform == StraightPaths.Watched)
		{
			StraightPaths.WatchLog.Add(Time.frameCount + ": no route round the closed voxels after " + __instance.Searched + " nodes, searching again with them open");
		}
		InitSearch(__instance);
		__result = false;
	}
}
#endif
