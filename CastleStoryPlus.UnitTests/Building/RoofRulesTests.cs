using CastleStoryPlus.Building;
using NUnit.Framework;

namespace CastleStoryPlus.UnitTests.Building;

[TestFixture]
public class RoofRulesTests
{
	[Test]
	public void AnItemOnTheRoofIsOnIt()
	{
		Assert.That(RoofRules.OnRoof(0, 4, 0, 3, 5f, 2f, 5.3f, 1f, 0.5f, 1.5f, 3f), Is.True);
	}

	[Test]
	public void AnItemOnTheFloorInsideIsNot()
	{
		Assert.That(RoofRules.OnRoof(0, 4, 0, 3, 5f, 2f, 0.5f, 1f, 0.5f, 1.5f, 3f), Is.False);
	}

	[Test]
	public void AnItemBesideTheBuildingIsNot()
	{
		Assert.That(RoofRules.OnRoof(0, 4, 0, 3, 5f, 6f, 5.3f, 1f, 0.5f, 1.5f, 3f), Is.False);
	}

	[Test]
	public void ColumnsAreCountedFromTheFootprintsEdge()
	{
		Assert.That(RoofRules.Ring(0, 3, 0, 2, 1, 1), Is.EqualTo(0));
		Assert.That(RoofRules.Ring(0, 3, 0, 2, 4, 1), Is.EqualTo(1));
		Assert.That(RoofRules.Ring(0, 3, 0, 2, -2, 5), Is.EqualTo(3));
	}
}
