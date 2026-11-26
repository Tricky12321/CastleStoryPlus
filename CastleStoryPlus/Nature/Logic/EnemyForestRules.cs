using System;

namespace CastleStoryPlus.Nature;

// The numbers of the enemies' way through trees (EnemyTreeClearing), as pure functions (no Unity, no game). Unit tests:
// CastleStoryPlus.UnitTests/Nature.
//   Route cost (#209): EnemyTreeClearing lets the enemies that break their way route through trees, and the game's
//   graph weighs a step beside a tree (a "forest" branch) 10, as much as a flat step on open grass, whatever the slope.
//   The enemies' A* (HeuristicPatch) takes the cheapest route by those weights, so a wave ran straight into a strip of
//   forest on its way and cut its way through tree by tree, though an open path ran beside it a few blocks away. A
//   forest step of a clearer's search now weighs the step penalty more, so the route takes the open ground round a forest
//   unless that is much longer; a row of trees with no way round is still crossed (and felled where it blocks).
//   Stuck: an enemy that stands still on its route in a forest fells the tree towards its next step; one that stands
//   still there with no tree to fell (an ally in the way, a step the walk cannot take), or still stands after the
//   strikes that should have felled it, searches its route again from where it stands, which now takes the shortest
//   way out of the forest.
internal static class EnemyForestRules
{
	// The game's weight of a forest step (TerrainPathNode.TryAddRegularNeighborBranch) and of a flat step.
	internal const int GameForestWeight = 10;

	// Extra weight of a forest step: 10 + 50 = 60, in the A*'s rank (length * 10 + weight * 2) 130 against a flat
	// step's 30, about 4 blocks of open ground per block of forest. Felling a tree in the way takes 4 s and more.
	internal const int DefaultStepPenalty = 50;

	internal const int MinStepPenalty = 0;

	internal const int MaxStepPenalty = 500;

	// A coarse place of the graph (level n) spans 2^n columns; its steps are charged for that many voxels, up to this.
	private const int MaxLodShift = 6;

	internal static int Clamp(int penalty)
	{
		return Math.Min(MaxStepPenalty, Math.Max(MinStepPenalty, penalty));
	}

	// The weight of a route up to a step: the game's (with whatever else was added) plus the penalty when the step is a
	// forest step, scaled by the size of the place stepped into.
	internal static int StepWeight(int weight, bool forest, int lod, int penalty)
	{
		if (!forest || penalty <= 0)
		{
			return weight;
		}
		int shift = Math.Max(0, Math.Min(MaxLodShift, lod));
		return weight + (penalty << shift);
	}

	// What a check of a clearer does (every CheckSeconds while it follows a route).
	internal enum StuckAction
	{
		None,
		Strike,
		Repath
	}

	// Checks in a row without getting on before a tree in the way is struck (1 s at 0.5 s a check).
	internal const int StrikeChecks = 2;

	// Checks in a row without getting on, with no tree to strike, before the route is searched again (3 s).
	internal const int RepathChecks = 6;

	// Checks in a row without getting on while striking before the route is searched again (8 s: a tree of 180 at 60
	// every 1.2 s falls in under 4 s, so it was not that tree that held the enemy up).
	internal const int StrikeRepathChecks = 16;

	// The action for a clearer that has not got on for stillChecks checks in a row: inForest whether it stands beside a
	// tree, treeAhead whether one lies towards its next route step, strikeReady whether its last strike is long enough
	// ago. Outside a forest nothing (the game's own walk handles it). The counter starts again after a repath.
	internal static StuckAction Decide(int stillChecks, bool inForest, bool treeAhead, bool strikeReady)
	{
		if (!inForest || stillChecks < StrikeChecks)
		{
			return StuckAction.None;
		}
		if (treeAhead)
		{
			if (stillChecks >= StrikeRepathChecks)
			{
				return StuckAction.Repath;
			}
			return strikeReady ? StuckAction.Strike : StuckAction.None;
		}
		return (stillChecks >= RepathChecks) ? StuckAction.Repath : StuckAction.None;
	}
}
