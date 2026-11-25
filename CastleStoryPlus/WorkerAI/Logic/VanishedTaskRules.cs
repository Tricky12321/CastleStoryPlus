namespace CastleStoryPlus.WorkerAI;

// The decisions of VanishedTask (issue #199: a bricktron put on a task the moment the task ended) as plain functions,
// with no call into the game or Unity, so they can be tested without the game (CastleStoryPlus.UnitTests).
internal static class VanishedTaskRules
{
	// Seconds between two looks for bricktrons left in a task that is gone.
	public const float CheckSeconds = 2f;

	// Whether a bricktron may join a task's crew: not when the task is gone (released to the game's pool or
	// destroyed), nor when its crew (the task's WorkForceProvider) is. Joining moves the bricktron's game object into
	// the task's crew holder; a task in the pool is switched off, and the bricktron with it.
	public static bool MayJoin(bool taskGone, bool crewGone)
	{
		return !taskGone && !crewGone;
	}

	// Whether a bricktron is stranded: one still in the game (not released, not phased out) whose game object sits in
	// the crew holder of a task that is gone. It neither moves nor takes orders, and the other players see it frozen.
	public static bool Stranded(bool bricktronLive, bool phasedOut, bool inGoneTask)
	{
		return bricktronLive && !phasedOut && inGoneTask;
	}

	// Whether a bricktron's current task, never started, is let go because its task area is gone: only when it was seen
	// so at the check before too, so a task in the middle of starting is left alone.
	public static bool LetGoUnstarted(bool started, bool areaGone, bool seenBefore)
	{
		return !started && areaGone && seenBefore;
	}
}
