using CastleStoryPlus.Achievements;
using NUnit.Framework;

namespace CastleStoryPlus.UnitTests.Achievements;

[TestFixture]
public class BestValuesTests
{
	private const string Wave = "wave";

	private const string Research = "research";

	[Test]
	public void OwnLoadedGameCountsAsItStandsWhileSettingUp()
	{
		BestValues best = new BestValues();
		best.Start(joined: false);
		Assert.That(best.Report(Wave, 12, settingUp: true), Is.EqualTo(12));
	}

	[Test]
	public void OwnGameCountsEveryLaterValue()
	{
		BestValues best = new BestValues();
		best.Start(joined: false);
		best.Report(Wave, 12, settingUp: true);
		Assert.That(best.Report(Wave, 12, settingUp: false), Is.EqualTo(12));
		Assert.That(best.Report(Wave, 13, settingUp: false), Is.EqualTo(13));
	}

	[Test]
	public void NothingForZeroOrLess()
	{
		BestValues best = new BestValues();
		best.Start(joined: false);
		Assert.That(best.Report(Wave, 0, settingUp: false), Is.EqualTo(0));
		Assert.That(best.Report(Wave, -1, settingUp: false), Is.EqualTo(0));
	}

	[Test]
	public void JoinedGameValuesWhileSettingUpAreItsBaseline()
	{
		BestValues best = new BestValues();
		best.Start(joined: true);
		Assert.That(best.Report(Wave, 20, settingUp: true), Is.EqualTo(0));
		Assert.That(best.Report(Wave, 18, settingUp: true), Is.EqualTo(0));
		Assert.That(best.Report(Wave, 20, settingUp: false), Is.EqualTo(0));
		Assert.That(best.Report(Wave, 21, settingUp: false), Is.EqualTo(21));
	}

	[Test]
	public void JoinedGameBaselineIsPerCounter()
	{
		BestValues best = new BestValues();
		best.Start(joined: true);
		best.Report(Wave, 20, settingUp: true);
		Assert.That(best.Report(Research, 3, settingUp: false), Is.EqualTo(3));
		Assert.That(best.Report(Wave, 3, settingUp: false), Is.EqualTo(0));
	}

	[Test]
	public void NewGameForgetsTheBaseline()
	{
		BestValues best = new BestValues();
		best.Start(joined: true);
		best.Report(Wave, 20, settingUp: true);
		best.Start(joined: true);
		Assert.That(best.Report(Wave, 5, settingUp: false), Is.EqualTo(5));
	}

	[Test]
	public void LeavingMakesTheNextGameTheOwnOne()
	{
		BestValues best = new BestValues();
		best.Start(joined: true);
		best.Report(Wave, 20, settingUp: true);
		best.Clear();
		Assert.That(best.Joined, Is.False);
		Assert.That(best.Report(Wave, 4, settingUp: true), Is.EqualTo(4));
	}
}
