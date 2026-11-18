using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using Brix.Components;
using Brix.Engine;
using Brix.Engine.Blocks;
using Brix.Game.AI;
using Brix.Game.Locomotion;
using Brix.Pathfinding;
using Brix.Pathfinding.Test;
using CastleStoryPlus.Building;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryPlus.Pathfinding;

// Stone roads (the block is made by CustomBlocks): flat stone pieces laid on the ground, one stone block each, that
// every unit walks faster on, enemies too (so a route does not depend on who asks).
//   Block: a road does not fill its block (its floor is the block's bottom, level with the ground), so a unit stands
//   in the road's voxel as on plain ground, with no step up onto it and no step off it, and a road is not a wall.
//   Speed: a unit standing on a road walks and runs [Roads] SpeedBonus percent faster.
//   Routes: a step between two road voxels costs 1 / speed factor of a plain step, one between a road and plain
//   ground the mean of both. The game's graph has it in the weight of the branch (Unity 6); the navigation mesh has the
//   road tiles as an area of their own with that cost, so both searches take a road when it is quicker overall.
//   Nature: nobody's steps wear a road (WornPaths) and grass and plants cannot grow in its voxel (a block is in the
//   way, as for any building), so nothing more is needed there.
[Feature(Features.Roads, Features.RoadsInfo)]
internal static class Roads
{
	internal const string Name = "Stone_Road";

	// Kept in saves; never change.
	internal const int Id = 9014;

	internal static ConfigEntry<int> SpeedBonus;

	// Road blocks in the world; only a gate, the block engine has the last word.
	private static readonly HashSet<XYZ> Positions = new HashSet<XYZ>();

	private static void Enable()
	{
		SpeedBonus = Plugin.Cfg.Bind("Roads", "SpeedBonus", 40, new ConfigDescription("How much faster (percent) every unit walks and runs on a stone road; routes take a road when it is quicker overall. 0: roads are no faster than the ground.", new AcceptableValueRange<int>(0, 200)));
		GameSession.OnLeave(Positions.Clear);
		CustomBlocks.EnableRoadMenu();
	}

	// How many times faster a road is than the ground.
	internal static float Factor => 1f + ((SpeedBonus != null) ? SpeedBonus.Value : 40) / 100f;

	internal static void Added(XYZ position)
	{
		Positions.Add(position);
	}

	internal static void Removed(XYZ position)
	{
		Positions.Remove(position);
	}

	internal static IEnumerable<XYZ> All => Positions;

	internal static bool Any => Positions.Count > 0;

	// Whether the voxel holds a road block.
	internal static bool IsRoad(XYZ position)
	{
		if (Positions.Count == 0 || !Positions.Contains(position))
		{
			return false;
		}
		BlockEngine engine = BrixSingleton<BlockEngine>.Instance;
		return engine && engine.GetBlockID(position) == Id;
	}

	// The voxel a unit at this position stands in: its floor is the voxel's bottom (or higher), so the position is
	// nudged up before rounding.
	internal static XYZ VoxelAt(Vector3 position)
	{
		return XYZ.FromVector3(position + new Vector3(0f, 0.25f, 0f));
	}

	internal static bool IsOn(Transform unit)
	{
		return Positions.Count > 0 && unit != null && IsRoad(VoxelAt(unit.position));
	}

	// The weight of a plain step between two voxels, scaled by the road under them.
	internal static int Scale(int weight, XYZ from, XYZ to)
	{
		bool a = IsRoad(from);
		bool b = IsRoad(to);
		if (!a && !b)
		{
			return weight;
		}
		float cost = 1f / Factor;
		float share = ((a ? cost : 1f) + (b ? cost : 1f)) / 2f;
		return Mathf.Max(1, Mathf.RoundToInt(weight * share));
	}
}

// Keeps the road positions.
[Feature(Features.Roads, Features.RoadsInfo)]
[HarmonyPatch(typeof(BlockEngine), nameof(BlockEngine.OnBlockDataCreated))]
internal static class RoadsBlockAddedPatch
{
	private static void Prefix(IBlockData bd)
	{
		if (bd != null && bd.BlockInfo != null && bd.BlockInfo.ID == Roads.Id)
		{
			Roads.Added(bd.Position);
		}
	}
}

[Feature(Features.Roads, Features.RoadsInfo)]
[HarmonyPatch(typeof(BlockEngine), "OnBlockDataReleased")]
internal static class RoadsBlockRemovedPatch
{
	private static void Prefix(IBlockData ibd)
	{
		if (ibd != null && ibd.BlockInfo != null && ibd.BlockInfo.ID == Roads.Id)
		{
			Roads.Removed(ibd.Position);
		}
	}
}

// The road's floor is at the bottom of its block, level with the ground (the game's default is a full block).
[Feature(Features.Roads, Features.RoadsInfo)]
[HarmonyPatch(typeof(BlockInfo), nameof(BlockInfo.CalculateNiveaux))]
internal static class RoadsNiveauxPatch
{
	private static void Postfix(BlockInfo __instance, ref int __result)
	{
		if (__instance.ID == Roads.Id)
		{
			__result = 0;
		}
	}
}

// Faster walking and running on a road.
[Feature(Features.Roads, Features.RoadsInfo)]
[HarmonyPatch]
internal static class RoadsSpeedPatch
{
	private static IEnumerable<MethodBase> TargetMethods()
	{
		yield return AccessTools.PropertyGetter(typeof(Walking), "SpeedMperS");
		yield return AccessTools.PropertyGetter(typeof(Running), "SpeedMperS");
	}

	private static void Postfix(LocomotionWalkableCycleState __instance, ref float __result)
	{
		Locomotion4 locomotion = __instance.Locomotion;
		if (locomotion != null && Roads.Any && Roads.IsOn(locomotion.transform))
		{
			__result *= Roads.Factor;
		}
	}
}

#if UNITY6
// The game's route search: a plain step onto or along a road weighs less. The weights are made when a place's branches
// are (again) made, which the game does for the places round a changed block.
[Feature(Features.Roads, Features.RoadsInfo)]
[HarmonyPatch(typeof(TerrainPathNode), nameof(TerrainPathNode.Init))]
internal static class RoadsBranchPatch
{
	private static void Postfix(TerrainPathNode __instance)
	{
		PathBranch[] siblings = __instance.siblings;
		if (!Roads.Any || siblings == null || siblings.Length == 0)
		{
			return;
		}
		XYZ position = __instance.position;
		bool here = Roads.IsRoad(position);
		for (int i = 0; i < siblings.Length; i++)
		{
			if (siblings[i].Type != PathBranch.BranchType.Full || !(siblings[i].Node is TerrainPathNode target))
			{
				continue;
			}
			if (here || Roads.IsRoad(target.position))
			{
				siblings[i].weight = Roads.Scale(siblings[i].weight, position, target.position);
			}
		}
	}
}
#endif
