namespace CastleStoryPlus.WorkerAI;

// The pure rules of a task's crew size (TaskCrewSize), without the game.
internal static class CrewRules
{
	// The game's crew size and limit: what a task starts with.
	internal const int GameValue = 8;

	// The crew size after one more bricktron is put on a task: one place more when the crew fills the size and the
	// size is below the maximum, otherwise unchanged.
	internal static int GrowFor(int crew, int size, int most)
	{
		if (size >= most || crew < size)
		{
			return size;
		}
		return size + 1;
	}

	// What a new task starts with: the game's 8, or the maximum when that is lower.
	internal static int StartSize(int most)
	{
		return most < GameValue ? most : GameValue;
	}
}
