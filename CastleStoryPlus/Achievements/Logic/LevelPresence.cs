namespace CastleStoryPlus.Achievements;

// Whether the achievements see a game running, as pure logic (no Unity, no game): AchievementTracker asks this every
// frame. Unit tests: CastleStoryPlus.UnitTests/Achievements.
//
// The tracker sets a game up when the game says its level is ready (GameSignals.LevelReady) and forgets it when the
// scene changes (GameSession.OnLeave). Those two come from different sources; when a scene change arrives after the
// level is ready, the game would run on without being set up and nothing would count in it (issue #144: wave
// achievements stuck after loading a save). So the game's own ready state (the ready signal received in the scene
// that runs now) is the truth: a game that is ready but not set up is set up again, and one that is not ready does not
// count even when it was set up.
internal sealed class LevelPresence
{
	private bool _setUp;

	internal bool SetUp => _setUp;

	internal void Ready()
	{
		_setUp = true;
	}

	internal void Left()
	{
		_setUp = false;
	}

	// levelReady: the game's level-ready signal was given in the scene that runs now. leaving: the game is being left.
	internal bool NeedsSetUp(bool levelReady, bool leaving)
	{
		return levelReady && !leaving && !_setUp;
	}

	internal bool Running(bool levelReady)
	{
		return levelReady && _setUp;
	}
}
