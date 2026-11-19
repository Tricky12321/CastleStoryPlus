using CastleStoryPlus.WorkerAI;
using NUnit.Framework;

namespace CastleStoryPlus.UnitTests.WorkerAI;

[TestFixture]
public class CrewRulesTests
{
	[Test]
	public void WithoutALimitTheRoomIsTheCrewSize()
	{
		Assert.That(CrewRules.Capacity(22, 0), Is.EqualTo(22));
		Assert.That(CrewRules.Capacity(-1, 0), Is.EqualTo(-1));
	}

	[Test]
	public void ALimitOnlyLowersTheRoom()
	{
		Assert.That(CrewRules.Capacity(22, 5), Is.EqualTo(5));
		Assert.That(CrewRules.Capacity(8, 22), Is.EqualTo(8));
		Assert.That(CrewRules.Capacity(-1, 5), Is.EqualTo(5));
	}

	[Test]
	public void TheMarkerShowsTheRoomInEffect()
	{
		Assert.That(CrewRules.Marker(8, 22, 0), Is.EqualTo(" x 8/22"));
		Assert.That(CrewRules.Marker(3, 22, 5), Is.EqualTo(" x 3/5"));
		Assert.That(CrewRules.Marker(8, 8, 22), Is.EqualTo(" x 8/8"));
		Assert.That(CrewRules.Marker(2, -1, 0), Is.EqualTo(" x 2"));
	}
}
