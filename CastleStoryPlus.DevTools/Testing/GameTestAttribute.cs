using System;

namespace CastleStoryPlus.DevTools.Testing;

// Marks an in-game test: a static method "IEnumerator Name(TestContext t)" (a coroutine, so it can wait for the
// game over frames) in this assembly. Found by the TestRunner at startup.
[AttributeUsage(AttributeTargets.Method)]
internal class GameTestAttribute : Attribute
{
	public readonly string Category;

	public readonly string Description;

	// Longest the test may take, in real seconds (the game's speed does not count).
	public float TimeoutSeconds = 60f;

	// Needs a game in progress, on the host (single player counts); skipped otherwise.
	public bool NeedsHostGame = true;

	// Runs alone even in a parallel run, when its class allows parallel runs ([ParallelTests]) but this test
	// changes something the others share (the home crystal, the factions' relations, ...).
	public bool Exclusive;

	public GameTestAttribute(string category, string description)
	{
		Category = category;
		Description = description;
	}
}

// The tests of this class may run at the same time as other tests (POST /api/tests/run?parallel=N): each works on
// ground of its own (Scenario.Site) with its own units. Tests of the same group never run at the same time as each
// other: Economy (workers fetch from and store into any stockpile, so their counts would mix), Combat (a fight draws
// in every enemy within sight). Without this attribute a class's tests run alone.
[AttributeUsage(AttributeTargets.Class)]
internal class ParallelTestsAttribute : Attribute
{
	public readonly string Group;

	public ParallelTestsAttribute(string group = null)
	{
		Group = group;
	}
}
