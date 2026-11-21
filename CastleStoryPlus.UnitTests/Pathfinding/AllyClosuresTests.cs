using CastleStoryPlus.Pathfinding;
using NUnit.Framework;

namespace CastleStoryPlus.UnitTests.Pathfinding;

[TestFixture]
public class AllyClosuresTests
{
	private static readonly long Stair = AllyClosures.Key(398, 144, 450);

	private static readonly long Other = AllyClosures.Key(397, 143, 450);

	[Test]
	public void KeysDifferPerVoxel()
	{
		Assert.That(AllyClosures.Key(1, 2, 3), Is.Not.EqualTo(AllyClosures.Key(3, 2, 1)));
		Assert.That(AllyClosures.Key(1, 2, 3), Is.Not.EqualTo(AllyClosures.Key(1, 3, 2)));
		Assert.That(AllyClosures.Key(-1, 0, 0), Is.Not.EqualTo(AllyClosures.Key(0, 0, 0)));
		Assert.That(Stair, Is.EqualTo(AllyClosures.Key(398, 144, 450)));
	}

	[Test]
	public void ClosedVoxelIsRefusedUntilItRunsOut()
	{
		AllyClosures closures = new AllyClosures();
		Assert.That(closures.Close(Stair, 10f, 5f), Is.True);
		Assert.That(closures.Count, Is.EqualTo(1));
		Assert.That(closures.Refuses(Stair, 12f), Is.True);
		Assert.That(closures.Refuses(Other, 12f), Is.False);
		Assert.That(closures.Refused, Is.EqualTo(1));
		Assert.That(closures.Refuses(Stair, 15f), Is.False);
		Assert.That(closures.Count, Is.EqualTo(0));
	}

	[Test]
	public void ClosingAgainExtendsTheSameSlot()
	{
		AllyClosures closures = new AllyClosures();
		closures.Close(Stair, 10f, 5f);
		closures.Close(Stair, 13f, 5f);
		Assert.That(closures.Count, Is.EqualTo(1));
		Assert.That(closures.Refuses(Stair, 17f), Is.True);
	}

	[Test]
	public void TheVoxelThatEndsFirstGivesWay()
	{
		AllyClosures closures = new AllyClosures();
		for (int i = 0; i < AllyClosures.MaxClosed; i++)
		{
			closures.Close(AllyClosures.Key(i, 0, 0), 10f + i, 5f);
		}
		closures.Close(Stair, 20f, 5f);
		Assert.That(closures.Count, Is.EqualTo(AllyClosures.MaxClosed));
		Assert.That(closures.Refuses(AllyClosures.Key(0, 0, 0), 14f), Is.False);
		Assert.That(closures.Refuses(AllyClosures.Key(1, 0, 0), 14f), Is.True);
		Assert.That(closures.Refuses(Stair, 21f), Is.True);
	}

	[Test]
	public void FailedSearchReopensTheClosedVoxelsAsChokes()
	{
		AllyClosures closures = new AllyClosures();
		closures.Close(Stair, 10f, 5f);
		closures.Refuses(Stair, 11f);
		Assert.That(closures.ReopenAsChokes(11f, 20f), Is.EqualTo(1));
		Assert.That(closures.Count, Is.EqualTo(0));
		Assert.That(closures.Refused, Is.EqualTo(0));
		Assert.That(closures.Refuses(Stair, 11f), Is.False);
		Assert.That(closures.IsChoke(Stair, 11f), Is.True);
		Assert.That(closures.IsChoke(Other, 11f), Is.False);
	}

	[Test]
	public void ChokeIsNotClosedAgainWhileRemembered()
	{
		AllyClosures closures = new AllyClosures();
		closures.Close(Stair, 10f, 5f);
		closures.ReopenAsChokes(11f, 20f);
		Assert.That(closures.Close(Stair, 12f, 5f), Is.False);
		Assert.That(closures.Count, Is.EqualTo(0));
		Assert.That(closures.Refuses(Stair, 13f), Is.False);
		// Another voxel still closes, so a unit still walks round an ally where there is room.
		Assert.That(closures.Close(Other, 12f, 5f), Is.True);
		Assert.That(closures.Refuses(Other, 13f), Is.True);
	}

	[Test]
	public void ChokeIsForgottenAfterItsTime()
	{
		AllyClosures closures = new AllyClosures();
		closures.Close(Stair, 10f, 5f);
		closures.ReopenAsChokes(11f, 20f);
		Assert.That(closures.IsChoke(Stair, 31f), Is.False);
		Assert.That(closures.Close(Stair, 31f, 5f), Is.True);
	}

	[Test]
	public void NothingToReopenWhenTheClosuresRanOut()
	{
		AllyClosures closures = new AllyClosures();
		closures.Close(Stair, 10f, 5f);
		Assert.That(closures.ReopenAsChokes(16f, 20f), Is.EqualTo(0));
		Assert.That(closures.IsChoke(Stair, 16f), Is.False);
		Assert.That(closures.Count, Is.EqualTo(0));
	}

	[Test]
	public void ResetRefusedStartsTheCountAfresh()
	{
		AllyClosures closures = new AllyClosures();
		closures.Close(Stair, 10f, 5f);
		closures.Refuses(Stair, 11f);
		closures.Refuses(Stair, 11f);
		Assert.That(closures.Refused, Is.EqualTo(2));
		closures.ResetRefused();
		Assert.That(closures.Refused, Is.EqualTo(0));
	}
}
