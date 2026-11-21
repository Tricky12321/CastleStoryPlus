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

	// StuckReset: a worker without a task for longer than this (seconds) is not in a retry loop; its watch ends.
	public const float TaskGapSeconds = 5f;

	// StuckReset: whether a gap without a task since lastSeen (game time) ends the watch of a worker.
	public static bool GapEndsWatch(float lastSeen, float now)
	{
		return now - lastSeen > TaskGapSeconds;
	}

	// StuckReset: how long a worker may stand still on a task goal (0: never reset). A walk, and a run of several tasks
	// on the same goal (each failed before the worker moved: a retry loop, #143), get the short limit; any other task
	// the long one.
	public static float StuckLimit(bool walk, bool severalTasks, float seconds, float longSeconds)
	{
		return (walk || severalTasks) ? seconds : longSeconds;
	}

	// StuckReset: whether a goal a worker stood still on over several failed tasks is marked unreachable. A walk that
	// failed says so; a blueprint whose tasks failed otherwise (no material to be found) is not unreachable, other
	// tasks (a dig, a tree...) that fail where the worker stands are taken to be out of its reach.
	public static bool MarksStoodStillGoal(bool walkFailed, bool blueprint)
	{
		return walkFailed || !blueprint;
	}

	// The reset of all bricktrons waits while the game is paused (timeScale 0): the workers' behaviour does not run
	// then, and a task let go of in a paused game was only half let go of (#142).
	public static bool CanResetNow(float timeScale)
	{
		return timeScale > 0f;
	}

	// ---- a bricktron whose legs do not take orders (#156)

	// LocomotionRecovery: every task starts by waiting for the bricktron's legs to be ready to move. A player's order
	// waits this long (seconds) at most before the legs are put back on their feet, any other task LongWaitSeconds.
	public const float OrderWaitSeconds = 2f;

	public const float LongWaitSeconds = 8f;

	// A bricktron that is still flailing faster than this (blocks per second) is falling or tumbling: left alone.
	public const float FallingSpeed = 0.5f;

	// LocomotionRecovery: the checks in a row (a second apart) that must find the legs out of step with the animation
	// (the animation stands, the legs wait for it to stand) before they are put back in step.
	public const int OutOfStepChecks = 2;

	// How long a task may wait for the legs before they are put back on their feet.
	public static float LegsWaitLimit(bool playerOrder)
	{
		return playerOrder ? OrderWaitSeconds : LongWaitSeconds;
	}

	// Whether the legs of a bricktron that cannot move may be put back on their feet: not a dead one, not one held in a
	// hand, not one that is out of the world (phased out), not one on a ladder, climbing or jumping down (those end by
	// themselves and must not be cut off in the air), and not one still tumbling through the air.
	public static bool LegsMayBeReset(bool dead, bool grabbed, bool phasedOut, bool climbing, bool flailing, float flailSpeed)
	{
		if (dead || grabbed || phasedOut || climbing)
		{
			return false;
		}
		return !flailing || flailSpeed < FallingSpeed;
	}

	// The legs are out of step with the animation: they wait for the animation to stand (they cannot move), while the
	// animation stands in place and is not on its way anywhere. The game moves the legs on only when the animation
	// enters a state, so an entry it missed left them waiting for good.
	public static bool LegsOutOfStep(bool canMove, bool animationStands, bool inTransition)
	{
		return !canMove && animationStands && !inTransition;
	}

	// Pieces carried that are not what a digger takes up (the stone, ore, crystals and coal of a quarry): they are put
	// into a storage before the dig, as they take the bag's room and the dig's count of what fits does not see them.
	public static int ForeignCount(int carriedTotal, int carriedDigYield)
	{
		return (carriedTotal > carriedDigYield) ? carriedTotal - carriedDigYield : 0;
	}
}
