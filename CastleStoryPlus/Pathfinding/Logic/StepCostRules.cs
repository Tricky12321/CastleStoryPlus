using System;

namespace CastleStoryPlus.Pathfinding;

// The route weights of a step up or down between two places of the game's graph, as pure functions (no Unity, no
// game): StepCosts reads the settings and hands them to the game's branch builder, NavMeshRoutes asks which steps are
// steep. Unit tests: CastleStoryPlus.UnitTests/Pathfinding.
//   The game (TerrainPathNode.TryAddRegularNeighborBranch) weighs a step by the denivelation between the two places'
//   facing edges, in thirds of a block summed over the edge's two corners (a full block is 6): a flat step 10, a step
//   up 10 + 240 * |d| / 4 and a climb (|d| 4 or more) 10 + 240, a step down 10 + 160 * d / 4 and a drop 10 + 160.
//   Natural ground is a smooth surface, so walking it is almost always a flat step (d 0), while every edge of a
//   player's block or stair landing is a step: a climb onto a block weighed as much as 25 blocks of flat ground (the
//   A* ranks a step by length * 10 + weight * 2), though it takes a bricktron about as long as 5. With the mod's
//   shorter routes (HeuristicPatch, the navigation mesh) the searches find the cheapest route by these weights, so
//   bricktrons walked a long way round over the landscape instead of up a few steps the player had built. The two
//   penalties are now settings with defaults near the time a climb and a drop take.
internal static class StepCostRules
{
	// The game's flat step and its two penalties.
	internal const int FlatWeight = 10;

	internal const int GameClimbPenalty = 240;

	internal const int GameStepDownPenalty = 160;

	// From this denivelation (thirds of a block, both corners) a step up is a climb and a step down a drop.
	internal const int ClimbDenivelation = 4;

	// A climb up one block: about 1.5 s with the stop before it, as long as 5 blocks of walking at 3 a second (the A*'s
	// rank 10 + 2 * 70 = 150 against 30 a flat step). A drop: about 1 s, 3 blocks (10 + 2 * 40 = 90).
	internal const int DefaultClimbPenalty = 60;

	internal const int DefaultStepDownPenalty = 30;

	// The settings' range: up to the game's own weights.
	internal const int MinPenalty = 0;

	internal const int MaxPenalty = 400;

	internal static int Clamp(int penalty)
	{
		return Math.Min(MaxPenalty, Math.Max(MinPenalty, penalty));
	}

	// The weight of a step with this denivelation (positive: down, negative: up; under 10 either way, or the game makes
	// no step), with the game's arithmetic and the given penalties.
	internal static int Weight(int denivelation, int climbPenalty, int stepDownPenalty)
	{
		if (denivelation >= 0)
		{
			if (denivelation >= ClimbDenivelation)
			{
				return FlatWeight + stepDownPenalty;
			}
			return FlatWeight + stepDownPenalty * denivelation / 4;
		}
		if (denivelation <= -ClimbDenivelation)
		{
			return FlatWeight + climbPenalty;
		}
		return FlatWeight + climbPenalty * -denivelation / 4;
	}

	// Whether the step is a climb (true) or a drop, as the game types it; plain steps are neither.
	internal static bool IsClimb(int denivelation)
	{
		return denivelation <= -ClimbDenivelation;
	}

	internal static bool IsStepDown(int denivelation)
	{
		return denivelation >= ClimbDenivelation;
	}

	// The game's steep steps for NavMeshRoutes.Ease: a walking step at least this heavy (100 at the game's penalties: a
	// step up of 2 thirds or more, a step down of 3) is looked at for a cheaper way round. Kept at the same share (3/8)
	// of the climb penalty, so at the default penalties the same steps count as steep as at the game's.
	internal static int SteepWeight(int climbPenalty)
	{
		return FlatWeight + Math.Max(1, climbPenalty * 3 / 8);
	}
}
