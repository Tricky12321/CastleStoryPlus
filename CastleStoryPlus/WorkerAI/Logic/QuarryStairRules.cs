using System;

namespace CastleStoryPlus.WorkerAI;

// The pure rules of what a quarry keeps of its stair (QuarryStairs), without the game.
internal static class QuarryStairRules
{
	// A cell no digging may take while the quarry stands: a stair step (cut, or still to be cut) or the ground right
	// under one, which holds the step up. The game's own quarry and its "make way" digging keep both; isStep tells
	// whether a cell is a step.
	internal static bool Keeps(int x, int y, int z, Func<int, int, int, bool> isStep)
	{
		return isStep(x, y, z) || isStep(x, y + 1, z);
	}

	// Whether a cell can hold a step of a quarry with this box, or the ground under one: inside the box, or right
	// under it (the ground under the lowest step lies under the box).
	internal static bool Near(int x, int y, int z, int minX, int minY, int minZ, int maxX, int maxY, int maxZ)
	{
		return x >= minX && x <= maxX && z >= minZ && z <= maxZ && y >= minY - 1 && y <= maxY;
	}

	// No lowest step: the floor is not held back.
	internal const int NoStep = int.MinValue;

	// The height of the lowest step a worker can still walk down to from the top: the steps' heights from the top down
	// (ys, highest first) and whether each was dug away (gone: an empty cell with ground under it, it can never be cut
	// again). The stair ends at the first step gone; NoStep for a quarry without steps.
	internal static int LowestStep(int[] ys, bool[] gone)
	{
		if (ys == null || ys.Length == 0)
		{
			return NoStep;
		}
		int lowest = ys[0];
		for (int i = 0; i < ys.Length; i++)
		{
			if (gone[i])
			{
				break;
			}
			lowest = ys[i];
		}
		return lowest;
	}

	// Whether a quarry's floor dig at height y is too deep for its stair: the lowest step's bottom is walked onto from
	// the floor one under it at most, so the floor is not dug lower than the ground right under the lowest step (the
	// pit of the "Doesnt matter" save was dug two and three under it and its diggers could not climb back out).
	internal static bool TooDeep(int y, int lowestStep)
	{
		return lowestStep != NoStep && y < lowestStep - 1;
	}
}
