using CastleStoryPlus.WorkerAI;
using NUnit.Framework;

namespace CastleStoryPlus.UnitTests.WorkerAI;

[TestFixture]
public class CrewRulesTests
{
	[Test]
	public void AFullCrewGrowsByOneUpToTheMaximum()
	{
		Assert.That(CrewRules.GrowFor(8, 8, 22), Is.EqualTo(9));
		Assert.That(CrewRules.GrowFor(21, 21, 22), Is.EqualTo(22));
		Assert.That(CrewRules.GrowFor(22, 22, 22), Is.EqualTo(22));
	}

	[Test]
	public void ACrewWithRoomStaysTheSize()
	{
		Assert.That(CrewRules.GrowFor(3, 8, 22), Is.EqualTo(8));
	}

	[Test]
	public void ANewTaskStartsAtTheGamesEightOrTheLowerMaximum()
	{
		Assert.That(CrewRules.StartSize(22), Is.EqualTo(8));
		Assert.That(CrewRules.StartSize(5), Is.EqualTo(5));
	}
}
