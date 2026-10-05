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

	public GameTestAttribute(string category, string description)
	{
		Category = category;
		Description = description;
	}
}
