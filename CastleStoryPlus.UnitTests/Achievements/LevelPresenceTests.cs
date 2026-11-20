using CastleStoryPlus.Achievements;
using NUnit.Framework;

namespace CastleStoryPlus.UnitTests.Achievements;

[TestFixture]
public class LevelPresenceTests
{
	[Test]
	public void NothingRunsBeforeAGame()
	{
		LevelPresence level = new LevelPresence();
		Assert.That(level.Running(false), Is.False);
		Assert.That(level.NeedsSetUp(false, false), Is.False);
	}

	[Test]
	public void ReadyThenSetUpRuns()
	{
		LevelPresence level = new LevelPresence();
		level.Ready();
		Assert.That(level.Running(true), Is.True);
		Assert.That(level.NeedsSetUp(true, false), Is.False);
	}

	[Test]
	public void SceneChangeBeforeReadyIsANormalLoad()
	{
		LevelPresence level = new LevelPresence();
		// Menu -> game: the scene changes, then the level becomes ready.
		level.Left();
		Assert.That(level.NeedsSetUp(false, false), Is.False);
		level.Ready();
		Assert.That(level.Running(true), Is.True);
	}

	[Test]
	public void SceneChangeAfterReadyIsSetUpAgain()
	{
		LevelPresence level = new LevelPresence();
		level.Ready();
		// The scene change arrives late: the game is forgotten while its level stays ready (#144).
		level.Left();
		Assert.That(level.Running(true), Is.False);
		Assert.That(level.NeedsSetUp(true, false), Is.True);
		level.Ready();
		Assert.That(level.Running(true), Is.True);
	}

	[Test]
	public void NotSetUpAgainWhileLeaving()
	{
		LevelPresence level = new LevelPresence();
		level.Ready();
		level.Left();
		Assert.That(level.NeedsSetUp(true, true), Is.False);
	}

	[Test]
	public void ALevelThatIsNoLongerReadyDoesNotRun()
	{
		LevelPresence level = new LevelPresence();
		level.Ready();
		// Back in the menu: the game's signals were cleared, the leave may not have come yet.
		Assert.That(level.Running(false), Is.False);
		Assert.That(level.NeedsSetUp(false, false), Is.False);
	}
}
