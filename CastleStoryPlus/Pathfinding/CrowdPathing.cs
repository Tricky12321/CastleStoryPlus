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
// a few seconds, so it walks round the ally; with no other way the search fails as it did before PassThroughAllies.
// It reads only what the game's own searches read, with the game time, and nothing is sent to other players.
[Feature(Features.CrowdPathing, Features.CrowdPathingInfo)]
[HarmonyPatch(typeof(Locomotion4), nameof(Locomotion4.PathBlockedAt))]
internal static class CrowdPathing
{
	// How long a blocking ally's voxel stays closed to the unit it blocked, in game seconds.
	internal const float ClosedSeconds = 5f;

	// The most voxels closed to one unit at a time (the oldest gives way to a new one).
	internal const int MaxClosed = 4;

	// A unit's closed voxels and the wrappers round its presence tests.
	internal sealed class Closure
	{
		public readonly XYZ[] Voxels = new XYZ[MaxClosed];

		public readonly float[] Until = new float[MaxClosed];

		public int Count;

		public readonly List<Func<XYZ, bool>> Inner = new List<Func<XYZ, bool>>(3);

		public readonly List<Func<XYZ, bool>> Wrapped = new List<Func<XYZ, bool>>(3);

		public void Close(XYZ voxel, float until)
		{
			int slot = Array.IndexOf(Voxels, voxel);
			if (slot < 0)
			{
				slot = Array.IndexOf(Voxels, XYZ.zero);
			}
			if (slot < 0)
			{
				slot = 0;
				for (int i = 1; i < MaxClosed; i++)
				{
					if (Until[i] < Until[slot])
					{
						slot = i;
					}
				}
			}
			if (Voxels[slot] == XYZ.zero)
			{
				Count++;
			}
			Voxels[slot] = voxel;
			Until[slot] = until;
		}

		private bool IsClosed(XYZ p)
		{
			for (int i = 0; i < MaxClosed; i++)
			{
				if (Voxels[i] != XYZ.zero && p == Voxels[i])
				{
					if (Time.time < Until[i])
					{
						return true;
					}
					Voxels[i] = XYZ.zero;
					Count--;
				}
			}
			return false;
		}

		public Func<XYZ, bool> Wrap(Func<XYZ, bool> inner)
		{
			for (int i = 0; i < Inner.Count; i++)
			{
				if (Inner[i] == inner)
				{
					return Wrapped[i];
				}
			}
			Func<XYZ, bool> wrapped = (XYZ p) => (Count == 0 || !IsClosed(p)) && inner(p);
			Inner.Add(inner);
			Wrapped.Add(wrapped);
			return wrapped;
		}
	}

	internal static readonly ConditionalWeakTable<VoxelPresence, Closure> Closures = new ConditionalWeakTable<VoxelPresence, Closure>();

	private static readonly Func<IVolume, VoxelPresence, bool> BlocksFunc = Blocks;

	// For the DevTools: voxels closed so far.
	internal static int Closed;

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
		Closures.GetOrCreateValue(presence).Close(voxel, Time.time + ClosedSeconds);
		Closed++;
		if (StraightPaths.Watched != null && __instance.transform == StraightPaths.Watched)
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
#endif
