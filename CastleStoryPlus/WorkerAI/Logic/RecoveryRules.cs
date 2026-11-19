using System.Collections.Generic;

namespace CastleStoryPlus.WorkerAI;

// The decisions of a bricktron's recovery (failed tasks that repeat, a task whose target is gone, the "reset all
// bricktrons" command, what a digger carries) as plain functions of numbers, with no call into the game or Unity, so
// they can be tested without the game (CastleStoryPlus.UnitTests). BricktronRecovery holds the state and the game side.
internal static class RecoveryRules
{
	// This many failed tasks in a row within FailureWindow seconds, with no task done well between, are a loop.
	public const int FailureLimit = 4;

	public const float FailureWindow = 40f;

	// A loop found again within this many seconds of the last one is not cured by letting go of the tasks: the worker
	// is taken off the task it was put on.
	public const float EscalateWindow = 120f;

	// A thing lower than this has fallen off the map (the terrain lies above 0).
	public const float VoidFloor = -20f;

	// A client may ask for a reset this often at most.
	public const float MinResetGap = 2f;

	// A task ended: the times of the recent failures in a row, with the new one added (or cleared by a task done
	// well). True when the failures now make a loop (the list is cleared then, so the next loop needs a full run).
	public static bool RecordOutcome(List<float> failures, float now, bool failed)
	{
		if (!failed)
		{
			failures.Clear();
			return false;
		}
		failures.Add(now);
		failures.RemoveAll(time => now - time > FailureWindow);
		if (failures.Count < FailureLimit)
		{
			return false;
		}
		failures.Clear();
		return true;
	}

	// Whether a loop is a repeat of one just cured (lastLoop: the game time of the previous one, a negative value for none).
	public static bool Escalates(float lastLoop, float now)
	{
		return lastLoop >= 0f && now - lastLoop <= EscalateWindow;
	}

	// A position below the map (or no position at all): the thing fell off the map.
	public static bool InVoid(float y)
	{
		return float.IsNaN(y) || y < VoidFloor;
	}

	// A client's reset command: from a connection that has a team, not more often than MinResetGap (last: when this
	// team asked last, a negative value for never).
	public static bool AcceptResetCommand(bool hasTeam, float last, float now)
	{
		return hasTeam && (last < 0f || now - last >= MinResetGap);
	}

	// Pieces carried that are not what a digger takes up (the stone, ore, crystals and coal of a quarry): they are put
	// into a storage before the dig, as they take the bag's room and the dig's count of what fits does not see them.
	public static int ForeignCount(int carriedTotal, int carriedDigYield)
	{
		return (carriedTotal > carriedDigYield) ? carriedTotal - carriedDigYield : 0;
	}
}
