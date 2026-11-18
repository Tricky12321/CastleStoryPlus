using System;

namespace CastleStoryPlus.Pathfinding;

// The numbers of the stone roads, as pure functions (no Unity, no game): Roads and the patches read the settings and the
// world and call these. Unit tests: CastleStoryPlus.UnitTests/Pathfinding.
internal static class RoadRules
{
	// The setting's default and range (percent faster).
	internal const int DefaultBonus = 40;

	internal const int MinBonus = 0;

	internal const int MaxBonus = 200;

	// A unit's position is nudged up this far before it is rounded to a voxel (its floor is the voxel's bottom or higher).
	internal const float VoxelNudge = 0.25f;

	// The game's graph: a flat step weighs this and the A* ranks a step by length * 10 + weight * 2 (PathAStarQueue).
	internal const int FlatWeight = 10;

	// How many times faster a road is than the ground. The host's bonus counts when there is one (a client), else the
	// player's setting, else the default (the setting is not bound yet).
	internal static float Factor(int? hostBonus, int? setting)
	{
		return 1f + (hostBonus ?? setting ?? DefaultBonus) / 100f;
	}

	// A client takes the host's bonus, kept to the setting's range.
	internal static int ClampBonus(int percent)
	{
		return Math.Min(MaxBonus, Math.Max(MinBonus, percent));
	}

	// The weight of a plain step between two voxels, scaled by the road under them. A flat step has weight 10 and each
	// third of a block of slope adds to it; the A* ranks a step by length * 10 + weight * 2, so a flat step ranks 30,
	// and the road's share of that (30 / speed factor) is reached by a weight of 15 * share - 5. Only the flat step's
	// part is discounted, not the slope. Between road and road the share is 1 / factor, between road and ground the mean
	// of that and 1. Never under 1. No road at either end: the weight as it is.
	internal static int Scale(int weight, bool fromRoad, bool toRoad, float factor)
	{
		if (!fromRoad && !toRoad)
		{
			return weight;
		}
		float cost = 1f / factor;
		float share = ((fromRoad ? cost : 1f) + (toRoad ? cost : 1f)) / 2f;
		return Math.Max(1, weight - FlatWeight + RoundToInt(15f * share - 5f));
	}

	// Unity's Mathf.RoundToInt: halves go to the even number.
	internal static int RoundToInt(float value)
	{
		return (int)Math.Round(value, MidpointRounding.ToEven);
	}

	// The cost of a navigation mesh area for a search: the road costs 1, the area that cannot be walked (1) and the
	// others the speed factor, so a route keeps to a road where that is quicker. A factor of 1 (no bonus) leaves the
	// costs as they are (null); a cost under 1 would break Detour's heuristic, so the road is never given less than 1.
	internal static float? AreaCost(int area, int roadArea, int notWalkableArea, float factor)
	{
		if (factor <= 1.001f || area == roadArea || area == notWalkableArea)
		{
			return null;
		}
		return factor;
	}

	// The stockpile area's cost: its own times the speed factor (it is not a road).
	internal static float StockpileCost(float cost, float factor)
	{
		return cost * factor;
	}
}
