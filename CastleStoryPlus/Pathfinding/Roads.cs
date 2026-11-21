using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using Brix.Components;
using Brix.Engine;
using Brix.Engine.Blocks;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Game.Components.Volumes;
using Brix.Game.Locomotion;
using Brix.Legacy;
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
//   Routes (Unity 6 only): a step between two road voxels costs 1 / speed factor of a plain step, one between a road
//   and plain ground the mean of both. The game's graph has it in the weight of the branch (set so that the A*'s rank,
//   length * 10 + weight * 2, drops by the same share); the navigation mesh has the road tiles as an area that costs
//   1 where the other walking areas cost the speed factor, so both searches favour a road.
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
		SpeedBonus = Plugin.Cfg.Bind("Roads", "SpeedBonus", RoadRules.DefaultBonus, new ConfigDescription("How much faster (percent) every unit walks and runs on a stone road; in the Unity 6 port route searches favour roads by the same share. 0: roads are no faster than the ground. In multiplayer the host's value counts.", new AcceptableValueRange<int>(RoadRules.MinBonus, RoadRules.MaxBonus)));
		GameSession.OnLeave(Positions.Clear);
		CustomBlocks.EnableRoadMenu();
	}

	// How many times faster a road is than the ground (a client uses the host's setting, RoadsSync). Weights the graph
	// made are kept until its places are made again.
	internal static float Factor => RoadRules.Factor(RoadsSync.HostBonus, (SpeedBonus != null) ? SpeedBonus.Value : (int?)null);

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
		return XYZ.FromVector3(position + new Vector3(0f, RoadRules.VoxelNudge, 0f));
	}

	internal static bool IsOn(Transform unit)
	{
		return Positions.Count > 0 && unit != null && IsRoad(VoxelAt(unit.position));
	}

	// The weight of a plain step between two voxels, scaled by the road under them (the arithmetic is RoadRules.Scale).
	internal static int Scale(int weight, XYZ from, XYZ to)
	{
		return RoadRules.Scale(weight, IsRoad(from), IsRoad(to), Factor);
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

// A road can be laid right in front of a workshop. The forge, workbench, lab, loom, altar (and the mod's smithy, armoury,
// market and research station) claim the spot their worker stands on in front of them (occupy, also on their
// blueprints: WorkSpotReserve), and the game's obstruction check (VUtil.IsObstructed) refuses every blueprint on a
// claimed cell, so a road could be laid along a building's other sides but not along its front ("something is in the
// way"). A road is a floor the worker stands on as on the ground, so for a road blueprint such a work spot is not in the
// way (RoadRules.MayLieOn); everything else the game checks still is: the ground, a tree, a stockpile's goods, a
// building's body, a walk-in building's floor and another blueprint of the same team.
[Feature(Features.Roads, Features.RoadsInfo)]
[HarmonyPatch(typeof(VUtil), nameof(VUtil.IsObstructed), typeof(XYZ), typeof(VPropertyMask), typeof(Faction), typeof(IVolume))]
internal static class RoadsWorkSpotPatch
{
	// The bits of another volume's cell MayLieOn looks at.
	private static readonly VPropertyMask[] Bits = new VPropertyMask[7]
	{
		VPropertyMask.access,
		VPropertyMask.obstacle,
		VPropertyMask.occupy,
		VPropertyMask.walkable,
		VPropertyMask.operation,
		VPropertyMask.fantome,
		VPropertyMask.debloc
	};

	private static void Postfix(XYZ i, VPropertyMask obstructions, Faction faction, IVolume excludedVolume, ref bool __result)
	{
		if (!__result || !IsRoadBlueprint(excludedVolume))
		{
			return;
		}
		__result = Obstructed(i, obstructions, faction, excludedVolume);
	}

	private static bool IsRoadBlueprint(IVolume volume)
	{
		if (volume == null)
		{
			return false;
		}
		Brix.External.Factories.Factory.AssetKey key = volume.AssetKey;
		return key != null && key.Name == Roads.Name && volume.GameObject != null && volume.GameObject.GetComponent<Blueprint>() != null;
	}

	// The game's check (VUtil.IsObstructed) with a workshop's work spot left out.
	private static bool Obstructed(XYZ cell, VPropertyMask obstructions, Faction faction, IVolume self)
	{
		using (IVoxel voxel = Voxel.GetVoxel(cell))
		{
			if (!voxel.IsNull && !voxel.IsCrust && voxel.IsTerrain)
			{
				return true;
			}
		}
		if (TreeRegistry.Instance != null && TreeRegistry.Instance.IsNearATree(cell))
		{
			return true;
		}
		if (StockpileHeadroom.Blocks(cell, self.GameObject))
		{
			return true;
		}
		XYZ up = new XYZ(cell.x, cell.y + 1, cell.z);
		foreach (IVolume other in VolumeRegistry.VolumesWithPropertyAtExcluding(cell, obstructions, self))
		{
			if (other is BlueprintVolume && other.Faction != faction)
			{
				continue;
			}
			if (!RoadRules.MayLieOn(MaskAt(other, cell), MaskAt(other, up)))
			{
				return true;
			}
		}
		return false;
	}

	private static long MaskAt(IVolume volume, XYZ cell)
	{
		long mask = 0L;
		foreach (VPropertyMask bit in Bits)
		{
			if (volume.HasAnyPropertiesAt(bit, cell))
			{
				mask |= (long)bit;
			}
		}
		return mask;
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
