#if UNITY6
using System;
using System.Runtime.CompilerServices;
using Brix.Engine;
using Brix.Game.AI;
using Brix.Game.Components.Volumes;
using Brix.Game.Locomotion;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryPlus.Pathfinding;

// Bricktrons paused below a step or a cliff, and two bricktrons on the same step could hold each other up (one
// climbing, one stepping down, or one standing on the edge where the other climbs). A walking bricktron passes through
// allies, but it stops before every climb up and step down and starts it from the in place state (InPlace.Update),
// which asks for a free voxel with no passing through allies at all (IsBlockedByCharacterOrObstacle(false)); the climb
// and step states themselves (ClimbUp, StepDown) let a worker pass an ally. With an ally in the voxel above, the game
// searched the route's next stretch again (RepathMoveNext), which with PassThroughAllies always finds the same climb
// through the ally, and tried again the next frame: the bricktron stood there, searching every frame, until the ally
// moved off or the walk's own stuck check (FollowPathRequest: 5 s without moving) gave the walk up and searched it all
// again, often the same way. Two bricktrons each waiting for the other's voxel waited for good.
// The climb or step into a voxel where only allies stand is now started from the in place state as well, as the game
// starts it when the voxel is free (turned towards it first): at once for a bricktron that may pass allies
// (Profession.CanPassThrough, a worker at work), the same rule the climb and the walk use. A unit that may not
// (soldiers, an idle worker, corruptrons) waits up to PatienceSeconds for the ally to move off, then gives the walk up
// as the game does when its way is blocked (PathBlockedAt), so CrowdPathing closes the ally's voxel and its next route
// climbs beside it. An enemy, a door or another obstacle in the voxel is left to the game, as before.
[Feature(Features.PassThroughAllies, Features.PassThroughAlliesInfo)]
[HarmonyPatch(typeof(InPlace), nameof(InPlace.Update))]
[HarmonyPriority(Priority.Low)]
internal static class ClimbPastAllies
{
	// How long a unit that may not pass allies waits below (or above) one before it looks for another way.
	internal const float PatienceSeconds = 1f;

	private sealed class Wait
	{
		public XYZ Voxel;

		public float Since;

		public float Last;
	}

	private static readonly ConditionalWeakTable<Locomotion4, Wait> Waits = new ConditionalWeakTable<Locomotion4, Wait>();

	private static readonly Func<IVolume, VoxelPresence, bool> IsOtherNonAllyFunc = IsOtherNonAlly;

	private static readonly Func<IVolume, VoxelPresence, bool> IsOtherAllyFunc = IsOtherAlly;

	// DevTools: climbs and steps started past an ally, and waits given up.
	internal static int Passed;

	internal static int GaveUp;

	private static bool Prefix(InPlace __instance, bool __runOriginal)
	{
		if (!__runOriginal)
		{
			return false;
		}
		Locomotion4 locomotion = __instance.Locomotion;
		DestinationIterator path = locomotion.PathIterator;
		if (!path.HasCurrent || __instance.animator.IsInTransition(0))
		{
			return true;
		}
		WalkNode current = path.Current;
		// Only a climb up or a step down that the game would start now (not a node it stands on, and not one that
		// must fail or breaks a block).
		if (current == null || __instance.IsOnSpot(current) || current.MustFail() || !(current.MustClimbUp() || current.MustStepDown()))
		{
			return true;
		}
		VoxelPresence self = locomotion.presence;
		XYZ entering = locomotion.PositionEntering;
		if (self == null || entering == XYZ.zero || entering != current.Voxel)
		{
			return true;
		}
		// Nobody there (the game climbs), or someone the game must judge: an enemy or a neutral unit, a door or other
		// obstacle.
		if (!VolumeRegistry.HasVolumesWithPropertyAt(entering, VProperty.character, IsOtherAllyFunc, self) || VolumeRegistry.HasVolumesWithPropertyAt(entering, VProperty.character, IsOtherNonAllyFunc, self) || __instance.IsBlockedByDynamicObstacle())
		{
			return true;
		}
		Labor labor = locomotion.Labor;
		bool mayPass = labor != null && labor.Profession != null && labor.Profession.CanPassThrough();
		if (!mayPass)
		{
			Wait wait = Waits.GetOrCreateValue(locomotion);
			float now = Time.time;
			// A new wait when it waits for another voxel, or has not waited here a moment ago.
			if (wait.Voxel != entering || now - wait.Last > 0.5f)
			{
				wait.Voxel = entering;
				wait.Since = now;
			}
			wait.Last = now;
			if (now - wait.Since < PatienceSeconds)
			{
				return false;
			}
			wait.Voxel = XYZ.zero;
			GaveUp++;
			locomotion.PathBlockedAt(current.Position);
			return false;
		}
		if (!__instance.IsTowards(current))
		{
			__instance.TurnOnSpot.TurnToDestinationPosition(current);
			return false;
		}
		if (current.MustClimbUp())
		{
			if (!locomotion.CanClimb)
			{
				// The game's own answer (it cannot climb with what it carries).
				return true;
			}
			__instance.DoClimbUp();
		}
		else
		{
			__instance.DoStepDown();
		}
		Passed++;
		return false;
	}

	private static bool IsOtherAlly(IVolume volume, VoxelPresence self)
	{
		VoxelPresence other = (volume != null) ? volume.GetComponent<VoxelPresence>() : null;
		return other != null && other != self && other.affiliation != null && other.affiliation.IsAllied(self);
	}

	private static bool IsOtherNonAlly(IVolume volume, VoxelPresence self)
	{
		VoxelPresence other = (volume != null) ? volume.GetComponent<VoxelPresence>() : null;
		return other != null && other != self && (other.affiliation == null || !other.affiliation.IsAllied(self));
	}
}
#endif
