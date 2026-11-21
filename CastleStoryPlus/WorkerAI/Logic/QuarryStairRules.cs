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
}
