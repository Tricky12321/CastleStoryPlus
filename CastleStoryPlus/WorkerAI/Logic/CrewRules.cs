namespace CastleStoryPlus.WorkerAI;

// The pure rules of a task's crew room (TaskCrewSize, WorkLimit), without the game.
internal static class CrewRules
{
	// The game's crew size and limit.
	internal const int GameValue = 8;

	// The room a task really has: its crew size (-1: no limit), lowered to the per-task "Workers at a time" limit
	// (0: none) when that is lower.
	internal static int Capacity(int size, int limit)
	{
		if (limit == 0)
		{
			return size;
		}
		return (size == -1) ? limit : System.Math.Min(size, limit);
	}

	// What the task's marker shows after "x": the crew over the room in effect ("x 8/22"), or only the crew when
	// there is no limit at all.
	internal static string Marker(int crew, int size, int limit)
	{
		int room = Capacity(size, limit);
		return (room == -1) ? " x " + crew : " x " + crew + "/" + room;
	}
}
