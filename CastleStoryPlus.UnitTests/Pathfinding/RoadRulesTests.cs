using CastleStoryPlus.Pathfinding;
using NUnit.Framework;

namespace CastleStoryPlus.UnitTests.Pathfinding;

[TestFixture]
public class RoadRulesTests
{
	[Test]
	public void FactorUsesTheSettingByDefault()
	{
		Assert.That(RoadRules.Factor(null, 40), Is.EqualTo(1.4f).Within(1e-5f));
		Assert.That(RoadRules.Factor(null, 0), Is.EqualTo(1f));
		Assert.That(RoadRules.Factor(null, 200), Is.EqualTo(3f).Within(1e-5f));
	}

	[Test]
	public void FactorFallsBackToTheDefaultBeforeTheSettingIsBound()
	{
		Assert.That(RoadRules.Factor(null, null), Is.EqualTo(1.4f).Within(1e-5f));
	}

	[Test]
	public void TheHostsBonusCountsOnAClient()
	{
		Assert.That(RoadRules.Factor(100, 40), Is.EqualTo(2f).Within(1e-5f));
		Assert.That(RoadRules.Factor(0, 40), Is.EqualTo(1f));
		Assert.That(RoadRules.Factor(100, null), Is.EqualTo(2f).Within(1e-5f));
	}

	[TestCase(-50, 0)]
	[TestCase(0, 0)]
	[TestCase(40, 40)]
	[TestCase(200, 200)]
	[TestCase(201, 200)]
	[TestCase(int.MaxValue, 200)]
	[TestCase(int.MinValue, 0)]
	public void AHostsBonusIsKeptToTheRange(int percent, int expected)
	{
		Assert.That(RoadRules.ClampBonus(percent), Is.EqualTo(expected));
	}

	// The A*'s rank of a step of length 1 (PathAStarQueue.Evaluate without the distance to the goal): length * 10 + weight * 2.
	private static int Rank(int weight)
	{
		return 1 * 10 + weight * 2;
	}

	[Test]
	public void AFlatStepRanksThirty()
	{
		Assert.That(Rank(RoadRules.FlatWeight), Is.EqualTo(30));
	}

	[Test]
	public void NoRoadLeavesTheWeightAlone()
	{
		Assert.That(RoadRules.Scale(10, false, false, 1.4f), Is.EqualTo(10));
		Assert.That(RoadRules.Scale(17, false, false, 3f), Is.EqualTo(17));
		Assert.That(RoadRules.Scale(0, false, false, 1.4f), Is.EqualTo(0));
	}

	[Test]
	public void NoBonusLeavesAFlatStepAtTen()
	{
		Assert.That(RoadRules.Scale(10, true, true, 1f), Is.EqualTo(10));
		Assert.That(RoadRules.Scale(10, true, false, 1f), Is.EqualTo(10));
	}

	[TestCase(1.4f, 6)]
	[TestCase(2f, 2)]
	[TestCase(3f, 1)]
	public void RoadToRoadScalesTheFlatStepsRankByTheSpeedFactor(float factor, int expectedWeight)
	{
		int weight = RoadRules.Scale(10, true, true, factor);
		Assert.That(weight, Is.EqualTo(expectedWeight));
		// The rank of a flat step is 30; the road's is 30 / factor, to within the rounding of the weight (2 per weight step).
		Assert.That(Rank(weight), Is.EqualTo(30f / factor).Within(2f));
	}

	[Test]
	public void RoadAndGroundIsTheMeanOfBoth()
	{
		// factor 2: share (0.5 + 1) / 2 = 0.75: 15 * 0.75 - 5 = 6.25 -> 6
		Assert.That(RoadRules.Scale(10, true, false, 2f), Is.EqualTo(6));
		Assert.That(RoadRules.Scale(10, false, true, 2f), Is.EqualTo(6));
		// factor 1.4 (the default): share (0.714 + 1) / 2 = 0.857: 15 * 0.857 - 5 = 7.86 -> 8
		Assert.That(RoadRules.Scale(10, true, false, 1.4f), Is.EqualTo(8));
	}

	[Test]
	public void ASlopeKeepsItsWeightAndOnlyTheFlatPartIsScaled()
	{
		// A step with 3 extra weight (a slope): weight 13 -> 3 + the flat step's scaled 5 (factor 2, road to road).
		Assert.That(RoadRules.Scale(13, true, true, 2f), Is.EqualTo(5));
		Assert.That(RoadRules.Scale(10, true, true, 2f) + 3, Is.EqualTo(5));
	}

	[Test]
	public void TheWeightIsNeverUnderOne()
	{
		Assert.That(RoadRules.Scale(10, true, true, 100f), Is.EqualTo(1));
		Assert.That(RoadRules.Scale(0, true, true, 2f), Is.EqualTo(1));
		Assert.That(RoadRules.Scale(5, true, true, 1000f), Is.EqualTo(1));
	}

	[Test]
	public void ABiggerFactorNeverRaisesTheWeight()
	{
		int last = int.MaxValue;
		for (float factor = 1f; factor <= 3f; factor += 0.05f)
		{
			int weight = RoadRules.Scale(10, true, true, factor);
			Assert.That(weight, Is.LessThanOrEqualTo(last), "factor " + factor);
			last = weight;
		}
	}

	[Test]
	public void RoadToRoadIsNeverSlowerThanRoadToGround()
	{
		for (float factor = 1f; factor <= 3f; factor += 0.1f)
		{
			Assert.That(RoadRules.Scale(10, true, true, factor), Is.LessThanOrEqualTo(RoadRules.Scale(10, true, false, factor)), "factor " + factor);
		}
	}

	[TestCase(0.5f, 0)]
	[TestCase(1.5f, 2)]
	[TestCase(2.5f, 2)]
	[TestCase(3.5f, 4)]
	[TestCase(-0.5f, 0)]
	[TestCase(-1.5f, -2)]
	[TestCase(6.25f, 6)]
	[TestCase(7.86f, 8)]
	public void RoundToIntMatchesUnity(float value, int expected)
	{
		Assert.That(RoadRules.RoundToInt(value), Is.EqualTo(expected));
	}

	[Test]
	public void AreaCostsLeaveTheRoadAndTheNonWalkableAreaAlone()
	{
		Assert.That(RoadRules.AreaCost(2, 2, 1, 1.4f), Is.Null);
		Assert.That(RoadRules.AreaCost(1, 2, 1, 1.4f), Is.Null);
	}

	[TestCase(0)]
	[TestCase(3)]
	[TestCase(31)]
	public void OtherAreasCostTheSpeedFactor(int area)
	{
		Assert.That(RoadRules.AreaCost(area, 2, 1, 1.4f), Is.EqualTo(1.4f));
	}

	[TestCase(1f)]
	[TestCase(1.001f)]
	[TestCase(0.5f)]
	public void NoBonusLeavesEveryAreaCostAsItIs(float factor)
	{
		for (int area = 0; area < 32; area++)
		{
			Assert.That(RoadRules.AreaCost(area, 2, 1, factor), Is.Null, "area " + area);
		}
	}

	[Test]
	public void TheStockpileCostsItsOwnTimesTheFactor()
	{
		Assert.That(RoadRules.StockpileCost(2f, 1.5f), Is.EqualTo(3f));
		Assert.That(RoadRules.StockpileCost(2f, 1f), Is.EqualTo(2f));
	}

	// The cells of the game's volumes, as the log prints them (the forge clone Smithy, a walk-in building, a pallet).
	private const long WorkSpot = RoadRules.AccessBit | RoadRules.OccupyBit;

	private const long WorkSpotWithOperation = RoadRules.AccessBit | RoadRules.OccupyBit | RoadRules.OperationBit;

	private const long WorkshopBody = RoadRules.ObstacleBit | RoadRules.OccupyBit | RoadRules.DeblocBit;

	private const long WorkshopUpperBody = RoadRules.ObstacleBit | RoadRules.OccupyBit;

	private const long PalletBody = RoadRules.ObstacleBit | RoadRules.OccupyBit | RoadRules.WalkableBit | RoadRules.DeblocBit;

	private const long WalkInFloor = RoadRules.OccupyBit | RoadRules.AccessBit | RoadRules.DeblocBit;

	private const long BlueprintBody = RoadRules.FantomeBit;

	[Test]
	public void ARoadMayLieOnAWorkshopsWorkSpot()
	{
		Assert.That(RoadRules.MayLieOn(WorkSpot, RoadRules.AccessBit), Is.True);
		Assert.That(RoadRules.MayLieOn(WorkSpotWithOperation, RoadRules.AccessBit), Is.True);
		Assert.That(RoadRules.MayLieOn(WorkSpot, 0L), Is.True);
		Assert.That(RoadRules.MayLieOn(RoadRules.OccupyBit | RoadRules.OperationBit, 0L), Is.True);
	}

	[Test]
	public void ARoadMayLieWhereTheVolumeClaimsNothing()
	{
		Assert.That(RoadRules.MayLieOn(0L, 0L), Is.True);
		Assert.That(RoadRules.MayLieOn(RoadRules.AccessBit, RoadRules.AccessBit), Is.True);
	}

	[Test]
	public void ARoadMayNotLieOnABuildingsBody()
	{
		Assert.That(RoadRules.MayLieOn(WorkshopBody, WorkshopUpperBody), Is.False);
		Assert.That(RoadRules.MayLieOn(WorkshopBody, 0L), Is.False);
		Assert.That(RoadRules.MayLieOn(WorkshopBody | RoadRules.AccessBit, 0L), Is.False);
		Assert.That(RoadRules.MayLieOn(PalletBody, WorkshopBody), Is.False);
		Assert.That(RoadRules.MayLieOn(RoadRules.ObstacleBit, 0L), Is.False);
	}

	[Test]
	public void ARoadMayNotLieOnAnotherBlueprint()
	{
		Assert.That(RoadRules.MayLieOn(BlueprintBody, 0L), Is.False);
		Assert.That(RoadRules.MayLieOn(BlueprintBody | RoadRules.AccessBit, 0L), Is.False);
		Assert.That(RoadRules.MayLieOn(BlueprintBody | WorkSpot, 0L), Is.False);
	}

	[Test]
	public void ARoadMayNotLieOnTheFloorInsideAWalkInBuilding()
	{
		Assert.That(RoadRules.MayLieOn(WalkInFloor, RoadRules.OccupyBit), Is.False);
		// A floor cell without debloc is still inside: the building claims the cell over it too.
		Assert.That(RoadRules.MayLieOn(WorkSpot, RoadRules.OccupyBit), Is.False);
		Assert.That(RoadRules.MayLieOn(WorkSpot, WorkshopUpperBody), Is.False);
		Assert.That(RoadRules.MayLieOn(WorkSpot, BlueprintBody), Is.False);
	}

	[Test]
	public void ARoadMayNotLieOnACellClaimedWithoutAPlaceToStand()
	{
		Assert.That(RoadRules.MayLieOn(RoadRules.OccupyBit, 0L), Is.False);
	}
}
